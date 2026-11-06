using System.Diagnostics;
using System.Runtime.CompilerServices;
using LMP.Core.Audio.Backends;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Interfaces;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Single-writer render clock contracts: SeqLock snapshot consistency under concurrent reads,
/// monotonicity, device queue extrapolation limits, and stream position rebinding to the render counter on seek.
/// </summary>
public static class RenderClockTests
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int SamplesPerSecond = SampleRate * Channels;
    private const int SamplesPerTick = SamplesPerSecond / 100; // 10 ms

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ConsumeTick")]
    private static extern void ConsumeTick(NullAudioBackend backend, float[] buffer, int samplesPerSecond);

    /// <summary>Shared state of the writer and readers for the concurrency test.</summary>
    private sealed class ConcurrencyProbe
    {
        public volatile bool WriterDone;
        public int StartedReaders;
        public long Reads;
        public string? Failure;
    }

    /// <summary>Readers under continuous publication see only complete snapshots and non-decreasing time.</summary>
    [TestMethod(TestCategory.Unit, "RenderClock SeqLock Snapshots Stay Consistent Under Concurrent Reads", Group = TestGroups.Audio, Order = 40, TimeoutSeconds = 60)]
    public static async Task TestRenderClockConcurrentSnapshotsAsync()
    {
        const int publishes = 200_000;
        const int readerCount = 3;

        var clock = new RenderClock();
        var probe = new ConcurrencyProbe();
        long baseTimestamp = 1_000_000;
        long step = Stopwatch.Frequency / 100;

        var workers = new Task[readerCount + 1];
        for (int r = 0; r < readerCount; r++)
            workers[r] = Task.Run(() => ReadUntilWriterDone(clock, probe, baseTimestamp, step, publishes));

        await AudioPipelineTestHarness.WaitUntilAsync(() => Volatile.Read(ref probe.StartedReaders) == readerCount, 2_000).ConfigureAwait(false);

        // Snapshot i: i ticks written, one tick still in the device queue; sample rate encodes the snapshot index
        workers[readerCount] = Task.Run(() =>
        {
            for (int i = 1; i <= publishes; i++)
                clock.Publish((long)SamplesPerTick * i, SamplesPerTick, baseTimestamp + i * step, SamplesPerSecond + i % 7);

            probe.WriterDone = true;
        });

        await Task.WhenAll(workers).ConfigureAwait(false);

        TestAssert.Null(Volatile.Read(ref probe.Failure), probe.Failure);
        TestAssert.True(Interlocked.Read(ref probe.Reads) > 0, "Readers must observe published snapshots");

        RenderClockSnapshot last = clock.Read();
        TestAssert.Equal((long)SamplesPerTick * publishes, last.PlayedSamples + last.QueuedSamples, "Final snapshot must cover every rendered sample");
    }

    /// <summary>Extrapolation is limited by the device queue; starvation freezes position; counter glitch does not roll it back.</summary>
    [TestMethod(TestCategory.Unit, "RenderClock Extrapolation Bounded By Device Queue & Never Regresses", Group = TestGroups.Audio, Order = 41)]
    public static Task TestRenderClockBoundsAsync()
    {
        long frequency = Stopwatch.Frequency;
        const long t0 = 1_000_000;
        var clock = new RenderClock();

        // 1. 9600 samples written, 4800 still in the device buffer
        clock.Publish(9_600, 4_800, t0, SamplesPerSecond);
        RenderClockSnapshot snapshot = clock.Read();
        TestAssert.Equal(4_800L, snapshot.PlayedSamples);
        TestAssert.Equal(4_800, snapshot.QueuedSamples);

        // 2. +25 ms: exactly 2400 samples of progress, but not beyond the end of the queue
        long after25Ms = RenderClock.Extrapolate(in snapshot, t0 + frequency / 40);
        TestAssert.True(Math.Abs(after25Ms - 7_200) <= 1, $"25 ms must advance 2400 samples, got {after25Ms - 4_800}");
        TestAssert.Equal(9_600L, RenderClock.Extrapolate(in snapshot, t0 + 10 * frequency), "Extrapolation must stop at the end of the device queue");

        // 3. Starvation: queue is empty — position remains at the boundary of written samples
        clock.Publish(9_600, 0, t0 + 10 * frequency, SamplesPerSecond);
        RenderClockSnapshot starved = clock.Read();
        TestAssert.Equal(9_600L, starved.PlayedSamples);
        TestAssert.Equal(9_600L, RenderClock.Extrapolate(in starved, t0 + 20 * frequency), "Starved clock must not extrapolate");

        // 4. New block after starvation with a deep queue: progress carryover prevents position rollback
        clock.Publish(10_560, 4_800, t0 + 10 * frequency + frequency / 200, SamplesPerSecond);
        RenderClockSnapshot resumed = clock.Read();
        TestAssert.Equal(9_600L, resumed.PlayedSamples, "Carry-over must keep the position reached before starvation");
        TestAssert.Equal(960, resumed.QueuedSamples);

        // 5. Counter glitch (counter goes backwards): writer does not lower the position
        clock.Publish(5_000, 0, t0 + 11 * frequency, SamplesPerSecond);
        TestAssert.True(clock.Read().PlayedSamples >= resumed.PlayedSamples, "Render clock must never move backwards");

        // 6. Unknown sample rate: no extrapolation
        var unknownRate = new RenderClock();
        unknownRate.Publish(9_600, 4_800, t0, 0);
        RenderClockSnapshot noRate = unknownRate.Read();
        TestAssert.Equal(4_800L, RenderClock.Extrapolate(in noRate, t0 + frequency), "Zero rate must disable extrapolation");

        return Task.CompletedTask;
    }

    /// <summary>Seek rebinds position to the render counter: audio already written to the device is not counted toward the new position.</summary>
    [TestMethod(TestCategory.Unit, "RenderClock Seek Rebases Stream Position On Rendered Counter", Group = TestGroups.Audio, Order = 42)]
    public static async Task TestRenderClockSeekRebaseAsync()
    {
        TestAssert.Equal(777L, RenderClock.MapToStream(1_000, 1_500, 777), "Samples rendered before the origin belong to the previous position");
        TestAssert.Equal(1_277L, RenderClock.MapToStream(2_000, 1_500, 777));

        const int blockSamples = 2_048;
        var backend = new NullAudioBackend();
        var ring = new LockFreeRingBuffer<float>(16_384);
        float[] prefill = new float[8_192];
        Array.Fill(prefill, 0.25f);
        ring.Write(prefill);

        var pipeline = AudioPipelineTestHarness.Create(
            new BlockingAudioSource(SampleRate, Channels),
            new ConstantDecoder(SampleRate, Channels, framesPerDecode: 960, maxFrameSize: 960, value: 0.25f),
            backend,
            ring,
            new AudioStreamInfo { DurationMs = 180_000 });

        backend.Initialize(SampleRate, Channels, buffer => AudioPipelineTestHarness.AudioCallback(pipeline, buffer));
        float[] block = new float[blockSamples];

        try
        {
            // 1. Without a device queue (NullBackend), exactly what was written was played
            ConsumeTick(backend, block, SamplesPerSecond);
            TestAssert.Equal((long)blockSamples, pipeline.PlayedSamples);

            // 2. Seek to 60 s: audio written before rebinding does not advance the new position
            const long seekBase = 60L * SamplesPerSecond;
            pipeline.SetDecodedSamplesPosition(seekBase);
            TestAssert.Equal(seekBase, pipeline.PlayedSamples, "Seek must rebase the clock without counting already rendered samples");

            // 3. Next block advances the position by exactly its length
            ConsumeTick(backend, block, SamplesPerSecond);
            TestAssert.Equal(seekBase + blockSamples, pipeline.PlayedSamples);

            double expectedSeconds = (double)(seekBase + blockSamples) / SamplesPerSecond;
            double actualSeconds = pipeline.GetPlaybackPosition().TotalSeconds;
            TestAssert.True(Math.Abs(actualSeconds - expectedSeconds) < 1e-6, $"Position {actualSeconds:F6}s must equal {expectedSeconds:F6}s");
        }
        finally
        {
            await pipeline.DisposeAsync().ConfigureAwait(false);
            backend.Dispose();
        }
    }

    private static void ReadUntilWriterDone(RenderClock clock, ConcurrencyProbe probe, long baseTimestamp, long step, int publishes)
    {
        Interlocked.Increment(ref probe.StartedReaders);

        long lastPlayed = 0;
        long lastTimestamp = 0;
        long reads = 0;

        while (!probe.WriterDone && Volatile.Read(ref probe.Failure) == null)
        {
            RenderClockSnapshot s = clock.Read();
            if (s.Timestamp == 0)
                continue;

            reads++;
            long offset = s.Timestamp - baseTimestamp;
            long index = offset / step;
            bool consistent = offset % step == 0
                && index >= 1 && index <= publishes
                && s.PlayedSamples == SamplesPerTick * (index - 1)
                && s.QueuedSamples == SamplesPerTick
                && s.SamplesPerSecond == SamplesPerSecond + (int)(index % 7);

            if (!consistent)
            {
                Interlocked.CompareExchange(ref probe.Failure,
                    $"Torn snapshot: played={s.PlayedSamples}, queued={s.QueuedSamples}, ts={s.Timestamp}, sps={s.SamplesPerSecond}", null);
                break;
            }

            if (s.PlayedSamples < lastPlayed || s.Timestamp < lastTimestamp)
            {
                Interlocked.CompareExchange(ref probe.Failure,
                    $"Non-monotonic snapshot: played {lastPlayed} -> {s.PlayedSamples}, ts {lastTimestamp} -> {s.Timestamp}", null);
                break;
            }

            lastPlayed = s.PlayedSamples;
            lastTimestamp = s.Timestamp;
        }

        Interlocked.Add(ref probe.Reads, reads);
    }
}