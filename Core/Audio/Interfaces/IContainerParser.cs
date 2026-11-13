namespace LMP.Core.Audio.Interfaces;

/// <summary>
/// Container format parser (WebM, MP4, etc.).
/// </summary>
internal interface IContainerParser : IAsyncDisposable, IDisposable
{
    /// <summary>Duration in milliseconds.</summary>
    long DurationMs { get; }
    
    /// <summary>Audio data codec.</summary>
    AudioCodec Codec { get; }
    
    /// <summary>Decoder-specific config (ASC for AAC, CodecPrivate for Opus).</summary>
    byte[]? DecoderConfig { get; }
    
    /// <summary>Sample rate (0 if unknown).</summary>
    int SampleRate { get; }
    
    /// <summary>Channels (0 if unknown).</summary>
    int Channels { get; }

    /// <summary>Indicates whether container resynchronization was required after corruption.</summary>
    bool ResyncOccurred => false;
    
    /// <summary>
    /// Parses container headers.
    /// </summary>
    ValueTask<bool> ParseHeadersAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Reads the next audio frame.
    /// </summary>
    ValueTask<AudioFrame?> ReadNextFrameAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Finds the seek position.
    /// </summary>
    (long BytePosition, long TimestampMs)? FindSeekPosition(long targetMs);
    
    /// <summary>
    /// Resets parser state after seek.
    /// </summary>
    void Reset();

    /// <summary>
    /// Instructs the parser to discard its current internal buffer and 
    /// force search for the next valid sync block on the next read.
    /// </summary>
    void RequireResync();
}