using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// Пользовательский или встроенный пресет эквалайзера.
/// </summary>
public sealed class EqualizerPreset
{
    /// <summary>Уникальный идентификатор пресета.</summary>
    public string Id { get; set; } = "";

    /// <summary>Отображаемое имя (для пользовательских пресетов) или ключ локализации.</summary>
    public string Name { get; set; } = "";

    /// <summary>Признак встроенного системного пресета (не подлежит удалению).</summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>Значения усиления 10 ISO-полос в дБ [-12..+12].</summary>
    public float[] Bands { get; set; } = new float[EqualizerProcessor.BandCount];
}

/// <summary>
/// Каталог встроенных музыкальных пресетов и преобразователь 5 макро-полос в 10 ISO-полос.
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

    // Выровненные студийные кривые без рези на 4–8 кГц (мягкий верх, чистая середина 250–500 Гц)
    // Полосы:                        31     62    125    250    500     1k     2k     4k     8k    16k
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
    public static readonly float[] PopBands = [2.0f, 2.5f, 1.0f, -1.0f, -0.5f, 1.5f, 1.5f, 1.0f, 0.5f, 1.5f];
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
    /// Возвращает 10 полос встроенного пресета без аллокаций.
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
    /// Возвращает ключ локализации для встроенного пресета.
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
    /// Преобразует 5 психоакустических макро-полос (Bass, Warmth, Vocals, Clarity, Air) в 10 канонических ISO-полос.
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
    /// Извлекает 5 макро-полос из 10 ISO-полос для отображения в упрощённом режиме UI.
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
    /// Разрешает эффективные 10 полос эквалайзера с учётом приоритета:
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
            && !string.IsNullOrEmpty(track.EqualizerPresetId))
        {
            targetPresetId = track.EqualizerPresetId;
        }

        if (string.IsNullOrEmpty(targetPresetId))
            targetPresetId = FlatId;

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

        var builtIn = GetPresetBands(targetPresetId);
        builtIn.CopyTo(destinationBands);
        return targetPresetId;
    }
}

/// <summary>
/// Высокопроизводительный 10-полосный параметрический/графический эквалайзер реального времени.
/// </summary>
public sealed class EqualizerProcessor
{
    public const int BandCount = 10;
    public const float MinGainDb = -12.0f;
    public const float MaxGainDb = 12.0f;

    /// <summary>Длительность активного окна сглаживания в стерео-фреймах (1440 фреймов = 30 мс при 48 кГц).</summary>
    private const int RampTotalFrames = 1440;

    /// <summary>
    /// Коэффициент посэмплового однополюсного фильтра нижних частот (Per-Sample 1-Pole Low-Pass Smoother):
    /// alpha = 0.0042f (постоянная времени tau ~ 5 мс, за 1440 сэмплов сходится на 99.8%, 0 ступенек квантования на ВЧ!).
    /// </summary>
    private const float SampleSmoothAlpha = 0.0042f;

    /// <summary>Стандартные центральные частоты 10 ISO-октавных полос (Гц).</summary>
    public static ReadOnlySpan<float> BandFrequenciesHz =>
        [31.25f, 62.5f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f];

    [StructLayout(LayoutKind.Sequential)]
    private struct SvfCoefficientsF
    {
        public float A1;
        public float A2;
        public float A3;
        public float M0;
        public float M1;
        public float M2;

        /// <summary>
        /// Посэмпловый шаг однополюсного НЧ-фильтра сглаживания коэффициентов (1-pole LPF без ВЧ-ступенек подблоков).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SmoothStepTowards(in SvfCoefficientsF target, float alpha)
        {
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ProcessStereoDouble(ref double left, ref double right, in SvfCoefficientsF c)
        {
            double inL = left;
            double inR = right;

            double a1 = c.A1;
            double a2 = c.A2;
            double a3 = c.A3;

            double v3L = inL - Ic2EqL;
            double v3R = inR - Ic2EqR;

            double v1L = Math.FusedMultiplyAdd(a1, Ic1EqL, a2 * v3L);
            double v1R = Math.FusedMultiplyAdd(a1, Ic1EqR, a2 * v3R);

            double v2L = Ic2EqL + Math.FusedMultiplyAdd(a2, Ic1EqL, a3 * v3L);
            double v2R = Ic2EqR + Math.FusedMultiplyAdd(a2, Ic1EqR, a3 * v3R);

            Ic1EqL = 2.0 * v1L - Ic1EqL;
            Ic1EqR = 2.0 * v1R - Ic1EqR;
            Ic2EqL = 2.0 * v2L - Ic2EqL;
            Ic2EqR = 2.0 * v2R - Ic2EqR;

            double m0 = c.M0;
            double m1 = c.M1;
            double m2 = c.M2;

            left = Math.FusedMultiplyAdd(m0, inL, Math.FusedMultiplyAdd(m1, v1L, m2 * v2L));
            right = Math.FusedMultiplyAdd(m0, inR, Math.FusedMultiplyAdd(m1, v1R, m2 * v2R));
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

    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly Lock _configureLock = new();

    // Поля SeqLock для lock-free публикации из UI-потока в AudioCallback
    private long _publishedVersion;
    private BandSvfCoeffArray _publishedCoeffs;
    private float _publishedPreampGain = 1.0f;
    private bool _publishedBypass = true;
    private bool _publishedHasPositiveBoost;

    // Локальное состояние потока AudioCallback (single writer / single reader)
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

    public EqualizerProcessor(int sampleRate, int channels)
    {
        _sampleRate = sampleRate > 0 ? sampleRate : AudioConstants.DefaultSampleRate;
        _channels = Math.Max(1, channels);

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
    /// Вычисляет коэффициенты Cytomic SVF с фиксированной центральной частотой g.
    /// При 0 dB гарантирует M0 = 1, M1 = 0, M2 = 0 (выход равен входу независимо от состояния интеграторов).
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
            k = 1.0 / q;
            m0 = 1.0;
            m1 = k * (a - 1.0);
            m2 = a * a - 1.0;
        }
        else if (bandIndex == BandCount - 1)
        {
            const double q = 0.7071067811865475;
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
            M0 = (float)m0,
            M1 = (float)m1,
            M2 = (float)m2
        };
    }

    public bool IsBypassed => _isBypassed;
    public bool HasPositiveBoost => _hasPositiveBoost;

    public void Configure(
        bool enabled,
        ReadOnlySpan<float> bandGainsDb,
        float preampDb = 0f,
        bool autoPreamp = true)
    {
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

                exactCoeffs[i] = ComputeBandCoefficients(_sampleRate, freqs[i], i, db);
                nextCoeffs[i] = ComputeSvfBandCoefficients(_sampleRate, freqs[i], i, db);
            }
        }
        else
        {
            nextCoeffs = _unityCoeffs;
        }

        float effectivePreampDb = enabled ? Math.Clamp(preampDb, -12f, 12f) : 0f;
        if (enabled && autoPreamp && !allFlat)
        {
            float peakResponseDb = ComputePeakResponseDb(exactCoeffs, _sampleRate);
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

    public void SnapToTarget()
    {
        SyncPublishedParameters();
        _currentCoeffs = _targetCoeffs;
        _currentPreampGain = _targetPreampGain;
        _rampFramesRemaining = 0;
        _isBypassed = _targetBypass;
        _hasPositiveBoost = _targetHasPositiveBoost;
    }

    public void Reset()
    {
        for (int i = 0; i < BandCount; i++)
            _states[i].Reset();
    }

    public void Process(Span<float> samples)
    {
        if (samples.IsEmpty) return;

        SyncPublishedParameters();

        if (_isBypassed && _rampFramesRemaining <= 0)
            return;

        int channels = _channels;
        int totalFrames = samples.Length / channels;
        if (totalFrames <= 0) return;

        ref float bufRef = ref MemoryMarshal.GetReference(samples);

        if (channels == 2)
        {
            ProcessStereoInternal(ref bufRef, totalFrames);
        }
        else
        {
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
            // Посэмпловое однополюсное сглаживание (Per-Sample 1-Pole LPF, 48 кГц):
            // 0 ступенек подблоков, 0 ВЧ-алиасинга при кручении НЧ!
            _currentPreampGain += (_targetPreampGain - _currentPreampGain) * SampleSmoothAlpha;
            _currentCoeffs[0].SmoothStepTowards(in _targetCoeffs[0], SampleSmoothAlpha);
            _currentCoeffs[1].SmoothStepTowards(in _targetCoeffs[1], SampleSmoothAlpha);
            _currentCoeffs[2].SmoothStepTowards(in _targetCoeffs[2], SampleSmoothAlpha);
            _currentCoeffs[3].SmoothStepTowards(in _targetCoeffs[3], SampleSmoothAlpha);
            _currentCoeffs[4].SmoothStepTowards(in _targetCoeffs[4], SampleSmoothAlpha);
            _currentCoeffs[5].SmoothStepTowards(in _targetCoeffs[5], SampleSmoothAlpha);
            _currentCoeffs[6].SmoothStepTowards(in _targetCoeffs[6], SampleSmoothAlpha);
            _currentCoeffs[7].SmoothStepTowards(in _targetCoeffs[7], SampleSmoothAlpha);
            _currentCoeffs[8].SmoothStepTowards(in _targetCoeffs[8], SampleSmoothAlpha);
            _currentCoeffs[9].SmoothStepTowards(in _targetCoeffs[9], SampleSmoothAlpha);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ProcessStereoInternal(ref float bufRef, int totalFrames)
    {
        for (int frameIdx = 0; frameIdx < totalFrames; frameIdx++)
        {
            StepSmootherOneFrame();

            int sampleOffset = frameIdx * 2;
            double preamp = _currentPreampGain;
            double left = Unsafe.Add(ref bufRef, sampleOffset) * preamp;
            double right = Unsafe.Add(ref bufRef, sampleOffset + 1) * preamp;

            _states[0].ProcessStereoDouble(ref left, ref right, in _currentCoeffs[0]);
            _states[1].ProcessStereoDouble(ref left, ref right, in _currentCoeffs[1]);
            _states[2].ProcessStereoDouble(ref left, ref right, in _currentCoeffs[2]);
            _states[3].ProcessStereoDouble(ref left, ref right, in _currentCoeffs[3]);
            _states[4].ProcessStereoDouble(ref left, ref right, in _currentCoeffs[4]);
            _states[5].ProcessStereoDouble(ref left, ref right, in _currentCoeffs[5]);
            _states[6].ProcessStereoDouble(ref left, ref right, in _currentCoeffs[6]);
            _states[7].ProcessStereoDouble(ref left, ref right, in _currentCoeffs[7]);
            _states[8].ProcessStereoDouble(ref left, ref right, in _currentCoeffs[8]);
            _states[9].ProcessStereoDouble(ref left, ref right, in _currentCoeffs[9]);

            Unsafe.Add(ref bufRef, sampleOffset) = (float)left;
            Unsafe.Add(ref bufRef, sampleOffset + 1) = (float)right;
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
                    _states[b].ProcessStereoDouble(ref mono, ref dummy, in _currentCoeffs[b]);

                Unsafe.Add(ref bufRef, f) = (float)mono;
            }
        }
        else
        {
            for (int f = 0; f < totalFrames; f++)
            {
                StepSmootherOneFrame();
                double preamp = _currentPreampGain;
                int idx = f * channels;
                double left = Unsafe.Add(ref bufRef, idx) * preamp;
                double right = Unsafe.Add(ref bufRef, idx + 1) * preamp;

                for (int b = 0; b < BandCount; b++)
                    _states[b].ProcessStereoDouble(ref left, ref right, in _currentCoeffs[b]);

                Unsafe.Add(ref bufRef, idx) = (float)left;
                Unsafe.Add(ref bufRef, idx + 1) = (float)right;

                float preampF = (float)preamp;
                for (int ch = 2; ch < channels; ch++)
                    Unsafe.Add(ref bufRef, idx + ch) *= preampF;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SyncPublishedParameters()
    {
        if (Volatile.Read(ref _publishedVersion) == _localVersion)
            return;

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

    public static float EvaluateFrequencyResponseDb(
        ReadOnlySpan<float> bandGainsDb,
        float freqHz,
        int sampleRate = AudioConstants.DefaultSampleRate)
    {
        if (bandGainsDb.Length < BandCount)
            return 0f;

        var freqs = BandFrequenciesHz;
        double totalMagSq = 1.0;

        for (int i = 0; i < BandCount; i++)
        {
            float db = Math.Clamp(bandGainsDb[i], MinGainDb, MaxGainDb);
            if (MathF.Abs(db) < 0.02f) continue;

            BiquadCoefficients c = ComputeBandCoefficients(sampleRate, freqs[i], i, db);
            totalMagSq *= c.ComputeMagnitudeSquared(freqHz, sampleRate);
        }

        return (float)(10.0 * Math.Log10(Math.Max(totalMagSq, 1e-12)));
    }
}