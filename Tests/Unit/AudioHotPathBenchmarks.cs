using System.Diagnostics;
using LMP.Core.Audio.Backends;
using LMP.Core.Audio.Helpers;
using LMP.Tests.Framework;
using TokenExtensions = LMP.Core.Helpers.Extensions.CancellationTokenExtensions;

namespace LMP.Tests.Unit;

/// <summary>
/// Audio pipeline throughput benchmarks with mandatory zero-allocation verification (0 B)
/// and a multithreaded ring buffer integrity stress test.
/// </summary>
public static class AudioHotPathBenchmarks
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int BufferSamples = SampleRate / 10 * Channels; // 100 ms stereo
    private const int WarmupIterations = 50;
    private const int MeasureIterations = 20_000;
    private const int AllocCheckIterations = 200;

    /// <summary>Number of measurement rounds; odd so that the median is exactly the middle element.</summary>
    private const int MeasureRounds = 5;

    /// <summary>
    /// Minimum warmup duration. Call count tracking for Tier 1 JIT optimization starts
    /// only after 100 ms with no new compilation, after which the code is recompiled in the background: 50 calls cannot reach Tier 1.
    /// </summary>
    private static readonly TimeSpan WarmupDuration = TimeSpan.FromMilliseconds(250);

    private readonly record struct BenchmarkResult(
        string Name,
        double MedianNs,
        double OpsPerSec,
        double SpreadPercent);

    private static BenchmarkResult RunBenchmark(string name, Action action, int iterations = MeasureIterations)
    {
        // Warmup by time, not call count: measurement must start on optimized code
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
    /// Measures an operation, logs the result, and verifies zero managed allocations.
    /// Timings are only logged: time thresholds are unstable on shared desktop environments.
    /// Logs the median of rounds and their variance: high variance flags the measurement as inconclusive.
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
    /// Marks the benchmark as inconclusive (Skipped) if process timings do not reflect release codegen.
    /// </summary>
    internal static void SkipIfUnoptimizedCodegen()
    {
        // Incremental build does not recompile the assembly when changing Optimize alone, hence --no-incremental
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
    /// Benchmark run header: logs the measurement environment and marks the run as inconclusive if codegen is non-release.
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
    /// DSP pipeline throughput on a real (non-decaying) signal with a guaranteed 0 B per operation.
    /// </summary>
    [TestMethod(TestCategory.Benchmark, "DSP & RingBuffer Throughput Suite",
        Group = TestGroups.Audio, Order = 1, TimeoutSeconds = 60)]
    public static Task TestAudioHotPathBenchmarkAsync()
    {
        float[] sourceData = CreateSineBuffer(BufferSamples);
        float[] workBuffer = new float[BufferSamples];

        // In-place processing of a single buffer with 0.75 gain causes signal denormalization and zeroing within ~360 iterations,
        // so each iteration starts with a copy of the original sine wave
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
    /// SPSC stress test: producer writes a monotonic sequence, consumer verifies ordering and zero data loss.
    /// </summary>
    [TestMethod(TestCategory.Integration, "RingBuffer SPSC Concurrency Stress",
        Group = TestGroups.Audio, Order = 2, TimeoutSeconds = 30)]
    public static async Task TestRingBufferContentionStressAsync()
    {
        const int totalSamples = SampleRate * Channels; // 1 second stereo; values < 2^24 are exactly representable in float
        const int chunkSize = SampleRate * Channels * 20 / 1000; // 20 ms chunk

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
                    // Stop the producer before exiting the test so it does not outlive using cts/ring
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