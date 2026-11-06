using System.Diagnostics;
using System.Runtime.CompilerServices;
using LMP.Core.Audio.Interfaces;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// Single-writer render clock based on SeqLock: the device thread publishes how many samples were rendered,
/// how many are queued in the device, and when the reading was taken, while any thread reads the snapshot lock-free and allocation-free.
/// </summary>
internal sealed class RenderClock
{
    /// <summary>Allowed carry lead over measurement: WASAPI engine period (10 ms) plus margin.</summary>
    internal const int MaxCarryLeadMs = 12;

    /// <summary>Clock deceleration divisor when narrowing the lead: clock rate is reduced by 1/64 (approx. 1.6%).</summary>
    private const int SlewDivisor = 64;

    private long _version;
    private long _playedSamples;
    private int _queuedSamples;
    private long _timestamp;
    private int _samplesPerSecond;

    /// <summary>Last published snapshot; owned by the writer thread.</summary>
    private RenderClockSnapshot _lastPublished;

    /// <summary>
    /// Publishes device readings. Called exclusively by the single writer—the render thread.
    /// If the carry lead exceeds <see cref="MaxCarryLeadMs"/>, the snapshot is assigned a decelerated rate.
    /// </summary>
    /// <param name="renderedSamples">Monotonically non-decreasing count of source samples at the end of the segment the device will play sequentially.</param>
    /// <param name="queuedSamples">Source samples for this segment still awaiting playback in the device buffer.</param>
    /// <param name="timestamp">Point in time when readings were taken, in <see cref="Stopwatch"/> ticks.</param>
    /// <param name="samplesPerSecond">Source sample rate × channel count.</param>
    public void Publish(long renderedSamples, int queuedSamples, long timestamp, int samplesPerSecond)
    {
        RenderClockSnapshot last = _lastPublished;
        long rendered = Math.Max(renderedSamples, last.PlayedSamples);
        long measured = rendered - Math.Max(queuedSamples, 0);
        long played = measured;
        int rate = samplesPerSecond;

        // Rate carry: the new snapshot never drops below what readers could have already observed from the old one
        long carried = Math.Min(Extrapolate(in last, timestamp), rendered);
        if (carried > played)
        {
            played = carried;

            // Device clock drift accumulates lead: beyond tolerance, rate decelerates while preserving monotonicity
            long maxLead = (long)samplesPerSecond * MaxCarryLeadMs / 1000;
            if (carried - measured > maxLead)
                rate = samplesPerSecond - samplesPerSecond / SlewDivisor;
        }

        int queued = (int)Math.Min(rendered - played, int.MaxValue);

        long open = Volatile.Read(ref _version) | 1L;
        Volatile.Write(ref _version, open);
        Interlocked.MemoryBarrier();

        _playedSamples = played;
        _queuedSamples = queued;
        _timestamp = timestamp;
        _samplesPerSecond = rate;

        Volatile.Write(ref _version, open + 1);
        _lastPublished = new RenderClockSnapshot(played, queued, timestamp, rate);
    }

    /// <summary>
    /// Returns a consistent snapshot; retries reading if a concurrent publish occurs.
    /// </summary>
    public RenderClockSnapshot Read()
    {
        SpinWait spinner = default;

        while (true)
        {
            long seq0 = Volatile.Read(ref _version);
            if ((seq0 & 1L) != 0)
            {
                spinner.SpinOnce();
                continue;
            }

            long played = Volatile.Read(ref _playedSamples);
            int queued = Volatile.Read(ref _queuedSamples);
            long timestamp = Volatile.Read(ref _timestamp);
            int samplesPerSecond = Volatile.Read(ref _samplesPerSecond);

            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref _version) == seq0)
                return new RenderClockSnapshot(played, queued, timestamp, samplesPerSecond);
        }
    }

    /// <summary>
    /// Extrapolates rendered samples from the snapshot to <paramref name="now"/>, bounded by the device queue.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Extrapolate(in RenderClockSnapshot snapshot, long now)
    {
        long elapsed = now - snapshot.Timestamp;
        if (elapsed <= 0 || snapshot.QueuedSamples <= 0 || snapshot.SamplesPerSecond <= 0)
            return snapshot.PlayedSamples;

        double advanced = (double)elapsed * snapshot.SamplesPerSecond / Stopwatch.Frequency;
        return snapshot.PlayedSamples + (long)Math.Min(advanced, snapshot.QueuedSamples);
    }

    /// <summary>
    /// Translates clock readings into stream position: <paramref name="baseSamples"/> plus samples rendered since the anchor point.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long MapToStream(long playedSamples, long originRenderedSamples, long baseSamples) =>
        baseSamples + Math.Max(0L, playedSamples - originRenderedSamples);
}