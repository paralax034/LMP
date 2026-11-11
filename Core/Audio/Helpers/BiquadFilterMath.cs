using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// Double-precision biquad IIR filter coefficients (Direct Form II Transposed).
/// Normalized (<c>a0 = 1</c>).
/// </summary>
public readonly record struct BiquadCoefficients(double B0, double B1, double B2, double A1, double A2)
{
    /// <summary>Identity filter (passthrough with no signal modification).</summary>
    public static BiquadCoefficients Identity => new(1.0, 0.0, 0.0, 0.0, 0.0);


    /// <summary>
    /// Calculates the squared magnitude response <c>|H(e^(jw))|²</c> at the specified frequency.
    /// Used to render the logarithmic magnitude response curve in the UI without allocations.
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
/// Single-precision biquad IIR filter coefficients for the equalizer hot path.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BiquadCoefficientsF
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BiquadCoefficientsF FromDouble(in BiquadCoefficients c) =>
        new((float)c.B0, (float)c.B1, (float)c.B2, (float)c.A1, (float)c.A2);
}

/// <summary>
/// Double-precision biquad IIR filter state (Direct Form II Transposed).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BiquadState
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
/// Unified math module for calculating IIR filter coefficients according to the RBJ Audio-EQ-Cookbook standard.
/// Eliminates duplication between <see cref="KWeightingFilter"/>, <see cref="AudioSpectrumAnalyzer"/>, and <see cref="EqualizerProcessor"/>.
/// </summary>
public static class BiquadFilterMath
{
    /// <summary>2nd order Low-Pass Filter (LPF).</summary>
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

    /// <summary>2nd order High-Pass Filter (HPF).</summary>
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

    /// <summary>Band-pass filter with constant 0 dB peak gain.</summary>
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

    /// <summary>Parametric peaking filter (Peaking EQ).</summary>
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

    /// <summary>Low-shelf filter.</summary>
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

    /// <summary>High-shelf filter.</summary>
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
/// K-weighting filter per ITU-R BS.1770-4 standard.
/// </summary>
internal sealed class KWeightingFilter
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

    public unsafe void ProcessBlock(ReadOnlySpan<float> input, Span<double> output)
    {
        int channels = _channels;
        if (channels <= 0 || input.Length == 0 || output.Length < input.Length) return;

        int frameCount = input.Length / channels;
        if (frameCount <= 0) return;

        if (channels == 2 && Sse2.IsSupported)
        {
            fixed (float* pIn = input)
            fixed (double* pOut = output)
            {
                if (Fma.IsSupported)
                    ProcessBlockStereoFma(pIn, pOut, frameCount);
                else
                    ProcessBlockStereoSse2(pIn, pOut, frameCount);
            }
            return;
        }

        ref var shelfRef = ref MemoryMarshal.GetArrayDataReference(_shelfState);
        ref var hpRef = ref MemoryMarshal.GetArrayDataReference(_highPassState);
        ref float inRef = ref MemoryMarshal.GetReference(input);
        ref double outRef = ref MemoryMarshal.GetReference(output);

        var shelf = _shelf;
        var hp = _highPass;
        int idx = 0;

        for (int frame = 0; frame < frameCount; frame++)
        {
            for (int ch = 0; ch < channels; ch++, idx++)
            {
                double x = Unsafe.Add(ref inRef, idx);
                x = Unsafe.Add(ref shelfRef, ch).Process(x, in shelf);
                x = Unsafe.Add(ref hpRef, ch).Process(x, in hp);
                Unsafe.Add(ref outRef, idx) = x;
            }
        }
    }

    /// <summary>
    /// Vectorized stereo K-Weighting pass (FMA3 + SSE2):
    /// leverages the RBJ High-Pass identity (<c>B2 == B0</c>, <c>B1 == -2 * B0</c>), reducing register usage
    /// from 17 to 15 XMM registers (0 stack accesses <c>[rsp + disp]</c> inside the loop).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private unsafe void ProcessBlockStereoFma(float* pIn, double* pOut, int frameCount)
    {
        if (!Fma.IsSupported)
            return;

        ref var shelf0 = ref _shelfState[0];
        ref var shelf1 = ref _shelfState[1];
        ref var hp0 = ref _highPassState[0];
        ref var hp1 = ref _highPassState[1];

        var sZ1 = Vector128.Create(shelf0.Z1, shelf1.Z1);
        var sZ2 = Vector128.Create(shelf0.Z2, shelf1.Z2);
        var hZ1 = Vector128.Create(hp0.Z1, hp1.Z1);
        var hZ2 = Vector128.Create(hp0.Z2, hp1.Z2);

        var sB0 = Vector128.Create(_shelf.B0);
        var sB1 = Vector128.Create(_shelf.B1);
        var sB2 = Vector128.Create(_shelf.B2);
        var sA1 = Vector128.Create(_shelf.A1);
        var sA2 = Vector128.Create(_shelf.A2);

        var hB0 = Vector128.Create(_highPass.B0);
        var hA1 = Vector128.Create(_highPass.A1);
        var hA2 = Vector128.Create(_highPass.A2);

        float* ptrIn = pIn;
        float* ptrEnd = pIn + (nuint)frameCount * 2;
        double* ptrOut = pOut;

        while (ptrIn < ptrEnd)
        {
            var raw64 = Sse2.LoadScalarVector128((double*)ptrIn);
            var x = Sse2.ConvertToVector128Double(Vector128.AsSingle(raw64));

            var yShelf = Fma.MultiplyAdd(sB0, x, sZ1);
            sZ1 = Fma.MultiplySubtract(sB1, x, Fma.MultiplySubtract(sA1, yShelf, sZ2));
            sZ2 = Fma.MultiplySubtract(sB2, x, Sse2.Multiply(sA2, yShelf));

            var hB0x = Sse2.Multiply(hB0, yShelf);
            var yHp = Sse2.Add(hB0x, hZ1);
            var negB1x = Sse2.Add(hB0x, hB0x);
            hZ1 = Sse2.Subtract(Sse2.Subtract(hZ2, negB1x), Sse2.Multiply(hA1, yHp));
            hZ2 = Sse2.Subtract(hB0x, Sse2.Multiply(hA2, yHp));

            Vector128.Store(yHp, ptrOut);
            ptrIn += 2;
            ptrOut += 2;
        }

        shelf0.Z1 = Vector128.ToScalar(sZ1);
        shelf1.Z1 = Vector128.ToScalar(Sse2.UnpackHigh(sZ1, sZ1));
        shelf0.Z2 = Vector128.ToScalar(sZ2);
        shelf1.Z2 = Vector128.ToScalar(Sse2.UnpackHigh(sZ2, sZ2));

        hp0.Z1 = Vector128.ToScalar(hZ1);
        hp1.Z1 = Vector128.ToScalar(Sse2.UnpackHigh(hZ1, hZ1));
        hp0.Z2 = Vector128.ToScalar(hZ2);
        hp1.Z2 = Vector128.ToScalar(Sse2.UnpackHigh(hZ2, hZ2));
    }

    /// <summary>
    /// Vectorized stereo K-Weighting pass (baseline SSE2):
    /// leverages the RBJ High-Pass identity (<c>B2 == B0</c>, <c>B1 == -2 * B0</c>), fitting into 15 XMM registers
    /// (0 stack accesses <c>[rsp + disp]</c> inside the loop).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private unsafe void ProcessBlockStereoSse2(float* pIn, double* pOut, int frameCount)
    {
        if (!Sse2.IsSupported)
            return;

        ref var shelf0 = ref _shelfState[0];
        ref var shelf1 = ref _shelfState[1];
        ref var hp0 = ref _highPassState[0];
        ref var hp1 = ref _highPassState[1];

        var sZ1 = Vector128.Create(shelf0.Z1, shelf1.Z1);
        var sZ2 = Vector128.Create(shelf0.Z2, shelf1.Z2);
        var hZ1 = Vector128.Create(hp0.Z1, hp1.Z1);
        var hZ2 = Vector128.Create(hp0.Z2, hp1.Z2);

        var sB0 = Vector128.Create(_shelf.B0);
        var sB1 = Vector128.Create(_shelf.B1);
        var sB2 = Vector128.Create(_shelf.B2);
        var sA1 = Vector128.Create(_shelf.A1);
        var sA2 = Vector128.Create(_shelf.A2);

        var hB0 = Vector128.Create(_highPass.B0);
        var hA1 = Vector128.Create(_highPass.A1);
        var hA2 = Vector128.Create(_highPass.A2);

        float* ptrIn = pIn;
        float* ptrEnd = pIn + (nuint)frameCount * 2;
        double* ptrOut = pOut;

        while (ptrIn < ptrEnd)
        {
            var raw64 = Sse2.LoadScalarVector128((double*)ptrIn);
            var x = Sse2.ConvertToVector128Double(Vector128.AsSingle(raw64));

            var yShelf = Sse2.Add(Sse2.Multiply(sB0, x), sZ1);
            sZ1 = Sse2.Subtract(
                Sse2.Add(sZ2, Sse2.Multiply(sB1, x)),
                Sse2.Multiply(sA1, yShelf));
            sZ2 = Sse2.Subtract(Sse2.Multiply(sB2, x), Sse2.Multiply(sA2, yShelf));

            var hB0x = Sse2.Multiply(hB0, yShelf);
            var yHp = Sse2.Add(hB0x, hZ1);
            var negB1x = Sse2.Add(hB0x, hB0x);
            hZ1 = Sse2.Subtract(Sse2.Subtract(hZ2, negB1x), Sse2.Multiply(hA1, yHp));
            hZ2 = Sse2.Subtract(hB0x, Sse2.Multiply(hA2, yHp));

            Vector128.Store(yHp, ptrOut);
            ptrIn += 2;
            ptrOut += 2;
        }

        shelf0.Z1 = Vector128.ToScalar(sZ1);
        shelf1.Z1 = Vector128.ToScalar(Sse2.UnpackHigh(sZ1, sZ1));
        shelf0.Z2 = Vector128.ToScalar(sZ2);
        shelf1.Z2 = Vector128.ToScalar(Sse2.UnpackHigh(sZ2, sZ2));

        hp0.Z1 = Vector128.ToScalar(hZ1);
        hp1.Z1 = Vector128.ToScalar(Sse2.UnpackHigh(hZ1, hZ1));
        hp0.Z2 = Vector128.ToScalar(hZ2);
        hp1.Z2 = Vector128.ToScalar(Sse2.UnpackHigh(hZ2, hZ2));
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