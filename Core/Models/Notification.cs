using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;

namespace LMP.Core.Models;

/// <summary>
/// Уведомление или живая фоновая задача в системе.
/// Поддерживает реактивное обновление прогресса, статуса и кнопки действия в реальном времени,
/// а также автоматический перевод при смене языка.
/// </summary>
public sealed partial class Notification : ObservableObject
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Уникальный ключ фоновой задачи (например, <c>outbox:pl_123</c> или <c>import:yt</c>).
    /// Для обычных уведомлений равен <see langword="null"/>.
    /// </summary>
    public string? TaskKey { get; init; }

    /// <summary>
    /// Указывает, что элемент представляет фоновую задачу облачной очереди или синхронизации.
    /// </summary>
    public bool IsTask { get; init; }

    /// <summary>
    /// Указывает, что фоновая задача в данный момент ожидает отправки или выполняется.
    /// При завершении переходит в <see langword="false"/>, превращаясь в обычную запись хроники.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTaskProgress))]
    [NotifyPropertyChangedFor(nameof(ShowTaskFooter))]
    [NotifyPropertyChangedFor(nameof(HasTaskAction))]
    [NotifyPropertyChangedFor(nameof(HasTaskStatus))]
    [NotifyPropertyChangedFor(nameof(TimeAgo))]
    public partial bool IsTaskActive { get; set; }

    /// <summary>
    /// Момент запланированной отправки задачи (UTC). Пока задан и нет <see cref="TaskStatusText"/>,
    /// в строке времени отображается живой обратный отсчёт.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTaskStatus))]
    [NotifyPropertyChangedFor(nameof(HasTaskCountdown))]
    [NotifyPropertyChangedFor(nameof(TimeAgo))]
    public partial DateTime? TaskDeadlineUtc { get; set; }

    /// <summary>
    /// Скрывает полосу прогресса задачи (ожидание отправки отображается таймером, а не анимацией).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTaskProgress))]
    [NotifyPropertyChangedFor(nameof(ShowTaskFooter))]
    public partial bool IsTaskProgressHidden { get; set; }

    /// <summary>
    /// Прогресс выполнения или ожидания задачи (0.0 .. 1.0).
    /// </summary>
    [ObservableProperty] public partial double TaskProgress { get; set; }

    /// <summary>
    /// Флаг неопределенного прогресса (пульсация при отправке в сеть).
    /// </summary>
    [ObservableProperty] public partial bool IsTaskIndeterminate { get; set; }

    /// <summary>
    /// Дополнительный статус задачи (например, таймер до отправки или состояние ретрая).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTaskStatus))]
    [NotifyPropertyChangedFor(nameof(HasTaskCountdown))]
    [NotifyPropertyChangedFor(nameof(TimeAgo))]
    public partial string? TaskStatusText { get; set; }

    /// <summary>
    /// Ключ локализации кнопки быстрого действия (например, <c>CloudTask_ForceSend</c> или <c>Button_Cancel</c>).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskActionText))]
    [NotifyPropertyChangedFor(nameof(HasTaskAction))]
    [NotifyPropertyChangedFor(nameof(ShowTaskFooter))]
    public partial string? TaskActionKey { get; set; }

    /// <summary>
    /// Указывает, является ли действие кнопкой отмены (отображает иконку крестика вместо облака).
    /// </summary>
    [ObservableProperty] public partial bool IsCancelAction { get; set; }

    /// <summary>
    /// Динамический генератор локализованного текста сообщения для составных задач очереди.
    /// Перевычисляется автоматически при смене языка интерфейса.
    /// </summary>
    public Func<string>? DynamicMessageFactory { get; set; }

    /// <summary>
    /// Локализованный текст кнопки быстрого действия («Отправить сейчас» или «Отмена»).
    /// </summary>
    public string? TaskActionText => !string.IsNullOrEmpty(TaskActionKey)
        ? L[TaskActionKey]
        : TaskActionRaw;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskActionText))]
    [NotifyPropertyChangedFor(nameof(HasTaskAction))]
    [NotifyPropertyChangedFor(nameof(ShowTaskFooter))]
    public partial string? TaskActionRaw { get; set; }

    /// <summary>
    /// Команда быстрого действия для активной задачи.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTaskAction))]
    [NotifyPropertyChangedFor(nameof(ShowTaskFooter))]
    public partial ICommand? TaskActionCommand { get; set; }

    /// <summary>
    /// Ключ локализации заголовка (например "Error_Playback_Title").
    /// Если null — используется <see cref="TitleRaw"/>.
    /// </summary>
    public string? TitleKey { get; set; }

    /// <summary>
    /// Готовый текст заголовка (fallback если TitleKey не задан).
    /// </summary>
    public string? TitleRaw { get; set; }

    /// <summary>
    /// Ключ локализации сообщения.
    /// </summary>
    public string? MessageKey { get; set; }

    /// <summary>
    /// Готовый текст сообщения (fallback).
    /// </summary>
    public string? MessageRaw { get; set; }

    /// <summary>
    /// Аргументы для форматирования сообщения (string.Format).
    /// </summary>
    public object[]? MessageArgs { get; set; }

    /// <summary>
    /// Ключ локализации рекомендации.
    /// </summary>
    public string? RecommendationKey { get; init; }

    /// <summary>
    /// Готовый текст рекомендации (fallback).
    /// </summary>
    public string? RecommendationRaw { get; init; }

    public NotificationSeverity Severity { get; set; }
    public bool IsRead { get; set; }
    public ObservableCollection<AttemptRecord>? Attempts { get; init; }
    public string? TrackId { get; init; }
    public string? TrackTitle { get; init; }
    public string? ExceptionDetails { get; init; }

    #region Computed Localized Properties

    /// <summary>
    /// Экземпляр сервиса локализации для прямой компилируемой привязки в шаблонах.
    /// </summary>
    public LocalizationService L => LocalizationService.Instance;

    /// <summary>
    /// Локализованный заголовок (автоматически переводится при смене языка).
    /// </summary>
    public string Title => !string.IsNullOrEmpty(TitleKey)
        ? L[TitleKey]
        : TitleRaw ?? "";

    public string TrackUrl => !string.IsNullOrEmpty(TrackId) ? $"https://www.youtube.com/watch?v={TrackId}" : "";

    /// <summary>
    /// Локализованное сообщение.
    /// </summary>
    public string Message
    {
        get
        {
            if (DynamicMessageFactory != null)
            {
                try { return DynamicMessageFactory(); }
                catch { }
            }

            if (string.IsNullOrEmpty(MessageKey))
                return MessageRaw ?? "";

            var template = L[MessageKey];

            if (MessageArgs is { Length: > 0 })
            {
                try
                {
                    return string.Format(template, MessageArgs);
                }
                catch
                {
                    return template;
                }
            }

            return template;
        }
    }

    /// <summary>
    /// Локализованная рекомендация.
    /// </summary>
    public string? Recommendation => !string.IsNullOrEmpty(RecommendationKey)
        ? L[RecommendationKey]
        : RecommendationRaw;

    #endregion

    #region UI Helpers

    public bool ShowTaskProgress => IsTaskActive && !IsTaskProgressHidden;
    public bool HasTaskAction => IsTaskActive && TaskActionCommand != null && !string.IsNullOrEmpty(TaskActionText);
    public bool HasTaskStatus => IsTaskActive && (!string.IsNullOrEmpty(TaskStatusText) || TaskDeadlineUtc.HasValue);

    /// <summary>
    /// Нижняя панель карточки задачи (прогресс и/или кнопка действия) нужна, только если в ней есть что показать.
    /// </summary>
    public bool ShowTaskFooter => IsTaskActive && (ShowTaskProgress || HasTaskAction);

    /// <summary>
    /// Активна ли живая строка обратного отсчёта (требует периодического <see cref="RefreshCountdown"/>).
    /// </summary>
    public bool HasTaskCountdown => IsTaskActive && TaskDeadlineUtc.HasValue && string.IsNullOrEmpty(TaskStatusText);

    public bool HasExceptionDetails => !string.IsNullOrWhiteSpace(ExceptionDetails);
    public bool HasDetails => Attempts?.Count > 0 || HasExceptionDetails;
    public bool HasRecommendation => !string.IsNullOrEmpty(Recommendation);

    public string TimeAgo
    {
        get
        {
            if (IsTaskActive && !string.IsNullOrEmpty(TaskStatusText))
                return TaskStatusText;

            if (IsTaskActive && TaskDeadlineUtc is { } deadlineUtc)
            {
                // Срок вышел, а воркер ещё не забрал задачу (нет сети, авторизации или идёт кулдаун):
                // вместо застывшего «0:00» показываем, что изменения ждут отправки
                var remaining = deadlineUtc - DateTime.UtcNow;
                return remaining > TimeSpan.Zero ? FormatCountdown(remaining) : L["CloudTask_Overdue"];
            }

            var elapsed = DateTime.UtcNow - Timestamp;

            return elapsed.TotalMinutes switch
            {
                < 1 => L["Notification_JustNow"],
                < 60 => string.Format(L["Notification_MinutesAgo"], (int)elapsed.TotalMinutes),
                < 1440 => string.Format(L["Notification_HoursAgo"], (int)elapsed.TotalHours),
                _ => Timestamp.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
            };
        }
    }

    public string Icon => IsTaskActive
        ? "☁"
        : Severity switch
        {
            NotificationSeverity.Info => "🛈",
            NotificationSeverity.Success => "✓",
            NotificationSeverity.Warning => "⚠",
            NotificationSeverity.Error => "✕",
            _ => "🖈"
        };

    /// <summary>
    /// Семантический HEX-цвет для индикатора серьезности уведомления.
    /// </summary>
    public string SeverityColorHex => IsTaskActive
        ? "#8BE9FD"
        : Severity switch
        {
            NotificationSeverity.Success => "#2ECC71",
            NotificationSeverity.Warning => "#FFB86C",
            NotificationSeverity.Error => "#FF5555",
            _ => "#8BE9FD"
        };

    /// <summary>
    /// Перерисовывает строку обратного отсчёта задачи (вызывается таймером открытой панели).
    /// </summary>
    public void RefreshCountdown() => OnPropertyChanged(nameof(TimeAgo));

    private static string FormatCountdown(TimeSpan remaining)
    {
        // Округление вверх: «0:00» появляется только в момент отправки
        long totalSeconds = (long)Math.Ceiling(remaining.TotalSeconds);
        long hours = totalSeconds / 3600;
        long minutes = totalSeconds % 3600 / 60;
        long seconds = totalSeconds % 60;

        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{seconds:00}");
    }

    /// <summary>
    /// Уведомляет UI об изменении текста, иконки и статуса задачи на месте.
    /// </summary>
    public void NotifyVisualStateChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(Recommendation));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(SeverityColorHex));
        OnPropertyChanged(nameof(TimeAgo));
        OnPropertyChanged(nameof(TaskActionText));
        OnPropertyChanged(nameof(ShowTaskProgress));
        OnPropertyChanged(nameof(ShowTaskFooter));
        OnPropertyChanged(nameof(HasTaskAction));
        OnPropertyChanged(nameof(HasTaskStatus));
        OnPropertyChanged(nameof(HasTaskCountdown));
    }

    #endregion
}

public sealed record AttemptRecord(
    string ClientName,
    bool Success,
    string? ErrorMessage,
    DateTime Timestamp)
{
    public string FormattedTime => Timestamp.ToString("HH:mm:ss");
    public string StatusIcon => Success ? "✅" : "❌";

    public string DisplayMessage
    {
        get
        {
            if (Success)
                return LocalizationService.Instance["Attempt_Success"];

            if (!string.IsNullOrEmpty(ErrorMessage))
            {
                return ErrorMessage.Length > 50
                    ? ErrorMessage[..50] + "..."
                    : ErrorMessage;
            }

            return LocalizationService.Instance["Attempt_Failed"];
        }
    }
}

public enum NotificationSeverity
{
    Info,
    Success,
    Warning,
    Error
}
