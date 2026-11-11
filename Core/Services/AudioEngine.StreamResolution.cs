using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Http;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Normalization;

namespace LMP.Core.Services;

public sealed partial class AudioEngine
{
    private string? _lastSoftRefreshTrackId;
    private long _lastSoftRefreshTick;

    /// <summary>Lifetime of the excluded client list after a CDN 403 (ms).</summary>
    private const long CdnForbiddenClientsTtlMs = 10 * 60 * 1000;

    /// <summary>Protects the state of clients whose URLs received a CDN 403 for the current track.</summary>
    private readonly Lock _cdnForbiddenClientsLock = new();

    /// <summary>The track to which <see cref="_cdnForbiddenClients"/> applies.</summary>
    private string? _cdnForbiddenClientsTrackId;

    /// <summary>Timestamp of the last registered CDN 403 (<see cref="Environment.TickCount64"/>).</summary>
    private long _cdnForbiddenClientsTick;

    /// <summary>InnerTube clients (<c>c</c> stream URL parameter) whose URLs were rejected by the CDN with HTTP 403.</summary>
    private readonly List<string> _cdnForbiddenClients = new(4);

    #region Stream Resolution

    /// <summary>
    /// Resolves the audio stream source for playing the track.
    /// </summary>
    /// <param name="track">Track metadata.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <param name="seekPosition">Initial playback position when seeking.</param>
    /// <returns>Handle to the prepared audio stream.</returns>
    private async Task<ResolvedStreamDescriptor> ResolveStreamAsync(
        TrackInfo track,
        CancellationToken ct,
        TimeSpan? seekPosition = null)
    {
        var requested = StreamSelectionHint.FromTrack(track, _library.Settings.RememberTrackFormat);

        Log.Debug(
            $"[AudioEngine] ResolveStreamAsync start: track={track.Id}, seek={seekPosition?.TotalMilliseconds ?? 0}ms, requestedFormat={requested.Format?.ToContainerName() ?? "-"}, requestedBitrate={requested.BitrateKbps}");

        var rawId = track.GetRawIdSpan().ToString();

        // --- Path 0: Downloaded local files (Downloads folder) ---
        if (track.IsDownloaded && !string.IsNullOrEmpty(track.LocalPath) && File.Exists(track.LocalPath))
        {
            var downloadedFormat = AudioSourceFactory.DetectFormat(track.LocalPath);
            if (downloadedFormat == AudioFormat.Unknown) downloadedFormat = AudioFormat.WebM;

            bool isUserOverrodeFormat = requested.HasFormat && requested.Format != downloadedFormat;

            if (!isUserOverrodeFormat)
            {
                var fileInfo = new FileInfo(track.LocalPath);
                var codec = AudioSourceFactory.GetCodecForFormat(downloadedFormat);
                int fileBitrateKbps = track.Duration.TotalSeconds > 0
                    ? Math.Max((int)(fileInfo.Length * 8 / track.Duration.TotalSeconds / 1000), 32)
                    : 128;

                var descriptor = new ResolvedStreamDescriptor
                {
                    TrackId = track.Id,
                    Url = track.LocalPath,
                    Format = downloadedFormat,
                    Codec = codec,
                    BitrateKbps = fileBitrateKbps,
                    ContentLengthBytes = fileInfo.Length,
                    Origin = StreamSource.DiskCacheFull
                };

                Log.Info($"[AudioEngine] ResolveStreamAsync LOCAL DOWNLOAD -> {descriptor}");
                TryEnrichIntegratedLufsFromLocalSources(track);
                return descriptor;
            }
        }

        // --- Path 1: Full disk cache (exact match by format+bitrate bucket) ---
        if (requested.HasFormat && requested.HasBitrate)
        {
            string exactCacheKey =
                AudioSourceFactory.BuildCacheKey(track.Id, requested.Format!.Value, requested.BitrateKbps);
            if (AudioSourceFactory.GlobalCache.IsFullyCached(exactCacheKey))
            {
                var exactEntry = AudioSourceFactory.GlobalCache.GetCacheInfo(exactCacheKey);
                if (exactEntry != null && IsCacheSizePlausible(exactEntry, track.Duration))
                {
                    TrackNormalizationHydrator.HydrateNormalization(track, exactEntry);
                    TryEnrichIntegratedLufsFromLocalSources(track);

                    var descriptor = new ResolvedStreamDescriptor
                    {
                        TrackId = track.Id,
                        Url = "",
                        Format = exactEntry.Format,
                        Codec = exactEntry.Codec,
                        BitrateKbps = exactEntry.Bitrate,
                        ContentLengthBytes = exactEntry.TotalSize,
                        Origin = StreamSource.DiskCacheFull
                    };

                    Log.Info($"[AudioEngine] ResolveStreamAsync FULL CACHE (exact) -> {descriptor}");
                    return descriptor;
                }
            }
        }

        // --- Path 1b: Full disk cache (any format, no user preference) ---
        if (!requested.HasFormat)
        {
            var fullCache = AudioSourceFactory.FindAnyCachedTrack(track.Id)
                            ?? (rawId != track.Id ? AudioSourceFactory.FindAnyCachedTrack(rawId) : null);

            if (fullCache != null && IsCacheSizePlausible(fullCache.Value.Entry, track.Duration))
            {
                var entry = fullCache.Value.Entry;
                TrackNormalizationHydrator.HydrateNormalization(track, entry);
                TryEnrichIntegratedLufsFromLocalSources(track);

                var descriptor = new ResolvedStreamDescriptor
                {
                    TrackId = track.Id,
                    Url = "",
                    Format = entry.Format,
                    Codec = entry.Codec,
                    BitrateKbps = entry.Bitrate,
                    ContentLengthBytes = entry.TotalSize,
                    Origin = StreamSource.DiskCacheFull
                };

                Log.Info($"[AudioEngine] ResolveStreamAsync FULL CACHE -> {descriptor}");
                return descriptor;
            }
        }

        // --- Path 2: Partial cache fast-start ---
        var bootstrapCache = TryGetPartialBootstrapCache(track, seekPosition);
        if (bootstrapCache != null)
        {
            TrackNormalizationHydrator.HydrateNormalization(track, bootstrapCache);
            TryEnrichIntegratedLufsFromLocalSources(track);

            if (TryGetCompatibleContinuationUrl(track, bootstrapCache, out var eagerUrl))
            {
                Log.Info($"[AudioEngine] Partial-cache fast start with eager continuation URL: {track.Id}");

                var descriptor = new ResolvedStreamDescriptor
                {
                    TrackId = track.Id,
                    Url = eagerUrl,
                    Format = bootstrapCache.Format,
                    Codec = bootstrapCache.Codec,
                    BitrateKbps = bootstrapCache.Bitrate,
                    ContentLengthBytes = bootstrapCache.TotalSize,
                    Origin = StreamSource.DiskCachePartial
                };

                Log.Info($"[AudioEngine] ResolveStreamAsync PARTIAL CACHE (eager URL) -> {descriptor}");
                return descriptor;
            }

            _ = PrimeContinuationUrlAsync(track, bootstrapCache, ct);

            Log.Info($"[AudioEngine] Partial-cache fast start: {track.Id}");

            var lazyDescriptor = new ResolvedStreamDescriptor
            {
                TrackId = track.Id,
                Url = "",
                Format = bootstrapCache.Format,
                Codec = bootstrapCache.Codec,
                BitrateKbps = bootstrapCache.Bitrate,
                ContentLengthBytes = bootstrapCache.TotalSize,
                Origin = StreamSource.DiskCachePartial
            };

            Log.Info($"[AudioEngine] ResolveStreamAsync PARTIAL CACHE (lazy URL) -> {lazyDescriptor}");
            return lazyDescriptor;
        }

        ct.ThrowIfCancellationRequested();

        // --- Path 3: Session cache (disk manifest with HEAD probe) ---
        var diskEntry = await SessionCacheStore
            .TryGetManifestAndProbeAsync(track.Id, SharedHttpClient.Instance, ct)
            .ConfigureAwait(false);

        if (diskEntry is { Variants.Count: > 0 })
        {
            var cacheEntry = FindNormalizationCacheEntry(track.Id);
            if (cacheEntry != null)
                TrackNormalizationHydrator.HydrateNormalization(track, cacheEntry);

            var selectedVariant = SelectBestVariantFromEntry(diskEntry.Variants, requested.Format);
            if (selectedVariant != null)
            {
                var format = selectedVariant.Format;
                var codec = selectedVariant.CodecType != AudioCodec.Unknown
                    ? selectedVariant.CodecType
                    : AudioSourceFactory.GetCodecForFormat(format);

                var descriptor = new ResolvedStreamDescriptor
                {
                    TrackId = track.Id,
                    Itag = selectedVariant.Itag,
                    Format = format,
                    Codec = codec,
                    BitrateKbps = selectedVariant.Bitrate / 1000,
                    ContentLengthBytes = selectedVariant.Clen,
                    Url = selectedVariant.Url,
                    ExpireUtc = diskEntry.ExpireUtc,
                    CdnHost = diskEntry.CdnHost,
                    IntegratedLufs = diskEntry.IntegratedLufs,
                    LanguageCode = selectedVariant.LanguageCode,
                    IsDefaultLanguage = selectedVariant.IsDefaultLanguage,
                    Origin = StreamSource.SessionCache
                };

                Log.Info($"[AudioEngine] Session cache hit: {track.Id} -> {descriptor}");
                return descriptor;
            }
        }

        // --- Path 4: Provider memory cache (RAM manifest) ---
        var memDescriptor = _youtube.TryGetCachedStreamDescriptor(
            track.Id,
            requested.Format,
            requested.BitrateKbps);

        if (memDescriptor != null)
        {
            if (memDescriptor.Value.ExpireUtc == default ||
                DateTime.UtcNow.AddMinutes(5) < memDescriptor.Value.ExpireUtc)
            {
                var cacheEntry = FindNormalizationCacheEntry(track.Id);
                if (cacheEntry != null)
                    TrackNormalizationHydrator.HydrateNormalization(track, cacheEntry);

                var descriptor = memDescriptor.Value with { TrackId = track.Id };
                Log.Info($"[AudioEngine] Provider memory cache hit: {track.Id} -> {descriptor}");
                return descriptor;
            }
            else
            {
                Log.Debug($"[AudioEngine] Provider memory cache hit ignored due to ExpireUtc passed: {track.Id}");
            }
        }

        // --- Path 5: YouTube API call (cold path) ---
        var freshDescriptor = await _youtube.RefreshStreamAsync(track, false, ct).ConfigureAwait(false)
                              ?? throw new InvalidOperationException($"Failed to resolve stream URL for {track.Id}");

        Log.Info($"[AudioEngine] ResolveStreamAsync YOUTUBE API -> {freshDescriptor}");
        return freshDescriptor;
    }

    #endregion

    #region URL Acquisition & Continuation

    /// <summary>
    /// Returns the current single-flight task fetching the track continuation URL, or starts a new one.
    /// </summary>
    /// <param name="track">Track requiring a continuation URL.</param>
    /// <param name="variant">
    /// Variant (container/bitrate) whose bytes are already present in source and partial cache;
    /// <c>null</c> — track preference (legacy behavior).
    /// </param>
    /// <param name="expectedContentLength">Full size of this variant (<c>clen</c>); <c>0</c> if unknown.</param>
    private Task<ContinuationUrlResult?> GetOrStartContinuationUrlAcquisitionAsync(
        TrackInfo track,
        StreamSelectionHint? variant,
        long expectedContentLength)
    {
        if (_pendingUrlAcquisitions.TryGetValue(track.Id, out var existing))
            return existing;

        var sessionToken = GetSessionToken();
        var created = AcquireContinuationUrlCoreAsync(track, variant, expectedContentLength, sessionToken);

        if (_pendingUrlAcquisitions.TryAdd(track.Id, created))
        {
            _ = RemovePendingContinuationAcquisitionAsync(track.Id, created);
            return created;
        }

        return _pendingUrlAcquisitions[track.Id];
    }

    /// <summary>
    /// Obtains a continuation URL for the exact variant already partially stored in cache:
    /// the session manifest is selected by exact <c>clen</c>, and refresh is performed with a source variant hint.
    /// A URL for a different variant is not returned: source guard would reject it anyway, stalling playback.
    /// </summary>
    /// <param name="track">Track requiring a continuation URL.</param>
    /// <param name="variant">Source variant; <c>null</c> — track preference.</param>
    /// <param name="expectedContentLength">Expected <c>clen</c>; <c>0</c> if unknown.</param>
    /// <param name="ct">Playback session token.</param>
    private async Task<ContinuationUrlResult?> AcquireContinuationUrlCoreAsync(
        TrackInfo track,
        StreamSelectionHint? variant,
        long expectedContentLength,
        CancellationToken ct)
    {
        var requested = variant is { HasFormat: true } exactVariant
            ? exactVariant
            : StreamSelectionHint.FromTrack(track, _library.Settings.RememberTrackFormat);

        var diskEntry = await SessionCacheStore
            .TryGetManifestAndProbeAsync(track.Id, SharedHttpClient.Instance, ct)
            .ConfigureAwait(false);

        if (diskEntry != null)
        {
            var selectedVariant = SelectContinuationVariantFromEntry(diskEntry.Variants, requested, expectedContentLength);
            if (selectedVariant != null)
            {
                return new ContinuationUrlResult(
                    selectedVariant.Url,
                    selectedVariant.Clen,
                    selectedVariant.Bitrate / 1000,
                    selectedVariant.Format,
                    selectedVariant.CodecType,
                    diskEntry.IntegratedLufs);
            }
        }

        var descriptor = await _youtube.RefreshStreamAsync(track, false, ct, variant).ConfigureAwait(false);
        if (descriptor is null || !descriptor.Value.HasLiveUrl)
            return null;

        var d = descriptor.Value;

        if (expectedContentLength > 0 && d.ContentLengthBytes > 0 && d.ContentLengthBytes != expectedContentLength)
        {
            Log.Warn($"[AudioEngine] Continuation URL for {track.Id} skipped: variant {d.Format}/{d.BitrateKbps}kbps " +
                     $"(itag={d.Itag}, clen={d.ContentLengthBytes}) does not match cached clen={expectedContentLength}");
            return null;
        }

        return new ContinuationUrlResult(
            d.Url,
            d.ContentLengthBytes,
            d.BitrateKbps,
            d.Format,
            d.Codec,
            d.IntegratedLufs);
    }

    private async Task RemovePendingContinuationAcquisitionAsync(
        string trackId,
        Task<ContinuationUrlResult?> task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }

        _pendingUrlAcquisitions.TryRemove(
            new KeyValuePair<string, Task<ContinuationUrlResult?>>(trackId, task));
    }

    private async ValueTask<TrackInfo?> TryResolveActiveTrackForCallbackAsync(
        string trackId,
        CancellationToken ct)
    {
        if (IsSealedFailedTrack(trackId)) return null;

        var track = (CurrentTrack?.Id == trackId ? CurrentTrack : null)
                    ?? _trackRegistry.TryGet(trackId)
                    ?? await _library.GetTrackAsync(trackId, ct).ConfigureAwait(false);

        return (track == null || IsSealedFailedTrack(trackId)) ? null : track;
    }

    /// <summary>
    /// Selects a session manifest variant for the continuation URL. Given a known size, only an exact match
    /// of container and <c>clen</c> is permitted: a neighboring variant of the same container (e.g., itag 250 instead of 251)
    /// has different byte offsets, and the source guard will reject it anyway.
    /// </summary>
    /// <param name="variants">Session manifest variants.</param>
    /// <param name="requested">Variant hint (container/bitrate).</param>
    /// <param name="expectedContentLength">Expected <c>clen</c>; <c>0</c> — select by container only (legacy behavior).</param>
    /// <returns>A matching variant or <c>null</c>.</returns>
    internal static VariantEntry? SelectContinuationVariantFromEntry(
        List<VariantEntry> variants,
        StreamSelectionHint requested,
        long expectedContentLength)
    {
        ArgumentNullException.ThrowIfNull(variants);

        if (expectedContentLength <= 0 || !requested.HasFormat)
            return SelectBestVariantFromEntry(variants, requested.Format);

        var blacklist = AudioSourceFactory.CdnBlacklist;
        VariantEntry? blockedMatch = null;

        for (int i = 0; i < variants.Count; i++)
        {
            var v = variants[i];
            if (v.Format != requested.Format || v.Clen != expectedContentLength)
                continue;

            if (!blacklist.IsBlockedUrl(v.Url))
                return v;

            blockedMatch ??= v;
        }

        return blockedMatch;
    }

    /// <summary>
    /// Unified source entry point for a stream URL. The request explicitly carries the track and stream variant of the initiating source,
    /// so the engine does not guess it from the active pipeline: this holds true during <c>InitializeAsync</c>,
    /// when the pipeline is not yet published to <c>AudioPlayer</c>.
    /// </summary>
    /// <param name="request">Source request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A live URL of a compatible variant, or <c>null</c>.</returns>
    private ValueTask<string?> ProvideStreamUrlAsync(StreamUrlRequest request, CancellationToken ct) =>
        request.Kind == StreamUrlRequestKind.Refresh
            ? RefreshUrlCallbackAsync(request, ct)
            : AcquireUrlCallbackAsync(request, ct);

    /// <summary>
    /// Checks whether the saved list of excluded clients belongs to the track and TTL has not expired. Must be called under lock.
    /// </summary>
    /// <param name="trackId">Track identifier.</param>
    private bool IsCdnForbiddenStateCurrent(string trackId) =>
        string.Equals(_cdnForbiddenClientsTrackId, trackId, StringComparison.Ordinal)
        && Environment.TickCount64 - _cdnForbiddenClientsTick < CdnForbiddenClientsTtlMs;

    /// <summary>
    /// Returns a snapshot of clients excluded after a CDN 403 for the track; an empty array if there are no exclusions.
    /// </summary>
    /// <param name="trackId">Track identifier.</param>
    private string[] GetCdnForbiddenClients(string trackId)
    {
        lock (_cdnForbiddenClientsLock)
        {
            return IsCdnForbiddenStateCurrent(trackId) ? _cdnForbiddenClients.ToArray() : Array.Empty<string>();
        }
    }

    /// <summary>
    /// Registers a client whose URL was rejected by the CDN with HTTP 403 and returns the current list of track exclusions.
    /// </summary>
    /// <param name="trackId">Track identifier.</param>
    /// <param name="clientName">Value of the <c>c</c> parameter from the rejected URL; <c>null</c> — extend TTL only.</param>
    private string[] RegisterCdnForbiddenClient(string trackId, string? clientName)
    {
        lock (_cdnForbiddenClientsLock)
        {
            if (!IsCdnForbiddenStateCurrent(trackId))
            {
                _cdnForbiddenClients.Clear();
                _cdnForbiddenClientsTrackId = trackId;
            }

            _cdnForbiddenClientsTick = Environment.TickCount64;

            if (!string.IsNullOrWhiteSpace(clientName)
                && !_cdnForbiddenClients.Exists(c => string.Equals(c, clientName, StringComparison.OrdinalIgnoreCase)))
            {
                _cdnForbiddenClients.Add(clientName);
            }

            return _cdnForbiddenClients.ToArray();
        }
    }

    /// <summary>
    /// Returns the URL of the refreshed descriptor only if it targets the same stream variant as source.
    /// </summary>
    /// <param name="request">Source request describing the expected stream variant.</param>
    /// <param name="descriptor">Refresh result.</param>
    /// <param name="trackId">Track identifier (for diagnostics).</param>
    /// <param name="stage">Refresh stage (for diagnostics).</param>
    /// <returns>A compatible live URL or <c>null</c>.</returns>
    private static string? AcceptRefreshedUrl(
        StreamUrlRequest request,
        ResolvedStreamDescriptor? descriptor,
        string trackId,
        string stage)
    {
        if (descriptor is not { HasLiveUrl: true } d)
            return null;

        if (!request.IsCompatible(d.Format, d.ContentLengthBytes))
        {
            Log.Warn($"[AudioEngine] {stage} refresh for {trackId} returned incompatible variant " +
                     $"({d.Format}/{d.BitrateKbps}kbps, itag={d.Itag}, clen={d.ContentLengthBytes}); source expects " +
                     $"{request.Format}/{request.BitrateKbps}kbps, clen={request.ContentLength}");
            return null;
        }

        return d.Url;
    }

    /// <summary>
    /// Refreshes the URL for the source stream variant: soft refresh, or on repeated CDN 403 — force refresh excluding the client.
    /// </summary>
    /// <param name="request">Source request with <see cref="StreamUrlRequestKind.Refresh"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A compatible live URL or <c>null</c>.</returns>
    private async ValueTask<string?> RefreshUrlCallbackAsync(StreamUrlRequest request, CancellationToken ct)
    {
        string trackId = request.TrackId;

        var track = await TryResolveActiveTrackForCallbackAsync(trackId, ct).ConfigureAwait(false);
        if (track == null)
        {
            Log.Warn($"[AudioEngine] URL refresh skipped for {trackId}: track is sealed after a fatal error or unresolvable");
            return null;
        }

        var sessionToken = GetSessionToken();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, sessionToken);

        // Refresh the exact variant (container/bitrate) whose bytes are already stored in source and partial cache,
        // rather than the current user preference: swapping WebM for MP4 breaks byte offsets.
        StreamSelectionHint? variant = request.ToSelectionHint();

        bool isForbiddenDriven = request.IsForbiddenDriven;

        // A URL issued for a different external IP (VPN/network node change) is rejected by the CDN regardless of client:
        // the client is not at fault and is not excluded; a fresh URL is acquired via standard soft refresh.
        bool isEgressMismatch = isForbiddenDriven
            && !LMP.Core.Youtube.Utils.StreamUrlEgressGuard.Shared.IsBoundToCurrentEgress(request.CurrentUrl);
        bool blameClient = isForbiddenDriven && !isEgressMismatch;

        // Client whose URL just received an HTTP 403 from CDN (parameter c=...).
        string? failedClient = blameClient
            ? UrlEx.TryGetQueryParameterValue(request.CurrentUrl, "c")
            : null;

        if (isEgressMismatch)
        {
            Log.Warn($"[AudioEngine] 403 for {trackId}: URL is bound to ip=" +
                     $"{LMP.Core.Youtube.Utils.StreamUrlEgressGuard.TryGetBoundIp(request.CurrentUrl)}, " +
                     $"current egress ip={LMP.Core.Youtube.Utils.StreamUrlEgressGuard.Shared.ObservedIp}; client is not blamed");
        }

        try
        {
            SessionCacheStore.Invalidate(trackId);
            _youtube.InvalidateMemoryCache(trackId);

            long nowTick = Environment.TickCount64;
            bool hadRecentSoftRefresh = string.Equals(_lastSoftRefreshTrackId, trackId, StringComparison.Ordinal)
                && (nowTick - Volatile.Read(ref _lastSoftRefreshTick)) < 60_000;

            // The first 403 for a track is usually an expired URL: soft refresh using the same client.
            // A repeated 403 means the CDN rejects URLs from this client — exclude it.
            string[] excludedClients = GetCdnForbiddenClients(trackId);
            if (blameClient && (hadRecentSoftRefresh || excludedClients.Length > 0))
                excludedClients = RegisterCdnForbiddenClient(trackId, failedClient);

            if (!hadRecentSoftRefresh || isEgressMismatch)
            {
                _lastSoftRefreshTrackId = trackId;
                Volatile.Write(ref _lastSoftRefreshTick, nowTick);

                Log.Info($"[AudioEngine] {(isForbiddenDriven ? "403" : "TTL")} refresh: per-track caches invalidated for {trackId}");

                var descriptor = await _youtube
                    .RefreshStreamAsync(track, false, linked.Token, variant, excludedClients.Length > 0 ? excludedClients : null)
                    .ConfigureAwait(false);

                string? softUrl = AcceptRefreshedUrl(request, descriptor, trackId, "Soft");
                if (softUrl != null)
                {
                    Log.Info($"[AudioEngine] Soft refresh returned new URL for {trackId}");
                    return softUrl;
                }
            }

            if (blameClient)
                excludedClients = RegisterCdnForbiddenClient(trackId, failedClient);

            Log.Warn($"[AudioEngine] Escalating 403 refresh to force refresh (PoToken + client rotation) for {trackId}, " +
                     $"failed client: {failedClient ?? "-"}, " +
                     $"excluded clients: {(excludedClients.Length == 0 ? "-" : string.Join(",", excludedClients))}");
            _lastSoftRefreshTrackId = null;

            // After a CDN 403, force refresh always follows CDN-403 ordering (WEB first), even if the client
            // is unrecognized: ANDROID_VR as first choice almost guaranteed hits bot detection and gets quarantined for 2 hours.
            IReadOnlyList<string>? forcedExclusions = isForbiddenDriven || excludedClients.Length > 0
                ? excludedClients
                : null;

            var forcedDescriptor = await _youtube
                .RefreshStreamAsync(track, true, linked.Token, variant, forcedExclusions)
                .ConfigureAwait(false);

            return AcceptRefreshedUrl(request, forcedDescriptor, trackId, "Force");
        }
        catch (Exception) when (linked.IsCancellationRequested
                                || sessionToken.IsCancellationRequested
                                || !string.Equals(CurrentTrack?.Id, trackId, StringComparison.Ordinal))
        {
            Log.Debug($"[AudioEngine] URL refresh for {trackId} abandoned: cancelled or track switched");
            return null;
        }
        catch (Exception ex)
        {
            // Background URL refresh errors (e.g., transient network/VPN drops) must not kill or remove the playing track;
            // returning null allows CachingStreamSource to enter a waiting state until network recovers.
            Log.Warn($"[AudioEngine] URL refresh failed for {trackId} (non-fatal, awaiting network recovery): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Obtains a continuation URL for the stream variant of partial-cache source.
    /// </summary>
    /// <param name="request">Source request with <see cref="StreamUrlRequestKind.Acquire"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Live URL or <c>null</c>.</returns>
    private async ValueTask<string?> AcquireUrlCallbackAsync(StreamUrlRequest request, CancellationToken ct)
    {
        // Continuation URL is requested for the source variant (partial cache), not for the track preference:
        // otherwise a WebM cache receives an MP4 URL, the guard rejects it, and playback stalls indefinitely.
        string trackId = request.TrackId;

        var track = await TryResolveActiveTrackForCallbackAsync(trackId, ct).ConfigureAwait(false);
        if (track == null) return null;

        try
        {
            var task = GetOrStartContinuationUrlAcquisitionAsync(
                track,
                request.ToSelectionHint(),
                request.ContentLength);
            var result = await task.WaitAsync(ct).ConfigureAwait(false);
            return result?.Url;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception) when (!string.Equals(CurrentTrack?.Id, trackId, StringComparison.Ordinal))
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Debug($"[AudioEngine] Acquire URL skipped for {trackId}: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region Partial Cache Fast Start

    private static int ComputePartialCacheBootstrapBytes(int bitrateKbps)
    {
        double bitrateBytesPerSec = Math.Max(1, bitrateKbps) * 1000.0 / 8.0;
        int bytes = (int)Math.Ceiling(bitrateBytesPerSec * PartialCacheBootstrapTargetMs / 1000.0);
        return Math.Clamp(bytes, PartialCacheBootstrapMinBytes, PartialCacheBootstrapMaxBytes);
    }

    private AudioCacheEntry? TryGetPartialBootstrapCache(TrackInfo track, TimeSpan? seekPosition)
    {
        if (seekPosition is { TotalMilliseconds: > 0 })
            return null;

        var cacheManager = AudioSourceFactory.GlobalCache;
        if (cacheManager == null)
            return null;

        int bitrateHint = StreamSelectionHint.FromTrack(track, _library.Settings.RememberTrackFormat).BitrateKbps;
        if (bitrateHint <= 0)
            bitrateHint = 160;

        int requiredBytes = ComputePartialCacheBootstrapBytes(bitrateHint);
        return cacheManager.FindBestStartupCache(track.Id, requiredBytes);
    }

    private bool TryAttachPrimedContinuationUrlToActiveSource(TrackInfo track, string url)
    {
        if (_disposed || string.IsNullOrWhiteSpace(url))
            return false;

        var current = CurrentTrack;
        if (current == null || !string.Equals(current.Id, track.Id, StringComparison.Ordinal))
            return false;

        var pipeline = _player.GetActivePipeline();
        if (pipeline?.Source is not Audio.Sources.CachingStreamSource cachingSource)
            return false;

        if (cachingSource.TryAttachContinuationUrl(url))
        {
            Log.Debug($"[AudioEngine] Primed continuation URL attached to live source: {track.Id}");
            return true;
        }

        return false;
    }

    private async Task PrimeContinuationUrlAsync(
        TrackInfo track,
        AudioCacheEntry expectedEntry,
        CancellationToken ct)
    {
        try
        {
            if (ct.IsCancellationRequested || IsSealedFailedTrack(track.Id))
                return;

            if (TryGetCompatibleContinuationUrl(track, expectedEntry, out var existingUrl))
            {
                TryAttachPrimedContinuationUrlToActiveSource(track, existingUrl);
                return;
            }

            var result = await GetOrStartContinuationUrlAcquisitionAsync(
                    track,
                    new StreamSelectionHint(expectedEntry.Format, expectedEntry.Bitrate),
                    expectedEntry.TotalSize)
                .WaitAsync(ct)
                .ConfigureAwait(false);

            if (result == null || string.IsNullOrEmpty(result.Value.Url))
                return;

            if (!IsContinuationVariantCompatible(expectedEntry, result.Value.Format, result.Value.Bitrate))
            {
                Log.Warn($"[AudioEngine] Continuation priming variant mismatch for {track.Id}: " +
                         $"expected={expectedEntry.Format}/{expectedEntry.Bitrate}kbps, " +
                         $"actual={result.Value.Format}/{result.Value.Bitrate}kbps");
                return;
            }

            TryAttachPrimedContinuationUrlToActiveSource(track, result.Value.Url);

            if (float.IsFinite(result.Value.IntegratedLufs))
            {
                CommitIntegratedLufs(
                    track.Id,
                    result.Value.IntegratedLufs,
                    LoudnessSource.YoutubePerceptual);

                UpdateRunningPipelineGain(track.Id, result.Value.IntegratedLufs);
            }

            Log.Info($"[AudioEngine] Partial-cache continuation primed: {track.Id} " +
                     $"({result.Value.Codec.ToDisplayName()}/{result.Value.Bitrate}kbps)");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Debug($"[AudioEngine] Continuation priming skipped for {track.Id}: {ex.Message}");
        }
    }

    #endregion

    #region Cache & Metadata Helpers

    /// <summary>
    /// Selects a suitable stereo variant from a manifest entry, filtering out multichannel (Surround 5.1) streams and CDN blacklisted hosts.
    /// </summary>
    private static VariantEntry? SelectBestVariantFromEntry(
        List<VariantEntry> variants,
        AudioFormat? preferredFormat)
    {
        if (variants.Count == 0) return null;

        var blacklist = AudioSourceFactory.CdnBlacklist;

        // Admissibility predicate: stereo/mono streams only (YouTube itags 328/338 and 5.1 surround are excluded)
        static bool IsStereoCompatible(VariantEntry v) =>
            v.Itag is not (325 or 328 or 338);

        // 1. Search by preferred format among non-blacklisted stereo streams
        if (preferredFormat is { } fmt && fmt != AudioFormat.Unknown)
        {
            for (int i = 0; i < variants.Count; i++)
            {
                var v = variants[i];
                if (v.Format != fmt || !IsStereoCompatible(v)) continue;
                if (!blacklist.IsBlockedUrl(v.Url)) return v;
            }

            for (int i = 0; i < variants.Count; i++)
            {
                var v = variants[i];
                if (v.Format == fmt && IsStereoCompatible(v)) return v;
            }
        }

        // 2. Format-agnostic: any compatible non-blacklisted stereo stream
        for (int i = 0; i < variants.Count; i++)
        {
            var v = variants[i];
            if (IsStereoCompatible(v) && !blacklist.IsBlockedUrl(v.Url)) return v;
        }

        // 3. Fallback: any stereo stream (even if CDN is blacklisted)
        for (int i = 0; i < variants.Count; i++)
        {
            var v = variants[i];
            if (IsStereoCompatible(v)) return v;
        }

        // 4. Extreme fallback to the first element in the list
        Log.Warn("[AudioEngine] No stereo-compatible variants found in manifest — falling back to first variant");
        return variants[0];
    }

    private static bool IsContinuationVariantCompatible(
        AudioCacheEntry expectedEntry,
        AudioFormat format,
        int bitrate)
    {
        if (format == AudioFormat.Unknown)
            return false;

        if (format != expectedEntry.Format)
            return false;

        if (bitrate <= 0 || expectedEntry.Bitrate <= 0)
            return true;

        string candidateKey = AudioSourceFactory.BuildCacheKey(
            expectedEntry.TrackId,
            format,
            bitrate);

        return string.Equals(candidateKey, expectedEntry.CacheKey, StringComparison.Ordinal);
    }

    private bool TryGetCompatibleContinuationUrl(
        TrackInfo track,
        AudioCacheEntry expectedEntry,
        out string url)
    {
        url = string.Empty;

        var manifest = SessionCacheStore.GetManifest(track.Id);

        if (manifest != null && IsContinuationUrlLikelyFresh(manifest))
        {
            for (int i = 0; i < manifest.Variants.Count; i++)
            {
                var variant = manifest.Variants[i];
                if (IsContinuationVariantCompatible(expectedEntry, variant.Format, variant.Bitrate / 1000))
                {
                    url = variant.Url;
                    return true;
                }
            }
        }

        var rawId = track.GetRawIdSpan().ToString();
        var descriptor = _youtube.TryGetCachedStreamDescriptor(
            rawId,
            expectedEntry.Format,
            expectedEntry.Bitrate);

        if (descriptor is { HasLiveUrl: true } d &&
            IsContinuationVariantCompatible(expectedEntry, d.Format, d.BitrateKbps))
        {
            if (d.ExpireUtc == default || DateTime.UtcNow.AddMinutes(5) < d.ExpireUtc)
            {
                url = d.Url;
                return true;
            }
        }

        return false;
    }

    private static bool IsContinuationUrlLikelyFresh(
        TrackManifestEntry manifest,
        int safetyBufferMinutes = 5)
    {
        if (manifest.ExpireUtc == default || manifest.ExpireUtc == DateTime.MinValue)
            return true;

        return DateTime.UtcNow.AddMinutes(safetyBufferMinutes) < manifest.ExpireUtc;
    }

    private static AudioCacheEntry? FindNormalizationCacheEntry(string trackId)
    {
        var cache = AudioSourceFactory.GlobalCache;
        if (cache == null || string.IsNullOrEmpty(trackId))
            return null;

        return cache.FindBestCacheByTrackId(trackId) ?? cache.FindBestStartupCache(trackId, 0);
    }

    private static void TryEnrichIntegratedLufsFromLocalSources(TrackInfo track)
    {
        if (track.HasIntegratedLufs)
            return;

        var manifest = SessionCacheStore.GetManifest(track.Id);
        if (manifest is null)
            return;

        if (!float.IsFinite(manifest.IntegratedLufs))
            return;

        track.SetIntegratedLufs(
            manifest.IntegratedLufs,
            LoudnessSource.YoutubePerceptual);

        AudioSourceFactory.GlobalCache.TryUpdateIntegratedLufs(
            track.Id,
            manifest.IntegratedLufs,
            LoudnessSource.YoutubePerceptual);

        Log.Debug($"[AudioEngine] Enriched LUFS from SessionCache: {track.Id} → {manifest.IntegratedLufs:F2} LUFS");
    }

    /// <summary>
    /// Validates whether the cached file size is plausible for the specified track duration.
    /// </summary>
    /// <param name="entry">The cache entry.</param>
    /// <param name="duration">The track duration from metadata.</param>
    /// <returns><c>true</c> if the file size conforms to codec physics.</returns>
    internal static bool IsCacheSizePlausible(AudioCacheEntry entry, TimeSpan duration)
    {
        if (duration <= TimeSpan.FromSeconds(5))
            return entry.TotalSize > 32 * 1024;

        int bitrate = entry.Bitrate > 0 ? entry.Bitrate : 128;
        long minExpectedBytes = (long)(duration.TotalSeconds * bitrate * 1000.0 / 8.0 * 0.50);
        return entry.TotalSize >= minExpectedBytes;
    }

    #endregion
}
