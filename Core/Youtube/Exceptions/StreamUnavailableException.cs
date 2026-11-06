namespace LMP.Core.Youtube.Exceptions;

/// <summary>
/// Reason for stream unavailability.
/// </summary>
public enum StreamUnavailableReason
{
    /// <summary>Unknown reason.</summary>
    Unknown = 0,

    /// <summary>HTTP 403 Forbidden.</summary>
    Forbidden403,

    /// <summary>All clients failed to obtain the stream.</summary>
    AllClientsFailed,

    /// <summary>Blocked in region.</summary>
    RegionBlocked,

    /// <summary>Age restricted.</summary>
    AgeRestricted,

    /// <summary>Live stream (not VOD).</summary>
    LiveStream,

    /// <summary>Private video.</summary>
    Private,

    /// <summary>Video deleted.</summary>
    Removed,

    /// <summary>Subscription/payment required.</summary>
    PaymentRequired,

    /// <summary>Blocked due to a copyright claim (copyright infringement).</summary>
    CopyrightBlocked
}

/// <summary>
/// Thrown when streams are unavailable (403, region block, etc.).
/// </summary>
public sealed class StreamUnavailableException : YoutubeExplodeException
{
    /// <summary>
    /// Stream unavailability type.
    /// </summary>
    public StreamUnavailableReason Reason { get; }

    /// <summary>
    /// Video ID.
    /// </summary>
    public string VideoId { get; }

    /// <summary>
    /// HTTP status code (if applicable).
    /// </summary>
    public int? HttpStatusCode { get; }

    /// <summary>
    /// Initializes a new instance of StreamUnavailableException.
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
    /// Localization key for this error type.
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