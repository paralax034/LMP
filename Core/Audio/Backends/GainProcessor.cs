using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LMP.Core.Audio.Backends;

/// <summary>
/// Применяет volume gain к PCM float-сэмплам in-place без фазового перекоса каналов и ступенек при движении ползунка.
/// </summary>
/// <remarks>
/// <para>
/// Спроектирован для вызова исключительно из потока воспроизведения (playback thread).
/// <see cref="SetVolumeGain"/> принимает значение потокобезопасно через <see langword="volatile"/>,
/// само вычисление огибающей происходит только внутри <see cref="Process"/>.
/// </para>
/// <para>
/// Zero-alloc hot path с аппаратным SIMD-ускорением через <see cref="Vector{T}"/> и защитой от срабатывания
/// системного лимитера микшера Windows (<c>audiodg.exe</c>) около 100% громкости.
/// </para>
/// </remarks>
public sealed class GainProcessor
{
    #region Constants

    /// <summary>
    /// Длина плавного перехода при изменении громкости в стерео-фреймах (1200 фреймов = 25 мс при 48 кГц).
    /// </summary>
    private const int RampFrames = 1200;

    private const float InvRampFrames = 1.0f / RampFrames;
    private const float GainEpsilon = 0.0001f;

    /// <summary>
    /// Безопасный потолок амплитуды для WASAPI Shared Mode (-0.13 dBFS),
    /// предотвращающий щелчки системного лимитера Windows на громкости 95–100%.
    /// </summary>
    private const float SafeCeiling = 0.985f;
    private const float SoftKneeThreshold = 0.90f;
    private const float SoftKneeHeadroom = SafeCeiling - SoftKneeThreshold; // 0.085f
    private const float InvSoftKneeHeadroom = 1.0f / SoftKneeHeadroom;

    #endregion

    #region Fields

    private volatile float _targetGain = 1.0f;
    private float _activeRampTarget = 1.0f;
    private float _currentGain = 1.0f;
    private float _rampStepPerFrame;
    private int _rampFramesRemaining;

    #endregion

    /// <summary>
    /// Мягко ограничивает пики, превышающие <see cref="SoftKneeThreshold"/> (0.90f),
    /// с сохранением непрерывности первой производной (C1) и асимптотой <see cref="SafeCeiling"/> (0.985f).
    /// Сигнал в диапазоне [-0.90, +0.90] проходит со 100% побитовой линейностью.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SoftClipSample(float x)
    {
        float abs = MathF.Abs(x);
        if (abs <= SoftKneeThreshold)
            return x;

        float u = (abs - SoftKneeThreshold) * InvSoftKneeHeadroom;
        float u2 = u * u;
        float compressed = SoftKneeThreshold + SoftKneeHeadroom * ((u + u2) / (1.0f + u + u2));
        return MathF.CopySign(compressed, x);
    }

    /// <summary>
    /// Задаёт целевой volume gain. Потокобезопасно через volatile-запись.
    /// </summary>
    /// <param name="gain">Линейный коэффициент усиления. Значения ниже 0 приводятся к 0.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetVolumeGain(float gain)
    {
        _targetGain = Math.Max(0f, gain);
    }

    /// <summary>
    /// Применяет volume gain к PCM float-сэмплам in-place.
    /// </summary>
    /// <param name="samples">Interleaved PCM float-сэмплы. Изменяются in-place.</param>
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

        if (_rampFramesRemaining > 0)
        {
            float current = _currentGain;
            float step = _rampStepPerFrame;
            int remaining = _rampFramesRemaining;

            int i = 0;
            int pairLimit = length & ~1;
            for (; i < pairLimit; i += 2)
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

                float s0 = Unsafe.Add(ref floatsRef, i) * current;
                float s1 = Unsafe.Add(ref floatsRef, i + 1) * current;
                Unsafe.Add(ref floatsRef, i) = SoftClipSample(s0);
                Unsafe.Add(ref floatsRef, i + 1) = SoftClipSample(s1);
            }

            for (; i < length; i++)
            {
                float sample = Unsafe.Add(ref floatsRef, i) * current;
                Unsafe.Add(ref floatsRef, i) = SoftClipSample(sample);
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
            for (int i = 0; i < length; i++)
            {
                float sample = Unsafe.Add(ref floatsRef, i) * g;
                Unsafe.Add(ref floatsRef, i) = SoftClipSample(sample);
            }
        }
    }
}