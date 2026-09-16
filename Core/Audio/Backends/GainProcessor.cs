using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LMP.Core.Audio.Backends;

/// <summary>
/// Применяет volume gain к PCM float-сэмплам in-place.
/// </summary>
/// <remarks>
/// <para>
/// Спроектирован для вызова исключительно из потока воспроизведения (playback thread).
/// Не является потокобезопасным — <see cref="SetVolumeGain"/> принимает значение через
/// <see langword="volatile"/>, само применение происходит только внутри <see cref="Process"/>.
/// </para>
/// <para>
/// Zero-alloc hot path с аппаратным SIMD-ускорением через <see cref="Vector{T}"/>.
/// </para>
/// </remarks>
public sealed class GainProcessor
{
    #region Constants

    private const int RampSamples = 2400;

    #endregion

    #region Fields

    private volatile float _targetGain = 1.0f;
    private float _currentGain = 1.0f;
    private float _rampStartGain = 1.0f;
    private int _rampRemaining;

    #endregion

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
    /// <remarks>
    /// При изменении <see cref="SetVolumeGain"/> запускается линейный ramp длиной
    /// <c>2400</c> сэмплов (~50 мс при 48 kHz) для предотвращения щелчков.
    /// После достижения целевого значения применяется SIMD-путь (AVX2/NEON).
    /// Результат клиппируется в диапазоне [-1, 1].
    /// </remarks>
    /// <param name="samples">Interleaved PCM float-сэмплы. Изменяются in-place.</param>
    public void Process(Span<float> samples)
    {
        if (samples.IsEmpty) return;

        int length = samples.Length;
        float target = _targetGain;

        if (MathF.Abs(target - _currentGain) > 0.0005f && _rampRemaining == 0)
        {
            _rampStartGain = _currentGain;
            _rampRemaining = RampSamples;
        }

        ref float floatsRef = ref MemoryMarshal.GetReference(samples);

        if (_rampRemaining > 0)
        {
            // Ramp-интерполяция (25мс)
            for (int i = 0; i < length; i++)
            {
                if (_rampRemaining > 0)
                {
                    float t = 1f - (float)_rampRemaining / RampSamples;
                    _currentGain = _rampStartGain + (target - _rampStartGain) * t;
                    _rampRemaining--;
                }
                else
                {
                    _currentGain = target;
                }

                float sample = Unsafe.Add(ref floatsRef, i) * _currentGain;
                Unsafe.Add(ref floatsRef, i) = Math.Clamp(sample, -1f, 1f);
            }
        }
        else if (MathF.Abs(_currentGain - 1.0f) > 0.0005f)
        {
            // SIMD-путь (AVX2/Neon)
            float g = _currentGain;
            int i = 0;
            int vectorSize = Vector<float>.Count;

            if (Vector.IsHardwareAccelerated && length >= vectorSize)
            {
                var gainVec = new Vector<float>(g);
                var minVec = new Vector<float>(-1.0f);
                var maxVec = new Vector<float>(1.0f);

                for (; i <= length - vectorSize; i += vectorSize)
                {
                    var vec = new Vector<float>(samples.Slice(i, vectorSize));
                    var amplified = vec * gainVec;
                    var clamped = Vector.Max(minVec, Vector.Min(maxVec, amplified));
                    clamped.CopyTo(samples.Slice(i, vectorSize));
                }
            }

            // Хвост
            for (; i < length; i++)
            {
                float sample = Unsafe.Add(ref floatsRef, i) * g;
                Unsafe.Add(ref floatsRef, i) = Math.Clamp(sample, -1f, 1f);
            }
        }
    }
}