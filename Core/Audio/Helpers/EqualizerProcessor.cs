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
/// <remarks>
/// <para><b>Архитектурные инварианты:</b></para>
/// <list type="bullet">
///   <item><b>Zero-Alloc Hot Path:</b> <see cref="Process"/> не выделяет ни одного байта в куче.</item>
///   <item><b>Lock-Free SeqLock:</b> передача коэффициентов из UI-потока в поток WASAPI осуществляется через атомарный чётный/нечётный счётчик поколений и фиксированный массив <see cref="InlineArrayAttribute"/>.</item>
///   <item><b>Sub-Block Parameter Smoothing:</b> плавная линейная интерполяция коэффициентов и предусиления на горизонте 960 стерео-фреймов (20 мс) с шагом 16 фреймов исключает цифровые щелчки (zipper noise).</item>
///   <item><b>Zero-CPU Bypass:</b> при выключенном эквалайзере или нулевом усилении всех полос после завершения рампы цикл фильтрации полностью пропускается.</item>
/// </list>
/// </remarks>
public sealed class EqualizerProcessor
{
    public const int BandCount = 10;
    public const float MinGainDb = -12.0f;
    public const float MaxGainDb = 12.0f;

    /// <summary>Длительность плавного перехода коэффициентов в стерео-фреймах (960 фреймов = 20 мс при 48 кГц).</summary>
    private const int RampTotalFrames = 960;

    /// <summary>Размер подблока обновления интерполированных коэффициентов (16 фреймов = 0.33 мс).</summary>
    private const int SubBlockFrames = 16;

    /// <summary>Стандартные центральные частоты 10 ISO-октавных полос (Гц).</summary>
    public static ReadOnlySpan<float> BandFrequenciesHz =>
        [31.25f, 62.5f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f];

    [InlineArray(BandCount)]
    private struct BandCoefficientArray
    {
        private BiquadCoefficientsF _element0;
    }

    [InlineArray(BandCount)]
    private struct BandStateArray
    {
        private StereoBiquadStateF _element0;
    }

    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly Lock _configureLock = new();

    // Поля SeqLock для lock-free публикации из UI-потока в AudioCallback
    private long _publishedVersion;
    private BandCoefficientArray _publishedCoeffs;
    private float _publishedPreampGain = 1.0f;
    private bool _publishedBypass = true;
    private bool _publishedHasPositiveBoost;

    // Локальное состояние потока AudioCallback (single writer / single reader)
    private long _localVersion;
    private BandCoefficientArray _startCoeffs;
    private BandCoefficientArray _currentCoeffs;
    private BandCoefficientArray _targetCoeffs;
    private BandStateArray _states;

    private float _startPreampGain = 1.0f;
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

        for (int i = 0; i < BandCount; i++)
        {
            _publishedCoeffs[i] = BiquadCoefficientsF.Identity;
            _startCoeffs[i] = BiquadCoefficientsF.Identity;
            _currentCoeffs[i] = BiquadCoefficientsF.Identity;
            _targetCoeffs[i] = BiquadCoefficientsF.Identity;
        }
    }

    /// <summary>
    /// Указывает, находится ли эквалайзер в состоянии полного байпаса (0% нагрузки на CPU).
    /// </summary>
    public bool IsBypassed => _isBypassed;

    /// <summary>
    /// Указывает, имеет ли хотя бы одна полоса положительное усиление (&gt; 0.05 dB),
    /// требующее обязательного участия <see cref="Normalization.TruePeakLimiter"/>.
    /// </summary>
    public bool HasPositiveBoost => _hasPositiveBoost;

    /// <summary>
    /// Потокобезопасно обновляет целевые параметры эквалайзера из UI/Command потока.
    /// </summary>
    /// <param name="enabled">Включён ли эквалайзер.</param>
    /// <param name="bandGainsDb">Массив из 10 значений усиления в дБ.</param>
    /// <param name="preampDb">Ручное предусиление в дБ.</param>
    /// <param name="autoPreamp">Автоматическая компенсация пикового усиления АЧХ для предотвращения перегруза на суббасе.</param>
    public void Configure(
        bool enabled,
        ReadOnlySpan<float> bandGainsDb,
        float preampDb = 0f,
        bool autoPreamp = true)
    {
        BandCoefficientArray nextCoeffs = default;
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

                BiquadCoefficients c = i switch
                {
                    0 => BiquadFilterMath.ComputeLowShelf(_sampleRate, freqs[0], db, 0.7071067811865475),
                    BandCount - 1 => BiquadFilterMath.ComputeHighShelf(_sampleRate, freqs[BandCount - 1], db, 0.7071067811865475),
                    _ => BiquadFilterMath.ComputePeakingEq(_sampleRate, freqs[i], db, 1.4142135623730951)
                };

                exactCoeffs[i] = c;
                nextCoeffs[i] = BiquadCoefficientsF.FromDouble(in c);
            }
        }
        else
        {
            for (int i = 0; i < BandCount; i++)
            {
                exactCoeffs[i] = BiquadCoefficients.Identity;
                nextCoeffs[i] = BiquadCoefficientsF.Identity;
            }
        }

        float effectivePreampDb = enabled ? Math.Clamp(preampDb, -12f, 12f) : 0f;
        if (enabled && autoPreamp && !allFlat)
        {
            float peakResponseDb = ComputePeakResponseDb(exactCoeffs, _sampleRate);
            if (peakResponseDb > 1.5f)
            {
                effectivePreampDb -= (peakResponseDb - 1.5f) * 0.75f;
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
    private static bool AreCoefficientsEqual(in BandCoefficientArray a, in BandCoefficientArray b)
    {
        for (int i = 0; i < BandCount; i++)
        {
            if (MathF.Abs(a[i].B0 - b[i].B0) > 1e-6f ||
                MathF.Abs(a[i].B1 - b[i].B1) > 1e-6f ||
                MathF.Abs(a[i].B2 - b[i].B2) > 1e-6f ||
                MathF.Abs(a[i].A1 - b[i].A1) > 1e-6f ||
                MathF.Abs(a[i].A2 - b[i].A2) > 1e-6f)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Мгновенно фиксирует текущие коэффициенты на целевых значениях без 20-мс рампы (вызывается перед стартом трека).
    /// </summary>
    public void SnapToTarget()
    {
        SyncPublishedParameters();
        _currentCoeffs = _targetCoeffs;
        _startCoeffs = _targetCoeffs;
        _currentPreampGain = _targetPreampGain;
        _startPreampGain = _targetPreampGain;
        _rampFramesRemaining = 0;
        _isBypassed = _targetBypass;
        _hasPositiveBoost = _targetHasPositiveBoost;
    }

    /// <summary>
    /// Сбрасывает регистры задержки всех полос (при Seek или остановке).
    /// </summary>
    public void Reset()
    {
        for (int i = 0; i < BandCount; i++)
            _states[i].Reset();
    }

    /// <summary>
    /// Обрабатывает interleaved PCM буфер in-place (0 B аллокаций).
    /// Вызывается исключительно из потока воспроизведения (<c>AudioCallback</c>).
    /// </summary>
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
    private void ProcessStereoInternal(ref float bufRef, int totalFrames)
    {
        int frameIdx = 0;

        // Если фильтр переходит в байпас (выключение EQ), коэффициенты _startCoeffs остаются неизменными,
        // а мы просто плавно кроссфейдим между выходом фильтра и чистым (dry) сигналом без движения полюсов ("капли в лужу")!
        bool rampingToBypass = _targetBypass && _rampFramesRemaining > 0;
        bool rampingFromBypass = !_targetBypass && _rampFramesRemaining > 0 && AreAllIdentity(in _startCoeffs);

        while (frameIdx < totalFrames)
        {
            int blockFrames = Math.Min(SubBlockFrames, totalFrames - frameIdx);
            float wetMix = 1.0f;

            if (_rampFramesRemaining > 0)
            {
                _rampFramesRemaining = Math.Max(0, _rampFramesRemaining - blockFrames);
                float t = 1.0f - (float)_rampFramesRemaining / RampTotalFrames;
                float smoothT = t * t * (3.0f - 2.0f * t);

                if (rampingToBypass)
                {
                    wetMix = 1.0f - smoothT;
                    _currentPreampGain = _startPreampGain;
                    _currentCoeffs = _startCoeffs;
                }
                else if (rampingFromBypass)
                {
                    wetMix = smoothT;
                    _currentPreampGain = _targetPreampGain;
                    _currentCoeffs = _targetCoeffs;
                }
                else
                {
                    _currentPreampGain = _startPreampGain + (_targetPreampGain - _startPreampGain) * smoothT;
                    for (int b = 0; b < BandCount; b++)
                    {
                        // Если одна из полос включается из Identity или выключается в Identity,
                        // не интерполируем её полюса A1/A2 через ноль (именно это создаёт звук "капли в лужу"!),
                        // а интерполируем только числитель или используем целевой полюс!
                        if (_startCoeffs[b].A1 == 0f && _targetCoeffs[b].A1 != 0f)
                        {
                            _currentCoeffs[b] = _targetCoeffs[b];
                        }
                        else if (_targetCoeffs[b].A1 == 0f && _startCoeffs[b].A1 != 0f)
                        {
                            _currentCoeffs[b] = _startCoeffs[b];
                        }
                        else
                        {
                            _currentCoeffs[b] = BiquadCoefficientsF.Lerp(in _startCoeffs[b], in _targetCoeffs[b], smoothT);
                        }
                    }
                }
            }
            else
            {
                _currentPreampGain = _targetPreampGain;
                _currentCoeffs = _targetCoeffs;
            }

            double preamp = _currentPreampGain;
            int endFrame = frameIdx + blockFrames;

            for (; frameIdx < endFrame; frameIdx++)
            {
                int sampleOffset = frameIdx * 2;
                float dryL = Unsafe.Add(ref bufRef, sampleOffset);
                float dryR = Unsafe.Add(ref bufRef, sampleOffset + 1);

                double left = dryL * preamp;
                double right = dryR * preamp;

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

                float wetL = (float)left;
                float wetR = (float)right;

                Unsafe.Add(ref bufRef, sampleOffset) = dryL + (wetL - dryL) * wetMix;
                Unsafe.Add(ref bufRef, sampleOffset + 1) = dryR + (wetR - dryR) * wetMix;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AreAllIdentity(in BandCoefficientArray coeffs)
    {
        for (int i = 0; i < BandCount; i++)
        {
            if (coeffs[i].A1 != 0f || MathF.Abs(coeffs[i].B0 - 1f) > 1e-4f)
                return false;
        }
        return true;
    }

    private void ProcessGenericChannelsInternal(ref float bufRef, int totalFrames, int channels)
    {
        _currentCoeffs = _targetCoeffs;
        _currentPreampGain = _targetPreampGain;
        double preamp = _currentPreampGain;

        if (channels == 1)
        {
            for (int f = 0; f < totalFrames; f++)
            {
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

        _rampFramesRemaining = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SyncPublishedParameters()
    {
        if (Volatile.Read(ref _publishedVersion) == _localVersion)
            return;

        BandCoefficientArray snapshotCoeffs;
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

        _startCoeffs = _currentCoeffs;
        _targetCoeffs = snapshotCoeffs;
        _startPreampGain = _currentPreampGain;
        _targetPreampGain = snapshotPreamp;
        _targetBypass = snapshotBypass;
        _targetHasPositiveBoost = snapshotBoost;
        _hasPositiveBoost = _hasPositiveBoost || snapshotBoost;

        if (!snapshotBypass)
            _isBypassed = false;

        _rampFramesRemaining = RampTotalFrames;
    }

    /// <summary>
    /// Вычисляет суммарную амплитудно-частотную характеристику (в дБ) на заданной частоте
    /// для построения гладкой кривой АЧХ в интерфейсе.
    /// </summary>
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

            BiquadCoefficients c = i switch
            {
                0 => BiquadFilterMath.ComputeLowShelf(sampleRate, freqs[0], db, 0.7071067811865475),
                BandCount - 1 => BiquadFilterMath.ComputeHighShelf(sampleRate, freqs[BandCount - 1], db, 0.7071067811865475),
                _ => BiquadFilterMath.ComputePeakingEq(sampleRate, freqs[i], db, 1.4142135623730951)
            };

            totalMagSq *= c.ComputeMagnitudeSquared(freqHz, sampleRate);
        }

        return (float)(10.0 * Math.Log10(Math.Max(totalMagSq, 1e-12)));
    }
}