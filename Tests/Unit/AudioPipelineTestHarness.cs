using System.Buffers;
using System.Runtime.CompilerServices;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Interfaces;

namespace LMP.Tests.Unit;

/// <summary>
/// Сборка <see cref="AudioPipeline"/> в обход фабрики: тест подставляет собственные источник, декодер и backend
/// и вызывает аудио-callback напрямую, без устройства, сети и рефлексии.
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

    /// <summary>Вызывает приватный аудио-callback так же, как это делает поток устройства.</summary>
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "AudioCallback")]
    internal static extern int AudioCallback(AudioPipeline pipeline, Span<float> buffer);

    /// <summary>Счётчик сэмплов, учтённых декодирующим циклом.</summary>
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_decodedSamples")]
    internal static extern ref long DecodedSamples(AudioPipeline pipeline);

    /// <summary>
    /// Создаёт pipeline над переданными компонентами; источник, декодер и кольцо переходят во владение pipeline.
    /// </summary>
    internal static AudioPipeline Create(
        IAudioSource source,
        IAudioDecoder decoder,
        IPlaybackBackend backend,
        LockFreeRingBuffer<float> ring,
        AudioStreamInfo? streamInfo = null)
    {
        // DisposeAsync pipeline возвращает буфер декодирования в пул
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

    /// <summary>Сжатый фрейм-заглушка: содержимое игнорируется тестовыми декодерами.</summary>
    internal static AudioFrame Frame(long timestampMs = 0) =>
        new() { Data = new byte[16], TimestampMs = timestampMs, DurationMs = 20 };

    /// <summary>Опрашивает условие каждые 5 мс, пока оно не выполнится или не истечёт <paramref name="timeoutMs"/>.</summary>
    /// <returns><c>true</c>, если условие выполнилось до таймаута.</returns>
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
/// Источник, отдающий фреймы только по команде теста и игнорирующий токен отмены, как чтение, зависшее в сокете.
/// Считает вызовы и одновременно незавершённые чтения.
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

    /// <summary>Сколько раз вызывался <see cref="ReadFrameAsync"/>.</summary>
    public int ReadCalls => Volatile.Read(ref _readCalls);

    /// <summary>Сколько чтений ждут фрейма прямо сейчас.</summary>
    public int ActiveReads => Volatile.Read(ref _activeReads);

    /// <summary>Наибольшее число одновременно незавершённых чтений за время жизни источника.</summary>
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

        // Токен намеренно игнорируется: чтение завершают только Release или Open
        return new ValueTask<AudioFrame?>(waiter.Task);
    }

    /// <summary>Отдаёт фрейм старейшему ожидающему чтению или ставит его в очередь для следующего.</summary>
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

    /// <summary>Завершает все ожидающие и будущие чтения концом потока (после выдачи уже поставленных фреймов).</summary>
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
/// Декодер, заполняющий выход постоянным значением: число сэмплов не зависит от кодека и содержимого фрейма.
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