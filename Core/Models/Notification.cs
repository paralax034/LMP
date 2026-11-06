using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;

namespace LMP.Core.Models;

/// <summary>
/// A notification or live background task in the system.
/// Supports real-time reactive updates for progress, status, and action buttons,
/// as well as automatic translation on language change.
/// </summary>
public sealed partial class Notification : ObservableObject
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Unique key of the background task (e.g., <c>outbox:pl_123</c> or <c>import:yt</c>).
    /// Equal to <see langword="null"/> for regular notifications.
    /// </summary>
    public string? TaskKey { get; init; }

    /// <summary>
    /// Indicates that the item represents a cloud queue or synchronization background task.
    /// </summary>
    public bool IsTask { get; init; }

    /// <summary>
    /// Indicates whether the background task is currently pending dispatch or executing.
    /// Switches to <see langword="false"/> upon completion, turning into a standard history entry.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTaskProgress))]
    [NotifyPropertyChangedFor(nameof(ShowTaskFooter))]
    [NotifyPropertyChangedFor(nameof(HasTaskAction))]
    [NotifyPropertyChangedFor(nameof(HasTaskStatus))]
    [NotifyPropertyChangedFor(nameof(TimeAgo))]
    public partial bool IsTaskActive { get; set; }

    /// <summary>
    /// Scheduled task dispatch time (UTC). While set and no <see cref="TaskStatusText"/> is present,
    /// the time string displays a live countdown.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTaskStatus))]
    [NotifyPropertyChangedFor(nameof(HasTaskCountdown))]
    [NotifyPropertyChangedFor(nameof(TimeAgo))]
    public partial DateTime? TaskDeadlineUtc { get; set; }

    /// <summary>
    /// Hides the task progress bar (dispatch countdown is displayed via timer instead of animation).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTaskProgress))]
    [NotifyPropertyChangedFor(nameof(ShowTaskFooter))]
    public partial bool IsTaskProgressHidden { get; set; }

    /// <summary>
    /// Task execution or pending progress (0.0 .. 1.0).
    /// </summary>
    [ObservableProperty] public partial double TaskProgress { get; set; }

    /// <summary>
    /// Indeterminate progress flag (pulsing during network dispatch).
    /// </summary>
    [ObservableProperty] public partial bool IsTaskIndeterminate { get; set; }

    /// <summary>
    /// Additional task status (e.g., dispatch timer or retry state).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTaskStatus))]
    [NotifyPropertyChangedFor(nameof(HasTaskCountdown))]
    [NotifyPropertyChangedFor(nameof(TimeAgo))]
    public partial string? TaskStatusText { get; set; }

    /// <summary>
    /// Localization key for the quick action button (e.g., <c>CloudTask_ForceSend</c> or <c>Button_Cancel</c>).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskActionText))]
    [NotifyPropertyChangedFor(nameof(HasTaskAction))]
    [NotifyPropertyChangedFor(nameof(ShowTaskFooter))]
    public partial string? TaskActionKey { get; set; }

    /// <summary>
    /// Indicates whether the action is a cancel button (renders a cross icon instead of a cloud icon).
    /// </summary>
    [ObservableProperty] public partial bool IsCancelAction { get; set; }

    /// <summary>
    /// Dynamic localized message generator for composite queue tasks.
    /// Automatically recomputed on UI language change.
    /// </summary>
    public Func<string>? DynamicMessageFactory { get; set; }

    /// <summary>
    /// Localized text for the quick action button ("Send Now" or "Cancel").
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
    /// Quick action command for the active task.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTaskAction))]
    [NotifyPropertyChangedFor(nameof(ShowTaskFooter))]
    public partial ICommand? TaskActionCommand { get; set; }

    /// <summary>
    /// Title localization key (e.g., "Error_Playback_Title").
    /// If null, <see cref="TitleRaw"/> is used.
    /// </summary>
    public string? TitleKey { get; set; }

    /// <summary>
    /// Raw title text (fallback if TitleKey is not set).
    /// </summary>
    public string? TitleRaw { get; set; }

    /// <summary>
    /// Message localization key.
    /// </summary>
    public string? MessageKey { get; set; }

    /// <summary>
    /// Raw message text (fallback).
    /// </summary>
    public string? MessageRaw { get; set; }

    /// <summary>
    /// Message formatting arguments (string.Format).
    /// </summary>
    public object[]? MessageArgs { get; set; }

    /// <summary>
    /// Recommendation localization key.
    /// </summary>
    public string? RecommendationKey { get; init; }

    /// <summary>
    /// Raw recommendation text (fallback).
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
    /// Localization service instance for direct compiled binding in templates.
    /// </summary>
    public LocalizationService L => LocalizationService.Instance;

    /// <summary>
    /// Localized title (automatically translated on language change).
    /// </summary>
    public string Title => !string.IsNullOrEmpty(TitleKey)
        ? L[TitleKey]
        : TitleRaw ?? "";

    public string TrackUrl => !string.IsNullOrEmpty(TrackId) ? $"https://www.youtube.com/watch?v={TrackId}" : "";

    /// <summary>
    /// Localized message.
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
    /// Localized recommendation.
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
    /// The task card bottom panel (progress and/or action button) is required only if there is content to show.
    /// </summary>
    public bool ShowTaskFooter => IsTaskActive && (ShowTaskProgress || HasTaskAction);

    /// <summary>
    /// Indicates whether the live countdown line is active (requires periodic <see cref="RefreshCountdown"/>).
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
                // Expired, but worker hasn't picked up the task yet (no network, auth issues, or active cooldown):
                // display that changes are pending dispatch instead of stuck "0:00"
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
    /// Semantic HEX color for the notification severity indicator.
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
    /// Redraws the task countdown string (called by the open panel timer).
    /// </summary>
    public void RefreshCountdown() => OnPropertyChanged(nameof(TimeAgo));

    private static string FormatCountdown(TimeSpan remaining)
    {
        // Ceiling rounding: "0:00" appears only at the moment of dispatch
        long totalSeconds = (long)Math.Ceiling(remaining.TotalSeconds);
        long hours = totalSeconds / 3600;
        long minutes = totalSeconds % 3600 / 60;
        long seconds = totalSeconds % 60;

        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{seconds:00}");
    }

    /// <summary>
    /// Notifies the UI to update task text, icon, and status in place.
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
