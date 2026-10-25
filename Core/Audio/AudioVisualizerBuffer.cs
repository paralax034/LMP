using System.Runtime.CompilerServices;

namespace LMP.Core.Audio;

/// <summary>
/// Потокобезопасный буфер для синхронизации мгновенных уровней сигнала (3 макро-полосы + 10 ISO-полос) между аудио-потоком и UI.
/// </summary>
public static class AudioVisualizerBuffer
{
    [InlineArray(10)]
    private struct BandLevels10
    {
        private float _element0;
    }

    private static float _lowLevel;
    private static float _midLevel;
    private static float _highLevel;
    private static float _beatPulse;
    private static float _overallLevel;
    private static int _dominantBandIndex;
    private static BandLevels10 _bands10;

    public static float LowLevel => Volatile.Read(ref _lowLevel);
    public static float MidLevel => Volatile.Read(ref _midLevel);
    public static float HighLevel => Volatile.Read(ref _highLevel);
    public static float BeatPulse => Volatile.Read(ref _beatPulse);
    public static float OverallLevel => Volatile.Read(ref _overallLevel);
    public static int DominantBandIndex => Volatile.Read(ref _dominantBandIndex);

    /// <summary>
    /// Читает текущие уровни 10 ISO-полос [0..1] в переданный Span без аллокаций.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CopyBands10(Span<float> destination)
    {
        int count = Math.Min(10, destination.Length);
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
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Reset()
    {
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
