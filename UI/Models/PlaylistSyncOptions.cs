namespace LMP.UI.Models;

/// <summary>
/// Стратегия разрешения конфликтов при синхронизации плейлиста.
/// </summary>
public enum PlaylistSyncStrategy
{
    /// <summary>
    /// YouTube → Local: перезаписать локальные данные облачными.
    /// Локальные уникальные треки будут удалены.
    /// </summary>
    ReplaceLocal,

    /// <summary>
    /// Двусторонний merge: добавить отсутствующие треки в обе стороны.
    /// Существующие треки не трогаются. Метаданные — по выбору пользователя.
    /// </summary>
    Merge,

    /// <summary>
    /// Local → YouTube: перезаписать облачные данные локальными.
    /// Облачные уникальные треки будут удалены из плейлиста YouTube.
    /// </summary>
    ReplaceCloud
}

/// <summary>
/// Опции синхронизации — что именно синхронизировать и какой стратегией.
/// Заполняется пользователем через <see cref="SyncPlaylistDialog"/>.
/// </summary>
public sealed class PlaylistSyncOptions
{
    /// <summary>Стратегия разрешения конфликтов для треков.</summary>
    public PlaylistSyncStrategy Strategy { get; set; } = PlaylistSyncStrategy.Merge;

    /// <summary>Синхронизировать название плейлиста.</summary>
    public bool SyncName { get; set; } = true;

    /// <summary>Синхронизировать описание плейлиста.</summary>
    public bool SyncDescription { get; set; } = true;

    /// <summary>Синхронизировать обложку (thumbnail URL).</summary>
    public bool SyncThumbnail { get; set; }

    /// <summary>Синхронизировать список треков.</summary>
    public bool SyncTracks { get; set; } = true;
}

/// <summary>
/// Результат операции синхронизации одного плейлиста.
/// </summary>
public sealed class PlaylistSyncResult
{
    public bool Success { get; init; }

    /// <summary>Кол-во треков, добавленных локально (из YouTube).</summary>
    public int TracksAddedLocally { get; init; }

    /// <summary>Кол-во треков, добавленных в YouTube (из локальных).</summary>
    public int TracksAddedToCloud { get; init; }

    /// <summary>Кол-во треков, удалённых локально (при ReplaceLocal).</summary>
    public int TracksRemovedLocally { get; init; }

    /// <summary>Кол-во треков, удалённых из YouTube (при ReplaceCloud).</summary>
    public int TracksRemovedFromCloud { get; init; }

    /// <summary>Были ли изменены метаданные (название, описание, обложка).</summary>
    public bool MetadataChanged { get; init; }

    /// <summary>Сообщение об ошибке (если Success=false).</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Краткая сводка для отображения пользователю.</summary>
    public string Summary
    {
        get
        {
            if (!Success) return ErrorMessage ?? "Sync failed";

            var parts = new List<string>(4);
            if (TracksAddedLocally > 0) parts.Add($"⬇ {TracksAddedLocally}");
            if (TracksAddedToCloud > 0) parts.Add($"⬆ {TracksAddedToCloud}");
            if (TracksRemovedLocally > 0) parts.Add($"🗑 local: {TracksRemovedLocally}");
            if (TracksRemovedFromCloud > 0) parts.Add($"🗑 cloud: {TracksRemovedFromCloud}");
            if (MetadataChanged) parts.Add("📝 metadata");
            return parts.Count > 0 ? string.Join(", ", parts) : "No changes";
        }
    }

    public static PlaylistSyncResult Fail(string error) => new()
    {
        Success = false,
        ErrorMessage = error
    };

    public static PlaylistSyncResult NoChanges() => new()
    {
        Success = true
    };
}

/// <summary>
/// Снимок состояния плейлиста в YouTube для сравнения с локальным.
/// Заполняется <see cref="PlaylistSyncService"/> перед показом диалога.
/// </summary>
public sealed class PlaylistSyncPreview
{
    /// <summary>Локальное название плейлиста.</summary>
    public string LocalName { get; init; } = "";

    /// <summary>Название в YouTube.</summary>
    public string CloudName { get; init; } = "";

    /// <summary>Локальное описание.</summary>
    public string? LocalDescription { get; init; }

    /// <summary>Описание в YouTube.</summary>
    public string? CloudDescription { get; init; }

    /// <summary>URL обложки в YouTube.</summary>
    public string? CloudThumbnailUrl { get; init; }

    /// <summary>Локальная обложка.</summary>
    public string? LocalThumbnailUrl { get; init; }

    /// <summary>Кол-во треков только в локальном плейлисте.</summary>
    public int LocalOnlyTrackCount { get; init; }

    /// <summary>Кол-во треков только в YouTube.</summary>
    public int CloudOnlyTrackCount { get; init; }

    /// <summary>Кол-во общих треков.</summary>
    public int CommonTrackCount { get; init; }

    /// <summary>Была ли обложка уже синхронизирована с облаком (локальный плейлист не редактировался после последней синхронизации).</summary>
    public bool IsThumbnailAlreadySynced { get; init; }

    /// <summary>ID плейлиста в YouTube для сопоставления студийных обложек.</summary>
    public string? YoutubePlaylistId { get; init; }

    /// <summary>Есть ли отличия в названии.</summary>
    public bool NameDiffers =>
        !string.Equals(LocalName?.Trim(), CloudName?.Trim(), StringComparison.Ordinal);

    /// <summary>Есть ли отличия в описании (null и пустая строка считаются эквивалентными).</summary>
    public bool DescriptionDiffers
    {
        get
        {
            var local = string.IsNullOrWhiteSpace(LocalDescription) ? string.Empty : LocalDescription.Trim();
            var cloud = string.IsNullOrWhiteSpace(CloudDescription) ? string.Empty : CloudDescription.Trim();
            return !string.Equals(local, cloud, StringComparison.Ordinal);
        }
    }

    /// <summary>Есть ли отличия в треках.</summary>
    public bool TracksDiffer => LocalOnlyTrackCount > 0 || CloudOnlyTrackCount > 0;

    /// <summary>Есть ли отличия в обложке с учётом нормализации и локальных файлов.</summary>
    public bool ThumbnailDiffers
    {
        get
        {
            // Если обе стороны не имеют обложки — различий нет
            if (string.IsNullOrWhiteSpace(LocalThumbnailUrl) && string.IsNullOrWhiteSpace(CloudThumbnailUrl))
                return false;

            // Если локальный плейлист не изменялся после последней синхронизации, а в облаке уже есть студийная обложка
            if (IsThumbnailAlreadySynced && !string.IsNullOrEmpty(CloudThumbnailUrl))
                return false;

            var localNorm = NormalizeUrl(LocalThumbnailUrl);
            var cloudNorm = NormalizeUrl(CloudThumbnailUrl);

            if (string.Equals(localNorm, cloudNorm, StringComparison.OrdinalIgnoreCase))
                return false;

            // Если оба URL указывают на студийную обложку текущего плейлиста в YouTube (независимо от параметров sqp=)
            if (!string.IsNullOrEmpty(YoutubePlaylistId) &&
                localNorm != null && cloudNorm != null &&
                localNorm.Contains($"/pl_c/{YoutubePlaylistId}/", StringComparison.OrdinalIgnoreCase) &&
                cloudNorm.Contains($"/pl_c/{YoutubePlaylistId}/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }
    }

    /// <summary>Есть ли какие-либо отличия.</summary>
    public bool HasAnyDifference =>
        NameDiffers || DescriptionDiffers || TracksDiffer || ThumbnailDiffers;

    private static string? NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            int q = url.IndexOf('?');
            return q >= 0 ? url[..q] : url;
        }
        return url;
    }
}