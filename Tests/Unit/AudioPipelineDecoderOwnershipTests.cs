using LMP.Core.Audio.Backends;
using LMP.Core.Audio.Helpers;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Ownership of the <see cref="AudioPipeline"/> decoding loop: a stop timeout does not spawn a second SPSC ring producer,
/// a frame read after cancellation does not enter the ring, and the decoded samples counter matches the actual written amount.
/// The source ignores cancellation, simulating a read hung in a socket.
/// </summary>
public static class AudioPipelineDecoderOwnershipTests
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;

    /// <summary>After a stop timeout, a restart does not launch a second loop on top of the still-alive first loop.</summary>
    [TestMethod(TestCategory.Unit, "Decoder Stop Timeout Never Spawns A Second Loop", Group = TestGroups.Audio, Order = 16, TimeoutSeconds = 10)]
    public static async Task TestStopTimeoutKeepsSingleDecoderLoopAsync()
    {
        var source = new BlockingAudioSource(SampleRate, Channels);
        var backend = new NullAudioBackend();
        var pipeline = CreatePipeline(source, backend, new LockFreeRingBuffer<float>(SampleRate * Channels));
        try
        {
            var options = new AudioPlayerOptions();
            pipeline.StartDecoding(options, null, null);
            TestAssert.True(await AudioPipelineTestHarness.WaitUntilAsync(() => source.ReadCalls >= 1, 2_000).ConfigureAwait(false), "Decoder loop must start reading");

            // Read hangs and ignores cancellation: stopping must hit the timeout
            await pipeline.StopDecodingAsync(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);

            try
            {
                pipeline.StartDecoding(options, null, null);
            }
            catch (InvalidOperationException)
            {
                // Explicit rejection while the previous loop is still alive also respects the single-producer contract
            }

            await AudioPipelineTestHarness.WaitUntilAsync(() => source.ReadCalls >= 2, 300).ConfigureAwait(false);
            TestAssert.Equal(1, source.MaxConcurrentReads, "Only one decoder loop may read the source and write the SPSC ring");
        }
        finally
        {
            await ShutdownAsync(pipeline, source, backend).ConfigureAwait(false);
        }
    }

    /// <summary>A frame returned by a hung read after cancellation has been requested is discarded.</summary>
    [TestMethod(TestCategory.Unit, "Frame Read After Cancellation Never Reaches The Ring", Group = TestGroups.Audio, Order = 17, TimeoutSeconds = 10)]
    public static async Task TestFrameAfterCancellationIsDroppedAsync()
    {
        var source = new BlockingAudioSource(SampleRate, Channels);
        var backend = new NullAudioBackend();
        var ring = new LockFreeRingBuffer<float>(SampleRate * Channels);
        var pipeline = CreatePipeline(source, backend, ring);
        try
        {
            pipeline.StartDecoding(new AudioPlayerOptions(), null, null);
            TestAssert.True(await AudioPipelineTestHarness.WaitUntilAsync(() => source.ReadCalls >= 1, 2_000).ConfigureAwait(false), "Decoder loop must start reading");

            // Cancellation is already signaled when the hung read finally returns a frame
            Task stop = pipeline.StopDecodingAsync(TimeSpan.FromSeconds(2));
            source.Release(AudioPipelineTestHarness.Frame());
            await stop.ConfigureAwait(false);

            TestAssert.Equal(0, ring.Count, "A frame read after cancellation must not be written into the ring");
        }
        finally
        {
            await ShutdownAsync(pipeline, source, backend).ConfigureAwait(false);
        }
    }

    /// <summary>On partial ring writes, the decoded samples counter increments strictly by the amount accepted by the ring.</summary>
    [TestMethod(TestCategory.Unit, "Decoded Sample Counter Matches Ring Writes On Partial Write", Group = TestGroups.Audio, Order = 18, TimeoutSeconds = 10)]
    public static async Task TestDecodedCounterMatchesPartialWriteAsync()
    {
        const int framesPerDecode = 4_096;

        var source = new BlockingAudioSource(SampleRate, Channels);
        var backend = new NullAudioBackend();

        // Ring capacity is 1023 samples, while the decoder yields 8192 with declared MaxFrameSize 64: Write will accept only a portion
        var ring = new LockFreeRingBuffer<float>(1_024);
        var pipeline = AudioPipelineTestHarness.Create(
            source,
            new ConstantDecoder(SampleRate, Channels, framesPerDecode, maxFrameSize: 64, value: 0.1f),
            backend,
            ring);
        try
        {
            source.Release(AudioPipelineTestHarness.Frame());
            pipeline.StartDecoding(new AudioPlayerOptions(), null, null);

            TestAssert.True(
                await AudioPipelineTestHarness.WaitUntilAsync(() => ReadDecodedSamples(pipeline) > 0, 2_000).ConfigureAwait(false),
                "Decoder must process the released frame");

            long decoded = ReadDecodedSamples(pipeline);
            TestAssert.Equal((long)ring.Count, decoded,
                $"Decoded counter must equal samples accepted by the ring: ring={ring.Count}, counter={decoded}, produced={framesPerDecode * Channels}");
        }
        finally
        {
            await ShutdownAsync(pipeline, source, backend).ConfigureAwait(false);
        }
    }

    private static AudioPipeline CreatePipeline(BlockingAudioSource source, NullAudioBackend backend, LockFreeRingBuffer<float> ring) =>
        AudioPipelineTestHarness.Create(
            source,
            new ConstantDecoder(SampleRate, Channels, framesPerDecode: 960, maxFrameSize: 960, value: 0.1f),
            backend,
            ring);

    private static long ReadDecodedSamples(AudioPipeline pipeline) =>
        Interlocked.Read(ref AudioPipelineTestHarness.DecodedSamples(pipeline));

    private static async Task ShutdownAsync(AudioPipeline pipeline, BlockingAudioSource source, NullAudioBackend backend)
    {
        // Unblock hung reads so the orphaned loop terminates before the ring is disposed
        source.Open();
        await AudioPipelineTestHarness.WaitUntilAsync(() => source.ActiveReads == 0, 1_000).ConfigureAwait(false);
        await pipeline.StopDecodingAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        await pipeline.DisposeAsync().ConfigureAwait(false);
        backend.Dispose();
    }
}