using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// Custom or built-in equalizer preset.
/// </summary>
public sealed class EqualizerPreset
{
    /// <summary>Unique preset identifier.</summary>
    public string Id { get; set; } = "";

    /// <summary>Display name (for user presets) or localization key.</summary>
    public string Name { get; set; } = "";

    /// <summary>Indicates whether this is a built-in system preset (cannot be deleted).</summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>Gain values for 10 ISO bands in dB [-12..+12].</summary>
    public float[] Bands { get; set; } = new float[EqualizerProcessor.BandCount];
}

/// <summary>
/// Built-in music preset catalog and converter from 5 macro bands to 10 ISO bands.
/// </summary>
public static class EqualizerPresets
{
    public const string FlatId = "flat";
    public const string WarmId = "warm_smooth";
    public const string BassBoostId = "deep_bass";
    public const string VocalId = "vocal_clear";
    public const string LoFiId = "lofi_cozy";
    public const string NightId = "late_night";
    public const string AnimeId = "anime_ost";
    public const string BreakcoreId = "breakcore_edm";
    public const string AmbientId = "ambient_chill";
    public const string RockId = "rock_grunge";
    public const string RapId = "rap_hiphop";
    public const string PopId = "pop_lyric";
    public const string AcousticId = "acoustic_jazz";
    public const string CustomId = "custom";

    // Aligned studio curves with no harshness at 4–8 kHz (smooth top end, clean midrange 250–500 Hz)
    // Bands:                        31     62    125    250    500     1k     2k     4k     8k    16k
    private static readonly float[] FlatBands = [0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f];
    private static readonly float[] WarmBands = [2.5f, 3.0f, 2.0f, 0.5f, -0.5f, 0.0f, -0.5f, -1.5f, -2.0f, -1.0f];
    private static readonly float[] BassBoostBands = [4.5f, 5.0f, 2.5f, -1.0f, -1.0f, 0.0f, 0.5f, 0.5f, 0.0f, 0.5f];
    private static readonly float[] VocalBands = [-1.5f, -1.0f, -0.5f, -1.5f, 1.0f, 3.0f, 2.5f, 1.0f, 0.0f, 0.5f];
    private static readonly float[] LoFiBands = [2.0f, 3.5f, 2.5f, 1.0f, 0.5f, 0.0f, -1.5f, -3.0f, -4.5f, -6.0f];
    private static readonly float[] NightBands = [-2.0f, 1.5f, 2.0f, 0.5f, 0.0f, 0.5f, 0.0f, -2.0f, -2.5f, -1.5f];
    private static readonly float[] AnimeBands = [2.0f, 2.5f, 0.5f, -1.5f, -0.5f, 2.0f, 2.0f, 1.0f, 0.5f, 1.5f];
    private static readonly float[] BreakcoreBands = [4.0f, 4.5f, 1.5f, -2.0f, -1.5f, 0.0f, 1.5f, 1.0f, 0.5f, 1.5f];
    private static readonly float[] AmbientBands = [3.5f, 3.0f, 1.5f, 0.0f, -1.0f, -0.5f, 0.5f, 0.5f, 1.0f, 2.5f];
    private static readonly float[] RockBands = [2.5f, 3.0f, 1.5f, -1.5f, -1.0f, 1.0f, 2.0f, 1.5f, 0.5f, 1.0f];
    private static readonly float[] RapBands = [4.5f, 5.0f, 2.0f, -1.5f, -1.0f, 1.0f, 1.5f, 0.5f, 0.0f, 1.0f];
    private static readonly float[] PopBands = [2.0f, 2.5f, 1.0f, -1.0f, -0.5f, 1.5f, 1.5f, 1.0f, 0.5f, 1.5f];
    private static readonly float[] AcousticBands = [1.5f, 2.0f, 1.0f, -1.0f, 0.5f, 1.5f, 1.5f, 0.5f, 0.5f, 1.5f];

    private static readonly string[] BuiltInIdsArray =
    [
        FlatId,
        WarmId,
        BassBoostId,
        VocalId,
        LoFiId,
        NightId,
        AnimeId,
        BreakcoreId,
        AmbientId,
        RockId,
        RapId,
        PopId,
        AcousticId
    ];

    public static ReadOnlySpan<string> BuiltInIds => BuiltInIdsArray;

    /// <summary>
    /// Returns 10 bands of a built-in preset without allocations.
    /// </summary>
    public static ReadOnlySpan<float> GetPresetBands(string? presetId) => presetId switch
    {
        WarmId => WarmBands,
        BassBoostId => BassBoostBands,
        VocalId => VocalBands,
        LoFiId => LoFiBands,
        NightId => NightBands,
        AnimeId => AnimeBands,
        BreakcoreId => BreakcoreBands,
        AmbientId => AmbientBands,
        RockId => RockBands,
        RapId => RapBands,
        PopId => PopBands,
        AcousticId => AcousticBands,
        _ => FlatBands
    };

    /// <summary>
    /// Returns the localization key for a built-in preset.
    /// </summary>
    public static string GetLocalizationKey(string presetId) => presetId switch
    {
        FlatId => "Eq_Preset_Flat",
        WarmId => "Eq_Preset_Warm",
        BassBoostId => "Eq_Preset_BassBoost",
        VocalId => "Eq_Preset_Vocal",
        LoFiId => "Eq_Preset_LoFi",
        NightId => "Eq_Preset_Night",
        AnimeId => "Eq_Preset_Anime",
        BreakcoreId => "Eq_Preset_Breakcore",
        AmbientId => "Eq_Preset_Ambient",
        RockId => "Eq_Preset_Rock",
        RapId => "Eq_Preset_Rap",
        PopId => "Eq_Preset_Pop",
        AcousticId => "Eq_Preset_Acoustic",
        CustomId => "Eq_Preset_Custom",
        _ => presetId
    };

    /// <summary>
    /// Converts 5 psychoacoustic macro bands (Bass, Warmth, Vocals, Clarity, Air) into 10 canonical ISO bands.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MapMacro5ToIso10(ReadOnlySpan<float> macro5, Span<float> iso10)
    {
        if (macro5.Length < 5 || iso10.Length < EqualizerProcessor.BandCount) return;

        float bass = Math.Clamp(macro5[0], -12f, 12f);
        float warmth = Math.Clamp(macro5[1], -12f, 12f);
        float vocals = Math.Clamp(macro5[2], -12f, 12f);
        float clarity = Math.Clamp(macro5[3], -12f, 12f);
        float air = Math.Clamp(macro5[4], -12f, 12f);

        iso10[0] = bass;
        iso10[1] = bass;
        iso10[2] = warmth;
        iso10[3] = warmth;
        iso10[4] = vocals;
        iso10[5] = vocals;
        iso10[6] = clarity;
        iso10[7] = clarity;
        iso10[8] = air;
        iso10[9] = air;
    }

    /// <summary>
    /// Extracts 5 macro bands from 10 ISO bands for simplified UI mode display.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MapIso10ToMacro5(ReadOnlySpan<float> iso10, Span<float> macro5)
    {
        if (iso10.Length < EqualizerProcessor.BandCount || macro5.Length < 5) return;

        macro5[0] = iso10[1]; // 62 Hz (Sub & Punch)
        macro5[1] = iso10[3]; // 250 Hz (Warmth / Mud)
        macro5[2] = iso10[5]; // 1 kHz (Vocals & Emotion)
        macro5[3] = iso10[7]; // 4 kHz (Drive & Clarity)
        macro5[4] = iso10[9]; // 16 kHz (Air & Sparkle)
    }

    /// <summary>
    /// Resolves effective 10 equalizer bands based on priority:
    /// <c>ForceGlobalEqualizer</c> -> <c>TrackInfo.EqualizerPresetId</c> -> <c>GlobalEqualizerPresetId</c>.
    /// </summary>
    public const string CustomTrackPrefix = "custom:";

    /// <summary>
    /// Determines whether the specified preset identifier represents a global or per-track custom curve.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsCustomPresetId(string? presetId) =>
        string.Equals(presetId, CustomId, StringComparison.Ordinal) ||
        (presetId != null && presetId.StartsWith(CustomTrackPrefix, StringComparison.Ordinal));

    /// <summary>
    /// Determines whether the specified preset identifier is a built-in preset.
    /// </summary>
    public static bool IsBuiltInPreset(string? presetId)
    {
        if (string.IsNullOrEmpty(presetId)) return false;
        var builtIn = BuiltInIds;
        for (int i = 0; i < builtIn.Length; i++)
        {
            if (string.Equals(builtIn[i], presetId, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Determines whether the specified preset identifier is a valid custom curve, built-in preset, or existing user preset.
    /// </summary>
    public static bool IsKnownPresetId(AudioSettings audioSettings, string? presetId)
    {
        if (string.IsNullOrEmpty(presetId))
            return false;

        if (IsCustomPresetId(presetId) || IsBuiltInPreset(presetId))
            return true;

        var userPresets = audioSettings.UserEqualizerPresets;
        if (userPresets is { Count: > 0 })
        {
            for (int i = 0; i < userPresets.Count; i++)
            {
                if (string.Equals(userPresets[i].Id, presetId, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether any band or preamp gain deviates from 0 dB beyond the deadzone threshold.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasNonZeroBands(ReadOnlySpan<float> bands, float preampDb = 0f)
    {
        if (MathF.Abs(preampDb) > 0.05f)
            return true;

        for (int i = 0; i < bands.Length; i++)
        {
            if (MathF.Abs(bands[i]) > 0.05f)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Encodes 10 ISO bands into a compact per-track preset string (<c>custom:b0,b1,...,b9</c>)
    /// so per-track custom curves persist in <see cref="TrackInfo.EqualizerPresetId"/>.
    /// </summary>
    public static string EncodeCustomTrackBands(ReadOnlySpan<float> bands)
    {
        if (!HasNonZeroBands(bands))
            return FlatId;

        Span<char> buf = stackalloc char[128];
        CustomTrackPrefix.AsSpan().CopyTo(buf);
        int pos = CustomTrackPrefix.Length;

        for (int i = 0; i < EqualizerProcessor.BandCount; i++)
        {
            if (i > 0)
                buf[pos++] = ',';

            float clamped = i < bands.Length
                ? Math.Clamp(bands[i], EqualizerProcessor.MinGainDb, EqualizerProcessor.MaxGainDb)
                : 0f;
            float val = MathF.Round(clamped * 2f) * 0.5f;

            if (!val.TryFormat(
                    buf[pos..],
                    out int written,
                    "0.#",
                    System.Globalization.CultureInfo.InvariantCulture))
            {
                break;
            }

            pos += written;
        }

        return new string(buf[..pos]);
    }

    /// <summary>
    /// Attempts to parse a per-track custom curve string (<c>custom:b0,b1,...,b9</c>) into 10 ISO bands.
    /// </summary>
    public static bool TryParseCustomTrackBands(string? presetId, Span<float> destinationBands)
    {
        if (presetId == null
            || !presetId.StartsWith(CustomTrackPrefix, StringComparison.Ordinal)
            || destinationBands.Length < EqualizerProcessor.BandCount)
        {
            return false;
        }

        destinationBands.Clear();
        ReadOnlySpan<char> rem = presetId.AsSpan(CustomTrackPrefix.Length);
        int bandIdx = 0;

        while (!rem.IsEmpty && bandIdx < EqualizerProcessor.BandCount)
        {
            int commaIdx = rem.IndexOf(',');
            ReadOnlySpan<char> token;
            if (commaIdx >= 0)
            {
                token = rem[..commaIdx];
                rem = rem[(commaIdx + 1)..];
            }
            else
            {
                token = rem;
                rem = default;
            }

            if (float.TryParse(
                    token,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out float val))
            {
                destinationBands[bandIdx] = Math.Clamp(
                    val,
                    EqualizerProcessor.MinGainDb,
                    EqualizerProcessor.MaxGainDb);
            }

            bandIdx++;
        }

        return bandIdx > 0;
    }

    /// <summary>
    /// Resolves effective 10 equalizer bands based on priority:
    /// <c>ForceGlobalEqualizer</c> -> <c>TrackInfo.EqualizerPresetId</c> -> <c>GlobalEqualizerPresetId</c>.
    /// </summary>
    public static string ResolveEffectiveBands(
        AudioSettings audioSettings,
        TrackInfo? track,
        Span<float> destinationBands)
    {
        destinationBands.Clear();
        if (destinationBands.Length < EqualizerProcessor.BandCount)
            return FlatId;

        string targetPresetId = audioSettings.GlobalEqualizerPresetId;

        if (!audioSettings.ForceGlobalEqualizer
            && track != null
            && IsKnownPresetId(audioSettings, track.EqualizerPresetId))
        {
            targetPresetId = track.EqualizerPresetId!;
        }

        if (string.IsNullOrEmpty(targetPresetId))
            targetPresetId = FlatId;

        if (TryParseCustomTrackBands(targetPresetId, destinationBands))
            return HasNonZeroBands(destinationBands) ? CustomId : FlatId;

        if (string.Equals(targetPresetId, CustomId, StringComparison.Ordinal))
        {
            var custom = audioSettings.CustomEqualizerBands;
            if (custom != null)
            {
                int copyLen = Math.Min(custom.Length, EqualizerProcessor.BandCount);
                custom.AsSpan(0, copyLen).CopyTo(destinationBands);
            }
            return CustomId;
        }

        var userPresets = audioSettings.UserEqualizerPresets;
        if (userPresets is { Count: > 0 })
        {
            for (int i = 0; i < userPresets.Count; i++)
            {
                var up = userPresets[i];
                if (string.Equals(up.Id, targetPresetId, StringComparison.Ordinal) && up.Bands != null)
                {
                    int copyLen = Math.Min(up.Bands.Length, EqualizerProcessor.BandCount);
                    up.Bands.AsSpan(0, copyLen).CopyTo(destinationBands);
                    return up.Id;
                }
            }
        }

        if (!IsBuiltInPreset(targetPresetId))
            targetPresetId = FlatId;

        var builtIn = GetPresetBands(targetPresetId);
        builtIn.CopyTo(destinationBands);
        return targetPresetId;
    }
}

/// <summary>
/// High-performance real-time 10-band parametric/graphic equalizer.
/// </summary>
public sealed class EqualizerProcessor
{
    public const int BandCount = 10;
    public const float MinGainDb = -12.0f;
    public const float MaxGainDb = 12.0f;

    /// <summary>Duration of the active smoothing window in stereo frames (1440 frames = 30 ms at 48 kHz).</summary>
    private const int RampTotalFrames = 1440;

    /// <summary>
    /// Coefficient of the per-sample 1-pole low-pass filter (Per-Sample 1-Pole Low-Pass Smoother):
    /// alpha = 0.0042f (time constant tau ~ 5 ms, converges to 99.8% in 1440 samples, 0 quantization steps on HF!).
    /// </summary>
    private const float SampleSmoothAlpha = 0.0042f;

    /// <summary>Standard center frequencies of 10 ISO octave bands (Hz).</summary>
    public static ReadOnlySpan<float> BandFrequenciesHz =>
        [31.25f, 62.5f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f];

    [StructLayout(LayoutKind.Sequential)]
    private struct SvfCoefficientsF
    {
        public float A1;
        public float A2;
        public float A3;
        public float PadA;
        public float M0;
        public float M1;
        public float M2;
        public float PadM;

        /// <summary>
        /// Per-sample step of the 1-pole LPF coefficient smoother (1-pole LPF without sub-block HF stepping).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SmoothStepTowards(in SvfCoefficientsF target, float alpha)
        {
            if (Sse.IsSupported)
            {
                ref float selfRef = ref Unsafe.As<SvfCoefficientsF, float>(ref this);
                ref float targetRef = ref Unsafe.As<SvfCoefficientsF, float>(ref Unsafe.AsRef(in target));

                var vAlpha = Vector128.Create(alpha);
                var curA = Vector128.LoadUnsafe(ref selfRef);
                var tgtA = Vector128.LoadUnsafe(ref targetRef);
                var curM = Vector128.LoadUnsafe(ref selfRef, 4);
                var tgtM = Vector128.LoadUnsafe(ref targetRef, 4);

                curA = Sse.Add(curA, Sse.Multiply(Sse.Subtract(tgtA, curA), vAlpha));
                curM = Sse.Add(curM, Sse.Multiply(Sse.Subtract(tgtM, curM), vAlpha));

                Vector128.StoreUnsafe(curA, ref selfRef);
                Vector128.StoreUnsafe(curM, ref selfRef, 4);
                return;
            }

            A1 += (target.A1 - A1) * alpha;
            A2 += (target.A2 - A2) * alpha;
            A3 += (target.A3 - A3) * alpha;
            M0 += (target.M0 - M0) * alpha;
            M1 += (target.M1 - M1) * alpha;
            M2 += (target.M2 - M2) * alpha;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StereoSvfState
    {
        public double Ic1EqL;
        public double Ic1EqR;
        public double Ic2EqL;
        public double Ic2EqR;

        /// <summary>
        /// Processes a stereo pair without <see cref="Math.FusedMultiplyAdd"/> calls (used for non-standard channel counts).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ProcessStereoScalar(ref double left, ref double right, in SvfCoefficientsF c)
        {
            double inL = left;
            double inR = right;

            double a1 = c.A1;
            double a2 = c.A2;
            double a3 = c.A3;

            double v3L = inL - Ic2EqL;
            double v3R = inR - Ic2EqR;

            double v1L = a1 * Ic1EqL + a2 * v3L;
            double v1R = a1 * Ic1EqR + a2 * v3R;

            double v2L = Ic2EqL + (a2 * Ic1EqL + a3 * v3L);
            double v2R = Ic2EqR + (a2 * Ic1EqR + a3 * v3R);

            Ic1EqL = (v1L + v1L) - Ic1EqL;
            Ic1EqR = (v1R + v1R) - Ic1EqR;
            Ic2EqL = (v2L + v2L) - Ic2EqL;
            Ic2EqR = (v2R + v2R) - Ic2EqR;

            double m0 = c.M0;
            double m1 = c.M1;
            double m2 = c.M2;

            left = m0 * inL + (m1 * v1L + m2 * v2L);
            right = m0 * inR + (m1 * v1R + m2 * v2R);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Reset()
        {
            Ic1EqL = 0.0;
            Ic1EqR = 0.0;
            Ic2EqL = 0.0;
            Ic2EqR = 0.0;
        }
    }

    [InlineArray(BandCount)]
    private struct BandSvfCoeffArray
    {
        private SvfCoefficientsF _element0;
    }

    [InlineArray(BandCount)]
    private struct BandSvfStateArray
    {
        private StereoSvfState _element0;
    }

    private int _sampleRate;
    private int _channels;
    private readonly Lock _configureLock = new();
    private StereoSvfState[]? _extraChannelStates;
    private readonly float[] _lastBands = new float[BandCount];
    private bool _lastEnabled;
    private float _lastPreampDb;
    private bool _lastAutoPreamp = true;

    // SeqLock fields for lock-free publishing from the UI thread to AudioCallback
    private long _publishedVersion;
    private BandSvfCoeffArray _publishedCoeffs;
    private float _publishedPreampGain = 1.0f;
    private bool _publishedBypass = true;
    private bool _publishedHasPositiveBoost;

    // Local thread state of AudioCallback (single writer / single reader)
    private long _localVersion;
    private BandSvfCoeffArray _unityCoeffs;
    private BandSvfCoeffArray _currentCoeffs;
    private BandSvfCoeffArray _targetCoeffs;
    private BandSvfStateArray _states;

    private float _currentPreampGain = 1.0f;
    private float _targetPreampGain = 1.0f;

    private int _rampFramesRemaining;
    private bool _targetBypass = true;
    private bool _targetHasPositiveBoost;
    private volatile bool _isBypassed = true;
    private volatile bool _hasPositiveBoost;

    internal EqualizerProcessor(int sampleRate, int channels)
    {
        _sampleRate = sampleRate > 0 ? sampleRate : AudioConstants.DefaultSampleRate;
        _channels = Math.Max(1, channels);
        if (_channels > 2)
            _extraChannelStates = new StereoSvfState[((_channels - 1) / 2) * BandCount];

        var freqs = BandFrequenciesHz;
        for (int i = 0; i < BandCount; i++)
        {
            SvfCoefficientsF cf = ComputeSvfBandCoefficients(_sampleRate, freqs[i], i, 0f);
            _unityCoeffs[i] = cf;
            _publishedCoeffs[i] = cf;
            _currentCoeffs[i] = cf;
            _targetCoeffs[i] = cf;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static BiquadCoefficients ComputeBandCoefficients(int sampleRate, float freqHz, int bandIndex, float gainDb) =>
        bandIndex switch
        {
            0 => BiquadFilterMath.ComputeLowShelf(sampleRate, freqHz, gainDb, 0.7071067811865475),
            BandCount - 1 => BiquadFilterMath.ComputeHighShelf(sampleRate, freqHz, gainDb, 0.7071067811865475),
            _ => BiquadFilterMath.ComputePeakingEq(sampleRate, freqHz, gainDb, 1.4142135623730951)
        };

    /// <summary>
    /// Calculates Cytomic SVF coefficients with a fixed center frequency g.
    /// At 0 dB guarantees M0 = 1, M1 = 0, M2 = 0 (output equals input regardless of integrator state).
    /// </summary>
    private static SvfCoefficientsF ComputeSvfBandCoefficients(int sampleRate, float freqHz, int bandIndex, float gainDb)
    {
        double clampedFreq = Math.Clamp((double)freqHz, 10.0, Math.Max(20.0, sampleRate * 0.47));
        double g = Math.Tan(Math.PI * clampedFreq / sampleRate);
        double a = Math.Abs(gainDb) < 0.001f ? 1.0 : Math.Pow(10.0, gainDb / 40.0);

        double k, m0, m1, m2;

        if (bandIndex == 0)
        {
            const double q = 0.7071067811865475;
            g /= Math.Sqrt(a);
            k = 1.0 / q;
            m0 = 1.0;
            m1 = k * (a - 1.0);
            m2 = a * a - 1.0;
        }
        else if (bandIndex == BandCount - 1)
        {
            const double q = 0.7071067811865475;
            g *= Math.Sqrt(a);
            k = 1.0 / q;
            m0 = a * a;
            m1 = k * (1.0 - a) * a;
            m2 = 1.0 - a * a;
        }
        else
        {
            const double q = 1.4142135623730951;
            k = 1.0 / (q * a);
            m0 = 1.0;
            m1 = k * (a * a - 1.0);
            m2 = 0.0;
        }

        double a1 = 1.0 / (1.0 + g * (g + k));
        double a2 = g * a1;
        double a3 = g * a2;

        return new SvfCoefficientsF
        {
            A1 = (float)a1,
            A2 = (float)a2,
            A3 = (float)a3,
            PadA = 0f,
            M0 = (float)m0,
            M1 = (float)m1,
            M2 = (float)m2,
            PadM = 0f
        };
    }

    internal bool IsBypassed => _isBypassed;
    internal bool HasPositiveBoost => _hasPositiveBoost;

    internal void UpdateFormat(int sampleRate, int channels)
    {
        int newRate = sampleRate > 0 ? sampleRate : AudioConstants.DefaultSampleRate;
        int newCh = Math.Max(1, channels);
        lock (_configureLock)
        {
            if (_sampleRate == newRate && _channels == newCh)
                return;

            _sampleRate = newRate;
            _channels = newCh;
            _extraChannelStates = newCh > 2
                ? new StereoSvfState[((newCh - 1) / 2) * BandCount]
                : null;

            var freqs = BandFrequenciesHz;
            for (int i = 0; i < BandCount; i++)
                _unityCoeffs[i] = ComputeSvfBandCoefficients(newRate, freqs[i], i, 0f);
        }

        Configure(_lastEnabled, _lastBands, _lastPreampDb, _lastAutoPreamp);
        SnapToTarget();
    }

    internal void Configure(
        bool enabled,
        ReadOnlySpan<float> bandGainsDb,
        float preampDb = 0f,
        bool autoPreamp = true)
    {
        int sampleRateSnapshot;
        lock (_configureLock)
        {
            sampleRateSnapshot = _sampleRate;
            _lastEnabled = enabled;
            _lastPreampDb = preampDb;
            _lastAutoPreamp = autoPreamp;
            if (bandGainsDb.Length >= BandCount)
                bandGainsDb[..BandCount].CopyTo(_lastBands);
            else
                Array.Clear(_lastBands);
        }

        BandSvfCoeffArray nextCoeffs = default;
        Span<BiquadCoefficients> exactCoeffs = stackalloc BiquadCoefficients[BandCount];
        bool allFlat = true;
        float maxBoostDb = 0f;
        var freqs = BandFrequenciesHz;

        if (enabled && bandGainsDb.Length >= BandCount)
        {
            for (int i = 0; i < BandCount; i++)
            {
                float db = Math.Clamp(bandGainsDb[i], MinGainDb, MaxGainDb);
                if (MathF.Abs(db) > 0.02f)
                    allFlat = false;
                if (db > maxBoostDb)
                    maxBoostDb = db;

                exactCoeffs[i] = ComputeBandCoefficients(sampleRateSnapshot, freqs[i], i, db);
                nextCoeffs[i] = ComputeSvfBandCoefficients(sampleRateSnapshot, freqs[i], i, db);
            }
        }
        else
        {
            nextCoeffs = _unityCoeffs;
        }

        float effectivePreampDb = enabled ? Math.Clamp(preampDb, -12f, 12f) : 0f;
        if (enabled && autoPreamp && !allFlat)
        {
            float peakResponseDb = ComputePeakResponseDb(exactCoeffs, sampleRateSnapshot);
            if (peakResponseDb > 6.0f)
            {
                effectivePreampDb -= (peakResponseDb - 6.0f) * 0.45f;
            }
        }

        float linearPreamp = enabled ? MathF.Pow(10f, effectivePreampDb / 20f) : 1.0f;
        bool shouldBypass = !enabled || (allFlat && MathF.Abs(linearPreamp - 1.0f) < 0.001f);
        bool hasBoost = enabled && (maxBoostDb > 0.05f || effectivePreampDb > 0.05f);

        lock (_configureLock)
        {
            if (_publishedVersion > 0
                && _publishedBypass == shouldBypass
                && _publishedHasPositiveBoost == hasBoost
                && MathF.Abs(_publishedPreampGain - linearPreamp) < 1e-5f
                && AreCoefficientsEqual(in _publishedCoeffs, in nextCoeffs))
            {
                return;
            }

            long seq = Volatile.Read(ref _publishedVersion);
            Volatile.Write(ref _publishedVersion, seq + 1);
            Interlocked.MemoryBarrier();

            _publishedCoeffs = nextCoeffs;
            _publishedPreampGain = linearPreamp;
            _publishedBypass = shouldBypass;
            _publishedHasPositiveBoost = hasBoost;

            if (hasBoost || _isBypassed)
                _hasPositiveBoost = hasBoost;

            Volatile.Write(ref _publishedVersion, seq + 2);
        }
    }

    private static float ComputePeakResponseDb(ReadOnlySpan<BiquadCoefficients> coeffs, int sampleRate)
    {
        ReadOnlySpan<double> probeFreqs =
        [
            20.0, 25.0, 31.25, 40.0, 50.0, 62.5, 80.0, 100.0, 125.0, 160.0,
            200.0, 250.0, 315.0, 400.0, 500.0, 630.0, 800.0, 1000.0, 1250.0, 1600.0,
            2000.0, 2500.0, 3150.0, 4000.0, 5000.0, 6300.0, 8000.0, 10000.0, 12500.0, 16000.0
        ];

        double maxMagSq = 1.0;
        for (int p = 0; p < probeFreqs.Length; p++)
        {
            double f = probeFreqs[p];
            double magSq = 1.0;
            for (int b = 0; b < BandCount; b++)
                magSq *= coeffs[b].ComputeMagnitudeSquared(f, sampleRate);

            if (magSq > maxMagSq)
                maxMagSq = magSq;
        }

        return (float)(10.0 * Math.Log10(maxMagSq));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AreCoefficientsEqual(in BandSvfCoeffArray a, in BandSvfCoeffArray b)
    {
        for (int i = 0; i < BandCount; i++)
        {
            if (!(MathF.Abs(a[i].A1 - b[i].A1) <= 1e-6f) ||
                !(MathF.Abs(a[i].A2 - b[i].A2) <= 1e-6f) ||
                !(MathF.Abs(a[i].A3 - b[i].A3) <= 1e-6f) ||
                !(MathF.Abs(a[i].M0 - b[i].M0) <= 1e-6f) ||
                !(MathF.Abs(a[i].M1 - b[i].M1) <= 1e-6f) ||
                !(MathF.Abs(a[i].M2 - b[i].M2) <= 1e-6f))
            {
                return false;
            }
        }
        return true;
    }

    internal void SnapToTarget()
    {
        SyncPublishedParameters();
        _currentCoeffs = _targetCoeffs;
        _currentPreampGain = _targetPreampGain;
        _rampFramesRemaining = 0;
        _isBypassed = _targetBypass;
        _hasPositiveBoost = _targetHasPositiveBoost;
    }

    internal void Reset()
    {
        for (int i = 0; i < BandCount; i++)
            _states[i].Reset();

        if (_extraChannelStates != null)
        {
            for (int i = 0; i < _extraChannelStates.Length; i++)
                _extraChannelStates[i].Reset();
        }
    }

    internal unsafe void Process(Span<float> samples)
    {
        if (samples.IsEmpty) return;

        SyncPublishedParameters();

        if (_isBypassed && _rampFramesRemaining <= 0)
            return;

        int channels = _channels;
        int totalFrames = samples.Length / channels;
        if (totalFrames <= 0) return;

        if (channels == 2)
        {
            fixed (float* pSamples = samples)
            {
                ProcessStereoInternal(pSamples, totalFrames, samples);
            }
        }
        else
        {
            ref float bufRef = ref MemoryMarshal.GetReference(samples);
            ProcessGenericChannelsInternal(ref bufRef, totalFrames, channels);
        }

        if (_rampFramesRemaining <= 0)
        {
            _hasPositiveBoost = _targetHasPositiveBoost;
            if (_targetBypass)
            {
                _isBypassed = true;
                Reset();
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void StepSmootherOneFrame()
    {
        if (_rampFramesRemaining <= 0)
            return;

        _rampFramesRemaining--;
        if (_rampFramesRemaining == 0)
        {
            _currentPreampGain = _targetPreampGain;
            _currentCoeffs = _targetCoeffs;
        }
        else
        {
            _currentPreampGain += (_targetPreampGain - _currentPreampGain) * SampleSmoothAlpha;
            for (int b = 0; b < BandCount; b++)
                _currentCoeffs[b].SmoothStepTowards(in _targetCoeffs[b], SampleSmoothAlpha);
        }
    }

    /// <summary>
    /// Dispatches stereo processing to the stationary coefficient path (99.9% of time)
    /// and smooth parameter transition path (30 ms window during slider movement).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void ProcessStereoInternal(float* pSamples, int totalFrames, Span<float> samplesSpan)
    {
        int rampRem = _rampFramesRemaining;
        if (rampRem > 0)
        {
            int rampFrames = Math.Min(totalFrames, rampRem);
            if (Fma.IsSupported)
                ProcessStereoRampFma(pSamples, rampFrames);
            else if (Sse2.IsSupported)
                ProcessStereoRampSse2(pSamples, rampFrames);
            else
                ProcessStereoRampScalar(pSamples, rampFrames);

            int steadyFrames = totalFrames - rampFrames;
            if (steadyFrames > 0)
            {
                float* pSteady = pSamples + (nuint)rampFrames * 2;
                Span<float> steadySpan = samplesSpan.Slice(rampFrames * 2, steadyFrames * 2);
                DispatchStereoSteady(pSteady, steadyFrames, steadySpan);
            }
        }
        else
        {
            DispatchStereoSteady(pSamples, totalFrames, samplesSpan);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void DispatchStereoSteady(float* pSamples, int totalFrames, Span<float> samplesSpan)
    {
        float preamp = _currentPreampGain;
        if (MathF.Abs(preamp - 1.0f) > 1e-6f)
            SpanMathHelper.MultiplyByConstant(samplesSpan, preamp);

        if (Fma.IsSupported)
            ProcessStereoSteadyFma(pSamples, totalFrames);
        else if (Sse2.IsSupported)
            ProcessStereoSteadySse2(pSamples, totalFrames);
        else
            ProcessStereoSteadyScalar(pSamples, totalFrames);
    }

    /// <summary>
    /// Equalizer hot kernel (FMA3 + SSE2, Band-Major):
    /// keeps <c>ic1</c>, <c>ic2</c> states and 6 coefficients in 8 XMM registers,
    /// processes L and R channels simultaneously in <see cref="Vector128{Double}"/>
    /// (≤ 12 XMM registers, 0 <c>call</c>s, 0 stack accesses <c>[rsp + disp]</c> inside loop).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private unsafe void ProcessStereoSteadyFma(float* pSamples, int totalFrames)
    {
        if (!Fma.IsSupported)
            return;

        float* pEnd = pSamples + (nuint)totalFrames * 2;
        ref StereoSvfState stateBase = ref _states[0];
        ref SvfCoefficientsF coeffBase = ref _currentCoeffs[0];

        for (int b = 0; b < BandCount; b++)
        {
            ref StereoSvfState st = ref Unsafe.Add(ref stateBase, b);
            ref readonly SvfCoefficientsF cf = ref Unsafe.Add(ref coeffBase, b);

            var ic1 = Vector128.LoadUnsafe(ref st.Ic1EqL);
            var ic2 = Vector128.LoadUnsafe(ref st.Ic2EqL);

            var va1 = Vector128.Create((double)cf.A1);
            var va2 = Vector128.Create((double)cf.A2);
            var va3 = Vector128.Create((double)cf.A3);
            var vm0 = Vector128.Create((double)cf.M0);
            var vm1 = Vector128.Create((double)cf.M1);
            var vm2 = Vector128.Create((double)cf.M2);

            float* ptr = pSamples;
            while (ptr < pEnd)
            {
                var raw64 = Sse2.LoadScalarVector128((double*)ptr);
                var x = Sse2.ConvertToVector128Double(Vector128.AsSingle(raw64));

                var v3 = Sse2.Subtract(x, ic2);
                var v1 = Fma.MultiplyAdd(va1, ic1, Sse2.Multiply(va2, v3));
                var v2 = Sse2.Add(ic2, Fma.MultiplyAdd(va2, ic1, Sse2.Multiply(va3, v3)));

                ic1 = Sse2.Subtract(Sse2.Add(v1, v1), ic1);
                ic2 = Sse2.Subtract(Sse2.Add(v2, v2), ic2);

                var y = Fma.MultiplyAdd(vm0, x, Fma.MultiplyAdd(vm1, v1, Sse2.Multiply(vm2, v2)));

                var packedOut = Vector128.AsDouble(Sse2.ConvertToVector128Single(y));
                Sse2.StoreScalar((double*)ptr, packedOut);
                ptr += 2;
            }

            Vector128.StoreUnsafe(ic1, ref st.Ic1EqL);
            Vector128.StoreUnsafe(ic2, ref st.Ic2EqL);
        }
    }

    /// <summary>
    /// Equalizer hot kernel for CPUs without AVX2/FMA3 (baseline SSE2, Pentium/Celeron):
    /// uses packed <see cref="Vector128{Double}"/> (<c>mulpd</c> + <c>addpd</c>)
    /// without a single <c>call fma</c> and without register spills to stack.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private unsafe void ProcessStereoSteadySse2(float* pSamples, int totalFrames)
    {
        if (!Sse2.IsSupported)
            return;

        float* pEnd = pSamples + (nuint)totalFrames * 2;
        ref StereoSvfState stateBase = ref _states[0];
        ref SvfCoefficientsF coeffBase = ref _currentCoeffs[0];

        for (int b = 0; b < BandCount; b++)
        {
            ref StereoSvfState st = ref Unsafe.Add(ref stateBase, b);
            ref readonly SvfCoefficientsF cf = ref Unsafe.Add(ref coeffBase, b);

            var ic1 = Vector128.LoadUnsafe(ref st.Ic1EqL);
            var ic2 = Vector128.LoadUnsafe(ref st.Ic2EqL);

            var va1 = Vector128.Create((double)cf.A1);
            var va2 = Vector128.Create((double)cf.A2);
            var va3 = Vector128.Create((double)cf.A3);
            var vm0 = Vector128.Create((double)cf.M0);
            var vm1 = Vector128.Create((double)cf.M1);
            var vm2 = Vector128.Create((double)cf.M2);

            float* ptr = pSamples;
            while (ptr < pEnd)
            {
                var raw64 = Sse2.LoadScalarVector128((double*)ptr);
                var x = Sse2.ConvertToVector128Double(Vector128.AsSingle(raw64));

                var v3 = Sse2.Subtract(x, ic2);
                var v1 = Sse2.Add(Sse2.Multiply(va1, ic1), Sse2.Multiply(va2, v3));
                var v2 = Sse2.Add(ic2, Sse2.Add(Sse2.Multiply(va2, ic1), Sse2.Multiply(va3, v3)));

                ic1 = Sse2.Subtract(Sse2.Add(v1, v1), ic1);
                ic2 = Sse2.Subtract(Sse2.Add(v2, v2), ic2);

                var y = Sse2.Add(
                    Sse2.Multiply(vm0, x),
                    Sse2.Add(Sse2.Multiply(vm1, v1), Sse2.Multiply(vm2, v2)));

                var packedOut = Vector128.AsDouble(Sse2.ConvertToVector128Single(y));
                Sse2.StoreScalar((double*)ptr, packedOut);
                ptr += 2;
            }

            Vector128.StoreUnsafe(ic1, ref st.Ic1EqL);
            Vector128.StoreUnsafe(ic2, ref st.Ic2EqL);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private unsafe void ProcessStereoSteadyScalar(float* pSamples, int totalFrames)
    {
        float* pEnd = pSamples + (nuint)totalFrames * 2;
        for (int b = 0; b < BandCount; b++)
        {
            ref StereoSvfState st = ref _states[b];
            ref readonly SvfCoefficientsF cf = ref _currentCoeffs[b];
            double ic1L = st.Ic1EqL;
            double ic1R = st.Ic1EqR;
            double ic2L = st.Ic2EqL;
            double ic2R = st.Ic2EqR;
            double a1 = cf.A1, a2 = cf.A2, a3 = cf.A3;
            double m0 = cf.M0, m1 = cf.M1, m2 = cf.M2;

            float* ptr = pSamples;
            while (ptr < pEnd)
            {
                double inL = ptr[0];
                double inR = ptr[1];

                double v3L = inL - ic2L;
                double v3R = inR - ic2R;

                double v1L = a1 * ic1L + a2 * v3L;
                double v1R = a1 * ic1R + a2 * v3R;

                double v2L = ic2L + (a2 * ic1L + a3 * v3L);
                double v2R = ic2R + (a2 * ic1R + a3 * v3R);

                ic1L = (v1L + v1L) - ic1L;
                ic1R = (v1R + v1R) - ic1R;
                ic2L = (v2L + v2L) - ic2L;
                ic2R = (v2R + v2R) - ic2R;

                ptr[0] = (float)(m0 * inL + (m1 * v1L + m2 * v2L));
                ptr[1] = (float)(m0 * inR + (m1 * v1R + m2 * v2R));
                ptr += 2;
            }

            st.Ic1EqL = ic1L;
            st.Ic1EqR = ic1R;
            st.Ic2EqL = ic2L;
            st.Ic2EqR = ic2R;
        }
    }

    /// <summary>
    /// Cascaded processing of the 30-ms smooth coefficient transition window (FMA3 + SSE2, Band-Major):
    /// isomorphic to per-sample smoothing, keeps coefficients and states in 14 XMM registers without <c>call</c>s or stack usage.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private unsafe void ProcessStereoRampFma(float* pSamples, int rampFrames)
    {
        if (!Fma.IsSupported)
            return;

        int initialRem = _rampFramesRemaining;
        float curPreamp = _currentPreampGain;
        float tgtPreamp = _targetPreampGain;

        for (int f = 0; f < rampFrames; f++)
        {
            int remAfter = initialRem - 1 - f;
            curPreamp = remAfter == 0
                ? tgtPreamp
                : curPreamp + (tgtPreamp - curPreamp) * SampleSmoothAlpha;

            float* pFrame = pSamples + (nuint)f * 2;
            pFrame[0] *= curPreamp;
            pFrame[1] *= curPreamp;
        }
        _currentPreampGain = curPreamp;

        bool completesRamp = (initialRem - rampFrames) == 0;
        int smoothSteps = completesRamp ? rampFrames - 1 : rampFrames;
        float* pSmoothEnd = pSamples + (nuint)smoothSteps * 2;
        float* pEnd = pSamples + (nuint)rampFrames * 2;

        var vAlpha = Vector128.Create(SampleSmoothAlpha);
        ref StereoSvfState stateBase = ref _states[0];
        ref SvfCoefficientsF curBase = ref _currentCoeffs[0];
        ref SvfCoefficientsF tgtBase = ref _targetCoeffs[0];

        for (int b = 0; b < BandCount; b++)
        {
            ref StereoSvfState st = ref Unsafe.Add(ref stateBase, b);
            ref SvfCoefficientsF curCf = ref Unsafe.Add(ref curBase, b);
            ref readonly SvfCoefficientsF tgtCf = ref Unsafe.Add(ref tgtBase, b);

            var ic1 = Vector128.LoadUnsafe(ref st.Ic1EqL);
            var ic2 = Vector128.LoadUnsafe(ref st.Ic2EqL);

            var curA = Vector128.LoadUnsafe(ref curCf.A1);
            var curM = Vector128.LoadUnsafe(ref curCf.M0);
            var tgtA = Vector128.LoadUnsafe(in tgtCf.A1);
            var tgtM = Vector128.LoadUnsafe(in tgtCf.M0);

            float* ptr = pSamples;
            while (ptr < pSmoothEnd)
            {
                curA = Fma.MultiplyAdd(Sse.Subtract(tgtA, curA), vAlpha, curA);
                curM = Fma.MultiplyAdd(Sse.Subtract(tgtM, curM), vAlpha, curM);

                var raw64 = Sse2.LoadScalarVector128((double*)ptr);
                var x = Sse2.ConvertToVector128Double(Vector128.AsSingle(raw64));

                var a12 = Sse2.ConvertToVector128Double(curA);
                var va1 = Sse2.UnpackLow(a12, a12);
                var va2 = Sse2.UnpackHigh(a12, a12);
                var a30 = Sse2.ConvertToVector128Double(Sse.MoveHighToLow(curA, curA));
                var va3 = Sse2.UnpackLow(a30, a30);

                var v3 = Sse2.Subtract(x, ic2);
                var v1 = Fma.MultiplyAdd(va1, ic1, Sse2.Multiply(va2, v3));
                var v2 = Sse2.Add(ic2, Fma.MultiplyAdd(va2, ic1, Sse2.Multiply(va3, v3)));

                ic1 = Sse2.Subtract(Sse2.Add(v1, v1), ic1);
                ic2 = Sse2.Subtract(Sse2.Add(v2, v2), ic2);

                var m01 = Sse2.ConvertToVector128Double(curM);
                var vm0 = Sse2.UnpackLow(m01, m01);
                var vm1 = Sse2.UnpackHigh(m01, m01);
                var m20 = Sse2.ConvertToVector128Double(Sse.MoveHighToLow(curM, curM));
                var vm2 = Sse2.UnpackLow(m20, m20);

                var y = Fma.MultiplyAdd(vm0, x, Fma.MultiplyAdd(vm1, v1, Sse2.Multiply(vm2, v2)));

                var packedOut = Vector128.AsDouble(Sse2.ConvertToVector128Single(y));
                Sse2.StoreScalar((double*)ptr, packedOut);
                ptr += 2;
            }

            if (ptr < pEnd)
            {
                curA = tgtA;
                curM = tgtM;

                var raw64 = Sse2.LoadScalarVector128((double*)ptr);
                var x = Sse2.ConvertToVector128Double(Vector128.AsSingle(raw64));

                var a12 = Sse2.ConvertToVector128Double(curA);
                var va1 = Sse2.UnpackLow(a12, a12);
                var va2 = Sse2.UnpackHigh(a12, a12);
                var a30 = Sse2.ConvertToVector128Double(Sse.MoveHighToLow(curA, curA));
                var va3 = Sse2.UnpackLow(a30, a30);

                var v3 = Sse2.Subtract(x, ic2);
                var v1 = Fma.MultiplyAdd(va1, ic1, Sse2.Multiply(va2, v3));
                var v2 = Sse2.Add(ic2, Fma.MultiplyAdd(va2, ic1, Sse2.Multiply(va3, v3)));

                ic1 = Sse2.Subtract(Sse2.Add(v1, v1), ic1);
                ic2 = Sse2.Subtract(Sse2.Add(v2, v2), ic2);

                var m01 = Sse2.ConvertToVector128Double(curM);
                var vm0 = Sse2.UnpackLow(m01, m01);
                var vm1 = Sse2.UnpackHigh(m01, m01);
                var m20 = Sse2.ConvertToVector128Double(Sse.MoveHighToLow(curM, curM));
                var vm2 = Sse2.UnpackLow(m20, m20);

                var y = Fma.MultiplyAdd(vm0, x, Fma.MultiplyAdd(vm1, v1, Sse2.Multiply(vm2, v2)));

                var packedOut = Vector128.AsDouble(Sse2.ConvertToVector128Single(y));
                Sse2.StoreScalar((double*)ptr, packedOut);
            }

            Vector128.StoreUnsafe(curA, ref curCf.A1);
            Vector128.StoreUnsafe(curM, ref curCf.M0);
            Vector128.StoreUnsafe(ic1, ref st.Ic1EqL);
            Vector128.StoreUnsafe(ic2, ref st.Ic2EqL);
        }

        _rampFramesRemaining = initialRem - rampFrames;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private unsafe void ProcessStereoRampSse2(float* pSamples, int rampFrames)
    {
        if (!Sse2.IsSupported)
            return;

        int initialRem = _rampFramesRemaining;
        float curPreamp = _currentPreampGain;
        float tgtPreamp = _targetPreampGain;

        for (int f = 0; f < rampFrames; f++)
        {
            int remAfter = initialRem - 1 - f;
            curPreamp = remAfter == 0
                ? tgtPreamp
                : curPreamp + (tgtPreamp - curPreamp) * SampleSmoothAlpha;

            float* pFrame = pSamples + (nuint)f * 2;
            pFrame[0] *= curPreamp;
            pFrame[1] *= curPreamp;
        }
        _currentPreampGain = curPreamp;

        bool completesRamp = (initialRem - rampFrames) == 0;
        int smoothSteps = completesRamp ? rampFrames - 1 : rampFrames;
        float* pSmoothEnd = pSamples + (nuint)smoothSteps * 2;
        float* pEnd = pSamples + (nuint)rampFrames * 2;

        var vAlpha = Vector128.Create(SampleSmoothAlpha);
        ref StereoSvfState stateBase = ref _states[0];
        ref SvfCoefficientsF curBase = ref _currentCoeffs[0];
        ref SvfCoefficientsF tgtBase = ref _targetCoeffs[0];

        for (int b = 0; b < BandCount; b++)
        {
            ref StereoSvfState st = ref Unsafe.Add(ref stateBase, b);
            ref SvfCoefficientsF curCf = ref Unsafe.Add(ref curBase, b);
            ref readonly SvfCoefficientsF tgtCf = ref Unsafe.Add(ref tgtBase, b);

            var ic1 = Vector128.LoadUnsafe(ref st.Ic1EqL);
            var ic2 = Vector128.LoadUnsafe(ref st.Ic2EqL);

            var curA = Vector128.LoadUnsafe(ref curCf.A1);
            var curM = Vector128.LoadUnsafe(ref curCf.M0);
            var tgtA = Vector128.LoadUnsafe(in tgtCf.A1);
            var tgtM = Vector128.LoadUnsafe(in tgtCf.M0);

            float* ptr = pSamples;
            while (ptr < pSmoothEnd)
            {
                curA = Sse.Add(curA, Sse.Multiply(Sse.Subtract(tgtA, curA), vAlpha));
                curM = Sse.Add(curM, Sse.Multiply(Sse.Subtract(tgtM, curM), vAlpha));

                var raw64 = Sse2.LoadScalarVector128((double*)ptr);
                var x = Sse2.ConvertToVector128Double(Vector128.AsSingle(raw64));

                var a12 = Sse2.ConvertToVector128Double(curA);
                var va1 = Sse2.UnpackLow(a12, a12);
                var va2 = Sse2.UnpackHigh(a12, a12);
                var a30 = Sse2.ConvertToVector128Double(Sse.MoveHighToLow(curA, curA));
                var va3 = Sse2.UnpackLow(a30, a30);

                var v3 = Sse2.Subtract(x, ic2);
                var v1 = Sse2.Add(Sse2.Multiply(va1, ic1), Sse2.Multiply(va2, v3));
                var v2 = Sse2.Add(ic2, Sse2.Add(Sse2.Multiply(va2, ic1), Sse2.Multiply(va3, v3)));

                ic1 = Sse2.Subtract(Sse2.Add(v1, v1), ic1);
                ic2 = Sse2.Subtract(Sse2.Add(v2, v2), ic2);

                var m01 = Sse2.ConvertToVector128Double(curM);
                var vm0 = Sse2.UnpackLow(m01, m01);
                var vm1 = Sse2.UnpackHigh(m01, m01);
                var m20 = Sse2.ConvertToVector128Double(Sse.MoveHighToLow(curM, curM));
                var vm2 = Sse2.UnpackLow(m20, m20);

                var y = Sse2.Add(
                    Sse2.Multiply(vm0, x),
                    Sse2.Add(Sse2.Multiply(vm1, v1), Sse2.Multiply(vm2, v2)));

                var packedOut = Vector128.AsDouble(Sse2.ConvertToVector128Single(y));
                Sse2.StoreScalar((double*)ptr, packedOut);
                ptr += 2;
            }

            if (ptr < pEnd)
            {
                curA = tgtA;
                curM = tgtM;

                var raw64 = Sse2.LoadScalarVector128((double*)ptr);
                var x = Sse2.ConvertToVector128Double(Vector128.AsSingle(raw64));

                var a12 = Sse2.ConvertToVector128Double(curA);
                var va1 = Sse2.UnpackLow(a12, a12);
                var va2 = Sse2.UnpackHigh(a12, a12);
                var a30 = Sse2.ConvertToVector128Double(Sse.MoveHighToLow(curA, curA));
                var va3 = Sse2.UnpackLow(a30, a30);

                var v3 = Sse2.Subtract(x, ic2);
                var v1 = Sse2.Add(Sse2.Multiply(va1, ic1), Sse2.Multiply(va2, v3));
                var v2 = Sse2.Add(ic2, Sse2.Add(Sse2.Multiply(va2, ic1), Sse2.Multiply(va3, v3)));

                ic1 = Sse2.Subtract(Sse2.Add(v1, v1), ic1);
                ic2 = Sse2.Subtract(Sse2.Add(v2, v2), ic2);

                var m01 = Sse2.ConvertToVector128Double(curM);
                var vm0 = Sse2.UnpackLow(m01, m01);
                var vm1 = Sse2.UnpackHigh(m01, m01);
                var m20 = Sse2.ConvertToVector128Double(Sse.MoveHighToLow(curM, curM));
                var vm2 = Sse2.UnpackLow(m20, m20);

                var y = Sse2.Add(
                    Sse2.Multiply(vm0, x),
                    Sse2.Add(Sse2.Multiply(vm1, v1), Sse2.Multiply(vm2, v2)));

                var packedOut = Vector128.AsDouble(Sse2.ConvertToVector128Single(y));
                Sse2.StoreScalar((double*)ptr, packedOut);
            }

            Vector128.StoreUnsafe(curA, ref curCf.A1);
            Vector128.StoreUnsafe(curM, ref curCf.M0);
            Vector128.StoreUnsafe(ic1, ref st.Ic1EqL);
            Vector128.StoreUnsafe(ic2, ref st.Ic2EqL);
        }

        _rampFramesRemaining = initialRem - rampFrames;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private unsafe void ProcessStereoRampScalar(float* pSamples, int rampFrames)
    {
        for (int frameIdx = 0; frameIdx < rampFrames; frameIdx++)
        {
            StepSmootherOneFrame();

            float* pFrame = pSamples + (nuint)frameIdx * 2;
            double preamp = _currentPreampGain;
            double left = pFrame[0] * preamp;
            double right = pFrame[1] * preamp;

            for (int b = 0; b < BandCount; b++)
                _states[b].ProcessStereoScalar(ref left, ref right, in _currentCoeffs[b]);

            pFrame[0] = (float)left;
            pFrame[1] = (float)right;
        }
    }

    private void ProcessGenericChannelsInternal(ref float bufRef, int totalFrames, int channels)
    {
        if (channels == 1)
        {
            for (int f = 0; f < totalFrames; f++)
            {
                StepSmootherOneFrame();
                double preamp = _currentPreampGain;
                double mono = Unsafe.Add(ref bufRef, f) * preamp;
                double dummy = 0.0;

                for (int b = 0; b < BandCount; b++)
                    _states[b].ProcessStereoScalar(ref mono, ref dummy, in _currentCoeffs[b]);

                Unsafe.Add(ref bufRef, f) = (float)mono;
            }
        }
        else
        {
            var extraStates = _extraChannelStates;
            for (int f = 0; f < totalFrames; f++)
            {
                StepSmootherOneFrame();
                double preamp = _currentPreampGain;
                int idx = f * channels;
                double left = Unsafe.Add(ref bufRef, idx) * preamp;
                double right = Unsafe.Add(ref bufRef, idx + 1) * preamp;

                for (int b = 0; b < BandCount; b++)
                    _states[b].ProcessStereoScalar(ref left, ref right, in _currentCoeffs[b]);

                Unsafe.Add(ref bufRef, idx) = (float)left;
                Unsafe.Add(ref bufRef, idx + 1) = (float)right;

                if (extraStates != null)
                {
                    for (int ch = 2, pair = 0; ch < channels; ch += 2, pair++)
                    {
                        int pairBase = pair * BandCount;
                        double chA = Unsafe.Add(ref bufRef, idx + ch) * preamp;
                        bool hasB = ch + 1 < channels;
                        double chB = hasB ? Unsafe.Add(ref bufRef, idx + ch + 1) * preamp : 0.0;

                        for (int b = 0; b < BandCount; b++)
                            extraStates[pairBase + b].ProcessStereoScalar(ref chA, ref chB, in _currentCoeffs[b]);

                        Unsafe.Add(ref bufRef, idx + ch) = (float)chA;
                        if (hasB)
                            Unsafe.Add(ref bufRef, idx + ch + 1) = (float)chB;
                    }
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SyncPublishedParameters()
    {
        if (Volatile.Read(ref _publishedVersion) != _localVersion)
            SyncPublishedParametersSlow();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SyncPublishedParametersSlow()
    {
        BandSvfCoeffArray snapshotCoeffs;
        float snapshotPreamp;
        bool snapshotBypass;
        bool snapshotBoost;

        while (true)
        {
            long seq0 = Volatile.Read(ref _publishedVersion);
            if ((seq0 & 1L) != 0)
            {
                Thread.SpinWait(1);
                continue;
            }

            snapshotCoeffs = _publishedCoeffs;
            snapshotPreamp = _publishedPreampGain;
            snapshotBypass = _publishedBypass;
            snapshotBoost = _publishedHasPositiveBoost;

            Interlocked.MemoryBarrier();
            long seq1 = Volatile.Read(ref _publishedVersion);
            if (seq0 == seq1)
            {
                _localVersion = seq1;
                break;
            }
        }

        if (_isBypassed && !snapshotBypass)
        {
            Reset();
            _currentCoeffs = _unityCoeffs;
            _currentPreampGain = 1.0f;
            _isBypassed = false;
        }

        _targetCoeffs = snapshotCoeffs;
        _targetPreampGain = snapshotPreamp;
        _targetBypass = snapshotBypass;
        _targetHasPositiveBoost = snapshotBoost;
        _hasPositiveBoost = _hasPositiveBoost || snapshotBoost;

        _rampFramesRemaining = RampTotalFrames;
    }
}