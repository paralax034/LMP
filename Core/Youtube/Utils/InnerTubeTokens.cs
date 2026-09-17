namespace LMP.Core.Youtube.Utils;

/// <summary>
/// Статические UTF-8 литералы ключей и строковых констант InnerTube API.
/// Используются для zero-alloc сопоставления токенов в <see cref="System.Text.Json.Utf8JsonReader"/>.
/// </summary>
internal static class InnerTubeTokens
{
    public static ReadOnlySpan<byte> VideoId => "videoId"u8;
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
}