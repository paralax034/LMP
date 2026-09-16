using System.Diagnostics;
using LMP.Core.Audio.Backends;
using LMP.Core.Audio.Helpers;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Бенчмарки горячего пути аудио-пайплайна.
/// </summary>
/// <remarks>
/// <para>
/// Измеряет throughput и латентность критических операций:
/// GainProcessor (SIMD/ramp), LockFreeRingBuffer (write/read/round-trip),
/// и полную симуляцию AudioCallback.
/// </para>
/// <para>
/// В Debug build числа throughput не репрезентативны (JIT не векторизует).
/// Zero-alloc проверка корректна в обеих конфигурациях.
/// Для производственных замеров используй Release build.
/// </para>
/// </remarks>
public static class AudioHotPathBenchmarks
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    /// <summary>100 мс при 48 kHz stereo — размер одного WinMM-буфера.</summary>
    private const int BufferSamples = SampleRate / 10 * Channels;
    private const int WarmupIterations = 100;
    private const int MeasureIterations = 50_000;

    // ── infrastructure ─────────────────────────────────────────────────────

    /// <summary>
    /// Результат одного бенчмарка.
    /// </summary>
    private readonly record struct BenchmarkResult(
        string Name,
        double MeanNs,
        double OpsPerSec,
        long AllocatedBytes);

    /// <summary>
    /// Запускает бенчмарк с замером времени и аллокаций.
    /// </summary>
    /// <remarks>
    /// Использует <see cref="Stopwatch.GetTimestamp"/> вместо <see cref="Stopwatch.StartNew"/>
    /// для исключения аллокации объекта <see cref="Stopwatch"/> из окна измерения.
    /// </remarks>
    /// <param name="name">Имя для логирования.</param>
    /// <param name="action">Тело бенчмарка. Не должно аллоцировать.</param>
    /// <param name="iterations">Количество измеряемых итераций.</param>
    /// <returns>Результат с метриками производительности и аллокациями.</returns>
    private static BenchmarkResult RunBenchmark(
        string name,
        Action action,
        int iterations = MeasureIterations)
    {
        for (int i = 0; i < WarmupIterations; i++) action();

        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        long startTs = Stopwatch.GetTimestamp();

        for (int i = 0; i < iterations; i++) action();

        long elapsedTs = Stopwatch.GetTimestamp() - startTs;
        long allocAfter = GC.GetAllocatedBytesForCurrentThread();

        double totalNs = elapsedTs * 1_000_000_000.0 / Stopwatch.Frequency;
        double meanNs = totalNs / iterations;
        double opsPerSec = iterations / (totalNs / 1_000_000_000.0);
        long allocated = allocAfter - allocBefore;

        return new BenchmarkResult(name, meanNs, opsPerSec, allocated);
    }

    private static void LogResult(BenchmarkResult r)
    {
        string allocStr = r.AllocatedBytes == 0 ? "0 B ✓" : $"{r.AllocatedBytes} B ✗ REGRESSION";
        Log.Info($"[Benchmark] {r.Name,-45} │ {r.MeanNs,10:F1} ns/op │ {r.OpsPerSec,12:F0} ops/s │ alloc: {allocStr}");
    }

    private static void AssertNoRegression(BenchmarkResult r)
    {
        if (r.AllocatedBytes != 0)
            throw new InvalidOperationException(
                $"[Benchmark Regression] {r.Name}: {r.AllocatedBytes} bytes allocated " +
                $"over {MeasureIterations} iterations ({(double)r.AllocatedBytes / MeasureIterations:F2} B/op)");
    }

    private static float[] CreateSineBuffer(int length)
    {
        var buf = new float[length];
        for (int i = 0; i < length; i++)
            buf[i] = MathF.Sin(i * 0.1f) * 0.5f;
        return buf;
    }

    // ── benchmarks ─────────────────────────────────────────────────────────

    [TestMethod(TestCategory.Benchmark, "Audio hot path: full benchmark suite", Group = TestGroups.Audio, Order = 1, TimeoutSeconds = 120)]
    public static Task TestAudioHotPathBenchmarkAsync()
    {
#if DEBUG
        Log.Warn("[Benchmark] Running in Debug build — throughput numbers are not representative. " +
                 "Zero-alloc assertions are still enforced.");
#endif

        Log.Info("[Benchmark] ═══════════════════════════════════════════════════════");
        Log.Info($"[Benchmark] Audio Hot Path Suite │ {MeasureIterations:N0} iterations │ buffer={BufferSamples} samples (100ms @ 48kHz stereo)");
        Log.Info("[Benchmark] ═══════════════════════════════════════════════════════");

        // ── GainProcessor ──────────────────────────────────────────────────

        var gainProcessor = new GainProcessor();
        gainProcessor.SetVolumeGain(0.75f);
        float[] gainBuffer = CreateSineBuffer(BufferSamples);

        // Прогрев ramp
        for (int i = 0; i < 3000; i++)
            gainProcessor.Process(gainBuffer.AsSpan());

        var r1 = RunBenchmark("GainProcessor.Process (SIMD, gain=0.75)", () =>
            gainProcessor.Process(gainBuffer.AsSpan()));
        LogResult(r1);

        var gainRamp = new GainProcessor();
        float[] rampBuffer = CreateSineBuffer(BufferSamples);
        bool toggle = false;

        var r2 = RunBenchmark("GainProcessor.Process (ramp, alternating)", () =>
        {
            gainRamp.SetVolumeGain(toggle ? 0.3f : 0.9f);
            toggle = !toggle;
            gainRamp.Process(rampBuffer.AsSpan());
        });
        LogResult(r2);

        // ── LockFreeRingBuffer ─────────────────────────────────────────────

        var ring = new LockFreeRingBuffer<float>(BufferSamples * 8);
        float[] writeData = CreateSineBuffer(BufferSamples);
        float[] readData = new float[BufferSamples];

        var r3 = RunBenchmark("RingBuffer.Write (full buffer)", () =>
        {
            ring.Write(writeData.AsSpan());
            ring.Clear();
        });
        LogResult(r3);

        var r4 = RunBenchmark("RingBuffer.Read (full buffer)", () =>
        {
            ring.Write(writeData.AsSpan());
            ring.Read(readData.AsSpan());
        });
        LogResult(r4);

        var r5 = RunBenchmark("RingBuffer round-trip (Write + Read)", () =>
        {
            ring.Write(writeData.AsSpan());
            ring.Read(readData.AsSpan());
        });
        LogResult(r5);

        // ── Full AudioCallback simulation ──────────────────────────────────

        var cbRing = new LockFreeRingBuffer<float>(BufferSamples * 8);
        var cbGain = new GainProcessor();
        cbGain.SetVolumeGain(0.8f);
        float[] cbSrc = CreateSineBuffer(BufferSamples);
        float[] cbDst = new float[BufferSamples];

        // Прогрев ramp
        for (int i = 0; i < 3000; i++)
        {
            cbRing.Write(cbSrc.AsSpan());
            int rd = cbRing.Read(cbDst.AsSpan());
            if (rd > 0) cbGain.Process(cbDst.AsSpan(0, rd));
        }

        var r6 = RunBenchmark("Full AudioCallback (Write → Read → Gain)", () =>
        {
            cbRing.Write(cbSrc.AsSpan());
            int read = cbRing.Read(cbDst.AsSpan());
            if (read > 0)
                cbGain.Process(cbDst.AsSpan(0, read));
        });
        LogResult(r6);

        Log.Info("[Benchmark] ═══════════════════════════════════════════════════════");

        AssertNoRegression(r1);
        AssertNoRegression(r2);
        AssertNoRegression(r3);
        AssertNoRegression(r4);
        AssertNoRegression(r5);
        AssertNoRegression(r6);

        Log.Info("[Benchmark] All benchmarks passed: 0 allocations across all hot paths ✓");
        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Benchmark, "RingBuffer: contention stress (SPSC)", Group = TestGroups.Audio, Order = 2, TimeoutSeconds = 60)]
    public static async Task TestRingBufferContentionStressAsync()
    {
        // 1 секунда реального аудио при 48kHz stereo
        const int TotalSamples = SampleRate * Channels;
        // Размер чанка совпадает с Opus frame (20ms)
        const int ChunkSize = SampleRate * Channels * 20 / 1000;

        var ring = new LockFreeRingBuffer<float>(ChunkSize * 16);
        float[] producerChunk = new float[ChunkSize];
        float[] consumerChunk = new float[ChunkSize];

        for (int i = 0; i < ChunkSize; i++)
            producerChunk[i] = MathF.Sin(i * 0.05f);

        long totalConsumed = 0;
        long totalProduced = 0;
        long consumerSpinWaits = 0;
        long producerSpinWaits = 0;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cts.Token;

        // Producer: пишет строго с реальным темпом (~20ms между чанками)
        var producerTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                long produced = Interlocked.Read(ref totalProduced);
                if (produced >= TotalSamples) break;

                int written = ring.Write(producerChunk.AsSpan());
                Interlocked.Add(ref totalProduced, written);

                if (written < ChunkSize)
                {
                    Interlocked.Increment(ref producerSpinWaits);
                    await Task.Delay(1, token).ConfigureAwait(false);
                    continue;
                }

                // Реальный темп: 20ms на один 20ms-чанк
                await Task.Delay(20, token).ConfigureAwait(false);
            }
        });

        // Consumer: читает с реальным темпом (~20ms между чанками, имитирует WinMM callback)
        long startTs = Stopwatch.GetTimestamp();

        while (!token.IsCancellationRequested)
        {
            long currentConsumed = Interlocked.Read(ref totalConsumed);
            if (currentConsumed >= TotalSamples) break;

            int read = ring.Read(consumerChunk.AsSpan());
            Interlocked.Add(ref totalConsumed, read);

            if (read == 0)
            {
                Interlocked.Increment(ref consumerSpinWaits);
                await Task.Delay(1, token).ConfigureAwait(false);
                continue;
            }

            // Реальный темп: 20ms на один 20ms-чанк
            await Task.Delay(20, token).ConfigureAwait(false);
        }

        long elapsedTs = Stopwatch.GetTimestamp() - startTs;
        double elapsedSec = elapsedTs / (double)Stopwatch.Frequency;

        try { await producerTask.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false); }
        catch { }

        long finalConsumed = Interlocked.Read(ref totalConsumed);
        long finalProduced = Interlocked.Read(ref totalProduced);

        // Реальное время = consumed_samples / (SampleRate * Channels) секунд аудио
        double audioSeconds = (double)finalConsumed / (SampleRate * Channels);
        // Wall-clock время должно быть ≈ audioSeconds (real-time ratio ≈ 1.0)
        double realTimeRatio = elapsedSec > 0 ? audioSeconds / elapsedSec : 0;

        Log.Info($"[Stress] Produced: {finalProduced:N0} samples, Consumed: {finalConsumed:N0} samples");
        Log.Info($"[Stress] Wall-clock: {elapsedSec:F2}s, Audio: {audioSeconds:F2}s");
        Log.Info($"[Stress] Real-time ratio: {realTimeRatio:F2}x (target: ~1.0 ±0.3)");
        Log.Info($"[Stress] Consumer underruns: {consumerSpinWaits}, Producer backpressure: {producerSpinWaits}");

        if (finalConsumed == 0)
            throw new InvalidOperationException("[Stress] Zero samples consumed — ring buffer is broken");

        // Real-time ratio должен быть близок к 1.0: мы намеренно идём с темпом реального аудио
        if (realTimeRatio is < 0.5 or > 3.0)
            throw new InvalidOperationException(
                $"[Stress] Unexpected real-time ratio: {realTimeRatio:F2}x (expected 0.5–3.0). " +
                $"Underruns: {consumerSpinWaits}");
    }
}