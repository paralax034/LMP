using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// High-precision 10-band real-time spectrum analyzer (31 Hz .. 16 kHz) + vocal and kick transient detector
/// based on isolated <see cref="BiquadFilterMath"/> filters with 0 B allocations.
/// </summary>
internal sealed class AudioSpectrumAnalyzer
{
    private const int BandCount = 10;

    /// <summary>Capacity of the pending level frame queue (at least 320 ms with 10 ms blocks).</summary>
    private const int PendingCapacity = 32;

    /// <summary>Number of 4-lane SIMD groups (<c>3 * 4 = 12</c> slots for 10 bands).</summary>
    private const int SimdGroupCount = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct SimdBiquadGroup
    {
        public Vector128<float> B0;
        public Vector128<float> B1;
        public Vector128<float> B2;
        public Vector128<float> A1;
        public Vector128<float> A2;
        public Vector128<float> HighMixWeight;
        public Vector128<float> Z1;
        public Vector128<float> Z2;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ResetState()
        {
            Z1 = Vector128<float>.Zero;
            Z2 = Vector128<float>.Zero;
        }
    }

    [InlineArray(SimdGroupCount)]
    private struct SimdGroupArray
    {
        private SimdBiquadGroup _element0;
    }

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

    [InlineArray(SimdGroupCount * 4)]
    private struct FloatArray12
    {
        private float _element0;
    }

    private struct LevelFrame
    {
        public FloatArray10 Bands;
        public float Low;
        public float Mid;
        public float High;
        public float Beat;
        public float Overall;
        public int Dominant;
        public long DueTimestamp;
    }

    [InlineArray(PendingCapacity)]
    private struct LevelQueue
    {
        private LevelFrame _element0;
    }

    private static readonly float[] SensitivityGainsArray =
          [2.8f, 2.9f, 3.1f, 3.3f, 3.6f, 4.0f, 4.2f, 4.5f, 4.8f, 5.2f];

    private static ReadOnlySpan<float> SensitivityGains => SensitivityGainsArray;

    private CoeffArray10 _bandCoeffs;
    private StateArray10 _bandStates;
    private SimdGroupArray _simdGroups;
    private FloatArray12 _sumSq12;

    private float _beatFollowerFast;
    private float _beatFollowerSlow;

    private LevelQueue _pending;
    private int _pendingHead;
    private int _pendingCount;
    private int _resetRequested;

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

        for (int g = 0; g < SimdGroupCount; g++)
        {
            int baseIdx = g * 4;
            BiquadCoefficientsF c0 = baseIdx + 0 < BandCount ? _bandCoeffs[baseIdx + 0] : default;
            BiquadCoefficientsF c1 = baseIdx + 1 < BandCount ? _bandCoeffs[baseIdx + 1] : default;
            BiquadCoefficientsF c2 = baseIdx + 2 < BandCount ? _bandCoeffs[baseIdx + 2] : default;
            BiquadCoefficientsF c3 = baseIdx + 3 < BandCount ? _bandCoeffs[baseIdx + 3] : default;

            float w0 = baseIdx + 0 >= 7 && baseIdx + 0 < BandCount ? 0.35f : 0f;
            float w1 = baseIdx + 1 >= 7 && baseIdx + 1 < BandCount ? 0.35f : 0f;
            float w2 = baseIdx + 2 >= 7 && baseIdx + 2 < BandCount ? 0.35f : 0f;
            float w3 = baseIdx + 3 >= 7 && baseIdx + 3 < BandCount ? 0.35f : 0f;

            _simdGroups[g] = new SimdBiquadGroup
            {
                B0 = Vector128.Create(c0.B0, c1.B0, c2.B0, c3.B0),
                B1 = Vector128.Create(c0.B1, c1.B1, c2.B1, c3.B1),
                B2 = Vector128.Create(c0.B2, c1.B2, c2.B2, c3.B2),
                A1 = Vector128.Create(c0.A1, c1.A1, c2.A1, c3.A1),
                A2 = Vector128.Create(c0.A2, c1.A2, c2.A2, c3.A2),
                HighMixWeight = Vector128.Create(w0, w1, w2, w3),
                Z1 = Vector128<float>.Zero,
                Z2 = Vector128<float>.Zero
            };
        }
    }

    /// <summary>
    /// Analyzes a PCM sample block (0 allocations) and publishes 10 bands + aggregates to <see cref="AudioVisualizerBuffer"/>.
    /// Called exclusively from the audio callback; executes a pending <see cref="RequestReset"/> first.
    /// </summary>
    /// <param name="samples">Interleaved PCM of the block just submitted to the device.</param>
    /// <param name="channels">Number of channels in <paramref name="samples"/>.</param>
    /// <param name="presentationDelayTicks">
    /// Time until the block actually plays in <see cref="Stopwatch"/> ticks (device buffer depth);
    /// when ≤ 0, levels are published immediately.
    /// </param>
    public unsafe void ProcessBlock(ReadOnlySpan<float> samples, int channels, long presentationDelayTicks = 0)
    {
        if (Volatile.Read(ref _resetRequested) != 0)
            Reset();

        int totalSamples = samples.Length;
        if (totalSamples == 0 || channels <= 0)
        {
            ClearPending();
            AudioVisualizerBuffer.Reset();
            return;
        }

        int frameCount = totalSamples / channels;
        if (frameCount <= 0)
        {
            ClearPending();
            AudioVisualizerBuffer.Reset();
            return;
        }

        float overallMaxPeak;
        fixed (float* pSamples = samples)
        fixed (float* pSumSq12 = &_sumSq12[0])
        {
            if (Fma.IsSupported)
                overallMaxPeak = AccumulateSimdFma(pSamples, frameCount, channels, pSumSq12);
            else if (Sse.IsSupported)
                overallMaxPeak = AccumulateSimdSse(pSamples, frameCount, channels, pSumSq12);
            else
                overallMaxPeak = AccumulateScalar(pSamples, frameCount, channels, pSumSq12);
        }

        FinalizeAndPublishLevels(frameCount, overallMaxPeak, presentationDelayTicks);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void FinalizeAndPublishLevels(int frameCount, float overallMaxPeak, long presentationDelayTicks)
    {
        float invFrames = 1.0f / frameCount;
        var gains = SensitivityGains;
        FloatArray10 levels10 = default;

        int dominantIdx = 0;
        float maxBandLevel = 0f;

        for (int b = 0; b < BandCount; b++)
        {
            float rms = MathF.Sqrt(_sumSq12[b] * invFrames);
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

        if (presentationDelayTicks <= 0)
        {
            ClearPending();
            AudioVisualizerBuffer.Update10Band(
                MemoryMarshal.CreateReadOnlySpan(ref levels10[0], BandCount),
                lowLevel,
                midLevel,
                highLevel,
                beatPulse,
                overallLevel,
                dominantIdx);
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (_pendingCount == PendingCapacity)
        {
            // Queue overflow: render the oldest frame early instead of dropping it
            Publish(ref _pending[_pendingHead]);
            _pendingHead = (_pendingHead + 1) % PendingCapacity;
            _pendingCount--;
        }

        ref LevelFrame slot = ref _pending[(_pendingHead + _pendingCount) % PendingCapacity];
        slot.Bands = levels10;
        slot.Low = lowLevel;
        slot.Mid = midLevel;
        slot.High = highLevel;
        slot.Beat = beatPulse;
        slot.Overall = overallLevel;
        slot.Dominant = dominantIdx;
        slot.DueTimestamp = now + presentationDelayTicks;
        _pendingCount++;

        PublishDueFrames(now);
    }

    /// <summary>
    /// Vectorized energy accumulation of 10 bands across 3 groups of 4 channels (<see cref="System.Runtime.Intrinsics.X86.Fma"/>):
    /// 11 XMM registers per pass, 0 <c>call</c>s, 0 stack accesses inside the loop.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private unsafe float AccumulateSimdFma(float* pSamples, int frameCount, int channels, float* pSumSq12)
    {
        if (!Fma.IsSupported)
            return 0f;

        float maxPeak = 0f;
        int stride = channels;
        float* pEnd = pSamples + (nuint)frameCount * (nuint)stride;

        if (channels >= 2)
        {
            for (float* ptr = pSamples; ptr < pEnd; ptr += stride)
            {
                float absL = MathF.Abs(ptr[0]);
                float absR = MathF.Abs(ptr[1]);
                float peak = absL > absR ? absL : absR;
                if (peak > maxPeak)
                    maxPeak = peak;
            }

            ref SimdBiquadGroup groupBase = ref _simdGroups[0];
            for (int g = 0; g < SimdGroupCount; g++)
            {
                ref SimdBiquadGroup grp = ref Unsafe.Add(ref groupBase, g);
                var b0 = grp.B0;
                var b1 = grp.B1;
                var b2 = grp.B2;
                var a1 = grp.A1;
                var a2 = grp.A2;
                var wHigh = grp.HighMixWeight;
                var z1 = grp.Z1;
                var z2 = grp.Z2;
                var sumSq = Vector128<float>.Zero;

                for (float* ptr = pSamples; ptr < pEnd; ptr += stride)
                {
                    float left = ptr[0];
                    float right = ptr[1];
                    var vCenter = Vector128.Create((left + right) * 0.5f);
                    var vDiff = Vector128.Create(left - right);
                    var x = Fma.MultiplyAdd(vDiff, wHigh, vCenter);

                    var y = Fma.MultiplyAdd(b0, x, z1);
                    z1 = Fma.MultiplySubtract(b1, x, Fma.MultiplySubtract(a1, y, z2));
                    z2 = Fma.MultiplySubtract(b2, x, Sse.Multiply(a2, y));
                    sumSq = Fma.MultiplyAdd(y, y, sumSq);
                }

                grp.Z1 = z1;
                grp.Z2 = z2;
                Vector128.Store(sumSq, pSumSq12 + g * 4);
            }
        }
        else
        {
            for (float* ptr = pSamples; ptr < pEnd; ptr++)
            {
                float abs = MathF.Abs(ptr[0]);
                if (abs > maxPeak)
                    maxPeak = abs;
            }

            ref SimdBiquadGroup groupBase = ref _simdGroups[0];
            for (int g = 0; g < SimdGroupCount; g++)
            {
                ref SimdBiquadGroup grp = ref Unsafe.Add(ref groupBase, g);
                var b0 = grp.B0;
                var b1 = grp.B1;
                var b2 = grp.B2;
                var a1 = grp.A1;
                var a2 = grp.A2;
                var z1 = grp.Z1;
                var z2 = grp.Z2;
                var sumSq = Vector128<float>.Zero;

                for (float* ptr = pSamples; ptr < pEnd; ptr++)
                {
                    var x = Vector128.Create(ptr[0]);
                    var y = Fma.MultiplyAdd(b0, x, z1);
                    z1 = Fma.MultiplySubtract(b1, x, Fma.MultiplySubtract(a1, y, z2));
                    z2 = Fma.MultiplySubtract(b2, x, Sse.Multiply(a2, y));
                    sumSq = Fma.MultiplyAdd(y, y, sumSq);
                }

                grp.Z1 = z1;
                grp.Z2 = z2;
                Vector128.Store(sumSq, pSumSq12 + g * 4);
            }
        }

        return maxPeak;
    }

    /// <summary>
    /// Vectorized energy accumulation of 10 bands across 3 groups of 4 channels for the baseline SSE profile:
    /// 0 <c>call</c>s, 0 stack accesses inside the loop.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private unsafe float AccumulateSimdSse(float* pSamples, int frameCount, int channels, float* pSumSq12)
    {
        if (!Sse.IsSupported)
            return 0f;

        float maxPeak = 0f;
        int stride = channels;
        float* pEnd = pSamples + (nuint)frameCount * (nuint)stride;

        if (channels >= 2)
        {
            for (float* ptr = pSamples; ptr < pEnd; ptr += stride)
            {
                float absL = MathF.Abs(ptr[0]);
                float absR = MathF.Abs(ptr[1]);
                float peak = absL > absR ? absL : absR;
                if (peak > maxPeak)
                    maxPeak = peak;
            }

            ref SimdBiquadGroup groupBase = ref _simdGroups[0];
            for (int g = 0; g < SimdGroupCount; g++)
            {
                ref SimdBiquadGroup grp = ref Unsafe.Add(ref groupBase, g);
                var b0 = grp.B0;
                var b1 = grp.B1;
                var b2 = grp.B2;
                var a1 = grp.A1;
                var a2 = grp.A2;
                var wHigh = grp.HighMixWeight;
                var z1 = grp.Z1;
                var z2 = grp.Z2;
                var sumSq = Vector128<float>.Zero;

                for (float* ptr = pSamples; ptr < pEnd; ptr += stride)
                {
                    float left = ptr[0];
                    float right = ptr[1];
                    var vCenter = Vector128.Create((left + right) * 0.5f);
                    var vDiff = Vector128.Create(left - right);
                    var x = Sse.Add(vCenter, Sse.Multiply(vDiff, wHigh));

                    var y = Sse.Add(Sse.Multiply(b0, x), z1);
                    z1 = Sse.Subtract(Sse.Add(Sse.Multiply(b1, x), z2), Sse.Multiply(a1, y));
                    z2 = Sse.Subtract(Sse.Multiply(b2, x), Sse.Multiply(a2, y));
                    sumSq = Sse.Add(sumSq, Sse.Multiply(y, y));
                }

                grp.Z1 = z1;
                grp.Z2 = z2;
                Vector128.Store(sumSq, pSumSq12 + g * 4);
            }
        }
        else
        {
            for (float* ptr = pSamples; ptr < pEnd; ptr++)
            {
                float abs = MathF.Abs(ptr[0]);
                if (abs > maxPeak)
                    maxPeak = abs;
            }

            ref SimdBiquadGroup groupBase = ref _simdGroups[0];
            for (int g = 0; g < SimdGroupCount; g++)
            {
                ref SimdBiquadGroup grp = ref Unsafe.Add(ref groupBase, g);
                var b0 = grp.B0;
                var b1 = grp.B1;
                var b2 = grp.B2;
                var a1 = grp.A1;
                var a2 = grp.A2;
                var z1 = grp.Z1;
                var z2 = grp.Z2;
                var sumSq = Vector128<float>.Zero;

                for (float* ptr = pSamples; ptr < pEnd; ptr++)
                {
                    var x = Vector128.Create(ptr[0]);
                    var y = Sse.Add(Sse.Multiply(b0, x), z1);
                    z1 = Sse.Subtract(Sse.Add(Sse.Multiply(b1, x), z2), Sse.Multiply(a1, y));
                    z2 = Sse.Subtract(Sse.Multiply(b2, x), Sse.Multiply(a2, y));
                    sumSq = Sse.Add(sumSq, Sse.Multiply(y, y));
                }

                grp.Z1 = z1;
                grp.Z2 = z2;
                Vector128.Store(sumSq, pSumSq12 + g * 4);
            }
        }

        return maxPeak;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private unsafe float AccumulateScalar(float* pSamples, int frameCount, int channels, float* pSumSq12)
    {
        for (int b = 0; b < SimdGroupCount * 4; b++)
            pSumSq12[b] = 0f;

        float maxPeak = 0f;
        int stride = channels;
        float* pEnd = pSamples + (nuint)frameCount * (nuint)stride;

        for (float* ptr = pSamples; ptr < pEnd; ptr += stride)
        {
            float center;
            float highMix;
            if (channels >= 2)
            {
                float left = ptr[0];
                float right = ptr[1];
                float peak = MathF.Max(MathF.Abs(left), MathF.Abs(right));
                if (peak > maxPeak)
                    maxPeak = peak;

                center = (left + right) * 0.5f;
                highMix = center + (left - right) * 0.35f;
            }
            else
            {
                float mono = ptr[0];
                float abs = MathF.Abs(mono);
                if (abs > maxPeak)
                    maxPeak = abs;

                center = mono;
                highMix = mono;
            }

            for (int b = 0; b < BandCount; b++)
            {
                float x = b >= 7 ? highMix : center;
                ref readonly var c = ref _bandCoeffs[b];
                ref var s = ref _bandStates[b];

                float y = c.B0 * x + s.Z1;
                s.Z1 = (c.B1 * x + s.Z2) - c.A1 * y;
                s.Z2 = c.B2 * x - c.A2 * y;
                pSumSq12[b] += y * y;
            }
        }

        return maxPeak;
    }

    /// <summary>
    /// Dequeues all frames from the head of the queue whose presentation time has arrived and publishes the latest one.
    /// </summary>
    private void PublishDueFrames(long now)
    {
        int due = 0;
        while (due < _pendingCount && _pending[(_pendingHead + due) % PendingCapacity].DueTimestamp <= now)
            due++;

        if (due == 0)
            return;

        Publish(ref _pending[(_pendingHead + due - 1) % PendingCapacity]);
        _pendingHead = (_pendingHead + due) % PendingCapacity;
        _pendingCount -= due;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Publish(ref LevelFrame frame) =>
        AudioVisualizerBuffer.Update10Band(
            MemoryMarshal.CreateReadOnlySpan(ref frame.Bands[0], BandCount),
            frame.Low,
            frame.Mid,
            frame.High,
            frame.Beat,
            frame.Overall,
            frame.Dominant);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClearPending()
    {
        _pendingHead = 0;
        _pendingCount = 0;
    }

    /// <summary>
    /// Thread-safely requests a reset: filters are zeroed by the next <see cref="ProcessBlock"/> inside the audio callback,
    /// while published levels are cleared immediately.
    /// </summary>
    public void RequestReset()
    {
        Volatile.Write(ref _resetRequested, 1);
        AudioVisualizerBuffer.Reset();
    }

    /// <summary>
    /// Resets internal filter delay lines and envelope state.
    /// Called exclusively from the audio callback (or before starting the backend); other threads must use <see cref="RequestReset"/>.
    /// </summary>
    public void Reset()
    {
        Volatile.Write(ref _resetRequested, 0);

        for (int b = 0; b < BandCount; b++)
            _bandStates[b].Reset();

        for (int g = 0; g < SimdGroupCount; g++)
            _simdGroups[g].ResetState();

        _beatFollowerFast = 0f;
        _beatFollowerSlow = 0f;
        ClearPending();
        AudioVisualizerBuffer.Reset();
    }
}