using System.Diagnostics;
using System.Runtime.CompilerServices;
using LMP.Core.Audio.Backends;
using LMP.Core.Audio.Helpers;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Гейт спектра в аудио-callback'е: анализ идёт только при живой аренде читателя и сам возобновляется опросом UI,
/// сброс из чужого потока откладывается до аудио-потока, а уровни публикуются к моменту фактического звучания блока.
/// </summary>
public static class VisualizerSpectrumGateTests
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_analyzer3Band")]
    private static extern ref AudioSpectrumAnalyzer SpectrumAnalyzer(AudioPipeline pipeline);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_beatFollowerFast")]
    private static extern ref float BeatFollowerFast(AudioSpectrumAnalyzer analyzer);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_beatFollowerSlow")]
    private static extern ref float BeatFollowerSlow(AudioSpectrumAnalyzer analyzer);

    /// <summary>Без читателей callback отдаёт PCM, но не считает спектр; первый же опрос UI возобновляет анализ.</summary>
    [TestMethod(TestCategory.Unit, "Spectrum Analysis Suspends Without Readers & Resumes On Poll", Group = TestGroups.Audio, Order = 13)]
    public static async Task TestSpectrumGateFollowsReaderLeaseAsync()
    {
        // Аренда читателя глобальна: ждём её истечения; живой UI, опрашивающий уровни, делает проверку невозможной
        await Task.Delay(AudioVisualizerBuffer.ReaderLeaseMs + 100).ConfigureAwait(false);
        if (AudioVisualizerBuffer.HasActiveReaders)
            TestAssert.Skip("AudioVisualizerBuffer is polled by a live UI reader; the reader lease cannot expire");

        var (pipeline, backend) = CreatePipeline(CreateTone(4_096, 1_000f, 0.8f));
        try
        {
            float[] block = new float[2_048];

            int frames = AudioPipelineTestHarness.AudioCallback(pipeline, block);
            TestAssert.Equal(1_024, frames, "Callback must deliver PCM regardless of the spectrum gate");
            TestAssert.False(AudioVisualizerBuffer.HasRecentActivity, "Spectrum must not be computed while nobody reads it");

            // Чтение выше продлило аренду: следующий блок снова попадает в анализатор
            AudioPipelineTestHarness.AudioCallback(pipeline, block);
            TestAssert.True(AudioVisualizerBuffer.HasRecentActivity, "A UI poll must resume spectrum analysis on the next callback");

            float level = AudioVisualizerBuffer.OverallLevel;
            TestAssert.True(level > 0.1f, $"Resumed spectrum must reflect the tone, got {level:F3}");
        }
        finally
        {
            await pipeline.DisposeAsync().ConfigureAwait(false);
            backend.Dispose();
        }
    }

    /// <summary>Stop из чужого потока не трогает фильтры анализатора: сброс исполняет ближайший аудио-callback.</summary>
    [TestMethod(TestCategory.Unit, "Pipeline Stop Defers Spectrum Reset To The Audio Thread", Group = TestGroups.Audio, Order = 14)]
    public static async Task TestStopDefersSpectrumResetAsync()
    {
        await SkipIfForeignPublisherAsync().ConfigureAwait(false);

        var (pipeline, backend) = CreatePipeline(CreateTone(8_192, 60f, 0.8f));
        try
        {
            AudioSpectrumAnalyzer analyzer = SpectrumAnalyzer(pipeline);
            float[] block = new float[2_048];

            // Чтение уровней продлевает аренду: анализ включён
            _ = AudioVisualizerBuffer.OverallLevel;
            AudioPipelineTestHarness.AudioCallback(pipeline, block);

            float fast = BeatFollowerFast(analyzer);
            float slow = BeatFollowerSlow(analyzer);
            TestAssert.True(fast > 0f && slow > 0f, $"60 Hz tone must charge beat followers: fast={fast}, slow={slow}");

            // Stop из потока теста: опубликованные уровни гаснут сразу, а состояние фильтров принадлежит аудио-потоку
            pipeline.Stop();
            TestAssert.Equal(fast, BeatFollowerFast(analyzer), "Stop must not mutate analyzer state outside the audio callback");
            TestAssert.Equal(slow, BeatFollowerSlow(analyzer), "Stop must not mutate analyzer state outside the audio callback");
            TestAssert.False(AudioVisualizerBuffer.HasRecentActivity, "Stop must clear published levels immediately");

            // Ближайший callback исполняет отложенный сброс и анализирует свой блок с нулевого состояния
            AudioPipelineTestHarness.AudioCallback(pipeline, block);

            var fresh = new AudioSpectrumAnalyzer(SampleRate, Channels);
            fresh.ProcessBlock(block, Channels);
            TestAssert.Equal(BeatFollowerFast(fresh), BeatFollowerFast(analyzer), "Deferred reset must restart the fast follower from zero");
            TestAssert.Equal(BeatFollowerSlow(fresh), BeatFollowerSlow(analyzer), "Deferred reset must restart the slow follower from zero");
        }
        finally
        {
            await pipeline.DisposeAsync().ConfigureAwait(false);
            backend.Dispose();
        }
    }

    /// <summary>Уровни блока появляются, когда блок звучит, а не когда он передан в буфер устройства.</summary>
    [TestMethod(TestCategory.Unit, "Spectrum Levels Are Published At Presentation Time", Group = TestGroups.Audio, Order = 15)]
    public static async Task TestSpectrumPresentationDelayAsync()
    {
        await SkipIfForeignPublisherAsync().ConfigureAwait(false);
        AudioVisualizerBuffer.Reset();

        var analyzer = new AudioSpectrumAnalyzer(SampleRate, Channels);
        float[] loud = CreateTone(2_048, 1_000f, 0.8f);
        float[] silence = new float[2_048 * Channels];
        long delayTicks = Stopwatch.Frequency / 5; // 200 мс в буфере устройства

        // 1. Громкий блок ещё ждёт в очереди устройства: показывать его рано
        analyzer.ProcessBlock(loud, Channels, delayTicks);
        TestAssert.False(AudioVisualizerBuffer.HasRecentActivity, "Levels must not lead the audible output");

        // 2. Срок звучания прошёл: следующий блок публикует громкий кадр, а свой тихий ставит в очередь
        await Task.Delay(260).ConfigureAwait(false);
        analyzer.ProcessBlock(silence, Channels, delayTicks);

        float level = AudioVisualizerBuffer.OverallLevel;
        TestAssert.True(level > 0.5f, $"Due loud frame must be published once its presentation time passes, got {level:F3}");

        // 3. Без задержки текущий блок публикуется сразу, а очередь сбрасывается
        analyzer.ProcessBlock(silence, Channels);
        TestAssert.True(AudioVisualizerBuffer.OverallLevel < 0.01f, "Zero delay must publish the current block immediately");
    }

    private static async Task SkipIfForeignPublisherAsync()
    {
        // Буфер визуализатора глобален: живое воспроизведение перезаписало бы уровни посреди проверки
        await Task.Delay(60).ConfigureAwait(false);
        if (AudioVisualizerBuffer.HasRecentActivity)
            TestAssert.Skip("AudioVisualizerBuffer is being fed by active playback; global state would corrupt assertions");
    }

    private static (AudioPipeline Pipeline, NullAudioBackend Backend) CreatePipeline(float[] prefill)
    {
        var backend = new NullAudioBackend();
        var ring = new LockFreeRingBuffer<float>(prefill.Length * 2);
        ring.Write(prefill);

        var pipeline = AudioPipelineTestHarness.Create(
            new BlockingAudioSource(SampleRate, Channels),
            new ConstantDecoder(SampleRate, Channels, framesPerDecode: 960, maxFrameSize: 960, value: 0f),
            backend,
            ring);

        return (pipeline, backend);
    }

    private static float[] CreateTone(int frames, float frequencyHz, float amplitude)
    {
        var tone = new float[frames * Channels];
        for (int f = 0; f < frames; f++)
        {
            float s = amplitude * MathF.Sin(2f * MathF.PI * frequencyHz * f / SampleRate);
            tone[f * Channels] = s;
            tone[f * Channels + 1] = s;
        }

        return tone;
    }
}