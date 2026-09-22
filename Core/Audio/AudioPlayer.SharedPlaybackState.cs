namespace LMP.Core.Audio;

public partial class AudioPlayer
{
    /// <summary>
    /// Неизменяемый снимок состояния физического воспроизведения для атомарного обмена ссылками.
    /// </summary>
    /// <param name="BaseSamples">Количество фактически воспроизведенных сэмплов драйвером.</param>
    /// <param name="BufferedSamples">Количество сэмплов, находящихся в буфере звукового устройства.</param>
    /// <param name="BaseTimestamp">Временная метка фиксации отсчета (Stopwatch ticks).</param>
    /// <param name="SampleRate">Частота дискретизации аудиопотока.</param>
    /// <param name="Channels">Число аудиоканалов.</param>
    /// <param name="IsPlaying">Флаг активного физического воспроизведения.</param>
    /// <param name="DurationMs">Общая длительность трека в миллисекундах.</param>
    private sealed record PlaybackSnapshot(
        long BaseSamples,
        int BufferedSamples,
        long BaseTimestamp,
        int SampleRate,
        int Channels,
        bool IsPlaying,
        long DurationMs);

    /// <summary>
    /// Атомарное, сверхбыстрое lock-free хранилище для бесшовной интерполяции положения воспроизведения.
    /// Позволяет UI-потоку "вытягивать" позицию на любой частоте (например, 60 FPS) с идеальной плавностью.
    /// </summary>
    private sealed class SharedPlaybackState
    {
        private PlaybackSnapshot _snapshot = new(0, 0, 0, 0, 0, false, 0);
        private double _lastReturnedSeconds;

        /// <summary>
        /// Атомарно обновляет базовые показатели физического воспроизведения.
        /// </summary>
        /// <param name="baseSamples">Сырые воспроизведенные сэмплы за вычетом буфера драйвера.</param>
        /// <param name="bufferedSamples">Сэмплы в кольцевом буфере бэкенда.</param>
        /// <param name="sampleRate">Частота дискретизации.</param>
        /// <param name="channels">Количество каналов.</param>
        /// <param name="isPlaying">Флаг воспроизведения.</param>
        /// <param name="durationMs">Длительность текущего трека.</param>
        /// <remarks>
        /// Создает согласованный неизменяемый снимок данных и подменяет ссылку одной атомарной инструкцией,
        /// полностью исключая эффект разорванного чтения (Torn Reads) со стороны UI-потока.
        /// </remarks>
        public void Update(long baseSamples, int bufferedSamples, int sampleRate, int channels, bool isPlaying, long durationMs)
        {
            var nextSnapshot = new PlaybackSnapshot(
                baseSamples,
                bufferedSamples,
                System.Diagnostics.Stopwatch.GetTimestamp(),
                sampleRate,
                channels,
                isPlaying,
                durationMs);

            Volatile.Write(ref _snapshot, nextSnapshot);
        }

        /// <summary>
        /// Возвращает экстраполированное монотонное время с защитой от микро-вибраций таймера
        /// </summary>
        /// <returns>Вычисленная текущая позиция воспроизведения.</returns>
        public TimeSpan GetCurrentPosition()
        {
            var snap = Volatile.Read(ref _snapshot);

            if (snap.SampleRate <= 0 || snap.Channels <= 0) return TimeSpan.Zero;

            double baseSeconds = (double)snap.BaseSamples / (snap.SampleRate * snap.Channels);
            double maxExtrapolation = baseSeconds + ((double)snap.BufferedSamples / (snap.SampleRate * snap.Channels));
            double maxSeconds = snap.DurationMs / 1000.0;

            if (!snap.IsPlaying)
            {
                double finalSec = Math.Clamp(baseSeconds, 0.0, maxSeconds);
                _lastReturnedSeconds = finalSec;
                return TimeSpan.FromSeconds(finalSec);
            }

            long currentTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            double elapsedSeconds = (double)(currentTimestamp - snap.BaseTimestamp) / System.Diagnostics.Stopwatch.Frequency;
            double extrapolated = baseSeconds + elapsedSeconds;

            // HARD CLAMP: Prevent slider ghosting during network starvation.
            // Extrapolation cannot exceed the amount of actual audio data resident in hardware buffer.
            if (extrapolated > maxExtrapolation) extrapolated = maxExtrapolation;

            if (extrapolated > maxSeconds) extrapolated = maxSeconds;
            if (extrapolated < 0.0) extrapolated = 0.0;

            // Clock Jitter Guard
            if (extrapolated < _lastReturnedSeconds && extrapolated >= _lastReturnedSeconds - 0.2)
            {
                extrapolated = _lastReturnedSeconds;
            }

            _lastReturnedSeconds = extrapolated;
            return TimeSpan.FromSeconds(extrapolated);
        }
    }
}