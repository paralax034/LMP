using System.Runtime.CompilerServices;

namespace LMP.Core.Audio;

/// <summary>
/// Потокобезопасный буфер для синхронизации мгновенных уровней сигнала между аудио-потоком и UI.
/// </summary>
public static class AudioVisualizerBuffer
{
    private static float _lowLevel;
    private static float _midLevel;
    private static float _highLevel;
    private static float _beatPulse;
    private static float _overallLevel;

    public static float LowLevel => Volatile.Read(ref _lowLevel);
    public static float MidLevel => Volatile.Read(ref _midLevel);
    public static float HighLevel => Volatile.Read(ref _highLevel);
    public static float BeatPulse => Volatile.Read(ref _beatPulse);
    public static float OverallLevel => Volatile.Read(ref _overallLevel);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Update3Band(float low, float mid, float high, float beat, float overall)
    {
        Volatile.Write(ref _lowLevel, low);
        Volatile.Write(ref _midLevel, mid);
        Volatile.Write(ref _highLevel, high);
        Volatile.Write(ref _beatPulse, beat);
        Volatile.Write(ref _overallLevel, overall);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Reset()
    {
        Volatile.Write(ref _lowLevel, 0f);
        Volatile.Write(ref _midLevel, 0f);
        Volatile.Write(ref _highLevel, 0f);
        Volatile.Write(ref _beatPulse, 0f);
        Volatile.Write(ref _overallLevel, 0f);
    }
}