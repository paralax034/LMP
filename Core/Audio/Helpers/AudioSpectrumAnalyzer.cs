using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// High-precision 16-channel Mid/Side real-time spectrum analyzer (10 Mid ISO bands 31 Hz .. 16 kHz + 6 Side bands 500 Hz .. 16 kHz)
/// with broadband adaptive loudness AGC, SuperFlux vibrato-suppressed onset detection, center-weighted vocal presence,
/// and continuous spectral centroid — 0 B allocations on the WASAPI-ProAudio hot path.
/// </summary>
internal sealed class AudioSpectrumAnalyzer
{
    private const int BandCount = 10;
    private const int SideBandOffset = 4; // Side filterbank covers bands 4..9 (500 Hz .. 16 kHz)
    private const int TotalFilterLanes = 16; // 10 Mid lanes [0..9] + 6 Side lanes [10..15]

    /// <summary>Capacity of the pending level frame queue (at least 320 ms with 10 ms blocks).</summary>
    private const int PendingCapacity = 32;

    private const int Simd128GroupCount = 4; // 4 * 4 = 16 lanes
    private const int Simd256GroupCount = 2; // 2 * 8 = 16 lanes

    private const float DenormalFlushThreshold = 1e-18f;
    private const float InitialAgcReferenceRms = 0.11f;
    private const float AgcFloorRms = 0.045f;
    private const float AgcTargetRms = 0.26f;
    private const float InitialTrackRefPrimeDurationSec = 1.20f;

    [StructLayout(LayoutKind.Sequential)]
    private struct Simd256BiquadGroup
    {
        public Vector256<float> B0;
        public Vector256<float> B1;
        public Vector256<float> B2;
        public Vector256<float> A1;
        public Vector256<float> A2;
        public Vector256<float> Z1;
        public Vector256<float> Z2;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ResetState()
        {
            Z1 = Vector256<float>.Zero;
            Z2 = Vector256<float>.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Simd128BiquadGroup
    {
        public Vector128<float> B0;
        public Vector128<float> B1;
        public Vector128<float> B2;
        public Vector128<float> A1;
        public Vector128<float> A2;
        public Vector128<float> Z1;
        public Vector128<float> Z2;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ResetState()
        {
            Z1 = Vector128<float>.Zero;
            Z2 = Vector128<float>.Zero;
        }
    }

    [InlineArray(Simd256GroupCount)]
    private struct Simd256GroupArray
    {
        private Simd256BiquadGroup _element0;
    }

    [InlineArray(Simd128GroupCount)]
    private struct Simd128GroupArray
    {
        private Simd128BiquadGroup _element0;
    }

    [InlineArray(TotalFilterLanes)]
    private struct CoeffArray16
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

    [InlineArray(TotalFilterLanes)]
    private struct StateArray16
    {
        private BiquadStateF _element0;
    }

    [InlineArray(BandCount)]
    private struct FloatArray10
    {
        private float _element0;
    }

    [InlineArray(TotalFilterLanes)]
    private struct FloatArray16
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
        public float Vocal;
        public float BassFlux;
        public float MidFlux;
        public float HighFlux;
        public float SpectralCentroid;
        public float MacroEnergy;
        public float DropImpact;
        public float HarmonicEnergy;
        public float PercussiveEnergy;
        public float SnareFlux;
        public int Dominant;
        public long DueTimestamp;
    }

    [InlineArray(PendingCapacity)]
    private struct LevelQueue
    {
        private LevelFrame _element0;
    }

    // Pink-noise perceptual tilt (+3 dB/octave compensation from sub-bass to air)
    private static readonly float[] PerceptualGainsArray =
        [2.0f, 2.2f, 2.5f, 2.9f, 3.4f, 3.9f, 4.3f, 4.7f, 5.2f, 5.8f];

    private static ReadOnlySpan<float> PerceptualGains => PerceptualGainsArray;

    private readonly int _sampleRate;

    private CoeffArray16 _laneCoeffs;
    private StateArray16 _scalarStates;
    private Simd128GroupArray _simd128Groups;
    private Simd256GroupArray _simd256Groups;
    private FloatArray16 _sumSq16;
    private FloatArray10 _prevLogEnergy;
    private FloatArray10 _bandSustain;

    // Kept with exact field names for test harness UnsafeAccessor verification
    private float _beatFollowerFast;
    private float _beatFollowerSlow;

    // Slow track reference (preserves verse-to-drop macro contrast) + phrase/drop envelopes
    private float _trackRefRms = InitialAgcReferenceRms;
    private float _phraseRms;
    private float _preDropRms = InitialAgcReferenceRms;
    private float _dropImpactEnv;
    private float _fluxBaseBass;
    private float _fluxBaseMid;
    private float _fluxBaseHigh;
    private float _bassFluxEnv;
    private float _midFluxEnv;
    private float _highFluxEnv;
    private float _snareFluxEnv;
    private float _beatEnv;
    private float _smoothedCentroid = 0.35f;

    // Cached dt-dependent ballistics (recomputed only if block frameCount changes)
    private int _cachedBallisticsFrames;
    private float _cachedDtSec;
    private float _elapsedSinceResetSec;
    private float _alphaBeatFast;
    private float _alphaBeatSlow;
    private float _alphaTrackInit;
    private float _alphaTrackAttack;
    private float _alphaTrackRelease;
    private float _alphaPhraseAttack;
    private float _alphaPhraseRelease;
    private float _alphaPreDrop;
    private float _dropDecay;
    private float _alphaSustainAttack;
    private float _alphaSustainRelease;
    private float _alphaLogRef;
    private float _alphaFluxBase;
    private float _fluxDecay;
    private float _alphaCentroid;

    private LevelQueue _pending;
    private int _pendingHead;
    private int _pendingCount;
    private int _resetRequested;

    public AudioSpectrumAnalyzer(int sampleRate, int channels)
    {
        _ = channels;
        int rate = sampleRate > 0 ? sampleRate : AudioConstants.DefaultSampleRate;
        _sampleRate = rate;
        var freqs = EqualizerProcessor.BandFrequenciesHz;

        // Lanes 0..9: 10 ISO bands on Mid (L+R)/2
        for (int i = 0; i < BandCount; i++)
        {
            BiquadCoefficients c = i switch
            {
                0 => BiquadFilterMath.ComputeLowPass(rate, 48.0, 0.85),
                BandCount - 1 => BiquadFilterMath.ComputeHighPass(rate, 11500.0, 0.85),
                _ => BiquadFilterMath.ComputeBandPass(rate, freqs[i], 1.35)
            };
            _laneCoeffs[i] = BiquadCoefficientsF.FromDouble(in c);
        }

        // Lanes 10..15: Side (L-R)/2 filterbank for bands 4..9 (500 Hz .. 16 kHz)
        // Guarantees exact L/R symmetry for panned HF instruments and enables center-vocal isolation.
        for (int s = 0; s < TotalFilterLanes - BandCount; s++)
            _laneCoeffs[BandCount + s] = _laneCoeffs[SideBandOffset + s];

        for (int g = 0; g < Simd128GroupCount; g++)
        {
            int b = g * 4;
            BiquadCoefficientsF c0 = _laneCoeffs[b + 0];
            BiquadCoefficientsF c1 = _laneCoeffs[b + 1];
            BiquadCoefficientsF c2 = _laneCoeffs[b + 2];
            BiquadCoefficientsF c3 = _laneCoeffs[b + 3];

            _simd128Groups[g] = new Simd128BiquadGroup
            {
                B0 = Vector128.Create(c0.B0, c1.B0, c2.B0, c3.B0),
                B1 = Vector128.Create(c0.B1, c1.B1, c2.B1, c3.B1),
                B2 = Vector128.Create(c0.B2, c1.B2, c2.B2, c3.B2),
                A1 = Vector128.Create(c0.A1, c1.A1, c2.A1, c3.A1),
                A2 = Vector128.Create(c0.A2, c1.A2, c2.A2, c3.A2),
                Z1 = Vector128<float>.Zero,
                Z2 = Vector128<float>.Zero
            };
        }

        for (int g = 0; g < Simd256GroupCount; g++)
        {
            int b = g * 8;
            BiquadCoefficientsF c0 = _laneCoeffs[b + 0];
            BiquadCoefficientsF c1 = _laneCoeffs[b + 1];
            BiquadCoefficientsF c2 = _laneCoeffs[b + 2];
            BiquadCoefficientsF c3 = _laneCoeffs[b + 3];
            BiquadCoefficientsF c4 = _laneCoeffs[b + 4];
            BiquadCoefficientsF c5 = _laneCoeffs[b + 5];
            BiquadCoefficientsF c6 = _laneCoeffs[b + 6];
            BiquadCoefficientsF c7 = _laneCoeffs[b + 7];

            _simd256Groups[g] = new Simd256BiquadGroup
            {
                B0 = Vector256.Create(c0.B0, c1.B0, c2.B0, c3.B0, c4.B0, c5.B0, c6.B0, c7.B0),
                B1 = Vector256.Create(c0.B1, c1.B1, c2.B1, c3.B1, c4.B1, c5.B1, c6.B1, c7.B1),
                B2 = Vector256.Create(c0.B2, c1.B2, c2.B2, c3.B2, c4.B2, c5.B2, c6.B2, c7.B2),
                A1 = Vector256.Create(c0.A1, c1.A1, c2.A1, c3.A1, c4.A1, c5.A1, c6.A1, c7.A1),
                A2 = Vector256.Create(c0.A2, c1.A2, c2.A2, c3.A2, c4.A2, c5.A2, c6.A2, c7.A2),
                Z1 = Vector256<float>.Zero,
                Z2 = Vector256<float>.Zero
            };
        }
    }

    /// <summary>
    /// Analyzes a PCM sample block (0 allocations) and publishes 10 bands + perceptual features to <see cref="AudioVisualizerBuffer"/>.
    /// Called exclusively from the audio callback; executes a pending <see cref="RequestReset"/> first.
    /// </summary>
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
        fixed (float* pSumSq16 = &_sumSq16[0])
        {
            if (Avx.IsSupported && Fma.IsSupported)
                overallMaxPeak = AccumulateSimdAvx2Fma(pSamples, frameCount, channels, pSumSq16);
            else if (Fma.IsSupported)
                overallMaxPeak = AccumulateSimdSseFma(pSamples, frameCount, channels, pSumSq16);
            else if (Sse.IsSupported)
                overallMaxPeak = AccumulateSimdSse(pSamples, frameCount, channels, pSumSq16);
            else
                overallMaxPeak = AccumulateScalar(pSamples, frameCount, channels, pSumSq16);
        }

        FinalizeAndPublishLevels(frameCount, overallMaxPeak, presentationDelayTicks);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureBallistics(int frameCount)
    {
        if (_cachedBallisticsFrames == frameCount)
            return;

        _cachedBallisticsFrames = frameCount;
        float dtSec = Math.Clamp((float)frameCount / _sampleRate, 0.001f, 0.250f);
        _cachedDtSec = dtSec;

        _alphaBeatFast = 1f - MathF.Exp(-dtSec / 0.018f);
        _alphaBeatSlow = 1f - MathF.Exp(-dtSec / 0.180f);

        // Fast initial priming adapts to quiet intros within ~1.2s, then locks to slow track reference for verse-to-drop contrast
        _alphaTrackInit = 1f - MathF.Exp(-dtSec / 0.300f);
        _alphaTrackAttack = 1f - MathF.Exp(-dtSec / 1.800f);
        _alphaTrackRelease = 1f - MathF.Exp(-dtSec / 8.000f);

        _alphaPhraseAttack = 1f - MathF.Exp(-dtSec / 0.045f);
        _alphaPhraseRelease = 1f - MathF.Exp(-dtSec / 0.320f);
        _alphaPreDrop = 1f - MathF.Exp(-dtSec / 1.400f);
        _dropDecay = MathF.Exp(-dtSec / 0.420f);

        // Causal 2-speed HPSS floor: 90ms attack rejects crisp 20ms drum hits while tracking singing vowels & chords
        _alphaSustainAttack = 1f - MathF.Exp(-dtSec / 0.090f);
        _alphaSustainRelease = 1f - MathF.Exp(-dtSec / 0.045f);

        // 32 ms log-energy reference accumulates 20–40 ms musical attacks cleanly even on 10 ms WASAPI blocks
        _alphaLogRef = 1f - MathF.Exp(-dtSec / 0.032f);
        _alphaFluxBase = 1f - MathF.Exp(-dtSec / 0.260f);
        _fluxDecay = MathF.Exp(-dtSec / 0.075f);
        _alphaCentroid = 1f - MathF.Exp(-dtSec / 0.065f);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void FinalizeAndPublishLevels(int frameCount, float overallMaxPeak, long presentationDelayTicks)
    {
        EnsureBallistics(frameCount);

        float invFrames = 1.0f / frameCount;
        var gains = PerceptualGains;
        FloatArray10 levels10 = default;
        Span<float> rms10 = stackalloc float[BandCount];
        Span<float> logE10 = stackalloc float[BandCount];

        // 1. Combine Mid + Side power (Parseval-symmetric L/R energy for bands 4..9) and estimate mix RMS
        float totalMeanSq = 0f;
        for (int b = 0; b < BandCount; b++)
        {
            float meanSq = b >= SideBandOffset
                ? (_sumSq16[b] + _sumSq16[b + (BandCount - SideBandOffset)]) * invFrames
                : _sumSq16[b] * invFrames;

            if (meanSq < 0f) meanSq = 0f;
            totalMeanSq += meanSq;

            float rms = MathF.Sqrt(meanSq);
            rms10[b] = rms;
            logE10[b] = MathF.Log2(1f + rms * gains[b] * 14f);
        }

        // 2. Two-timescale macro dynamics: fast initial priming for quiet intros + slow track reference preserving verse-to-drop contrast
        float mixRms = MathF.Sqrt(totalMeanSq);
        float phraseAlpha = mixRms > _phraseRms ? _alphaPhraseAttack : _alphaPhraseRelease;
        _phraseRms += (mixRms - _phraseRms) * phraseAlpha;

        if (mixRms > 1e-4f)
        {
            bool isPriming = _elapsedSinceResetSec < InitialTrackRefPrimeDurationSec;
            _elapsedSinceResetSec += _cachedDtSec;

            float trackAlpha = isPriming
                ? _alphaTrackInit
                : (mixRms > _trackRefRms ? _alphaTrackAttack : _alphaTrackRelease);
            float preDropAlpha = isPriming ? _alphaTrackInit : _alphaPreDrop;

            _trackRefRms += (mixRms - _trackRefRms) * trackAlpha;
            _preDropRms += (mixRms - _preDropRms) * preDropAlpha;
        }
        else
        {
            _trackRefRms += (InitialAgcReferenceRms - _trackRefRms) * (_alphaTrackRelease * 0.25f);
            _preDropRms += (0f - _preDropRms) * _alphaPreDrop;
        }

        float macroRatio = _phraseRms / MathF.Max(_trackRefRms, 0.08f);
        float loudnessPresence = MathF.Min(1f, _phraseRms * 6.0f);
        float macroEnergy = Math.Clamp(macroRatio * 0.65f * loudnessPresence, 0f, 1.5f);

        // Foote-style structural novelty: detects breakdown -> drop surges cleanly
        float preDropBase = MathF.Max(_preDropRms, 0.05f);
        float rawDrop = _phraseRms > (preDropBase * 1.40f + 0.025f)
            ? Math.Clamp((_phraseRms / preDropBase - 1.35f) * 0.65f, 0f, 1f)
            : 0f;
        _dropImpactEnv = MathF.Max(rawDrop, _dropImpactEnv * _dropDecay);

        // Gentle track-level AGC (clamped to [0.55..2.4] so drops are never squashed down to verse height)
        float agcGain = Math.Clamp(AgcTargetRms / MathF.Max(_trackRefRms, AgcFloorRms), 0.55f, 2.4f);

        // 3. Perceptual band levels + causal 2-speed HPSS (Harmonic vs Percussive) + Spectral Centroid
        int dominantIdx = 0;
        float maxBandLevel = 0f;
        float centroidNum = 0f;
        float centroidDen = 0f;
        float harmonicSum = 0f;
        float percussiveMax = 0f;
        Span<float> harm10 = stackalloc float[BandCount];
        Span<float> perc10 = stackalloc float[BandCount];

        for (int b = 0; b < BandCount; b++)
        {
            float scaled = rms10[b] * agcGain * gains[b];
            float lvl = Math.Clamp(MathF.Log2(1f + scaled * 3.4f) * 0.34f, 0f, 1f);
            levels10[b] = lvl;

            float susAlpha = lvl > _bandSustain[b] ? _alphaSustainAttack : _alphaSustainRelease;
            _bandSustain[b] += (lvl - _bandSustain[b]) * susAlpha;

            float harm = MathF.Min(lvl, _bandSustain[b]);
            float perc = MathF.Max(0f, lvl - _bandSustain[b]);
            harm10[b] = harm;
            perc10[b] = perc;

            if (b <= 7) harmonicSum += harm;
            if (perc > percussiveMax) percussiveMax = perc;

            if (lvl > maxBandLevel)
            {
                maxBandLevel = lvl;
                dominantIdx = b;
            }

            float energyWeight = lvl * lvl;
            centroidNum += b * energyWeight;
            centroidDen += energyWeight;
        }

        float rawCentroid = centroidDen > 1e-4f ? centroidNum / (centroidDen * (BandCount - 1)) : _smoothedCentroid;
        _smoothedCentroid += (rawCentroid - _smoothedCentroid) * _alphaCentroid;

        float harmonicEnergy = Math.Clamp(MathF.Max(harmonicSum * 0.24f, MathF.Max(harm10[1], MathF.Max(harm10[4], harm10[5]))), 0f, 1f);
        float percussiveEnergy = Math.Clamp(percussiveMax * 1.55f, 0f, 1f);

        // 4. Macro bands (including 250 Hz band[3] for bass warmth & snare/vocal body)
        float lowLevel = MathF.Max(
            MathF.Max(levels10[0], levels10[1]),
            MathF.Max(levels10[2] * 0.90f, levels10[3] * 0.55f));
        float midLevel = MathF.Max(
            MathF.Max(levels10[3] * 0.80f, levels10[4] * 0.92f),
            MathF.Max(levels10[5], levels10[6]));
        float highLevel = MathF.Max(levels10[7], MathF.Max(levels10[8], levels10[9]));
        float overallLevel = overallMaxPeak > 1e-5f
            ? Math.Clamp(MathF.Log2(1f + overallMaxPeak * 3.4f) * 0.48f, 0f, 1f)
            : 0f;

        // 5. Center-weighted Harmonic Vocal presence (Mid >> Side AND sustained harmonicity rejects snare cracks & wide pads)
        float vocalMidSq = (_sumSq16[4] * 0.75f + _sumSq16[5] + _sumSq16[6] * 0.95f) * invFrames;
        float vocalSideSq = (_sumSq16[10] * 0.75f + _sumSq16[11] + _sumSq16[12] * 0.95f) * invFrames;
        float vocalMidRms = MathF.Sqrt(MathF.Max(0f, vocalMidSq));
        float vocalSideRms = MathF.Sqrt(MathF.Max(0f, vocalSideSq));
        float centerRatio = vocalMidRms / (vocalMidRms + vocalSideRms * 1.15f + 1e-5f);
        float vocalFocus = Math.Clamp((centerRatio - 0.30f) * 1.43f, 0f, 1f);
        float vocalBandLvl = MathF.Max(levels10[4] * 0.85f, MathF.Max(levels10[5], levels10[6] * 0.95f));
        float vocalHarmLvl = MathF.Max(harm10[4] * 0.85f, MathF.Max(harm10[5], harm10[6] * 0.95f));
        float vocalHarmRatio = vocalBandLvl > 1e-4f ? vocalHarmLvl / vocalBandLvl : 0f;
        float vocalHarmGate = Math.Clamp((vocalHarmRatio - 0.15f) * 1.65f, 0f, 1f);
        float vocalPresence = Math.Clamp(vocalBandLvl * vocalFocus * vocalHarmGate, 0f, 1f);

        // 6. Per-band logarithmic onset detection (Klapuri 1999 / Bello et al. 2005):
        // Avoids cross-octave masking on a 1-band/octave filterbank while a 32 ms EMA reference preserves 20–40 ms attacks on 10 ms blocks.
        float bassSf = 0f;
        float midSf = 0f;
        float highSf = 0f;

        for (int b = 0; b < BandCount; b++)
        {
            float prevRef = _prevLogEnergy[b];
            float sf = MathF.Max(0f, logE10[b] - prevRef);
            float nextRef = prevRef + ((logE10[b] - prevRef) * _alphaLogRef);
            _prevLogEnergy[b] = nextRef < 1e-6f ? 0f : nextRef;

            if (b <= 2)
                bassSf += sf * (b == 1 ? 1.15f : 0.90f);
            else if (b <= 6)
                midSf += sf * (b == 3 ? 0.70f : 1.0f);
            else
                highSf += sf * (b == 8 ? 1.10f : 0.95f);
        }

        _fluxBaseBass += (bassSf - _fluxBaseBass) * _alphaFluxBase;
        _fluxBaseMid += (midSf - _fluxBaseMid) * _alphaFluxBase;
        _fluxBaseHigh += (highSf - _fluxBaseHigh) * _alphaFluxBase;

        float bassOnset = Math.Clamp((bassSf - (_fluxBaseBass * 1.18f + 0.025f)) * 1.25f, 0f, 1f);
        float midOnset = Math.Clamp((midSf - (_fluxBaseMid * 1.18f + 0.022f)) * 1.15f, 0f, 1f);
        float highOnset = Math.Clamp((highSf - (_fluxBaseHigh * 1.18f + 0.022f)) * 1.20f, 0f, 1f);

        // Dedicated snare/clap transient detector: broadband body (250 Hz) + crack (1–4 kHz) percussive spike
        float snarePerc = (perc10[3] * 0.45f) + (perc10[5] * 0.65f) + (perc10[6] * 0.90f) + (perc10[7] * 0.90f);
        float rawSnare = Math.Clamp(MathF.Max(snarePerc * 0.85f, midOnset * 0.75f + highOnset * 0.45f) * (percussiveEnergy > 0.15f ? 1f : 0.3f), 0f, 1f);

        _bassFluxEnv = MathF.Max(bassOnset, _bassFluxEnv * _fluxDecay);
        _midFluxEnv = MathF.Max(midOnset, _midFluxEnv * _fluxDecay);
        _highFluxEnv = MathF.Max(highOnset, _highFluxEnv * _fluxDecay);
        _snareFluxEnv = MathF.Max(rawSnare, _snareFluxEnv * _fluxDecay);

        // 7. Block-size-independent kick/snare beat detector (combines low follower delta + SuperFlux kick/snare attack)
        float kickEnergy = MathF.Max(levels10[0], MathF.Max(levels10[1], levels10[2] * 0.80f));
        _beatFollowerFast += (kickEnergy - _beatFollowerFast) * _alphaBeatFast;
        _beatFollowerSlow += (kickEnergy - _beatFollowerSlow) * _alphaBeatSlow;

        float followerBeat = Math.Clamp(MathF.Max(0f, _beatFollowerFast - (_beatFollowerSlow * 1.08f)) * 2.8f, 0f, 1f);
        float rawBeat = Math.Clamp(MathF.Max(followerBeat, _bassFluxEnv * 0.95f + _snareFluxEnv * 0.30f), 0f, 1f);
        _beatEnv = MathF.Max(rawBeat, _beatEnv * _fluxDecay);

        if (Volatile.Read(ref _resetRequested) != 0)
        {
            Reset();
            return;
        }

        if (presentationDelayTicks <= 0)
        {
            ClearPending();
            AudioVisualizerBuffer.Update10Band(
                MemoryMarshal.CreateReadOnlySpan(ref levels10[0], BandCount),
                lowLevel,
                midLevel,
                highLevel,
                _beatEnv,
                overallLevel,
                vocalPresence,
                _bassFluxEnv,
                _midFluxEnv,
                _highFluxEnv,
                _smoothedCentroid,
                dominantIdx,
                macroEnergy,
                _dropImpactEnv,
                harmonicEnergy,
                percussiveEnergy,
                _snareFluxEnv);
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (_pendingCount == PendingCapacity)
        {
            Publish(ref _pending[_pendingHead]);
            _pendingHead = (_pendingHead + 1) % PendingCapacity;
            _pendingCount--;
        }

        ref LevelFrame slot = ref _pending[(_pendingHead + _pendingCount) % PendingCapacity];
        slot.Bands = levels10;
        slot.Low = lowLevel;
        slot.Mid = midLevel;
        slot.High = highLevel;
        slot.Beat = _beatEnv;
        slot.Overall = overallLevel;
        slot.Vocal = vocalPresence;
        slot.BassFlux = _bassFluxEnv;
        slot.MidFlux = _midFluxEnv;
        slot.HighFlux = _highFluxEnv;
        slot.SpectralCentroid = _smoothedCentroid;
        slot.MacroEnergy = macroEnergy;
        slot.DropImpact = _dropImpactEnv;
        slot.HarmonicEnergy = harmonicEnergy;
        slot.PercussiveEnergy = percussiveEnergy;
        slot.SnareFlux = _snareFluxEnv;
        slot.Dominant = dominantIdx;
        slot.DueTimestamp = now + presentationDelayTicks;
        _pendingCount++;

        PublishDueFrames(now);
    }

    /// <summary>
    /// Ultra-fast 2-pass AVX2 + FMA3 accumulation for all 16 Mid/Side filter lanes:
    /// Pass 0 processes Mid bands 0..7 + fused peak tracking; Pass 1 processes Mid 8..9 + Side 4..9 via single-cycle <c>vblendps</c>.
    /// Uses ≤ 11 YMM registers, 0 calls, 0 stack spills, and flushes subnormals at block end.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private unsafe float AccumulateSimdAvx2Fma(float* pSamples, int frameCount, int channels, float* pSumSq16)
    {
        if (!Avx.IsSupported || !Fma.IsSupported)
            return 0f;

        float maxPeak = 0f;
        int stride = channels;
        float* pEnd = pSamples + (nuint)frameCount * (nuint)stride;
        var vMinNormal = Vector256.Create(DenormalFlushThreshold);

        if (channels >= 2)
        {
            // Pass 0: Mid bands 0..7 + fused maxPeak
            {
                ref Simd256BiquadGroup grp = ref _simd256Groups[0];
                var b0 = grp.B0;
                var b1 = grp.B1;
                var b2 = grp.B2;
                var a1 = grp.A1;
                var a2 = grp.A2;
                var z1 = grp.Z1;
                var z2 = grp.Z2;
                var sumSq = Vector256<float>.Zero;

                for (float* ptr = pSamples; ptr < pEnd; ptr += stride)
                {
                    float left = ptr[0];
                    float right = ptr[1];
                    float absL = MathF.Abs(left);
                    float absR = MathF.Abs(right);
                    float peak = absL > absR ? absL : absR;
                    if (peak > maxPeak) maxPeak = peak;

                    var x = Vector256.Create((left + right) * 0.5f);
                    var y = Fma.MultiplyAdd(b0, x, z1);
                    z1 = Fma.MultiplySubtract(b1, x, Fma.MultiplySubtract(a1, y, z2));
                    z2 = Fma.MultiplySubtract(b2, x, Avx.Multiply(a2, y));
                    sumSq = Fma.MultiplyAdd(y, y, sumSq);
                }

                grp.Z1 = Vector256.ConditionalSelect(Vector256.GreaterThanOrEqual(Vector256.Abs(z1), vMinNormal), z1, Vector256<float>.Zero);
                grp.Z2 = Vector256.ConditionalSelect(Vector256.GreaterThanOrEqual(Vector256.Abs(z2), vMinNormal), z2, Vector256<float>.Zero);
                Vector256.Store(sumSq, pSumSq16);
            }

            // Pass 1: Lanes [0..1] = Mid bands 8..9, Lanes [2..7] = Side bands 4..9
            {
                ref Simd256BiquadGroup grp = ref _simd256Groups[1];
                var b0 = grp.B0;
                var b1 = grp.B1;
                var b2 = grp.B2;
                var a1 = grp.A1;
                var a2 = grp.A2;
                var z1 = grp.Z1;
                var z2 = grp.Z2;
                var sumSq = Vector256<float>.Zero;

                for (float* ptr = pSamples; ptr < pEnd; ptr += stride)
                {
                    float left = ptr[0];
                    float right = ptr[1];
                    var vMid = Vector256.Create((left + right) * 0.5f);
                    var vSide = Vector256.Create((left - right) * 0.5f);
                    var x = Avx.Blend(vSide, vMid, 0b0000_0011);

                    var y = Fma.MultiplyAdd(b0, x, z1);
                    z1 = Fma.MultiplySubtract(b1, x, Fma.MultiplySubtract(a1, y, z2));
                    z2 = Fma.MultiplySubtract(b2, x, Avx.Multiply(a2, y));
                    sumSq = Fma.MultiplyAdd(y, y, sumSq);
                }

                grp.Z1 = Vector256.ConditionalSelect(Vector256.GreaterThanOrEqual(Vector256.Abs(z1), vMinNormal), z1, Vector256<float>.Zero);
                grp.Z2 = Vector256.ConditionalSelect(Vector256.GreaterThanOrEqual(Vector256.Abs(z2), vMinNormal), z2, Vector256<float>.Zero);
                Vector256.Store(sumSq, pSumSq16 + 8);
            }
        }
        else
        {
            // Mono: Side is zero, Pass 0 computes bands 0..7 + maxPeak, Pass 1 computes bands 8..9 (zeroing side lanes 2..7)
            {
                ref Simd256BiquadGroup grp = ref _simd256Groups[0];
                var b0 = grp.B0;
                var b1 = grp.B1;
                var b2 = grp.B2;
                var a1 = grp.A1;
                var a2 = grp.A2;
                var z1 = grp.Z1;
                var z2 = grp.Z2;
                var sumSq = Vector256<float>.Zero;

                for (float* ptr = pSamples; ptr < pEnd; ptr++)
                {
                    float mono = ptr[0];
                    float abs = MathF.Abs(mono);
                    if (abs > maxPeak) maxPeak = abs;

                    var x = Vector256.Create(mono);
                    var y = Fma.MultiplyAdd(b0, x, z1);
                    z1 = Fma.MultiplySubtract(b1, x, Fma.MultiplySubtract(a1, y, z2));
                    z2 = Fma.MultiplySubtract(b2, x, Avx.Multiply(a2, y));
                    sumSq = Fma.MultiplyAdd(y, y, sumSq);
                }

                grp.Z1 = Vector256.ConditionalSelect(Vector256.GreaterThanOrEqual(Vector256.Abs(z1), vMinNormal), z1, Vector256<float>.Zero);
                grp.Z2 = Vector256.ConditionalSelect(Vector256.GreaterThanOrEqual(Vector256.Abs(z2), vMinNormal), z2, Vector256<float>.Zero);
                Vector256.Store(sumSq, pSumSq16);
            }

            {
                ref Simd256BiquadGroup grp = ref _simd256Groups[1];
                var b0 = grp.B0;
                var b1 = grp.B1;
                var b2 = grp.B2;
                var a1 = grp.A1;
                var a2 = grp.A2;
                var z1 = grp.Z1;
                var z2 = grp.Z2;
                var sumSq = Vector256<float>.Zero;
                var zero = Vector256<float>.Zero;

                for (float* ptr = pSamples; ptr < pEnd; ptr++)
                {
                    var x = Avx.Blend(zero, Vector256.Create(ptr[0]), 0b0000_0011);
                    var y = Fma.MultiplyAdd(b0, x, z1);
                    z1 = Fma.MultiplySubtract(b1, x, Fma.MultiplySubtract(a1, y, z2));
                    z2 = Fma.MultiplySubtract(b2, x, Avx.Multiply(a2, y));
                    sumSq = Fma.MultiplyAdd(y, y, sumSq);
                }

                grp.Z1 = Vector256.ConditionalSelect(Vector256.GreaterThanOrEqual(Vector256.Abs(z1), vMinNormal), z1, Vector256<float>.Zero);
                grp.Z2 = Vector256.ConditionalSelect(Vector256.GreaterThanOrEqual(Vector256.Abs(z2), vMinNormal), z2, Vector256<float>.Zero);
                Vector256.Store(sumSq, pSumSq16 + 8);
            }
        }

        return maxPeak;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private unsafe float AccumulateSimdSseFma(float* pSamples, int frameCount, int channels, float* pSumSq16)
    {
        if (!Fma.IsSupported)
            return 0f;

        float maxPeak = 0f;
        int stride = channels;
        float* pEnd = pSamples + (nuint)frameCount * (nuint)stride;
        var vMinNormal = Vector128.Create(DenormalFlushThreshold);
        ref Simd128BiquadGroup groupBase = ref _simd128Groups[0];

        if (channels >= 2)
        {
            for (int g = 0; g < Simd128GroupCount; g++)
            {
                ref Simd128BiquadGroup grp = ref Unsafe.Add(ref groupBase, g);
                var b0 = grp.B0;
                var b1 = grp.B1;
                var b2 = grp.B2;
                var a1 = grp.A1;
                var a2 = grp.A2;
                var z1 = grp.Z1;
                var z2 = grp.Z2;
                var sumSq = Vector128<float>.Zero;

                if (g == 0)
                {
                    for (float* ptr = pSamples; ptr < pEnd; ptr += stride)
                    {
                        float left = ptr[0];
                        float right = ptr[1];
                        float absL = MathF.Abs(left);
                        float absR = MathF.Abs(right);
                        float peak = absL > absR ? absL : absR;
                        if (peak > maxPeak) maxPeak = peak;

                        var x = Vector128.Create((left + right) * 0.5f);
                        var y = Fma.MultiplyAdd(b0, x, z1);
                        z1 = Fma.MultiplySubtract(b1, x, Fma.MultiplySubtract(a1, y, z2));
                        z2 = Fma.MultiplySubtract(b2, x, Sse.Multiply(a2, y));
                        sumSq = Fma.MultiplyAdd(y, y, sumSq);
                    }
                }
                else if (g == 1)
                {
                    for (float* ptr = pSamples; ptr < pEnd; ptr += stride)
                    {
                        var x = Vector128.Create((ptr[0] + ptr[1]) * 0.5f);
                        var y = Fma.MultiplyAdd(b0, x, z1);
                        z1 = Fma.MultiplySubtract(b1, x, Fma.MultiplySubtract(a1, y, z2));
                        z2 = Fma.MultiplySubtract(b2, x, Sse.Multiply(a2, y));
                        sumSq = Fma.MultiplyAdd(y, y, sumSq);
                    }
                }
                else if (g == 2)
                {
                    for (float* ptr = pSamples; ptr < pEnd; ptr += stride)
                    {
                        float left = ptr[0];
                        float right = ptr[1];
                        var vMid = Vector128.Create((left + right) * 0.5f);
                        var vSide = Vector128.Create((left - right) * 0.5f);
                        var x = Sse.Shuffle(vMid, vSide, 0x00);

                        var y = Fma.MultiplyAdd(b0, x, z1);
                        z1 = Fma.MultiplySubtract(b1, x, Fma.MultiplySubtract(a1, y, z2));
                        z2 = Fma.MultiplySubtract(b2, x, Sse.Multiply(a2, y));
                        sumSq = Fma.MultiplyAdd(y, y, sumSq);
                    }
                }
                else
                {
                    for (float* ptr = pSamples; ptr < pEnd; ptr += stride)
                    {
                        var x = Vector128.Create((ptr[0] - ptr[1]) * 0.5f);
                        var y = Fma.MultiplyAdd(b0, x, z1);
                        z1 = Fma.MultiplySubtract(b1, x, Fma.MultiplySubtract(a1, y, z2));
                        z2 = Fma.MultiplySubtract(b2, x, Sse.Multiply(a2, y));
                        sumSq = Fma.MultiplyAdd(y, y, sumSq);
                    }
                }

                grp.Z1 = Vector128.ConditionalSelect(Vector128.GreaterThanOrEqual(Vector128.Abs(z1), vMinNormal), z1, Vector128<float>.Zero);
                grp.Z2 = Vector128.ConditionalSelect(Vector128.GreaterThanOrEqual(Vector128.Abs(z2), vMinNormal), z2, Vector128<float>.Zero);
                Vector128.Store(sumSq, pSumSq16 + g * 4);
            }
        }
        else
        {
            Vector128.Store(Vector128<float>.Zero, pSumSq16 + 12);
            for (int g = 0; g < 3; g++)
            {
                ref Simd128BiquadGroup grp = ref Unsafe.Add(ref groupBase, g);
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
                    float mono = ptr[0];
                    if (g == 0)
                    {
                        float abs = MathF.Abs(mono);
                        if (abs > maxPeak) maxPeak = abs;
                    }

                    var x = g == 2
                        ? Sse.Shuffle(Vector128.Create(mono), Vector128<float>.Zero, 0x00)
                        : Vector128.Create(mono);

                    var y = Fma.MultiplyAdd(b0, x, z1);
                    z1 = Fma.MultiplySubtract(b1, x, Fma.MultiplySubtract(a1, y, z2));
                    z2 = Fma.MultiplySubtract(b2, x, Sse.Multiply(a2, y));
                    sumSq = Fma.MultiplyAdd(y, y, sumSq);
                }

                grp.Z1 = Vector128.ConditionalSelect(Vector128.GreaterThanOrEqual(Vector128.Abs(z1), vMinNormal), z1, Vector128<float>.Zero);
                grp.Z2 = Vector128.ConditionalSelect(Vector128.GreaterThanOrEqual(Vector128.Abs(z2), vMinNormal), z2, Vector128<float>.Zero);
                Vector128.Store(sumSq, pSumSq16 + g * 4);
            }
        }

        return maxPeak;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private unsafe float AccumulateSimdSse(float* pSamples, int frameCount, int channels, float* pSumSq16)
    {
        if (!Sse.IsSupported)
            return 0f;

        float maxPeak = 0f;
        int stride = channels;
        float* pEnd = pSamples + (nuint)frameCount * (nuint)stride;
        var vMinNormal = Vector128.Create(DenormalFlushThreshold);
        ref Simd128BiquadGroup groupBase = ref _simd128Groups[0];

        if (channels >= 2)
        {
            for (int g = 0; g < Simd128GroupCount; g++)
            {
                ref Simd128BiquadGroup grp = ref Unsafe.Add(ref groupBase, g);
                var b0 = grp.B0;
                var b1 = grp.B1;
                var b2 = grp.B2;
                var a1 = grp.A1;
                var a2 = grp.A2;
                var z1 = grp.Z1;
                var z2 = grp.Z2;
                var sumSq = Vector128<float>.Zero;

                if (g == 0)
                {
                    for (float* ptr = pSamples; ptr < pEnd; ptr += stride)
                    {
                        float left = ptr[0];
                        float right = ptr[1];
                        float absL = MathF.Abs(left);
                        float absR = MathF.Abs(right);
                        float peak = absL > absR ? absL : absR;
                        if (peak > maxPeak) maxPeak = peak;

                        var x = Vector128.Create((left + right) * 0.5f);
                        var y = Sse.Add(Sse.Multiply(b0, x), z1);
                        z1 = Sse.Subtract(Sse.Add(Sse.Multiply(b1, x), z2), Sse.Multiply(a1, y));
                        z2 = Sse.Subtract(Sse.Multiply(b2, x), Sse.Multiply(a2, y));
                        sumSq = Sse.Add(sumSq, Sse.Multiply(y, y));
                    }
                }
                else if (g == 1)
                {
                    for (float* ptr = pSamples; ptr < pEnd; ptr += stride)
                    {
                        var x = Vector128.Create((ptr[0] + ptr[1]) * 0.5f);
                        var y = Sse.Add(Sse.Multiply(b0, x), z1);
                        z1 = Sse.Subtract(Sse.Add(Sse.Multiply(b1, x), z2), Sse.Multiply(a1, y));
                        z2 = Sse.Subtract(Sse.Multiply(b2, x), Sse.Multiply(a2, y));
                        sumSq = Sse.Add(sumSq, Sse.Multiply(y, y));
                    }
                }
                else if (g == 2)
                {
                    for (float* ptr = pSamples; ptr < pEnd; ptr += stride)
                    {
                        float left = ptr[0];
                        float right = ptr[1];
                        var vMid = Vector128.Create((left + right) * 0.5f);
                        var vSide = Vector128.Create((left - right) * 0.5f);
                        var x = Sse.Shuffle(vMid, vSide, 0x00);

                        var y = Sse.Add(Sse.Multiply(b0, x), z1);
                        z1 = Sse.Subtract(Sse.Add(Sse.Multiply(b1, x), z2), Sse.Multiply(a1, y));
                        z2 = Sse.Subtract(Sse.Multiply(b2, x), Sse.Multiply(a2, y));
                        sumSq = Sse.Add(sumSq, Sse.Multiply(y, y));
                    }
                }
                else
                {
                    for (float* ptr = pSamples; ptr < pEnd; ptr += stride)
                    {
                        var x = Vector128.Create((ptr[0] - ptr[1]) * 0.5f);
                        var y = Sse.Add(Sse.Multiply(b0, x), z1);
                        z1 = Sse.Subtract(Sse.Add(Sse.Multiply(b1, x), z2), Sse.Multiply(a1, y));
                        z2 = Sse.Subtract(Sse.Multiply(b2, x), Sse.Multiply(a2, y));
                        sumSq = Sse.Add(sumSq, Sse.Multiply(y, y));
                    }
                }

                grp.Z1 = Vector128.ConditionalSelect(Vector128.GreaterThanOrEqual(Vector128.Abs(z1), vMinNormal), z1, Vector128<float>.Zero);
                grp.Z2 = Vector128.ConditionalSelect(Vector128.GreaterThanOrEqual(Vector128.Abs(z2), vMinNormal), z2, Vector128<float>.Zero);
                Vector128.Store(sumSq, pSumSq16 + g * 4);
            }
        }
        else
        {
            Vector128.Store(Vector128<float>.Zero, pSumSq16 + 12);
            for (int g = 0; g < 3; g++)
            {
                ref Simd128BiquadGroup grp = ref Unsafe.Add(ref groupBase, g);
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
                    float mono = ptr[0];
                    if (g == 0)
                    {
                        float abs = MathF.Abs(mono);
                        if (abs > maxPeak) maxPeak = abs;
                    }

                    var x = g == 2
                        ? Sse.Shuffle(Vector128.Create(mono), Vector128<float>.Zero, 0x00)
                        : Vector128.Create(mono);

                    var y = Sse.Add(Sse.Multiply(b0, x), z1);
                    z1 = Sse.Subtract(Sse.Add(Sse.Multiply(b1, x), z2), Sse.Multiply(a1, y));
                    z2 = Sse.Subtract(Sse.Multiply(b2, x), Sse.Multiply(a2, y));
                    sumSq = Sse.Add(sumSq, Sse.Multiply(y, y));
                }

                grp.Z1 = Vector128.ConditionalSelect(Vector128.GreaterThanOrEqual(Vector128.Abs(z1), vMinNormal), z1, Vector128<float>.Zero);
                grp.Z2 = Vector128.ConditionalSelect(Vector128.GreaterThanOrEqual(Vector128.Abs(z2), vMinNormal), z2, Vector128<float>.Zero);
                Vector128.Store(sumSq, pSumSq16 + g * 4);
            }
        }

        return maxPeak;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private unsafe float AccumulateScalar(float* pSamples, int frameCount, int channels, float* pSumSq16)
    {
        for (int i = 0; i < TotalFilterLanes; i++)
            pSumSq16[i] = 0f;

        float maxPeak = 0f;
        int stride = channels;
        float* pEnd = pSamples + (nuint)frameCount * (nuint)stride;

        for (float* ptr = pSamples; ptr < pEnd; ptr += stride)
        {
            float mid;
            float side;
            if (channels >= 2)
            {
                float left = ptr[0];
                float right = ptr[1];
                float peak = MathF.Max(MathF.Abs(left), MathF.Abs(right));
                if (peak > maxPeak) maxPeak = peak;

                mid = (left + right) * 0.5f;
                side = (left - right) * 0.5f;
            }
            else
            {
                float mono = ptr[0];
                float abs = MathF.Abs(mono);
                if (abs > maxPeak) maxPeak = abs;

                mid = mono;
                side = 0f;
            }

            for (int lane = 0; lane < TotalFilterLanes; lane++)
            {
                float x = lane < BandCount ? mid : side;
                ref readonly var c = ref _laneCoeffs[lane];
                ref var s = ref _scalarStates[lane];

                float y = c.B0 * x + s.Z1;
                s.Z1 = c.B1 * x + s.Z2 - c.A1 * y;
                s.Z2 = c.B2 * x - c.A2 * y;
                pSumSq16[lane] += y * y;
            }
        }

        for (int lane = 0; lane < TotalFilterLanes; lane++)
        {
            ref var s = ref _scalarStates[lane];
            if (MathF.Abs(s.Z1) < DenormalFlushThreshold) s.Z1 = 0f;
            if (MathF.Abs(s.Z2) < DenormalFlushThreshold) s.Z2 = 0f;
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
            frame.Vocal,
            frame.BassFlux,
            frame.MidFlux,
            frame.HighFlux,
            frame.SpectralCentroid,
            frame.Dominant,
            frame.MacroEnergy,
            frame.DropImpact,
            frame.HarmonicEnergy,
            frame.PercussiveEnergy,
            frame.SnareFlux);

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

        for (int i = 0; i < TotalFilterLanes; i++)
            _scalarStates[i].Reset();

        for (int g = 0; g < Simd128GroupCount; g++)
            _simd128Groups[g].ResetState();

        for (int g = 0; g < Simd256GroupCount; g++)
            _simd256Groups[g].ResetState();

        for (int b = 0; b < BandCount; b++)
        {
            _prevLogEnergy[b] = 0f;
            _bandSustain[b] = 0f;
        }

        _beatFollowerFast = 0f;
        _beatFollowerSlow = 0f;
        _elapsedSinceResetSec = 0f;
        _trackRefRms = InitialAgcReferenceRms;
        _phraseRms = 0f;
        _preDropRms = InitialAgcReferenceRms;
        _dropImpactEnv = 0f;
        _fluxBaseBass = 0f;
        _fluxBaseMid = 0f;
        _fluxBaseHigh = 0f;
        _bassFluxEnv = 0f;
        _midFluxEnv = 0f;
        _highFluxEnv = 0f;
        _snareFluxEnv = 0f;
        _beatEnv = 0f;
        _smoothedCentroid = 0.35f;
        ClearPending();
        AudioVisualizerBuffer.Reset();
    }
}