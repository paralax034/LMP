using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// Высокоточный 10-полосный анализатор спектра реального времени (31 Гц .. 16 кГц) + детектор вокала и транзиента бочки
/// на базе изолированных фильтров <see cref="BiquadFilterMath"/> за 0 B аллокаций.
/// </summary>
public sealed class AudioSpectrumAnalyzer
{
    private const int BandCount = 10;

    [InlineArray(BandCount)]
    private struct CoeffArray10
    {
        private BiquadCoefficientsF _element0;
    }

    private struct BiquadStateF
    {
        public float Z1;
        public float Z2;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Reset()
        {
            Z1 = 0f;
            Z2 = 0f;
        }
    }

    [InlineArray(BandCount)]
    private struct StateArray10
    {
        private BiquadStateF _element0;
    }

    [InlineArray(BandCount)]
    private struct FloatArray10
    {
        private float _element0;
    }

    private static readonly float[] SensitivityGainsArray =
          [2.8f, 2.9f, 3.1f, 3.3f, 3.6f, 4.0f, 4.2f, 4.5f, 4.8f, 5.2f];

    private static ReadOnlySpan<float> SensitivityGains => SensitivityGainsArray;

    private CoeffArray10 _bandCoeffs;
    private StateArray10 _bandStates;

    private float _beatFollowerFast;
    private float _beatFollowerSlow;

    public AudioSpectrumAnalyzer(int sampleRate, int channels)
    {
        _ = channels;
        int rate = sampleRate > 0 ? sampleRate : AudioConstants.DefaultSampleRate;
        var freqs = EqualizerProcessor.BandFrequenciesHz;

        for (int i = 0; i < BandCount; i++)
        {
            BiquadCoefficients c = i switch
            {
                0 => BiquadFilterMath.ComputeLowPass(rate, 48.0, 0.85),
                BandCount - 1 => BiquadFilterMath.ComputeHighPass(rate, 11500.0, 0.85),
                _ => BiquadFilterMath.ComputeBandPass(rate, freqs[i], 1.35)
            };
            _bandCoeffs[i] = BiquadCoefficientsF.FromDouble(in c);
        }
    }

    /// <summary>
    /// Анализирует блок PCM сэмплов (0 аллокаций) и публикует 10 полос + агрегаты в <see cref="AudioVisualizerBuffer"/>.
    /// </summary>
    public void ProcessBlock(ReadOnlySpan<float> samples, int channels)
    {
        int totalSamples = samples.Length;
        if (totalSamples == 0 || channels <= 0)
        {
            AudioVisualizerBuffer.Reset();
            return;
        }

        int frameCount = totalSamples / channels;
        if (frameCount <= 0)
        {
            AudioVisualizerBuffer.Reset();
            return;
        }

        ref float srcRef = ref MemoryMarshal.GetReference(samples);
        FloatArray10 sumSq = default;
        float overallMaxPeak = 0f;

        int processedFrames = 0;

        if (channels >= 2)
        {
            for (int f = 0; f < frameCount; f++)
            {
                int idx = f * channels;
                float left = Unsafe.Add(ref srcRef, idx);
                float right = Unsafe.Add(ref srcRef, idx + 1);

                float peak = MathF.Max(MathF.Abs(left), MathF.Abs(right));
                if (peak > overallMaxPeak)
                    overallMaxPeak = peak;

                float center = (left + right) * 0.5f;
                float highMix = center + (left - right) * 0.35f;

                AccumulateBands(center, highMix, ref sumSq);
                processedFrames++;
            }
        }
        else
        {
            for (int f = 0; f < frameCount; f++)
            {
                float mono = Unsafe.Add(ref srcRef, f);
                float abs = MathF.Abs(mono);
                if (abs > overallMaxPeak)
                    overallMaxPeak = abs;

                AccumulateBands(mono, mono, ref sumSq);
                processedFrames++;
            }
        }

        if (processedFrames <= 0) return;

        float invFrames = 1.0f / processedFrames;
        var gains = SensitivityGains;
        FloatArray10 levels10 = default;

        int dominantIdx = 0;
        float maxBandLevel = 0f;

        for (int b = 0; b < BandCount; b++)
        {
            float rms = MathF.Sqrt(sumSq[b] * invFrames);
            float lvl = Math.Clamp(MathF.Pow(rms * gains[b], 0.62f), 0f, 1f);
            levels10[b] = lvl;

            if (lvl > maxBandLevel)
            {
                maxBandLevel = lvl;
                dominantIdx = b;
            }
        }

        float lowLevel = MathF.Max(levels10[0], MathF.Max(levels10[1], levels10[2] * 0.85f));
        float midLevel = MathF.Max(levels10[4] * 0.85f, MathF.Max(levels10[5], levels10[6]));
        float highLevel = MathF.Max(levels10[7], MathF.Max(levels10[8], levels10[9]));
        float overallLevel = Math.Clamp(MathF.Pow(overallMaxPeak, 0.70f), 0f, 1f);

        float kickEnergy = MathF.Max(levels10[0], levels10[1]);
        _beatFollowerFast += (kickEnergy - _beatFollowerFast) * 0.50f;
        _beatFollowerSlow += (kickEnergy - _beatFollowerSlow) * 0.09f;

        float beatDelta = MathF.Max(0f, _beatFollowerFast - (_beatFollowerSlow * 1.12f));
        float beatPulse = Math.Clamp(beatDelta * 2.8f, 0f, 1f);

        AudioVisualizerBuffer.Update10Band(
            MemoryMarshal.CreateReadOnlySpan(ref levels10[0], BandCount),
            lowLevel,
            midLevel,
            highLevel,
            beatPulse,
            overallLevel,
            dominantIdx);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AccumulateBands(float centerInput, float highInput, ref FloatArray10 sumSq)
    {
        ref BiquadCoefficientsF cRef = ref _bandCoeffs[0];
        ref BiquadStateF sRef = ref _bandStates[0];
        ref float sumRef = ref sumSq[0];

        for (int b = 0; b < BandCount; b++)
        {
            float x = b >= 7 ? highInput : centerInput;
            ref readonly var c = ref Unsafe.Add(ref cRef, b);
            ref var s = ref Unsafe.Add(ref sRef, b);

            float y = MathF.FusedMultiplyAdd(c.B0, x, s.Z1);
            s.Z1 = MathF.FusedMultiplyAdd(c.B1, x, s.Z2) - c.A1 * y;
            s.Z2 = c.B2 * x - c.A2 * y;

            ref float dst = ref Unsafe.Add(ref sumRef, b);
            dst = MathF.FusedMultiplyAdd(y, y, dst);
        }
    }

    /// <summary>
    /// Сбрасывает внутренние задержки фильтров и огибающей.
    /// </summary>
    public void Reset()
    {
        for (int b = 0; b < BandCount; b++)
            _bandStates[b].Reset();

        _beatFollowerFast = 0f;
        _beatFollowerSlow = 0f;
        AudioVisualizerBuffer.Reset();
    }
}
