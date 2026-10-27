using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// Коэффициенты биквадратного IIR-фильтра двойной точности (Direct Form II Transposed).
/// Нормализованы (<c>a0 = 1</c>).
/// </summary>
public readonly record struct BiquadCoefficients(double B0, double B1, double B2, double A1, double A2)
{
    /// <summary>Единичный фильтр (полный пропуск сигнала без изменений).</summary>
    public static BiquadCoefficients Identity => new(1.0, 0.0, 0.0, 0.0, 0.0);

    /// <summary>
    /// Линейно интерполирует коэффициенты двойной точности между двумя состояниями фильтра.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BiquadCoefficients Lerp(in BiquadCoefficients a, in BiquadCoefficients b, double t) =>
        new(
            a.B0 + (b.B0 - a.B0) * t,
            a.B1 + (b.B1 - a.B1) * t,
            a.B2 + (b.B2 - a.B2) * t,
            a.A1 + (b.A1 - a.A1) * t,
            a.A2 + (b.A2 - a.A2) * t);

    /// <summary>
    /// Вычисляет квадрат модуля передаточной функции <c>|H(e^(jw))|²</c> на заданной частоте.
    /// Используется для построения логарифмической кривой АЧХ в UI без аллокаций.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double ComputeMagnitudeSquared(double freqHz, int sampleRate)
    {
        if (sampleRate <= 0 || freqHz <= 0.0 || freqHz >= sampleRate * 0.5)
            return 1.0;

        double w = 2.0 * Math.PI * freqHz / sampleRate;
        double cosW = Math.Cos(w);
        double cos2W = 2.0 * cosW * cosW - 1.0;

        double num = B0 * B0 + B1 * B1 + B2 * B2
                   + 2.0 * (B0 * B1 + B1 * B2) * cosW
                   + 2.0 * B0 * B2 * cos2W;

        double den = 1.0 + A1 * A1 + A2 * A2
                   + 2.0 * (A1 + A1 * A2) * cosW
                   + 2.0 * A2 * cos2W;

        return den > 1e-18 ? Math.Max(0.0, num / den) : 1.0;
    }
}

/// <summary>
/// Коэффициенты биквадратного IIR-фильтра одинарной точности для горячего пути эквалайзера.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct BiquadCoefficientsF
{
    public float B0;
    public float B1;
    public float B2;
    public float A1;
    public float A2;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BiquadCoefficientsF(float b0, float b1, float b2, float a1, float a2)
    {
        B0 = b0;
        B1 = b1;
        B2 = b2;
        A1 = a1;
        A2 = a2;
    }

    public static BiquadCoefficientsF Identity => new(1f, 0f, 0f, 0f, 0f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BiquadCoefficientsF FromDouble(in BiquadCoefficients c) =>
        new((float)c.B0, (float)c.B1, (float)c.B2, (float)c.A1, (float)c.A2);

    /// <summary>
    /// Линейно интерполирует коэффициенты между двумя устойчивыми состояниями фильтра.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BiquadCoefficientsF Lerp(in BiquadCoefficientsF a, in BiquadCoefficientsF b, float t) =>
        new(
            a.B0 + (b.B0 - a.B0) * t,
            a.B1 + (b.B1 - a.B1) * t,
            a.B2 + (b.B2 - a.B2) * t,
            a.A1 + (b.A1 - a.A1) * t,
            a.A2 + (b.A2 - a.A2) * t);
}

/// <summary>
/// Состояние биквадратного IIR-фильтра двойной точности (Direct Form II Transposed).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct BiquadState
{
    public double Z1;
    public double Z2;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double Process(double input, in BiquadCoefficients c)
    {
        double output = c.B0 * input + Z1;
        Z1 = c.B1 * input - c.A1 * output + Z2;
        Z2 = c.B2 * input - c.A2 * output;
        return output;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset()
    {
        Z1 = 0;
        Z2 = 0;
    }
}

/// <summary>
/// Спаренное стерео-состояние биквадратного IIR-фильтра с внутренней двойной точностью аккумуляторов задержки.
/// Исключает смещение полюсов за единичную окружность на суб-низких частотах (31–62 Гц @ 48 кГц) и денормализованные числа.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct StereoBiquadStateF
{
    public double Z1L;
    public double Z1R;
    public double Z2L;
    public double Z2R;

    /// <summary>
    /// Обрабатывает пару стерео-сэмплов (L, R) в двойной точности без промежуточного усечения мантиссы между каскадами.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ProcessStereoDouble(ref double left, ref double right, in BiquadCoefficientsF c)
    {
        double inL = left;
        double inR = right;

        double b0 = c.B0;
        double b1 = c.B1;
        double b2 = c.B2;
        double a1 = c.A1;
        double a2 = c.A2;

        double outL = Math.FusedMultiplyAdd(b0, inL, Z1L);
        double outR = Math.FusedMultiplyAdd(b0, inR, Z1R);

        Z1L = Math.FusedMultiplyAdd(b1, inL, Z2L) - a1 * outL;
        Z1R = Math.FusedMultiplyAdd(b1, inR, Z2R) - a1 * outR;

        Z2L = b2 * inL - a2 * outL;
        Z2R = b2 * inR - a2 * outR;

        left = outL;
        right = outR;
    }

    /// <summary>
    /// Обрабатывает пару стерео-сэмплов (L, R) за один такт с идентичными коэффициентами полосы.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ProcessStereo(ref float left, ref float right, in BiquadCoefficientsF c)
    {
        double inL = left;
        double inR = right;

        double b0 = c.B0;
        double b1 = c.B1;
        double b2 = c.B2;
        double a1 = c.A1;
        double a2 = c.A2;

        double outL = Math.FusedMultiplyAdd(b0, inL, Z1L);
        double outR = Math.FusedMultiplyAdd(b0, inR, Z1R);

        Z1L = Math.FusedMultiplyAdd(b1, inL, Z2L) - a1 * outL;
        Z1R = Math.FusedMultiplyAdd(b1, inR, Z2R) - a1 * outR;

        Z2L = b2 * inL - a2 * outL;
        Z2R = b2 * inR - a2 * outR;

        left = (float)outL;
        right = (float)outR;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset()
    {
        Z1L = 0.0;
        Z1R = 0.0;
        Z2L = 0.0;
        Z2R = 0.0;
    }
}

/// <summary>
/// Единый математический модуль расчёта коэффициентов IIR-фильтров по стандарту RBJ Audio-EQ-Cookbook.
/// Устраняет дублирование между <see cref="KWeightingFilter"/>, <see cref="AudioSpectrumAnalyzer"/> и <see cref="EqualizerProcessor"/>.
/// </summary>
public static class BiquadFilterMath
{
    /// <summary>ФНЧ 2-го порядка (Low-Pass Filter).</summary>
    public static BiquadCoefficients ComputeLowPass(int sampleRate, double freqHz, double q = 0.7071067811865475)
    {
        double w0 = 2.0 * Math.PI * ClampFreq(freqHz, sampleRate) / sampleRate;
        (double sinW0, double cosW0) = Math.SinCos(w0);
        double alpha = sinW0 / (2.0 * q);

        double b0 = (1.0 - cosW0) * 0.5;
        double b1 = 1.0 - cosW0;
        double b2 = (1.0 - cosW0) * 0.5;
        double a0 = 1.0 + alpha;
        double a1 = -2.0 * cosW0;
        double a2 = 1.0 - alpha;

        double invA0 = 1.0 / a0;
        return new BiquadCoefficients(b0 * invA0, b1 * invA0, b2 * invA0, a1 * invA0, a2 * invA0);
    }

    /// <summary>ФВЧ 2-го порядка (High-Pass Filter).</summary>
    public static BiquadCoefficients ComputeHighPass(int sampleRate, double freqHz, double q = 0.7071067811865475)
    {
        double w0 = 2.0 * Math.PI * ClampFreq(freqHz, sampleRate) / sampleRate;
        (double sinW0, double cosW0) = Math.SinCos(w0);
        double alpha = sinW0 / (2.0 * q);

        double b0 = (1.0 + cosW0) * 0.5;
        double b1 = -(1.0 + cosW0);
        double b2 = (1.0 + cosW0) * 0.5;
        double a0 = 1.0 + alpha;
        double a1 = -2.0 * cosW0;
        double a2 = 1.0 - alpha;

        double invA0 = 1.0 / a0;
        return new BiquadCoefficients(b0 * invA0, b1 * invA0, b2 * invA0, a1 * invA0, a2 * invA0);
    }

    /// <summary>Полосовой фильтр с единичным пиковым усилением (Constant 0 dB Peak Gain Band-Pass).</summary>
    public static BiquadCoefficients ComputeBandPass(int sampleRate, double freqHz, double q = 0.75)
    {
        double w0 = 2.0 * Math.PI * ClampFreq(freqHz, sampleRate) / sampleRate;
        (double sinW0, double cosW0) = Math.SinCos(w0);
        double alpha = sinW0 / (2.0 * q);

        double b0 = alpha;
        double b1 = 0.0;
        double b2 = -alpha;
        double a0 = 1.0 + alpha;
        double a1 = -2.0 * cosW0;
        double a2 = 1.0 - alpha;

        double invA0 = 1.0 / a0;
        return new BiquadCoefficients(b0 * invA0, b1 * invA0, b2 * invA0, a1 * invA0, a2 * invA0);
    }

    /// <summary>Параметрический колоколообразный фильтр (Peaking EQ).</summary>
    public static BiquadCoefficients ComputePeakingEq(int sampleRate, double freqHz, double gainDb, double q = 1.4142135623730951)
    {
        double a = Math.Abs(gainDb) < 0.001 ? 1.0 : Math.Pow(10.0, gainDb / 40.0);
        double w0 = 2.0 * Math.PI * ClampFreq(freqHz, sampleRate) / sampleRate;
        (double sinW0, double cosW0) = Math.SinCos(w0);
        double alpha = sinW0 / (2.0 * q);

        double b0 = 1.0 + alpha * a;
        double b1 = -2.0 * cosW0;
        double b2 = 1.0 - alpha * a;
        double a0 = 1.0 + alpha / a;
        double a1 = -2.0 * cosW0;
        double a2 = 1.0 - alpha / a;

        double invA0 = 1.0 / a0;
        return new BiquadCoefficients(b0 * invA0, b1 * invA0, b2 * invA0, a1 * invA0, a2 * invA0);
    }

    /// <summary>Низкочастотный шельфовый фильтр (Low-Shelf).</summary>
    public static BiquadCoefficients ComputeLowShelf(int sampleRate, double freqHz, double gainDb, double q = 0.7071067811865475)
    {
        double a = Math.Abs(gainDb) < 0.001 ? 1.0 : Math.Pow(10.0, gainDb / 40.0);
        double w0 = 2.0 * Math.PI * ClampFreq(freqHz, sampleRate) / sampleRate;
        (double sinW0, double cosW0) = Math.SinCos(w0);
        double alpha = sinW0 / (2.0 * q);
        double twoSqrtAAlpha = 2.0 * Math.Sqrt(a) * alpha;

        double a0 = a + 1.0 + (a - 1.0) * cosW0 + twoSqrtAAlpha;
        double a1 = -2.0 * (a - 1.0 + (a + 1.0) * cosW0);
        double a2 = a + 1.0 + (a - 1.0) * cosW0 - twoSqrtAAlpha;
        double b0 = a * (a + 1.0 - (a - 1.0) * cosW0 + twoSqrtAAlpha);
        double b1 = 2.0 * a * (a - 1.0 - (a + 1.0) * cosW0);
        double b2 = a * (a + 1.0 - (a - 1.0) * cosW0 - twoSqrtAAlpha);

        double invA0 = 1.0 / a0;
        return new BiquadCoefficients(b0 * invA0, b1 * invA0, b2 * invA0, a1 * invA0, a2 * invA0);
    }

    /// <summary>Высокочастотный шельфовый фильтр (High-Shelf).</summary>
    public static BiquadCoefficients ComputeHighShelf(int sampleRate, double freqHz, double gainDb, double q = 0.7071067811865475)
    {
        double a = Math.Abs(gainDb) < 0.001 ? 1.0 : Math.Pow(10.0, gainDb / 40.0);
        double w0 = 2.0 * Math.PI * ClampFreq(freqHz, sampleRate) / sampleRate;
        (double sinW0, double cosW0) = Math.SinCos(w0);
        double alpha = sinW0 / (2.0 * q);
        double twoSqrtAAlpha = 2.0 * Math.Sqrt(a) * alpha;

        double a0 = a + 1.0 - (a - 1.0) * cosW0 + twoSqrtAAlpha;
        double a1 = 2.0 * (a - 1.0 - (a + 1.0) * cosW0);
        double a2 = a + 1.0 - (a - 1.0) * cosW0 - twoSqrtAAlpha;
        double b0 = a * (a + 1.0 + (a - 1.0) * cosW0 + twoSqrtAAlpha);
        double b1 = -2.0 * a * (a - 1.0 + (a + 1.0) * cosW0);
        double b2 = a * (a + 1.0 + (a - 1.0) * cosW0 - twoSqrtAAlpha);

        double invA0 = 1.0 / a0;
        return new BiquadCoefficients(b0 * invA0, b1 * invA0, b2 * invA0, a1 * invA0, a2 * invA0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ClampFreq(double freqHz, int sampleRate) =>
        Math.Clamp(freqHz, 10.0, Math.Max(20.0, sampleRate * 0.47));
}

/// <summary>
/// K-weighting фильтр по стандарту ITU-R BS.1770-4.
/// </summary>
public sealed class KWeightingFilter
{
    private const double ShelfGainDb = 3.999843853973347;
    private const double ShelfFreqHz = 1500.0;
    private const double ShelfQ = 0.7071752369554196;

    private const double HighPassFreqHz = 38.13547087602444;
    private const double HighPassQ = 0.5003270373238773;

    private readonly BiquadCoefficients _shelf;
    private readonly BiquadCoefficients _highPass;
    private readonly BiquadState[] _shelfState;
    private readonly BiquadState[] _highPassState;
    private readonly int _channels;

    public KWeightingFilter(int sampleRate, int channels)
    {
        _channels = channels;
        _shelf = BiquadFilterMath.ComputeHighShelf(sampleRate, ShelfFreqHz, ShelfGainDb, ShelfQ);
        _highPass = BiquadFilterMath.ComputeHighPass(sampleRate, HighPassFreqHz, HighPassQ);
        _shelfState = new BiquadState[channels];
        _highPassState = new BiquadState[channels];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double ProcessSample(int channel, float input)
    {
        double x = input;
        x = _shelfState[channel].Process(x, in _shelf);
        x = _highPassState[channel].Process(x, in _highPass);
        return x;
    }

    public void ProcessBlock(ReadOnlySpan<float> input, Span<double> output)
    {
        int channels = _channels;
        if (channels == 0 || input.Length == 0) return;

        ref var shelfRef = ref MemoryMarshal.GetArrayDataReference(_shelfState);
        ref var hpRef = ref MemoryMarshal.GetArrayDataReference(_highPassState);

        var shelf = _shelf;
        var hp = _highPass;

        int frameCount = input.Length / channels;
        int idx = 0;

        for (int frame = 0; frame < frameCount; frame++)
        {
            for (int ch = 0; ch < channels; ch++, idx++)
            {
                double x = input[idx];
                x = Unsafe.Add(ref shelfRef, ch).Process(x, in shelf);
                x = Unsafe.Add(ref hpRef, ch).Process(x, in hp);
                output[idx] = x;
            }
        }
    }

    public void Reset()
    {
        for (int ch = 0; ch < _channels; ch++)
        {
            _shelfState[ch].Reset();
            _highPassState[ch].Reset();
        }
    }
}