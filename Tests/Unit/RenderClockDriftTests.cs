using System.Diagnostics;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Interfaces;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Advances render clock under device clock drift relative to QPC: lead is capped to tolerance, time is monotonic.
/// </summary>
public static class RenderClockDriftTests
{
    private const int SamplesPerSecond = 96_000;
    private const int QueueSamples = 4_800;
    private const int Publishes = 12_000;
    private const double SlowDevicePpm = 1_000.0;
    private const double MaxLeadMs = RenderClock.MaxCarryLeadMs + 1.0;

    /// <summary>Device is slower than QPC by 1000 ppm: advance lead does not grow to queue depth under steady step or jitter.</summary>
    [TestMethod(TestCategory.Unit, "RenderClock Carry-Over Converges Under Device Drift", Group = TestGroups.Audio, Order = 43)]
    public static Task TestRenderClockDriftConvergesAsync()
    {
        AssertLeadBounded(jitter: false);
        AssertLeadBounded(jitter: true);
        return Task.CompletedTask;
    }

    private static void AssertLeadBounded(bool jitter)
    {
        var clock = new RenderClock();
        long frequency = Stopwatch.Frequency;
        long periodTicks = frequency / 100;
        long now = frequency;
        double elapsedSeconds = 0;
        uint state = 0x2545F491u;
        long lastRead = long.MinValue;
        double maxLeadMs = 0;

        for (int i = 0; i < Publishes; i++)
        {
            long step = periodTicks;
            if (jitter)
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                step += (long)(state % (uint)(periodTicks / 5)) - periodTicks / 10;
            }

            now += step;
            elapsedSeconds += (double)step / frequency;

            long actual = (long)(elapsedSeconds * SamplesPerSecond * (1.0 - SlowDevicePpm * 1e-6));
            clock.Publish(actual + QueueSamples, QueueSamples, now, SamplesPerSecond);

            RenderClockSnapshot snapshot = clock.Read();
            long read = RenderClock.Extrapolate(in snapshot, now);
            TestAssert.True(read >= lastRead, $"Reader time must never decrease (jitter={jitter}, publish={i})");
            lastRead = read;

            if (i >= Publishes - 1_000)
                maxLeadMs = Math.Max(maxLeadMs, (read - actual) * 1000.0 / SamplesPerSecond);
        }

        TestAssert.True(maxLeadMs <= MaxLeadMs,
            $"Carry-over lead must stay within {MaxLeadMs:F0} ms under {SlowDevicePpm:F0} ppm drift (jitter={jitter}), got {maxLeadMs:F2} ms");
    }
}