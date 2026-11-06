using System.Globalization;
using System.Text.Json;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Youtube.Bridge;

/// <summary>
/// Flat immutable data model of a YouTube playlist video.
/// Extracts all fields during initialization without retaining a reference to <see cref="JsonElement"/> in memory.
/// </summary>
internal sealed class PlaylistVideoData
{
    /// <summary>
    /// Sequential index of the video in the playlist (0-based index).
    /// </summary>
    public int? Index { get; init; }

    /// <summary>
    /// YouTube video identifier (11 characters).
    /// </summary>
    public string? Id { get; init; }

    /// <summary>
    /// Video title text.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Name of the author or channel that published the video.
    /// </summary>
    public string? Author { get; init; }

    /// <summary>
    /// Channel identifier of the video author.
    /// </summary>
    public string? ChannelId { get; init; }

    /// <summary>
    /// Duration of the video.
    /// </summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>
    /// Flag indicating whether the element belongs to music content based on strict InnerTube API markers.
    /// </summary>
    public bool IsMusic { get; init; }

    /// <summary>
    /// YouTube video category identifier (e.g., "10" for Music).
    /// </summary>
    public string? CategoryId { get; init; }

    /// <summary>
    /// List of available video thumbnails.
    /// </summary>
    public IReadOnlyList<ThumbnailData> Thumbnails { get; init; } = [];

    /// <summary>
    /// Unique identifier of the video entry in the playlist (for cloud deletion and reordering).
    /// </summary>
    public string? SetVideoId { get; init; }

    /// <summary>
    /// Initializes the flat track model from a <c>playlistVideoRenderer</c> or <c>playlistPanelVideoRenderer</c> JSON element.
    /// </summary>
    /// <param name="content">The JSON element of the video.</param>
    public PlaylistVideoData(JsonElement content)
    {
        Index = content
            .GetPropertyOrNull(InnerTubeTokens.NavigationEndpoint)
            ?.GetPropertyOrNull(InnerTubeTokens.WatchEndpoint)
            ?.GetPropertyOrNull(InnerTubeTokens.Index)
            ?.GetInt32OrNull();

        Id = content.GetPropertyOrNull(InnerTubeTokens.VideoId)?.GetStringOrNull();
        SetVideoId = content.GetPropertyOrNull(InnerTubeTokens.SetVideoId)?.GetStringOrNull()
            ?? content.GetPropertyOrNull(InnerTubeTokens.PlaylistSetVideoId)?.GetStringOrNull();

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