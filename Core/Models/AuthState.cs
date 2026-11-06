namespace LMP.Core.Models;

/// <summary>
/// Represents the Google/YouTube user authorization state.
/// </summary>
public sealed class AuthState
{
    public const string DefaultAuthUser = "0";

    private string _userName = "Guest";

    public bool IsAuthenticated { get; set; }

    /// <summary>
    /// The display name of the user. Automatically returns the localized guest name from resources if no session exists.
    /// </summary>
    public string UserName
    {
        get => IsAuthenticated ? _userName : LocalizationService.Instance["Auth_Guest"];
        set => _userName = value;
    }

    public string UserEmail { get; set; } = "";
    public string AvatarUrl { get; set; } = "";

    /// <summary>
    /// The active YouTube account identifier (Gaia ID) for targeted isolation.
    /// </summary>
    public string ActiveGaiaId { get; set; } = string.Empty;

    /// <summary>
    /// The Google multi-login session index of the active account.
    /// Defaults to an empty string to trust the IsSelected flag from YouTube 
    /// during initial sign-in via the extension.
    /// </summary>
    public string AuthUser { get; set; } = DefaultAuthUser;

    /// <summary>
    /// The cached list of user channels for instant offline switching.
    /// </summary>
    public List<YoutubeAccountItem> CachedAccounts { get; set; } = [];

    /// <summary>
    /// A unique identifier for the current profile used for DB data isolation.
    /// Uses Gaia ID if available, otherwise email, with an unconditional fallback to guest.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayId => !string.IsNullOrEmpty(ActiveGaiaId)
        ? ActiveGaiaId
        : (!string.IsNullOrEmpty(UserEmail) ? UserEmail : "guest");

    public DateTime LastUpdated { get; set; } = DateTime.MinValue;
}