using System.Diagnostics;
using LMP.Core.Audio.Backends;
using LMP.Core.Audio.Helpers;
using LMP.Tests.Framework;

namespace LMP.Tests.Benchmarks;

/// <summary>
/// Audio DSP and ring-buffer throughput benchmarks with mandatory zero-allocation verification (0 B).
/// </summary>
public static class AudioHotPathBenchmarks
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int BufferSamples = SampleRate / 10 * Channels; // 100 ms stereo
    private const int WarmupIterations = 50;
    private const int MeasureIterations = 20_000;
    private const int AllocCheckIterations = 200;
    private const int MeasureRounds = 5;
    private static readonly TimeSpan WarmupDuration = TimeSpan.FromMilliseconds(250);

    private readonly record struct BenchmarkResult(
        string Name,
        double MedianNs,
        double OpsPerSec,
        double SpreadPercent);

    private static BenchmarkResult RunBenchmark(string name, Action action, int iterations = MeasureIterations)
    {
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

    internal static void SkipIfUnoptimizedCodegen()
    {
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

    [TestMethod(TestCategory.Benchmark, "DSP & RingBuffer Throughput Suite",
        Group = TestGroups.Audio, Order = 1, TimeoutSeconds = 60)]
    public static Task TestAudioHotPathBenchmarkAsync()
    {
        float[] sourceData = CreateSineBuffer(BufferSamples);
        float[] workBuffer = new float[BufferSamples];

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
}