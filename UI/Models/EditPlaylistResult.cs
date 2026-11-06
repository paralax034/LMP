namespace LMP.UI.Models;

/// <summary>
/// The result of the playlist editing dialog.
/// </summary>
public sealed class EditPlaylistResult
{
    public string Name { get; set; } = "";
    public string? ThumbnailUrl { get; set; }
    public string? CustomColor { get; set; }

    /// <summary>
    /// Playlist description. null if unchanged.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Color extracted from the cover art (if recalculated in the dialog).
    /// null — not recalculated, leave as is.
    /// </summary>
    public string? ComputedColor { get; set; }

    /// <summary>
    /// YouTube sync state change.
    /// null — user did not touch the toggle.
    /// true — user wants to link to the cloud.
    /// false — user wants to unlink from the cloud.
    /// </summary>
    public bool? SyncToCloud { get; set; }

    /// <summary>
    /// User requested to create a local copy of the playlist.
    /// When true, <see cref="PlaylistEditService"/> creates a new local playlist
    /// instead of editing the original. A copy is never linked to YouTube.
    /// </summary>
    public bool ShouldCreateCopy { get; set; }
}