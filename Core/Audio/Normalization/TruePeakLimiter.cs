using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LMP.Core.Audio.Normalization;

internal sealed class TruePeakLimiter
{
    #region Constants

    /// <summary>
    /// True Peak protection ceiling (-0.13 dBFS = 0.985f).
    /// Prevents triggering the Windows system limiter (CAudioLimiter APO in audiodg.exe) at 95-100% volume.
    /// </summary>
    private const float Ceiling = 0.985f;

    /// <summary>
    /// Soft asymptotic peak rounding knee threshold (0.88f) during the initial milliseconds of attack.
    /// Guarantees 0 flat clips (FlatClipped == 0) and no Ceiling (0.985f) overshoot without envelope jumps.
    /// </summary>
    private const float KneeStart = 0.88f;
    private const float KneeSpan = Ceiling - KneeStart; // 0.105f
    private const float InvKneeSpan = 1.0f / KneeSpan;

    /// <summary>Smooth envelope attack time (4 ms) — completely eliminates clicks and modulation ("fluttering string") on transients.</summary>
    private const float AttackMs = 4.0f;

    private const float PeakHoldMs = 30f;

    /// <summary>Release time in milliseconds (smooth return after a bass section ends).</summary>
    private const float ReleaseMs = 400f;

    #endregion

    #region State

    private float _envelopeGain;
    private float _targetEnvelope;
    private int _holdFramesRemaining;

    /// <summary>
    /// Returns the current attenuation state of the limiter envelope.
    /// A value of 1.0f means the limiter has fully recovered and is not attenuating the signal.
    /// </summary>
    public float EnvelopeGain => _envelopeGain;

    private readonly int _holdFrames;
    private readonly float _attackCoef;
    private readonly float _releaseCoef;

    #endregion

    /// <summary>
    /// Creates a True Peak Limiter with the specified sample rate.
    /// </summary>
    /// <param name="sampleRate">Audio sample rate (Hz).</param>
    public TruePeakLimiter(int sampleRate)
    {
        int rate = Math.Max(8000, sampleRate);
        _envelopeGain = 1.0f;
        _targetEnvelope = 1.0f;
        _holdFramesRemaining = 0;

        _holdFrames = Math.Max(1, (int)(rate * PeakHoldMs / 1000f));
        float attackSamples = rate * AttackMs / 1000f;
        float releaseSamples = rate * ReleaseMs / 1000f;

        _attackCoef = MathF.Exp(-1f / attackSamples);
        _releaseCoef = MathF.Exp(-1f / releaseSamples);
    }

    /// <summary>
    /// Smooth C2-continuous asymptotic saturator for positive peak amplitude.
    /// Has <c>f'(0) = 1</c> and <c>f''(0) = 0</c> at <see cref="KneeStart"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SoftCeilingPositive(float abs)
    {
        float u = (abs - KneeStart) * InvKneeSpan;
        float u2 = u * u;
        float sum = u + u2;
        return KneeStart + KneeSpan * (sum * Helpers.SpanMathHelper.FastReciprocal(1.0f + sum));
    }

    /// <summary>
    /// Multiplier that smoothly fits the frame peak under <see cref="Ceiling"/> using the same C2-curve as the limiter itself.
    /// Returns exactly 1 for peaks below <see cref="KneeStart"/>: signals below the knee pass through bit-exact.
    /// Required for nodes downstream of the limiter that generate inter-sample peaks (e.g., WASAPI resampler).
    /// </summary>
    /// <param name="peak">Maximum sample magnitude of the frame across all channels.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float SoftCeilingScale(float peak)
        => peak > KneeStart ? SoftCeilingPositive(peak) * Helpers.SpanMathHelper.FastReciprocal(peak) : 1.0f;

    /// <summary>
    /// Processes an interleaved PCM buffer in-place (Zero-Latency, 100% preservation of L/R stereo balance and phase on Seek).
    /// </summary>
    /// <param name="samples">Interleaved PCM samples (in-place processing).</param>
    /// <param name="crossfader">Reference to the crossfader for per-sample gain interpolation.</param>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public void Process(Span<float> samples, ref GainCrossfader crossfader)
    {
        int length = samples.Length;
        if (length == 0) return;

        ref float samplesRef = ref MemoryMarshal.GetReference(samples);

        float envGain = _envelopeGain;
        float targetEnv = _targetEnvelope;
        int holdRem = _holdFramesRemaining;
        int holdMax = _holdFrames;
        float attackCoef = _attackCoef;
        float releaseCoef = _releaseCoef;

        int i = 0;
        int pairLimit = length & ~1;

        for (; i < pairLimit; i += 2)
        {
            float gain = crossfader.AdvanceFrame(2);
            float inL = Unsafe.Add(ref samplesRef, i) * gain;
            float inR = Unsafe.Add(ref samplesRef, i + 1) * gain;

            float absL = MathF.Abs(inL);
            float absR = MathF.Abs(inR);
            float absPeak = absL > absR ? absL : absR;
            float neededGain = absPeak > KneeStart
                ? KneeStart * Helpers.SpanMathHelper.FastReciprocal(absPeak)
                : 1.0f;

            if (neededGain <= targetEnv)
            {
                targetEnv = neededGain;
                holdRem = holdMax;
            }
            else if (neededGain <= targetEnv * 1.06f && neededGain < 1.0f)
            {
                holdRem = holdMax;
            }
            else if (holdRem > 0)
            {
                holdRem--;
            }
            else
            {
                targetEnv = 1.0f + releaseCoef * (targetEnv - 1.0f);
            }

            float coef = targetEnv < envGain ? attackCoef : releaseCoef;
            envGain = targetEnv + coef * (envGain - targetEnv);

            float outL = inL * envGain;
            float outR = inR * envGain;
            float outPeak = absPeak * envGain;
            if (outPeak > KneeStart)
            {
                float softScale = SoftCeilingPositive(outPeak) * Helpers.SpanMathHelper.FastReciprocal(outPeak);
                outL *= softScale;
                outR *= softScale;
            }

            Unsafe.Add(ref samplesRef, i) = outL;
            Unsafe.Add(ref samplesRef, i + 1) = outR;
        }

        for (; i < length; i++)
        {
            float gain = crossfader.AdvanceFrame(1);
            float amplified = Unsafe.Add(ref samplesRef, i) * gain;
            float absAmp = MathF.Abs(amplified);

            float neededGain = absAmp > KneeStart
                ? KneeStart * Helpers.SpanMathHelper.FastReciprocal(absAmp)
                : 1.0f;

            if (neededGain < targetEnv)
            {
                targetEnv = neededGain;
                holdRem = holdMax;
            }
            else if (holdRem > 0)
            {
                holdRem--;
            }
            else
            {
                targetEnv = 1.0f + releaseCoef * (targetEnv - 1.0f);
            }

            float coef = targetEnv < envGain ? attackCoef : releaseCoef;
            envGain = targetEnv + coef * (envGain - targetEnv);

            float outM = amplified * envGain;
            float outAbs = absAmp * envGain;
            if (outAbs > KneeStart)
                outM = MathF.CopySign(SoftCeilingPositive(outAbs), outM);

            Unsafe.Add(ref samplesRef, i) = outM;
        }

        _envelopeGain = envGain;
        _targetEnvelope = targetEnv;
        _holdFramesRemaining = holdRem;
    }

    /// <summary>
    /// Resets the envelope to its initial state (gain = 1.0).
    /// </summary>
    public void Reset()
    {
        _envelopeGain = 1.0f;
        _targetEnvelope = 1.0f;
        _holdFramesRemaining = 0;
    }
}