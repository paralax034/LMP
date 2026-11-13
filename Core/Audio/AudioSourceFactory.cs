using System.Net;
using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Http;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Sources;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio;

/// <summary>
/// Audio source factory. Determines format, checks cache,
/// and creates a suitable <see cref="IAudioSource"/>.
/// </summary>
public static class AudioSourceFactory
{
    private static AudioCacheManager? _globalCacheManager;
    private static AudioCacheManager? _subscribedCacheManager;
    private static Func<AudioCacheManager>? _lazyCacheFactory;
    private static volatile StreamingConfig _currentConfig = StreamingProfiles.Medium;

    /// <summary>
    /// Raised when a track format is cached or exported, without forcing eager initialization of <see cref="GlobalCache"/> on subscription.
    /// </summary>
    public static event Action<string, AudioFormat, int, bool>? OnFormatCached;

    /// <summary>
    /// Raised when the audio cache is cleared, without forcing eager initialization of <see cref="GlobalCache"/> on subscription.
    /// </summary>
    public static event Action? OnCacheCleared;

    /// <summary>
    /// Runtime blacklist of CDN hosts blocked for media traffic by TSPU.
    /// Singleton — persists across the entire application lifecycle.
    /// </summary>
    internal static readonly CdnBlacklist CdnBlacklist = new(ttl: TimeSpan.FromMinutes(5));

    /// <summary>
    /// Current active proxy settings for use in probe requests.
    /// </summary>
    internal static ProxySettings? CurrentProxySettings { get; set; }

    /// <summary>
    /// Registers a deferred initialization factory for the global cache manager from the DI container.
    /// </summary>
    public static void RegisterLazyFactory(Func<AudioCacheManager> factory)
    {
        _lazyCacheFactory = factory;
    }

    /// <summary>
    /// Initializes the global cache manager and wires static proxy events.
    /// </summary>
    public static void InitializeGlobalCache(AudioCacheManager cacheManager)
    {
        ArgumentNullException.ThrowIfNull(cacheManager);
        AttachGlobalCache(cacheManager);
        Log.Info("[AudioSourceFactory] Global cache initialized");
    }

    /// <summary>
    /// Global cache manager (created lazily on first access if not explicitly initialized).
    /// </summary>
    public static AudioCacheManager GlobalCache =>
        _globalCacheManager ?? AttachGlobalCache(_lazyCacheFactory?.Invoke() ?? new AudioCacheManager());

    /// <summary>
    /// Returns an already initialized instance of the global cache manager or <c>null</c> without triggering lazy creation.
    /// </summary>
    public static AudioCacheManager? TryGetInitializedGlobalCache() => _globalCacheManager;

    private static AudioCacheManager AttachGlobalCache(AudioCacheManager cacheManager)
    {
        _globalCacheManager = cacheManager;
        if (!ReferenceEquals(_subscribedCacheManager, cacheManager))
        {
            if (_subscribedCacheManager != null)
            {
                _subscribedCacheManager.OnFormatCached -= ForwardFormatCached;
                _subscribedCacheManager.OnCacheCleared -= ForwardCacheCleared;
            }

            _subscribedCacheManager = cacheManager;
            cacheManager.OnFormatCached += ForwardFormatCached;
            cacheManager.OnCacheCleared += ForwardCacheCleared;
        }

        return cacheManager;
    }

    private static void ForwardFormatCached(string trackId, AudioFormat format, int bitrate, bool isDownloaded) =>
        OnFormatCached?.Invoke(trackId, format, bitrate, isDownloaded);

    private static void ForwardCacheCleared() =>
        OnCacheCleared?.Invoke();

    /// <summary>
    /// Updates the streaming configuration.
    /// </summary>
    private static bool _isConfigInitialized;

    internal static void SetStreamingConfig(StreamingConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (_isConfigInitialized && ReferenceEquals(_currentConfig, config))
            return;

        _currentConfig = config;
        _isConfigInitialized = true;
        Log.Info($"[AudioSourceFactory] Config updated: " +
                 $"align={config.RequestAlignmentBytes / 1024}KB, " +
                 $"request={config.MinRequestSizeBytes / 1024}-{config.MaxRequestSizeBytes / 1024}KB, " +
                 $"targetBuffer={config.TargetBufferMs}ms, " +
                 $"concurrent={config.MaxConcurrentDownloads}");
    }

    /// <summary>
    /// Updates the configuration from the internet profile.
    /// </summary>
    public static void ApplyInternetProfile(InternetProfile profile)
    {
        SetStreamingConfig(StreamingProfiles.GetConfig(profile));
    }

    /// <summary>
    /// Builds a unique cache key: trackId + format + **normalized** bitrate.
    /// </summary>
    internal static string BuildCacheKey(string trackId, AudioFormat format, int bitrate)
    {
        int normalizedBitrate = NormalizeBitrate(bitrate);
        return $"{trackId}_{format}_{normalizedBitrate}";
    }

    /// <summary>
    /// Searches for a fully cached track of any format/bitrate.
    /// </summary>
    internal static (string Path, AudioCacheEntry Entry)? FindAnyCachedTrack(string trackId)
    {
        var cache = GlobalCache;
        if (cache == null) return null;

        var entry = cache.FindBestCache(trackId);
        if (entry == null) return null;

        var path = cache.GetCachePath(entry.CacheKey);
        if (!File.Exists(path)) return null;

        return (path, entry);
    }

    /// <summary>
    /// Creates an audio source instance based on a stream descriptor.
    /// </summary>
    /// <param name="descriptor">Descriptor of the resolved stream.</param>
    /// <param name="urlProvider">URL provider for continuation acquire and refresh on 403.</param>
    /// <param name="config">Streaming configuration settings.</param>
    /// <returns>An initialized audio source instance.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the global cache has not been initialized.</exception>
    /// <exception cref="ArgumentException">Thrown when both URL and local cache are missing.</exception>
    internal static Task<IAudioSource> CreateAsync(
        ResolvedStreamDescriptor descriptor,
        StreamUrlProvider? urlProvider = null,
        StreamingConfig? config = null)
    {
        var cacheManager = GlobalCache;
        if (cacheManager == null)
        {
            throw new InvalidOperationException(
                "AudioSourceFactory.InitializeGlobalCache() must be called before creating sources");
        }

        Log.Info($"[AudioSourceFactory] CreateAsync -> {descriptor}");

        config ??= _currentConfig;
        string trackId = descriptor.TrackId;
        string url = descriptor.Url;
        var format = descriptor.Format;
        var codec = descriptor.Codec;
        int bitrateKbps = descriptor.BitrateKbps;
        long contentLength = descriptor.ContentLengthBytes;

        if (!string.IsNullOrEmpty(url) && File.Exists(url))
        {
            Log.Info($"[AudioSourceFactory] Source decision: LocalFileSource, track={trackId}, reason=local-download");
            return Task.FromResult<IAudioSource>(new LocalFileSource(
                url,
                contentLength,
                trackId,
                null,
                null));
        }

        // Calculate the canonical cache key for the given track and format
        string resolvedCacheKey = format != AudioFormat.Unknown && bitrateKbps > 0
            ? BuildCacheKey(trackId, format, bitrateKbps)
            : string.Empty;

        // Cache-only or empty URL path
        if (string.IsNullOrEmpty(url))
        {
            // If Partial Cache is explicitly enabled, prevent fallback to LocalFileSource: launch CachingStreamSource
            if (descriptor.Origin == StreamSource.DiskCachePartial)
            {
                AudioCacheEntry? startupEntry = !string.IsNullOrEmpty(resolvedCacheKey)
                    ? cacheManager.GetCacheInfo(resolvedCacheKey)
                    : null;

                if (startupEntry == null)
                {
                    int bootstrapBytes = Math.Min(config.InitialPrebufferBytes, int.MaxValue);
                    startupEntry = cacheManager.FindBestStartupCache(trackId, bootstrapBytes);
                }

                long effectiveLength = contentLength > 0
                    ? contentLength
                    : (startupEntry?.TotalSize ?? 0);

                string partialCacheKey = !string.IsNullOrEmpty(resolvedCacheKey)
                    ? resolvedCacheKey
                    : (startupEntry?.CacheKey ?? BuildCacheKey(trackId, format, bitrateKbps));

                return Task.FromResult<IAudioSource>(CreatePartialCacheSource(
                    partialCacheKey,
                    trackId,
                    effectiveLength,
                    format != AudioFormat.Unknown ? format : (startupEntry?.Format ?? AudioFormat.WebM),
                    codec != AudioCodec.Unknown ? codec : (startupEntry?.Codec ?? AudioCodec.Opus),
                    bitrateKbps > 0 ? bitrateKbps : (startupEntry?.Bitrate ?? 128),
                    cacheManager,
                    config,
                    urlProvider));
            }

            if (TryCreateFullCacheSource(cacheManager, resolvedCacheKey, trackId) is { } exactSource)
                return Task.FromResult<IAudioSource>(exactSource);

            var cached = FindAnyCachedTrack(trackId);
            if (cached != null)
            {
                Log.Info($"[AudioSourceFactory] Source decision: LocalFileSource, track={trackId}, reason=full-cache");
                return Task.FromResult<IAudioSource>(new LocalFileSource(
                    cached.Value.Path,
                    cached.Value.Entry.TotalSize,
                    trackId,
                    cacheManager,
                    cached.Value.Entry.CacheKey));
            }

            int defaultBootstrapBytes = Math.Min(config.InitialPrebufferBytes, int.MaxValue);
            var fallbackStartupEntry = cacheManager.FindBestStartupCache(trackId, defaultBootstrapBytes);
            if (fallbackStartupEntry != null)
            {
                return Task.FromResult<IAudioSource>(CreatePartialCacheSource(
                    fallbackStartupEntry.CacheKey,
                    trackId,
                    fallbackStartupEntry.TotalSize,
                    fallbackStartupEntry.Format,
                    fallbackStartupEntry.Codec,
                    fallbackStartupEntry.Bitrate > 0 ? fallbackStartupEntry.Bitrate : bitrateKbps,
                    cacheManager,
                    config,
                    urlProvider));
            }

            throw new ArgumentException("No URL provided and no cache available", nameof(descriptor));
        }

        // Format and codec come from descriptor — no HTTP detect needed
        if (format == AudioFormat.Unknown)
        {
            format = DetectFormat(url);
            if (format == AudioFormat.Unknown)
                throw new NotSupportedException($"Could not detect audio format for descriptor: {trackId}");
        }

        if (codec == AudioCodec.Unknown)
            codec = GetCodecForFormat(format);

        if (contentLength <= 0)
        {
            contentLength = ExtractContentLengthFromUrl(url);
            if (contentLength <= 0)
                contentLength = 100 * 1024 * 1024;
        }

        string finalCacheKey = !string.IsNullOrEmpty(resolvedCacheKey)
            ? resolvedCacheKey
            : BuildCacheKey(trackId, format, bitrateKbps);

        if (descriptor.Origin != StreamSource.DiskCachePartial
            && TryCreateFullCacheSource(cacheManager, finalCacheKey, trackId) is { } fullCacheSource)
        {
            return Task.FromResult<IAudioSource>(fullCacheSource);
        }

        Log.Info($"[AudioSourceFactory] Source decision: CachingStreamSource, track={trackId}, reason=live-stream, cacheKey={finalCacheKey}, hasLiveUrl={descriptor.HasLiveUrl}");

        return Task.FromResult<IAudioSource>(new CachingStreamSource(
            finalCacheKey,
            trackId,
            url,
            contentLength,
            format,
            codec,
            bitrateKbps,
            cacheManager,
            config,
            urlProvider));
    }

    /// <summary>
    /// Creates an audio source instance via a direct URL (legacy overload).
    /// </summary>
    /// <param name="url">Direct stream URL or path to a local file.</param>
    /// <param name="httpClient">HTTP client for network requests.</param>
    /// <param name="urlProvider">URL provider for continuation acquire and refresh on 403.</param>
    /// <param name="trackId">Track identifier.</param>
    /// <param name="bitrateHint">Estimated bitrate in kbps.</param>
    /// <param name="config">Streaming configuration settings.</param>
    /// <param name="ct">Operation cancellation token.</param>
    /// <returns>An initialized audio source instance.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the global cache has not been initialized.</exception>
    /// <exception cref="ArgumentException">Thrown when URL is empty and no cache is available.</exception>
    internal static async Task<IAudioSource> CreateAsync(
          string url,
          HttpClient httpClient,
          StreamUrlProvider? urlProvider = null,
          string? trackId = null,
          int bitrateHint = 0,
          StreamingConfig? config = null,
          CancellationToken ct = default)
    {
        var cacheManager = GlobalCache;
        if (cacheManager == null)
        {
            throw new InvalidOperationException(
                "AudioSourceFactory.InitializeGlobalCache() must be called before creating sources");
        }

        config ??= _currentConfig;
        trackId ??= GenerateTrackIdFromUrl(url);

        if (!string.IsNullOrEmpty(url) && File.Exists(url))
        {
            Log.Info($"[AudioSourceFactory] Source decision: LocalFileSource, reason=local-download");
            return new LocalFileSource(url, 0, trackId, null, null);
        }

        if (string.IsNullOrEmpty(url))
        {
            var cached = FindAnyCachedTrack(trackId);
            if (cached != null)
            {
                Log.Info($"[AudioSourceFactory] Using cached: {trackId} " +
                         $"({cached.Value.Entry.Format}/{cached.Value.Entry.Bitrate}kbps)");
                return new LocalFileSource(
                    cached.Value.Path,
                    cached.Value.Entry.TotalSize,
                    trackId,
                    cacheManager,
                    cached.Value.Entry.CacheKey);
            }

            int bootstrapBytes = Math.Min(config.InitialPrebufferBytes, int.MaxValue);
            var startupEntry = cacheManager.FindBestStartupCache(trackId, bootstrapBytes);
            if (startupEntry != null)
            {
                Log.Info($"[AudioSourceFactory] Partial-cache bootstrap: {trackId} " +
                         $"({startupEntry.Format}/{startupEntry.Bitrate}kbps, " +
                         $"prefix={startupEntry.GetContiguousDownloadedBytesFrom(0) / 1024}KB)");

                return new CachingStreamSource(
                    startupEntry.CacheKey,
                    trackId,
                    url: string.Empty,
                    contentLength: startupEntry.TotalSize,
                    format: startupEntry.Format,
                    codec: startupEntry.Codec,
                    bitrate: startupEntry.Bitrate > 0 ? startupEntry.Bitrate : bitrateHint,
                    cacheManager: cacheManager,
                    config: config,
                    urlProvider: urlProvider);
            }

            throw new ArgumentException("No URL provided and no cache available", nameof(url));
        }

        var format = await DetectFormatAsync(url, httpClient, ct).ConfigureAwait(false);
        if (format == AudioFormat.Unknown)
            throw new NotSupportedException($"Could not detect audio format for: {url}");

        var (contentLength, codec, detectedBitrate) =
            await GetStreamInfoAsync(url, format, httpClient, ct).ConfigureAwait(false);

        int finalBitrate = bitrateHint > 0 ? bitrateHint : detectedBitrate;
        string cacheKey = BuildCacheKey(trackId, format, finalBitrate);

        if (cacheManager.IsFullyCached(cacheKey))
        {
            var cachePath = cacheManager.GetCachePath(cacheKey);
            if (File.Exists(cachePath))
            {
                var exactEntry = cacheManager.GetCacheInfo(cacheKey);
                Log.Info($"[AudioSourceFactory] Exact cache hit: {cacheKey}");
                return new LocalFileSource(
                    cachePath,
                    exactEntry?.TotalSize ?? 0,
                    trackId,
                    cacheManager,
                    cacheKey);
            }
        }

        Log.Info($"[AudioSourceFactory] Streaming {format}/{codec}/{finalBitrate}kbps: {cacheKey}");

        return new CachingStreamSource(
            cacheKey,
            trackId,
            url,
            contentLength,
            format,
            codec,
            finalBitrate,
            cacheManager,
            config,
            urlProvider);
    }

    /// <summary>
    /// Triggers speculative TCP+TLS connection warmup to the last known CDN hosts.
    /// </summary>
    internal static void PreWarmCdnConnections(HttpClient httpClient, CancellationToken ct)
    {
        CdnConnectionPreWarmer.PreWarmRecentHosts(httpClient, ct);
    }

    /// <summary>
    /// Returns cache information for the track.
    /// </summary>
    internal static AudioCacheEntry? GetCacheInfo(string trackId) =>
        _globalCacheManager?.FindBestCache(trackId);

    #region Format Detection

    /// <summary>
    /// Determines format by URL (MIME parameters, extension).
    /// </summary>
    public static AudioFormat DetectFormat(string url)
    {
        if (string.IsNullOrEmpty(url))
            return AudioFormat.Unknown;

        if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("/hls/", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.Hls;

        if (url.Contains("mime=audio%2Fwebm", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("mime=audio/webm", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.WebM;

        if (url.Contains("mime=audio%2Fmp4", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("mime=audio/mp4", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.Mp4;

        if (url.Contains("mime=audio%2Fogg", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("mime=audio/ogg", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.Ogg;

        if (url.Contains(".webm", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.WebM;

        if (url.Contains(".mp4", StringComparison.OrdinalIgnoreCase) ||
            url.Contains(".m4a", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.Mp4;

        if (url.Contains(".ogg", StringComparison.OrdinalIgnoreCase) ||
            url.Contains(".opus", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.Ogg;

        return AudioFormat.Unknown;
    }

    /// <summary>
    /// Determines format: first by URL, then by magic bytes.
    /// </summary>
    internal static async Task<AudioFormat> DetectFormatAsync(
        string url, HttpClient httpClient, CancellationToken ct = default)
    {
        var urlFormat = DetectFormat(url);
        if (urlFormat != AudioFormat.Unknown)
            return urlFormat;

        try
        {
            using var request = SharedHttpClient.CreateRangeRequest(url, 0, FormatDetectionHeaderSize - 1);
            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode == HttpStatusCode.Forbidden)
                return AudioFormat.Unknown;

            response.EnsureSuccessStatusCode();

            var header = await response.Content.ReadAsByteArrayAsync(ct);
            return DetectFormatByMagic(header);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"[AudioSourceFactory] Format detection failed: {ex.Message}");
            return AudioFormat.Unknown;
        }
    }

    /// <summary>
    /// Determines format by header magic bytes.
    /// </summary>
    internal static AudioFormat DetectFormatByMagic(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4) return AudioFormat.Unknown;

        if (header[..4].SequenceEqual(WebMMagic))
            return AudioFormat.WebM;

        if (header.Length >= 8 && header.Slice(4, 4).SequenceEqual(Mp4FtypMagic))
            return AudioFormat.Mp4;

        if (header[..4].SequenceEqual(OggMagic))
            return AudioFormat.Ogg;

        return AudioFormat.Unknown;
    }

    /// <summary>
    /// Determines codec by container format.
    /// </summary>
    public static AudioCodec GetCodecForFormat(AudioFormat format) => format switch
    {
        AudioFormat.WebM => AudioCodec.Opus,
        AudioFormat.Ogg => AudioCodec.Opus,
        AudioFormat.Mp4 => AudioCodec.Aac,
        AudioFormat.Hls => AudioCodec.Aac,
        _ => AudioCodec.Unknown
    };

    #endregion

    #region Private Helpers

    private static string GenerateTrackIdFromUrl(string url)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(url));
        return "cache_" + Convert.ToHexString(hash)[..16];
    }

    internal static async Task<(long ContentLength, AudioCodec Codec, int Bitrate)> GetStreamInfoAsync(
     string url, AudioFormat format, HttpClient httpClient, CancellationToken ct)
    {
        long contentLength = 0;

        if (url.Contains("googlevideo.com/videoplayback"))
        {
            contentLength = ExtractContentLengthFromUrl(url);
        }
        else
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = await httpClient.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                    contentLength = response.Content.Headers.ContentLength ?? 0;
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        if (contentLength <= 0)
            contentLength = 100 * 1024 * 1024;

        var codec = GetCodecForFormat(format);
        int bitrate = ExtractBitrateFromUrl(url);
        if (bitrate == 0)
            bitrate = codec == AudioCodec.Opus ? 128 : 96;

        return (contentLength, codec, bitrate);
    }

    private static LocalFileSource? TryCreateFullCacheSource(
        AudioCacheManager cacheManager,
        string cacheKey,
        string trackId)
    {
        if (string.IsNullOrEmpty(cacheKey) || !cacheManager.IsFullyCached(cacheKey))
            return null;

        string cachePath = cacheManager.GetCachePath(cacheKey);
        if (!File.Exists(cachePath))
            return null;

        var exactEntry = cacheManager.GetCacheInfo(cacheKey);
        Log.Info($"[AudioSourceFactory] Source decision: LocalFileSource, track={trackId}, reason=full-cache");
        return new LocalFileSource(
            cachePath,
            exactEntry?.TotalSize ?? 0,
            trackId,
            cacheManager,
            cacheKey);
    }

    private static CachingStreamSource CreatePartialCacheSource(
        string cacheKey,
        string trackId,
        long contentLength,
        AudioFormat format,
        AudioCodec codec,
        int bitrate,
        AudioCacheManager cacheManager,
        StreamingConfig config,
        StreamUrlProvider? urlProvider)
    {
        Log.Info($"[AudioSourceFactory] Source decision: CachingStreamSource, track={trackId}, reason=partial-cache-bootstrap, cacheKey={cacheKey}");
        return new CachingStreamSource(
            cacheKey,
            trackId,
            url: string.Empty,
            contentLength: contentLength,
            format: format,
            codec: codec,
            bitrate: bitrate,
            cacheManager: cacheManager,
            config: config,
            urlProvider: urlProvider);
    }

    private static long ExtractContentLengthFromUrl(string url) =>
        long.TryParse(UrlEx.TryGetQueryParameterValue(url, "clen"), out var clen) ? clen : 0;

    private static int ExtractBitrateFromUrl(string url) =>
        int.TryParse(UrlEx.TryGetQueryParameterValue(url, "bitrate"), out var br) ? br / 1000 : 0;

    #endregion
}

public enum AudioFormat
{
    Unknown = 0,
    WebM = 1,
    Mp4 = 2,
    Ogg = 3,
    Hls = 4
}