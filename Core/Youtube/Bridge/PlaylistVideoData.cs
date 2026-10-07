using System.Globalization;
using System.Text.Json;
using LMP.Core.Helpers.Extensions;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Youtube.Bridge;

/// <summary>
/// Плоская immutable-модель данных видеоролика внутри плейлиста YouTube.
/// Извлекает все поля при инициализации, не удерживая ссылку на <see cref="JsonElement"/> в памяти.
/// </summary>
internal sealed class PlaylistVideoData
{
    /// <summary>
    /// Порядковый номер видео в плейлисте (0-based индекс).
    /// </summary>
    public int? Index { get; init; }

    /// <summary>
    /// Идентификатор видеоролика YouTube (11 символов).
    /// </summary>
    public string? Id { get; init; }

    /// <summary>
    /// Текст заголовка видео.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Имя автора или канала, опубликовавшего видео.
    /// </summary>
    public string? Author { get; init; }

    /// <summary>
    /// Идентификатор канала автора видео.
    /// </summary>
    public string? ChannelId { get; init; }

    /// <summary>
    /// Длительность видеоролика.
    /// </summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>
    /// Флаг схемной принадлежности элемента к музыкальному контенту по строгим маркерам InnerTube API.
    /// </summary>
    public bool IsMusic { get; init; }

    /// <summary>
    /// Идентификатор категории YouTube видеоролика (например, "10" для Music).
    /// </summary>
    public string? CategoryId { get; init; }

    /// <summary>
    /// Список доступных миниатюр видеоролика.
    /// </summary>
    public IReadOnlyList<ThumbnailData> Thumbnails { get; init; } = [];

    /// <summary>
    /// Инициализирует плоскую модель трека из JSON-элемента <c>playlistVideoRenderer</c> или <c>playlistPanelVideoRenderer</c>.
    /// </summary>
    /// <param name="content">JSON-элемент видеоролика.</param>
    public PlaylistVideoData(JsonElement content)
    {
        Index = content
            .GetPropertyOrNull(InnerTubeTokens.NavigationEndpoint)
            ?.GetPropertyOrNull(InnerTubeTokens.WatchEndpoint)
            ?.GetPropertyOrNull(InnerTubeTokens.Index)
            ?.GetInt32OrNull();

        Id = content.GetPropertyOrNull(InnerTubeTokens.VideoId)?.GetStringOrNull();

        var titleEl = content.GetPropertyOrNull(InnerTubeTokens.Title);
        Title = titleEl is null
            ? null
            : titleEl.Value.GetPropertyOrNull(InnerTubeTokens.SimpleText)?.GetStringOrNull()
                ?? YoutubeParsingHelpers.ConcatTextRuns(titleEl.Value.GetPropertyOrNull(InnerTubeTokens.Runs));

        var authorDetails =
            content.GetPropertyOrNull(InnerTubeTokens.LongBylineText)
                ?.GetPropertyOrNull(InnerTubeTokens.Runs)
                ?.GetArrayElementOrNull(0)
            ?? content.GetPropertyOrNull(InnerTubeTokens.ShortBylineText)
                ?.GetPropertyOrNull(InnerTubeTokens.Runs)
                ?.GetArrayElementOrNull(0);

        Author = authorDetails?.GetPropertyOrNull(InnerTubeTokens.Text)?.GetStringOrNull();

        ChannelId =
            authorDetails
                ?.GetPropertyOrNull(InnerTubeTokens.NavigationEndpoint)
                ?.GetPropertyOrNull(InnerTubeTokens.BrowseEndpoint)
                ?.GetPropertyOrNull(InnerTubeTokens.BrowseId)
                ?.GetStringOrNull()
            // Some videos have multiple authors. Our current data model does not support that, so we only
            // extract the first one, since it's the channel that actually uploaded the video.
            ?? authorDetails
                ?.GetPropertyOrNull(InnerTubeTokens.NavigationEndpoint)
                ?.GetPropertyOrNull(InnerTubeTokens.ShowDialogCommand)
                ?.GetPropertyOrNull(InnerTubeTokens.PanelLoadingStrategy)
                ?.GetPropertyOrNull(InnerTubeTokens.InlineContent)
                ?.GetPropertyOrNull(InnerTubeTokens.DialogViewModel)
                ?.GetPropertyOrNull(InnerTubeTokens.CustomContent)
                ?.GetPropertyOrNull(InnerTubeTokens.ListViewModel)
                ?.GetPropertyOrNull(InnerTubeTokens.ListItems)
                ?.EnumerateArrayOrNull()
                ?.FirstOrNull()
                ?.GetPropertyOrNull(InnerTubeTokens.ListItemViewModel)
                ?.GetPropertyOrNull(InnerTubeTokens.RendererContext)
                ?.GetPropertyOrNull(InnerTubeTokens.CommandContext)
                ?.GetPropertyOrNull(InnerTubeTokens.OnTap)
                ?.GetPropertyOrNull(InnerTubeTokens.InnertubeCommand)
                ?.GetPropertyOrNull(InnerTubeTokens.BrowseEndpoint)
                ?.GetPropertyOrNull(InnerTubeTokens.BrowseId)
                ?.GetStringOrNull();

        var raw = content.GetPropertyOrNull(InnerTubeTokens.LengthSeconds)?.GetStringOrNull();
        if (raw is not null && double.TryParse(raw, CultureInfo.InvariantCulture, out var seconds))
        {
            Duration = TimeSpan.FromSeconds(seconds);
        }
        else
        {
            var simpleText = content
                .GetPropertyOrNull(InnerTubeTokens.LengthText)
                ?.GetPropertyOrNull(InnerTubeTokens.SimpleText)
                ?.GetStringOrNull();

            if (simpleText is not null)
            {
                Duration = YoutubeClientUtils.DurationParser.Parse(simpleText);
            }
            else
            {
                var text = YoutubeParsingHelpers.ConcatTextRuns(
                    content.GetPropertyOrNull(InnerTubeTokens.LengthText)?.GetPropertyOrNull(InnerTubeTokens.Runs));
                Duration = text is not null ? YoutubeClientUtils.DurationParser.Parse(text) : null;
            }
        }

        var thumbsArray = content
            .GetPropertyOrNull(InnerTubeTokens.Thumbnail)
            ?.GetPropertyOrNull(InnerTubeTokens.Thumbnails);

        if (thumbsArray is { ValueKind: JsonValueKind.Array } arr)
        {
            int len = arr.GetArrayLength();
            if (len > 0)
            {
                var result = new ThumbnailData[len];
                for (int i = 0; i < len; i++)
                    result[i] = new ThumbnailData(arr[i]);
                Thumbnails = result;
            }
        }

        CategoryId = content.GetPropertyOrNull(InnerTubeTokens.CategoryId)?.GetStringOrNull();

        IsMusic = DetermineIsMusic(content, authorDetails, CategoryId);
    }
    private static bool DetermineIsMusic(JsonElement content, JsonElement? authorDetails, string? categoryId)
    {
        if (string.Equals(categoryId, InnerTubeConstants.MusicCategoryId, StringComparison.Ordinal))
            return true;

        if (content.TryGetProperty(InnerTubeTokens.MusicResponsiveListItemRenderer, out _))
            return true;

        var navEndpoint = content.GetPropertyOrNull(InnerTubeTokens.NavigationEndpoint);
        if (navEndpoint?.TryGetProperty(InnerTubeTokens.WatchEndpoint, out var watchEndpoint) == true &&
            watchEndpoint.TryGetProperty(InnerTubeTokens.WatchEndpointMusicSupportedConfigs, out _))
        {
            return true;
        }

        var browseConfig = authorDetails
            ?.GetPropertyOrNull(InnerTubeTokens.NavigationEndpoint)
            ?.GetPropertyOrNull(InnerTubeTokens.BrowseEndpoint)
            ?.GetPropertyOrNull(InnerTubeTokens.BrowseEndpointContextSupportedConfigs)
            ?.GetPropertyOrNull(InnerTubeTokens.BrowseEndpointContextMusicConfig);

        if (browseConfig is not null)
        {
            var pageType = browseConfig.Value.GetPropertyOrNull(InnerTubeTokens.PageType)?.GetStringOrNull();
            if (string.Equals(pageType, InnerTubeConstants.PageTypeAlbum, StringComparison.Ordinal) ||
                string.Equals(pageType, InnerTubeConstants.PageTypeArtist, StringComparison.Ordinal))
            {
                return true;
            }
        }

        var badges = content.GetPropertyOrNull(InnerTubeTokens.OwnerBadges) ?? content.GetPropertyOrNull(InnerTubeTokens.Badges);
        if (badges?.ValueKind == JsonValueKind.Array)
        {
            foreach (var badge in badges.Value.EnumerateArray())
            {
                var style = badge
                    .GetPropertyOrNull(InnerTubeTokens.MetadataBadgeRenderer)
                    ?.GetPropertyOrNull(InnerTubeTokens.Style)
                    ?.GetStringOrNull();

                if (string.Equals(style, InnerTubeConstants.BadgeVerifiedArtist, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }
}