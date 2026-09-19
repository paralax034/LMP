using System.Collections.ObjectModel;
using System.Net;
using Avalonia.Media;
using Avalonia.Threading;
using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Normalization;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Utils;
using LMP.UI.Dialogs;
using LMP.UI.Features.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.UI.Features.Settings;

/// <summary>
/// Обёртка над произвольным значением с именем для отображения в ComboBox.
/// <para>
/// ToString() возвращает Name — ComboBox вызывает его напрямую,
/// без создания DataTemplate-контейнеров. Это устраняет утечку памяти
/// от DisplayMemberBinding → ControlTemplate binding → PointerDeferredContent.
/// </para>
/// </summary>
public sealed class LocalizedItem<T>(T value, string name)
{
    public T Value { get; } = value;
    public string Name { get; } = name;

    public override string ToString() => Name;
}

/// <summary>Пресеты количества bitmap-объектов в RAM-кэше изображений.</summary>
public enum ImageCachePreset { Custom, Low, Medium, High }

/// <summary>
/// ViewModel страницы настроек.
/// <para>
/// Архитектура: sidebar (ListBox с 9 items) + ContentControl справа.
/// В каждый момент в visual tree живёт одна страница (~10–15 контролов).
/// Все страницы получают DataContext = этот VM напрямую (без Owner.*).
/// </para>
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase, IDisposable, ISmoothTransitionViewModel
{
    /// <inheritdoc />
    protected override bool HandlesAccountChanges => true;

    private const int NavigationDebounceMs = 128;

    private readonly INetworkManager _networkManager;
    private readonly LibraryService _library;
    private readonly TrackRegistry _registry;
    private readonly SearchCacheService _searchCache;
    private readonly ImageCacheService _imageCache;
    private readonly ThemeManagerService _themeManager;
    private readonly CookieAuthService _auth;
    private readonly LocalAuthServer _localServer;
    private readonly DialogService _dialog;
    private readonly AudioEngine _audio;
    private readonly YoutubeProvider _youtube;
    private readonly YoutubeUserDataService _userData;
    private readonly NotificationService _notifications;

    private bool _isLoadingTheme;
    private bool _isUpdatingPreset;
    private bool _isLoadingSettings;
    private bool _isDisposed;

    // Таймеры дебаунса для числовых полей и настроек диска/памяти
    private DispatcherTimer? _storageDebounceTimer;
    private DispatcherTimer? _downloadsLimitDebounceTimer;
    private DispatcherTimer? _memoryIntervalDebounceTimer;
    private DispatcherTimer? _memoryPressureDebounceTimer;
    private DispatcherTimer? _normLufsDebounceTimer;
    private DispatcherTimer? _normGainDebounceTimer;
    private DispatcherTimer? _suggestionsDebounceTimer;
    private DispatcherTimer? _proxyDebounceTimer;

    /// <summary>
    /// Локальный признак наличия данных в памяти.
    /// </summary>
    private bool _isDataLoaded;

    /// <summary>
    /// Признак готовности контента.
    /// <para>
    /// Пока <c>false</c> — показывается skeleton-заглушка.
    /// После <c>true</c> — sidebar + страница контента.
    /// </para>
    /// </summary>
    [ObservableProperty] public partial bool IsContentReady { get; private set; }

    #region Sidebar

    /// <summary>
    /// Элементы sidebar — по одному на секцию настроек.
    /// <para>
    /// ListBox отображает их иконкой + названием через DataTemplate по DataType.
    /// ContentControl справа подбирает страницу по типу выбранного элемента.
    /// В каждый момент в visual tree — одна страница (~10–15 контролов).
    /// </para>
    /// </summary>
    public ObservableCollection<SettingsSidebarItemBase> SidebarItems { get; } = [];

    /// <summary>Текущий выбранный элемент sidebar — определяет какая страница отображается.</summary>
    [ObservableProperty] public partial SettingsSidebarItemBase? SelectedSidebarItem { get; set; }

    /// <summary>
    /// Управляет видимостью текстовых лейблов в sidebar.
    /// <para>
    /// <c>true</c>  — sidebar достаточно широкий, показываем иконку + текст.<br/>
    /// <c>false</c> — sidebar узкий, показываем только иконки (tooltip всегда виден).
    /// Значение устанавливается из code-behind при изменении ширины колонки GridSplitter-ом.
    /// </para>
    /// </summary>
    [ObservableProperty] public partial bool IsSidebarExpanded { get; set; } = true;

    partial void OnSelectedSidebarItemChanged(SettingsSidebarItemBase? value)
    {
        if (_isLoadingSettings || value is null) return;
        if (value is NetworkSidebarItem && NetworkStatus == NetworkStatusKind.Unknown)
            _ = TestNetworkAsync();
    }

    #endregion

    #region Account

    /// <summary>Признак авторизации пользователя через cookies.</summary>
    [ObservableProperty] public partial bool IsAuthenticated { get; private set; }

    /// <summary>Имя пользователя или локализованная строка "не авторизован".</summary>
    public string AccountName => IsAuthenticated ? _auth.State.UserName : SL["Auth_NotSignedIn"];

    /// <summary>URL аватара или <c>null</c> если не авторизован — управляет видимостью Image/Icon.</summary>
    public string? AccountAvatarUrl => IsAuthenticated ? _auth.State.AvatarUrl : null;

    /// <summary>Email или локализованная строка "гость".</summary>
    public string AccountSubtitle => IsAuthenticated ? _auth.State.UserEmail : SL["Auth_Guest"];

    /// <summary>
    /// Указывает, выполняется ли в данный момент сетевая транзакция с аккаунтом (вход, смена канала, выход).
    /// </summary>
    [ObservableProperty] public partial bool IsAccountLoading { get; private set; }

    #endregion

    #region Network

    /// <summary>Состояние последней проверки подключения.</summary>
    public enum NetworkStatusKind
    {
        /// <summary>Проверка ещё не выполнялась.</summary>
        Unknown,
        /// <summary>Проверка выполняется в данный момент.</summary>
        Checking,
        /// <summary>YouTube доступен напрямую.</summary>
        Ok,
        /// <summary>Нет подключения к интернету.</summary>
        NoInternet,
        /// <summary>Подключение идёт через VPN-интерфейс.</summary>
        VpnDetected,
        /// <summary>Подключение идёт через явно заданный прокси.</summary>
        ProxyActive,
        /// <summary>YouTube недоступен — ошибка сети или блокировка.</summary>
        Error
    }

    [ObservableProperty]
    public partial NetworkStatusKind NetworkStatus { get; private set; }
    = NetworkStatusKind.Unknown;

    /// <summary>
    /// Цвет индикатора статуса сети.
    /// Читается из активной темы приложения — не хардкодим цвета.
    /// </summary>
    public Color NetworkStatusColor => NetworkStatus switch
    {
        NetworkStatusKind.Ok => ThemeManagerService.GetThemeColor("Accent"),
        NetworkStatusKind.VpnDetected => ThemeManagerService.GetThemeColor("SystemInfoBlue"),
        NetworkStatusKind.ProxyActive => ThemeManagerService.GetThemeColor("Accent"),
        NetworkStatusKind.Checking => ThemeManagerService.GetThemeColor("SystemWarnOrange"),
        NetworkStatusKind.NoInternet => ThemeManagerService.GetThemeColor("SystemError"),
        NetworkStatusKind.Error => ThemeManagerService.GetThemeColor("SystemError"),
        _ => ThemeManagerService.GetThemeColor("TextSecondary"),
    };

    [ObservableProperty] public partial string NetworkStatusText { get; private set; } = "";

    /// <summary>Задержка последнего успешного запроса к YouTube в миллисекундах.</summary>
    [ObservableProperty] public partial int NetworkLatencyMs { get; private set; }

    /// <summary>
    /// Возвращает <c>true</c> если задержка измерена и ненулевая.
    /// Управляет видимостью метки латентности без конвертеров на стороне XAML.
    /// </summary>
    public bool HasLatency => NetworkLatencyMs > 0;

    /// <summary>Флаг активной проверки подключения — блокирует повторный запуск.</summary>
    [ObservableProperty] public partial bool IsNetworkTesting { get; private set; }

    /// <summary>Доступные профили скорости интернета для ComboBox.</summary>
    public ObservableCollection<LocalizedItem<InternetProfile>> InternetProfileOptions { get; } = [];

    /// <summary>Выбранный профиль скорости; синхронизируется с настройками через подписку.</summary>
    [ObservableProperty] public partial LocalizedItem<InternetProfile>? SelectedInternetProfile { get; set; }

    [ObservableProperty] public partial bool ProxyEnabled { get; set; }
    [ObservableProperty] public partial string ProxyHost { get; set; } = "";
    [ObservableProperty] public partial int ProxyPort { get; set; } = 8080;
    [ObservableProperty] public partial bool ProxyAuth { get; set; }
    [ObservableProperty] public partial string ProxyUser { get; set; } = "";
    [ObservableProperty] public partial string ProxyPass { get; set; } = "";

    /// <summary>
    /// Признак того, что изменения сети требуют перезапуска.
    /// <para>Устанавливается при смене профиля, прокси или клиента.</para>
    /// </summary>
    [ObservableProperty] public partial bool NetworkRestartRequired { get; set; }

    partial void OnSelectedInternetProfileChanged(LocalizedItem<InternetProfile>? value)
    {
        if (_isLoadingSettings || value is null) return;
        _library.UpdateSettings(s => s.InternetProfile = value.Value);
        _ = AudioEngine.ReinitializeWithProfileAsync(value.Value);
    }

    partial void OnProxyEnabledChanged(bool value) => OnProxyParamChanged();
    partial void OnProxyHostChanged(string value) => OnProxyParamChanged();
    partial void OnProxyPortChanged(int value) => OnProxyParamChanged();
    partial void OnProxyAuthChanged(bool value) => OnProxyParamChanged();
    partial void OnProxyUserChanged(string value) => OnProxyParamChanged();
    partial void OnProxyPassChanged(string value) => OnProxyParamChanged();

    private void OnProxyParamChanged()
    {
        if (_isLoadingSettings) return;

        _proxyDebounceTimer?.Stop();
        _proxyDebounceTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(400),
            DispatcherPriority.Normal,
            (_, _) =>
            {
                _proxyDebounceTimer?.Stop();
                SaveNetworkSettings();
            });
        _proxyDebounceTimer.Start();
    }

    #endregion

    #region Storage

    /// <summary>Текущий путь к папке загрузок.</summary>
    [ObservableProperty] public partial string DownloadPath { get; set; } = string.Empty;

    /// <summary>Пресеты количества bitmap-объектов в RAM для ComboBox.</summary>
    public ObservableCollection<LocalizedItem<ImageCachePreset>> ImageCachePresets { get; } = [];

    /// <summary>Выбранный пресет; <c>null</c> означает Custom (произвольное значение слайдера).</summary>
    [ObservableProperty] public partial LocalizedItem<ImageCachePreset>? SelectedImageCachePreset { get; set; }

    /// <summary>Максимальное количество bitmap-объектов в RAM-кэше.</summary>
    [ObservableProperty] public partial int MaxBitmapCacheItems { get; set; }

    [ObservableProperty] public partial int ImageCacheLimitMb { get; set; }
    [ObservableProperty] public partial int AudioCacheLimitMb { get; set; }
    [ObservableProperty] public partial int DownloadedTracksLimitMb { get; set; }

    /// <summary>Статистика кэша изображений в формате "X MB / Y MB (N files, RAM: M)".</summary>
    [ObservableProperty] public partial string ImageCacheStats { get; private set; } = "...";

    /// <summary>Статистика аудиокэша в формате "X MB / Y MB (N files)".</summary>
    [ObservableProperty] public partial string AudioCacheStats { get; private set; } = "...";

    /// <summary>Доля занятого места в кэше изображений [0..1] для ProgressBar.</summary>
    [ObservableProperty] public partial double ImageCacheUsagePercent { get; private set; }

    /// <summary>Доля занятого места в аудиокэше [0..1] для ProgressBar.</summary>
    [ObservableProperty] public partial double AudioCacheUsagePercent { get; private set; }

    /// <summary>Статистика загрузок в формате "X MB / Y MB (N files)".</summary>
    [ObservableProperty] public partial string DownloadsStats { get; private set; } = "...";

    /// <summary>Доля занятого места загрузками [0..1] для ProgressBar.</summary>
    [ObservableProperty] public partial double DownloadsUsagePercent { get; private set; }

    /// <summary>Автоматически сохранять загрузки в папку Downloads.</summary>
    [ObservableProperty] public partial bool AutoSaveToDownloads { get; set; }

    partial void OnAutoSaveToDownloadsChanged(bool value)
    {
        if (_isLoadingSettings) return;
        _library.UpdateSettings(s => s.Storage.AutoSaveToDownloads = value);
    }

    partial void OnDownloadedTracksLimitMbChanged(int value)
    {
        if (_isLoadingSettings) return;
        _downloadsLimitDebounceTimer?.Stop();
        _downloadsLimitDebounceTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(500),
            DispatcherPriority.Normal,
            (_, _) =>
            {
                _downloadsLimitDebounceTimer?.Stop();
                _library.UpdateSettings(s => s.Storage.DownloadedTracksLimitMb = value);
                UpdateCacheStats();
            });
        _downloadsLimitDebounceTimer.Start();
    }

    partial void OnImageCacheLimitMbChanged(int value) => ScheduleStorageSettingsSave();
    partial void OnAudioCacheLimitMbChanged(int value) => ScheduleStorageSettingsSave();

    private void ScheduleStorageSettingsSave()
    {
        if (_isLoadingSettings) return;
        _storageDebounceTimer?.Stop();
        _storageDebounceTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(500),
            DispatcherPriority.Normal,
            (_, _) =>
            {
                _storageDebounceTimer?.Stop();
                SaveStorageSettings();
            });
        _storageDebounceTimer.Start();
    }

    partial void OnSelectedImageCachePresetChanged(LocalizedItem<ImageCachePreset>? value)
    {
        if (_isUpdatingPreset || _isLoadingSettings || value is null) return;
        _isUpdatingPreset = true;
        MaxBitmapCacheItems = value.Value switch
        {
            ImageCachePreset.Low => 20,
            ImageCachePreset.Medium => 50,
            ImageCachePreset.High => 100,
            _ => MaxBitmapCacheItems
        };
        _isUpdatingPreset = false;
    }

    partial void OnMaxBitmapCacheItemsChanged(int value)
    {
        if (_isLoadingSettings) return;
        _library.UpdateSettings(s => s.Storage.MaxBitmapCacheItems = value);
        _imageCache.EnforceLimits();

        if (_isUpdatingPreset) return;

        _isUpdatingPreset = true;
        SelectedImageCachePreset = value switch
        {
            20 => ImageCachePresets.FirstOrDefault(x => x.Value == ImageCachePreset.Low),
            50 => ImageCachePresets.FirstOrDefault(x => x.Value == ImageCachePreset.Medium),
            100 => ImageCachePresets.FirstOrDefault(x => x.Value == ImageCachePreset.High),
            _ => null
        };
        _isUpdatingPreset = false;
    }

    #endregion

    #region Theme

    /// <summary>Встроенные и пользовательские пресеты тем для ComboBox.</summary>
    public ObservableCollection<ThemeSettings> ThemePresets { get; } = [];

    /// <summary>Выбранный пресет темы; при смене — цвета применяются к color picker'ам.</summary>
    [ObservableProperty] public partial ThemeSettings? SelectedPreset { get; set; }

    [ObservableProperty] public partial Color AccentColor { get; set; }
    [ObservableProperty] public partial Color BgPrimaryColor { get; set; }
    [ObservableProperty] public partial Color BgSecondaryColor { get; set; }
    [ObservableProperty] public partial Color BgElevatedColor { get; set; }
    [ObservableProperty] public partial Color TextPrimaryColor { get; set; }
    [ObservableProperty] public partial Color TextSecondaryColor { get; set; }

    /// <summary>
    /// Признак несохранённых изменений темы.
    /// <para>Управляет видимостью кнопок Apply / Reset.</para>
    /// </summary>
    [ObservableProperty] public partial bool HasUnsavedThemeChanges { get; set; }

    partial void OnSelectedPresetChanged(ThemeSettings? value)
    {
        if (_isLoadingSettings || value is null) return;
        ApplyPresetToColorPickers(value);
    }

    partial void OnAccentColorChanged(Color value) => OnColorPickerChanged();
    partial void OnBgPrimaryColorChanged(Color value) => OnColorPickerChanged();
    partial void OnBgSecondaryColorChanged(Color value) => OnColorPickerChanged();
    partial void OnBgElevatedColorChanged(Color value) => OnColorPickerChanged();
    partial void OnTextPrimaryColorChanged(Color value) => OnColorPickerChanged();
    partial void OnTextSecondaryColorChanged(Color value) => OnColorPickerChanged();

    private void OnColorPickerChanged()
    {
        if (!_isLoadingSettings && !_isLoadingTheme)
            HasUnsavedThemeChanges = true;
    }

    #endregion

    #region Audio

    /// <summary>
    /// Обёрнутые значения AudioQualityPreference — ComboBox использует ToString()
    /// от LocalizedItem, DataTemplate и конвертер AudioQualityToString не нужны.
    /// </summary>
    public List<LocalizedItem<AudioQualityPreference>> QualityOptions { get; private set; } = [];

    /// <summary>Выбранный элемент качества; синхронизируется с настройками через подписку.</summary>
    [ObservableProperty] public partial LocalizedItem<AudioQualityPreference>? SelectedQualityItem { get; set; }

    [ObservableProperty] public partial int MaxVolumeLimit { get; set; }
    [ObservableProperty] public partial float TargetGainDb { get; set; }
    [ObservableProperty] public partial bool RememberTrackFormat { get; set; }
    [ObservableProperty] public partial bool VolumeBoostEnabled { get; set; }
    [ObservableProperty] public partial bool AudioNormalizationEnabled { get; set; }
    [ObservableProperty] public partial float NormalizationTargetLufs { get; set; }
    [ObservableProperty] public partial float NormalizationMaxGain { get; set; }

    /// <summary>Варианты кривой громкости для ComboBox.</summary>
    public ObservableCollection<LocalizedItem<VolumeCurveType>> VolumeCurveOptions { get; } = [];

    /// <summary>Выбранная кривая громкости; синхронизируется с настройками через подписку.</summary>
    [ObservableProperty] public partial LocalizedItem<VolumeCurveType>? SelectedVolumeCurve { get; set; }

    /// <summary>Варианты поведения при ошибке воспроизведения для ComboBox.</summary>
    public ObservableCollection<LocalizedItem<PlaybackErrorBehavior>> ErrorBehaviorOptions { get; } = [];

    /// <summary>Выбранное поведение при ошибке; синхронизируется с настройками через подписку.</summary>
    [ObservableProperty] public partial LocalizedItem<PlaybackErrorBehavior>? SelectedErrorBehavior { get; set; }

    [ObservableProperty] public partial bool PlayErrorSound { get; set; }
    [ObservableProperty] public partial bool SkipNTokenTracks { get; set; }

    /// <summary>Варианты режима нормализации для ComboBox.</summary>
    public ObservableCollection<LocalizedItem<NormalizationMode>> NormalizationModeOptions { get; } = [];

    /// <summary>Выбранный режим нормализации; синхронизируется с настройками через подписку.</summary>
    [ObservableProperty] public partial LocalizedItem<NormalizationMode>? SelectedNormalizationMode { get; set; }

    /// <summary>
    /// Флаг программного отката при отмене выключения нормализации.
    /// </summary>
    private bool _isRevertingNormalization;

    partial void OnMaxVolumeLimitChanged(int value)
    {
        if (_isLoadingSettings) return;
        _library.UpdateSettings(s => s.MaxVolumeLimit = value);
        _audio.OnMaxVolumeLimitChanged(value);
    }

    partial void OnTargetGainDbChanged(float value)
    {
        if (_isLoadingSettings) return;
        _library.UpdateSettings(s => s.TargetGainDb = value);
        _audio.UpdateAudioSettings();
    }

    partial void OnRememberTrackFormatChanged(bool value)
    {
        if (_isLoadingSettings) return;
        _library.UpdateSettings(s => s.RememberTrackFormat = value);
    }

    partial void OnVolumeBoostEnabledChanged(bool value)
    {
        if (_isLoadingSettings) return;
        _library.UpdateSettings(s => s.Audio.VolumeBoostEnabled = value);
        _audio.UpdateAudioSettings();
    }

    partial void OnAudioNormalizationEnabledChanged(bool value)
    {
        if (_isLoadingSettings || _isRevertingNormalization) return;

        if (!value)
        {
            Dispatcher.UIThread.Post(async () =>
            {
                var confirmed = await _dialog.ConfirmAsync(
                    SL["Settings_NormalizationDisable_Title"],
                    SL["Settings_NormalizationDisable_Message"],
                    SL["Common_Disable"],
                    SL["Common_Cancel"]);

                if (!confirmed)
                {
                    _isRevertingNormalization = true;
                    AudioNormalizationEnabled = true;
                    _isRevertingNormalization = false;
                    return;
                }

                _library.UpdateSettings(s => s.Audio.NormalizationEnabled = false);
                _audio.UpdateAudioSettings();
            });
            return;
        }

        _library.UpdateSettings(s => s.Audio.NormalizationEnabled = true);
        _audio.UpdateAudioSettings();
    }

    partial void OnNormalizationTargetLufsChanged(float value)
    {
        if (_isLoadingSettings) return;
        _normLufsDebounceTimer?.Stop();
        _normLufsDebounceTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(300),
            DispatcherPriority.Normal,
            (_, _) =>
            {
                _normLufsDebounceTimer?.Stop();
                _library.UpdateSettings(s => s.Audio.NormalizationTargetLufs = value);
                _audio.UpdateAudioSettings();
            });
        _normLufsDebounceTimer.Start();
    }

    partial void OnNormalizationMaxGainChanged(float value)
    {
        if (_isLoadingSettings) return;
        _normGainDebounceTimer?.Stop();
        _normGainDebounceTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(300),
            DispatcherPriority.Normal,
            (_, _) =>
            {
                _normGainDebounceTimer?.Stop();
                _library.UpdateSettings(s => s.Audio.NormalizationMaxGain = value);
                _audio.UpdateAudioSettings();
            });
        _normGainDebounceTimer.Start();
    }

    partial void OnSelectedNormalizationModeChanged(LocalizedItem<NormalizationMode>? value)
    {
        if (_isLoadingSettings || value is null) return;
        _library.UpdateSettings(s => s.Audio.NormalizationMode = value.Value);
        _audio.UpdateAudioSettings();
    }

    partial void OnSelectedVolumeCurveChanged(LocalizedItem<VolumeCurveType>? value)
    {
        if (_isLoadingSettings || value is null) return;
        _library.UpdateSettings(s => s.Audio.VolumeCurve = value.Value);
        _audio.UpdateAudioSettings();
    }

    partial void OnSelectedErrorBehaviorChanged(LocalizedItem<PlaybackErrorBehavior>? value)
    {
        if (_isLoadingSettings || value is null) return;
        _library.UpdateSettings(s => s.Audio.CriticalErrorBehavior = value.Value);
        UpdatePlaybackFailureActionAvailability();
    }

    partial void OnPlayErrorSoundChanged(bool value)
    {
        if (_isLoadingSettings) return;
        _library.UpdateSettings(s => s.Audio.PlayErrorSound = value);
    }

    partial void OnSkipNTokenTracksChanged(bool value)
    {
        if (_isLoadingSettings) return;
        _library.UpdateSettings(s => s.Audio.SkipNTokenTracks = value);
    }

    partial void OnSelectedQualityItemChanged(LocalizedItem<AudioQualityPreference>? value)
    {
        if (_isLoadingSettings || value is null) return;
        _library.UpdateSettings(s => s.QualityPreference = value.Value);
        _youtube.ClearCache();
    }

    #endregion

    #region Playback & Failure Settings

    /// <summary>Варианты уведомлений n-токена для ComboBox.</summary>
    public ObservableCollection<LocalizedItem<NTokenNotificationMode>> NTokenNotificationOptions { get; } = [];

    /// <summary>Выбранный режим предупреждений расшифровки n-токена.</summary>
    [ObservableProperty] public partial LocalizedItem<NTokenNotificationMode>? SelectedNTokenNotification { get; set; }

    /// <summary>Варианты поведения при сбое воспроизведения для ComboBox.</summary>
    public ObservableCollection<LocalizedItem<PlaybackFailureBehavior>> PlaybackFailureOptions { get; } = [];

    /// <summary>Выбранный режим поведения при фатальном сбое трека.</summary>
    [ObservableProperty] public partial LocalizedItem<PlaybackFailureBehavior>? SelectedPlaybackFailure { get; set; }

    [ObservableProperty] public partial bool IsPlaybackFailureActionEnabled { get; private set; } = true;

    partial void OnSelectedNTokenNotificationChanged(LocalizedItem<NTokenNotificationMode>? value)
    {
        if (_isLoadingSettings || value is null) return;
        _library.UpdateSettings(s => s.Audio.NTokenNotificationMode = value.Value);
    }

    partial void OnSelectedPlaybackFailureChanged(LocalizedItem<PlaybackFailureBehavior>? value)
    {
        if (_isLoadingSettings || value is null) return;
        _library.UpdateSettings(s => s.Audio.PlaybackFailureBehavior = value.Value);
    }

    #endregion

    #region UI & Behavior

    [ObservableProperty] public partial bool DiscordRpcEnabled { get; set; }
    [ObservableProperty] public partial bool AutoPlayOnPaste { get; set; }
    [ObservableProperty] public partial int SearchBatchSize { get; set; }
    [ObservableProperty] public partial bool EnableSearchCache { get; set; }
    [ObservableProperty] public partial int SearchCacheTtlMinutes { get; set; }

    /// <summary>Список доступных языков — статический, берётся из LocalizationService.</summary>
    public static List<LanguageItem> Languages => LocalizationService.Instance.AvailableLanguages;

    /// <summary>Выбранный язык; при смене применяется немедленно.</summary>
    [ObservableProperty] public partial LanguageItem? SelectedLanguage { get; set; }

    /// <summary>Варианты действия при закрытии окна для ComboBox.</summary>
    public ObservableCollection<LocalizedItem<CloseAction>> CloseActionOptions { get; } = [];

    /// <summary>Выбранное действие при закрытии; синхронизируется с настройками через подписку.</summary>
    [ObservableProperty] public partial LocalizedItem<CloseAction>? SelectedCloseAction { get; set; }

    [ObservableProperty] public partial bool MinimizeToTray { get; set; }

    /// <summary>
    /// Максимальное количество отображаемых подсказок в строке поиска и ленте чипов.
    /// </summary>
    [ObservableProperty] public partial int MaxSuggestionsCount { get; set; }

    partial void OnDiscordRpcEnabledChanged(bool value)
    {
        if (_isLoadingSettings) return;
        _library.UpdateSettings(s => s.DiscordRpcEnabled = value);
    }

    partial void OnAutoPlayOnPasteChanged(bool value)
    {
        if (_isLoadingSettings) return;
        _library.UpdateSettings(s => s.AutoPlayOnUrlPaste = value);
    }

    partial void OnSearchBatchSizeChanged(int value)
    {
        if (_isLoadingSettings) return;
        _library.UpdateSettings(s => s.SearchBatchSize = value);
    }

    partial void OnEnableSearchCacheChanged(bool value)
    {
        if (_isLoadingSettings) return;
        _library.UpdateSettings(s => s.EnableSearchCache = value);
    }

    partial void OnSearchCacheTtlMinutesChanged(int value)
    {
        if (_isLoadingSettings) return;
        _library.UpdateSettings(s => s.SearchCacheTtlMinutes = value);
        _ = _searchCache.CleanupExpiredAsync();
    }

    partial void OnSelectedLanguageChanged(LanguageItem? value)
    {
        if (_isLoadingSettings || value is null) return;
        LocalizationService.Instance.CurrentLanguage = value.Code;
        _library.UpdateSettings(s => s.LanguageCode = value.Code);
    }

    partial void OnSelectedCloseActionChanged(LocalizedItem<CloseAction>? value)
    {
        if (_isLoadingSettings || value is null) return;
        _library.UpdateSettings(s => s.CloseAction = value.Value);
    }

    partial void OnMinimizeToTrayChanged(bool value)
    {
        if (_isLoadingSettings) return;
        _library.UpdateSettings(s => s.MinimizeToTray = value);
    }

    partial void OnMaxSuggestionsCountChanged(int value)
    {
        if (_isLoadingSettings) return;
        _suggestionsDebounceTimer?.Stop();
        _suggestionsDebounceTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(400),
            DispatcherPriority.Normal,
            (_, _) =>
            {
                _suggestionsDebounceTimer?.Stop();
                _library.UpdateSettings(s => s.MaxSuggestionsCount = value);
            });
        _suggestionsDebounceTimer.Start();
    }

    #endregion

    #region Memory

    /// <summary>
    /// Элемент пресета GPU-кэша.
    /// <para>ToString() → Name: ComboBox не создаёт DataTemplate-контейнеры.</para>
    /// </summary>
    public sealed record GpuCachePresetItem(long Mb, string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>Пресеты размера GPU-кэша текстур для ComboBox.</summary>
    public ObservableCollection<GpuCachePresetItem> GpuCachePresets { get; } = [];

    /// <summary>Выбранный пресет GPU-кэша; при смене требует перезапуска.</summary>
    [ObservableProperty] public partial GpuCachePresetItem? SelectedGpuCachePreset { get; set; }

    /// <summary>Признак того, что изменение GPU-кэша требует перезапуска приложения.</summary>
    [ObservableProperty] public partial bool GpuCacheRestartRequired { get; private set; }

    [ObservableProperty] public partial bool AutoMemoryCleanupEnabled { get; set; }
    [ObservableProperty] public partial int MemoryCleanupIntervalMinutes { get; set; }
    [ObservableProperty] public partial int MemoryPressureThresholdMb { get; set; }

    /// <summary>Принудительная очистка памяти прямо сейчас (aggressive GC).</summary>
    public IRelayCommand CleanupMemoryNowCommand { get; }

    partial void OnSelectedGpuCachePresetChanged(GpuCachePresetItem? value)
    {
        if (_isLoadingSettings || value is null) return;
        if (BootstrapSettings.Current.GpuTextureCacheMb == value.Mb) return;

        BootstrapSettings.Current.GpuTextureCacheMb = value.Mb;
        BootstrapSettings.Current.Save();
        GpuCacheRestartRequired = true;
        Log.Info($"[Settings] GPU cache → {value.Mb}MB (restart required)");
    }

    partial void OnAutoMemoryCleanupEnabledChanged(bool value)
    {
        if (_isLoadingSettings) return;
        _library.UpdateSettings(s => s.Memory.AutoCleanupEnabled = value);
        MemoryCleanupHelper.RestartAutoCleanup();
    }

    partial void OnMemoryCleanupIntervalMinutesChanged(int value)
    {
        if (_isLoadingSettings) return;
        _memoryIntervalDebounceTimer?.Stop();
        _memoryIntervalDebounceTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(500),
            DispatcherPriority.Normal,
            (_, _) =>
            {
                _memoryIntervalDebounceTimer?.Stop();
                _library.UpdateSettings(s => s.Memory.AutoCleanupIntervalMinutes = value);
                MemoryCleanupHelper.RestartAutoCleanup();
            });
        _memoryIntervalDebounceTimer.Start();
    }

    partial void OnMemoryPressureThresholdMbChanged(int value)
    {
        if (_isLoadingSettings) return;
        _memoryPressureDebounceTimer?.Stop();
        _memoryPressureDebounceTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(500),
            DispatcherPriority.Normal,
            (_, _) =>
            {
                _memoryPressureDebounceTimer?.Stop();
                _library.UpdateSettings(s => s.Memory.PressureThresholdMb = value);
            });
        _memoryPressureDebounceTimer.Start();
    }

    #endregion

    #region Commands

    public IAsyncRelayCommand BrowseDownloadPathCommand { get; }
    public IAsyncRelayCommand ClearHistoryCommand { get; }
    public IAsyncRelayCommand ResetLibraryCommand { get; }
    public IAsyncRelayCommand LoginCommand { get; }
    public IAsyncRelayCommand LogoutCommand { get; }
    public IAsyncRelayCommand SwitchAccountCommand { get; }
    public IAsyncRelayCommand ClearImageCacheCommand { get; }
    public IAsyncRelayCommand ClearAudioCacheCommand { get; }
    public IRelayCommand ApplyThemeCommand { get; }
    public IRelayCommand ResetThemeCommand { get; }
    public IAsyncRelayCommand ClearDownloadsCommand { get; }
    public IAsyncRelayCommand ShowNormalizationInfoCommand { get; }
    public IAsyncRelayCommand RefreshProfileCommand { get; }
    public IAsyncRelayCommand TestNetworkCommand { get; }

    #endregion

    /// <summary>
    /// Создаёт VM настроек и инициализирует команды и sidebar.
    /// </summary>
    public SettingsViewModel(
        INetworkManager networkManager,
        LibraryService library,
        TrackRegistry registry,
        SearchCacheService searchCache,
        ImageCacheService imageCache,
        ThemeManagerService themeManager,
        CookieAuthService auth,
        DialogService dialog,
        AudioEngine audio,
        LocalAuthServer localServer,
        YoutubeProvider youtube,
        YoutubeUserDataService userData,
        NotificationService notifications)
    {
        _networkManager = networkManager;
        _library = library;
        _registry = registry;
        _searchCache = searchCache;
        _imageCache = imageCache;
        _themeManager = themeManager;
        _auth = auth;
        _dialog = dialog;
        _audio = audio;
        _localServer = localServer;
        _youtube = youtube;
        _userData = userData;
        _notifications = notifications;

        LoginCommand = new AsyncRelayCommand(LoginAsync);
        SwitchAccountCommand = new AsyncRelayCommand(SwitchAccountAsync);
        LogoutCommand = new AsyncRelayCommand(LogoutAsync);
        BrowseDownloadPathCommand = new AsyncRelayCommand(BrowseDownloadPathAsync);
        ClearHistoryCommand = new AsyncRelayCommand(ClearHistoryAsync);
        ResetLibraryCommand = new AsyncRelayCommand(ResetLibraryAsync);
        ClearImageCacheCommand = new AsyncRelayCommand(ClearImageCacheAsync);
        ClearAudioCacheCommand = new AsyncRelayCommand(ClearAudioCacheAsync);
        ApplyThemeCommand = new RelayCommand(ApplyTheme);
        ResetThemeCommand = new RelayCommand(ResetTheme);
        ClearDownloadsCommand = new AsyncRelayCommand(ClearDownloadsAsync);
        CleanupMemoryNowCommand = new RelayCommand(() => MemoryCleanupHelper.PerformCleanup(aggressive: true));
        ShowNormalizationInfoCommand = new AsyncRelayCommand(ShowNormalizationInfoAsync);
        RefreshProfileCommand = new AsyncRelayCommand(RefreshProfileAsync);
        TestNetworkCommand = new AsyncRelayCommand(TestNetworkAsync);

        SidebarItems =
        [
            new AccountLanguageSidebarItem(this),
            new NetworkSidebarItem(this),
            new StorageCacheSidebarItem(this),
            new MemorySidebarItem(this),
            new AppearanceSidebarItem(this),
            new AudioSidebarItem(this),
            new PlaybackSidebarItem(this),
            new WindowBehaviorSidebarItem(this),
            new GeneralSidebarItem(this),
        ];

        var cache = AudioSourceFactory.GlobalCache;
        if (cache != null)
        {
            cache.OnFormatCached += OnAudioFormatCached;
            cache.OnCacheCleared += OnAudioCacheCleared;
        }

        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
    }

    private void OnAudioFormatCached(string trackId, AudioFormat format, int bitrate, bool isDownloaded)
    {
        Dispatcher.UIThread.Post(UpdateCacheStats);
    }

    private void OnAudioCacheCleared()
    {
        Dispatcher.UIThread.Post(UpdateCacheStats);
    }

    /// <inheritdoc />
    public void PrepareForTransition()
    {
        IsContentReady = false;
    }

    /// <summary>
    /// Вызывается при переходе на страницу настроек.
    /// Перезагружает актуальные параметры из сервиса библиотеки для синхронизации с внешними изменениями.
    /// </summary>
    public override async Task OnNavigatedToAsync()
    {
        if (_isDisposed) return;

        if (_isDataLoaded)
        {
            LoadAllSettings();
            IsContentReady = true;
            return;
        }

        await Task.Delay(NavigationDebounceMs);
        if (_isDisposed) return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            InitializeLists();
            LoadAllSettings();
            UpdateCacheStats();

            SelectedSidebarItem ??= SidebarItems.FirstOrDefault();

            _isDataLoaded = true;
            IsContentReady = true;
        });

        MemoryCleanupHelper.PerformCleanup(aggressive: false);
    }

    /// <inheritdoc />
    protected override void OnAccountChanged()
    {
        base.OnAccountChanged();

        _isDataLoaded = false;

        if (IsContentReady)
        {
            Log.Info("[Settings] Account changed while settings page was open. Re-loading configuration in real-time.");
            IsContentReady = false;
            LoadAllSettings();
            UpdateCacheStats();
            IsContentReady = true;
        }
    }

    private void OnLanguageChanged(object? sender, string e) => RefreshLocalizedLists();

    private void InitializeLists()
    {
        RefreshThemePresets();
        RefreshLocalizedLists();
        InitGpuCachePresets();
    }

    private void InitGpuCachePresets()
    {
        GpuCachePresets.Clear();
        GpuCachePresets.Add(new GpuCachePresetItem(32, $"32 MB  ({SL["Cache_Low"]})"));
        GpuCachePresets.Add(new GpuCachePresetItem(64, $"64 MB  ({SL["Cache_Medium"]}) ✓"));
        GpuCachePresets.Add(new GpuCachePresetItem(128, $"128 MB ({SL["Cache_High"]})"));
        GpuCachePresets.Add(new GpuCachePresetItem(256, $"256 MB ({SL["Cache_Ultra"]})"));

        var currentMb = BootstrapSettings.Current.GpuTextureCacheMb;
        SelectedGpuCachePreset = GpuCachePresets.FirstOrDefault(x => x.Mb == currentMb)
                              ?? GpuCachePresets[1];
    }

    private void RefreshThemePresets()
    {
        ThemePresets.Clear();

        foreach (var preset in ThemeManagerService.GetBuiltInPresets())
            ThemePresets.Add(preset);

        var saved = _themeManager.GetCurrentTheme();
        var isBuiltIn = ThemePresets.Any(p =>
            string.Equals(p.AccentColor, saved.AccentColor, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.BgPrimary, saved.BgPrimary, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.BgSecondary, saved.BgSecondary, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.AccentHover, saved.AccentHover, StringComparison.OrdinalIgnoreCase));

        if (!isBuiltIn && !saved.IsBuiltIn)
            ThemePresets.Add(saved);
    }

    private void LoadThemeColors()
    {
        _isLoadingTheme = true;
        try
        {
            var currentTheme = _themeManager.GetCurrentTheme();
            ApplyThemeToColorPickers(currentTheme);

            var matchingPreset = ThemePresets.FirstOrDefault(p =>
                string.Equals(p.AccentColor, currentTheme.AccentColor, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.BgPrimary, currentTheme.BgPrimary, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.BgSecondary, currentTheme.BgSecondary, StringComparison.OrdinalIgnoreCase));

            matchingPreset ??= ThemePresets.FirstOrDefault(p =>
                string.Equals(p.Name, currentTheme.Name, StringComparison.OrdinalIgnoreCase));

            SelectedPreset = matchingPreset ?? ThemePresets.FirstOrDefault();
            HasUnsavedThemeChanges = false;
        }
        finally
        {
            _isLoadingTheme = false;
        }
    }

    private void ApplyTheme()
    {
        static string GetRgbHex(Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";

        var current = _themeManager.GetCurrentTheme();

        var theme = new ThemeSettings
        {
            Name = SelectedPreset?.Name ?? SL["Theme_Custom"],

            AccentColor = AccentColor.ToString(),
            AccentHover = SmartAccentHover(AccentColor).ToString(),
            BgPrimary = BgPrimaryColor.ToString(),
            BgSecondary = BgSecondaryColor.ToString(),
            BgElevated = BgElevatedColor.ToString(),
            BgHighlight = LightenColor(BgSecondaryColor, 0.1).ToString(),
            BgHover = LightenColor(BgSecondaryColor, 0.2).ToString(),
            BgSkeleton = LightenColor(BgSecondaryColor, 0.05).ToString(),
            BgSkeletonDeep = DarkenColor(BgSecondaryColor, 0.2).ToString(),
            BgOverlay = $"#CC{GetRgbHex(BgPrimaryColor)}",

            TextPrimary = TextPrimaryColor.ToString(),
            TextSecondary = TextSecondaryColor.ToString(),
            TextMuted = DarkenColor(TextSecondaryColor, 0.3).ToString(),
            TextDark = BgPrimaryColor.ToString(),

            SystemError = current.SystemError,
            SystemErrorBg = current.SystemErrorBg,
            SystemInfoBlue = current.SystemInfoBlue,
            SystemWarnOrange = current.SystemWarnOrange,
        };

        _themeManager.SaveTheme(theme);
        _themeManager.ApplyTheme(theme);
        HasUnsavedThemeChanges = false;

        RefreshThemePresets();

        _isLoadingTheme = true;
        SelectedPreset = ThemePresets.FirstOrDefault(p =>
            string.Equals(p.Name, theme.Name, StringComparison.OrdinalIgnoreCase))
            ?? ThemePresets.FirstOrDefault();
        _isLoadingTheme = false;
    }

    private static Color SmartAccentHover(Color accent)
    {
        var brightness = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255.0;
        return brightness > 0.7
            ? DarkenColor(accent, 0.15)
            : LightenColor(accent, 0.15);
    }

    private void RefreshLocalizedLists()
    {
        var currentProfile = SelectedInternetProfile?.Value ?? _library.Settings.InternetProfile;
        InternetProfileOptions.Clear();
        foreach (var p in Enum.GetValues<InternetProfile>())
            InternetProfileOptions.Add(new LocalizedItem<InternetProfile>(p, SL[$"NetProfile_{p}"]));
        SelectedInternetProfile = InternetProfileOptions.FirstOrDefault(x => x.Value == currentProfile)
                               ?? InternetProfileOptions[1];

        var currentImgPreset = SelectedImageCachePreset?.Value ?? ImageCachePreset.Custom;
        ImageCachePresets.Clear();
        ImageCachePresets.Add(new LocalizedItem<ImageCachePreset>(ImageCachePreset.Low, $"{SL["Cache_Low"]} (20)"));
        ImageCachePresets.Add(new LocalizedItem<ImageCachePreset>(ImageCachePreset.Medium, $"{SL["Cache_Medium"]} (50)"));
        ImageCachePresets.Add(new LocalizedItem<ImageCachePreset>(ImageCachePreset.High, $"{SL["Cache_High"]} (100)"));
        if (currentImgPreset != ImageCachePreset.Custom)
            SelectedImageCachePreset = ImageCachePresets.FirstOrDefault(x => x.Value == currentImgPreset);

        var currentCurve = SelectedVolumeCurve?.Value ?? _library.Settings.Audio.VolumeCurve;
        VolumeCurveOptions.Clear();
        VolumeCurveOptions.Add(new(VolumeCurveType.Linear, SL["VolumeCurve_Linear"]));
        VolumeCurveOptions.Add(new(VolumeCurveType.Quadratic, SL["VolumeCurve_Quadratic"]));
        VolumeCurveOptions.Add(new(VolumeCurveType.Logarithmic, SL["VolumeCurve_Logarithmic"]));
        VolumeCurveOptions.Add(new(VolumeCurveType.Cubic, SL["VolumeCurve_Cubic"]));
        VolumeCurveOptions.Add(new(VolumeCurveType.SpeedOfLight, SL["VolumeCurve_SpeedOfLight"]));
        SelectedVolumeCurve = VolumeCurveOptions.FirstOrDefault(x => x.Value == currentCurve)
                           ?? VolumeCurveOptions[1];

        var currentErrorBehavior = SelectedErrorBehavior?.Value ?? _library.Settings.Audio.CriticalErrorBehavior;
        ErrorBehaviorOptions.Clear();
        ErrorBehaviorOptions.Add(new(PlaybackErrorBehavior.Dialog, SL["Settings_ErrorBehavior_Dialog"]));
        ErrorBehaviorOptions.Add(new(PlaybackErrorBehavior.ToastAndSkip, SL["Settings_ErrorBehavior_ToastAndSkip"]));
        ErrorBehaviorOptions.Add(new(PlaybackErrorBehavior.Ignore, SL["Settings_ErrorBehavior_Ignore"]));
        SelectedErrorBehavior = ErrorBehaviorOptions.FirstOrDefault(x => x.Value == currentErrorBehavior)
                             ?? ErrorBehaviorOptions.FirstOrDefault(x => x.Value == PlaybackErrorBehavior.ToastAndSkip)
                             ?? ErrorBehaviorOptions[0];

        var currentNTokenMode = SelectedNTokenNotification?.Value ?? _library.Settings.Audio.NTokenNotificationMode;
        NTokenNotificationOptions.Clear();
        NTokenNotificationOptions.Add(new(NTokenNotificationMode.Disabled, SL["Settings_NTokenMode_Disabled"]));
        NTokenNotificationOptions.Add(new(NTokenNotificationMode.PanelOnly, SL["Settings_NTokenMode_PanelOnly"]));
        NTokenNotificationOptions.Add(new(NTokenNotificationMode.Toast, SL["Settings_NTokenMode_Toast"]));
        SelectedNTokenNotification = NTokenNotificationOptions.FirstOrDefault(x => x.Value == currentNTokenMode)
                                  ?? NTokenNotificationOptions.FirstOrDefault(x => x.Value == NTokenNotificationMode.Toast)
                                  ?? NTokenNotificationOptions[^1];

        var currentFailureMode = SelectedPlaybackFailure?.Value ?? _library.Settings.Audio.PlaybackFailureBehavior;
        PlaybackFailureOptions.Clear();
        PlaybackFailureOptions.Add(new(PlaybackFailureBehavior.SkipAndPlay, SL["Settings_PlaybackFailure_SkipAndPlay"]));
        PlaybackFailureOptions.Add(new(PlaybackFailureBehavior.SkipAndPause, SL["Settings_PlaybackFailure_SkipAndPause"]));
        PlaybackFailureOptions.Add(new(PlaybackFailureBehavior.Stop, SL["Settings_PlaybackFailure_Stop"]));
        SelectedPlaybackFailure = PlaybackFailureOptions.FirstOrDefault(x => x.Value == currentFailureMode)
                               ?? PlaybackFailureOptions.FirstOrDefault(x => x.Value == PlaybackFailureBehavior.SkipAndPause)
                               ?? PlaybackFailureOptions[0];

        UpdatePlaybackFailureActionAvailability();

        var currentCloseAction = SelectedCloseAction?.Value ?? _library.Settings.CloseAction;
        CloseActionOptions.Clear();
        CloseActionOptions.Add(new(CloseAction.Exit, SL["CloseAction_Exit"]));
        CloseActionOptions.Add(new(CloseAction.MinimizeToTray, SL["CloseAction_MinimizeToTray"]));
        CloseActionOptions.Add(new(CloseAction.Ask, SL["CloseAction_Ask"]));
        SelectedCloseAction = CloseActionOptions.FirstOrDefault(x => x.Value == currentCloseAction)
                           ?? CloseActionOptions[2];

        var currentNormMode = SelectedNormalizationMode?.Value ?? _library.Settings.Audio.NormalizationMode;
        NormalizationModeOptions.Clear();
        NormalizationModeOptions.Add(new(NormalizationMode.Bidirectional, SL["NormMode_Bidirectional"]));
        NormalizationModeOptions.Add(new(NormalizationMode.DownwardOnly, SL["NormMode_DownwardOnly"]));
        SelectedNormalizationMode = NormalizationModeOptions.FirstOrDefault(x => x.Value == currentNormMode)
                                 ?? NormalizationModeOptions[0];

        var currentQuality = SelectedQualityItem?.Value ?? _library.Settings.QualityPreference;
        QualityOptions = Enum.GetValues<AudioQualityPreference>()
            .Select(q => new LocalizedItem<AudioQualityPreference>(
                q, SL[$"AudioQuality_{q}"] ?? q.ToString()))
            .ToList();
        SelectedQualityItem = QualityOptions.FirstOrDefault(x => x.Value == currentQuality)
                           ?? QualityOptions[0];
        OnPropertyChanged(nameof(QualityOptions));
    }

    private void LoadAllSettings()
    {
        _isLoadingSettings = true;
        try
        {
            var s = _library.Settings;

            DownloadPath = _library.DownloadPath;
            DiscordRpcEnabled = s.DiscordRpcEnabled;
            AutoPlayOnPaste = s.AutoPlayOnUrlPaste;
            SearchBatchSize = s.SearchBatchSize;
            MaxVolumeLimit = s.MaxVolumeLimit;
            TargetGainDb = s.TargetGainDb;
            RememberTrackFormat = s.RememberTrackFormat;

            EnableSearchCache = s.EnableSearchCache;
            SearchCacheTtlMinutes = s.SearchCacheTtlMinutes;
            MaxSuggestionsCount = s.MaxSuggestionsCount > 0 ? s.MaxSuggestionsCount : 8;
            SelectedLanguage = Languages.FirstOrDefault(x => x.Code == s.LanguageCode) ?? Languages[0];

            var mem = s.Memory;
            AutoMemoryCleanupEnabled = mem.AutoCleanupEnabled;
            MemoryCleanupIntervalMinutes = mem.AutoCleanupIntervalMinutes > 0 ? mem.AutoCleanupIntervalMinutes : 30;
            MemoryPressureThresholdMb = mem.PressureThresholdMb > 0 ? mem.PressureThresholdMb : 400;

            VolumeBoostEnabled = s.Audio.VolumeBoostEnabled;
            AudioNormalizationEnabled = s.Audio.NormalizationEnabled;
            NormalizationTargetLufs = s.Audio.NormalizationTargetLufs;
            NormalizationMaxGain = s.Audio.NormalizationMaxGain;
            SelectedNormalizationMode = NormalizationModeOptions.FirstOrDefault(x => x.Value == s.Audio.NormalizationMode)
                                     ?? NormalizationModeOptions[0];
            SelectedVolumeCurve = VolumeCurveOptions.FirstOrDefault(x => x.Value == s.Audio.VolumeCurve)
                                     ?? VolumeCurveOptions[1];
            PlayErrorSound = s.Audio.PlayErrorSound;
            SelectedErrorBehavior = ErrorBehaviorOptions.FirstOrDefault(x => x.Value == s.Audio.CriticalErrorBehavior)
                                 ?? ErrorBehaviorOptions.FirstOrDefault(x => x.Value == PlaybackErrorBehavior.ToastAndSkip)
                                 ?? ErrorBehaviorOptions[0];
            SkipNTokenTracks = s.Audio.SkipNTokenTracks;
            SelectedNTokenNotification = NTokenNotificationOptions.FirstOrDefault(x => x.Value == s.Audio.NTokenNotificationMode)
                                      ?? NTokenNotificationOptions.FirstOrDefault(x => x.Value == NTokenNotificationMode.Toast)
                                      ?? NTokenNotificationOptions[^1];
            SelectedPlaybackFailure = PlaybackFailureOptions.FirstOrDefault(x => x.Value == s.Audio.PlaybackFailureBehavior)
                                   ?? PlaybackFailureOptions.FirstOrDefault(x => x.Value == PlaybackFailureBehavior.SkipAndPause)
                                   ?? PlaybackFailureOptions[0];

            SelectedQualityItem = QualityOptions.FirstOrDefault(x => x.Value == s.QualityPreference)
                               ?? QualityOptions[0];

            IsAuthenticated = _auth.IsAuthenticated;
            RaiseAccountProperties();

            SelectedInternetProfile = InternetProfileOptions.FirstOrDefault(x => x.Value == s.InternetProfile)
                                   ?? InternetProfileOptions[1];

            ProxyEnabled = s.Proxy.Enabled;
            ProxyHost = s.Proxy.Host;
            ProxyPort = s.Proxy.Port;
            ProxyAuth = s.Proxy.UseAuth;
            ProxyUser = s.Proxy.Username;
            ProxyPass = s.Proxy.Password;

            ImageCacheLimitMb = s.Storage.ImageCacheLimitMb;
            AudioCacheLimitMb = s.Storage.AudioCacheLimitMb;
            DownloadedTracksLimitMb = s.Storage.DownloadedTracksLimitMb;
            AutoSaveToDownloads = s.Storage.AutoSaveToDownloads;

            MaxBitmapCacheItems = s.Storage.MaxBitmapCacheItems > 0 ? s.Storage.MaxBitmapCacheItems : 40;
            _isUpdatingPreset = true;
            SelectedImageCachePreset = MaxBitmapCacheItems switch
            {
                20 => ImageCachePresets.FirstOrDefault(x => x.Value == ImageCachePreset.Low),
                50 => ImageCachePresets.FirstOrDefault(x => x.Value == ImageCachePreset.Medium),
                100 => ImageCachePresets.FirstOrDefault(x => x.Value == ImageCachePreset.High),
                _ => null
            };
            _isUpdatingPreset = false;

            SelectedCloseAction = CloseActionOptions.FirstOrDefault(x => x.Value == s.CloseAction)
                               ?? CloseActionOptions.LastOrDefault();
            MinimizeToTray = s.MinimizeToTray;

            UpdatePlaybackFailureActionAvailability();
            LoadThemeColors();
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    private void ApplyPresetToColorPickers(ThemeSettings preset)
    {
        ApplyThemeToColorPickers(preset);
        HasUnsavedThemeChanges = true;
    }

    private void ApplyThemeToColorPickers(ThemeSettings theme)
    {
        _isLoadingTheme = true;
        try
        {
            AccentColor = ParseColorSafe(theme.AccentColor);
            BgPrimaryColor = ParseColorSafe(theme.BgPrimary);
            BgSecondaryColor = ParseColorSafe(theme.BgSecondary);
            BgElevatedColor = ParseColorSafe(theme.BgElevated);
            TextPrimaryColor = ParseColorSafe(theme.TextPrimary);
            TextSecondaryColor = ParseColorSafe(theme.TextSecondary);
        }
        finally
        {
            _isLoadingTheme = false;
        }
    }

    private void ResetTheme()
    {
        _themeManager.ResetToDefault();
        LoadThemeColors();
        SelectedPreset = ThemePresets.FirstOrDefault();
    }

    private static Color ParseColorSafe(string hex)
    {
        try { return Color.Parse(hex); }
        catch { return Colors.Magenta; }
    }

    private static Color LightenColor(Color c, double factor) =>
        Color.FromArgb(c.A,
            (byte)Math.Min(255, c.R + (255 - c.R) * factor),
            (byte)Math.Min(255, c.G + (255 - c.G) * factor),
            (byte)Math.Min(255, c.B + (255 - c.B) * factor));

    private static Color DarkenColor(Color c, double factor) =>
        Color.FromArgb(c.A,
            (byte)(c.R * (1 - factor)),
            (byte)(c.G * (1 - factor)),
            (byte)(c.B * (1 - factor)));

    private void SaveNetworkSettings()
    {
        var proxy = new ProxySettings
        {
            Enabled = ProxyEnabled,
            Host = ProxyHost,
            Port = ProxyPort,
            UseAuth = ProxyAuth,
            Username = ProxyUser,
            Password = ProxyPass,
        };

        _library.UpdateSettings(s => s.Proxy = proxy);
        _networkManager.UpdateProxy(proxy);

        NetworkStatus = NetworkStatusKind.Unknown;
        NetworkStatusText = "";
        NetworkLatencyMs = 0;
        OnPropertyChanged(nameof(NetworkStatusColor));
        OnPropertyChanged(nameof(HasLatency));

        Log.Info("[Settings] Network settings applied immediately via NetworkManager.");
    }

    private void SaveStorageSettings()
    {
        _library.UpdateSettings(s =>
        {
            s.Storage.ImageCacheLimitMb = ImageCacheLimitMb;
            s.Storage.AudioCacheLimitMb = AudioCacheLimitMb;
        });
        UpdateCacheStats();
    }

    private async Task ClearImageCacheAsync()
    {
        await _imageCache.ClearDiskCacheAsync();
        UpdateCacheStats();
    }

    private async Task ClearAudioCacheAsync()
    {
        var cache = AudioSourceFactory.GlobalCache;
        if (cache is not null)
            await cache.ClearAllAsync();
        else
            Log.Warn("[AudioCache] AudioCacheManager not initialized, cannot clear cache.");

        UpdateCacheStats();
    }

    private void UpdatePlaybackFailureActionAvailability()
    {
        var behavior = SelectedErrorBehavior?.Value ?? _library.Settings.Audio.CriticalErrorBehavior;
        IsPlaybackFailureActionEnabled = PlaybackErrorBehaviorMatrix.UsesPlaybackFailureBehavior(behavior);
    }

    private void UpdateCacheStats()
    {
        var (memItems, _, imgCount, imgSizeMb) = _imageCache.GetStats();

        var cache = AudioSourceFactory.GlobalCache;
        var (audioFileCount, audioSizeMb) = cache?.GetStatsCompact() ?? (0, 0);
        var (downloadFileCount, downloadSizeMb) = AudioCacheManager.GetDownloadsStats();

        ImageCacheStats = $"{imgSizeMb} MB / {ImageCacheLimitMb} MB ({imgCount} {SL["Common_Files"]}, RAM: {memItems})";
        AudioCacheStats = $"{audioSizeMb} MB / {AudioCacheLimitMb} MB ({audioFileCount} {SL["Common_Files"]})";
        DownloadsStats = $"{downloadSizeMb} MB / {DownloadedTracksLimitMb} MB ({downloadFileCount} {SL["Common_Files"]})";

        ImageCacheUsagePercent = ImageCacheLimitMb > 0 ? Math.Clamp((double)imgSizeMb / ImageCacheLimitMb, 0, 1) : 0;
        AudioCacheUsagePercent = AudioCacheLimitMb > 0 ? Math.Clamp((double)audioSizeMb / AudioCacheLimitMb, 0, 1) : 0;
        DownloadsUsagePercent = DownloadedTracksLimitMb > 0 ? Math.Clamp((double)downloadSizeMb / DownloadedTracksLimitMb, 0, 1) : 0;
    }

    private async Task LoginAsync()
    {
        IsAccountLoading = true;
        try
        {
            var authVm = new AuthDialogViewModel(_auth, _userData, _localServer);
            var host = AppEntry.Services.GetRequiredService<DialogHostViewModel>();
            var tcs = new TaskCompletionSource<bool>();

            authVm.OnResult = result =>
            {
                host.CloseDialog(result);
                tcs.TrySetResult(result);
            };

            _ = host.ShowAsync<object>(authVm);
            var success = await tcs.Task;

            if (success)
            {
                IsAuthenticated = _auth.IsAuthenticated;
                RaiseAccountProperties();

                await _notifications.ShowToastAsync(
                    titleKey: SL["Dialog_Success"] ?? "Success",
                    messageKey: string.Format(SL["Auth_LoggedInAs"] ?? "Signed in: {0}", _auth.State.UserName),
                    severity: NotificationSeverity.Success,
                    durationMs: 4000);
            }
        }
        finally
        {
            IsAccountLoading = false;
        }
    }

    private async Task SwitchAccountAsync()
    {
        if (!IsAuthenticated) return;

        IsAccountLoading = true;
        _isLoadingSettings = true;
        try
        {
            var accounts = _auth.State.CachedAccounts;
            if (accounts == null || accounts.Count == 0)
            {
                Log.Info("[Settings] No cached accounts found. Making fallback network request...");
                accounts = await _userData.GetAvailableAccountsAsync();
            }

            if (accounts.Count == 0)
            {
                await _dialog.ShowInfoAsync(SL["Dialog_Error_Title"] ?? "Error", SL["Auth_ProfileLoadError_Message"] ?? "Failed to load profile data.");
                return;
            }

            if (accounts.Count <= 1)
            {
                await _dialog.ShowInfoAsync(SL["Dialog_Info_Title"] ?? "Info", SL["Auth_NoMultipleAccounts"] ?? "There are no other channels available for this profile.");
                return;
            }

            var selectedAccount = await _dialog.ShowAccountSelectionDialogAsync(accounts);
            if (selectedAccount == null) return;

            _auth.SetAuthUser(selectedAccount.AuthUser);
            _auth.UpdateUserProfile(selectedAccount.Name, selectedAccount.Email, selectedAccount.AvatarUrl, selectedAccount.GaiaId);

            _youtube.ClearCache();
            RaiseAccountProperties();

            await _notifications.ShowToastAsync(
                titleKey: SL["Dialog_Success"] ?? "Success",
                messageKey: string.Format(SL["Auth_LoggedInAs"] ?? "Signed in: {0}", selectedAccount.Name),
                severity: NotificationSeverity.Success,
                durationMs: 4000);
        }
        catch (LoginRequiredException ex) when (ex.Reason == LoginRequiredReason.SessionExpired)
        {
            Log.Warn("[Settings] Switch account failed due to expired session. Prompting user to update cookies.");

            var title = SL["Auth_SessionExpired_SwitchAccount_Title"] ?? "Session Update Required";
            var msg = SL["Auth_SessionExpired_SwitchAccount_Message"] ?? "To switch accounts, you need to update your authorization because the current session has expired.\n\nDo you want to sign in again right now?";
            var loginText = SL["Auth_Login"] ?? "Sign In";
            var cancelText = SL["Common_Cancel"] ?? "Cancel";

            bool wantsToLogin = await _dialog.ConfirmAsync(title, msg, loginText, cancelText);
            if (wantsToLogin)
            {
                await LoginAsync();
            }
        }
        catch (Exception ex)
        {
            await _dialog.ShowInfoAsync(SL["Dialog_Error_Title"] ?? "Error", ex.Message);
        }
        finally
        {
            _isLoadingSettings = false;
            IsAccountLoading = false;
        }
    }

    private async Task RefreshProfileAsync()
    {
        if (!IsAuthenticated) return;

        IsAccountLoading = true;
        try
        {
            var (name, email, avatar, gaiaId) = await _userData.GetAccountInfoAsync();
            if (!string.IsNullOrEmpty(name))
            {
                _auth.UpdateUserProfile(name, email, avatar, gaiaId);
                RaiseAccountProperties();
            }

            _ = _userData.GetAvailableAccountsAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"[Settings] Failed to refresh profile manually: {ex.Message}");
        }
        finally
        {
            IsAccountLoading = false;
        }
    }

    private async Task LogoutAsync()
    {
        if (!await _dialog.ConfirmAsync(SL["Auth_Logout"], SL["Dialog_LogoutMessage"])) return;
        IsAccountLoading = true;
        try
        {
            _auth.Logout();
            IsAuthenticated = _auth.IsAuthenticated;
            RaiseAccountProperties();
        }
        finally
        {
            IsAccountLoading = false;
        }
    }

    private void RaiseAccountProperties()
    {
        OnPropertyChanged(nameof(AccountName));
        OnPropertyChanged(nameof(AccountAvatarUrl));
        OnPropertyChanged(nameof(AccountSubtitle));
    }

    private async Task BrowseDownloadPathAsync()
    {
        var newPath = await DialogService.SelectFolderAsync(DownloadPath);
        if (string.IsNullOrEmpty(newPath)) return;
        DownloadPath = newPath;
        _library.DownloadPath = newPath;
    }

    private async Task ClearHistoryAsync()
    {
        if (!await _dialog.ConfirmAsync(SL["Dialog_Confirm_Title"], SL["Dialog_ClearHistoryMessage"])) return;
        await _library.ClearHistoryAsync();
        await _dialog.ShowInfoAsync(SL["Dialog_Done_Title"], SL["Dialog_HistoryCleared"]);
    }

    private async Task ResetLibraryAsync()
    {
        if (!await _dialog.ConfirmAsync(SL["Dialog_Warning_Title"], SL["Dialog_ResetMessage"])) return;
        await _library.ResetAsync();
        LoadAllSettings();
        await _dialog.ShowInfoAsync(SL["Dialog_Done_Title"], SL["Dialog_ResetComplete"]);
    }

    private async Task ClearDownloadsAsync()
    {
        if (!await _dialog.ConfirmAsync(SL["Dialog_Confirm_Title"], SL["Settings_ClearDownloadsConfirm"])) return;

        await AudioCacheManager.ClearDownloadsAsync();

        foreach (var track in _registry.GetPinnedTracks().Where(t => t.IsDownloaded))
        {
            track.IsDownloaded = false;
            track.LocalPath = null;
        }

        UpdateCacheStats();
    }

    private async Task ShowNormalizationInfoAsync()
    {
        await _dialog.ShowInfoAsync(
            SL["Settings_NormalizationInfo_Title"],
            SL["Settings_NormalizationInfo_Body"],
            SL["Common_GotIt"]);
    }

    private async Task TestNetworkAsync()
    {
        if (IsNetworkTesting) return;

        IsNetworkTesting = true;
        NetworkStatus = NetworkStatusKind.Checking;
        NetworkStatusText = SL["Network_StatusChecking"];
        NetworkLatencyMs = 0;
        OnPropertyChanged(nameof(NetworkStatusColor));

        try
        {
            if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
            {
                SetNetworkStatus(NetworkStatusKind.NoInternet, SL["Network_StatusNoInternet"]);
                return;
            }

            bool vpnDetected = _networkManager.IsVpnActive || DetectVpnInterface();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool reachable = await ProbeYoutubeAsync().ConfigureAwait(false);
            sw.Stop();

            if (!reachable)
            {
                SetNetworkStatus(NetworkStatusKind.Error, SL["Network_StatusError"]);
                return;
            }

            bool cdnReachable = await ProbeCdnAsync().ConfigureAwait(false);

            NetworkLatencyMs = (int)sw.ElapsedMilliseconds;

            Log.Info($"[Settings] Network test: {NetworkLatencyMs}ms (proxy={ProxyEnabled}, vpn={vpnDetected}, cdnReachable={cdnReachable})");

            if (ProxyEnabled && !string.IsNullOrWhiteSpace(ProxyHost))
            {
                SetNetworkStatus(
                    NetworkStatusKind.ProxyActive,
                    string.Format(SL["Network_StatusProxy"], ProxyHost, ProxyPort));
            }
            else if (vpnDetected)
            {
                SetNetworkStatus(NetworkStatusKind.VpnDetected, SL["Network_StatusVpn"]);
            }
            else if (!cdnReachable)
            {
                SetNetworkStatus(
                    NetworkStatusKind.Error,
                    SL["Network_StatusCdnBlocked"]);
            }
            else
            {
                SetNetworkStatus(NetworkStatusKind.Ok, SL["Network_StatusOk"]);
            }
        }
        catch (Exception ex)
        {
            SetNetworkStatus(NetworkStatusKind.Error, ex.Message);
            Log.Warn($"[Settings] Error testing network: {ex.Message}");
        }
        finally
        {
            IsNetworkTesting = false;
        }
    }

    private async Task<bool> ProbeCdnAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var request = new HttpRequestMessage(
                HttpMethod.Get, "https://redirector.googlevideo.com/generate_204");

            request.Version = System.Net.HttpVersion.Version11;
            request.Headers.TryAddWithoutValidation("User-Agent",
                YoutubeClientUtils.UaWebRemix);

            using var response = await _networkManager.ProbeClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            return (int)response.StatusCode is >= 200 and <= 399;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> ProbeYoutubeAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var request = new HttpRequestMessage(
                HttpMethod.Get, "https://music.youtube.com/generate_204");

            request.Version = HttpVersion.Version11;
            request.Headers.TryAddWithoutValidation("User-Agent", YoutubeClientUtils.UaWebRemix);

            using var response = await _networkManager.ProbeClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            return (int)response.StatusCode is >= 200 and <= 399;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Проверяет наличие активного VPN-интерфейса среди сетевых адаптеров Windows.
    ///
    /// Изменения vs старой версии:
    /// - Тип Tunnel больше НЕ является достаточным признаком: Microsoft Teredo Tunneling
    ///   Adapter имеет тип Tunnel но VPN не является — давал false positive на чистом Wi-Fi.
    ///   Теперь Tunnel + фильтр по имени/описанию.
    /// - "tun" по substring заменён на точные паттерны с границами слова / позицией,
    ///   чтобы не ловить "fortune", "Saturn", "intuned" и т.п.
    /// - Исключаем Teredo, 6to4, Bluetooth PAN, Loopback, VMware/VirtualBox/Hyper-V
    ///   которые всегда присутствуют на Windows и к VPN не относятся.
    /// </summary>
    private static bool DetectVpnInterface()
    {
        try
        {
            foreach (var iface in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    continue;

                var name = iface.Name;
                var desc = iface.Description;
                var nameLow = name.ToLowerInvariant();
                var descLow = desc.ToLowerInvariant();

                if (iface.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    continue;

                if (descLow.Contains("radmin") ||
                    descLow.Contains("hamachi") ||
                    descLow.Contains("zerotier") ||
                    descLow.Contains("logmein") ||
                    nameLow.Contains("radmin"))
                {
#if DEBUG
                    if (!nameLow.Contains("--") && !nameLow.Contains("-wfp") &&
                        !nameLow.Contains("-qos") && !nameLow.Contains("-npcap"))
                    {
                        Log.Debug($"[Settings] Skipping LAN-only virtual adapter: {name}");
                    }
#endif
                    continue;
                }

                if (descLow.Contains("teredo") || descLow.Contains("6to4") || nameLow.Contains("teredo"))
                    continue;

                if (descLow.Contains("vmware") || descLow.Contains("virtualbox") ||
                    descLow.Contains("hyper-v") || descLow.Contains("hyperv"))
                    continue;

                if (descLow.Contains("bluetooth") || descLow.Contains("personal area network"))
                    continue;

                if (descLow.Contains("wi-fi direct") || descLow.Contains("microsoft hosted"))
                    continue;

                if (iface.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                {
                    Log.Debug($"[Settings] VPN detected by type=Tunnel: {name} / {desc}");
                    return true;
                }

                if (ContainsWord(nameLow, "vpn") || ContainsWord(descLow, "vpn"))
                {
                    Log.Debug($"[Settings] VPN detected by keyword 'vpn': {name} / {desc}");
                    return true;
                }

                if (ContainsWord(descLow, "tap") || nameLow.StartsWith("tap", StringComparison.Ordinal))
                {
                    Log.Debug($"[Settings] VPN detected by keyword 'tap': {name} / {desc}");
                    return true;
                }

                if (System.Text.RegularExpressions.Regex.IsMatch(nameLow, @"^u?tun\d*$"))
                {
                    Log.Debug($"[Settings] VPN detected by tun interface name: {name} / {desc}");
                    return true;
                }

                if (descLow.Contains("wireguard") || nameLow.Contains("wireguard"))
                {
                    Log.Debug($"[Settings] VPN detected by keyword 'wireguard': {name} / {desc}");
                    return true;
                }

                if (descLow.Contains("openvpn") || descLow.Contains("cisco") ||
                    descLow.Contains("nordvpn") || descLow.Contains("expressvpn") ||
                    descLow.Contains("xray") || descLow.Contains("sing-box") ||
                    descLow.Contains("shadowsocks"))
                {
                    Log.Debug($"[Settings] VPN detected by vendor keyword: {name} / {desc}");
                    return true;
                }

                if (descLow.Contains("wintun"))
                {
                    Log.Debug($"[Settings] VPN detected by 'wintun' driver: {name} / {desc}");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[Settings] VPN detection error: {ex.Message}");
        }

        return false;
    }

    private static bool ContainsWord(string haystack, string word)
    {
        int idx = 0;
        while ((idx = haystack.IndexOf(word, idx, StringComparison.Ordinal)) >= 0)
        {
            bool beforeOk = idx == 0 || !char.IsLetterOrDigit(haystack[idx - 1]);
            bool afterOk = idx + word.Length >= haystack.Length ||
                           !char.IsLetterOrDigit(haystack[idx + word.Length]);
            if (beforeOk || afterOk) return true;
            idx += word.Length;
        }
        return false;
    }

    private void SetNetworkStatus(NetworkStatusKind kind, string text)
    {
        NetworkStatus = kind;
        NetworkStatusText = text;
        OnPropertyChanged(nameof(NetworkStatusColor));
        OnPropertyChanged(nameof(HasLatency));
    }

    /// <inheritdoc />
    protected override void OnResume()
    {
        base.OnResume();
        LoadAllSettings();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;
        if (disposing)
        {
            _isDisposed = true;
            LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;

            var cache = AudioSourceFactory.GlobalCache;
            if (cache != null)
            {
                cache.OnFormatCached -= OnAudioFormatCached;
                cache.OnCacheCleared -= OnAudioCacheCleared;
            }

            _storageDebounceTimer?.Stop();
            _downloadsLimitDebounceTimer?.Stop();
            _memoryIntervalDebounceTimer?.Stop();
            _memoryPressureDebounceTimer?.Stop();
            _normLufsDebounceTimer?.Stop();
            _normGainDebounceTimer?.Stop();
            _suggestionsDebounceTimer?.Stop();
        }
        base.Dispose(disposing);
    }
}