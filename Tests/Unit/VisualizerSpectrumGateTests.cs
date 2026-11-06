using System.Diagnostics;
using System.Runtime.CompilerServices;
using LMP.Core.Audio.Backends;
using LMP.Core.Audio.Helpers;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Spectrum gate in the audio callback: analysis runs only with an active reader lease and self-resumes via UI polling,
/// resets from another thread are deferred to the audio thread, and levels are published when the block actually plays.
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

    /// <summary>Without readers, the callback outputs PCM without calculating the spectrum; the very first UI poll resumes analysis.</summary>
    [TestMethod(TestCategory.Unit, "Spectrum Analysis Suspends Without Readers & Resumes On Poll", Group = TestGroups.Audio, Order = 13)]
    public static async Task TestSpectrumGateFollowsReaderLeaseAsync()
    {
        // Reader lease is global: waiting for its expiration; active UI polling levels makes verification impossible
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

            // The read above extended the lease: the next block enters the analyzer again
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

    /// <summary>Stop from another thread does not touch analyzer filters: the reset is performed by the next audio callback.</summary>
    [TestMethod(TestCategory.Unit, "Pipeline Stop Defers Spectrum Reset To The Audio Thread", Group = TestGroups.Audio, Order = 14)]
    public static async Task TestStopDefersSpectrumResetAsync()
    {
        await SkipIfForeignPublisherAsync().ConfigureAwait(false);

        var (pipeline, backend) = CreatePipeline(CreateTone(8_192, 60f, 0.8f));
        try
        {
            AudioSpectrumAnalyzer analyzer = SpectrumAnalyzer(pipeline);
            float[] block = new float[2_048];

            // Reading levels extends the lease: analysis is enabled
            _ = AudioVisualizerBuffer.OverallLevel;
            AudioPipelineTestHarness.AudioCallback(pipeline, block);

            float fast = BeatFollowerFast(analyzer);
            float slow = BeatFollowerSlow(analyzer);
            TestAssert.True(fast > 0f && slow > 0f, $"60 Hz tone must charge beat followers: fast={fast}, slow={slow}");

            // Stop from the test thread: published levels clear immediately, while filter state belongs to the audio thread
            pipeline.Stop();
            TestAssert.Equal(fast, BeatFollowerFast(analyzer), "Stop must not mutate analyzer state outside the audio callback");
            TestAssert.Equal(slow, BeatFollowerSlow(analyzer), "Stop must not mutate analyzer state outside the audio callback");
            TestAssert.False(AudioVisualizerBuffer.HasRecentActivity, "Stop must clear published levels immediately");

            // The next callback performs the deferred reset and analyzes its block from a clean state
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

    /// <summary>Block levels appear when the block plays, not when it is submitted to the device buffer.</summary>
    [TestMethod(TestCategory.Unit, "Spectrum Levels Are Published At Presentation Time", Group = TestGroups.Audio, Order = 15)]
    public static async Task TestSpectrumPresentationDelayAsync()
    {
        await SkipIfForeignPublisherAsync().ConfigureAwait(false);
        AudioVisualizerBuffer.Reset();

        var analyzer = new AudioSpectrumAnalyzer(SampleRate, Channels);
        float[] loud = CreateTone(2_048, 1_000f, 0.8f);
        float[] silence = new float[2_048 * Channels];
        long delayTicks = Stopwatch.Frequency / 5; // 200 ms in the device buffer

        // 1. The loud block is still queued in the device buffer: too early to display it
        analyzer.ProcessBlock(loud, Channels, delayTicks);
        TestAssert.False(AudioVisualizerBuffer.HasRecentActivity, "Levels must not lead the audible output");

        // 2. Playback time has passed: the next block publishes the loud frame and queues its silent one
        await Task.Delay(260).ConfigureAwait(false);
        analyzer.ProcessBlock(silence, Channels, delayTicks);

        float level = AudioVisualizerBuffer.OverallLevel;
        TestAssert.True(level > 0.5f, $"Due loud frame must be published once its presentation time passes, got {level:F3}");

        // 3. Without delay, the current block is published immediately, and the queue is flushed
        analyzer.ProcessBlock(silence, Channels);
        TestAssert.True(AudioVisualizerBuffer.OverallLevel < 0.01f, "Zero delay must publish the current block immediately");
    }

    private static async Task SkipIfForeignPublisherAsync()
    {
        // Visualizer buffer is global: live playback would overwrite levels during verification
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