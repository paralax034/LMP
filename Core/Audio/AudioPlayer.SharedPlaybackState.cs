using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace LMP.Core.Audio;

public partial class AudioPlayer
{
    /// <summary>
    /// Атомарное lock-free хранилище для бесшовной интерполяции положения воспроизведения на базе SeqLock.
    /// Позволяет UI-потоку опрашивать позицию на любой частоте (60–240 FPS) без аллокаций в куче и разорванного чтения (Torn Reads).
    /// </summary>
    internal sealed class SharedPlaybackState
    {
        private long _version;
        private long _baseSamples;
        private int _bufferedSamples;
        private long _baseTimestamp;
        private int _sampleRate;
        private int _channels;
        private bool _isPlaying;
        private long _durationMs;

        private double _lastReturnedSeconds;

        /// <summary>
        /// Атомарно обновляет базовые показатели физического воспроизведения через чётный/нечётный счётчик поколений без аллокаций в куче.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Update(long baseSamples, int bufferedSamples, int sampleRate, int channels, bool isPlaying, long durationMs)
        {
            long timestamp = Stopwatch.GetTimestamp();

            long seq = Volatile.Read(ref _version);
            Volatile.Write(ref _version, seq + 1);

            _baseSamples = baseSamples;
            _bufferedSamples = bufferedSamples;
            _baseTimestamp = timestamp;
            _sampleRate = sampleRate;
            _channels = channels;
            _isPlaying = isPlaying;
            _durationMs = durationMs;

            Volatile.Write(ref _version, seq + 2);
        }

        /// <summary>
        /// Возвращает экстраполированное монотонное время воспроизведения с ограничением по запасу буфера и защитой от джиттера.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TimeSpan GetCurrentPosition()
        {
            long baseSamples;
            int bufferedSamples;
            long baseTimestamp;
            int sampleRate;
            int channels;
            bool isPlaying;
            long durationMs;

            while (true)
            {
                long seq0 = Volatile.Read(ref _version);
                if ((seq0 & 1L) != 0)
                {
                    Thread.SpinWait(1);
                    continue;
                }

                baseSamples = Volatile.Read(ref _baseSamples);
                bufferedSamples = Volatile.Read(ref _bufferedSamples);
                baseTimestamp = Volatile.Read(ref _baseTimestamp);
                sampleRate = Volatile.Read(ref _sampleRate);
                channels = Volatile.Read(ref _channels);
                isPlaying = Volatile.Read(ref _isPlaying);
                durationMs = Volatile.Read(ref _durationMs);

                long seq1 = Volatile.Read(ref _version);
                if (seq0 == seq1)
                    break;
            }

            if (sampleRate <= 0 || channels <= 0)
                return TimeSpan.Zero;

            double samplesPerSecond = (double)sampleRate * channels;
            double baseSeconds = baseSamples / samplesPerSecond;
            double maxExtrapolation = baseSeconds + (bufferedSamples / samplesPerSecond);
            double maxSeconds = durationMs / 1000.0;

            if (!isPlaying)
            {
                double finalSec = Math.Clamp(baseSeconds, 0.0, maxSeconds);
                _lastReturnedSeconds = finalSec;
                return TimeSpan.FromSeconds(finalSec);
            }

            long currentTimestamp = Stopwatch.GetTimestamp();
            double elapsedSeconds = (double)(currentTimestamp - baseTimestamp) / Stopwatch.Frequency;
            double extrapolated = baseSeconds + elapsedSeconds;

            // Защита от выхода за пределы фактически имеющихся в кольцевом буфере сэмплов
            if (extrapolated > maxExtrapolation)
                extrapolated = maxExtrapolation;

            if (extrapolated > maxSeconds)
                extrapolated = maxSeconds;

            if (extrapolated < 0.0)
                extrapolated = 0.0;

            // Защита от дрожания таймера при незначительном откате назад
            if (extrapolated < _lastReturnedSeconds && extrapolated >= _lastReturnedSeconds - 0.2)
            {
                extrapolated = _lastReturnedSeconds;
            }

            _lastReturnedSeconds = extrapolated;
            return TimeSpan.FromSeconds(extrapolated);
        }
    }
}