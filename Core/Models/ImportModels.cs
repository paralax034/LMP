namespace LMP.Core.Models;

/// <summary>
/// Каноническая модель трека из внешнего стримингового сервиса.
/// </summary>
public sealed record ExternalTrack(
    string Title,
    string Artist,
    TimeSpan Duration,
    string? OriginalId = null,
    string? Album = null,
    string? CoverUrl = null);

/// <summary>
/// Метаданные плейлиста из внешнего источника.
/// </summary>
public sealed record ExternalPlaylist(
    string Id,
    string Title,
    string? Description,
    string? CoverUrl,
    string? HexColor,
    int TrackCount,
    long? OwnerUid = null,
    string? AuthorName = null)
{
    public bool IsLikedPlaylist => Id == "likes";
    public bool HasCover => !string.IsNullOrEmpty(CoverUrl) && !IsLikedPlaylist;
    public bool ShowPlaceholder => string.IsNullOrEmpty(CoverUrl) && !IsLikedPlaylist;

    public string FormattedTrackCount =>
        LocalizationService.Instance.GetPlural("Playlist_TracksCount", TrackCount);
}

/// <summary>
/// Профиль пользователя во внешнем сервисе.
/// </summary>
public sealed record ExternalUserProfile(
    long Uid,
    string Login,
    string DisplayName);

/// <summary>
/// Отчёт о прогрессе импорта для UI.
/// </summary>
public sealed record ImportProgressReport(
    int Processed,
    int Total,
    string CurrentTrackName);

/// <summary>
/// Финальный результат импорта с замером трафика и списком ненайденных треков.
/// </summary>
public sealed record ImportSummary(
    int Matched,
    int Total,
    int LocalHits,
    int NetworkSearches,
    long SourceBytesReceived,
    long SourceBytesSent,
    long TotalTrafficBytes,
    long SavedTrafficBytes,
    IReadOnlyList<ExternalTrack> FailedTracks,
    string PlaylistId)
{
    public string FormattedTotalTraffic => FormatBytes(TotalTrafficBytes);
    public string FormattedSavedTraffic => FormatBytes(SavedTrafficBytes);
    public string FormattedSourceTraffic => FormatBytes(SourceBytesReceived + SourceBytesSent);

    public static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            >= 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} ГБ",
            >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F2} МБ",
            >= 1024 => $"{bytes / 1024.0:F1} КБ",
            _ => $"{bytes} Б"
        };
    }
}
