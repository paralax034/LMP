using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LMP.Core.Audio.Backends;

/// <summary>
/// Applies volume gain to PCM float samples in-place without channel phase skew or stepping artifacts during slider movement.
/// </summary>
/// <remarks>
/// <para>
/// Designed to be called exclusively from the playback thread.
/// <see cref="SetVolumeGain"/> receives the value in a thread-safe manner via <see langword="volatile"/>,
/// while the envelope calculation itself occurs strictly inside <see cref="Process"/>.
/// </para>
/// <para>
/// Zero-alloc hot path with hardware SIMD acceleration via <see cref="Vector{T}"/> and protection against
/// triggering the Windows mixer system limiter (<c>audiodg.exe</c>) near 100% volume.
/// </para>
/// </remarks>
internal sealed class GainProcessor
{
    #region Constants

    /// <summary>
    /// Smooth transition duration when changing volume, in stereo frames (1200 frames = 25 ms at 48 kHz).
    /// </summary>
    private const int RampFrames = 1200;

    private const float InvRampFrames = 1.0f / RampFrames;
    private const float GainEpsilon = 0.0001f;

    /// <summary>
    /// Safe amplitude ceiling for WASAPI Shared Mode (-0.13 dBFS),
    /// preventing Windows system limiter clicks at 95–100% volume.
    /// </summary>
    private const float SafeCeiling = 0.985f;
    private const float SoftKneeThreshold = 0.90f;
    private const float SoftKneeHeadroom = SafeCeiling - SoftKneeThreshold; // 0.085f
    private const float InvSoftKneeHeadroom = 1.0f / SoftKneeHeadroom;

    #endregion

    #region Fields

    private volatile float _targetGain = 1.0f;
    private volatile int _channels = 2;
    private float _activeRampTarget = 1.0f;
    private float _currentGain = 1.0f;
    private float _rampStepPerFrame;
    private int _rampFramesRemaining;

    #endregion

    /// <summary>
    /// Softly limits peaks exceeding <see cref="SoftKneeThreshold"/> (0.90f),
    /// preserving first-derivative (C1) continuity with <see cref="SafeCeiling"/> (0.985f) as an asymptote.
    /// Signals within the [-0.90, +0.90] range pass through with 100% bit-exact linearity.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SoftClipSample(float x)
    {
        float abs = MathF.Abs(x);
        if (abs <= SoftKneeThreshold)
            return x;

        float compressed = SoftCeilingPositive(abs);
        return MathF.CopySign(compressed, x);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SoftCeilingPositive(float abs)
    {
        float u = (abs - SoftKneeThreshold) * InvSoftKneeHeadroom;
        float u2 = u * u;
        float sum = u + u2;
        return SoftKneeThreshold + SoftKneeHeadroom * (sum * Helpers.SpanMathHelper.FastReciprocal(1.0f + sum));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetChannels(int channels)
    {
        _channels = Math.Max(1, channels);
    }

    /// <summary>
    /// Sets the target volume gain. Thread-safe via volatile write.
    /// </summary>
    /// <param name="gain">Linear gain factor. Values below 0 are clamped to 0.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetVolumeGain(float gain)
    {
        _targetGain = Math.Max(0f, gain);
    }

    /// <summary>
    /// Applies volume gain to PCM float samples in-place.
    /// </summary>
    /// <param name="samples">Interleaved PCM float samples. Modified in-place.</param>
    public void Process(Span<float> samples)
    {
        if (samples.IsEmpty) return;

        int length = samples.Length;
        float target = _targetGain;

        if (MathF.Abs(target - _activeRampTarget) > GainEpsilon)
        {
            _activeRampTarget = target;
            float delta = target - _currentGain;
            if (MathF.Abs(delta) > GainEpsilon)
            {
                _rampFramesRemaining = RampFrames;
                _rampStepPerFrame = delta * InvRampFrames;
            }
            else
            {
                _currentGain = target;
                _rampFramesRemaining = 0;
                _rampStepPerFrame = 0f;
            }
        }

        ref float floatsRef = ref MemoryMarshal.GetReference(samples);
        int chCount = Math.Max(1, _channels);

        if (_rampFramesRemaining > 0)
        {
            float current = _currentGain;
            float step = _rampStepPerFrame;
            int remaining = _rampFramesRemaining;

            int frameCount = length / chCount;
            int idx = 0;
            for (int f = 0; f < frameCount; f++, idx += chCount)
            {
                if (remaining > 0)
                {
                    current += step;
                    remaining--;
                    if (remaining == 0)
                        current = target;
                }
                else
                {
                    current = target;
                }

                if (current <= 1.0f)
                {
                    for (int c = 0; c < chCount; c++)
                    {
                        float s = Unsafe.Add(ref floatsRef, idx + c) * current;
                        Unsafe.Add(ref floatsRef, idx + c) = Math.Clamp(s, -SafeCeiling, SafeCeiling);
                    }
                }
                else
                {
                    float framePeak = 0f;
                    for (int c = 0; c < chCount; c++)
                    {
                        float s = Unsafe.Add(ref floatsRef, idx + c) * current;
                        Unsafe.Add(ref floatsRef, idx + c) = s;
                        float abs = MathF.Abs(s);
                        if (abs > framePeak) framePeak = abs;
                    }

                    if (framePeak > SoftKneeThreshold)
                    {
                        float scale = SoftCeilingPositive(framePeak) * Helpers.SpanMathHelper.FastReciprocal(framePeak);
                        for (int c = 0; c < chCount; c++)
                            Unsafe.Add(ref floatsRef, idx + c) *= scale;
                    }
                }
            }

            for (; idx < length; idx++)
            {
                float s = Unsafe.Add(ref floatsRef, idx) * current;
                Unsafe.Add(ref floatsRef, idx) = current <= 1.0f
                    ? Math.Clamp(s, -SafeCeiling, SafeCeiling)
                    : SoftClipSample(s);
            }

            _currentGain = current;
            _rampFramesRemaining = remaining;
        }
        else if (_currentGain <= 1.0f)
        {
            float g = _currentGain;
            int i = 0;
            int vectorSize = Vector<float>.Count;

            if (Vector.IsHardwareAccelerated && length >= vectorSize)
            {
                var gainVec = new Vector<float>(g);
                var minVec = new Vector<float>(-SafeCeiling);
                var maxVec = new Vector<float>(SafeCeiling);

                for (; i <= length - vectorSize; i += vectorSize)
                {
                    ref var vRef = ref Unsafe.As<float, Vector<float>>(ref Unsafe.Add(ref floatsRef, i));
                    var amplified = vRef * gainVec;
                    vRef = Vector.Max(minVec, Vector.Min(maxVec, amplified));
                }
            }

            for (; i < length; i++)
            {
                float sample = Unsafe.Add(ref floatsRef, i) * g;
                Unsafe.Add(ref floatsRef, i) = Math.Clamp(sample, -SafeCeiling, SafeCeiling);
            }
        }
        else
        {
            float g = _currentGain;
            if (chCount == 2)
            {
                int pairLimit = length & ~1;
                int i = 0;
                for (; i < pairLimit; i += 2)
                {
                    float s0 = Unsafe.Add(ref floatsRef, i) * g;
                    float s1 = Unsafe.Add(ref floatsRef, i + 1) * g;
                    float a0 = MathF.Abs(s0);
                    float a1 = MathF.Abs(s1);
                    float peak = a0 > a1 ? a0 : a1;
                    if (peak > SoftKneeThreshold)
                    {
                        float scale = SoftCeilingPositive(peak) * Helpers.SpanMathHelper.FastReciprocal(peak);
                        s0 *= scale;
                        s1 *= scale;
                    }
                    Unsafe.Add(ref floatsRef, i) = s0;
                    Unsafe.Add(ref floatsRef, i + 1) = s1;
                }

                for (; i < length; i++)
                    Unsafe.Add(ref floatsRef, i) = SoftClipSample(Unsafe.Add(ref floatsRef, i) * g);
            }
            else
            {
                int frameCount = length / chCount;
                int idx = 0;
                for (int f = 0; f < frameCount; f++, idx += chCount)
                {
                    float peak = 0f;
                    for (int c = 0; c < chCount; c++)
                    {
                        float s = Unsafe.Add(ref floatsRef, idx + c) * g;
                        Unsafe.Add(ref floatsRef, idx + c) = s;
                        float abs = MathF.Abs(s);
                        if (abs > peak) peak = abs;
                    }

                    if (peak > SoftKneeThreshold)
                    {
                        float scale = SoftCeilingPositive(peak) * Helpers.SpanMathHelper.FastReciprocal(peak);
                        for (int c = 0; c < chCount; c++)
                            Unsafe.Add(ref floatsRef, idx + c) *= scale;
                    }
                }

                for (; idx < length; idx++)
                    Unsafe.Add(ref floatsRef, idx) = SoftClipSample(Unsafe.Add(ref floatsRef, idx) * g);
            }
        }
    }
}