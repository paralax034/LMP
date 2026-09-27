using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// 3-полосный кроссовер-анализатор спектра (Low / Mid / High) с детектором транзиентов бочки.
/// </summary>
/// <remarks>
/// Работает по принципу Sidechain DSP без модификации исходного аудиопотока.
/// Использует нормализованные биквадратные IIR-фильтры второго порядка (Audio EQ Cookbook).
/// Нулевые аллокации в hot-path: все состояния выделены в конструкторе.
/// </remarks>
public sealed class Audio3BandAnalyzer
{
    private const double LowCutoffHz = 220.0;
    private const double HighCutoffHz = 3400.0;
    private const double FilterQ = 0.7071067811865475; // Butterworth / Linkwitz-Riley

    private readonly BiquadCoefficients _lowPass;
    private readonly BiquadCoefficients _highPass;
    private readonly BiquadState[] _lowPassState;
    private readonly BiquadState[] _highPassState;
    private readonly int _channels;

    private float _beatFollowerFast;
    private float _beatFollowerSlow;

    public Audio3BandAnalyzer(int sampleRate, int channels)
    {
        _channels = Math.Max(1, channels);

        _lowPass = ComputeLowPassCoefficients(sampleRate, LowCutoffHz, FilterQ);
        _highPass = ComputeHighPassCoefficients(sampleRate, HighCutoffHz, FilterQ);

        _lowPassState = new BiquadState[_channels];
        _highPassState = new BiquadState[_channels];
    }

    /// <summary>
    /// Анализирует блок PCM сэмплов и публикует уровни полос в <see cref="AudioVisualizerBuffer"/>.
    /// </summary>
    public void ProcessBlock(ReadOnlySpan<float> samples, int channels)
    {
        int totalSamples = samples.Length;
        if (totalSamples == 0 || channels <= 0)
        {
            AudioVisualizerBuffer.Reset();
            return;
        }

        int chCount = Math.Min(_channels, channels);
        int frameCount = totalSamples / channels;

        ref var lpRef = ref MemoryMarshal.GetArrayDataReference(_lowPassState);
        ref var hpRef = ref MemoryMarshal.GetArrayDataReference(_highPassState);
        ref float srcRef = ref MemoryMarshal.GetReference(samples);

        var lpCoeff = _lowPass;
        var hpCoeff = _highPass;

        float lowSumSq = 0f;
        float midSumSq = 0f;
        float highSumSq = 0f;
        float overallMaxPeak = 0f;

        int idx = 0;
        for (int f = 0; f < frameCount; f++)
        {
            for (int ch = 0; ch < chCount; ch++, idx++)
            {
                float raw = Unsafe.Add(ref srcRef, idx);
                float abs = MathF.Abs(raw);
                if (abs > overallMaxPeak)
                    overallMaxPeak = abs;

                double x = raw;
                double low = Unsafe.Add(ref lpRef, ch).Process(x, in lpCoeff);
                double high = Unsafe.Add(ref hpRef, ch).Process(x, in hpCoeff);
                double mid = x - low - high;

                lowSumSq += (float)(low * low);
                midSumSq += (float)(mid * mid);
                highSumSq += (float)(high * high);
            }

            // Пропуск остальных каналов при многоканальном потоке (> стерео)
            for (int ch = chCount; ch < channels; ch++, idx++) { }
        }

        int processedCount = frameCount * chCount;
        float invCount = processedCount > 0 ? 1.0f / processedCount : 0f;

        float lowRms = MathF.Sqrt(lowSumSq * invCount);
        float midRms = MathF.Sqrt(midSumSq * invCount);
        float highRms = MathF.Sqrt(highSumSq * invCount);

        // Нелинейная экспансия динамического диапазона (компенсация EBU R128 нормализации)
        float lowLevel = Math.Clamp(MathF.Pow(lowRms * 2.8f, 0.65f), 0f, 1f);
        float midLevel = Math.Clamp(MathF.Pow(midRms * 3.2f, 0.60f), 0f, 1f);
        float highLevel = Math.Clamp(MathF.Pow(highRms * 4.0f, 0.55f), 0f, 1f);
        float overallLevel = Math.Clamp(MathF.Pow(overallMaxPeak, 0.70f), 0f, 1f);

        // Быстрый детектор транзиента бочки (Kick Transient)
        _beatFollowerFast += (lowLevel - _beatFollowerFast) * 0.45f;
        _beatFollowerSlow += (lowLevel - _beatFollowerSlow) * 0.08f;

        float beatDelta = MathF.Max(0f, _beatFollowerFast - (_beatFollowerSlow * 1.15f));
        float beatPulse = Math.Clamp(beatDelta * 2.6f, 0f, 1f);

        AudioVisualizerBuffer.Update3Band(lowLevel, midLevel, highLevel, beatPulse, overallLevel);
    }

    /// <summary>
    /// Сбрасывает внутренние задержки фильтров и огибающей.
    /// </summary>
    public void Reset()
    {
        for (int ch = 0; ch < _channels; ch++)
        {
            _lowPassState[ch].Reset();
            _highPassState[ch].Reset();
        }

        _beatFollowerFast = 0f;
        _beatFollowerSlow = 0f;
        AudioVisualizerBuffer.Reset();
    }

    private static BiquadCoefficients ComputeLowPassCoefficients(int sampleRate, double freq, double q)
    {
        double w0 = 2.0 * Math.PI * freq / sampleRate;
        double cosW0 = Math.Cos(w0);
        double sinW0 = Math.Sin(w0);
        double alpha = sinW0 / (2.0 * q);

        double b0 = (1.0 - cosW0) / 2.0;
        double b1 = 1.0 - cosW0;
        double b2 = (1.0 - cosW0) / 2.0;
        double a0 = 1.0 + alpha;
        double a1 = -2.0 * cosW0;
        double a2 = 1.0 - alpha;

        double invA0 = 1.0 / a0;
        return new BiquadCoefficients(b0 * invA0, b1 * invA0, b2 * invA0, a1 * invA0, a2 * invA0);
    }

    private static BiquadCoefficients ComputeHighPassCoefficients(int sampleRate, double freq, double q)
    {
        double w0 = 2.0 * Math.PI * freq / sampleRate;
        double cosW0 = Math.Cos(w0);
        double sinW0 = Math.Sin(w0);
        double alpha = sinW0 / (2.0 * q);

        double b0 = (1.0 + cosW0) / 2.0;
        double b1 = -(1.0 + cosW0);
        double b2 = (1.0 + cosW0) / 2.0;
        double a0 = 1.0 + alpha;
        double a1 = -2.0 * cosW0;
        double a2 = 1.0 - alpha;

        double invA0 = 1.0 / a0;
        return new BiquadCoefficients(b0 * invA0, b1 * invA0, b2 * invA0, a1 * invA0, a2 * invA0);
    }
}