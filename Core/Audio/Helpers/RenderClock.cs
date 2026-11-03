using System.Diagnostics;
using System.Runtime.CompilerServices;
using LMP.Core.Audio.Interfaces;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// Часы рендера с единственным писателем на базе SeqLock: поток устройства публикует, сколько сэмплов прозвучало,
/// сколько ещё стоит в очереди устройства и когда сняты показания, а любые потоки читают снимок без блокировок и аллокаций.
/// </summary>
internal sealed class RenderClock
{
    /// <summary>Допустимое опережение переноса над измерением: период движка WASAPI (10 мс) плюс запас.</summary>
    internal const int MaxCarryLeadMs = 12;

    /// <summary>Делитель замедления хода при стягивании опережения: ход снижается на 1/64 (около 1,6 %).</summary>
    private const int SlewDivisor = 64;

    private long _version;
    private long _playedSamples;
    private int _queuedSamples;
    private long _timestamp;
    private int _samplesPerSecond;

    /// <summary>Последний опубликованный снимок; принадлежит потоку-писателю.</summary>
    private RenderClockSnapshot _lastPublished;

    /// <summary>
    /// Публикует показания устройства. Вызывается только единственным писателем — потоком рендера.
    /// При опережении переноса сверх <see cref="MaxCarryLeadMs"/> снимок получает замедленный ход.
    /// </summary>
    /// <param name="renderedSamples">Счётчик сэмплов источника на конце отрезка, который устройство проиграет подряд; не убывает.</param>
    /// <param name="queuedSamples">Сэмплы источника этого отрезка, ещё ожидающие воспроизведения в буфере устройства.</param>
    /// <param name="timestamp">Момент снятия показаний в тиках <see cref="Stopwatch"/>.</param>
    /// <param name="samplesPerSecond">Частота источника × число каналов.</param>
    public void Publish(long renderedSamples, int queuedSamples, long timestamp, int samplesPerSecond)
    {
        RenderClockSnapshot last = _lastPublished;
        long rendered = Math.Max(renderedSamples, last.PlayedSamples);
        long measured = rendered - Math.Max(queuedSamples, 0);
        long played = measured;
        int rate = samplesPerSecond;

        // Перенос хода: новый снимок не опускается ниже того, что читатели уже могли увидеть по старому
        long carried = Math.Min(Extrapolate(in last, timestamp), rendered);
        if (carried > played)
        {
            played = carried;

            // Дрейф часов устройства копит опережение: сверх допуска ход замедляется, монотонность сохраняется
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
    /// Возвращает согласованный снимок; при параллельной публикации повторяет чтение.
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
    /// Экстраполирует прозвучавшие сэмплы снимка на момент <paramref name="now"/>, не выходя за пределы очереди устройства.
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
    /// Переводит показания часов в позицию потока: <paramref name="baseSamples"/> плюс сэмплы, прозвучавшие после точки привязки.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long MapToStream(long playedSamples, long originRenderedSamples, long baseSamples) =>
        baseSamples + Math.Max(0L, playedSamples - originRenderedSamples);
}