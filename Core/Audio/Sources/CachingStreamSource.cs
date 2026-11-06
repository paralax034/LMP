using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Exceptions;

namespace LMP.Core.Audio.Sources;

/// <summary>
/// Audio source featuring range-based caching and HTTP Range request loading.
///
/// <para><b>Architecture:</b></para>
/// <list type="bullet">
///   <item>Data is loaded in arbitrary ranges with adaptive size (MAPO)</item>
///   <item>Ranges are cached in RAM (<see cref="SlidingRamCache"/>) and on disk (<see cref="AudioCacheManager"/>)</item>
///   <item>Background preload loop maintains a buffer around the current position</item>
///   <item>Seek is implemented via epoch-based cancellation</item>
///   <item>Suspend/Resume pauses background downloading when the window is minimized</item>
/// </list>
///
/// <para><b>Partial class structure:</b></para>
/// <list type="bullet">
///   <item><c>CachingStreamSource.cs</c> — core: fields, constructor, properties, read frames, network assessment</item>
///   <item><c>CachingStreamSource.Init.cs</c> — initialization, parser creation, startup prefetch</item>
///   <item><c>CachingStreamSource.Lifecycle.cs</c> — dispose, epoch cancellation, drain pending writes</item>
///   <item><c>CachingStreamSource.Download.cs</c> — range downloading, ReadAtAsync, EnsureRangeAsync, adaptive metrics</item>
///   <item><c>CachingStreamSource.Planning.cs</c> — MAPO planner, alignment, coverage queries</item>
///   <item><c>CachingStreamSource.Refresh.cs</c> — URL refresh, 403 diagnostics, HTTP request building</item>
///   <item><c>CachingStreamSource.Cache.cs</c> — SlidingRamCache, RamRangeBlock</item>
///   <item><c>CachingStreamSource.Coordination.cs</c> — active download dedup and coordination</item>
///   <item><c>CachingStreamSource.Seeking.cs</c> — seek with epoch-based cancellation</item>
///   <item><c>CachingStreamSource.Preload.cs</c> — background downloading and buffering</item>
///   <item><c>CachingStreamSource.ReadStream.cs</c> — Stream wrapper for parsers</item>
/// </list>
/// </summary>
public sealed partial class CachingStreamSource : IAudioSource
{
    #region Constants

    /// <summary>Short delay before final resource disposal (ms).</summary>
    private const int DisposalDelayMs = 32;

    /// <summary>Critical timeout for suspend-mode backfetching (ms).</summary>
    private const int PlaybackGateCriticalTimeoutMs = 512;

    /// <summary>Number of read retries on epoch change.</summary>
    private const int ReadAtMaxEpochRetries = 3;

    /// <summary>Delay between read retries on epoch change (ms).</summary>
    private const int ReadAtEpochRetryDelayMs = 30;

    /// <summary>
    /// Unified refresh budget after HTTP 403: maximum attempts allowed to acquire a URL without a single successful CDN response.
    /// Consumption is determined by the <see cref="DecideRefresh"/> table; once exhausted, the source throws
    /// <see cref="ChunkDownloadFatalException"/> instead of an infinite retry loop.
    /// </summary>
    private const int MaxForbiddenRefreshBudget = 3;

    /// <summary>Minimum bound for seek clamp.</summary>
    private const long SeekLowerBound = 0;

    /// <summary>Offset for the last byte of content.</summary>
    private const long SeekEndOffset = 1;

    /// <summary>Delay before disposing the CTS of the previous epoch (ms).</summary>
    private const int DeferredEpochDisposeDelayMs = 2000;

    /// <summary>Timeout for awaiting preload task completion during DisposeAsync (ms).</summary>
    private const int PreloadTaskDisposeWaitTimeoutMs = 1000;

    /// <summary>Buffer hysteresis to prevent jitter around target (ms).</summary>
    private const int TargetBufferHysteresisMs = 2500;

    /// <summary>Critical refill threshold (ms). Below this, aggressive prefetch is triggered.</summary>
    private const int CriticalRefillBufferMs = 4000;

    /// <summary>Emergency buffer threshold (ms). Below this, maximum aggressive loading is triggered.</summary>
    private const int EmergencyRefillBufferMs = 1500;

    /// <summary>Threshold of consecutive network failures to publish a stall event.</summary>
    private const int NetworkStallThreshold = 5;

    /// <summary>Base network retry delay in ReadAtAsync (ms).</summary>
    private const int NetworkRetryBaseMs = 500;

    /// <summary>Maximum network retry delay ceiling in ReadAtAsync (ms).</summary>
    private const int NetworkRetryMaxBackoffMs = 5000;

    /// <summary>Threshold of consecutive network errors to open the source-level circuit breaker.</summary>
    private const int SourceCircuitBreakerThreshold = 15;

    /// <summary>Initial circuit breaker backoff (ms).</summary>
    private const int SourceCircuitBreakerInitialBackoffMs = 2_000;

    /// <summary>Maximum pause between HTTP response body chunks before the connection is considered hung (ms).</summary>
    private const int BodyIdleTimeoutMs = 10_000;

    /// <summary>Maximum circuit breaker backoff (ms).</summary>
    private const int SourceCircuitBreakerMaxBackoffMs = 30_000;

    #endregion

    #region Enums

    /// <summary>
    /// Degradation level of the current network connection.
    /// Affects concurrency, request size, and preload aggressiveness.
    /// </summary>
    private enum NetworkDegradationLevel
    {
        /// <summary>Network is normal. Standard parameters.</summary>
        Normal,

        /// <summary>Increased latency or limited bandwidth. Restricted concurrency.</summary>
        Degraded,

        /// <summary>Extreme latency or minimal bandwidth. Single thread, small requests.</summary>
        Critical
    }

    #endregion

    #region Fields

    //  Configuration 
    private readonly StreamingConfig _config;

    //  Identity 
    private readonly string _cacheKey;
    private readonly string _trackId;
    private readonly long _contentLength;
    private readonly AudioFormat _format;
    private readonly int _bitrate;

    /// <summary>
    /// Flag indicating that a critical network operation for Seek is currently in progress.
    /// The preload loop must pause to avoid competing for network bandwidth.
    /// </summary>
    private volatile bool _seekInProgress;

    //  Transport alignment 
    private int _requestAlignmentBytes;

    //  Dependencies 

    /// <summary>
    /// The HTTP client is retrieved from SharedHttpClient.Instance on every request
    /// to automatically use the rebuilt client after Rebuild().
    /// Capturing the reference in the constructor would lead to using a disposed client.
    /// An explicitly injected client (for deterministic tests) takes precedence.
    /// </summary>
    private HttpClient CurrentHttpClient => _httpClientOverride ?? Audio.Http.SharedHttpClient.Instance;

    /// <summary>
    /// Injected HTTP client; <c>null</c> means the application shared client is used.
    /// </summary>
    private readonly HttpClient? _httpClientOverride;

    private readonly AudioCacheManager _cacheManager;

    /// <summary>
    /// Stream URL provider: initial continuation acquire (partial cache) and refresh after 403/TTL expiration.
    /// Requests are always constructed by this source (<see cref="CreateUrlRequest"/>), so the recipient knows the track and variant.
    /// </summary>
    private readonly StreamUrlProvider? _urlProvider;

    //  Parsing 

    /// <summary>
    /// Cache metadata for the current track. Guaranteed not null after successful
    /// <see cref="InitializeAsync"/>; accessing outside init is a logic error.
    /// </summary>
    private AudioCacheEntry? _cacheEntry;
    private IContainerParser? _parser;
    private AsyncCachingReadStream? _readStream;

    //  RAM cache & active downloads 

    /// <summary>
    /// Byte range RAM cache optimized for a small number of active blocks
    /// in a sliding window around the current playback position.
    /// </summary>
    private readonly SlidingRamCache _ramCache = new();

    /// <summary>
    /// Active HTTP download registry for deduplication.
    /// Key = aligned range start offset. Strict overlap protection.
    /// </summary>
    private readonly Lock _activeDownloadsLock = new();
    private readonly Dictionary<long, ActiveRangeDownload> _activeDownloads = new(8);

    /// <summary>Concurrent download semaphore.</summary>
    private readonly SemaphoreSlim _downloadSlots;

    //  Epoch-based cancellation 
    private long _downloadEpoch;
    private CancellationTokenSource? _downloadCts;
    private readonly Lock _epochLock = new();

    /// <summary>
    /// Lightweight gate to control the background preload loop.
    /// Set = playback active, Reset = player paused.
    /// </summary>
    private readonly ManualResetEventSlim _playbackGate = new(initialState: true);

    /// <summary>
    /// ManualResetEventSlim to block the preload loop during suspend.
    /// Set = running, Reset = paused.
    /// </summary>
    private readonly ManualResetEventSlim _suspendGate = new(initialState: true);

    //  Lifecycle 
    private CancellationTokenSource? _lifetimeCts;
    private Task? _preloadTask;

    /// <summary>Counter of pending background disk write operations.</summary>
    private int _pendingDiskWrites;

    /// <summary>Flag indicating whether the lease was successfully acquired.</summary>
    private volatile bool _leaseAcquired;

    /// <summary>
    /// Single-flight promise for initial continuation URL resolution.
    /// Ensures that only one URL resolution runs concurrently.
    /// Not used for 403 refreshes — those go through <see cref="CoordinatedRefreshAsync"/>.
    /// </summary>
    private TaskCompletionSource<string?>? _continuationUrlTcs;
    private readonly Lock _continuationLock = new();

    //  Position tracking 
    private long _currentReadOffset;
    private long _positionMs;
    private string _currentUrl;

    //  Refresh / retry state 
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private DateTime _lastRefreshTime = DateTime.MinValue;
    private int _consecutive403Count;
    private int _requestSequenceNumber;
    /// <summary>
    /// Consumed unified refresh budget after HTTP 403. A successful refresh does NOT reset it —
    /// only a real 2xx from the CDN does, ensuring the "new URL → 403 again" cycle is finite.
    /// </summary>
    private int _forbiddenRefreshBudgetSpent;

    /// <summary>Outcome of the last attempt that consumed budget; used exclusively for fatal diagnostics.</summary>
    private RefreshOutcome _lastBudgetRefreshOutcome;

    /// <summary>Flag (0/1): free refresh after external IP change has already been consumed in the current budget window.</summary>
    private int _egressRefreshUsed;
    private Exception? _lastDownloadException;

    // Source-level circuit breaker
    private readonly Lock _circuitBreakerLock = new();
    private int _consecutiveSourceNetworkFailures;
    private bool _circuitBreakerIsOpen;
    private long _circuitBreakerOpenedAtTick;
    private int _circuitBreakerBackoffMs;

    //  Latency Tracking & Adaptive Transport 
    private readonly object _latencyLock = new();
    private double _latency0;
    private double _latency1;
    private double _latency2;
    private double _estimatedBandwidthBytesPerSec;

    /// <summary>
    /// Accumulated bandwidth sample count.
    /// Used to determine the EMA bootstrap phase:
    /// initial <see cref="StreamingConfig.BandwidthBootstrapSampleCount"/> samples
    /// receive higher weight for rapid initial convergence.
    /// </summary>
    private int _bandwidthSampleCount;

    //  State flags 
    private volatile bool _initialized;
    private volatile bool _disposed;

    // Cache calculated buffering ranges to eliminate allocations on the 500ms timer
    private readonly Lock _bufferedRangesCacheLock = new();
    private long _cachedDiskRangesVersion = -1;
    private long _cachedRamRangesVersion = -1;
    private (double Start, double End)[] _cachedBufferedRanges = [];

    #endregion

    #region Properties

    /// <summary>Current estimated network download speed (bytes/sec).</summary>
    public double EstimatedSpeedBytesPerSec
    {
        get { lock (_latencyLock) return _estimatedBandwidthBytesPerSec; }
    }

    /// <summary>Current average network latency (ms).</summary>
    public double AveragePingMs
    {
        get { lock (_latencyLock) return GetAverageLatencyInternal(); }
    }

    /// <inheritdoc/>
    public long DurationMs => _parser?.DurationMs ?? _cacheEntry?.DurationMs ?? -1;

    /// <inheritdoc/>
    public long PositionMs => Volatile.Read(ref _positionMs);

    /// <inheritdoc/>
    public bool CanSeek => true;

    /// <inheritdoc/>
    public AudioCodec Codec { get; private set; }

    /// <inheritdoc/>
    public byte[]? DecoderConfig => _parser?.DecoderConfig;

    /// <inheritdoc/>
    public int SampleRate => _parser?.SampleRate ?? 0;

    /// <inheritdoc/>
    public int Channels => _parser?.Channels ?? 0;

    /// <summary>Buffering progress (0–100%).</summary>
    public double BufferProgress => _cacheEntry?.DownloadProgress ?? 0;

    /// <summary>
    /// Estimated forward contiguous buffer length from the current read position in milliseconds.
    /// </summary>
    /// <remarks>
    /// Uses locally available and in-flight ranges.
    /// Required for latency-aware decisions on safely opening the playback gate.
    /// </remarks>
    public int BufferedAheadMs
    {
        get
        {
            if (!_initialized) return 0;

            long position = Volatile.Read(ref _currentReadOffset);
            if (position < 0) return 0;
            if (position >= _contentLength) return int.MaxValue;

            long bufferedAhead = GetBufferedBytesAheadIncludingInflight(position);
            if (position + bufferedAhead >= _contentLength)
                return int.MaxValue;

            return ConvertBufferedBytesToMs(bufferedAhead);
        }
    }

    /// <summary>
    /// Indicates that all data from the current read position to the very end of the file (EOF) is locally available in RAM or on disk.
    /// </summary>
    public bool IsTailLocallyBuffered
    {
        get
        {
            if (!_initialized) return false;
            if (_cacheEntry?.IsComplete == true) return true;

            long position = Volatile.Read(ref _currentReadOffset);
            if (position < 0) return false;
            if (position >= _contentLength) return true;

            long localAhead = GetBufferedBytesAhead(position);
            return position + localAhead >= _contentLength;
        }
    }

    /// <summary>Whether the track is fully downloaded to disk.</summary>
    public bool IsFullyBuffered => _cacheEntry?.IsComplete ?? false;

    /// <summary>Volume of downloaded data in bytes.</summary>
    public long DownloadedBytes => _cacheEntry?.DownloadedBytes ?? 0;

    /// <summary>Bitrate (kbps).</summary>
    public int Bitrate => _cacheEntry?.Bitrate ?? _bitrate;

    /// <summary>Container parser. Available for diagnostics.</summary>
    internal IContainerParser? Parser => _parser;

    /// <summary>Unique key of the disk cache entry.</summary>
    public string CacheKey => _cacheKey;

    /// <summary>ID of the track owning the source.</summary>
    internal string TrackId => _trackId;

    /// <summary>Stream container whose bytes are already present in RAM and disk cache.</summary>
    internal AudioFormat ContainerFormat => _format;

    /// <summary>Current stream URL (empty string until continuation URL is acquired).</summary>
    internal string CurrentUrl => _currentUrl;

    /// <summary>
    /// Indicates that the last CDN response was HTTP 403 and the current URL has not yet been replaced with a working one.
    /// </summary>
    internal bool HasPendingForbidden => Volatile.Read(ref _consecutive403Count) > 0;

    /// <summary>
    /// Creates an explicit URL request containing track, stream variant, and CDN 403 status of this source.
    /// </summary>
    /// <param name="kind">Target purpose of the request.</param>
    /// <returns>An immutable request that does not hold a reference to the source.</returns>
    internal StreamUrlRequest CreateUrlRequest(StreamUrlRequestKind kind)
    {
        long totalSize = _cacheEntry?.TotalSize ?? 0;

        return new StreamUrlRequest(
            kind,
            _trackId,
            _format,
            Bitrate,
            totalSize > 0 ? totalSize : _contentLength,
            _currentUrl,
            HasPendingForbidden);
    }

    /// <summary>
    /// Indicates whether the source has an associated URL provider.
    /// </summary>
    internal bool HasUrlProvider => _urlProvider != null;

    /// <summary>
    /// Requests a stream URL from the provider on behalf of this source. Does not apply the URL: caller makes the decision.
    /// </summary>
    /// <param name="kind">Target purpose of the request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>URL from the provider, or <c>null</c> if no provider exists or URL acquisition failed.</returns>
    internal ValueTask<string?> RequestUrlAsync(StreamUrlRequestKind kind, CancellationToken ct) =>
        _urlProvider?.Invoke(CreateUrlRequest(kind), ct) ?? ValueTask.FromResult<string?>(null);

    /// <summary>Number of contiguous bytes from the beginning of the file (contiguous prefix).</summary>
    public long ContiguousPrefixBytes => _cacheEntry?.GetContiguousDownloadedBytesFrom(0) ?? 0;

    /// <summary>
    /// Global event raised on non-fatal network issues encountered by the source.
    /// </summary>
    public static event Action<string, Exception>? OnSourceWarning;

    /// <summary>
    /// Data source detected persistent network loss and is awaiting recovery.
    /// Decoder is blocked, PCM buffer is depleting.
    /// </summary>
    public static event Action<string>? OnNetworkStalled;

    /// <summary>
    /// Network recovered after stall — data is flowing again.
    /// </summary>
    public static event Action<string>? OnNetworkRecovered;

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a source with range-based HTTP streaming.
    /// </summary>
    /// <param name="cacheKey">Unique cache key (trackId + format + normalized_bitrate).</param>
    /// <param name="trackId">Track identifier.</param>
    /// <param name="url">Initial stream URL.</param>
    /// <param name="contentLength">Total content length in bytes.</param>
    /// <param name="format">Audio container format.</param>
    /// <param name="codec">Audio codec.</param>
    /// <param name="bitrate">Bitrate in kbps.</param>
    /// <param name="cacheManager">Disk cache manager.</param>
    /// <param name="config">Streaming configuration.</param>
    /// <param name="urlProvider">URL provider for initial acquire (partial cache) and 403 refreshes.</param>
    /// <param name="httpClient">
    /// Optional HTTP client for deterministic testing; defaults to <c>SharedHttpClient.Instance</c>.
    /// </param>
    public CachingStreamSource(
        string cacheKey,
        string trackId,
        string url,
        long contentLength,
        AudioFormat format,
        AudioCodec codec,
        int bitrate,
        AudioCacheManager cacheManager,
        StreamingConfig config,
        StreamUrlProvider? urlProvider = null,
        HttpClient? httpClient = null)
    {
        _config = config;
        _cacheKey = cacheKey;
        _trackId = trackId;
        _currentUrl = url;
        _contentLength = contentLength;
        _format = format;
        _bitrate = bitrate;
        _cacheManager = cacheManager;
        _urlProvider = urlProvider;
        _httpClientOverride = httpClient;
        Codec = codec;

        _requestAlignmentBytes = Math.Max(4096, config.RequestAlignmentBytes);
        _downloadSlots = new SemaphoreSlim(config.MaxConcurrentDownloads);
    }

    #endregion

    #region Network Assessment

    private double GetAverageLatencyInternal()
    {
        if (_latency0 <= 0) return 0;
        if (_latency1 <= 0) return _latency0;
        if (_latency2 <= 0) return (_latency0 + _latency1) / 2.0;
        return (_latency0 + _latency1 + _latency2) / 3.0;
    }

    /// <summary>
    /// Evaluates network degradation across three independent signals:
    /// <list type="number">
    ///   <item>
    ///     <b>Absolute bandwidth</b> — comparison against profile thresholds
    ///     (<see cref="StreamingConfig.AbsoluteCriticalBandwidthBytesPerSec"/>,
    ///     <see cref="StreamingConfig.AbsoluteDegradedBandwidthBytesPerSec"/>).
    ///     Prevents false <c>Normal</c> states on low-bandwidth connections with low-bitrate audio,
    ///     where relative ratio might be high (10 Mbps + 128 kbps → ratio ≈ 78).
    ///   </item>
    ///   <item>
    ///     <b>Connection viability</b> — bandwidth must exceed audio bitrate
    ///     by at least <see cref="StreamingConfig.MinViableBandwidthMultiplier"/>×,
    ///     otherwise buffering is fundamentally impossible.
    ///   </item>
    ///   <item>
    ///     <b>Relative ratio and RTT</b> — classic evaluation based on
    ///     bandwidth/bitrate ratio and average latency.
    ///   </item>
    /// </list>
    /// Signals are checked in decreasing order of strictness: the first triggered signal
    /// dictates the resulting level.
    /// </summary>
    private NetworkDegradationLevel GetNetworkDegradationLevel()
    {
        double avgLatencyMs;
        double bw;

        lock (_latencyLock)
        {
            avgLatencyMs = GetAverageLatencyInternal();
            bw = _estimatedBandwidthBytesPerSec;
        }

        if (bw > 0)
        {
            if (bw < _config.AbsoluteCriticalBandwidthBytesPerSec)
                return NetworkDegradationLevel.Critical;

            if (bw < _config.AbsoluteDegradedBandwidthBytesPerSec)
                return NetworkDegradationLevel.Degraded;
        }

        double bitrateBps = Math.Max(1, _bitrate) * 1000.0 / 8.0;

        if (bw > 0 && bw < bitrateBps * _config.MinViableBandwidthMultiplier)
            return NetworkDegradationLevel.Critical;

        double widthRatio = bw > 0 ? bw / bitrateBps : double.PositiveInfinity;

        if (avgLatencyMs > 2500 || widthRatio < 3.0)
            return NetworkDegradationLevel.Critical;

        if (avgLatencyMs > 800 || widthRatio < 6.0)
            return NetworkDegradationLevel.Degraded;

        return NetworkDegradationLevel.Normal;
    }

    /// <summary>
    /// Returns the maximum allowed number of concurrent HTTP downloads
    /// based on current network condition.
    /// </summary>
    private int GetAdaptiveMaxConcurrentDownloads() => GetNetworkDegradationLevel() switch
    {
        NetworkDegradationLevel.Critical => 1,
        NetworkDegradationLevel.Degraded => Math.Min(2, _config.MaxConcurrentDownloads),
        _ => _config.MaxConcurrentDownloads
    };

    /// <summary>
    /// Returns an adaptive post-seek preload volume.
    /// Seek preload is reduced on constrained networks.
    /// </summary>
    private int GetAdaptiveSeekPreloadBytes()
    {
        int preload = _config.SeekPreloadBytes;
        return GetNetworkDegradationLevel() switch
        {
            NetworkDegradationLevel.Critical =>
                Math.Max(_requestAlignmentBytes * 2, Math.Min(preload, _requestAlignmentBytes * 4)),
            NetworkDegradationLevel.Degraded =>
                Math.Max(_requestAlignmentBytes * 2, Math.Min(preload, _requestAlignmentBytes * 8)),
            _ => preload
        };
    }

    /// <summary>
    /// Converts buffered audio byte count to playback duration in milliseconds.
    /// </summary>
    /// <param name="bytes">Audio stream byte count.</param>
    /// <returns>Equivalent duration in milliseconds.</returns>
    private int ConvertBufferedBytesToMs(long bytes)
    {
        if (bytes <= 0) return 0;
        double bitrateBps = Math.Max(1, _bitrate) * 1000.0 / 8.0;
        return (int)(bytes / bitrateBps * 1000.0);
    }

    /// <summary>
    /// Calculates adaptive target buffer size based on RTT and available bandwidth.
    /// High RTT increases the target, while low bandwidth caps unnecessary buffer bloat.
    /// </summary>
    private int GetAdaptiveTargetBufferMs()
    {
        double avgLatencyMs;
        double bw;

        lock (_latencyLock)
        {
            avgLatencyMs = GetAverageLatencyInternal();
            bw = _estimatedBandwidthBytesPerSec;
        }

        int target = _config.TargetBufferMs;

        if (avgLatencyMs > 3000) target = Math.Min(target * 4, 45_000);
        else if (avgLatencyMs > 1500) target = Math.Min(target * 3, 32_000);
        else if (avgLatencyMs > 800) target = Math.Min(target * 2, 24_000);
        else if (avgLatencyMs > 300) target = Math.Min((target * 3) / 2, 18_000);

        if (bw > 0)
        {
            double bitrateBps = Math.Max(1, _bitrate) * 1000.0 / 8.0;
            double widthRatio = bw / bitrateBps;

            if (widthRatio < 2.0) target = Math.Min(target, 8_000);
            else if (widthRatio < 3.0) target = Math.Min(target, 12_000);
            else if (widthRatio < 5.0) target = Math.Min(target, 16_000);
        }

        return Math.Max(target, 4_000);
    }

    /// <summary>
    /// Returns the minimum contiguous data prefix (in bytes)
    /// required for a safe decoder start after a seek.
    /// </summary>
    /// <param name="position">Target seek position in bytes.</param>
    /// <returns>The required number of contiguous bytes.</returns>
    private int GetMinimalSeekStartBytes(long position)
    {
        if (position >= _contentLength) return 0;

        var deg = GetNetworkDegradationLevel();

        int desiredMs = deg switch
        {
            NetworkDegradationLevel.Critical => 2500,
            NetworkDegradationLevel.Degraded => 3000,
            _ => 3000
        };

        int minClamp = deg switch
        {
            NetworkDegradationLevel.Critical => 32 * 1024,
            NetworkDegradationLevel.Degraded => 48 * 1024,
            _ => 32 * 1024
        };

        int maxClamp = deg switch
        {
            NetworkDegradationLevel.Critical => 96 * 1024,
            NetworkDegradationLevel.Degraded => 128 * 1024,
            _ => 128 * 1024
        };

        double bitrateBps = Math.Max(1, _bitrate) * 1000.0 / 8.0;
        int byTime = (int)Math.Ceiling(bitrateBps * desiredMs / 1000.0);
        int required = Math.Max(_requestAlignmentBytes * 2, AlignUp(byTime, _requestAlignmentBytes));
        required = Math.Clamp(required, minClamp, maxClamp);

        long remaining = _contentLength - position;
        return (int)Math.Min(required, remaining);
    }

    /// <summary>
    /// Checks whether enough locally available contiguous data exists
    /// for an immediate decoder start after a seek.
    /// </summary>
    /// <param name="position">Target seek position in bytes.</param>
    /// <returns><c>true</c> if the seek can start without waiting for the network.</returns>
    private bool HasMinimalLocalSeekStartData(long position)
    {
        int required = GetMinimalSeekStartBytes(position);
        if (required <= 0) return true;

        long ramBytes = _ramCache.GetContiguousBytesFrom(position);
        long diskBytes = _cacheEntry?.GetContiguousDownloadedBytesFrom(position) ?? 0;
        long available = Math.Max(ramBytes, diskBytes);

        return available >= required;
    }

    /// <summary>
    /// Calculates an adaptive critical-range wait timeout during seek.
    /// </summary>
    /// <param name="expectedBytes">Expected size of the critical range.</param>
    /// <returns>
    /// Timeout in milliseconds, accounting for RTT, throughput,
    /// and reconnect costs on high-latency networks.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The previous upper clamp of 5000ms was too aggressive for high-latency networks:
    /// the first useful contiguous startup prefix could arrive after 8–12 seconds,
    /// even though throughput itself remained sufficient for playback.
    /// </para>
    /// </remarks>
    private int ComputeAdaptiveSeekCriticalTimeoutMs(int expectedBytes)
    {
        double avgLatencyMs;
        double bw;

        lock (_latencyLock)
        {
            avgLatencyMs = GetAverageLatencyInternal();
            bw = _estimatedBandwidthBytesPerSec;
        }

        double bitrateBps = Math.Max(1, _bitrate) * 1000.0 / 8.0;
        double fallbackBps = Math.Max(16 * 1024, bitrateBps * 2.0);
        double effectiveBps = bw > 0 ? Math.Max(bw * 0.35, fallbackBps) : fallbackBps;

        double transferMs = expectedBytes / effectiveBps * 1000.0;

        double latencyBudgetMs = avgLatencyMs > 0
            ? Math.Max(800.0, avgLatencyMs * 3.0)
            : 1500.0;

        int timeoutMs = (int)Math.Ceiling(latencyBudgetMs + transferMs + 1000.0);

        return Math.Clamp(timeoutMs, 2500, 15000);
    }

    #endregion

    #region Public Seek Helpers

    /// <summary>
    /// Checks whether enough contiguous data is available for a safe decoder start
    /// after a previously executed seek (for AudioPlayer polling).
    /// </summary>
    /// <param name="positionMs">Position in milliseconds to which the seek was performed.</param>
    /// <returns><c>true</c> if the minimum startup prefix is available.</returns>
    public bool IsSeekDataReady(long positionMs)
    {
        if (_parser == null || !_initialized) return false;

        var seekInfo = _parser.FindSeekPosition(positionMs);
        if (seekInfo == null) return false;

        long targetBytePos = Math.Min(seekInfo.Value.BytePosition, Math.Max(0, _contentLength - 1));
        return HasMinimalLocalSeekStartData(targetBytePos);
    }

    #endregion

    #region Reading

    /// <inheritdoc/>
    /// <remarks>
    /// <para><b>Self-Healing:</b> When the parser detects corruption, targeted range
    /// recovery is triggered without interrupting playback.</para>
    /// </remarks>
    public ValueTask<AudioFrame?> ReadFrameAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var parser = _parser;
        if (!_initialized || parser == null)
            throw new InvalidOperationException("Source not initialized");

        if (ct.IsCancellationRequested || _seekInProgress)
            return new ValueTask<AudioFrame?>((AudioFrame?)null);

        try
        {
            var vt = parser.ReadNextFrameAsync(ct);
            if (vt.IsCompletedSuccessfully)
            {
                var frame = vt.Result;
                if (frame != null)
                    OnFrameRead(frame.Value);
                return new ValueTask<AudioFrame?>(frame);
            }

            return ReadFrameSlowAsync(vt, ct);
        }
        // null is allowed only on caller token cancellation; otherwise AudioPipeline treats it as EOF.
        // Read epoch cancellation is propagated and handled in AudioPipeline as a transient retry.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ValueTask<AudioFrame?>((AudioFrame?)null);
        }
        catch (ParserCorruptionException ex)
        {
            return HealAndReadFrameSlowAsync(ex.AbsoluteBytePosition, ct);
        }
    }

    /// <summary>
    /// Captures the timestamp and stream offset after successfully reading a frame.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void OnFrameRead(in AudioFrame frame)
    {
        Volatile.Write(ref _positionMs, frame.TimestampMs);
        if (_readStream != null)
            Volatile.Write(ref _currentReadOffset, _readStream.Position);
    }

    /// <summary>
    /// Asynchronous frame reading path when waiting for data prefetching from disk or network.
    /// </summary>
    private async ValueTask<AudioFrame?> ReadFrameSlowAsync(
        ValueTask<AudioFrame?> pendingRead,
        CancellationToken ct)
    {
        try
        {
            var frame = await pendingRead.ConfigureAwait(false);
            if (frame != null)
                OnFrameRead(frame.Value);
            return frame;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (ParserCorruptionException ex)
        {
            return await HealAndReadFrameSlowAsync(ex.AbsoluteBytePosition, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Slow container recovery path after a parsing error, with read retries.
    /// </summary>
    private async ValueTask<AudioFrame?> HealAndReadFrameSlowAsync(
        long initialCorruptBytePos,
        CancellationToken ct)
    {
        const int maxHealingAttempts = 3;
        long corruptPos = initialCorruptBytePos;

        for (int attempt = 0; attempt < maxHealingAttempts; attempt++)
        {
            if (ct.IsCancellationRequested || _seekInProgress)
                return null;

            await HealCorruptionAsync(corruptPos, ct).ConfigureAwait(false);

            if (ct.IsCancellationRequested || _seekInProgress)
                return null;

            try
            {
                var frame = await _parser!.ReadNextFrameAsync(ct).ConfigureAwait(false);
                if (frame != null)
                    OnFrameRead(frame.Value);
                return frame;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch (ParserCorruptionException nextEx)
            {
                corruptPos = nextEx.AbsoluteBytePosition;
            }
        }

        if (ct.IsCancellationRequested || _seekInProgress)
            return null;

        throw new InvalidDataException("Unrecoverable container corruption after max healing attempts.");
    }

    /// <summary>
    /// Self-healing: invalidates the damaged range and re-fetches it from the network.
    /// If no network is available, marks the range as dead and performs a resync.
    /// </summary>
    private async Task HealCorruptionAsync(long absoluteBytePosition, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        long rangeStart = AlignDown(absoluteBytePosition, _requestAlignmentBytes);
        int rangeLength = GetAlignedReadLength(rangeStart, _config.MinRequestSizeBytes);
        if (rangeLength <= 0) return;

        Log.Warn($"[SelfHealing] Corruption detected at byte {absoluteBytePosition} " +
                 $"(Range {rangeStart}-{rangeStart + rangeLength - 1})");

        if (_cacheEntry == null || _readStream == null || _parser == null) return;

        _cacheManager.InvalidateRange(_cacheKey, rangeStart, rangeLength);

        if (_ramCache.TryRemoveContaining(absoluteBytePosition, out var badBlock) && badBlock is not null)
            badBlock.Dispose();

        var healResult = await EnsureRangeAsync(rangeStart, rangeLength, ct, isCritical: true)
            .ConfigureAwait(false);

        if (healResult == RangeDownloadResult.Success)
        {
            Log.Info($"[SelfHealing] Range {rangeStart}-{rangeStart + rangeLength - 1} healed from network.");
            _readStream.SeekAndCancelPendingReads(rangeStart);
            Volatile.Write(ref _currentReadOffset, rangeStart);
            _parser.RequireResync();
            return;
        }

        if (healResult == RangeDownloadResult.Cancelled && ct.IsCancellationRequested)
            ct.ThrowIfCancellationRequested();

        _cacheEntry.MarkRangeCorruptedOffline(rangeStart);
        long nextBoundary = Math.Min(rangeStart + rangeLength, _contentLength);

        Log.Warn($"[SelfHealing] Network unavailable. Marking range as dead for this session.");

        _readStream.SeekAndCancelPendingReads(nextBoundary);
        Volatile.Write(ref _currentReadOffset, nextBoundary);

        if (nextBoundary < _contentLength)
            _parser.RequireResync();
    }

    #endregion

    #region Public Buffer Management

    /// <inheritdoc/>
    public void ReleaseRamBuffers()
    {
        long currentOffset = Volatile.Read(ref _currentReadOffset);
        _ramCache.Trim(currentOffset, _config.RamEvictionWindowBytes, _config.MaxRamBytes);
    }

    /// <inheritdoc/>
    public void CancelPendingOperations() => _lifetimeCts?.Cancel();

    /// <inheritdoc/>
    public void SetPlaybackActive(bool active)
    {
        if (_disposed) return;
        if (_playbackGate.IsSet == active) return;

        if (active) _playbackGate.Set();
        else _playbackGate.Reset();

        Log.Debug($"[CachingSource] Playback active state updated: {active}");
    }

    /// <summary>
    /// Attempts to attach a continuation URL received out-of-band
    /// to an already running source.
    /// </summary>
    /// <param name="url">Ready final stream URL.</param>
    /// <returns>
    /// <c>true</c> if the URL was accepted;
    /// <c>false</c> if the source already had a URL, was disposed, or the input URL is invalid.
    /// </returns>
    internal bool TryAttachContinuationUrl(string url)
    {
        if (_disposed) return false;
        if (string.IsNullOrWhiteSpace(url)) return false;

        TaskCompletionSource<string?>? pendingTcs;

        lock (_continuationLock)
        {
            if (!string.IsNullOrWhiteSpace(_currentUrl))
                return false;

            if (!IsUrlVariantCompatible(url, out string? rejectReason))
            {
                Log.Warn($"[CachingSource] [{_trackId}] External continuation URL rejected: variant mismatch ({rejectReason})");
                return false;
            }

            _currentUrl = url;
            pendingTcs = _continuationUrlTcs;
            _continuationUrlTcs = null;
        }

        _cacheEntry?.OriginalUrl = url;

        pendingTcs?.TrySetResult(url);

        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "?";
        Log.Info($"[CachingSource] Continuation URL attached externally: track={_trackId}, host={host}");
        return true;
    }

    /// <summary>
    /// Atomically updates the stream URL after a successful refresh.
    /// Resets the consecutive 403 counter and updates the URL in the cache entry.
    /// A URL from a different variant (itag/clen) is rejected without state mutation.
    /// </summary>
    /// <param name="newUrl">New valid stream URL.</param>
    /// <returns><c>true</c> if the URL was applied; <c>false</c> on dispose, empty URL, or mismatched variant.</returns>
    internal bool UpdateUrl(string newUrl)
    {
        if (_disposed || string.IsNullOrWhiteSpace(newUrl))
            return false;

        if (!IsUrlVariantCompatible(newUrl, out string? rejectReason))
        {
            Log.Warn($"[CachingSource] [{_trackId}] UpdateUrl rejected: variant mismatch ({rejectReason})");
            return false;
        }

        _currentUrl = newUrl;
        Volatile.Write(ref _consecutive403Count, 0);

        _cacheEntry?.OriginalUrl = newUrl;

        string host = Uri.TryCreate(newUrl, UriKind.Absolute, out var uri) ? uri.Host : "?";
        Log.Warn($"[CachingSource] URL updated after refresh: track={_trackId}, host={host}, consecutive403Reset=true");
        return true;
    }

    #endregion

    #region Warning Events

    /// <summary>
    /// Publishes a non-fatal source warning to the external UI orchestrator.
    /// </summary>
    /// <param name="exception">The cause of the warning.</param>
    private void PublishSourceWarning(Exception exception)
    {
        var handler = OnSourceWarning;
        if (handler is null) return;

        try
        {
            handler(_trackId, exception);
        }
        catch (Exception ex)
        {
            Log.Warn($"[CachingSource] Source warning callback failed: {ex.Message}");
        }
    }

    #endregion
}