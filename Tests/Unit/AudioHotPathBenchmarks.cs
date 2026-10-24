using System.Diagnostics;
using LMP.Core.Audio.Backends;
using LMP.Core.Audio.Helpers;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Бенчмарки пропускной способности аудио-тракта и многопоточный стресс-тест кольцевого буфера.
/// </summary>
public static class AudioHotPathBenchmarks
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int BufferSamples = SampleRate / 10 * Channels; // 100 мс stereo
    private const int WarmupIterations = 50;
    private const int MeasureIterations = 20_000;

    private readonly record struct BenchmarkResult(
        string Name,
        double MeanNs,
        double OpsPerSec);

    private static BenchmarkResult RunBenchmark(string name, Action action, int iterations = MeasureIterations)
    {
        for (int i = 0; i < WarmupIterations; i++) action();

        long startTs = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++) action();
        long elapsedTs = Stopwatch.GetTimestamp() - startTs;

        double totalNs = elapsedTs * 1_000_000_000.0 / Stopwatch.Frequency;
        double meanNs = totalNs / iterations;
        double opsPerSec = iterations / (totalNs / 1_000_000_000.0);

        return new BenchmarkResult(name, meanNs, opsPerSec);
    }

    private static float[] CreateSineBuffer(int length)
    {
        var buf = new float[length];
        for (int i = 0; i < length; i++)
            buf[i] = MathF.Sin(i * 0.1f) * 0.5f;
        return buf;
    }

    [TestMethod(TestCategory.Benchmark, "DSP & RingBuffer Throughput Suite",
        Group = TestGroups.Audio, Order = 1, TimeoutSeconds = 60)]
    public static Task TestAudioHotPathBenchmarkAsync()
    {
        var gainProcessor = new GainProcessor();
        gainProcessor.SetVolumeGain(0.75f);
        float[] gainBuffer = CreateSineBuffer(BufferSamples);

        for (int i = 0; i < 3000; i++)
            gainProcessor.Process(gainBuffer.AsSpan());

        var r1 = RunBenchmark("GainProcessor.Process (SIMD)", () => gainProcessor.Process(gainBuffer.AsSpan()));
        Log.Info($"[Benchmark] {r1.Name,-35} | {r1.MeanNs,8:F1} ns/op | {r1.OpsPerSec,10:F0} ops/s");

        var ring = new LockFreeRingBuffer<float>(BufferSamples * 8);
        float[] writeData = CreateSineBuffer(BufferSamples);
        float[] readData = new float[BufferSamples];

        var r2 = RunBenchmark("LockFreeRingBuffer Roundtrip", () =>
        {
            ring.Write(writeData.AsSpan());
            ring.Read(readData.AsSpan());
        });
        Log.Info($"[Benchmark] {r2.Name,-35} | {r2.MeanNs,8:F1} ns/op | {r2.OpsPerSec,10:F0} ops/s");

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Integration, "RingBuffer SPSC Concurrency Stress",
        Group = TestGroups.Audio, Order = 2, TimeoutSeconds = 30)]
    public static async Task TestRingBufferContentionStressAsync()
    {
        const int totalSamples = SampleRate * Channels; // 1 секунда stereo
        const int chunkSize = SampleRate * Channels * 20 / 1000; // 20мс чанк

        var ring = new LockFreeRingBuffer<float>(chunkSize * 16);
        float[] producerChunk = CreateSineBuffer(chunkSize);
        float[] consumerChunk = new float[chunkSize];

        long totalConsumed = 0;
        long totalProduced = 0;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = cts.Token;

        var producerTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                if (Interlocked.Read(ref totalProduced) >= totalSamples) break;

                int written = ring.Write(producerChunk.AsSpan());
                Interlocked.Add(ref totalProduced, written);

                if (written < chunkSize)
                    await Task.Delay(1, token).ConfigureAwait(false);
                else
                    await Task.Delay(5, token).ConfigureAwait(false);
            }
        }, token);

        while (!token.IsCancellationRequested)
        {
            if (Interlocked.Read(ref totalConsumed) >= totalSamples) break;

            int read = ring.Read(consumerChunk.AsSpan());
            Interlocked.Add(ref totalConsumed, read);

            if (read == 0)
                await Task.Delay(1, token).ConfigureAwait(false);
            else
                await Task.Delay(5, token).ConfigureAwait(false);
        }

        await producerTask.ConfigureAwait(false);

        TestAssert.True(totalConsumed >= totalSamples, $"Consumed {totalConsumed} of {totalSamples} samples");
        Log.Info($"[Stress] SPSC RingBuffer: successfully transmitted {totalConsumed:N0} samples");
    }
}