using System.Runtime.CompilerServices;

namespace LMP.Core.Audio;

/// <summary>
/// Thread-safe buffer for synchronizing instantaneous signal levels (3 macro bands + 10 ISO bands) between the audio stream and the UI.
/// </summary>
/// <remarks>
/// Each level read extends the reader lease by <see cref="ReaderLeaseMs"/> ms. The audio stream checks it via
/// <see cref="HasActiveReaders"/> and skips render-time spectrum analysis when visualization is not being polled.
/// </remarks>
public static class AudioVisualizerBuffer
{
    [InlineArray(10)]
    private struct BandLevels10
    {
        private float _element0;
    }

    /// <summary>
    /// Spectrum data staleness threshold (45 ms in Stopwatch ticks).
    /// If WASAPI is paused or awaiting network during Seek, reading instantly returns 0f,
    /// allowing the UI to decay smoothly without race conditions between threads.
    /// </summary>
    private static readonly long StaleThresholdTicks = System.Diagnostics.Stopwatch.Frequency * 45 / 1000;

    /// <summary>
    /// Reader lease duration in milliseconds: double the idle polling interval (250 ms) in <c>EqualizerCurveControl</c>,
    /// ensuring a single missed UI tick does not disable analysis.
    /// </summary>
    internal const int ReaderLeaseMs = 500;

    private static readonly long ReaderLeaseTicks = System.Diagnostics.Stopwatch.Frequency * ReaderLeaseMs / 1000;

    private static long _lastUpdateTimestamp;
    private static long _lastReadTimestamp;
    private static float _lowLevel;
    private static float _midLevel;
    private static float _highLevel;
    private static float _beatPulse;
    private static float _overallLevel;
    private static int _dominantBandIndex;
    private static BandLevels10 _bands10;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsActive()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        Volatile.Write(ref _lastReadTimestamp, now);

        long last = Volatile.Read(ref _lastUpdateTimestamp);
        return last != 0 && (now - last) <= StaleThresholdTicks;
    }

    /// <summary>
    /// Returns true if fresh audio frames were received from an active playback stream within the last 45 ms.
    /// Extends the reader lease, so UI idle polling automatically resumes spectrum analysis without deadlocks.
    /// </summary>
    public static bool HasRecentActivity => IsActive();

    /// <summary>
    /// Returns true if levels were read by anyone within the last <see cref="ReaderLeaseMs"/> ms.
    /// Intended for the audio stream and does not extend the lease itself.
    /// </summary>
    public static bool HasActiveReaders
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            long last = Volatile.Read(ref _lastReadTimestamp);
            return last != 0 && (System.Diagnostics.Stopwatch.GetTimestamp() - last) <= ReaderLeaseTicks;
        }
    }

    public static float LowLevel => IsActive() ? Volatile.Read(ref _lowLevel) : 0f;
    public static float MidLevel => IsActive() ? Volatile.Read(ref _midLevel) : 0f;
    public static float HighLevel => IsActive() ? Volatile.Read(ref _highLevel) : 0f;
    public static float BeatPulse => IsActive() ? Volatile.Read(ref _beatPulse) : 0f;
    public static float OverallLevel => IsActive() ? Volatile.Read(ref _overallLevel) : 0f;

    /// <summary>Index of the dominant ISO band [0..9]; reading extends the reader lease.</summary>
    public static int DominantBandIndex
    {
        get
        {
            Volatile.Write(ref _lastReadTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
            return Volatile.Read(ref _dominantBandIndex);
        }
    }

    /// <summary>
    /// Reads current levels of 10 ISO bands [0..1] into the provided Span with zero allocations.
    /// Automatically zeroes out the bands if the playback stream is paused or seeking.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CopyBands10(Span<float> destination)
    {
        int count = Math.Min(10, destination.Length);
        if (!IsActive())
        {
            destination[..count].Clear();
            return;
        }

        for (int i = 0; i < count; i++)
            destination[i] = Volatile.Read(ref _bands10[i]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Update10Band(
        ReadOnlySpan<float> bands10,
        float low,
        float mid,
        float high,
        float beat,
        float overall,
        int dominantBand)
    {
        int count = Math.Min(10, bands10.Length);
        for (int i = 0; i < count; i++)
            Volatile.Write(ref _bands10[i], bands10[i]);

        Volatile.Write(ref _lowLevel, low);
        Volatile.Write(ref _midLevel, mid);
        Volatile.Write(ref _highLevel, high);
        Volatile.Write(ref _beatPulse, beat);
        Volatile.Write(ref _overallLevel, overall);
        Volatile.Write(ref _dominantBandIndex, Math.Clamp(dominantBand, 0, 9));
        Volatile.Write(ref _lastUpdateTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Reset()
    {
        Volatile.Write(ref _lastUpdateTimestamp, 0L);

        for (int i = 0; i < 10; i++)
            Volatile.Write(ref _bands10[i], 0f);

        Volatile.Write(ref _lowLevel, 0f);
        Volatile.Write(ref _midLevel, 0f);
        Volatile.Write(ref _highLevel, 0f);
        Volatile.Write(ref _beatPulse, 0f);
        Volatile.Write(ref _overallLevel, 0f);
        Volatile.Write(ref _dominantBandIndex, 0);
    }
}