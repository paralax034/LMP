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

    public static ReadOnlySpan<byte> PlayabilityStatus => "playabilityStatus"u8;
    public static ReadOnlySpan<byte> Status => "status"u8;
    public static ReadOnlySpan<byte> Reason => "reason"u8;
    public static ReadOnlySpan<byte> DesktopLegacyAgeGateReason => "desktopLegacyAgeGateReason"u8;
    public static ReadOnlySpan<byte> Microformat => "microformat"u8;
    public static ReadOnlySpan<byte> PlayerMicroformatRenderer => "playerMicroformatRenderer"u8;
    public static ReadOnlySpan<byte> Category => "category"u8;
    public static ReadOnlySpan<byte> VideoDetails => "videoDetails"u8;
    public static ReadOnlySpan<byte> CategoryId => "categoryId"u8;
    public static ReadOnlySpan<byte> MusicVideoType => "musicVideoType"u8;
    public static ReadOnlySpan<byte> ChannelId => "channelId"u8;
    public static ReadOnlySpan<byte> Author => "author"u8;
    public static ReadOnlySpan<byte> UploadDate => "uploadDate"u8;
    public static ReadOnlySpan<byte> LengthSeconds => "lengthSeconds"u8;
    public static ReadOnlySpan<byte> Keywords => "keywords"u8;
    public static ReadOnlySpan<byte> ShortDescription => "shortDescription"u8;
    public static ReadOnlySpan<byte> ViewCount => "viewCount"u8;
    public static ReadOnlySpan<byte> ErrorScreen => "errorScreen"u8;
    public static ReadOnlySpan<byte> StreamingData => "streamingData"u8;
    public static ReadOnlySpan<byte> Formats => "formats"u8;
    public static ReadOnlySpan<byte> AdaptiveFormats => "adaptiveFormats"u8;
    public static ReadOnlySpan<byte> Captions => "captions"u8;
    public static ReadOnlySpan<byte> PlayerCaptionsTracklistRenderer => "playerCaptionsTracklistRenderer"u8;
    public static ReadOnlySpan<byte> CaptionTracks => "captionTracks"u8;
    public static ReadOnlySpan<byte> PlayerConfig => "playerConfig"u8;
    public static ReadOnlySpan<byte> AudioConfig => "audioConfig"u8;
    public static ReadOnlySpan<byte> PerceptualLoudnessDb => "perceptualLoudnessDb"u8;
    public static ReadOnlySpan<byte> BaseUrl => "baseUrl"u8;
    public static ReadOnlySpan<byte> LanguageCode => "languageCode"u8;
    public static ReadOnlySpan<byte> Name => "name"u8;
    public static ReadOnlySpan<byte> VssId => "vssId"u8;

    public static ReadOnlySpan<byte> Itag => "itag"u8;
    public static ReadOnlySpan<byte> Cipher => "cipher"u8;
    public static ReadOnlySpan<byte> SignatureCipher => "signatureCipher"u8;
    public static ReadOnlySpan<byte> ContentLength => "contentLength"u8;
    public static ReadOnlySpan<byte> Bitrate => "bitrate"u8;
    public static ReadOnlySpan<byte> MimeType => "mimeType"u8;
    public static ReadOnlySpan<byte> AudioChannels => "audioChannels"u8;
    public static ReadOnlySpan<byte> AudioTrack => "audioTrack"u8;
    public static ReadOnlySpan<byte> Id => "id"u8;
    public static ReadOnlySpan<byte> DisplayName => "displayName"u8;
    public static ReadOnlySpan<byte> AudioIsDefault => "audioIsDefault"u8;
    public static ReadOnlySpan<byte> QualityLabel => "qualityLabel"u8;
    public static ReadOnlySpan<byte> Width => "width"u8;
    public static ReadOnlySpan<byte> Height => "height"u8;
    public static ReadOnlySpan<byte> Fps => "fps"u8;
    public static ReadOnlySpan<byte> MusicPlaylistShelfRenderer => "musicPlaylistShelfRenderer"u8;
    public static ReadOnlySpan<byte> PlaylistVideoListRenderer => "playlistVideoListRenderer"u8;
    public static ReadOnlySpan<byte> MusicPlaylistShelfContinuation => "musicPlaylistShelfContinuation"u8;
    public static ReadOnlySpan<byte> PlaylistVideoListContinuation => "playlistVideoListContinuation"u8;
    public static ReadOnlySpan<byte> PlayNavigationEndpoint => "playNavigationEndpoint"u8;
    public static ReadOnlySpan<byte> WatchEndpoint => "watchEndpoint"u8;
    public static ReadOnlySpan<byte> OnTap => "onTap"u8;
    public static ReadOnlySpan<byte> InnertubeCommand => "innertubeCommand"u8;
    public static ReadOnlySpan<byte> ReelWatchEndpoint => "reelWatchEndpoint"u8;
    public static ReadOnlySpan<byte> OverlayMetadata => "overlayMetadata"u8;
    public static ReadOnlySpan<byte> PrimaryText => "primaryText"u8;
    public static ReadOnlySpan<byte> OwnerText => "ownerText"u8;
    public static ReadOnlySpan<byte> ShortBylineText => "shortBylineText"u8;
    public static ReadOnlySpan<byte> ChannelThumbnailSupportedRenderers => "channelThumbnailSupportedRenderers"u8;
    public static ReadOnlySpan<byte> ChannelThumbnailWithLinkRenderer => "channelThumbnailWithLinkRenderer"u8;
    public static ReadOnlySpan<byte> OwnerBadges => "ownerBadges"u8;
    public static ReadOnlySpan<byte> MetadataBadgeRenderer => "metadataBadgeRenderer"u8;
    public static ReadOnlySpan<byte> Icon => "icon"u8;
    public static ReadOnlySpan<byte> IconType => "iconType"u8;
    public static ReadOnlySpan<byte> LengthText => "lengthText"u8;
    public static ReadOnlySpan<byte> ThumbnailViewModel => "thumbnailViewModel"u8;
    public static ReadOnlySpan<byte> Image => "image"u8;
    public static ReadOnlySpan<byte> Sources => "sources"u8;
    public static ReadOnlySpan<byte> SecondSubtitle => "secondSubtitle"u8;
    public static ReadOnlySpan<byte> Subtitle => "subtitle"u8;
    public static ReadOnlySpan<byte> Description => "description"u8;
    public static ReadOnlySpan<byte> MusicDescriptionShelfRenderer => "musicDescriptionShelfRenderer"u8;
    public static ReadOnlySpan<byte> ContentId => "contentId"u8;
    public static ReadOnlySpan<byte> VideoRenderer => "videoRenderer"u8;
    public static ReadOnlySpan<byte> ShortsLockupViewModel => "shortsLockupViewModel"u8;
    public static ReadOnlySpan<byte> ReelItemRenderer => "reelItemRenderer"u8;
    public static ReadOnlySpan<byte> LockupViewModel => "lockupViewModel"u8;
    public static ReadOnlySpan<byte> PlaylistRenderer => "playlistRenderer"u8;
    public static ReadOnlySpan<byte> ChannelRenderer => "channelRenderer"u8;
    public static ReadOnlySpan<byte> LockupMetadataViewModel => "lockupMetadataViewModel"u8;
    public static ReadOnlySpan<byte> Metadata => "metadata"u8;
    public static ReadOnlySpan<byte> Expire => "expire"u8;
    public static ReadOnlySpan<byte> PlaylistPanelVideoRenderer => "playlistPanelVideoRenderer"u8;
    public static ReadOnlySpan<byte> WatchEndpointMusicSupportedConfigs => "watchEndpointMusicSupportedConfigs"u8;
    public static ReadOnlySpan<byte> LongBylineText => "longBylineText"u8;
    public static ReadOnlySpan<byte> Badges => "badges"u8;
    public static ReadOnlySpan<byte> Style => "style"u8;
    public static ReadOnlySpan<byte> Index => "index"u8;
    public static ReadOnlySpan<byte> Contents => "contents"u8;
    public static ReadOnlySpan<byte> OnResponseReceivedActions => "onResponseReceivedActions"u8;
    public static ReadOnlySpan<byte> AppendContinuationItemsAction => "appendContinuationItemsAction"u8;
    public static ReadOnlySpan<byte> ContinuationItems => "continuationItems"u8;
    public static ReadOnlySpan<byte> TwoColumnWatchNextResults => "twoColumnWatchNextResults"u8;
    public static ReadOnlySpan<byte> Playlist => "playlist"u8;
    public static ReadOnlySpan<byte> OwnerName => "ownerName"u8;
    public static ReadOnlySpan<byte> TotalVideosText => "totalVideosText"u8;
    public static ReadOnlySpan<byte> VideoCountText => "videoCountText"u8;
    public static ReadOnlySpan<byte> ShowDialogCommand => "showDialogCommand"u8;
    public static ReadOnlySpan<byte> PanelLoadingStrategy => "panelLoadingStrategy"u8;
    public static ReadOnlySpan<byte> InlineContent => "inlineContent"u8;
    public static ReadOnlySpan<byte> DialogViewModel => "dialogViewModel"u8;
    public static ReadOnlySpan<byte> CustomContent => "customContent"u8;
    public static ReadOnlySpan<byte> ListViewModel => "listViewModel"u8;
    public static ReadOnlySpan<byte> ListItems => "listItems"u8;
    public static ReadOnlySpan<byte> ListItemViewModel => "listItemViewModel"u8;
    public static ReadOnlySpan<byte> RendererContext => "rendererContext"u8;
    public static ReadOnlySpan<byte> CommandContext => "commandContext"u8;
    public static ReadOnlySpan<byte> Items => "items"u8;
    public static ReadOnlySpan<byte> PrimaryContents => "primaryContents"u8;
    public static ReadOnlySpan<byte> SecondaryContents => "secondaryContents"u8;
    public static ReadOnlySpan<byte> TwoColumnSearchResultsRenderer => "twoColumnSearchResultsRenderer"u8;
    public static ReadOnlySpan<byte> SectionListRenderer => "sectionListRenderer"u8;
    public static ReadOnlySpan<byte> ItemSectionRenderer => "itemSectionRenderer"u8;
    public static ReadOnlySpan<byte> MusicShelfRenderer => "musicShelfRenderer"u8;
    public static ReadOnlySpan<byte> RichGridRenderer => "richGridRenderer"u8;
    public static ReadOnlySpan<byte> ShelfRenderer => "shelfRenderer"u8;
    public static ReadOnlySpan<byte> TabbedSearchResultsRenderer => "tabbedSearchResultsRenderer"u8;
    public static ReadOnlySpan<byte> TabRenderer => "tabRenderer"u8;
    public static ReadOnlySpan<byte> Tabs => "tabs"u8;
    public static ReadOnlySpan<byte> OnResponseReceivedCommands => "onResponseReceivedCommands"u8;
    public static ReadOnlySpan<byte> ContinuationContents => "continuationContents"u8;
    public static ReadOnlySpan<byte> MusicShelfContinuation => "musicShelfContinuation"u8;
    public static ReadOnlySpan<byte> SectionListContinuation => "sectionListContinuation"u8;
    public static ReadOnlySpan<byte> ItemSectionContinuation => "itemSectionContinuation"u8;
    public static ReadOnlySpan<byte> Target => "target"u8;
    public static ReadOnlySpan<byte> SingleColumnBrowseResultsRenderer => "singleColumnBrowseResultsRenderer"u8;
    public static ReadOnlySpan<byte> GridRenderer => "gridRenderer"u8;
    public static ReadOnlySpan<byte> MusicCarouselShelfRenderer => "musicCarouselShelfRenderer"u8;
    public static ReadOnlySpan<byte> MusicTwoRowItemRenderer => "musicTwoRowItemRenderer"u8;
    public static ReadOnlySpan<byte> MusicCarouselShelfBasicHeaderRenderer => "musicCarouselShelfBasicHeaderRenderer"u8;
    public static ReadOnlySpan<byte> Album => "album"u8;
    public static ReadOnlySpan<byte> Sidebar => "sidebar"u8;
    public static ReadOnlySpan<byte> PlaylistSidebarRenderer => "playlistSidebarRenderer"u8;
    public static ReadOnlySpan<byte> PlaylistSidebarPrimaryInfoRenderer => "playlistSidebarPrimaryInfoRenderer"u8;
    public static ReadOnlySpan<byte> PlaylistSidebarSecondaryInfoRenderer => "playlistSidebarSecondaryInfoRenderer"u8;
    public static ReadOnlySpan<byte> PrivacyForm => "privacyForm"u8;
    public static ReadOnlySpan<byte> DropdownFormFieldRenderer => "dropdownFormFieldRenderer"u8;
    public static ReadOnlySpan<byte> TitleForm => "titleForm"u8;
    public static ReadOnlySpan<byte> InlineFormRenderer => "inlineFormRenderer"u8;
    public static ReadOnlySpan<byte> FormField => "formField"u8;
    public static ReadOnlySpan<byte> TextInputFormFieldRenderer => "textInputFormFieldRenderer"u8;
    public static ReadOnlySpan<byte> Value => "value"u8;
    public static ReadOnlySpan<byte> VideoOwner => "videoOwner"u8;
    public static ReadOnlySpan<byte> VideoOwnerRenderer => "videoOwnerRenderer"u8;
    public static ReadOnlySpan<byte> DescriptionForm => "descriptionForm"u8;
    public static ReadOnlySpan<byte> Stats => "stats"u8;
    public static ReadOnlySpan<byte> ThumbnailRenderer => "thumbnailRenderer"u8;
    public static ReadOnlySpan<byte> PlaylistVideoThumbnailRenderer => "playlistVideoThumbnailRenderer"u8;
    public static ReadOnlySpan<byte> PlaylistCustomThumbnailRenderer => "playlistCustomThumbnailRenderer"u8;
    public static ReadOnlySpan<byte> TwoColumnBrowseResultsRenderer => "twoColumnBrowseResultsRenderer"u8;
    public static ReadOnlySpan<byte> IsSelected => "isSelected"u8;
    public static ReadOnlySpan<byte> PlaylistPrivacy => "playlistPrivacy"u8;
    public static ReadOnlySpan<byte> Int32Value => "int32Value"u8;
    public static ReadOnlySpan<byte> FrameworkUpdates => "frameworkUpdates"u8;
    public static ReadOnlySpan<byte> EntityBatchUpdate => "entityBatchUpdate"u8;
    public static ReadOnlySpan<byte> Mutations => "mutations"u8;
    public static ReadOnlySpan<byte> Payload => "payload"u8;
    public static ReadOnlySpan<byte> PageHeaderEntity => "pageHeaderEntity"u8;
    public static ReadOnlySpan<byte> PageHeaderViewModel => "pageHeaderViewModel"u8;
    public static ReadOnlySpan<byte> ContentMetadataViewModel => "contentMetadataViewModel"u8;
    public static ReadOnlySpan<byte> MetadataRows => "metadataRows"u8;
    public static ReadOnlySpan<byte> MetadataParts => "metadataParts"u8;
    public static ReadOnlySpan<byte> PlayerLegacyDesktopYpcTrailerRenderer => "playerLegacyDesktopYpcTrailerRenderer"u8;
    public static ReadOnlySpan<byte> TrailerVideoId => "trailerVideoId"u8;
    public static ReadOnlySpan<byte> YpcTrailerRenderer => "ypcTrailerRenderer"u8;
    public static ReadOnlySpan<byte> PlayerVars => "playerVars"u8;
    public static ReadOnlySpan<byte> PlayerResponseProperty => "playerResponse"u8;
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

    /// <summary>Иконка значка официального верифицированного аудио-исполнителя.</summary>
    public const string AudioBadge = "AUDIO_BADGE";

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