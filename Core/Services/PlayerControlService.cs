namespace LMP.Core.Services;

/// <summary>
/// Единый координатор управления воспроизведением.
/// Предоставляет свойства и события, синхронизированные между всеми UI компонентами.
///
/// <para><b>Архитектура:</b></para>
/// <list type="bullet">
///   <item>Является единственным подписчиком на события AudioEngine для state tracking</item>
///   <item>Предоставляет свойства на ObservableObject и типизированные события C# для PlayerBar, TrayIcon, MediaKeys</item>
///   <item>Работает независимо от suspend/resume состояния окна</item>
///   <item>Является единственной точкой управления громкостью — UI не обращается к AudioEngine напрямую</item>
/// </list>
///
/// <para><b>ForceSync:</b></para>
/// <para>Не генерирует повторные уведомления для CurrentTrack если объект тот же (по Id).
/// Это предотвращает ложный TrackReset при восстановлении из трея.</para>
///
/// <para><b>Shuffle:</b></para>
/// <para>Все изменения ShuffleEnabled ДОЛЖНЫ идти через этот сервис (SetShuffleEnabled / ToggleAutoShuffle),
/// чтобы состояние всегда было синхронизировано с AudioEngine.</para>
///
/// <para><b>N-Token Warning:</b></para>
/// <para>Предупреждение о сложной расшифровке публикуется через <see cref="NTokenWarning"/>
/// и одновременно показывается через <see cref="NotificationService"/> (если доступен).</para>
/// </summary>
public sealed partial class PlayerControlService : ObservableObject, IDisposable
{
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly NotificationService? _notificationService;

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial TrackInfo? CurrentTrack { get; set; }

    [ObservableProperty]
    public partial RepeatMode RepeatMode { get; set; }

    [ObservableProperty]
    public partial bool ShuffleEnabled { get; set; }

    [ObservableProperty]
    public partial int QueueCount { get; set; }

    [ObservableProperty]
    public partial int CurrentVolume { get; set; }

    /// <summary>
    /// ID плейлиста, из которого была запущена текущая очередь.
    /// Null = источник не плейлист (Home, Search, одиночный трек).
    /// </summary>
    [ObservableProperty]
    public partial string? ActivePlaylistId { get; set; }

    public bool HasTrack => CurrentTrack != null;

    #region Events

    public event Action? ForceSyncTriggered;
    public event Action? ResumeRequested;

    public event Action<bool>? IsPlayingChanged;
    public event Action<bool>? IsPausedChanged;
    public event Action<bool>? IsLoadingChanged;
    public event Action<TrackInfo?>? CurrentTrackChanged;
    public event Action<RepeatMode>? RepeatModeChanged;
    public event Action<bool>? ShuffleEnabledChanged;
    public event Action<int>? QueueCountChanged;
    public event Action<int>? VolumeChanged;
    public event Action<string?>? ActivePlaylistIdChanged;

    #endregion

    private bool _disposed;

    #region Constructors

    public PlayerControlService(AudioEngine audio, LibraryService library)
        : this(audio, library, null)
    {
    }

    public PlayerControlService(AudioEngine audio, LibraryService library, NotificationService? notificationService)
    {
        _audio = audio;
        _library = library;
        _notificationService = notificationService;
        CurrentTrack = _audio.CurrentTrack;
        IsPlaying = _audio.IsPlaying;
        IsPaused = _audio.IsPaused;
        IsLoading = _audio.IsLoading;
        RepeatMode = _audio.RepeatMode;
        ShuffleEnabled = _audio.ShuffleEnabled;
        QueueCount = _audio.Queue.Count;
        CurrentVolume = (int)Math.Round(_audio.GetVolume());

        _audio.OnPlaybackStateChanged += HandlePlaybackStateChanged;
        _audio.OnTrackChanged += HandleTrackChanged;
        _audio.OnQueueChanged += HandleQueueChanged;
        _audio.OnLoadingStateChanged += HandleLoadingStateChanged;
        _audio.OnNTokenDecryptionWarning += HandleNTokenDecryptionWarning;

        // Если БД уже загружена — синхронизируем UI-состояние, если нет — ждем Initialized
        if (_library.IsInitialized)
        {
            _audio.InitializeVolumeFromSettings();
            ForceSync();
        }
        else
        {
            _library.OnInitialized += () =>
            {
                _audio.InitializeVolumeFromSettings();
                ForceSync();
            };
        }

        Log.Debug("[PlayerControl] Service initialized");
    }

    #endregion

    #region Commands

    public async Task PlayPauseAsync()
    {
        try
        {
            await _audio.SetPlaybackStateAsync(!_audio.IsPlaying);
        }
        catch (Exception ex)
        {
            Log.Error($"[PlayerControl] PlayPause error: {ex.Message}");
        }
    }

    public async Task NextAsync()
    {
        try
        {
            await _audio.PlayNextAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"[PlayerControl] Next error: {ex.Message}");
        }
    }

    public async Task PreviousAsync()
    {
        try
        {
            await _audio.PlayPreviousAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"[PlayerControl] Previous error: {ex.Message}");
        }
    }

    public void ToggleRepeat()
    {
        var newMode = _audio.RepeatMode switch
        {
            RepeatMode.None => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            RepeatMode.One => RepeatMode.None,
            _ => RepeatMode.None
        };

        _audio.RepeatMode = newMode;
        _library.UpdateSettings(s => s.RepeatMode = newMode);
        RepeatMode = newMode;
        RepeatModeChanged?.Invoke(newMode);

        Log.Debug($"[PlayerControl] RepeatMode changed to {newMode}");
    }

    public void ShuffleQueue()
    {
        _audio.ShuffleQueue();
        Log.Debug("[PlayerControl] Queue shuffled");
    }

    /// <summary>
    /// Переключает авто-перемешивание (toggle).
    /// Синхронизирует AudioEngine, сохраняет в настройки, обновляет состояние.
    /// </summary>
    public void ToggleAutoShuffle()
    {
        bool newState = !_audio.ShuffleEnabled;
        _audio.ShuffleEnabled = newState;

        // При включении shuffle — немедленно перемешать текущую очередь.
        // Пользователь сразу видит случайный порядок в QueueView.
        // При выключении — порядок остаётся как есть (уже перемешан).
        if (newState)
            _audio.ShuffleQueue();

        _library.UpdateSettings(s => s.ShuffleEnabled = newState);
        ShuffleEnabled = newState;
        ShuffleEnabledChanged?.Invoke(newState);

        Log.Debug($"[PlayerControl] AutoShuffle changed to {newState}");
    }

    /// <summary>
    /// Устанавливает состояние авто-перемешивания напрямую.
    /// Используется из PlaylistViewModel и других мест, которые хотят
    /// явно установить shuffle = false перед стартом очереди.
    ///
    /// <para><b>ВАЖНО:</b> Все изменения ShuffleEnabled должны идти через этот метод
    /// или ToggleAutoShuffle(), чтобы свойство оставалось синхронизированным.</para>
    /// </summary>
    /// <param name="enabled">Новое состояние авто-перемешивания.</param>
    public void SetShuffleEnabled(bool enabled)
    {
        if (_audio.ShuffleEnabled == enabled)
            return;

        _audio.ShuffleEnabled = enabled;
        _library.UpdateSettings(s => s.ShuffleEnabled = enabled);
        ShuffleEnabled = enabled;
        ShuffleEnabledChanged?.Invoke(enabled);

        Log.Debug($"[PlayerControl] ShuffleEnabled set to {enabled}");
    }

    /// <summary>
    /// Устанавливает ID плейлиста-источника текущей очереди.
    /// Вызывается из PlaylistViewModel перед StartQueueAsync.
    /// Null = очередь запущена не из плейлиста.
    /// </summary>
    public void SetActivePlaylistId(string? playlistId)
    {
        if (ActivePlaylistId == playlistId) return;
        ActivePlaylistId = playlistId;
        ActivePlaylistIdChanged?.Invoke(playlistId);
        Log.Debug($"[PlayerControl] ActivePlaylistId = {playlistId ?? "null"}");
    }

    /// <summary>
    /// Изменяет громкость на указанный шаг (положительный или отрицательный).
    /// Используется для колеса мыши в трее и горячих клавиш.
    /// </summary>
    /// <param name="delta">Величина изменения громкости.</param>
    /// <returns>Новое значение громкости.</returns>
    public int AdjustVolume(int delta)
    {
        int currentVolume = (int)Math.Round(_audio.GetVolume());
        int maxVolume = _library.Settings.MaxVolumeLimit;
        if (maxVolume <= 0) maxVolume = 100;

        int newVolume = Math.Clamp(currentVolume + delta, 0, maxVolume);
        SetVolume(newVolume);
        return newVolume;
    }

    /// <summary>
    /// Устанавливает громкость воспроизведения, применяет её к аудио-пайплайну и планирует фоновое сохранение.
    /// Является единственной точкой входа для изменения громкости во всём приложении.
    /// </summary>
    /// <param name="volume">Новое значение громкости (0–MaxVolume).</param>
    public void SetVolume(int volume)
    {
        int maxVolume = _library.Settings.MaxVolumeLimit;
        if (maxVolume <= 0) maxVolume = 100;

        int clamped = Math.Clamp(volume, 0, maxVolume);
        int current = (int)Math.Round(_audio.GetVolume());

        if (clamped != current)
        {
            _audio.SetVolumeInstant(clamped);
            CurrentVolume = clamped;
            VolumeChanged?.Invoke(clamped);
        }

        // Обновляем настройки; дебаунсер в LibraryService сам запишет их на диск без фризов UI
        _library.UpdateSettings(s => s.Volume = clamped);
    }

    /// <summary>
    /// Возвращает текущую громкость из AudioEngine (округлённую до int).
    /// </summary>
    public int GetCurrentVolume() => (int)Math.Round(_audio.GetVolume());

    /// <summary>
    /// Возвращает максимальную громкость из настроек.
    /// </summary>
    public int GetMaxVolume()
    {
        int max = _library.Settings.MaxVolumeLimit;
        return max > 0 ? max : 100;
    }

    /// <summary>
    /// Запрашивает Resume у MainWindow (через OnResumeRequested).
    /// Вызывается когда пользователь взаимодействует с UI в suspend-режиме.
    /// </summary>
    public void RequestResume()
    {
        ResumeRequested?.Invoke();
    }

    #endregion

    #region AudioEngine Event Handlers

    private void HandlePlaybackStateChanged(bool isPlaying, bool isPaused)
    {
        IsPlaying = isPlaying;
        IsPaused = isPaused;
        IsPlayingChanged?.Invoke(isPlaying);
        IsPausedChanged?.Invoke(isPaused);
    }

    /// <summary>
    /// Обрабатывает смену трека из AudioEngine.
    /// Не переиздаёт событие если Id трека не изменился,
    /// чтобы предотвратить ложные TrackReset при восстановлении из трея.
    /// </summary>
    private void HandleTrackChanged(TrackInfo? track)
    {
        var previous = CurrentTrack;
        CurrentTrack = track;

        if (previous?.Id == track?.Id)
            return;

        CurrentTrack = track;
        CurrentTrackChanged?.Invoke(track);

        // Сбрасываем источник только при реальной остановке (track → null).
        // При переходе между треками (prev != null → new != null) источник сохраняется —
        // это нормально: пользователь слушает тот же плейлист.
        if (track == null && ActivePlaylistId != null)
        {
            ActivePlaylistId = null;
            ActivePlaylistIdChanged?.Invoke(null);
            Log.Debug("[PlayerControl] ActivePlaylistId cleared (track → null)");
        }
    }

    private void HandleQueueChanged()
    {
        QueueCount = _audio.Queue.Count;
        QueueCountChanged?.Invoke(QueueCount);
    }

    private void HandleLoadingStateChanged(bool isLoading)
    {
        IsLoading = isLoading;
        IsLoadingChanged?.Invoke(isLoading);
    }

    /// <summary>
    /// Публикует предупреждение о сложной расшифровке n-токена
    /// и, если это разрешено настройками, либо добавляет его в центр уведомлений,
    /// либо показывает toast.
    /// </summary>
    private void HandleNTokenDecryptionWarning(AudioEngine.NTokenWarningInfo warning)
    {
        if (_notificationService == null)
            return;

        var mode = _library.Settings.Audio.NTokenNotificationMode;
        switch (mode)
        {
            case NTokenNotificationMode.Disabled:
                Log.Debug($"[PlayerControl] N-Token warning suppressed for track '{warning.Track?.Id}' as configured.");
                return;

            case NTokenNotificationMode.PanelOnly:
                _ = PublishNTokenWarningAsync(_notificationService, warning, showToast: false);
                return;

            default:
                _ = PublishNTokenWarningAsync(_notificationService, warning, showToast: true);
                return;
        }
    }

    /// <summary>
    /// Публикует уведомление о сложной расшифровке n-токена
    /// либо как toast, либо только в центр уведомлений.
    /// </summary>
    private static async Task PublishNTokenWarningAsync(
        NotificationService notificationService,
        AudioEngine.NTokenWarningInfo warning,
        bool showToast)
    {
        try
        {
            var track = warning.Track;
            string trackDisplay = track?.Title ?? track?.Id ?? "Unknown";
            string? trackTitle = track?.Title ?? track?.Id;
            string messageKey = warning.WasSkipped
                ? "Notification_NToken_Skipped"
                : "Notification_NToken_Message";

            if (showToast)
            {
                await notificationService.ShowToastAsync(
                    titleKey: "Notification_NToken_Title",
                    messageKey: messageKey,
                    severity: NotificationSeverity.Warning,
                    messageArgs: [trackDisplay],
                    trackId: track?.Id,
                    trackTitle: trackTitle);
            }
            else
            {
                await notificationService.AddToPanelAsync(
                    titleKey: "Notification_NToken_Title",
                    messageKey: messageKey,
                    severity: NotificationSeverity.Warning,
                    messageArgs: [trackDisplay],
                    trackId: track?.Id,
                    trackTitle: trackTitle);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[PlayerControl] Failed to publish n-token warning: {ex.Message}");
        }
    }

    #endregion

    #region Sync

    /// <summary>
    /// Принудительная синхронизация всех состояний при восстановлении из трея.
    ///
    /// <para><b>ВАЖНО:</b> НЕ переиздаёт CurrentTrack если трек тот же самый (по Id).
    /// Это предотвращает ложный BeginTrackReset → замораживание UI.</para>
    ///
    /// <para>Вместо этого вызывает ForceSync, на который PlayerBarViewModel
    /// подписывается для мягкого обновления (позиция, буфер, стрим-инфо).</para>
    ///
    /// <para>Если реальный трек в AudioEngine отличается от кэшированного (по Id),
    /// публикует новый трек через CurrentTrack.</para>
    /// </summary>
    public void ForceSync()
    {
        IsPlaying = _audio.IsPlaying;
        IsPaused = _audio.IsPaused;
        IsLoading = _audio.IsLoading;
        RepeatMode = _audio.RepeatMode;
        ShuffleEnabled = _audio.ShuffleEnabled;
        QueueCount = _audio.Queue.Count;
        CurrentVolume = (int)Math.Round(_audio.GetVolume());

        var actualTrack = _audio.CurrentTrack;
        if (CurrentTrack?.Id != actualTrack?.Id)
        {
            CurrentTrack = actualTrack;
            CurrentTrackChanged?.Invoke(actualTrack);
        }
        else
        {
            // Обновляем ссылку без переиздания события
            CurrentTrack = actualTrack;
        }

        ForceSyncTriggered?.Invoke();

        Log.Debug("[PlayerControl] Forced sync completed (soft, no track reset)");
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _audio.OnPlaybackStateChanged -= HandlePlaybackStateChanged;
        _audio.OnTrackChanged -= HandleTrackChanged;
        _audio.OnQueueChanged -= HandleQueueChanged;
        _audio.OnLoadingStateChanged -= HandleLoadingStateChanged;
        _audio.OnNTokenDecryptionWarning -= HandleNTokenDecryptionWarning;

        Log.Debug("[PlayerControl] Service disposed");
    }

    #endregion
}