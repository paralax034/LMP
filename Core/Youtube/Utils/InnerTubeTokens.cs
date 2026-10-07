namespace LMP.Core.Youtube.Utils;

/// <summary>
/// Статические UTF-8 литералы ключей и строковых констант InnerTube API.
/// Используются для zero-alloc сопоставления токенов в <see cref="System.Text.Json.Utf8JsonReader"/> и <see cref="System.Text.Json.Utf8JsonWriter"/>.
/// </summary>
internal static class InnerTubeTokens
{
    public static ReadOnlySpan<byte> VideoId => "videoId"u8;
    public static ReadOnlySpan<byte> PlaylistId => "playlistId"u8;
    public static ReadOnlySpan<byte> PlaylistIndex => "playlistIndex"u8;
    public static ReadOnlySpan<byte> PlaylistSetVideoId => "playlistSetVideoId"u8;
    public static ReadOnlySpan<byte> SetVideoId => "setVideoId"u8;
    public static ReadOnlySpan<byte> Title => "title"u8;
    public static ReadOnlySpan<byte> Text => "text"u8;
    public static ReadOnlySpan<byte> Runs => "runs"u8;
    public static ReadOnlySpan<byte> SimpleText => "simpleText"u8;
    public static ReadOnlySpan<byte> Content => "content"u8;

    public static ReadOnlySpan<byte> MusicResponsiveListItemRenderer => "musicResponsiveListItemRenderer"u8;
    public static ReadOnlySpan<byte> PlaylistVideoRenderer => "playlistVideoRenderer"u8;
    public static ReadOnlySpan<byte> PlaylistItemData => "playlistItemData"u8;
    public static ReadOnlySpan<byte> FlexColumns => "flexColumns"u8;
    public static ReadOnlySpan<byte> FixedColumns => "fixedColumns"u8;
    public static ReadOnlySpan<byte> MusicResponsiveListItemFlexColumnRenderer => "musicResponsiveListItemFlexColumnRenderer"u8;
    public static ReadOnlySpan<byte> MusicResponsiveListItemFixedColumnRenderer => "musicResponsiveListItemFixedColumnRenderer"u8;

    public static ReadOnlySpan<byte> MusicResponsiveHeaderRenderer => "musicResponsiveHeaderRenderer"u8;
    public static ReadOnlySpan<byte> MusicEditablePlaylistDetailHeaderRenderer => "musicEditablePlaylistDetailHeaderRenderer"u8;
    public static ReadOnlySpan<byte> MusicDetailHeaderRenderer => "musicDetailHeaderRenderer"u8;
    public static ReadOnlySpan<byte> PlaylistHeaderRenderer => "playlistHeaderRenderer"u8;
    public static ReadOnlySpan<byte> Header => "header"u8;

    public static ReadOnlySpan<byte> NavigationEndpoint => "navigationEndpoint"u8;
    public static ReadOnlySpan<byte> BrowseEndpoint => "browseEndpoint"u8;
    public static ReadOnlySpan<byte> BrowseEndpointContextSupportedConfigs => "browseEndpointContextSupportedConfigs"u8;
    public static ReadOnlySpan<byte> BrowseEndpointContextMusicConfig => "browseEndpointContextMusicConfig"u8;
    public static ReadOnlySpan<byte> PageType => "pageType"u8;

    public static ReadOnlySpan<byte> PageTypeArtist => "MUSIC_PAGE_TYPE_ARTIST"u8;
    public static ReadOnlySpan<byte> PageTypeUserChannel => "MUSIC_PAGE_TYPE_USER_CHANNEL"u8;
    public static ReadOnlySpan<byte> PageTypeAlbum => "MUSIC_PAGE_TYPE_ALBUM"u8;

    public static ReadOnlySpan<byte> Thumbnail => "thumbnail"u8;
    public static ReadOnlySpan<byte> Thumbnails => "thumbnails"u8;
    public static ReadOnlySpan<byte> MusicThumbnailRenderer => "musicThumbnailRenderer"u8;
    public static ReadOnlySpan<byte> Url => "url"u8;

    public static ReadOnlySpan<byte> MusicItemRendererDisplayPolicy => "musicItemRendererDisplayPolicy"u8;
    public static ReadOnlySpan<byte> GreyOutPolicy => "MUSIC_ITEM_RENDERER_DISPLAY_POLICY_GREY_OUT"u8;

    public static ReadOnlySpan<byte> ContinuationItemRenderer => "continuationItemRenderer"u8;
    public static ReadOnlySpan<byte> ContinuationEndpoint => "continuationEndpoint"u8;
    public static ReadOnlySpan<byte> ContinuationCommand => "continuationCommand"u8;
    public static ReadOnlySpan<byte> NextContinuationData => "nextContinuationData"u8;
    public static ReadOnlySpan<byte> Continuations => "continuations"u8;
    public static ReadOnlySpan<byte> Continuation => "continuation"u8;
    public static ReadOnlySpan<byte> Token => "token"u8;

    public static ReadOnlySpan<byte> ResponseContext => "responseContext"u8;
    public static ReadOnlySpan<byte> VisitorData => "visitorData"u8;

    public static ReadOnlySpan<byte> TrackingParams => "trackingParams"u8;
    public static ReadOnlySpan<byte> ClickTrackingParams => "clickTrackingParams"u8;
    public static ReadOnlySpan<byte> ServiceTrackingParams => "serviceTrackingParams"u8;
    public static ReadOnlySpan<byte> CommandMetadata => "commandMetadata"u8;

    public static ReadOnlySpan<byte> Context => "context"u8;
    public static ReadOnlySpan<byte> Client => "client"u8;
    public static ReadOnlySpan<byte> ClientName => "clientName"u8;
    public static ReadOnlySpan<byte> ClientVersion => "clientVersion"u8;
    public static ReadOnlySpan<byte> Hl => "hl"u8;
    public static ReadOnlySpan<byte> Gl => "gl"u8;
    public static ReadOnlySpan<byte> UtcOffsetMinutes => "utcOffsetMinutes"u8;
    public static ReadOnlySpan<byte> WebRemix => "WEB_REMIX"u8;
    public static ReadOnlySpan<byte> Web => "WEB"u8;
    public static ReadOnlySpan<byte> User => "user"u8;
    public static ReadOnlySpan<byte> Query => "query"u8;
    public static ReadOnlySpan<byte> Params => "params"u8;
    public static ReadOnlySpan<byte> BrowseId => "browseId"u8;
}

/// <summary>
/// Централизованные строковые константы протокола InnerTube API.
/// </summary>
internal static class InnerTubeConstants
{
    /// <summary>Идентификатор категории видео YouTube для музыки.</summary>
    public const string MusicCategoryId = "10";

    /// <summary>Значение метаданных типа страницы для альбома.</summary>
    public const string PageTypeAlbum = "MUSIC_PAGE_TYPE_ALBUM";

    /// <summary>Значение метаданных типа страницы для артиста.</summary>
    public const string PageTypeArtist = "MUSIC_PAGE_TYPE_ARTIST";

    /// <summary>Значение метаданных типа страницы для канала пользователя.</summary>
    public const string PageTypeUserChannel = "MUSIC_PAGE_TYPE_USER_CHANNEL";

    /// <summary>Стиль значка официального верифицированного артиста.</summary>
    public const string BadgeVerifiedArtist = "BADGE_STYLE_TYPE_VERIFIED_ARTIST";

    /// <summary>Специфичные сериализованные параметры InnerTube protobuf.</summary>
    public static class Params
    {
        /// <summary>Параметр browse-запроса плейлиста для получения полного списка видео.</summary>
        public static ReadOnlySpan<byte> PlaylistVideoList => "wgYCEAE%3D"u8;

        // Фильтры поиска YouTube Music
        public const string MusicFilterGeneral = "EgWKAQIIAWoKEAkQBRAKEAMQBA%3D%3D";
        public const string MusicFilterSong = "EgWKAQIIAWoKEAkQBRAKEAMQBA%3D%3D";
        public const string MusicFilterVideo = "EgWKAQIQAWoKEAkQChAFEAMQBA%3D%3D";
        public const string MusicFilterAlbum = "EgWKAQIYAWoKEAkQChAFEAMQBA%3D%3D";
        public const string MusicFilterArtist = "EgWKAQIgAWoKEAkQChAFEAMQBA%3D%3D";
        public const string MusicFilterPlaylist = "EgeKAQQoAEABagoQAxAEEAoQCRAF";

        // Фильтры поиска стандартного YouTube Web
        public const string WebFilterVideo = "EgIQAQ%3D%3D";
        public const string WebFilterPlaylist = "EgIQAw%3D%3D";
        public const string WebFilterChannel = "EgIQAg%3D%3D";
    }
}