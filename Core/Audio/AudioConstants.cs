namespace LMP.Core.Audio;

/// <summary>
/// Centralized audio system constants.
/// The single source of truth for all numeric parameters.
/// </summary>
public static class AudioConstants
{
    // ═══════════════════════════════════════════════════════
    // SOURCE STATE — Common values for audio sources
    // ═══════════════════════════════════════════════════════

    /// <summary>Unknown track duration in milliseconds.</summary>
    public const long UnknownDurationMs = -1;

    /// <summary>Unknown sample rate from container.</summary>
    public const int UnknownSampleRate = 0;

    /// <summary>Unknown channel count from container.</summary>
    public const int UnknownChannels = 0;

    /// <summary>Complete buffering progress in percent.</summary>
    public const double FullBufferProgressPercent = 100.0;

    /// <summary>Start of full buffering range.</summary>
    public const double BufferedRangeStart = 0.0;

    /// <summary>End of full buffering range.</summary>
    public const double BufferedRangeEnd = 1.0;

    /// <summary>
    /// Pre-allocated single-element range list representing a 100% buffered stream [0.0, 1.0].
    /// Eliminates per-tick array allocations in 500ms buffer state timers.
    /// </summary>
    public static readonly IReadOnlyList<(double Start, double End)> FullBufferedRanges =
        [(BufferedRangeStart, BufferedRangeEnd)];

    // ═══════════════════════════════════════════════════════
    // CHUNK SETTINGS — Data segmentation management
    // ═══════════════════════════════════════════════════════

    /// <summary>Chunk size for caching (64KB = optimal for HTTP Range + minimal allocations).</summary>
    public const int ChunkSize = 64 * 1024;

    // ═══════════════════════════════════════════════════════
    // PRELOAD SETTINGS — Preload strategy
    // ═══════════════════════════════════════════════════════

    /// <summary>Preload loop check interval (ms).</summary>
    public const int PreloadIntervalMs = 500;

    /// <summary>Maximum parallel chunk downloads (speed/RAM balance).</summary>
    public const int MaxConcurrentDownloads = 3;

    // ═══════════════════════════════════════════════════════
    // DOWNLOAD TIMEOUTS — HTTP operations
    // ═══════════════════════════════════════════════════════

    /// <summary>Download slot wait timeout (ms).</summary>
    public const int DownloadSlotTimeoutMs = 300;

    // ═══════════════════════════════════════════════════════
    // BACKGROUND DOWNLOAD LIMITS — Network saving
    // ═══════════════════════════════════════════════════════

    /// <summary>Idle cycles before background download (5 × 1s = 5s idle).</summary>
    public const int BackgroundFillIdleCycles = 5;

    /// <summary>Pause between background downloads (5s = gentle network usage).</summary>
    public const int BackgroundFillIntervalMs = 5_000;

    // ═══════════════════════════════════════════════════════
    // DECODER SETTINGS — Decoding parameters
    // ═══════════════════════════════════════════════════════

    /// <summary>Default sample rate (48kHz = industry standard).</summary>
    public const int DefaultSampleRate = 48_000;

    /// <summary>Default channel count (stereo).</summary>
    public const int DefaultChannels = 2;

    /// <summary>Decoding buffer (samples per channel, not bytes).</summary>
    public const int DecoderBufferFrames = 8192;

    /// <summary>Frames to skip after seek for Opus (pre-skip compensation).</summary>
    public const int SkipFramesAfterSeekOpus = 2;

    /// <summary>Frames to skip after seek for AAC (encoder delay).</summary>
    public const int SkipFramesAfterSeekAac = 5;

    /// <summary>Decoder graceful shutdown timeout (ms).</summary>
    public const int DecoderStopTimeoutMs = 500;

    /// <summary>
    /// Decoder stop timeout during seek (ms).
    /// </summary>
    /// <remarks>
    /// <para>An increased timeout ensures that the old decoder fully stops operation 
    /// and releases the port BEFORE starting the new decoder, preventing thread leaks and SocketException.</para>
    /// </remarks>
    public const int DecoderStopTimeoutSeekMs = 600;

    // ═══════════════════════════════════════════════════════
    // PLAYBACK BUFFER SETTINGS — PCM ring buffer
    // ═══════════════════════════════════════════════════════

    /// <summary>Maximum allowed player volume gain (Gain).</summary>
    public const float MaxVolumeGain = 4.0f;

    /// <summary>PCM buffer size in seconds (2s = smooth playback).</summary>
    public const int BufferSizeSeconds = 2;

    // ═══════════════════════════════════════════════════════
    // POSITION REPORTING — UI updates
    // ═══════════════════════════════════════════════════════

    /// <summary>Default position update interval (ms).</summary>
    public const int DefaultPositionUpdateIntervalMs = 250;

    /// <summary>Buffer state check interval (ms).</summary>
    public const int BufferStateUpdateIntervalMs = 500;

    // ═══════════════════════════════════════════════════════
    // RETRY POLICY — Error handling
    // ═══════════════════════════════════════════════════════

    /// <summary>Maximum operation retry attempts.</summary>
    public const int MaxRetryAttempts = 3;

    /// <summary>Delay between attempts (ms).</summary>
    public const int RetryDelayMs = 1_000;

    // ═══════════════════════════════════════════════════════
    // CACHE MANAGEMENT — Disk cache
    // ═══════════════════════════════════════════════════════

    /// <summary>Cache metadata file name.</summary>
    public const string CacheMetadataFileName = "cache_index.json";

    /// <summary>Audio cache file extension.</summary>
    public const string CacheFileExtension = ".audio";

    /// <summary>Cache index auto-save interval (ms).</summary>
    public const int CacheAutoSaveIntervalMs = 30_000;

    /// <summary>Lock timeout for index saving (ms).</summary>
    public const int CacheSaveLockTimeoutMs = 100;

    /// <summary>Buffer size for file operations (64KB).</summary>
    public const int CacheFileBufferSize = 65_536;

    /// <summary>Number of bytes per kilobyte.</summary>
    public const int BytesPerKilobyte = 1024;

    // ═══════════════════════════════════════════════════════
    // BITRATE NORMALIZATION THRESHOLDS
    // ═══════════════════════════════════════════════════════

    public const int BitrateThresholdLow = 50;
    public const int BitrateNormLow = 48;

    public const int BitrateThresholdMedLow = 62;
    public const int BitrateNormMedLow = 56;

    public const int BitrateThresholdMed = 80;
    public const int BitrateNormMed = 72;

    public const int BitrateThresholdStandardAac = 110;
    public const int BitrateNormStandardAac = 96;

    public const int BitrateThresholdStandardOpus = 140;
    public const int BitrateNormStandardOpus = 128;

    public const int BitrateThresholdHigh = 180;
    public const int BitrateNormHigh = 160;

    public const int BitrateThresholdVeryHigh = 260;
    public const int BitrateNormVeryHigh = 256;

    public const int BitrateNormMax = 320;

    /// <summary>
    /// Normalizes bitrate for cache keys.
    /// Groups close bitrates into standard buckets.
    /// </summary>
    public static int NormalizeBitrate(int bitrate) => bitrate switch
    {
        <= 0 => 0,
        < BitrateThresholdLow => BitrateNormLow,
        < BitrateThresholdMedLow => BitrateNormMedLow,
        < BitrateThresholdMed => BitrateNormMed,
        < BitrateThresholdStandardAac => BitrateNormStandardAac,
        < BitrateThresholdStandardOpus => BitrateNormStandardOpus,
        < BitrateThresholdHigh => BitrateNormHigh,
        < BitrateThresholdVeryHigh => BitrateNormVeryHigh,
        _ => BitrateNormMax
    };

    // ═══════════════════════════════════════════════════════
    // FORMAT DETECTION — Magic bytes for formats
    // ═══════════════════════════════════════════════════════

    /// <summary>Header size for format detection (in bytes).</summary>
    public const int FormatDetectionHeaderSize = 12;

    /// <summary>WebM: EBML header magic bytes.</summary>
    public static ReadOnlySpan<byte> WebMMagic => [0x1A, 0x45, 0xDF, 0xA3];

    /// <summary>MP4: 'ftyp' box identifier at offset 4.</summary>
    public static ReadOnlySpan<byte> Mp4FtypMagic => "ftyp"u8;

    /// <summary>Ogg: 'OggS' page header magic.</summary>
    public static ReadOnlySpan<byte> OggMagic => "OggS"u8;

    // ═══════════════════════════════════════════════════════
    // AAC DECODER TABLES — Single source of truth
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Complete table of sample rates by index from AAC Audio Specific Config (ISO 14496-3).
    /// 16 elements: indices 0–11 = standard frequencies, 12 = 7350 Hz (rare),
    /// 13–15 = reserved (0).
    /// </summary>
    /// <remarks>
    /// <para><b>Single source of truth.</b> Used in Mp4ContainerParser,
    /// AacDecoder, and any code dealing with AAC Audio Specific Config.</para>
    /// <para>The previous version contained 12 elements (without 7350), which caused
    /// IndexOutOfRange when parsing rare AAC-LC profiles.</para>
    /// </remarks>
    private static readonly int[] AacSampleRates =
    [
        96000, 88200, 64000, 48000, 44100, 32000,
        24000, 22050, 16000, 12000, 11025, 8000,
        7350, 0, 0, 0
    ];

    /// <summary>
    /// Gets the sample rate by index from AAC Audio Specific Config.
    /// </summary>
    /// <param name="index">Frequency index (0–15) from ASC.</param>
    /// <returns>Sample rate in Hz; <see cref="DefaultSampleRate"/> for invalid/reserved indices.</returns>
    public static int GetAacSampleRate(int index)
    {
        if ((uint)index >= (uint)AacSampleRates.Length)
            return DefaultSampleRate;

        int rate = AacSampleRates[index];
        return rate > 0 ? rate : DefaultSampleRate;
    }

    /// <summary>
    /// Complete table of channel count by channel configuration from AAC ASC (ISO 14496-3 §1.6.5.1).
    /// </summary>
    /// <remarks>
    /// <para>Index 0 = defined separately (program_config_element), maps to <see cref="DefaultChannels"/> here.</para>
    /// <para>Index 7 = 7.1 surround (8 channels), indices 8–15 = reserved.</para>
    /// </remarks>
    private static readonly int[] AacChannelCounts = [0, 1, 2, 3, 4, 5, 6, 8];

    /// <summary>
    /// Gets the channel count by channel configuration from AAC Audio Specific Config.
    /// </summary>
    /// <param name="channelConfig">Channel configuration (0–7) from ASC.</param>
    /// <returns>Channel count; <see cref="DefaultChannels"/> for invalid values.</returns>
    public static int GetAacChannels(int channelConfig)
    {
        if ((uint)channelConfig >= (uint)AacChannelCounts.Length)
            return DefaultChannels;

        int ch = AacChannelCounts[channelConfig];
        return ch > 0 ? ch : DefaultChannels;
    }
}