namespace LMP.Core.Youtube.Exceptions;

/// <summary>
/// Причина недоступности стрима.
/// </summary>
public enum StreamUnavailableReason
{
    /// <summary>Неизвестная причина.</summary>
    Unknown = 0,

    /// <summary>HTTP 403 Forbidden.</summary>
    Forbidden403,

    /// <summary>Все клиенты не смогли получить стрим.</summary>
    AllClientsFailed,

    /// <summary>Заблокировано в регионе.</summary>
    RegionBlocked,

    /// <summary>Возрастные ограничения.</summary>
    AgeRestricted,

    /// <summary>Прямая трансляция (не VOD).</summary>
    LiveStream,

    /// <summary>Приватное видео.</summary>
    Private,

    /// <summary>Видео удалено.</summary>
    Removed,

    /// <summary>Требуется подписка/оплата.</summary>
    PaymentRequired,

    /// <summary>Заблокировано по жалобе правообладателя (нарушение авторских прав).</summary>
    CopyrightBlocked
}

/// <summary>
/// Выбрасывается когда стримы недоступны (403, блокировка региона, и т.д.).
/// </summary>
public sealed class StreamUnavailableException : YoutubeExplodeException
{
    /// <summary>
    /// Тип недоступности стрима.
    /// </summary>
    public StreamUnavailableReason Reason { get; }

    /// <summary>
    /// ID видео.
    /// </summary>
    public string VideoId { get; }

    /// <summary>
    /// HTTP статус код (если применимо).
    /// </summary>
    public int? HttpStatusCode { get; }

    /// <summary>
    /// Инициализирует новое исключение StreamUnavailableException.
    /// </summary>
    public StreamUnavailableException(
        string message,
        string videoId,
        StreamUnavailableReason reason,
        int? httpStatusCode = null) : base(message)
    {
        VideoId = videoId;
        Reason = reason;
        HttpStatusCode = httpStatusCode;
    }

    /// <summary>
    /// Ключ локализации для данного типа ошибки.
    /// </summary>
    public string GetLocalizationKey()
    {
        return Reason switch
        {
            StreamUnavailableReason.CopyrightBlocked
                => "Error_Stream_CopyrightBlocked",

            StreamUnavailableReason.Forbidden403
                => "Error_Stream_Forbidden",

            StreamUnavailableReason.AllClientsFailed
                => "Error_Stream_AllClientsFailed",

            StreamUnavailableReason.RegionBlocked
                => "Error_Stream_RegionBlocked",

            StreamUnavailableReason.AgeRestricted
                => "Error_Stream_AgeRestricted",

            StreamUnavailableReason.LiveStream
                => "Error_Stream_LiveStream",

            StreamUnavailableReason.Private
                => "Error_Stream_Private",

            StreamUnavailableReason.Removed
                => "Error_Stream_Removed",

            StreamUnavailableReason.PaymentRequired
                => "Error_Stream_PaymentRequired",

            _ => "Error_Stream_Unknown"
        };
    }
}