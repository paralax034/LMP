using System.Text.Json.Serialization;
using LMP.Core.Youtube.Search;

namespace LMP.Core.Models;

/// <summary>
/// Режим хранения и синхронизации плейлиста.
/// Отвечает исключительно за поведение sync, а не за ownership.
/// </summary>
public enum PlaylistSyncMode
{
    LocalOnly,
    TwoWaySync,
    CloudPublic
}

/// <summary>
/// Владение плейлистом с точки зрения YouTube.
/// Определяет, может ли текущий пользователь редактировать контент.
/// </summary>
public enum PlaylistOwnership
{
    /// <summary>Данные о владельце отсутствуют (старые записи, fallback).</summary>
    Unknown = 0,

    /// <summary>Плейлист принадлежит текущему аутентифицированному пользователю.</summary>
    Mine = 1,

    /// <summary>Плейлист принадлежит другому пользователю.</summary>
    Foreign = 2,

    /// <summary>Системный плейлист (Liked, Watch Later).</summary>
    System = 3
}

/// <summary>
/// Уровень доступа к плейлисту на YouTube.
/// </summary>
public enum PlaylistVisibility
{
    /// <summary>Данные о видимости отсутствуют.</summary>
    Unknown = 0,

    /// <summary>Доступен только владельцу.</summary>
    Private = 1,

    /// <summary>Доступен по прямой ссылке.</summary>
    Unlisted = 2,

    /// <summary>Доступен всем.</summary>
    Public = 3
}

/// <summary>
/// Доменная модель плейлиста.
/// Хранит реальную информацию о плейлисте; права и ограничения вычисляются из неё.
/// </summary>
public sealed class Playlist : IBatchItem, ISearchResult
{
    #region Identity

    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("name")]
    public string StoredName { get; set; } = "New Playlist";

    [JsonIgnore]
    public string Name
    {
        get
        {
            if (Id == LibraryService.LikedPlaylistId)
                return LocalizationService.Instance["Playlist_Liked"];
            return StoredName;
        }
        set => StoredName = value;
    }

    [JsonIgnore]
    public string Title => Name;

    [JsonIgnore]
    public string Url => YoutubeId != null
        ? $"https://www.youtube.com/playlist?list={YoutubeId}"
        : string.Empty;

    #endregion

    #region Appearance

    public string? ThumbnailUrl { get; set; }
    public string? CustomColor { get; set; }

    /// <summary>
    /// Автоматически вычисленный доминантный цвет из обложки.
    /// Формат: #RRGGBB. Пересчитывается при смене обложки.
    /// </summary>
    public string? ComputedColor { get; set; }

    /// <summary>
    /// Эффективный цвет для градиента хедера.
    /// Приоритет: CustomColor → ComputedColor → null.
    /// </summary>
    [JsonIgnore]
    public string? EffectiveColor => CustomColor ?? ComputedColor;

    #endregion

    #region Author & Ownership

    /// <summary>
    /// Отображаемое имя автора/владельца плейлиста.
    /// </summary>
    public string? Author { get; set; }

    /// <summary>
    /// Описание плейлиста.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// YouTube Channel ID владельца плейлиста.
    /// Стабильный идентификатор для определения ownership и построения ссылки на канал.
    /// </summary>
    public string? OwnerChannelId { get; set; }

    /// <summary>
    /// Кому принадлежит плейлист: текущему пользователю, другому, системе.
    /// Определяется при импорте сравнением <see cref="OwnerChannelId"/>
    /// с идентификатором канала текущего аккаунта.
    /// </summary>
    public PlaylistOwnership Ownership { get; set; } = PlaylistOwnership.Unknown;

    /// <summary>
    /// Уровень доступа к плейлисту на YouTube: публичный, по ссылке, приватный.
    /// Заполняется из payload YouTube API, если данные доступны.
    /// </summary>
    public PlaylistVisibility Visibility { get; set; } = PlaylistVisibility.Unknown;

    #endregion

    #region Sync & Cloud

    /// <summary>
    /// Количество просмотров плейлиста по данным YouTube Music.
    /// Заполняется при синхронизации (PlaylistSyncService).
    /// </summary>
    public long? ViewCount { get; set; }

    /// <summary>
    /// Дата создания/обновления плейлиста по данным YouTube.
    /// </summary>
    public DateOnly? ReleaseDate { get; set; }

    /// <summary>
    /// Режим синхронизации. Определяет только поведение sync, не ownership.
    /// </summary>
    public PlaylistSyncMode SyncMode { get; set; } = PlaylistSyncMode.LocalOnly;

    /// <summary>
    /// YouTube-идентификатор плейлиста для облачных операций.
    /// </summary>
    public string? YoutubeId { get; set; }

    /// <summary>
    /// ETag для оптимистичной проверки изменений.
    /// </summary>
    public string? ETag { get; set; }

    /// <summary>
    /// Количество треков по данным YouTube API.
    /// Позволяет показать count до полной загрузки списка треков.
    /// </summary>
    public int? CloudTrackCount { get; set; }

    /// <summary>
    /// UTC-время последней успешной синхронизации с YouTube.
    /// </summary>
    public DateTime? LastSyncedAtUtc { get; set; }

    /// <summary>
    /// Плейлист недоступен в облаке (удалён, закрыт, 403/404 при sync).
    /// </summary>
    public bool IsCloudUnavailable { get; set; }

    #endregion

    #region Local Ownership (account isolation)

    /// <summary>
    /// Идентификатор владельца локальной записи для изоляции между аккаунтами.
    /// Не путать с <see cref="OwnerChannelId"/> (владелец на YouTube).
    /// </summary>
    public string OwnerId { get; set; } = string.Empty;

    #endregion

    #region Sync Mode Queries (backward-compatible)

    /// <summary>
    /// Плейлист имеет локальное представление (LocalOnly или TwoWaySync).
    /// </summary>
    [JsonIgnore]
    public bool IsLocal => SyncMode is PlaylistSyncMode.LocalOnly or PlaylistSyncMode.TwoWaySync;

    /// <summary>
    /// Плейлист синхронизируется с аккаунтом YouTube Music.
    /// </summary>
    [JsonIgnore]
    public bool IsFromAccount => SyncMode == PlaylistSyncMode.TwoWaySync;

    #endregion

    #region Ownership Queries

    /// <summary>
    /// Плейлист принадлежит текущему аутентифицированному пользователю.
    /// </summary>
    [JsonIgnore]
    public bool IsMine => Ownership == PlaylistOwnership.Mine;

    /// <summary>
    /// Плейлист принадлежит другому пользователю.
    /// </summary>
    [JsonIgnore]
    public bool IsForeign => Ownership == PlaylistOwnership.Foreign;

    /// <summary>
    /// Системный плейлист (Liked, Watch Later).
    /// </summary>
    [JsonIgnore]
    public bool IsSystem => Ownership == PlaylistOwnership.System;

    #endregion

    #region Cloud Link Queries

    /// <summary>
    /// Плейлист имеет активную привязку к YouTube (ID есть и не помечен как недоступный).
    /// </summary>
    [JsonIgnore]
    public bool HasCloudLink => !string.IsNullOrEmpty(YoutubeId) && !IsCloudUnavailable;

    /// <summary>
    /// Текущий пользователь может синхронизировать изменения обратно в YouTube.
    /// </summary>
    [JsonIgnore]
    public bool CanSyncToCloud => (IsMine || Ownership == PlaylistOwnership.Unknown) && HasCloudLink && SyncMode == PlaylistSyncMode.TwoWaySync;

    /// <summary>
    /// Можно получить свежие данные из YouTube.
    /// </summary>
    [JsonIgnore]
    public bool CanPullFromCloud => HasCloudLink;

    #endregion

    #region Permissions

    /// <summary>
    /// Плейлист доступен только для чтения (чужой или CloudPublic).
    /// <para><b>Backward-compatible:</b> для <see cref="PlaylistOwnership.Unknown"/>
    /// возвращает <c>false</c>, сохраняя поведение для старых записей.</para>
    /// </summary>
    [JsonIgnore]
    public bool IsReadOnly => IsForeign || SyncMode == PlaylistSyncMode.CloudPublic;

    /// <summary>
    /// Плейлист можно редактировать (не read-only и не системный).
    /// Заменяет старую логику <c>SyncMode != CloudPublic</c>,
    /// добавляя проверку ownership.
    /// </summary>
    [JsonIgnore]
    public bool IsEditable => !IsReadOnly && !IsSystem;

    /// <summary>
    /// Можно менять название, описание, обложку.
    /// </summary>
    [JsonIgnore]
    public bool CanEditMetadata => IsEditable;

    /// <summary>
    /// Можно добавлять, удалять, переставлять треки.
    /// </summary>
    [JsonIgnore]
    public bool CanEditTracks => IsEditable;

    #endregion

    #region Author URL

    /// <summary>
    /// Прямая ссылка на канал автора плейлиста.
    /// <c>null</c> если <see cref="OwnerChannelId"/> неизвестен.
    /// </summary>
    [JsonIgnore]
    public string? AuthorUrl => !string.IsNullOrEmpty(OwnerChannelId)
        ? $"https://www.youtube.com/channel/{OwnerChannelId}"
        : null;

    #endregion

    #region Timestamps

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    #endregion

    #region Runtime (not persisted)

    [JsonIgnore]
    public List<string> TrackIds { get; set; } = [];

    [JsonIgnore]
    public int TrackCount { get; set; }

    #endregion
}