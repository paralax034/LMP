namespace LMP.Core.Models;

/// <summary>
/// Inclusion state of a set of tracks in the playlist.
/// </summary>
public enum PlaylistMembershipState
{
    /// <summary>
    /// None of the checked tracks are included in the playlist.
    /// </summary>
    None = 0,

    /// <summary>
    /// Only some of the checked tracks are included in the playlist.
    /// </summary>
    Indeterminate = 1,

    /// <summary>
    /// All checked tracks are already included in the playlist.
    /// </summary>
    All = 2
}