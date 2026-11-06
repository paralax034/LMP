using MemoryPack;

namespace LMP.Core.Models;

[MemoryPackable]
public sealed partial class YoutubeAccountItem
{
    public static LocalizationService L => LocalizationService.Instance;

    /// <summary>
    /// Account ordinal index in the list.
    /// </summary>
    public int Index { get; set; }

    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string AvatarUrl { get; set; } = "";

    /// <summary>
    /// Gaia ID of the primary account.
    /// </summary>
    public string GaiaId { get; set; } = "";

    /// <summary>
    /// Channel tag (e.g., @handle).
    /// </summary>
    public string Handle { get; set; } = "";

    /// <summary>
    /// Returns the display priority ID (GaiaId for the main profile or Email).
    /// </summary>
    public string DisplayId => !string.IsNullOrEmpty(GaiaId) ? GaiaId : Email;

    /// <summary>
    /// Google multi-auth session index (typically "0" for primary, "1", "2" for secondary accounts).
    /// </summary>
    public string AuthUser { get; set; } = AuthState.DefaultAuthUser;

    public bool IsSelected { get; set; }
}