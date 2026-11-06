using System.Buffers;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Interfaces;

namespace LMP.Core.Audio.Parsers;

/// <summary>
/// <see cref="WebMParser"/> adapter for <see cref="IContainerParser"/>.
/// Encapsulates memory management logic (Zero-Allocation) for audio frames.
/// </summary>
public sealed class WebMContainerParser : IContainerParser
{
    private readonly WebMParser _parser;
    private bool _disposed;

    // Holds a reference to the current frame's rented buffer to return it when reading the next one
    private IMemoryOwner<byte>? _currentFrameOwner;

    public long DurationMs => _parser.DurationMs;
    public AudioCodec Codec => AudioCodec.Opus;
    public byte[]? DecoderConfig => _parser.CodecPrivate;
    public int SampleRate => _parser.SampleRate > 0 ? _parser.SampleRate : 48000;
    public int Channels => _parser.Channels > 0 ? _parser.Channels : 2;

    /// <summary>
    /// Indicates whether a resync occurred in the current parser.
    /// If true, a subsequent EOF indicates actual file truncation.
    /// </summary>
    public bool ResyncOccurred => _parser.ResyncOccurred;

    public WebMContainerParser(Stream stream)
    {
        _parser = new WebMParser(stream);
    }

    public ValueTask<bool> ParseHeadersAsync(CancellationToken ct = default)
    {
        return _parser.ParseHeadersAsync(ct);
    }

    /// <summary>
    /// Reads the next audio frame. Uses a synchronous fast-path without allocating an async state machine
    /// when the block is instantly available from the parser's internal buffer.
    /// </summary>
    public ValueTask<AudioFrame?> ReadNextFrameAsync(CancellationToken ct = default)
    {
        ReleaseCurrentFrame();

        var blockTask = _parser.ReadNextBlockAsync(ct);
        if (blockTask.IsCompletedSuccessfully)
        {
            return new ValueTask<AudioFrame?>(WrapBlock(blockTask.Result));
        }

        return ReadNextFrameSlowAsync(blockTask);
    }

    private async ValueTask<AudioFrame?> ReadNextFrameSlowAsync(ValueTask<WebMParser.AudioBlock?> blockTask)
    {
        var block = await blockTask.ConfigureAwait(false);
        return WrapBlock(block);
    }

    private AudioFrame? WrapBlock(WebMParser.AudioBlock? block)
    {
        if (block == null)
            return null;

        _currentFrameOwner = block.Value.Owner;

        return new AudioFrame
        {
            Data = _currentFrameOwner.Memory[..block.Value.Length],
            TimestampMs = block.Value.TimestampMs,
            DurationMs = 20, // Default Opus frame duration
            IsKeyFrame = block.Value.IsKeyFrame
        };
    }

    /// <summary>
    /// Searches for the nearest entry point for seeking.
    /// </summary>
    /// <returns>
    /// BytePosition — absolute position in the stream (start of Cluster).
    /// TimestampMs — ACTUAL timestamp from the cue point (not target!).
    /// This is the position from which decoding will start. The decoder will skip frames
    /// up to targetMs via the skip frames mechanism.
    /// </returns>
    public (long BytePosition, long TimestampMs)? FindSeekPosition(long targetMs)
    {
        var cuePoints = _parser.CuePoints;
        if (cuePoints.Count == 0)
            return null;

        // Binary search for the nearest cue point ≤ targetMs
        int low = 0, high = cuePoints.Count - 1;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (cuePoints[mid].TimeMs <= targetMs)
                low = mid;
            else
                high = mid - 1;
        }

        var cue = cuePoints[low];
        long bytePos = _parser.FindSeekPosition(targetMs) ?? 0;

        // Return the ACTUAL timestamp from the cue point.
        // CachingStreamSource.SeekAsync will set positionMs = cue.TimeMs,
        // and AudioPipeline.SetDecodedSamplesPosition will recalculate the sample position.
        return (bytePos, cue.TimeMs);
    }

    public void Reset()
    {
        ReleaseCurrentFrame();
        _parser.Reset();
    }

    private void ReleaseCurrentFrame()
    {
        _currentFrameOwner?.Dispose();
        _currentFrameOwner = null;
    }

    public void RequireResync()
    {
        ReleaseCurrentFrame();
        _parser.RequireResync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ReleaseCurrentFrame();
        _parser.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}