namespace LMP.Core.Models;

/// <summary>
/// Audio streaming and buffering configuration for range-based transport.
/// </summary>
public sealed record StreamingConfig
{
    #region Range Request Settings

    /// <summary>
    /// Alignment of network requests and local cache in bytes.
    /// </summary>
    public int RequestAlignmentBytes { get; init; } = Defaults.RequestAlignmentBytes;

    /// <summary>
    /// Minimum size of a single network range request in bytes.
    /// </summary>
    public int MinRequestSizeBytes { get; init; } = Defaults.MinRequestSizeBytes;

    /// <summary>
    /// Maximum size of a single network range request in bytes.
    /// </summary>
    public int MaxRequestSizeBytes { get; init; } = Defaults.MaxRequestSizeBytes;

    #endregion

    #region RAM Cache

    /// <summary>
    /// Maximum RAM cache size in bytes.
    /// </summary>
    public int MaxRamBytes { get; init; } = Defaults.MaxRamBytes;

    /// <summary>
    /// RAM cache retention window around the current position in bytes.
    /// </summary>
    public int RamEvictionWindowBytes { get; init; } = Defaults.RamEvictionWindowBytes;

    #endregion

    #region Download Settings

    /// <summary>Maximum number of parallel range downloads.</summary>
    public int MaxConcurrentDownloads { get; init; } = Defaults.MaxConcurrentDownloads;

    /// <summary>Timeout for waiting for a download slot (ms). 0 = no wait.</summary>
    public int DownloadSlotTimeoutMs { get; init; } = Defaults.DownloadSlotTimeoutMs;

    #endregion

    #region Retry / Resilience

    /// <summary>Maximum retry count on network errors.</summary>
    public int MaxNetworkRetries { get; init; } = Defaults.MaxNetworkRetries;

    /// <summary>Base delay between retries (ms).</summary>
    public int NetworkRetryBaseDelayMs { get; init; } = Defaults.NetworkRetryBaseDelayMs;

    /// <summary>Use exponential backoff on retries.</summary>
    public bool UseExponentialBackoff { get; init; } = Defaults.UseExponentialBackoff;

    /// <summary>Maximum consecutive HTTP 403 responses before triggering the circuit breaker.</summary>
    public int Max403BeforeCircuitBreak { get; init; } = Defaults.Max403BeforeCircuitBreak;

    /// <summary>Minimum interval between URL refresh requests (ms).</summary>
    public int RefreshCooldownMs { get; init; } = Defaults.RefreshCooldownMs;

    /// <summary>Delay after URL refresh before retrying download (ms).</summary>
    public int PostRefreshDelayMs { get; init; } = Defaults.PostRefreshDelayMs;

    #endregion

    #region Buffer Targets

    /// <summary>
    /// Target lookahead buffer size from the current position in milliseconds.
    /// Used by the MAPO scheduler.
    /// </summary>
    public int TargetBufferMs { get; init; } = Defaults.TargetBufferMs;

    /// <summary>
    /// Initial data volume in bytes preferred before starting parsing/decoding.
    /// </summary>
    public int InitialPrebufferBytes { get; init; } = Defaults.InitialPrebufferBytes;

    /// <summary>
    /// Data volume in bytes for best-effort prefetching around the seek position.
    /// </summary>
    public int SeekPreloadBytes { get; init; } = Defaults.SeekPreloadBytes;

    /// <summary>
    /// Minimum lookahead buffer volume in milliseconds before launching background backfill.
    /// </summary>
    public int MinBufferAheadForBackgroundFillMs { get; init; } = Defaults.MinBufferAheadForBackgroundFillMs;

    #endregion

    #region Background Fill

    /// <summary>Idle cycles before background backfill.</summary>
    public int BackgroundFillIdleCycles { get; init; } = Defaults.BackgroundFillIdleCycles;

    /// <summary>Pause between background range downloads (ms).</summary>
    public int BackgroundFillIntervalMs { get; init; } = Defaults.BackgroundFillIntervalMs;

    /// <summary>Maximum background range requests per session (0 = unlimited).</summary>
    public int MaxBackgroundRequestsPerSession { get; init; } = Defaults.MaxBackgroundRequestsPerSession;

    #endregion

    #region Preload Loop

    /// <summary>Preload loop interval (ms).</summary>
    public int PreloadIntervalMs { get; init; } = Defaults.PreloadIntervalMs;

    #endregion

    #region Throttling

    /// <summary>
    /// Download speed multiplier relative to the audio bitrate.
    /// 0 = unlimited. 3.0 = download no faster than 3× bitrate.
    /// Prevents burst downloading, which causes YouTube to throttle the connection.
    /// </summary>
    public double ThrottleMultiplier { get; init; } = Defaults.ThrottleMultiplier;

    #endregion

    #region Network Assessment

    /// <summary>
    /// Safety factor for throughput-based request sizing (0.0–1.0).
    /// <para>
    /// Based on the THROUGHPUT model (dash.js): requests no more than this fraction of estimated
    /// throughput. A value of 0.9 means using 90% of the EMA estimate,
    /// leaving a 10% margin to avoid hitting the channel ceiling and competing
    /// for bandwidth with parallel requests.
    /// </para>
    /// </summary>
    public double ThroughputSafetyFactor { get; init; } = Defaults.ThroughputSafetyFactor;

    /// <summary>
    /// Absolute lower bandwidth threshold (bytes/sec) below which the network is considered
    /// critically degraded regardless of the relative ratio to the bitrate.
    /// <para>
    /// Solves the issue: on a narrow channel (10 Mbps) with low-bitrate audio (128 kbps),
    /// relative ratio ≈ 78 — the system considers the network "normal" despite the channel being strictly limited.
    /// </para>
    /// </summary>
    public int AbsoluteCriticalBandwidthBytesPerSec { get; init; } = Defaults.AbsoluteCriticalBandwidthBytesPerSec;

    /// <summary>
    /// Absolute lower bandwidth threshold (bytes/sec) below which the network is considered
    /// degraded regardless of the relative ratio to the bitrate.
    /// </summary>
    public int AbsoluteDegradedBandwidthBytesPerSec { get; init; } = Defaults.AbsoluteDegradedBandwidthBytesPerSec;

    /// <summary>
    /// Minimum viable ratio of bandwidth to audio bitrate.
    /// If <c>bandwidth &lt; bitrate × multiplier</c>, the channel is considered critical
    /// (does not provide even a minimal margin over streaming).
    /// </summary>
    public double MinViableBandwidthMultiplier { get; init; } = Defaults.MinViableBandwidthMultiplier;

    /// <summary>
    /// Maximum lookahead buffer level (ms) at which the BDP floor is activated.
    /// <para>
    /// Based on the BBA principle (Netflix/dash.js): BDP floor is only needed when the buffer is empty
    /// (startup/seek phase). In steady state (buffer above this threshold), throughput estimation
    /// is not required — demand-based sizing is sufficient.
    /// </para>
    /// </summary>
    public int BdpFloorMaxBufferMs { get; init; } = Defaults.BdpFloorMaxBufferMs;

    /// <summary>
    /// Number of initial bandwidth measurements to which an increased bootstrap weight is applied.
    /// <para>
    /// Resolves the slow EMA convergence issue at startup: the first N measurements
    /// use <see cref="BandwidthBootstrapWeight"/> instead of size-based weight,
    /// allowing the system to quickly converge on a realistic channel estimate.
    /// </para>
    /// </summary>
    public int BandwidthBootstrapSampleCount { get; init; } = Defaults.BandwidthBootstrapSampleCount;

    /// <summary>
    /// EMA weight for bootstrap measurements (first <see cref="BandwidthBootstrapSampleCount"/> samples).
    /// Must be higher than the steady-state weight for fast initial convergence.
    /// </summary>
    public double BandwidthBootstrapWeight { get; init; } = Defaults.BandwidthBootstrapWeight;

    #endregion

    #region Startup Pipeline

    /// <summary>
    /// Data volume (bytes) for immediate background prefetch after parser initialization.
    /// <para>
    /// Executed in parallel with decoder creation and the warmup check,
    /// so that by the time playback start decision is made, the buffer is already
    /// partially populated. Covers the delay of <see cref="PreloadIntervalMs"/>
    /// of the first preload loop iteration.
    /// </para>
    /// <para>
    /// Recommended value: sufficient to cover the warmup threshold
    /// <c>(TargetBufferMs / 2)</c> seconds of audio at the target bitrate.
    /// </para>
    /// </summary>
    public int StartupPrefetchBytes { get; init; } = Defaults.StartupPrefetchBytes;

    #endregion

    /// <summary>
    /// Default values.
    /// </summary>
    public static class Defaults
    {
        public const int RequestAlignmentBytes = 16 * 1024;
        public const int MinRequestSizeBytes = 16 * 1024;
        public const int MaxRequestSizeBytes = 256 * 1024;

        public const int MaxRamBytes = 6 * 1024 * 1024;
        public const int RamEvictionWindowBytes = 1536 * 1024;

        public const int MaxConcurrentDownloads = 3;
        public const int DownloadSlotTimeoutMs = 300;

        public const int MaxNetworkRetries = 3;
        public const int NetworkRetryBaseDelayMs = 500;
        public const bool UseExponentialBackoff = true;
        public const int Max403BeforeCircuitBreak = 3;
        public const int RefreshCooldownMs = 3000;
        public const int PostRefreshDelayMs = 500;

        public const int TargetBufferMs = 12_000;
        public const int InitialPrebufferBytes = 96 * 1024;
        public const int SeekPreloadBytes = 192 * 1024;
        public const int MinBufferAheadForBackgroundFillMs = 4000;

        public const int BackgroundFillIdleCycles = 5;
        public const int BackgroundFillIntervalMs = 3000;
        public const int MaxBackgroundRequestsPerSession = 0;

        public const int PreloadIntervalMs = 500;

        public const double ThrottleMultiplier = 3.0;

        // Network Assessment

        /// <summary>
        /// 90% of estimated throughput (THROUGHPUT model, dash.js).
        /// Leaves a 10% safety margin to avoid hitting the channel ceiling.
        /// </summary>
        public const double ThroughputSafetyFactor = 0.90;

        /// <summary>
        /// 128 KB/s ≈ 1 Mbps — absolute minimum for stable audio streaming.
        /// Below this threshold, any relative ratio will be misleading.
        /// </summary>
        public const int AbsoluteCriticalBandwidthBytesPerSec = 128 * 1024;

        /// <summary>
        /// 512 KB/s ≈ 4 Mbps — lower bound for comfortable buffered streaming.
        /// </summary>
        public const int AbsoluteDegradedBandwidthBytesPerSec = 512 * 1024;

        /// <summary>
        /// Channel speed must be at least 1.5× the audio bitrate; otherwise,
        /// playback without buffering is impossible.
        /// </summary>
        public const double MinViableBandwidthMultiplier = 1.5;

        /// <summary>
        /// The BDP floor is active only while the buffer is less than 4 seconds (startup/seek phase).
        /// In steady state, demand-based sizing without BDP correction is sufficient.
        /// </summary>
        public const int BdpFloorMaxBufferMs = 4_000;

        /// <summary>
        /// The first 3 samples use an increased bootstrap weight for fast
        /// initial EMA convergence.
        /// </summary>
        public const int BandwidthBootstrapSampleCount = 3;

        /// <summary>
        /// 30% weight for bootstrap samples — 6× higher than the minimum steady-state weight (5%),
        /// ensuring fast convergence to the actual channel speed.
        /// </summary>
        public const double BandwidthBootstrapWeight = 0.30;

        /// <summary>
        /// 128 KB covers ~5–7 seconds of audio at 128–192 kbps,
        /// which is sufficient for the warmup threshold on the Medium profile.
        /// </summary>
        public const int StartupPrefetchBytes = 128 * 1024;
    }
}