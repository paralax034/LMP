namespace LMP.Core.Models;

/// <summary>
/// Состояние включения набора треков в список воспроизведения.
/// </summary>
public enum PlaylistMembershipState
{
    /// <summary>
    /// Ни один из проверяемых треков не входит в плейлист.
    /// </summary>
    None = 0,

    /// <summary>
    /// В плейлист входит только часть из проверяемых треков.
    /// </summary>
    Indeterminate = 1,

    /// <summary>
    /// Все проверяемые треки уже включены в плейлист.
    /// </summary>
    All = 2
}