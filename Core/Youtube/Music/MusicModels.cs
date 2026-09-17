using System.Runtime.CompilerServices;
using System.Text.Json;
using LMP.Core.Youtube.Bridge;
using LMP.Core.Helpers.Extensions;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Youtube.Music;

public sealed class MusicShelf(string title, List<MusicItem> items)
{
    public string Title { get; } = title;
    public List<MusicItem> Items { get; } = items;
}

public sealed class MusicItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Author { get; set; }
    public string? Album { get; set; }
    public TimeSpan? Duration { get; set; }
    public IReadOnlyList<Thumbnail> Thumbnails { get; set; } = [];
    public string Type { get; set; } = "Song";

    /// <summary>
    /// YouTube playlist item ID, required for removing tracks from playlists.
    /// Only populated when item is loaded from a playlist context.
    /// </summary>
    public string? SetVideoId { get; set; }
}

/// <summary>
/// Полный снимок плейлиста из YouTube Music (WEB_REMIX browse).
/// </summary>
public sealed class FullPlaylistSyncData
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? ThumbnailUrl { get; set; }
    public long? ViewCount { get; set; }

    /// <summary>
    /// Дата последнего обновления, распарсенная из subtitle runs.
    /// </summary>
    public DateOnly? ReleaseDate { get; set; }

    public List<RemoteTrackInfo> Tracks { get; set; } = [];
}

internal sealed class MusicBrowseResponse
{
    public List<MusicShelf> Shelves { get; } = [];
    public string? Title { get; private set; }
    public string? ContinuationToken { get; private set; }

    public MusicBrowseResponse(JsonElement root)
    {
        // 1. Заголовок
        var header = root.GetPropertyOrNull("header")?.GetPropertyOrNull("musicDetailHeaderRenderer")
            ?? root.GetPropertyOrNull("header")?.GetPropertyOrNull("musicResponsiveHeaderRenderer");

        if (header.HasValue)
        {
            Title = header.Value.GetPropertyOrNull("title")?.GetPropertyOrNull("runs")
                        ?.GetFirstArrayElementOrNull()?.GetPropertyOrNull("text")?.GetStringOrNull()
                    ?? header.Value.GetPropertyOrNull("title")?.GetPropertyOrNull("simpleText")?.GetStringOrNull();
        }

        // 2. Continuation
        if (TryParseContinuation(root))
            return;

        // 3. Browse
        var sectionList = root.GetPropertyOrNull("contents")
            ?.GetPropertyOrNull("twoColumnBrowseResultsRenderer")
            ?.GetPropertyOrNull("secondaryContents")
            ?.GetPropertyOrNull("sectionListRenderer")
            ?.GetPropertyOrNull("contents");

        if (sectionList == null)
        {
            var tabs = root.GetPropertyOrNull("contents")
                ?.GetPropertyOrNull("singleColumnBrowseResultsRenderer")
                ?.GetPropertyOrNull("tabs");

            if (tabs != null)
            {
                var firstTab = tabs.Value.GetFirstArrayElementOrNull();
                sectionList = firstTab?.GetPropertyOrNull("tabRenderer")
                    ?.GetPropertyOrNull("content")
                    ?.GetPropertyOrNull("sectionListRenderer")
                    ?.GetPropertyOrNull("contents");
            }
        }

        if (sectionList != null)
        {
            foreach (var section in sectionList.Value.EnumerateArrayOrEmpty())
            {
                if (section.TryGetProperty("musicPlaylistShelfRenderer", out var playlistShelf))
                {
                    ParseShelfContent(playlistShelf, "Tracks");

                    ContinuationToken ??= playlistShelf.GetPropertyOrNull("continuations")
                        ?.GetFirstArrayElementOrNull()
                        ?.GetPropertyOrNull("nextContinuationData")
                        ?.GetPropertyOrNull("continuation")?.GetStringOrNull();
                }
                else if (section.TryGetProperty("gridRenderer", out var grid))
                {
                    ParseGridContent(grid, "Library");
                }
                else if (section.TryGetProperty("musicCarouselShelfRenderer", out var carousel))
                {
                    ParseShelfContent(carousel, null);
                }
            }
        }
    }

    private bool TryParseContinuation(JsonElement root)
    {
        bool foundAny = false;

        var actions = root.GetPropertyOrNull("onResponseReceivedActions");
        if (actions != null)
        {
            foreach (var action in actions.Value.EnumerateArrayOrEmpty())
            {
                var continuationItems = action.GetPropertyOrNull("appendContinuationItemsAction")
                    ?.GetPropertyOrNull("continuationItems");
                if (continuationItems != null)
                {
                    ParseMixedContent(continuationItems.Value, "Continuation");
                    foundAny = true;
                }
            }
        }

        var contContents = root.GetPropertyOrNull("continuationContents")
            ?.GetPropertyOrNull("musicPlaylistShelfContinuation");
        if (contContents != null)
        {
            var contents = contContents.Value.GetPropertyOrNull("contents");
            if (contents != null)
                ParseMixedContent(contents.Value, "Continuation");

            ContinuationToken ??= contContents.Value.GetPropertyOrNull("continuations")
                ?.GetFirstArrayElementOrNull()
                ?.GetPropertyOrNull("nextContinuationData")
                ?.GetPropertyOrNull("continuation")?.GetStringOrNull();

            foundAny = true;
        }

        return foundAny;
    }

    private void ParseMixedContent(JsonElement itemsArray, string? shelfTitle)
    {
        var tracks = new List<MusicItem>(16);

        foreach (var item in itemsArray.EnumerateArrayOrEmpty())
        {
            if (item.TryGetProperty("musicResponsiveListItemRenderer", out var trackJson))
            {
                var musicItem = ParseMusicItem(trackJson);
                if (musicItem != null) tracks.Add(musicItem);
                continue;
            }

            if (item.TryGetProperty("musicTwoRowItemRenderer", out var twoRowJson))
            {
                var musicItem = ParseTwoRowItem(twoRowJson);
                if (musicItem != null) tracks.Add(musicItem);
                continue;
            }

            if (item.TryGetProperty("continuationItemRenderer", out var contItem))
            {
                var token = contItem.GetPropertyOrNull("continuationEndpoint")
                    ?.GetPropertyOrNull("continuationCommand")
                    ?.GetPropertyOrNull("token")?.GetStringOrNull();

                if (!string.IsNullOrEmpty(token))
                    ContinuationToken = token;
            }
        }

        if (tracks.Count > 0)
            Shelves.Add(new MusicShelf(shelfTitle ?? "Music", tracks));
    }

    private void ParseShelfContent(JsonElement shelf, string? title)
    {
        var displayTitle = title;
        displayTitle ??= shelf.GetPropertyOrNull("header")
                ?.GetPropertyOrNull("musicCarouselShelfBasicHeaderRenderer")
                ?.GetPropertyOrNull("title")
                ?.GetPropertyOrNull("runs")
                ?.GetFirstArrayElementOrNull()
                ?.GetPropertyOrNull("text")?.GetStringOrNull()
                ?? "Tracks";

        var contents = shelf.GetPropertyOrNull("contents");
        if (contents != null)
            ParseMixedContent(contents.Value, displayTitle);
    }

    private void ParseGridContent(JsonElement grid, string title)
    {
        var items = grid.GetPropertyOrNull("items");
        if (items != null) ParseMixedContent(items.Value, title);
    }

    private static MusicItem? ParseMusicItem(JsonElement json)
    {
        var playlistItemData = json.GetPropertyOrNull("playlistItemData");

        var id = playlistItemData?.GetPropertyOrNull("videoId")?.GetStringOrNull();
        id ??= json.FindFirstDescendantProperty("videoId")?.GetStringOrNull();

        if (id == null) return null;

        var setVideoId = playlistItemData?.GetPropertyOrNull("playlistSetVideoId")?.GetStringOrNull()
            ?? playlistItemData?.GetPropertyOrNull("setVideoId")?.GetStringOrNull()
            ?? json.GetPropertyOrNull("playlistSetVideoId")?.GetStringOrNull()
            ?? json.FindFirstDescendantProperty("playlistSetVideoId")?.GetStringOrNull()
            ?? json.FindFirstDescendantProperty("setVideoId")?.GetStringOrNull();

        var flexCols = json.GetPropertyOrNull("flexColumns");

        var title = flexCols?.GetArrayElementOrNull(0)
            ?.GetPropertyOrNull("musicResponsiveListItemFlexColumnRenderer")
            ?.GetPropertyOrNull("text")
            ?.GetPropertyOrNull("runs")
            ?.GetFirstArrayElementOrNull()
            ?.GetPropertyOrNull("text")?.GetStringOrNull() ?? "";

        var metaRuns = flexCols?.GetArrayElementOrNull(1)
            ?.GetPropertyOrNull("musicResponsiveListItemFlexColumnRenderer")
            ?.GetPropertyOrNull("text")
            ?.GetPropertyOrNull("runs");

        string? author = null;
        string? album = null;

        if (metaRuns != null)
        {
            foreach (var run in metaRuns.Value.EnumerateArrayOrEmpty())
            {
                var text = run.GetPropertyOrNull("text")?.GetStringOrNull();
                if (text == null) continue;

                var nav = run.GetPropertyOrNull("navigationEndpoint");
                if (nav != null)
                {
                    var pageType = nav.Value.GetPropertyOrNull("browseEndpoint")
                        ?.GetPropertyOrNull("browseEndpointContextSupportedConfigs")
                        ?.GetPropertyOrNull("browseEndpointContextMusicConfig")
                        ?.GetPropertyOrNull("pageType")?.GetStringOrNull();

                    if (pageType is "MUSIC_PAGE_TYPE_ARTIST" or "MUSIC_PAGE_TYPE_USER_CHANNEL")
                        author = text;
                    else if (pageType == "MUSIC_PAGE_TYPE_ALBUM")
                        album = text;
                }
                else if (author == null && !ContainsAnyOf(text, "views", "Song", "Video", ":"))
                {
                    author = text;
                }
            }
        }

        var durationText = json.GetPropertyOrNull("fixedColumns")
            ?.GetFirstArrayElementOrNull()
            ?.GetPropertyOrNull("musicResponsiveListItemFixedColumnRenderer")
            ?.GetPropertyOrNull("text")
            ?.GetPropertyOrNull("runs")
            ?.GetFirstArrayElementOrNull()
            ?.GetPropertyOrNull("text")?.GetStringOrNull();

        TimeSpan? duration = durationText != null
            ? YoutubeClientUtils.DurationParser.Parse(durationText)
            : null;

        var thumbs = ExtractThumbnails(
            json.GetPropertyOrNull("thumbnail")
                ?.GetPropertyOrNull("musicThumbnailRenderer")
                ?.GetPropertyOrNull("thumbnail")
                ?.GetPropertyOrNull("thumbnails"));

        return new MusicItem
        {
            Id = id,
            Title = title,
            Author = author,
            Album = album,
            Duration = duration,
            Thumbnails = thumbs,
            Type = "Song",
            SetVideoId = setVideoId
        };
    }

    private static MusicItem? ParseTwoRowItem(JsonElement json)
    {
        var title = json.GetPropertyOrNull("title")
            ?.GetPropertyOrNull("runs")
            ?.GetFirstArrayElementOrNull()
            ?.GetPropertyOrNull("text")?.GetStringOrNull() ?? "";

        var subtitle = json.GetPropertyOrNull("subtitle")
            ?.GetPropertyOrNull("runs")
            ?.GetFirstArrayElementOrNull()
            ?.GetPropertyOrNull("text")?.GetStringOrNull();

        var nav = json.GetPropertyOrNull("navigationEndpoint");
        var browseId = nav?.GetPropertyOrNull("browseEndpoint")
            ?.GetPropertyOrNull("browseId")?.GetStringOrNull();
        var watchId = nav?.GetPropertyOrNull("watchEndpoint")
            ?.GetPropertyOrNull("videoId")?.GetStringOrNull();

        string id;
        string type;

        if (browseId != null)
        {
            id = browseId;
            var span = id.AsSpan();
            if (span.StartsWith("VL") && span.Length > 2)
                id = id[2..];

            span = id.AsSpan();
            if (span.StartsWith("PL") || span.StartsWith("RD") || id == "LM")
                type = "Playlist";
            else if (span.StartsWith("MPRE") || span.StartsWith("OLAK"))
                type = "Album";
            else if (span.StartsWith("UC"))
                type = "Artist";
            else
                type = "Video";
        }
        else if (watchId != null)
        {
            id = watchId;
            type = "Song";
        }
        else
        {
            return null;
        }

        if (string.IsNullOrEmpty(id)) return null;

        var thumbs = ExtractThumbnails(
            json.GetPropertyOrNull("thumbnailRenderer")
                ?.GetPropertyOrNull("musicThumbnailRenderer")
                ?.GetPropertyOrNull("thumbnail")
                ?.GetPropertyOrNull("thumbnails"));

        return new MusicItem
        {
            Id = id,
            Title = title,
            Author = subtitle,
            Thumbnails = thumbs,
            Type = type
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Thumbnail[] ExtractThumbnails(JsonElement? thumbsElement)
    {
        if (thumbsElement == null) return [];

        var len = thumbsElement.Value.GetArrayLength();
        if (len == 0) return [];

        var result = new Thumbnail[len];
        int idx = 0;
        foreach (var j in thumbsElement.Value.EnumerateArray())
        {
            var td = new ThumbnailData(j);
            result[idx++] = new Thumbnail(td.Url ?? "", new Resolution(td.Width ?? 0, td.Height ?? 0));
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ContainsAnyOf(string text, string a, string b, string c, string d)
    {
        var span = text.AsSpan();
        return span.Contains(a, StringComparison.Ordinal) ||
               span.Contains(b, StringComparison.Ordinal) ||
               span.Contains(c, StringComparison.Ordinal) ||
               span.Contains(d, StringComparison.Ordinal);
    }
}