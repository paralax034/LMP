using System.Collections.ObjectModel;

namespace LMP.Core.Models;

/// <summary>
/// Уведомление в системе.
/// Поддерживает автоматический перевод при смене языка.
/// </summary>
public sealed class Notification
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Ключ локализации заголовка (например "Error_Playback_Title").
    /// Если null — используется <see cref="TitleRaw"/>.
    /// </summary>
    public string? TitleKey { get; init; }

    /// <summary>
    /// Готовый текст заголовка (fallback если TitleKey не задан).
    /// </summary>
    public string? TitleRaw { get; init; }

    /// <summary>
    /// Ключ локализации сообщения.
    /// </summary>
    public string? MessageKey { get; init; }

    /// <summary>
    /// Готовый текст сообщения (fallback).
    /// </summary>
    public string? MessageRaw { get; init; }

    /// <summary>
    /// Аргументы для форматирования сообщения (string.Format).
    /// </summary>
    public object[]? MessageArgs { get; init; }

    /// <summary>
    /// Ключ локализации рекомендации.
    /// </summary>
    public string? RecommendationKey { get; init; }

    /// <summary>
    /// Готовый текст рекомендации (fallback).
    /// </summary>
    public string? RecommendationRaw { get; init; }

    public NotificationSeverity Severity { get; init; }
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

    public bool HasExceptionDetails => !string.IsNullOrWhiteSpace(ExceptionDetails);
    public bool HasDetails => Attempts?.Count > 0 || HasExceptionDetails;
    public bool HasRecommendation => !string.IsNullOrEmpty(Recommendation);

    public string TimeAgo
    {
        get
        {
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

    public string Icon => Severity switch
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
    public string SeverityColorHex => Severity switch
    {
        NotificationSeverity.Success => "#2ECC71",
        NotificationSeverity.Warning => "#FFB86C",
        NotificationSeverity.Error => "#FF5555",
        _ => "#8BE9FD"
    };

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