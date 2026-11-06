namespace LMP.Core.Audio.Interfaces;

/// <summary>
/// Audio data source (network stream, file, etc.).
/// Provides compressed audio frames for decoding.
/// </summary>
public interface IAudioSource : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Initializes the source and prepares it for streaming.
    /// </summary>
    ValueTask<bool> InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Reads the next compressed audio frame.
    /// </summary>
    ValueTask<AudioFrame?> ReadFrameAsync(CancellationToken ct = default);

    /// <summary>
    /// Seeks to the specified position.
    /// </summary>
    ValueTask<bool> SeekAsync(long positionMs, CancellationToken ct = default);

    /// <summary>Total duration in milliseconds (-1 if unknown)</summary>
    long DurationMs { get; }

    /// <summary>Current position in milliseconds</summary>
    long PositionMs { get; }

    /// <summary>Indicates whether the source supports seeking</summary>
    bool CanSeek { get; }

    /// <summary>Audio data codec type</summary>
    AudioCodec Codec { get; }

    /// <summary>Buffering progress (0-100)</summary>
    double BufferProgress { get; }

    /// <summary>Fully loaded</summary>
    bool IsFullyBuffered { get; }

    /// <summary>
    /// Decoder-specific config (ASC for AAC, CodecPrivate for Opus).
    /// Null if not required or unavailable.
    /// </summary>
    byte[]? DecoderConfig { get; }

    /// <summary>Sample rate from container (0 if unknown)</summary>
    int SampleRate { get; }

    /// <summary>Channel count from container (0 if unknown)</summary>
    int Channels { get; }

    /// <summary>Buffered ranges for visualization</summary>
    IReadOnlyList<(double Start, double End)> GetBufferedRanges();

    /// <summary>Frees RAM buffers</summary>
    void ReleaseRamBuffers();

    /// <summary>Cancellation of current operations</summary>
    void CancelPendingOperations();

    /// <summary>
    /// Notifies the source of playback state changes (pause or active playback).
    /// Used by network sources to pause background downloads when paused.
    /// </summary>
    /// <param name="active">True — track is playing, background download allowed; False — paused.</param>
    void SetPlaybackActive(bool active);
}

/// <summary>
/// Audio frame with metadata
/// </summary>
public readonly struct AudioFrame
{
    public required ReadOnlyMemory<byte> Data { get; init; }
    public required long TimestampMs { get; init; }
    public required int DurationMs { get; init; }
    public bool IsKeyFrame { get; init; }
}

/// <summary>
/// Supported audio codecs
/// </summary>
public enum AudioCodec
{
    Unknown = 0,
    Opus = 1,
    Aac = 2,
    Vorbis = 3
}