using System.Buffers;
using System.Runtime.CompilerServices;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Interfaces;

namespace LMP.Tests.Unit;

/// <summary>
/// Assembles <see cref="AudioPipeline"/> bypassing the factory: the test injects custom source, decoder, and backend,
/// invoking the audio callback directly without device, network, or reflection.
/// </summary>
internal static class AudioPipelineTestHarness
{
    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern AudioPipeline CreatePipeline(
        IAudioSource source,
        IAudioDecoder decoder,
        IPlaybackBackend backend,
        LockFreeRingBuffer<float> pcmBuffer,
        float[] decodeBuffer,
        AudioStreamInfo streamInfo,
        CancellationTokenSource lifetimeCts);

    /// <summary>Invokes the private audio callback in the same manner as the device thread does.</summary>
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "AudioCallback")]
    internal static extern int AudioCallback(AudioPipeline pipeline, Span<float> buffer);

    /// <summary>Counter of samples accounted for by the decoding loop.</summary>
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_decodedSamples")]
    internal static extern ref long DecodedSamples(AudioPipeline pipeline);

    /// <summary>
    /// Creates a pipeline over the provided components; the source, decoder, and ring buffer are transferred to pipeline ownership.
    /// </summary>
    internal static AudioPipeline Create(
        IAudioSource source,
        IAudioDecoder decoder,
        IPlaybackBackend backend,
        LockFreeRingBuffer<float> ring,
        AudioStreamInfo? streamInfo = null)
    {
        // DisposeAsync pipeline returns the decoding buffer to the pool
        float[] decodeBuffer = ArrayPool<float>.Shared.Rent(Math.Max(decoder.MaxFrameSize, 4_096) * decoder.Channels);
        return CreatePipeline(
            source,
            decoder,
            backend,
            ring,
            decodeBuffer,
            streamInfo ?? AudioStreamInfo.Empty,
            new CancellationTokenSource());
    }

    /// <summary>Stub compressed frame: content is ignored by test decoders.</summary>
    internal static AudioFrame Frame(long timestampMs = 0) =>
        new() { Data = new byte[16], TimestampMs = timestampMs, DurationMs = 20 };

    /// <summary>Polls the condition every 5 ms until it is met or <paramref name="timeoutMs"/> expires.</summary>
    /// <returns><c>true</c> if the condition was met before the timeout.</returns>
    internal static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline)
                return condition();

            await Task.Delay(5).ConfigureAwait(false);
        }

        return true;
    }
}

/// <summary>
/// A source that yields frames only when triggered by the test and ignores the cancellation token, simulating a read hung in a socket.
/// Tracks total calls and active pending reads concurrently.
/// </summary>
internal sealed class BlockingAudioSource(int sampleRate = 48_000, int channels = 2) : IAudioSource
{
    private readonly Lock _gate = new();
    private readonly Queue<TaskCompletionSource<AudioFrame?>> _waiters = new();
    private readonly Queue<AudioFrame> _ready = new();
    private bool _open;
    private int _readCalls;
    private int _activeReads;
    private int _maxConcurrentReads;

    /// <summary>Number of times <see cref="ReadFrameAsync"/> was invoked.</summary>
    public int ReadCalls => Volatile.Read(ref _readCalls);

    /// <summary>Number of reads currently awaiting a frame.</summary>
    public int ActiveReads => Volatile.Read(ref _activeReads);

    /// <summary>Maximum number of concurrent pending reads over the source lifetime.</summary>
    public int MaxConcurrentReads => Volatile.Read(ref _maxConcurrentReads);

    public long DurationMs => 0;
    public long PositionMs => 0;
    public bool CanSeek => false;
    public AudioCodec Codec => AudioCodec.Unknown;
    public double BufferProgress => 1.0;
    public bool IsFullyBuffered => true;
    public byte[]? DecoderConfig => null;
    public int SampleRate => sampleRate;
    public int Channels => channels;

    public ValueTask<bool> InitializeAsync(CancellationToken ct = default) => ValueTask.FromResult(true);

    public ValueTask<AudioFrame?> ReadFrameAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _readCalls);
        RaiseMaxConcurrentReads(Interlocked.Increment(ref _activeReads));

        TaskCompletionSource<AudioFrame?> waiter;
        lock (_gate)
        {
            if (_ready.TryDequeue(out AudioFrame frame))
            {
                Interlocked.Decrement(ref _activeReads);
                return ValueTask.FromResult<AudioFrame?>(frame);
            }

            if (_open)
            {
                Interlocked.Decrement(ref _activeReads);
                return ValueTask.FromResult<AudioFrame?>(null);
            }

            waiter = new TaskCompletionSource<AudioFrame?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Enqueue(waiter);
        }

        // Token is intentionally ignored: reads are completed only by Release or Open
        return new ValueTask<AudioFrame?>(waiter.Task);
    }

    /// <summary>Delivers a frame to the oldest pending read or queues it for the next one.</summary>
    public void Release(AudioFrame frame)
    {
        TaskCompletionSource<AudioFrame?>? waiter;
        lock (_gate)
        {
            if (!_waiters.TryDequeue(out waiter))
            {
                _ready.Enqueue(frame);
                return;
            }
        }

        Interlocked.Decrement(ref _activeReads);
        waiter.TrySetResult(frame);
    }

    /// <summary>Completes all pending and future reads with end-of-stream (after serving already queued frames).</summary>
    public void Open()
    {
        TaskCompletionSource<AudioFrame?>[] waiters;
        lock (_gate)
        {
            _open = true;
            waiters = [.. _waiters];
            _waiters.Clear();
        }

        foreach (var waiter in waiters)
        {
            Interlocked.Decrement(ref _activeReads);
            waiter.TrySetResult(null);
        }
    }

    public ValueTask<bool> SeekAsync(long positionMs, CancellationToken ct = default) => ValueTask.FromResult(false);
    public IReadOnlyList<(double Start, double End)> GetBufferedRanges() => [];
    public void ReleaseRamBuffers() { }
    public void CancelPendingOperations() { }
    public void SetPlaybackActive(bool active) { }
    public void Dispose() => Open();

    public ValueTask DisposeAsync()
    {
        Open();
        return ValueTask.CompletedTask;
    }

    private void RaiseMaxConcurrentReads(int active)
    {
        int observed = Volatile.Read(ref _maxConcurrentReads);
        while (active > observed)
        {
            int prior = Interlocked.CompareExchange(ref _maxConcurrentReads, active, observed);
            if (prior == observed)
                return;

            observed = prior;
        }
    }
}

/// <summary>
/// A decoder that fills the output with a constant value: the sample count does not depend on the codec or frame payload.
/// </summary>
internal sealed class ConstantDecoder(int sampleRate, int channels, int framesPerDecode, int maxFrameSize, float value) : IAudioDecoder
{
    public int SampleRate => sampleRate;
    public int Channels => channels;
    public int MaxFrameSize => maxFrameSize;
    public AudioCodec Codec => AudioCodec.Unknown;
    public bool IsInitialized => true;

    public int Decode(ReadOnlySpan<byte> encodedData, Span<float> outputBuffer)
    {
        int frames = Math.Min(framesPerDecode, outputBuffer.Length / channels);
        outputBuffer[..(frames * channels)].Fill(value);
        return frames;
    }

    public int DecodeWithReset(ReadOnlySpan<byte> encodedData, Span<float> outputBuffer) => Decode(encodedData, outputBuffer);
    public void FlushState() { }
    public void Dispose() { }
}