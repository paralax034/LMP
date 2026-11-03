using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using LMP.Core.Audio.Backends;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Normalization;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Порядок узлов графа воспроизведения: лимитер работает на частоте декодера, а ресемплер WASAPI приводит поток к 48 кГц
/// уже после него. Тест измеряет межотсчётные пики, которые интерполяция создаёт поверх гарантии потолка лимитера.
/// </summary>
public static class PlaybackGraphTests
{
    private const int SourceRate = 44_100;
    private const int DeviceRate = 48_000;
    private const int Channels = 2;

    /// <summary>Потолок <c>TruePeakLimiter</c> (константа <c>Ceiling</c>).</summary>
    private const float LimiterCeiling = 0.985f;

    /// <summary>Колено мягкого потолка <c>TruePeakLimiter</c> (константа <c>KneeStart</c>).</summary>
    private const float LimiterKnee = 0.88f;

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "SoftCeilingScale")]
    private static extern float SoftCeilingScale(TruePeakLimiter? owner, float peak);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_callback")]
    private static extern ref AudioDataCallback? CallbackField(WinAudioBackend backend);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_channels")]
    private static extern ref int ChannelsField(WinAudioBackend backend);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_sourceSampleRate")]
    private static extern ref int SourceSampleRateField(WinAudioBackend backend);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_sampleRate")]
    private static extern ref int DeviceSampleRateField(WinAudioBackend backend);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ReadCallbackWithResamplingUnderLock")]
    private static extern int ReadResampled(WinAudioBackend backend, Span<float> target, int requestedOutFrames);

    /// <summary>Сигнал после ресемплера 44.1 → 48 кГц не превышает потолок лимитера, стоящего до него.</summary>
    [TestMethod(TestCategory.Unit, "Resampled Device Output Stays Under Limiter Ceiling (44.1 -> 48 kHz)", Group = TestGroups.Audio, Order = 19)]
    [SupportedOSPlatform("windows")]
    public static async Task TestResampledOutputRespectsLimiterCeilingAsync()
    {
        if (!OperatingSystem.IsWindows())
            TestAssert.Skip("Windows-only WASAPI resampler test");

        const int toneFrames = 8_192;
        const int deviceFrames = 480;
        const int ticks = 12;
        const int warmupTicks = 2;

        // fs/4 с фазой π/4: отсчёты равны ±0.99, а непрерывная волна между ними достигает 1.4 — лимитер видит только отсчёты
        float[] tone = new float[toneFrames * Channels];
        for (int n = 0; n < toneFrames; n++)
        {
            float s = 1.4f * MathF.Sin(MathF.PI * n / 2f + MathF.PI / 4f);
            tone[n * Channels] = s;
            tone[n * Channels + 1] = s;
        }

        var ring = new LockFreeRingBuffer<float>(tone.Length * 2);
        ring.Write(tone);

        var backend = new WinAudioBackend();
        var pipeline = AudioPipelineTestHarness.Create(
            new BlockingAudioSource(SourceRate, Channels),
            new ConstantDecoder(SourceRate, Channels, framesPerDecode: 1_024, maxFrameSize: 1_024, value: 0f),
            backend,
            ring);

        // Контрольная точка: пик того, что лимитер отдаёт на частоте источника
        float maxSource = 0f;
        CallbackField(backend) = buffer =>
        {
            int frames = AudioPipelineTestHarness.AudioCallback(pipeline, buffer);
            foreach (float sample in buffer[..(frames * Channels)])
                maxSource = MathF.Max(maxSource, MathF.Abs(sample));

            return frames;
        };

        ChannelsField(backend) = Channels;
        SourceSampleRateField(backend) = SourceRate;
        DeviceSampleRateField(backend) = DeviceRate;

        try
        {
            float[] device = new float[deviceFrames * Channels];
            float maxDevice = 0f;

            for (int tick = 0; tick < ticks; tick++)
            {
                int produced = ReadResampled(backend, device, deviceFrames);
                TestAssert.Equal(deviceFrames, produced, $"Resampler must fill device tick {tick}");

                // Первые тики история ресемплера заполнена повтором первого отсчёта и форма волны не показательна
                if (tick < warmupTicks)
                    continue;

                foreach (float sample in device)
                    maxDevice = MathF.Max(maxDevice, MathF.Abs(sample));
            }

            TestAssert.True(maxSource <= LimiterCeiling, $"Control: limiter output at the source rate must respect its ceiling, got {maxSource:F4}");
            TestAssert.True(maxDevice <= LimiterCeiling,
                $"Resampler creates inter-sample overs after the limiter: device peak {maxDevice:F4} > ceiling {LimiterCeiling:F3}");
        }
        finally
        {
            CallbackField(backend) = null;
            await pipeline.DisposeAsync().ConfigureAwait(false);
            backend.Dispose();
        }
    }

    /// <summary>Защитная кривая ресемплера прозрачна ниже колена, монотонна и асимптотически держит потолок лимитера.</summary>
    [TestMethod(TestCategory.Unit, "Resampler Soft Ceiling Guard Is Transparent Below Knee & Bounded Above", Group = TestGroups.Audio, Order = 19)]
    public static Task TestResamplerSoftCeilingGuardAsync()
    {
        // Ниже колена множитель обязан быть ровно единицей, иначе защита окрасит обычный сигнал
        foreach (float peak in (ReadOnlySpan<float>)[0f, 0.25f, 0.5f, 0.75f, LimiterKnee])
            TestAssert.Equal(1.0f, SoftCeilingScale(null, peak), $"Guard must be identity at peak {peak:F3}");

        // Сразу за коленом кривая продолжает прямую y = x без излома
        float nearKnee = LimiterKnee + 0.001f;
        float nearOut = nearKnee * SoftCeilingScale(null, nearKnee);
        TestAssert.True(MathF.Abs(nearOut - nearKnee) < 1e-5f, $"Guard must join y = x smoothly at the knee, got {nearOut:F6} for {nearKnee:F6}");

        float previous = LimiterKnee;
        for (int i = 1; i <= 4_000; i++)
        {
            float peak = LimiterKnee + i * 0.001f;
            float output = peak * SoftCeilingScale(null, peak);

            TestAssert.True(output <= LimiterCeiling, $"Guard output {output:F5} exceeds ceiling at peak {peak:F3}");
            // Допуск 1e-6 поглощает округление float у асимптоты, где прирост кривой меньше ulp
            TestAssert.True(output >= previous - 1e-6f, $"Guard curve must be monotonic: {output:F6} < {previous:F6} at peak {peak:F3}");
            previous = output;
        }

        return Task.CompletedTask;
    }
}