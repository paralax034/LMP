using System.Collections.ObjectModel;
using Avalonia.Threading;
using LMP.Core.Data.Repositories;

namespace LMP.Core.Services;

/// <summary>
/// Сервис уведомлений: история, toast-очередь, авто-очистка.
/// <para>
/// Счётчик непрочитанных — O(1) через <see cref="_unreadCount"/>,
/// авто-очистка — <see cref="PeriodicTimer"/> в фоновом потоке.
/// </para>
/// </summary>
public sealed partial class NotificationService : ObservableObject, IDisposable
{
    private readonly LibraryService _libraryService;
    private readonly INotificationRepository _repository;

    private const int DefaultToastDuration = 4000;

    // O(1) счётчик, обновляется при каждой мутации коллекции
    private int _unreadCount;

    public ObservableCollection<Notification> Notifications { get; } = [];

    /// <inheritdoc cref="_unreadCount"/>
    public int UnreadCount => _unreadCount;
    public bool HasUnread => _unreadCount > 0;

    private Notification? _currentToast;
    public Notification? CurrentToast
    {
        get => _currentToast;
        private set => SetProperty(ref _currentToast, value);
    }

    public bool IsToastVisible => CurrentToast != null;

    private CancellationTokenSource? _toastCts;
    private CancellationTokenSource? _cleanupCts;
    private bool _isInitialized;

    /// <summary>
    /// Максимум уведомлений в памяти. Ограничен 100 для сохранения максимальной отзывчивости интерфейса.
    /// </summary>
    private int MaxNotifications => Math.Clamp(_libraryService.Settings.Notifications.MaxInPanelCount, 10, 100);

    public NotificationService(LibraryService libraryService, INotificationRepository repository)
    {
        _libraryService = libraryService;
        _repository = repository;
        Log.Info("[NotificationService] Initialized");
    }

    #region Initialization

    /// <summary>
    /// Загружает историю из БД и запускает фоновую авто-очистку.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_isInitialized) return;

        try
        {
            var loadedNotifications = await _repository.GetRecentAsync(MaxNotifications, ct);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                for (int i = 0; i < loadedNotifications.Count; i++)
                {
                    var n = loadedNotifications[i];
                    Notifications.Add(n);
                    if (!n.IsRead) _unreadCount++;
                }

                RaiseUnreadProperties();
            });

            _isInitialized = true;
            Log.Info($"[NotificationService] Loaded {loadedNotifications.Count} notifications from DB");

            StartAutoCleanup();
        }
        catch (Exception ex)
        {
            Log.Error($"[NotificationService] Failed to load history: {ex.Message}");
            _isInitialized = true;
        }
    }

    #endregion

    #region Public API

    /// <summary>
    /// Показывает toast-уведомление и добавляет его в историю.
    /// </summary>
    public async Task ShowToastAsync(
        string titleKey,
        string messageKey,
        NotificationSeverity severity = NotificationSeverity.Info,
        int durationMs = 0,
        object[]? messageArgs = null,
        CancellationToken ct = default,
        string? trackId = null,
        string? trackTitle = null,
        string? exceptionDetails = null,
        string? recommendationKey = null)
    {
        if (durationMs <= 0) durationMs = DefaultToastDuration;

        var notification = new Notification
        {
            TitleKey = titleKey,
            MessageKey = messageKey,
            MessageArgs = messageArgs,
            Severity = severity,
            TrackId = trackId,
            TrackTitle = trackTitle,
            ExceptionDetails = exceptionDetails,
            RecommendationKey = recommendationKey
        };

        await PublishAsync(notification, showToast: true, durationMs, ct);
    }

    internal async Task AddToPanelAsync(
      string titleKey,
      string messageKey,
      NotificationSeverity severity = NotificationSeverity.Info,
      object[]? messageArgs = null,
      CancellationToken ct = default,
      string? trackId = null,
      string? trackTitle = null,
      string? exceptionDetails = null,
      string? recommendationKey = null)
    {
        var notification = new Notification
        {
            TitleKey = titleKey,
            MessageKey = messageKey,
            MessageArgs = messageArgs,
            Severity = severity,
            TrackId = trackId,
            TrackTitle = trackTitle,
            ExceptionDetails = exceptionDetails,
            RecommendationKey = recommendationKey
        };

        await PublishAsync(notification, showToast: false, 0, ct);
    }

    /// <summary>
    /// Показывает детализированный toast об ошибке воспроизведения с попытками.
    /// </summary>
    public async Task ShowPlaybackErrorAsync(
        string titleKey,
        string messageKey,
        string? trackId,
        string? trackTitle,
        IEnumerable<AttemptRecord>? attempts,
        string? exceptionDetails,
        NotificationSeverity severity = NotificationSeverity.Error,
        int durationMs = 0,
        string? recommendationKey = null,
        object[]? messageArgs = null,
        CancellationToken ct = default)
    {
        if (durationMs <= 0) durationMs = DefaultToastDuration;

        var notification = new Notification
        {
            TitleKey = titleKey,
            MessageKey = messageKey,
            MessageArgs = messageArgs,
            Severity = severity,
            TrackId = trackId,
            TrackTitle = trackTitle,
            Attempts = attempts != null ? new ObservableCollection<AttemptRecord>(attempts) : null,
            ExceptionDetails = exceptionDetails,
            RecommendationKey = recommendationKey
        };

        await PublishAsync(notification, showToast: true, durationMs, ct);
    }

    public static async Task ShowOsNotificationAsync(
        string title,
        string message,
        NotificationSeverity severity = NotificationSeverity.Info)
    {
        await OsNotificationHelper.ShowAsync(title, message, severity);
    }

    public void TryPlayErrorSound()
    {
        if (_libraryService.Settings.Audio.PlayErrorSound)
            OsSoundPlayer.PlayError();
    }

    public static void PlaySuccessSound() => OsSoundPlayer.PlaySuccess();

    private async Task PublishAsync(Notification notification, bool showToast, int durationMs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        await AddToHistoryAsync(notification);
        await PersistAsync(notification);

        if (!showToast)
            return;

        ct.ThrowIfCancellationRequested();
        await ShowToastInternalAsync(notification, durationMs, ct);
    }

    #endregion

    #region Auto-Cleanup

    /// <summary>
    /// Запускает фоновый таймер авто-очистки уведомлений.
    /// Работает в <see cref="ThreadPool"/>, не блокирует UI.
    /// </summary>
    private void StartAutoCleanup()
    {
        var settings = _libraryService.Settings.Notifications;
        if (!settings.AutoCleanupEnabled) return;

        _cleanupCts = new CancellationTokenSource();
        var token = _cleanupCts.Token;

        _ = Task.Run(() => RunCleanupLoopAsync(settings, token), token);
    }

    private async Task RunCleanupLoopAsync(NotificationSettings settings, CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(settings.CleanupCheckIntervalMinutes);
        using var timer = new PeriodicTimer(interval);

        try
        {
            // Первый прогон — сразу при старте
            await CleanupOldNotificationsAsync(settings, ct);

            while (await timer.WaitForNextTickAsync(ct))
                await CleanupOldNotificationsAsync(settings, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"[NotificationService] Cleanup loop error: {ex.Message}");
        }
    }

    /// <summary>
    /// Удаляет уведомления старше <see cref="NotificationSettings.AutoCleanupAfterHours"/> часов.
    /// UI-операции выполняются через <see cref="Dispatcher.UIThread"/>.
    /// </summary>
    private async Task CleanupOldNotificationsAsync(NotificationSettings settings, CancellationToken ct)
    {
        var threshold = DateTime.UtcNow - TimeSpan.FromHours(settings.AutoCleanupAfterHours);

        // Собираем кандидатов вне UI-потока
        List<Notification>? toRemove = null;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            toRemove = [.. Notifications.Where(n => n.Timestamp < threshold)];
        });

        if (toRemove is not { Count: > 0 }) return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var n in toRemove)
            {
                if (Notifications.Remove(n) && !n.IsRead)
                    _unreadCount = Math.Max(0, _unreadCount - 1);
            }

            RaiseUnreadProperties();
        });

        try
        {
            await _repository.DeleteOlderThanAsync(threshold, ct);
        }
        catch (Exception ex)
        {
            Log.Warn($"[NotificationService] Cleanup DB error: {ex.Message}");
        }

        Log.Info($"[NotificationService] Auto-cleanup removed {toRemove.Count} old notifications");
    }

    #endregion

    #region Persistence

    private async Task PersistAsync(Notification notification)
    {
        try
        {
            await _repository.AddAsync(notification);
            await _repository.PruneAsync(MaxNotifications * 2);
        }
        catch (Exception ex)
        {
            Log.Warn($"[NotificationService] Failed to persist: {ex.Message}");
        }
    }

    public sealed record AttemptDto(string ClientName, bool Success, string? ErrorMessage, DateTime Timestamp);

    #endregion

    #region Internal Methods

    private async Task AddToHistoryAsync(Notification notification)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Notifications.Insert(0, notification);

            // Поддерживаем O(1) счётчик
            if (!notification.IsRead) _unreadCount++;

            // Вытесняем самые старые, поддерживая счётчик
            while (Notifications.Count > MaxNotifications)
            {
                var last = Notifications[^1];
                if (!last.IsRead) _unreadCount = Math.Max(0, _unreadCount - 1);
                Notifications.RemoveAt(Notifications.Count - 1);
            }

            RaiseUnreadProperties();
        });
    }

    private async Task ShowToastInternalAsync(Notification notification, int durationMs, CancellationToken ct)
    {
        _toastCts?.Cancel();
        _toastCts?.Dispose();
        _toastCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var localToken = _toastCts.Token;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            CurrentToast = notification;
            OnPropertyChanged(nameof(IsToastVisible));
        });

        _ = AutoDismissToastAsync(notification, durationMs, localToken);
    }

    private async Task AutoDismissToastAsync(Notification notification, int durationMs, CancellationToken ct)
    {
        try
        {
            await Task.Delay(durationMs, ct);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (CurrentToast == notification)
                {
                    CurrentToast = null;
                    OnPropertyChanged(nameof(IsToastVisible));
                }
            });
        }
        catch (OperationCanceledException) { }
    }

    #endregion

    #region UI Commands

    /// <summary>
    /// Отмечает все уведомления как прочитанные и сохраняет в БД.
    /// </summary>
    public void MarkAllAsRead()
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var notification in Notifications)
                notification.IsRead = true;

            _unreadCount = 0;
            RaiseUnreadProperties();
        });

        _ = Task.Run(async () =>
        {
            try { await _repository.MarkAllAsReadAsync(); }
            catch (Exception ex) { Log.Warn($"[NotificationService] MarkAllAsRead DB error: {ex.Message}"); }
        });
    }

    /// <summary>
    /// Удаляет все уведомления из памяти и БД.
    /// </summary>
    public void ClearAll()
    {
        Dispatcher.UIThread.Post(() =>
        {
            Notifications.Clear();
            _unreadCount = 0;
            RaiseUnreadProperties();
        });

        _ = Task.Run(async () =>
        {
            try { await _repository.ClearAllAsync(); }
            catch (Exception ex) { Log.Warn($"[NotificationService] ClearAll DB error: {ex.Message}"); }
        });
    }

    public void Remove(Notification notification)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Notifications.Remove(notification))
            {
                if (!notification.IsRead) _unreadCount = Math.Max(0, _unreadCount - 1);
                RaiseUnreadProperties();
            }
        });
    }

    public void DismissToast()
    {
        _toastCts?.Cancel();
        Dispatcher.UIThread.Post(() =>
        {
            CurrentToast = null;
            OnPropertyChanged(nameof(IsToastVisible));
        });
    }

    /// <summary>
    /// Поднимает PropertyChanged для счётчика непрочитанных.
    /// Вызывается только после мутаций — не в горячем пути биндинга.
    /// </summary>
    private void RaiseUnreadProperties()
    {
        OnPropertyChanged(nameof(UnreadCount));
        OnPropertyChanged(nameof(HasUnread));
    }

    /// <summary>
    /// Генерирует набор тестовых уведомлений для проверки производительности UI-панели.
    /// </summary>
    /// <param name="count">Количество создаваемых уведомлений.</param>
    /// <param name="clearExisting">Флаг предварительной очистки существующей истории.</param>
    public async Task SeedDebugNotificationsAsync(int count = 100, bool clearExisting = true)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            PopulateDebugNotifications(count, clearExisting);
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() => PopulateDebugNotifications(count, clearExisting));
    }

    private void PopulateDebugNotifications(int count, bool clearExisting)
    {
        if (clearExisting)
        {
            Notifications.Clear();
            _unreadCount = 0;
        }

        bool isRu = LocalizationService.Instance.CurrentLanguageCode == "ru";

        var severities = new[]
        {
            NotificationSeverity.Info,
            NotificationSeverity.Success,
            NotificationSeverity.Warning,
            NotificationSeverity.Error
        };

        for (int i = 0; i < count; i++)
        {
            var severity = severities[i % severities.Length];
            bool isError = severity == NotificationSeverity.Error;
            bool isWarning = severity == NotificationSeverity.Warning;

            string severityName = severity switch
            {
                NotificationSeverity.Info => isRu ? "Инфо" : "Info",
                NotificationSeverity.Success => isRu ? "Успешно" : "Success",
                NotificationSeverity.Warning => isRu ? "Предупреждение" : "Warning",
                NotificationSeverity.Error => isRu ? "Ошибка" : "Error",
                _ => isRu ? "Заметка" : "Notice"
            };

            string title = isRu
                ? $"Тестовое уведомление #{i + 1} ({severityName})"
                : $"Test Notification #{i + 1} ({severityName})";

            string message = severity switch
            {
                NotificationSeverity.Error => isRu
                    ? $"Сбой потока воспроизведения при расшифровке подписи для потока #{i + 1}."
                    : $"Sample playback stream failure occurred while resolving cipher for stream #{i + 1}.",
                NotificationSeverity.Warning => isRu
                    ? $"Обнаружена повышенная задержка при загрузке аудио-сегмента #{i + 1}."
                    : $"High latency detected while downloading audio segment #{i + 1}.",
                NotificationSeverity.Success => isRu
                    ? $"Синхронизация треков успешно завершена для пакета #{i + 1}."
                    : $"Track synchronization completed successfully for batch #{i + 1}.",
                _ => isRu
                    ? $"Диагностическое событие телеметрии получено для пакета #{i + 1}."
                    : $"Diagnostic test event dispatched for telemetry chunk #{i + 1}."
            };

            string? recommendation = isWarning
                ? (isRu ? "Проверьте настройки прокси или сетевое подключение." : "Check your proxy or network connection.")
                : null;

            var notification = new Notification
            {
                TitleRaw = title,
                MessageRaw = message,
                Severity = severity,
                IsRead = i >= 5,
                Timestamp = DateTime.UtcNow.AddMinutes(-i * 3),
                TrackId = (isError || isWarning) ? "dQw4w9WgXcQ" : null,
                TrackTitle = (isError || isWarning) ? $"Rick Astley - Never Gonna Give You Up (Track #{i + 1})" : null,
                RecommendationRaw = recommendation,
                ExceptionDetails = isError
                    ? $"LMP.Core.Exceptions.StreamUnavailableException: Stream not found for itag 251\n" +
                      $"   at LMP.Core.Youtube.Streams.StreamClient.GetAsync(VideoId id) in StreamClient.cs:line 142\n" +
                      $"   at LMP.Core.Audio.AudioEngine.ResolveAsync(TrackInfo track) in AudioEngine.cs:line 320"
                    : null,
                Attempts = isError
                    ? new ObservableCollection<AttemptRecord>
                    {
                        new("ANDROID", false, "HTTP 403 Forbidden", DateTime.UtcNow.AddSeconds(-30)),
                        new("WEB_REMIX", false, isRu ? "Сбой расшифровки N-Token" : "N-Token decryption failed", DateTime.UtcNow.AddSeconds(-20)),
                        new("TVHTML5", true, null, DateTime.UtcNow.AddSeconds(-10))
                    }
                    : null
            };

            Notifications.Add(notification);
            if (!notification.IsRead)
                _unreadCount++;
        }

        RaiseUnreadProperties();
        Log.Info($"[NotificationService] Seeded {count} debug notifications (Unread: {_unreadCount})");
    }

    #endregion

    #region Dispose

    public void Dispose()
    {
        _cleanupCts?.Cancel();
        _cleanupCts?.Dispose();
        _toastCts?.Cancel();
        _toastCts?.Dispose();
        Notifications.Clear();
        CurrentToast = null;
        Log.Info("[NotificationService] Disposed");
    }

    #endregion
}