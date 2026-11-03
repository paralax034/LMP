using System.Diagnostics;
using LMP.Core.Audio.Backends;
using LMP.Core.Audio.Helpers;
using LMP.Tests.Framework;
using TokenExtensions = LMP.Core.Helpers.Extensions.CancellationTokenExtensions;

namespace LMP.Tests.Unit;

/// <summary>
/// Бенчмарки пропускной способности аудио-тракта с обязательной проверкой 0 B аллокаций
/// и многопоточный стресс-тест целостности кольцевого буфера.
/// </summary>
public static class AudioHotPathBenchmarks
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int BufferSamples = SampleRate / 10 * Channels; // 100 мс stereo
    private const int WarmupIterations = 50;
    private const int MeasureIterations = 20_000;
    private const int AllocCheckIterations = 200;

    /// <summary>Число раундов замера; нечётное, чтобы медиана была ровно средним элементом.</summary>
    private const int MeasureRounds = 5;

    /// <summary>
    /// Минимальная длительность прогрева. Счёт вызовов для перехода на оптимизированный уровень JIT стартует
    /// лишь после 100 мс без новой компиляции, затем код пересобирается в фоне: 50 вызовов не успевают дойти до Tier1.
    /// </summary>
    private static readonly TimeSpan WarmupDuration = TimeSpan.FromMilliseconds(250);

    private readonly record struct BenchmarkResult(
        string Name,
        double MedianNs,
        double OpsPerSec,
        double SpreadPercent);

    private static BenchmarkResult RunBenchmark(string name, Action action, int iterations = MeasureIterations)
    {
        // Прогрев по времени, а не по числу вызовов: замер обязан начаться уже на оптимизированном коде
        long warmupStart = Stopwatch.GetTimestamp();
        int warmed = 0;
        while (warmed < WarmupIterations || Stopwatch.GetElapsedTime(warmupStart) < WarmupDuration)
        {
            action();
            warmed++;
        }

        int perRound = Math.Max(1, iterations / MeasureRounds);
        Span<double> rounds = stackalloc double[MeasureRounds];
        for (int round = 0; round < MeasureRounds; round++)
        {
            long roundStart = Stopwatch.GetTimestamp();
            for (int i = 0; i < perRound; i++) action();
            rounds[round] = Stopwatch.GetElapsedTime(roundStart).TotalNanoseconds / perRound;
        }

        rounds.Sort();
        double medianNs = rounds[MeasureRounds / 2];
        double spreadPercent = medianNs > 0 ? (rounds[^1] - rounds[0]) / medianNs * 100.0 : 0.0;

        return new BenchmarkResult(name, medianNs, 1_000_000_000.0 / medianNs, spreadPercent);
    }

    /// <summary>
    /// Измеряет операцию, публикует результат в лог и проверяет отсутствие управляемых аллокаций.
    /// Тайминги только логируются: пороги по времени нестабильны на общем десктопе.
    /// В лог идёт медиана раундов и их разброс: большой разброс помечает замер как недостоверный.
    /// </summary>
    private static void MeasureZeroAlloc(string name, Action action, int iterations = MeasureIterations)
    {
        var result = RunBenchmark(name, action, iterations);
        Log.Info($"[Benchmark] {result.Name,-40} | {result.MedianNs,8:F1} ns/op | {result.OpsPerSec,10:F0} ops/s | spread {result.SpreadPercent,5:F1}%");
        TestAssert.ZeroAlloc(action, warmup: 0, iterations: AllocCheckIterations, context: name);
    }

    private static float[] CreateSineBuffer(int length)
    {
        var buf = new float[length];
        for (int i = 0; i < length; i++)
            buf[i] = MathF.Sin(i * 0.1f) * 0.5f;
        return buf;
    }

    /// <summary>
    /// Помечает бенчмарк неубедительным (Skipped), если тайминги процесса не отражают релизный кодоген.
    /// </summary>
    internal static void SkipIfUnoptimizedCodegen()
    {
        // Инкрементальная сборка не перекомпилирует сборку при смене одного Optimize, отсюда --no-incremental
        string? reason = DescribeUnoptimizedCodegen();
        if (reason != null)
            TestAssert.Skip($"Inconclusive: {reason}. Re-run after 'dotnet build -c Debug -p:Optimize=true --no-incremental' without a debugger.");
    }

    private static string? DescribeUnoptimizedCodegen()
    {
        if (Debugger.IsAttached)
            return "debugger attached";

        System.Reflection.Assembly hotPath = typeof(GainProcessor).Assembly;
        if (IsJitOptimizerDisabled(hotPath))
            return $"assembly '{hotPath.GetName().Name}' is built without optimizations";

        System.Reflection.Assembly harness = typeof(AudioHotPathBenchmarks).Assembly;
        if (harness != hotPath && IsJitOptimizerDisabled(harness))
            return $"assembly '{harness.GetName().Name}' is built without optimizations";

        return null;
    }

    private static bool IsJitOptimizerDisabled(System.Reflection.Assembly assembly) =>
        Attribute.GetCustomAttribute(assembly, typeof(DebuggableAttribute)) is DebuggableAttribute { IsJITOptimizerDisabled: true };

    /// <summary>
    /// Шапка прогона бенчмарков: записывает в лог среду измерений и помечает прогон неубедительным, если кодоген не релизный.
    /// </summary>
    [TestMethod(TestCategory.Benchmark, "Benchmark Environment Header",
        Group = TestGroups.Audio, Order = 0)]
    public static Task TestBenchmarkEnvironmentHeaderAsync()
    {
        Log.Info($"[Benchmark] Runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}"
            + $" | OS={System.Runtime.InteropServices.RuntimeInformation.OSDescription}"
            + $" | Arch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}"
            + $" | Vector<float>.Count={System.Numerics.Vector<float>.Count}"
            + $" | V128={System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated}"
            + $" | V256={System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated}"
            + $" | Fma={System.Runtime.Intrinsics.X86.Fma.IsSupported}"
            + $" | Avx2={System.Runtime.Intrinsics.X86.Avx2.IsSupported}"
            + $" | AdvSimd={System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported}"
            + $" | JitCompiled={System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeCompiled}"
            + $" | JitOptimizerDisabled={IsJitOptimizerDisabled(typeof(GainProcessor).Assembly)}"
            + $" | Debugger={Debugger.IsAttached}");

        SkipIfUnoptimizedCodegen();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Пропускная способность DSP-цепочки на реальном (не затухающем) сигнале с гарантией 0 B на каждую операцию.
    /// </summary>
    [TestMethod(TestCategory.Benchmark, "DSP & RingBuffer Throughput Suite",
        Group = TestGroups.Audio, Order = 1, TimeoutSeconds = 60)]
    public static Task TestAudioHotPathBenchmarkAsync()
    {
        float[] sourceData = CreateSineBuffer(BufferSamples);
        float[] workBuffer = new float[BufferSamples];

        // In-place обработка одного буфера с gain 0.75 за ~360 итераций уводит сигнал в денормалы и ноль,
        // поэтому каждая итерация начинается с копии исходного синуса
        var gainProcessor = new GainProcessor();
        gainProcessor.SetVolumeGain(0.75f);
        for (int i = 0; i < 3000; i++)
        {
            sourceData.AsSpan().CopyTo(workBuffer);
            gainProcessor.Process(workBuffer.AsSpan());
        }

        MeasureZeroAlloc("GainProcessor.Process (SIMD, +copy)", () =>
        {
            sourceData.AsSpan().CopyTo(workBuffer);
            gainProcessor.Process(workBuffer.AsSpan());
        });

        using var ring = new LockFreeRingBuffer<float>(BufferSamples * 8);
        float[] readData = new float[BufferSamples];

        MeasureZeroAlloc("LockFreeRingBuffer Roundtrip", () =>
        {
            ring.Write(sourceData.AsSpan());
            ring.Read(readData.AsSpan());
        });

        var eq = new EqualizerProcessor(SampleRate, Channels);
        MeasureZeroAlloc("EqualizerProcessor (Bypass)", () => eq.Process(workBuffer.AsSpan()));

        eq.Configure(enabled: true, EqualizerPresets.GetPresetBands(EqualizerPresets.AnimeId), preampDb: 0f, autoPreamp: true);
        eq.SnapToTarget();

        MeasureZeroAlloc("EqualizerProcessor (10-Band IIR, +copy)", () =>
        {
            sourceData.AsSpan().CopyTo(workBuffer);
            eq.Process(workBuffer.AsSpan());
        }, iterations: 5_000);

        var spectrum = new AudioSpectrumAnalyzer(SampleRate, Channels);
        MeasureZeroAlloc("AudioSpectrumAnalyzer (10-Band)",
            () => spectrum.ProcessBlock(sourceData.AsSpan(), Channels),
            iterations: 5_000);

        var limiter = new Core.Audio.Normalization.TruePeakLimiter(SampleRate);
        var crossfader = new Core.Audio.Normalization.GainCrossfader(1.2f);
        MeasureZeroAlloc("TruePeakLimiter (Stereo-Linked, +copy)", () =>
        {
            sourceData.AsSpan().CopyTo(workBuffer);
            limiter.Process(workBuffer.AsSpan(), ref crossfader);
        }, iterations: 5_000);

        return Task.CompletedTask;
    }

    /// <summary>
    /// SPSC-стресс: производитель пишет монотонную последовательность, потребитель проверяет порядок и отсутствие потерь.
    /// </summary>
    [TestMethod(TestCategory.Integration, "RingBuffer SPSC Concurrency Stress",
        Group = TestGroups.Audio, Order = 2, TimeoutSeconds = 30)]
    public static async Task TestRingBufferContentionStressAsync()
    {
        const int totalSamples = SampleRate * Channels; // 1 секунда stereo; значения < 2^24 точно представимы во float
        const int chunkSize = SampleRate * Channels * 20 / 1000; // 20 мс чанк

        using var ring = new LockFreeRingBuffer<float>(chunkSize * 16);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = cts.Token;

        var producerTask = Task.Run(async () =>
        {
            float[] chunk = new float[chunkSize];
            int next = 0;

            while (next < totalSamples && !token.IsCancellationRequested)
            {
                int count = Math.Min(chunkSize, totalSamples - next);
                for (int i = 0; i < count; i++)
                    chunk[i] = next + i;

                int offset = 0;
                while (offset < count)
                {
                    int written = ring.Write(chunk.AsSpan(offset, count - offset));
                    offset += written;

                    if (written == 0 && !await TokenExtensions.DelayNoThrowAsync(token, 1, continueOnCapturedContext: false).ConfigureAwait(false))
                        return next + offset;
                }

                next += count;
            }

            return next;
        });

        float[] consumerChunk = new float[chunkSize];
        int expected = 0;

        while (expected < totalSamples)
        {
            int read = ring.Read(consumerChunk.AsSpan());
            for (int i = 0; i < read; i++)
            {
                if (consumerChunk[i] != expected)
                {
                    // Останавливаем производителя до выхода из теста, чтобы он не пережил using cts/ring
                    cts.Cancel();
                    TestAssert.Fail($"SPSC order violated at sample {expected}: got {consumerChunk[i]}");
                }

                expected++;
            }

            if (read == 0 && !await TokenExtensions.DelayNoThrowAsync(token, 1, continueOnCapturedContext: false).ConfigureAwait(false))
                break;
        }

        int produced = await producerTask.ConfigureAwait(false);

        TestAssert.Equal(totalSamples, produced, $"Producer stalled: wrote {produced} of {totalSamples} samples");
        TestAssert.Equal(totalSamples, expected, $"Consumer received {expected} of {totalSamples} samples in order");
        Log.Info($"[Stress] SPSC RingBuffer: {expected:N0} samples transmitted in strict order");
    }
}