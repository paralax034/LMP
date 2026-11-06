namespace LMP.Core.Audio.Interfaces;

/// <summary>
/// Compressed audio to PCM decoder.
/// </summary>
public interface IAudioDecoder : IDisposable
{
    /// <summary>
    /// Decodes a compressed frame to float32 PCM.
    /// </summary>
    /// <param name="encodedData">Compressed data (single frame)</param>
    /// <param name="outputBuffer">Output buffer (float32, interleaved)</param>
    /// <returns>Number of samples per channel</returns>
    int Decode(ReadOnlySpan<byte> encodedData, Span<float> outputBuffer);

    /// <summary>
    /// Decodes with a state reset (after a seek operation).
    /// </summary>
    /// <remarks>
    /// <para>Retained as a compatible high-level API for direct decoder usage.</para>
    /// <para>In the main playback pipeline, a single call to
    /// <see cref="FlushState"/> prior to decoding warm-up skip frames and
    /// subsequent calls to <see cref="Decode"/> are preferred to avoid executing
    /// the special seek-reset path on the hot path of the first skipped frame.</para>
    /// </remarks>
    int DecodeWithReset(ReadOnlySpan<byte> encodedData, Span<float> outputBuffer);

    /// <summary>
    /// Performs a light reset of the internal decoder state without full recreation.
    /// </summary>
    /// <remarks>
    /// <para>This method is used during seek operations for a one-time clear of the decoder state
    /// prior to decoding and discarding warm-up skip frames.</para>
    /// <para>The implementation depends on the codec: AAC performs an actual soft flush of internal
    /// buffers, while Opus may be an intentional no-op if the decoding library
    /// does not provide a safe intermediate flush without a hard reset.</para>
    /// </remarks>
    void FlushState();

    /// <summary>Sample rate</summary>
    int SampleRate { get; }

    /// <summary>Channel count</summary>
    int Channels { get; }

    /// <summary>Maximum output buffer size in samples per channel</summary>
    int MaxFrameSize { get; }

    /// <summary>Codec type</summary>
    AudioCodec Codec { get; }

    /// <summary>Indicates whether the decoder is initialized</summary>
    bool IsInitialized { get; }
}