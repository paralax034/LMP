using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace LMP.Core.Audio.Backends;

/// <summary>
/// IWaveProvider-обёртка над <see cref="BufferedWaveProvider"/>, применяющая
/// volume gain к PCM-данным в момент чтения WaveOut (on-read path).
/// Zero-alloc hot path с аппаратным SIMD-ускорением через <see cref="Vector{T}"/>.
/// </summary>
public sealed class GainWaveProvider : IWaveProvider
{
    #region Constants

    private const int RampSamples = 2400;

    #endregion

    #region Fields

    private readonly BufferedWaveProvider _source;
    private volatile float _targetGain = 1.0f;
    private float _currentGain = 1.0f;
    private float _rampStartGain = 1.0f;
    private int _rampRemaining;

    #endregion

    public GainWaveProvider(BufferedWaveProvider source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    /// <inheritdoc/>
    public WaveFormat WaveFormat => _source.WaveFormat;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetVolumeGain(float gain)
    {
        _targetGain = Math.Max(0f, gain);
    }

    /// <inheritdoc/>
    public int Read(byte[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read == 0) return 0;

        // Zero-copy reinterpret байтового среза в float span
        var floats = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, read));
        int length = floats.Length;

        float target = _targetGain;

        if (MathF.Abs(target - _currentGain) > 0.0005f && _rampRemaining == 0)
        {
            _rampStartGain = _currentGain;
            _rampRemaining = RampSamples;
        }

        ref float floatsRef = ref MemoryMarshal.GetReference(floats);

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
                    var vec = new Vector<float>(floats.Slice(i, vectorSize));
                    var amplified = vec * gainVec;
                    var clamped = Vector.Max(minVec, Vector.Min(maxVec, amplified));
                    clamped.CopyTo(floats.Slice(i, vectorSize));
                }
            }

            // Хвост
            for (; i < length; i++)
            {
                float sample = Unsafe.Add(ref floatsRef, i) * g;
                Unsafe.Add(ref floatsRef, i) = Math.Clamp(sample, -1f, 1f);
            }
        }

        return read;
    }
}