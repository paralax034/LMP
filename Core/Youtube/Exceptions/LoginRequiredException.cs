namespace LMP.Core.Youtube.Exceptions;

/// <summary>
/// Thrown when YouTube requires authentication to play content or perform operations.
/// Typically caused by age restrictions, private content, bot detection, or an expired session.
/// </summary>
public sealed class LoginRequiredException(
    string message,
    string videoId,
    LoginRequiredReason reason) : YoutubeExplodeException(message)
{
    /// <summary>
    /// Reason for requiring authentication.
    /// </summary>
    public LoginRequiredReason Reason { get; } = reason;

    /// <summary>
    /// Video ID.
    /// </summary>
    public string VideoId { get; } = videoId;

    /// <summary>
    /// Localization key for this error type.
    /// </summary>
    public string GetLocalizationKey()
    {
        return Reason switch
        {
            LoginRequiredReason.AgeRestricted => "Error_Login_AgeRestricted",
            LoginRequiredReason.Private => "Error_Login_Private",
            LoginRequiredReason.MembersOnly => "Error_Login_MembersOnly",
            LoginRequiredReason.SessionExpired => "Auth_SessionExpired_Message",
            LoginRequiredReason.BotDetection => "Error_Login_BotDetection",
            _ => "Error_Login_Required"
        };
    }
}

/// <summary>
/// Reason for requiring authentication.
/// </summary>
public enum LoginRequiredReason
{
    /// <summary>Unknown reason.</summary>
    Unknown = 0,

    /// <summary>Age-restricted.</summary>
    AgeRestricted,

    /// <summary>Private video.</summary>
    Private,

    /// <summary>Members-only.</summary>
    MembersOnly,

    /// <summary>Authentication session expired or invalid.</summary>
    SessionExpired,

    /// <summary>
    /// YouTube flagged the request as automated (bot detection).
    /// The response contains <c>LOGIN_REQUIRED</c> + <c>"Sign in to confirm you're not a bot"</c>.
    /// </summary>
    BotDetection
}