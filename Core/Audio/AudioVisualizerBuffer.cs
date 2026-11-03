using System.Runtime.CompilerServices;

namespace LMP.Core.Audio;

/// <summary>
/// Потокобезопасный буфер для синхронизации мгновенных уровней сигнала (3 макро-полосы + 10 ISO-полос) между аудио-потоком и UI.
/// </summary>
/// <remarks>
/// Каждое чтение уровней продлевает аренду читателя на <see cref="ReaderLeaseMs"/> мс. Аудио-поток проверяет её через
/// <see cref="HasActiveReaders"/> и не тратит время рендера на анализ спектра, пока визуализацию никто не опрашивает.
/// </remarks>
public static class AudioVisualizerBuffer
{
    [InlineArray(10)]
    private struct BandLevels10
    {
        private float _element0;
    }

    /// <summary>
    /// Порог устаревания данных спектра (45 мс в тиках Stopwatch).
    /// Если WASAPI стоит на паузе или ждёт сеть при Seek, чтение мгновенно возвращает 0f,
    /// позволяя UI плавно затухнуть без гонок между потоками.
    /// </summary>
    private static readonly long StaleThresholdTicks = System.Diagnostics.Stopwatch.Frequency * 45 / 1000;

    /// <summary>
    /// Длительность аренды читателя в миллисекундах: вдвое больше холостого опроса (250 мс) в <c>EqualizerCurveControl</c>,
    /// поэтому пропуск одного тика UI не выключает анализ.
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
    /// Возвращает true, если за последние 45 мс поступали свежие аудио-фреймы от активного потока воспроизведения.
    /// Продлевает аренду читателя, поэтому холостой опрос UI сам возобновляет анализ спектра без взаимной блокировки.
    /// </summary>
    public static bool HasRecentActivity => IsActive();

    /// <summary>
    /// Возвращает true, если уровни кто-либо читал за последние <see cref="ReaderLeaseMs"/> мс.
    /// Предназначено для аудио-потока и само аренду не продлевает.
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

    /// <summary>Индекс доминирующей ISO-полосы [0..9]; чтение продлевает аренду читателя.</summary>
    public static int DominantBandIndex
    {
        get
        {
            Volatile.Write(ref _lastReadTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
            return Volatile.Read(ref _dominantBandIndex);
        }
    }

    /// <summary>
    /// Читает текущие уровни 10 ISO-полос [0..1] в переданный Span без аллокаций.
    /// Автоматически обнуляет полосы, если поток воспроизведения стоит на паузе или в Seek.
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