using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Youtube.Bridge;

/// <summary>
/// Represents a YouTube API response for a playlist browse page request.
/// Pure lightweight DTO model: extracts data during parsing and does not hold <see cref="JsonDocument"/> in memory.
/// </summary>
internal partial class PlaylistBrowseResponse : IPlaylistData
{
    private const string PrefixLastUpdatedOn = "Last updated on ";
    private const string PrefixUpdated = "Updated ";
    public bool IsAvailable { get; init; }
    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? ChannelId { get; init; }
    public string? Description { get; init; }
    public int? Count { get; init; }

    /// <summary>
    /// View count of the playlist.
    /// Supports the new UI-rendered header object (frameworkUpdates) and the classic sidebar.
    /// </summary>
    public long? ViewCount { get; init; }

    /// <summary>
    /// Playlist creation or last update date (string representation).
    /// Extracts a clean date value without localized prefixes ("Updated").
    /// Supports the new header UI (frameworkUpdates) and parsing of the third sidebar
    /// stat element in the classic sidebar.
    /// </summary>
    public DateOnly? ReleaseDate { get; init; }

    /// <summary>
    /// Calculated playlist access level.
    /// For owned playlists, it is obtained from the privacy dropdown; for others, it remains Unknown.
    /// </summary>
    public PlaylistVisibility Visibility { get; init; } = PlaylistVisibility.Unknown;

    /// <summary>
    /// Indicates whether the playlist belongs to the current account and is editable
    /// (presence of privacy, title, or description edit forms in the sidebar).
    /// </summary>
    public bool IsEditable { get; init; }

    public IReadOnlyList<ThumbnailData> Thumbnails { get; init; } = [];

    /// <summary>
    /// List of videos in the current playlist batch.
    /// </summary>
    public IReadOnlyList<PlaylistVideoData> Videos { get; init; } = [];

    /// <summary>
    /// Pagination token for fetching the next page of the video list.
    /// </summary>
    public string? ContinuationToken { get; init; }

    /// <summary>
    /// User session data for context tracking.
    /// </summary>
    public string? VisitorData { get; init; }

    /// <summary>
    /// Initializes the DTO model by extracting all properties from the root JSON element in a single pass.
    /// </summary>
    /// <param name="content">Root JSON element of the Browse response.</param>
    public PlaylistBrowseResponse(JsonElement content)
    {
        var sidebar = content
            .GetPropertyOrNull("sidebar")
            ?.GetPropertyOrNull("playlistSidebarRenderer")
            ?.GetPropertyOrNull("items");

        var sidebarPrimary = sidebar
            ?.EnumerateArrayOrNull()
            ?.ElementAtOrNull(0)
            ?.GetPropertyOrNull("playlistSidebarPrimaryInfoRenderer");

        var sidebarSecondary = sidebar
            ?.EnumerateArrayOrNull()
            ?.ElementAtOrNull(1)
            ?.GetPropertyOrNull("playlistSidebarSecondaryInfoRenderer");

        /// <summary>
        /// Playlist privacy form renderer. Typically available for playlists
        /// belonging to the currently authenticated user.
        /// </summary>
        var privacyDropdownRenderer = sidebarPrimary
            ?.GetPropertyOrNull("privacyForm")
            ?.GetPropertyOrNull("dropdownFormFieldRenderer");

        var effectivePlaylistContents = ResolvePlaylistContents(content);

        IsAvailable = sidebar is not null || effectivePlaylistContents is not null;

        IsEditable = privacyDropdownRenderer is not null
                     || sidebarPrimary?.GetPropertyOrNull("titleForm") is not null
                     || sidebarPrimary?.GetPropertyOrNull("descriptionForm") is not null;

        if (TryParseVisibility(privacyDropdownRenderer, out var visibility))
            Visibility = visibility;

        Title =
            sidebarPrimary
                ?.GetPropertyOrNull("title")
                ?.GetPropertyOrNull("simpleText")
                ?.GetStringOrNull()
            ?? YoutubeParsingHelpers.ConcatTextRuns(
                sidebarPrimary?.GetPropertyOrNull("title")?.GetPropertyOrNull("runs"))
            ?? sidebarPrimary
                ?.GetPropertyOrNull("titleForm")
                ?.GetPropertyOrNull("inlineFormRenderer")
                ?.GetPropertyOrNull("formField")
                ?.GetPropertyOrNull("textInputFormFieldRenderer")
                ?.GetPropertyOrNull("value")
                ?.GetStringOrNull()
            ?? content
                .GetPropertyOrNull("header")
                ?.GetPropertyOrNull("playlistHeaderRenderer")
                ?.GetPropertyOrNull("title")
                ?.GetPropertyOrNull("simpleText")
                ?.GetStringOrNull();

        var authorDetails =
            sidebarSecondary?.GetPropertyOrNull("videoOwner")?.GetPropertyOrNull("videoOwnerRenderer");

        Author =
            authorDetails
                ?.GetPropertyOrNull("title")
                ?.GetPropertyOrNull("simpleText")
                ?.GetStringOrNull()
            ?? YoutubeParsingHelpers.ConcatTextRuns(
                authorDetails?.GetPropertyOrNull("title")?.GetPropertyOrNull("runs"))
            ?? content
                .GetPropertyOrNull("header")
                ?.GetPropertyOrNull("playlistHeaderRenderer")
                ?.GetPropertyOrNull("ownerText")
                ?.GetPropertyOrNull("runs")
                ?.GetArrayElementOrNull(0)
                ?.GetPropertyOrNull("text")
                ?.GetStringOrNull();

        ChannelId =
            authorDetails
                ?.GetPropertyOrNull("navigationEndpoint")
                ?.GetPropertyOrNull("browseEndpoint")
                ?.GetPropertyOrNull("browseId")
                ?.GetStringOrNull();

        Description =
            sidebarPrimary
                ?.GetPropertyOrNull("description")
                ?.GetPropertyOrNull("simpleText")
                ?.GetStringOrNull()
            ?? YoutubeParsingHelpers.ConcatTextRuns(
                sidebarPrimary?.GetPropertyOrNull("description")?.GetPropertyOrNull("runs"))
            ?? sidebarPrimary
                ?.GetPropertyOrNull("descriptionForm")
                ?.GetPropertyOrNull("inlineFormRenderer")
                ?.GetPropertyOrNull("formField")
                ?.GetPropertyOrNull("textInputFormFieldRenderer")
                ?.GetPropertyOrNull("value")
                ?.GetStringOrNull();

        var stats = sidebarPrimary?.GetPropertyOrNull("stats");
        if (stats?.ValueKind == JsonValueKind.Array && stats.Value.GetArrayLength() > 0)
        {
            var text = GetTextFromStat(stats.Value[0]);
            if (text != null)
            {
                var val = YoutubeParsingHelpers.ParseLongFromText(text);
                if (val.HasValue)
                    Count = (int)val.Value;
            }
        }

        ViewCount = ExtractViewCount(content, sidebarPrimary);
        ReleaseDate = ExtractReleaseDate(content, sidebarPrimary);

        var thumbsElement = sidebarPrimary
            ?.GetPropertyOrNull("thumbnailRenderer")
            ?.GetPropertyOrNull("playlistVideoThumbnailRenderer")
            ?.GetPropertyOrNull("thumbnail")
            ?.GetPropertyOrNull("thumbnails")
            ?? sidebarPrimary
            ?.GetPropertyOrNull("thumbnailRenderer")
            ?.GetPropertyOrNull("playlistCustomThumbnailRenderer")
            ?.GetPropertyOrNull("thumbnail")
            ?.GetPropertyOrNull("thumbnails");

        if (thumbsElement is { ValueKind: JsonValueKind.Array } thumbsArray)
        {
            int len = thumbsArray.GetArrayLength();
            if (len > 0)
            {
                var result = new ThumbnailData[len];
                for (int i = 0; i < len; i++)
                    result[i] = new ThumbnailData(thumbsArray[i]);
                Thumbnails = result;
            }
        }

        Videos = ExtractVideos(effectivePlaylistContents);
        ContinuationToken = ExtractContinuationToken(content, effectivePlaylistContents);
        VisitorData = content.GetVisitorData();
    }

    /// <summary>
    /// Performs a structural search for <c>playlistVideoListRenderer</c> across all tabs,
    /// sections, and items of the browse response.
    /// </summary>
    private static JsonElement? ResolvePlaylistContents(JsonElement content)
    {
        var tabs = content
            .GetPropertyOrNull("contents")
            ?.GetPropertyOrNull("twoColumnBrowseResultsRenderer")
            ?.GetPropertyOrNull("tabs");

        if (tabs is { } tabsEl && tabsEl.ValueKind == JsonValueKind.Array)
        {
            int tabCount = tabsEl.GetArrayLength();
            for (int t = 0; t < tabCount; t++)
            {
                var tabContent = tabsEl[t]
                    .GetPropertyOrNull("tabRenderer")
                    ?.GetPropertyOrNull("content");
                if (tabContent is null) continue;

                var found = FindRendererInTabContent(tabContent.Value);
                if (found is not null) return found;
            }
        }

        return content.FindFirstDescendantProperty("playlistVideoListRenderer");
    }

    /// <summary>
    /// Searches for <c>playlistVideoListRenderer</c> inside the contents of a single tab.
    /// </summary>
    private static JsonElement? FindRendererInTabContent(JsonElement tabContent)
    {
        var sections = tabContent
            .GetPropertyOrNull("sectionListRenderer")
            ?.GetPropertyOrNull("contents");

        if (sections is { } arr && arr.ValueKind == JsonValueKind.Array)
        {
            int sectionCount = arr.GetArrayLength();
            for (int s = 0; s < sectionCount; s++)
            {
                var section = arr[s];

                var itemContents = section
                    .GetPropertyOrNull("itemSectionRenderer")
                    ?.GetPropertyOrNull("contents");

                if (itemContents is { } items && items.ValueKind == JsonValueKind.Array)
                {
                    int itemCount = items.GetArrayLength();
                    for (int i = 0; i < itemCount; i++)
                    {
                        var renderer = items[i].GetPropertyOrNull("playlistVideoListRenderer");
                        if (renderer is not null) return renderer;
                    }
                }

                var direct = section.GetPropertyOrNull("playlistVideoListRenderer");
                if (direct is not null) return direct;
            }
        }

        var directInTab = tabContent.GetPropertyOrNull("playlistVideoListRenderer");
        if (directInTab is not null) return directInTab;

        return tabContent.FindFirstDescendantProperty("playlistVideoListRenderer");
    }

    /// <summary>
    /// Tries to determine playlist visibility from the privacy dropdown.
    /// </summary>
    private static bool TryParseVisibility(JsonElement? root, out PlaylistVisibility visibility)
    {
        visibility = PlaylistVisibility.Unknown;

        if (root is null)
            return false;

        if (TryFindSelectedPrivacyEntry(root.Value, out var selectedEntry) &&
            TryParseVisibilityFromEntry(selectedEntry, out visibility))
        {
            return true;
        }

        return TryParseVisibilityFromEntry(root.Value, out visibility);
    }

    /// <summary>
    /// Recursively searches for a dropdown item object that has <c>isSelected = true</c>.
    /// </summary>
    private static bool TryFindSelectedPrivacyEntry(JsonElement element, out JsonElement selectedEntry)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var isSelected = element.GetPropertyOrNull("isSelected")?.GetBooleanOrNull();
                    if (isSelected == true)
                    {
                        selectedEntry = element;
                        return true;
                    }

                    foreach (var property in element.EnumerateObject())
                    {
                        if (TryFindSelectedPrivacyEntry(property.Value, out selectedEntry))
                            return true;
                    }

                    break;
                }

            case JsonValueKind.Array:
                {
                    int len = element.GetArrayLength();
                    for (int i = 0; i < len; i++)
                    {
                        if (TryFindSelectedPrivacyEntry(element[i], out selectedEntry))
                            return true;
                    }

                    break;
                }
        }

        selectedEntry = default;
        return false;
    }

    /// <summary>
    /// Tries to extract visibility from a specific dropdown item.
    /// </summary>
    private static bool TryParseVisibilityFromEntry(JsonElement element, out PlaylistVisibility visibility)
    {
        visibility = PlaylistVisibility.Unknown;

        var privacyCode = element.FindFirstDescendantProperty("playlistPrivacy")?.GetStringOrNull();
        if (TryMapPrivacyCode(privacyCode, out visibility))
            return true;

        var intValue = element.FindFirstDescendantProperty("int32Value")?.GetInt32OrNull();
        if (TryMapPrivacyInt(intValue, out visibility))
            return true;

        var iconType = element.FindFirstDescendantProperty("iconType")?.GetStringOrNull();
        if (TryMapPrivacyIcon(iconType, out visibility))
            return true;

        return false;
    }

    private static bool TryMapPrivacyCode(string? code, out PlaylistVisibility visibility)
    {
        visibility = code switch
        {
            "PRIVATE" => PlaylistVisibility.Private,
            "UNLISTED" => PlaylistVisibility.Unlisted,
            "PUBLIC" => PlaylistVisibility.Public,
            _ => PlaylistVisibility.Unknown
        };

        return visibility != PlaylistVisibility.Unknown;
    }

    private static bool TryMapPrivacyInt(int? value, out PlaylistVisibility visibility)
    {
        visibility = value switch
        {
            0 => PlaylistVisibility.Private,
            2 => PlaylistVisibility.Unlisted,
            1 => PlaylistVisibility.Public,
            _ => PlaylistVisibility.Unknown
        };

        return visibility != PlaylistVisibility.Unknown;
    }

    private static bool TryMapPrivacyIcon(string? iconType, out PlaylistVisibility visibility)
    {
        visibility = iconType switch
        {
            "PRIVACY_PRIVATE" => PlaylistVisibility.Private,
            "PRIVACY_UNLISTED" => PlaylistVisibility.Unlisted,
            "PRIVACY_PUBLIC" => PlaylistVisibility.Public,
            _ => PlaylistVisibility.Unknown
        };

        return visibility != PlaylistVisibility.Unknown;
    }

    private static long? ExtractViewCount(JsonElement content, JsonElement? sidebarPrimary)
    {
        long? sidebarViews = null;

        // 1. Classic Sidebar stats[1] always contains the exact unshortened view count (e.g., "53,412 views")
        var stats = sidebarPrimary?.GetPropertyOrNull("stats");
        if (stats?.ValueKind == JsonValueKind.Array && stats.Value.GetArrayLength() > 1)
        {
            var text = GetTextFromStat(stats.Value[1]);
            sidebarViews = YoutubeParsingHelpers.ParseLongFromText(text);
            if (sidebarViews.HasValue && sidebarViews.Value % 100 != 0)
                return sidebarViews;
        }

        // 2. New UI (frameworkUpdates / pageHeaderViewModel)
        long? headerViews = null;
        var mutations = content
            .GetPropertyOrNull("frameworkUpdates")
            ?.GetPropertyOrNull("entityBatchUpdate")
            ?.GetPropertyOrNull("mutations");

        if (mutations?.ValueKind == JsonValueKind.Array)
        {
            foreach (var mutation in mutations.Value.EnumerateArray())
            {
                var metadataRows = mutation
                    .GetPropertyOrNull("payload")
                    ?.GetPropertyOrNull("pageHeaderEntity")
                    ?.GetPropertyOrNull("header")
                    ?.GetPropertyOrNull("pageHeaderViewModel")
                    ?.GetPropertyOrNull("metadata")
                    ?.GetPropertyOrNull("contentMetadataViewModel")
                    ?.GetPropertyOrNull("metadataRows");

                if (metadataRows?.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in metadataRows.Value.EnumerateArray())
                    {
                        var parts = row.GetPropertyOrNull("metadataParts");
                        if (parts?.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var part in parts.Value.EnumerateArray())
                            {
                                var text = part.GetPropertyOrNull("text")?.GetPropertyOrNull("content")?.GetStringOrNull();
                                if (text != null && text.Contains("view", StringComparison.OrdinalIgnoreCase))
                                {
                                    headerViews = YoutubeParsingHelpers.ParseLongFromText(text);
                                    if (headerViews.HasValue)
                                        break;
                                }
                            }
                        }
                        if (headerViews.HasValue) break;
                    }
                }
                if (headerViews.HasValue) break;
            }
        }

        if (sidebarViews.HasValue && headerViews.HasValue)
        {
            return sidebarViews.Value % 100 != 0 ? sidebarViews : headerViews;
        }

        return sidebarViews ?? headerViews;
    }

    private static DateOnly? ExtractReleaseDate(JsonElement content, JsonElement? sidebarPrimary)
    {
        // 1. Attempt via the new UI (frameworkUpdates)
        var mutations = content
            .GetPropertyOrNull("frameworkUpdates")
            ?.GetPropertyOrNull("entityBatchUpdate")
            ?.GetPropertyOrNull("mutations");

        if (mutations?.ValueKind == JsonValueKind.Array)
        {
            foreach (var mutation in mutations.Value.EnumerateArray())
            {
                var metadataRows = mutation
                    .GetPropertyOrNull("payload")
                    ?.GetPropertyOrNull("pageHeaderEntity")
                    ?.GetPropertyOrNull("header")
                    ?.GetPropertyOrNull("pageHeaderViewModel")
                    ?.GetPropertyOrNull("metadata")
                    ?.GetPropertyOrNull("contentMetadataViewModel")
                    ?.GetPropertyOrNull("metadataRows");

                if (metadataRows?.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in metadataRows.Value.EnumerateArray())
                    {
                        var parts = row.GetPropertyOrNull("metadataParts");
                        if (parts?.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var part in parts.Value.EnumerateArray())
                            {
                                var text = part.GetPropertyOrNull("text")
                                    ?.GetPropertyOrNull("content")
                                    ?.GetStringOrNull();

                                if (text != null)
                                {
                                    var parsed = TryParseDate(text);
                                    if (parsed.HasValue) return parsed;
                                }
                            }
                        }
                    }
                }
            }
        }

        // 2. Attempt via classic Sidebar stats[2] (third element of the array)
        var stats = sidebarPrimary?.GetPropertyOrNull("stats");
        if (stats?.ValueKind == JsonValueKind.Array)
        {
            int len = stats.Value.GetArrayLength();
            if (len > 2)
            {
                var dateStat = stats.Value[2];

                var dateFromRuns = GetDateFromStatRuns(dateStat.GetPropertyOrNull("runs"));
                if (dateFromRuns != null)
                    return TryParseDate(dateFromRuns);

                var simpleText = dateStat.GetPropertyOrNull("simpleText")?.GetStringOrNull();
                if (simpleText != null)
                    return TryParseDate(simpleText);
            }
        }

        return null;
    }

    /// <summary>
    /// Performs deterministic conversion of an English YouTube date string to <see cref="DateOnly"/>.
    /// </summary>
    private static DateOnly? TryParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var clean = StripUpdatePrefix(text);
        var span = clean.AsSpan().Trim();
        if (span.IsEmpty) return null;

        if (DateOnly.TryParseExact(span, YoutubeParsingHelpers.DateFormats,
                YoutubeParsingHelpers.EnCulture, DateTimeStyles.AllowWhiteSpaces, out var dateEn))
        {
            return dateEn;
        }

        if (DateOnly.TryParse(span, YoutubeParsingHelpers.EnCulture,
                DateTimeStyles.AllowWhiteSpaces, out var flexEn))
        {
            return flexEn;
        }

        if (DateOnly.TryParse(span, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var flexInv))
        {
            return flexInv;
        }

        if (YoutubeParsingHelpers.IsRelativeDate(span))
        {
            var relative = YoutubeParsingHelpers.ParseRelativeDate(span, DateTime.UtcNow);
            if (relative.HasValue)
                return relative;
        }

        return null;
    }

    private static List<PlaylistVideoData> ExtractVideos(JsonElement? effectivePlaylistContents)
    {
        var contents = effectivePlaylistContents?.GetPropertyOrNull(InnerTubeTokens.Contents);
        if (contents is null || contents.Value.ValueKind != JsonValueKind.Array)
            return [];

        var array = contents.Value;
        int len = array.GetArrayLength();
        if (len == 0) return [];

        var result = new List<PlaylistVideoData>(len);
        for (int i = 0; i < len; i++)
        {
            var videoRenderer = array[i].GetPropertyOrNull(InnerTubeTokens.PlaylistVideoRenderer);
            if (videoRenderer is not null)
            {
                result.Add(new PlaylistVideoData(videoRenderer.Value));
            }
        }

        return result;
    }

    private static string? ExtractContinuationToken(JsonElement content, JsonElement? effectivePlaylistContents)
    {
        if (effectivePlaylistContents != null)
        {
            var continuations = effectivePlaylistContents.Value.GetPropertyOrNull("continuations");
            if (continuations != null)
            {
                var token = continuations.Value.EnumerateArrayOrNull()?.FirstOrNull()
                    ?.GetPropertyOrNull("nextContinuationData")
                    ?.GetPropertyOrNull("continuation")
                    ?.GetStringOrNull();
                if (token != null) return token;
            }

            var contents = effectivePlaylistContents.Value.GetPropertyOrNull("contents");
            if (contents != null)
            {
                var token = BridgeUtils.FindTokenInContents(contents.Value);
                if (token != null) return token;
            }
        }

        var actions = content.GetPropertyOrNull("onResponseReceivedActions");
        if (actions != null)
        {
            var continuationItems = actions.Value.EnumerateArrayOrNull()?.FirstOrNull()
                ?.GetPropertyOrNull("appendContinuationItemsAction")
                ?.GetPropertyOrNull("continuationItems");

            if (continuationItems != null)
            {
                var token = BridgeUtils.FindTokenInContents(continuationItems.Value);
                if (token != null) return token;
            }
        }

        return null;
    }

    #region High-Performance JSON Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string? GetDateFromStatRuns(JsonElement? runsElement)
    {
        if (runsElement is null || runsElement.Value.ValueKind != JsonValueKind.Array)
            return null;

        var array = runsElement.Value;
        int len = array.GetArrayLength();
        if (len == 0) return null;

        if (len == 1)
        {
            var text = array[0].GetPropertyOrNull(InnerTubeTokens.Text)?.GetStringOrNull();
            return text != null ? StripUpdatePrefix(text) : null;
        }

        if (len == 2)
            return array[1].GetPropertyOrNull(InnerTubeTokens.Text)?.GetStringOrNull()?.Trim();

        int dateRunCount = len - 1;
        int totalLen = 0;

        string?[]? rentedArray = null;
        char[]? rentedChars = null;
        try
        {
            rentedArray = System.Buffers.ArrayPool<string?>.Shared.Rent(dateRunCount);

            for (int i = 0; i < dateRunCount; i++)
            {
                var t = array[i + 1].GetPropertyOrNull(InnerTubeTokens.Text)?.GetStringOrNull();
                if (t != null)
                {
                    rentedArray[i] = t;
                    totalLen += t.Length;
                }
                else
                {
                    rentedArray[i] = null;
                }
            }

            if (totalLen == 0) return null;

            Span<char> span = totalLen <= 128
                ? stackalloc char[totalLen]
                : (rentedChars = System.Buffers.ArrayPool<char>.Shared.Rent(totalLen)).AsSpan(0, totalLen);
            int pos = 0;

            for (int i = 0; i < dateRunCount; i++)
            {
                if (rentedArray[i] is { } s)
                {
                    s.AsSpan().CopyTo(span[pos..]);
                    pos += s.Length;
                }
            }

            return new string(span[..pos].Trim());
        }
        finally
        {
            if (rentedChars != null)
            {
                System.Buffers.ArrayPool<char>.Shared.Return(rentedChars);
            }
            if (rentedArray != null)
            {
                System.Buffers.ArrayPool<string?>.Shared.Return(rentedArray, clearArray: true);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string StripUpdatePrefix(string text)
    {
        var span = text.AsSpan();
        int prefixLen = 0;

        if (span.StartsWith(PrefixLastUpdatedOn, StringComparison.OrdinalIgnoreCase))
            prefixLen = PrefixLastUpdatedOn.Length;
        else if (span.StartsWith(PrefixUpdated, StringComparison.OrdinalIgnoreCase))
            prefixLen = PrefixUpdated.Length;

        if (prefixLen == 0)
            return text;

        var remaining = span[prefixLen..].Trim();
        return remaining.IsEmpty ? text : new string(remaining);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string? GetTextFromStat(JsonElement stat) =>
        stat.GetPropertyOrNull(InnerTubeTokens.SimpleText)?.GetStringOrNull()
        ?? YoutubeParsingHelpers.ConcatTextRuns(stat.GetPropertyOrNull(InnerTubeTokens.Runs));

    #endregion
}

internal partial class PlaylistBrowseResponse
{
    /// <summary>
    /// Parses a playlist Browse response from a raw JSON string.
    /// Disposes <see cref="JsonDocument"/> immediately after constructing the model.
    /// </summary>
    public static PlaylistBrowseResponse Parse(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return new PlaylistBrowseResponse(doc.RootElement);
    }

    /// <summary>
    /// Parses a playlist Browse response directly from a network stream without intermediate strings in LOH.
    /// Disposes <see cref="JsonDocument"/> immediately after constructing the model.
    /// </summary>
    /// <param name="stream">HTTP response network stream.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>An instance of the flat playlist response.</returns>
    public static async ValueTask<PlaylistBrowseResponse> ParseAsync(Stream stream, CancellationToken ct = default)
    {
        using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
        return new PlaylistBrowseResponse(doc.RootElement);
    }
}