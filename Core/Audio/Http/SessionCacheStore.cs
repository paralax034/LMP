using System.Text.Json;
using System.Text.Json.Serialization;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Models.Json;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Videos.Streams;
using MemoryPack;

namespace LMP.Core.Audio.Http;

// Public Models

/// <summary>
/// A single audio stream variant inside a cached manifest.
/// </summary>
[MemoryPackable]
public sealed partial class VariantEntry
{
    /// <summary>YouTube stream itag.</summary>
    public int Itag { get; set; }

    /// <summary>Full videoplayback URL.</summary>
    public required string Url { get; set; }

    /// <summary>Container (webm, mp4).</summary>
    public string Container { get; set; } = "";

    /// <summary>Codec (opus, aac).</summary>
    public string Codec { get; set; } = "";

    /// <summary>Bitrate in bps.</summary>
    public int Bitrate { get; set; }

    /// <summary>Content length in bytes.</summary>
    public long Clen { get; set; }

    /// <summary>
    /// Typed container format.
    /// Not serialized and computed from <see cref="Container"/>.
    /// </summary>
    [JsonIgnore]
    [MemoryPackIgnore]
    public AudioFormat Format => YoutubeIdHelper.MapContainerToFormat(Container);

    /// <summary>
    /// Typed audio codec.
    /// Not serialized and computed from <see cref="Codec"/>.
    /// </summary>
    [JsonIgnore]
    [MemoryPackIgnore]
    public AudioCodec CodecType => Codec.ToAudioCodec(Format);

    /// <summary>Audio track language code.</summary>
    public string? LanguageCode { get; set; }

    /// <summary>Default language flag.</summary>
    public bool IsDefaultLanguage { get; set; }
}

/// <summary>
/// A cached full manifest for a single track.
/// Contains all available audio variants with live URLs.
/// </summary>
[MemoryPackable]
public sealed partial class TrackManifestEntry
{
    /// <summary>Track identifier (with yt_ prefix).</summary>
    public required string TrackId { get; set; }

    /// <summary>CDN hostname of the first variant.</summary>
    public string CdnHost { get; set; } = "";

    /// <summary>
    /// URL expiration time (from &amp;expire= of the first variant, UTC).
    /// Used as a hint for probe-bypass, but NOT as a hard TTL.
    /// Real invalidation occurs on HTTP 403/410 during probe.
    /// </summary>
    public DateTime ExpireUtc { get; set; }

    /// <summary>
    /// Track-level integrated loudness in LUFS from YouTube <c>perceptualLoudnessDb</c>.
    /// </summary>
    [JsonConverter(typeof(NaNFloatJsonConverter))]
    public float IntegratedLufs { get; set; } = float.NaN;

    /// <summary>All available audio variants.</summary>
    public List<VariantEntry> Variants { get; set; } = [];

    /// <summary>Time recorded to cache (UTC).</summary>
    public DateTime SavedAtUtc { get; set; }
}

/// <summary>
/// Wrapper envelope for the session cache JSON file.
/// </summary>
[MemoryPackable]
public sealed partial class SessionCacheEnvelope
{
    /// <summary>Cached manifests (LRU, max entries).</summary>
    public List<TrackManifestEntry> Manifests { get; set; } = [];

    /// <summary>Last cleanup time (UTC).</summary>
    public DateTime LastCleanupUtc { get; set; }
}

// Store

/// <summary>
/// Persistent disk cache for full YouTube audio manifests.
/// <para>
/// <b>"Keep while it works" architecture:</b>
/// </para>
/// <list type="number">
///   <item>
///     After every successful YouTube API resolve, <see cref="RecordManifest"/> is called
///     to persist <b>all</b> audio variants (URL, itag, codec, bitrate, loudness).
///   </item>
///   <item>
///     On subsequent playback, <see cref="TryGetManifestAndProbeAsync"/>
///     checks for entry existence and issues an HTTP HEAD to one of the URLs.
///     If the CDN responds with 200/206, the YouTube API call is bypassed completely.
///   </item>
///   <item>
///     On HTTP 403/410, the entry is invalidated entirely — all variants
///     were fetched in a single API call and expire simultaneously.
///   </item>
/// </list>
/// <para>
/// <b>Network savings:</b> A single manifest fetch serves playback, quality switch,
/// AND the UI quality menu without extra requests — both in RAM and after app restart.
/// </para>
/// </summary>
internal static class SessionCacheStore
{
    private const int MaxManifests = 50;
    private const int TtlSafetyMarginMinutes = 30;
    private const int MinProbeTimeoutMs = 1000;
    private const int MaxProbeTimeoutMs = 3000;
    private const int DefaultProbeTimeoutMs = 2000;
    private const int TtlBypassProbeMinutes = 10;
    private const double ProbeTtfbMultiplier = 4.0;

    private const int SaveDebounceMs = 3000;

    private static readonly Lock _lock = new();
    private static readonly Lock _saveIoLock = new();
    private static readonly Timer _saveDebounceTimer = new(static _ => Save(), null, Timeout.Infinite, Timeout.Infinite);
    private static SessionCacheEnvelope _data = new();
    private static bool _dirty;

    // Load / Save

    /// <summary>
    /// Loads the session cache from disk. Called once at startup.
    /// </summary>
    public static void Load()
    {
        try
        {
            var binPath = G.FilePath.SessionCache;
            var binary = AtomicFile.ReadBytesWithFallback(binPath, out bool recovered);

            if (recovered)
                Log.Warn("[SessionCache] Recovered session cache from backup (.bak)");

            if (binary != null && binary.Length > 0)
            {
                var envelope = MemoryPackSerializer.Deserialize<SessionCacheEnvelope>(binary);
                if (envelope != null)
                {
                    lock (_lock)
                    {
                        _data = envelope;
                        EvictExpiredMustHoldLock();
                    }

                    int count, variants;
                    lock (_lock)
                    {
                        count = _data.Manifests.Count;
                        variants = CountTotalVariants();
                    }

                    Log.Debug($"[SessionCache] Loaded {count} manifest(s), {variants} variant(s) from binary store");
                    return;
                }
            }

            var legacyJsonPath = Path.ChangeExtension(binPath, ".json");
            if (File.Exists(legacyJsonPath))
            {
                MigrateLegacyJsonIfPresent(legacyJsonPath);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[SessionCache] Load failed (starting fresh): {ex.Message}");
            lock (_lock) { _data = new SessionCacheEnvelope(); }
        }
    }

    /// <summary>
    /// Isolated migration of manifests from legacy JSON to binary MemoryPack.
    /// </summary>
    private static void MigrateLegacyJsonIfPresent(string legacyJsonPath)
    {
        var json = AtomicFile.ReadTextWithFallback(legacyJsonPath, out bool jsonRecovered);
        if (string.IsNullOrWhiteSpace(json))
            return;

        if (jsonRecovered)
            Log.Warn("[SessionCache] Recovered session cache from legacy backup (.bak)");

        var legacyEnvelope = JsonSerializer.Deserialize(
            json,
            AppJsonContext.DefaultCompact.SessionCacheEnvelope);

        if (legacyEnvelope is null)
            return;

        lock (_lock)
        {
            _data = legacyEnvelope;
            EvictExpiredMustHoldLock();
            _dirty = true;
        }

        Save();

        try
        {
            var legacyBak = legacyJsonPath + ".bak";
            if (File.Exists(legacyJsonPath) && !File.Exists(legacyBak))
                File.Copy(legacyJsonPath, legacyBak, overwrite: true);

            if (File.Exists(legacyJsonPath))
                File.Delete(legacyJsonPath);
        }
        catch { }

        Log.Info($"[SessionCache] Migrated {legacyEnvelope.Manifests.Count} manifest(s) from legacy JSON to MemoryPack");
    }

    /// <summary>
    /// Saves the session cache to disk.
    /// </summary>
    public static void Save()
    {
        byte[] binary;
        int manifestCount;
        int variants = 0;

        lock (_lock)
        {
            if (!_dirty)
                return;

            EvictExpiredMustHoldLock();
            binary = MemoryPackSerializer.Serialize(_data);
            manifestCount = _data.Manifests.Count;
            for (int i = 0; i < manifestCount; i++)
                variants += _data.Manifests[i].Variants.Count;
            _dirty = false;
        }

        try
        {
            lock (_saveIoLock)
            {
                AtomicFile.WriteBytes(G.FilePath.SessionCache, binary, createBackup: false);
            }

            Log.Debug($"[SessionCache] Saved {manifestCount} manifest(s), {variants} variant(s) [binary]");
        }
        catch (Exception ex)
        {
            Log.Warn($"[SessionCache] Save failed: {ex.Message}");
            lock (_lock) { _dirty = true; }
        }
    }

    // Record

    /// <summary>
    /// Saves a full manifest after a successful YouTube API resolve.
    /// Replaces the previous entry for this track entirely.
    /// </summary>
    /// <param name="trackId">Track ID (with yt_ prefix).</param>
    /// <param name="streams">All audio variants from the manifest.</param>
    public static void RecordManifest(
         string trackId,
         IReadOnlyList<AudioOnlyStreamInfo> streams,
         float perceptualLufs = float.NaN)
    {
        if (string.IsNullOrEmpty(trackId) || streams.Count == 0)
            return;

        string firstUrl = streams[0].Url;

        // Fresh player API response: the ip parameter of its URL is the public address YouTube sees the client from.
        StreamUrlEgressGuard.Shared.ObserveFreshUrl(firstUrl);

        if (!TryExtractCdnHost(firstUrl, out var cdnHost))
            cdnHost = "";

        var expireUtc = ParseExpireFromUrl(firstUrl);

        var variants = new List<VariantEntry>(streams.Count);
        for (int i = 0; i < streams.Count; i++)
        {
            var s = streams[i];
            var format = YoutubeIdHelper.MapContainerToFormat(s.Container.Name);

            variants.Add(new VariantEntry
            {
                Itag = s.Itag,
                Url = s.Url,
                Container = format.ToContainerName(),
                Codec = s.AudioCodec.ToAudioCodec(format).ToDisplayName(),
                Bitrate = (int)s.Bitrate.BitsPerSecond,
                Clen = s.Size.Bytes,
                LanguageCode = s.AudioLanguage?.Code,
                IsDefaultLanguage = s.IsAudioLanguageDefault ?? false
            });
        }

        var entry = new TrackManifestEntry
        {
            TrackId = trackId,
            CdnHost = cdnHost,
            IntegratedLufs = perceptualLufs,
            ExpireUtc = expireUtc,
            Variants = variants,
            SavedAtUtc = DateTime.UtcNow
        };

        lock (_lock)
        {
            for (int i = _data.Manifests.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_data.Manifests[i].TrackId, trackId, StringComparison.Ordinal))
                {
                    _data.Manifests.RemoveAt(i);
                    break;
                }
            }

            while (_data.Manifests.Count >= MaxManifests)
                EvictLeastRecentlyUsedMustHoldLock();

            _data.Manifests.Add(entry);
            _dirty = true;
        }

        Log.Info($"[SessionCache] RecordManifest: track={trackId}, variants={variants.Count}, cdn={cdnHost}, expire={expireUtc:O}");
        try
        {
            _saveDebounceTimer.Change(SaveDebounceMs, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // Probe

    /// <summary>
    /// Tries to get and validate a cached manifest for a track.
    /// <para>
    /// Algorithm:
    /// </para>
    /// <list type="number">
    ///   <item>Find entry by <paramref name="trackId"/>.</item>
    ///   <item>Check <see cref="TrackManifestEntry.ExpireUtc"/> timestamp (instant purge without a network call).</item>
    ///   <item>If the entry is fresh (saved &lt; 10 min ago) — bypass without probe.</item>
    ///   <item>HTTP HEAD to the first URL with adaptive timeout.</item>
    ///   <item>200/206 → returns entry. 403/404/410 → drops entry.</item>
    /// </list>
    /// </summary>
    /// <param name="trackId">Track ID.</param>
    /// <param name="httpClient">HTTP client.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A valid entry or null.</returns>
    public static async ValueTask<TrackManifestEntry?> TryGetManifestAndProbeAsync(
        string trackId,
        HttpClient httpClient,
        CancellationToken ct)
    {
        TrackManifestEntry? entry;

        lock (_lock)
        {
            entry = FindMustHoldLock(trackId);
            if (entry is not null && DropIfForeignEgressMustHoldLock(entry))
                return null;

            if (entry is null || entry.Variants.Count == 0)
                return null;

            // If the manifest's CDN host is already blocked by TSPU (in CdnBlacklist),
            // drop the cache immediately and do not bypass — a fresh API resolve with a different host is required
            if (!string.IsNullOrEmpty(entry.CdnHost) && AudioSourceFactory.CdnBlacklist.IsBlocked(entry.CdnHost))
            {
                Log.Warn($"[SessionCache] Manifest CDN host '{entry.CdnHost}' is blacklisted. Dropping cached manifest for {trackId}.");
                DropMustHoldLock(trackId);
                return null;
            }

            // Instant invalidation without performing a network probe if the link has expired
            if (entry.ExpireUtc <= DateTime.UtcNow)
            {
                Log.Debug($"[SessionCache] ExpireUtc passed for {trackId}, dropping manifest without probe");
                DropMustHoldLock(trackId);
                return null;
            }
            else if (DateTime.UtcNow - entry.SavedAtUtc < TimeSpan.FromMinutes(TtlBypassProbeMinutes))
            {
                Log.Debug($"[SessionCache] Probe bypass: track={trackId}, age={(DateTime.UtcNow - entry.SavedAtUtc).TotalSeconds:F0}s, variants={entry.Variants.Count}");
                return entry;
            }
        }

        var probeUrl = entry.Variants[0].Url;
        var probeTimeoutMs = ComputeProbeTimeout(entry.CdnHost);

        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(probeTimeoutMs);

            using var request = new HttpRequestMessage(HttpMethod.Head, probeUrl);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            request.Version = System.Net.HttpVersion.Version11;
            SharedHttpClient.ApplyUserAgentFromUrl(request, probeUrl);

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, probeCts.Token)
                .ConfigureAwait(false);

            var status = (int)response.StatusCode;

            if (status is 200 or 206)
            {
                Log.Info($"[SessionCache] Probe OK ({status}): track={trackId}, variants={entry.Variants.Count}");
                return entry;
            }

            if (status is 401 or 403 or 404 or 410)
            {
                Log.Debug($"[SessionCache] Probe rejected (HTTP {status}): track={trackId}, dropping manifest");
                lock (_lock) { DropMustHoldLock(trackId); }
                return null;
            }

            Log.Warn($"[SessionCache] Probe returned HTTP {status} for {trackId}, keeping for safety");
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Debug($"[SessionCache] Probe timed out ({probeTimeoutMs}ms) for {trackId}");
            return null;
        }
        catch (HttpRequestException ex)
        {
            Log.Debug($"[SessionCache] Probe network error for {trackId}: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Log.Debug($"[SessionCache] Probe failed for {trackId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Removes an entry whose URLs were issued for a different public IP (VPN node or network change). Called under lock.
    /// </summary>
    /// <param name="entry">The entry to check.</param>
    /// <returns><c>true</c> if the entry was removed.</returns>
    private static bool DropIfForeignEgressMustHoldLock(TrackManifestEntry entry)
    {
        if (entry.Variants.Count == 0)
            return false;

        string url = entry.Variants[0].Url;
        var guard = StreamUrlEgressGuard.Shared;
        if (guard.IsBoundToCurrentEgress(url))
            return false;

        Log.Info($"[SessionCache] Manifest for {entry.TrackId} is bound to ip={StreamUrlEgressGuard.TryGetBoundIp(url)}, " +
                 $"current egress ip={guard.ObservedIp}. Dropping.");
        DropMustHoldLock(entry.TrackId);
        return true;
    }

    /// <summary>
    /// Returns a cached manifest entry without probing (for local reads).
    /// An entry with URLs issued for a different public IP is removed: the CDN would reject them with HTTP 403.
    /// </summary>
    public static TrackManifestEntry? GetManifest(string trackId)
    {
        lock (_lock)
        {
            var found = FindMustHoldLock(trackId);
            return found is not null && DropIfForeignEgressMustHoldLock(found) ? null : found;
        }
    }

    /// <summary>
    /// Returns a snapshot of all trackIds in the current cache.
    /// Used by diagnostic tests to probe all known streams.
    /// </summary>
    internal static IReadOnlyList<string> GetAllCachedTrackIds()
    {
        lock (_lock)
        {
            if (_data.Manifests.Count == 0)
                return [];

            var result = new string[_data.Manifests.Count];
            for (int i = 0; i < _data.Manifests.Count; i++)
                result[i] = _data.Manifests[i].TrackId;

            return result;
        }
    }

    /// <summary>
    /// Invalidates an entry for a track (after fatal 403 or force refresh).
    /// </summary>
    public static void Invalidate(string trackId)
    {
        lock (_lock)
        {
            DropMustHoldLock(trackId);
        }
        Log.Debug($"[SessionCache] Invalidated manifest for {trackId}");
    }

    // Private Helpers

    /// <summary>
    /// Returns the record index by trackId. Called under lock.
    /// </summary>
    /// <returns>Index or -1 if not found.</returns>
    private static int FindIndexMustHoldLock(string trackId)
    {
        for (int i = 0; i < _data.Manifests.Count; i++)
        {
            if (string.Equals(_data.Manifests[i].TrackId, trackId, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    private static TrackManifestEntry? FindMustHoldLock(string trackId)
    {
        int idx = FindIndexMustHoldLock(trackId);
        return idx >= 0 ? _data.Manifests[idx] : null;
    }

    private static void DropMustHoldLock(string trackId)
    {
        int idx = FindIndexMustHoldLock(trackId);
        if (idx < 0) return;

        _data.Manifests.RemoveAt(idx);
        _dirty = true;
    }

    /// <summary>
    /// Sums the number of variants across all manifests. Called under lock.
    /// </summary>
    private static int CountTotalVariants()
    {
        int total = 0;
        for (int i = 0; i < _data.Manifests.Count; i++)
            total += _data.Manifests[i].Variants.Count;
        return total;
    }

    private static int ComputeProbeTimeout(string cdnHost)
    {
        var avgTtfb = CdnHostStatsStore.GetAvgTtfbMs(cdnHost);
        if (double.IsNaN(avgTtfb))
            return DefaultProbeTimeoutMs;

        return Math.Clamp((int)(avgTtfb * ProbeTtfbMultiplier), MinProbeTimeoutMs, MaxProbeTimeoutMs);
    }

    private static void EvictLeastRecentlyUsedMustHoldLock()
    {
        if (_data.Manifests.Count == 0) return;

        int oldestIdx = 0;
        for (int i = 1; i < _data.Manifests.Count; i++)
        {
            if (_data.Manifests[i].SavedAtUtc < _data.Manifests[oldestIdx].SavedAtUtc)
                oldestIdx = i;
        }

        _data.Manifests.RemoveAt(oldestIdx);
    }

    private static void EvictExpiredMustHoldLock()
    {
        var threshold = DateTime.UtcNow.AddMinutes(-TtlSafetyMarginMinutes);
        for (int i = _data.Manifests.Count - 1; i >= 0; i--)
        {
            if (_data.Manifests[i].ExpireUtc <= threshold)
                _data.Manifests.RemoveAt(i);
        }
        _data.LastCleanupUtc = DateTime.UtcNow;
    }

    private static bool TryExtractCdnHost(string url, out string host)
    {
        host = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (!uri.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase))
            return false;

        host = uri.Host;
        return true;
    }

    private static DateTime ParseExpireFromUrl(string url)
    {
        var expireStr = UrlEx.TryGetQueryParameterValue(url, "expire");
        if (expireStr is null || !long.TryParse(expireStr, out var unixSeconds))
            return DateTime.UtcNow.AddHours(6);

        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
    }
}