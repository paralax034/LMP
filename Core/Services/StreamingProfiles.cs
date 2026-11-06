namespace LMP.Core.Services;

/// <summary>
/// Streaming profile factory for various network conditions.
/// </summary>
public static class StreamingProfiles
{
    /// <summary>
    /// Gets the streaming configuration for the specified profile.
    /// </summary>
    public static StreamingConfig GetConfig(InternetProfile profile) => profile switch
    {
        InternetProfile.Low => Low,
        InternetProfile.Medium => Medium,
        InternetProfile.High => High,
        InternetProfile.Ultra => Ultra,
        _ => Medium
    };

    /// <summary>
    /// Bandwidth saving and high responsiveness on weak and unstable connections.
    /// <para>
    /// Initial fetch is minimal (24 KB) — sufficient for WebM/MP4 headers.
    /// Startup prefetch (48 KB) covers warmup latency on a narrow channel.
    /// Absolute bandwidth thresholds are lowered to avoid prematurely forcing a single stream.
    /// </para>
    /// </summary>
    public static StreamingConfig Low { get; } = new()
    {
        RequestAlignmentBytes = 8 * 1024,
        MinRequestSizeBytes = 8 * 1024,
        MaxRequestSizeBytes = 96 * 1024,

        MaxRamBytes = 4 * 1024 * 1024,
        RamEvictionWindowBytes = 768 * 1024,

        MaxConcurrentDownloads = 2,
        DownloadSlotTimeoutMs = 800,

        MaxNetworkRetries = 4,
        NetworkRetryBaseDelayMs = 800,
        UseExponentialBackoff = true,
        Max403BeforeCircuitBreak = 3,
        RefreshCooldownMs = 5000,
        PostRefreshDelayMs = 800,

        TargetBufferMs = 10_000,
        InitialPrebufferBytes = 24 * 1024,
        StartupPrefetchBytes = 48 * 1024,
        SeekPreloadBytes = 96 * 1024,
        MinBufferAheadForBackgroundFillMs = 3000,

        BackgroundFillIdleCycles = 8,
        BackgroundFillIntervalMs = 5000,
        MaxBackgroundRequestsPerSession = 60,

        PreloadIntervalMs = 800,
        ThrottleMultiplier = 2.0,

        ThroughputSafetyFactor = 0.85,
        AbsoluteCriticalBandwidthBytesPerSec = 64 * 1024,
        AbsoluteDegradedBandwidthBytesPerSec = 256 * 1024,
        BdpFloorMaxBufferMs = 3_000
    };

    /// <summary>
    /// Balanced profile for a typical home connection (5–30 Mbps).
    /// <para>
    /// Initial fetch (48 KB) — 3 alignment blocks, sufficient for all containers.
    /// Startup prefetch (128 KB) covers ~5–7 seconds of audio, which satisfies
    /// the warmup threshold without waiting for the preload loop.
    /// </para>
    /// </summary>
    public static StreamingConfig Medium { get; } = new()
    {
        RequestAlignmentBytes = 16 * 1024,
        MinRequestSizeBytes = 16 * 1024,
        MaxRequestSizeBytes = 384 * 1024,

        MaxRamBytes = 6 * 1024 * 1024,
        RamEvictionWindowBytes = 1536 * 1024,

        MaxConcurrentDownloads = 3,
        DownloadSlotTimeoutMs = 300,

        MaxNetworkRetries = 3,
        NetworkRetryBaseDelayMs = 500,
        UseExponentialBackoff = true,
        Max403BeforeCircuitBreak = 3,
        RefreshCooldownMs = 3000,
        PostRefreshDelayMs = 500,

        TargetBufferMs = 12_000,
        InitialPrebufferBytes = 48 * 1024,
        StartupPrefetchBytes = 128 * 1024,
        SeekPreloadBytes = 256 * 1024,
        MinBufferAheadForBackgroundFillMs = 4000,

        BackgroundFillIdleCycles = 5,
        BackgroundFillIntervalMs = 3000,
        MaxBackgroundRequestsPerSession = 0,

        PreloadIntervalMs = 500,
        ThrottleMultiplier = 3.0
    };

    /// <summary>
    /// Profile for fast and stable networks (50+ Mbps).
    /// </summary>
    public static StreamingConfig High { get; } = new()
    {
        RequestAlignmentBytes = 32 * 1024,
        MinRequestSizeBytes = 32 * 1024,
        MaxRequestSizeBytes = 512 * 1024,

        MaxRamBytes = 8 * 1024 * 1024,
        RamEvictionWindowBytes = 2 * 1024 * 1024,

        MaxConcurrentDownloads = 4,
        DownloadSlotTimeoutMs = 300,

        MaxNetworkRetries = 2,
        NetworkRetryBaseDelayMs = 400,
        UseExponentialBackoff = true,
        Max403BeforeCircuitBreak = 3,
        RefreshCooldownMs = 2000,
        PostRefreshDelayMs = 300,

        TargetBufferMs = 15_000,
        InitialPrebufferBytes = 96 * 1024,
        StartupPrefetchBytes = 256 * 1024,
        SeekPreloadBytes = 384 * 1024,
        MinBufferAheadForBackgroundFillMs = 5000,

        BackgroundFillIdleCycles = 3,
        BackgroundFillIntervalMs = 1500,
        MaxBackgroundRequestsPerSession = 0,

        PreloadIntervalMs = 350,
        ThrottleMultiplier = 0
    };

    /// <summary>
    /// Profile for high-speed connections (100+ Mbps).
    /// </summary>
    public static StreamingConfig Ultra { get; } = new()
    {
        RequestAlignmentBytes = 64 * 1024,
        MinRequestSizeBytes = 64 * 1024,
        MaxRequestSizeBytes = 1024 * 1024,

        MaxRamBytes = 12 * 1024 * 1024,
        RamEvictionWindowBytes = 3 * 1024 * 1024,

        MaxConcurrentDownloads = 4,
        DownloadSlotTimeoutMs = 300,

        MaxNetworkRetries = 2,
        NetworkRetryBaseDelayMs = 400,
        UseExponentialBackoff = true,
        Max403BeforeCircuitBreak = 3,
        RefreshCooldownMs = 2000,
        PostRefreshDelayMs = 300,

        TargetBufferMs = 18_000,
        InitialPrebufferBytes = 128 * 1024,
        StartupPrefetchBytes = 384 * 1024,
        SeekPreloadBytes = 768 * 1024,
        MinBufferAheadForBackgroundFillMs = 6000,

        BackgroundFillIdleCycles = 3,
        BackgroundFillIntervalMs = 1500,
        MaxBackgroundRequestsPerSession = 0,

        PreloadIntervalMs = 300,
        ThrottleMultiplier = 0
    };
}