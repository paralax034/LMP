using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LMP.Core.Youtube.Utils;
using LMP.Core.Helpers.Extensions;

namespace LMP.Core.Youtube.Bridge;

internal partial class SearchResponse
{
    public IReadOnlyList<VideoData> Videos { get; }
    public IReadOnlyList<PlaylistData> Playlists { get; }
    public IReadOnlyList<ChannelData> Channels { get; }
    public string? ContinuationToken { get; }

    private SearchResponse(JsonElement content)
    {
        var videos = new List<VideoData>(32);
        var playlists = new List<PlaylistData>(8);
        var channels = new List<ChannelData>(4);
        string? foundToken = null;

        CollectAndClassify(content, videos, playlists, channels, ref foundToken);

        Videos = videos;
        Playlists = playlists;
        Channels = channels;
        ContinuationToken = foundToken ?? ExtractContinuationTokenFast(content);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsItemRenderer(in JsonProperty prop)
    {
        return prop.NameEquals(InnerTubeTokens.MusicResponsiveListItemRenderer) ||
               prop.NameEquals(InnerTubeTokens.VideoRenderer) ||
               prop.NameEquals(InnerTubeTokens.PlaylistRenderer) ||
               prop.NameEquals(InnerTubeTokens.ChannelRenderer) ||
               prop.NameEquals(InnerTubeTokens.ShortsLockupViewModel) ||
               prop.NameEquals(InnerTubeTokens.ReelItemRenderer) ||
               prop.NameEquals(InnerTubeTokens.ContinuationItemRenderer) ||
               prop.NameEquals(InnerTubeTokens.LockupViewModel);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsContainer(in JsonProperty prop)
    {
        return prop.NameEquals(InnerTubeTokens.Contents) ||
               prop.NameEquals(InnerTubeTokens.Items) ||
               prop.NameEquals(InnerTubeTokens.PrimaryContents) ||
               prop.NameEquals(InnerTubeTokens.SecondaryContents) ||
               prop.NameEquals(InnerTubeTokens.TwoColumnSearchResultsRenderer) ||
               prop.NameEquals(InnerTubeTokens.SectionListRenderer) ||
               prop.NameEquals(InnerTubeTokens.ItemSectionRenderer) ||
               prop.NameEquals(InnerTubeTokens.MusicShelfRenderer) ||
               prop.NameEquals(InnerTubeTokens.RichGridRenderer) ||
               prop.NameEquals(InnerTubeTokens.ShelfRenderer) ||
               prop.NameEquals(InnerTubeTokens.TabbedSearchResultsRenderer) ||
               prop.NameEquals(InnerTubeTokens.TabRenderer) ||
               prop.NameEquals(InnerTubeTokens.Tabs) ||
               prop.NameEquals(InnerTubeTokens.Content) ||
               prop.NameEquals(InnerTubeTokens.Continuations) ||
               prop.NameEquals(InnerTubeTokens.OnResponseReceivedCommands) ||
               prop.NameEquals(InnerTubeTokens.OnResponseReceivedActions) ||
               prop.NameEquals(InnerTubeTokens.AppendContinuationItemsAction) ||
               prop.NameEquals(InnerTubeTokens.ContinuationItems) ||
               prop.NameEquals(InnerTubeTokens.ContinuationContents) ||
               prop.NameEquals(InnerTubeTokens.MusicShelfContinuation) ||
               prop.NameEquals(InnerTubeTokens.MusicPlaylistShelfContinuation) ||
               prop.NameEquals(InnerTubeTokens.SectionListContinuation) ||
               prop.NameEquals(InnerTubeTokens.ItemSectionContinuation);
    }

    /// <summary>
    /// Объединённый обход + классификация в одном проходе.
    /// Использует ArrayPool для стека вместо Stack{T} (меньше аллокаций).
    /// </summary>
    private static void CollectAndClassify(
        JsonElement root,
        List<VideoData> videos,
        List<PlaylistData> playlists,
        List<ChannelData> channels,
        ref string? token)
    {
        var stackBuffer = ArrayPool<JsonElement>.Shared.Rent(128);
        int stackTop = 0;
        stackBuffer[stackTop++] = root;

        try
        {
            while (stackTop > 0)
            {
                var current = stackBuffer[--stackTop];

                if (current.ValueKind == JsonValueKind.Array)
                {
                    int len = current.GetArrayLength();

                    if (stackTop + len > stackBuffer.Length)
                    {
                        var newBuffer = ArrayPool<JsonElement>.Shared.Rent(Math.Max(stackBuffer.Length * 2, stackTop + len));
                        Array.Copy(stackBuffer, newBuffer, stackTop);
                        ArrayPool<JsonElement>.Shared.Return(stackBuffer);
                        stackBuffer = newBuffer;
                    }

                    for (int i = len - 1; i >= 0; i--)
                        stackBuffer[stackTop++] = current[i];

                    continue;
                }

                if (current.ValueKind != JsonValueKind.Object)
                    continue;

                bool isItem = false;
                foreach (var prop in current.EnumerateObject())
                {
                    if (IsItemRenderer(prop))
                    {
                        if (prop.NameEquals(InnerTubeTokens.LockupViewModel))
                        {
                            if (prop.Value.TryGetProperty(InnerTubeTokens.ContentId, out _))
                            {
                                isItem = true;
                                break;
                            }
                        }
                        else
                        {
                            isItem = true;
                            break;
                        }
                    }
                }

                if (isItem)
                {
                    ClassifyAndExtract(current, videos, playlists, channels, ref token);
                    continue;
                }

                foreach (var prop in current.EnumerateObject())
                {
                    if (IsContainer(prop))
                    {
                        if (stackTop >= stackBuffer.Length)
                        {
                            var newBuffer = ArrayPool<JsonElement>.Shared.Rent(stackBuffer.Length * 2);
                            Array.Copy(stackBuffer, newBuffer, stackTop);
                            ArrayPool<JsonElement>.Shared.Return(stackBuffer);
                            stackBuffer = newBuffer;
                        }
                        stackBuffer[stackTop++] = prop.Value;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<JsonElement>.Shared.Return(stackBuffer, clearArray: true);
        }
    }

    /// <summary>
    /// Классифицирует элемент и сразу добавляет в нужный список — без промежуточного ClassificationResult.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ClassifyAndExtract(
        JsonElement item,
        List<VideoData> videos,
        List<PlaylistData> playlists,
        List<ChannelData> channels,
        ref string? token)
    {
        foreach (var prop in item.EnumerateObject())
        {
            if (prop.NameEquals(InnerTubeTokens.ContinuationItemRenderer))
            {
                token ??= BridgeUtils.ExtractContinuationToken(prop.Value);
                return;
            }

            if (prop.NameEquals(InnerTubeTokens.MusicResponsiveListItemRenderer))
            {
                ProcessMusicItem(prop.Value, videos, playlists, channels);
                return;
            }

            if (prop.NameEquals(InnerTubeTokens.VideoRenderer) ||
                prop.NameEquals(InnerTubeTokens.ShortsLockupViewModel) ||
                prop.NameEquals(InnerTubeTokens.ReelItemRenderer))
            {
                var videoData = new VideoData(prop.Value, isYtm: false);
                if (!string.IsNullOrEmpty(videoData.Id))
                    videos.Add(videoData);
                return;
            }

            if (prop.NameEquals(InnerTubeTokens.LockupViewModel))
            {
                var contentId = prop.Value.GetPropertyOrNull(InnerTubeTokens.ContentId)?.GetStringOrNull();
                if (!string.IsNullOrEmpty(contentId) && IsPlaylistId(contentId))
                    playlists.Add(new PlaylistData(prop.Value));
                return;
            }

            if (prop.NameEquals(InnerTubeTokens.PlaylistRenderer))
            {
                playlists.Add(new PlaylistData(prop.Value));
                return;
            }

            if (prop.NameEquals(InnerTubeTokens.ChannelRenderer))
            {
                channels.Add(new ChannelData(prop.Value));
                return;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPlaylistId(string id)
    {
        var span = id.AsSpan();
        return span.Length >= 2 &&
            (span.StartsWith("PL") || span.StartsWith("OL") || span.StartsWith("RD"));
    }

    private static void ProcessMusicItem(
        JsonElement musicItem,
        List<VideoData> videos,
        List<PlaylistData> playlists,
        List<ChannelData> channels)
    {
        var data = new VideoData(musicItem, isYtm: true);

        if (data.IsPlaylistContext)
        {
            playlists.Add(new PlaylistData(musicItem, isYtm: true));
            return;
        }

        if (data.IsArtistContext)
        {
            channels.Add(new ChannelData(musicItem, isYtm: true));
            return;
        }

        if (!string.IsNullOrEmpty(data.Id))
            videos.Add(data);
    }

    /// <summary>
    /// Быстрый поиск токена с ArrayPool стеком и ранним выходом.
    /// </summary>
    private static string? ExtractContinuationTokenFast(JsonElement root)
    {
        var stackBuffer = ArrayPool<JsonElement>.Shared.Rent(64);
        int stackTop = 0;
        stackBuffer[stackTop++] = root;

        try
        {
            while (stackTop > 0)
            {
                var current = stackBuffer[--stackTop];

                if (current.ValueKind == JsonValueKind.Object)
                {
                    if (current.TryGetProperty(InnerTubeTokens.ContinuationItemRenderer, out var continuationItem))
                    {
                        var token = BridgeUtils.ExtractContinuationToken(continuationItem);
                        if (token != null) return token;
                    }

                    foreach (var prop in current.EnumerateObject())
                    {
                        if (prop.NameEquals(InnerTubeTokens.ContinuationCommand))
                        {
                            var token = prop.Value.GetPropertyOrNull(InnerTubeTokens.Token)?.GetStringOrNull();
                            if (token != null) return token;
                        }
                        else if (prop.NameEquals(InnerTubeTokens.NextContinuationData))
                        {
                            var token = prop.Value.GetPropertyOrNull(InnerTubeTokens.Continuation)?.GetStringOrNull();
                            if (token != null) return token;
                        }
                        else if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        {
                            if (stackTop >= stackBuffer.Length)
                            {
                                var newBuffer = ArrayPool<JsonElement>.Shared.Rent(stackBuffer.Length * 2);
                                Array.Copy(stackBuffer, newBuffer, stackTop);
                                ArrayPool<JsonElement>.Shared.Return(stackBuffer);
                                stackBuffer = newBuffer;
                            }
                            stackBuffer[stackTop++] = prop.Value;
                        }
                    }
                }
                else if (current.ValueKind == JsonValueKind.Array)
                {
                    int len = current.GetArrayLength();
                    if (stackTop + len > stackBuffer.Length)
                    {
                        var newBuffer = ArrayPool<JsonElement>.Shared.Rent(Math.Max(stackBuffer.Length * 2, stackTop + len));
                        Array.Copy(stackBuffer, newBuffer, stackTop);
                        ArrayPool<JsonElement>.Shared.Return(stackBuffer);
                        stackBuffer = newBuffer;
                    }
                    for (int i = len - 1; i >= 0; i--)
                        stackBuffer[stackTop++] = current[i];
                }
            }
        }
        finally
        {
            ArrayPool<JsonElement>.Shared.Return(stackBuffer, clearArray: true);
        }

        return null;
    }

    public static SearchResponse Parse(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return new SearchResponse(doc.RootElement);
    }

    /// <summary>
    /// Парсит из потока — избегает промежуточной строки.
    /// </summary>
    public static async ValueTask<SearchResponse> ParseAsync(Stream stream, CancellationToken ct = default)
    {
        using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
        return new SearchResponse(doc.RootElement);
    }

    internal sealed class VideoData
    {
        public bool IsMusicItem { get; init; }
        public string? Id { get; init; }
        public string? Title { get; init; }
        public string? Author { get; init; }
        public string? ChannelId { get; init; }
        public bool IsOfficialArtist { get; init; }
        public bool IsShort { get; init; }
        public TimeSpan? Duration { get; init; }
        public IReadOnlyList<ThumbnailData> Thumbnails { get; init; } = [];
        public bool IsPlaylistContext { get; init; }
        public bool IsArtistContext { get; init; }

        public VideoData(JsonElement content, bool isYtm)
        {
            IsMusicItem = isYtm;
            Id = ComputeId(content, isYtm);
            Title = ComputeTitle(content, isYtm);
            Author = ComputeAuthor(content, isYtm);
            ChannelId = ComputeChannelId(content, isYtm);
            IsOfficialArtist = ComputeIsOfficialArtist(content, isYtm);
            IsShort = !isYtm &&
                (content.TryGetProperty(InnerTubeTokens.ShortsLockupViewModel, out _) ||
                 content.TryGetProperty(InnerTubeTokens.ReelItemRenderer, out _));
            Duration = ComputeDuration(content, isYtm);
            Thumbnails = ComputeThumbnails(content);
            IsPlaylistContext = ComputeIsPlaylistContext(content, isYtm, Id, Title);
            IsArtistContext = ComputeIsArtistContext(content, isYtm);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static string? ComputeId(JsonElement content, bool isYtm)
        {
            if (isYtm)
            {
                var vid = content.GetPropertyOrNull(InnerTubeTokens.PlaylistItemData)
                    ?.GetPropertyOrNull(InnerTubeTokens.VideoId)?.GetStringOrNull();
                if (!string.IsNullOrEmpty(vid)) return vid;

                var nav = content.GetPropertyOrNull(InnerTubeTokens.PlayNavigationEndpoint)
                    ?? content.GetPropertyOrNull(InnerTubeTokens.NavigationEndpoint);
                return nav?.GetPropertyOrNull(InnerTubeTokens.WatchEndpoint)
                    ?.GetPropertyOrNull(InnerTubeTokens.VideoId)?.GetStringOrNull();
            }

            var id = content.GetPropertyOrNull(InnerTubeTokens.VideoId)?.GetStringOrNull();
            if (!string.IsNullOrEmpty(id)) return id;

            return content.GetPropertyOrNull(InnerTubeTokens.OnTap)
                ?.GetPropertyOrNull(InnerTubeTokens.InnertubeCommand)
                ?.GetPropertyOrNull(InnerTubeTokens.ReelWatchEndpoint)
                ?.GetPropertyOrNull(InnerTubeTokens.VideoId)?.GetStringOrNull();
        }

        private static string? ComputeTitle(JsonElement content, bool isYtm)
        {
            if (isYtm) return GetRunText(content, 0);

            var titleProp = content.GetPropertyOrNull(InnerTubeTokens.Title);
            if (titleProp.HasValue)
            {
                return titleProp.Value.GetPropertyOrNull(InnerTubeTokens.SimpleText)?.GetStringOrNull()
                    ?? YoutubeParsingHelpers.ConcatTextRuns(titleProp.Value.GetPropertyOrNull(InnerTubeTokens.Runs));
            }

            return content.GetPropertyOrNull(InnerTubeTokens.OverlayMetadata)
                ?.GetPropertyOrNull(InnerTubeTokens.PrimaryText)
                ?.GetPropertyOrNull(InnerTubeTokens.Content)?.GetStringOrNull();
        }

        private static string? ComputeAuthor(JsonElement content, bool isYtm)
        {
            if (isYtm)
            {
                var runsElement = GetRunsElement(content, 1);
                if (runsElement == null) return null;

                foreach (var run in runsElement.Value.EnumerateArray())
                {
                    var pageType = run.GetPropertyOrNull(InnerTubeTokens.NavigationEndpoint)
                        ?.GetPropertyOrNull(InnerTubeTokens.BrowseEndpoint)
                        ?.GetPropertyOrNull(InnerTubeTokens.BrowseEndpointContextSupportedConfigs)
                        ?.GetPropertyOrNull(InnerTubeTokens.BrowseEndpointContextMusicConfig)
                        ?.GetPropertyOrNull(InnerTubeTokens.PageType)?.GetStringOrNull();

                    if (pageType is InnerTubeConstants.PageTypeArtist or InnerTubeConstants.PageTypeUserChannel)
                        return run.GetPropertyOrNull(InnerTubeTokens.Text)?.GetStringOrNull();
                }

                var first = runsElement.Value.GetFirstArrayElementOrNull();
                return first?.GetPropertyOrNull(InnerTubeTokens.Text)?.GetStringOrNull();
            }

            var ownerRuns = content.GetPropertyOrNull(InnerTubeTokens.OwnerText)?.GetPropertyOrNull(InnerTubeTokens.Runs);
            if (ownerRuns.HasValue)
                return YoutubeParsingHelpers.ConcatTextRuns(ownerRuns.Value);

            var bylineRuns = content.GetPropertyOrNull(InnerTubeTokens.ShortBylineText)?.GetPropertyOrNull(InnerTubeTokens.Runs);
            if (bylineRuns.HasValue)
                return YoutubeParsingHelpers.ConcatTextRuns(bylineRuns.Value);

            return null;
        }

        private static string? ComputeChannelId(JsonElement content, bool isYtm)
        {
            if (isYtm)
            {
                var runsElement = GetRunsElement(content, 1);
                if (runsElement == null) return null;

                foreach (var run in runsElement.Value.EnumerateArray())
                {
                    var id = run.GetPropertyOrNull(InnerTubeTokens.NavigationEndpoint)
                        ?.GetPropertyOrNull(InnerTubeTokens.BrowseEndpoint)
                        ?.GetPropertyOrNull(InnerTubeTokens.BrowseId)?.GetStringOrNull();

                    if (id != null && id.AsSpan().StartsWith("UC"))
                        return id;
                }
                return null;
            }

            var ownerRuns = content.GetPropertyOrNull(InnerTubeTokens.OwnerText)?.GetPropertyOrNull(InnerTubeTokens.Runs);
            if (ownerRuns.HasValue)
            {
                foreach (var run in ownerRuns.Value.EnumerateArrayOrEmpty())
                {
                    var id = run.GetPropertyOrNull(InnerTubeTokens.NavigationEndpoint)
                        ?.GetPropertyOrNull(InnerTubeTokens.BrowseEndpoint)
                        ?.GetPropertyOrNull(InnerTubeTokens.BrowseId)?.GetStringOrNull();
                    if (id != null) return id;
                }
            }

            return content.GetPropertyOrNull(InnerTubeTokens.ChannelThumbnailSupportedRenderers)
                ?.GetPropertyOrNull(InnerTubeTokens.ChannelThumbnailWithLinkRenderer)
                ?.GetPropertyOrNull(InnerTubeTokens.NavigationEndpoint)
                ?.GetPropertyOrNull(InnerTubeTokens.BrowseEndpoint)
                ?.GetPropertyOrNull(InnerTubeTokens.BrowseId)?.GetStringOrNull();
        }

        private static bool ComputeIsOfficialArtist(JsonElement content, bool isYtm)
        {
            if (isYtm) return true;

            var badges = content.GetPropertyOrNull(InnerTubeTokens.OwnerBadges);
            if (badges == null) return false;

            foreach (var badge in badges.Value.EnumerateArrayOrEmpty())
            {
                var iconType = badge.GetPropertyOrNull(InnerTubeTokens.MetadataBadgeRenderer)
                    ?.GetPropertyOrNull(InnerTubeTokens.Icon)
                    ?.GetPropertyOrNull(InnerTubeTokens.IconType)?.GetStringOrNull();
                if (iconType == InnerTubeConstants.AudioBadge) return true;
            }
            return false;
        }

        private static TimeSpan? ComputeDuration(JsonElement content, bool isYtm)
        {
            if (isYtm)
            {
                var runsElement = GetRunsElement(content, 1);
                if (runsElement == null) return null;

                foreach (var run in runsElement.Value.EnumerateArray())
                {
                    var text = run.GetPropertyOrNull(InnerTubeTokens.Text)?.GetStringOrNull();
                    if (text != null && text.Contains(':') && !text.Contains('•'))
                    {
                        var ts = YoutubeClientUtils.DurationParser.Parse(text);
                        if (ts.HasValue) return ts;
                    }
                }
                return null;
            }

            var textDuration = content.GetPropertyOrNull(InnerTubeTokens.LengthText)
                ?.GetPropertyOrNull(InnerTubeTokens.SimpleText)?.GetStringOrNull();

            return textDuration != null ? YoutubeClientUtils.DurationParser.Parse(textDuration) : null;
        }

        internal static IReadOnlyList<ThumbnailData> ComputeThumbnails(JsonElement content)
        {
            var thumbsElement = content.GetPropertyOrNull(InnerTubeTokens.Thumbnail)
                ?.GetPropertyOrNull(InnerTubeTokens.MusicThumbnailRenderer)
                ?.GetPropertyOrNull(InnerTubeTokens.Thumbnail)
                ?.GetPropertyOrNull(InnerTubeTokens.Thumbnails);

            thumbsElement ??= content.GetPropertyOrNull(InnerTubeTokens.Thumbnail)
                ?.GetPropertyOrNull(InnerTubeTokens.Thumbnails);

            thumbsElement ??= content.GetPropertyOrNull(InnerTubeTokens.ThumbnailViewModel)
                ?.GetPropertyOrNull(InnerTubeTokens.Image)
                ?.GetPropertyOrNull(InnerTubeTokens.Sources);

            if (thumbsElement == null) return [];

            var len = thumbsElement.Value.GetArrayLength();
            if (len == 0) return [];

            var list = new ThumbnailData[len];
            int idx = 0;
            foreach (var t in thumbsElement.Value.EnumerateArray())
                list[idx++] = new ThumbnailData(t);
            return list;
        }

        private static bool ComputeIsPlaylistContext(JsonElement content, bool isYtm, string? id, string? title)
        {
            if (!isYtm) return false;

            var pageType = content.GetPropertyOrNull(InnerTubeTokens.NavigationEndpoint)
                ?.GetPropertyOrNull(InnerTubeTokens.BrowseEndpoint)
                ?.GetPropertyOrNull(InnerTubeTokens.BrowseEndpointContextSupportedConfigs)
                ?.GetPropertyOrNull(InnerTubeTokens.BrowseEndpointContextMusicConfig)
                ?.GetPropertyOrNull(InnerTubeTokens.PageType)?.GetStringOrNull();

            return pageType == InnerTubeConstants.PageTypeAlbum || (id == null && title != null);
        }

        private static bool ComputeIsArtistContext(JsonElement content, bool isYtm)
        {
            if (!isYtm) return false;

            var pageType = content.GetPropertyOrNull(InnerTubeTokens.NavigationEndpoint)
                ?.GetPropertyOrNull(InnerTubeTokens.BrowseEndpoint)
                ?.GetPropertyOrNull(InnerTubeTokens.BrowseEndpointContextSupportedConfigs)
                ?.GetPropertyOrNull(InnerTubeTokens.BrowseEndpointContextMusicConfig)
                ?.GetPropertyOrNull(InnerTubeTokens.PageType)?.GetStringOrNull();

            return pageType == InnerTubeConstants.PageTypeArtist;
        }

        /// <summary>
        /// Возвращает JsonElement массива runs вместо IEnumerable — без аллокации.
        /// </summary>
        private static JsonElement? GetRunsElement(JsonElement item, int columnIndex)
        {
            var cols = item.GetPropertyOrNull(InnerTubeTokens.FlexColumns);
            if (cols == null) return null;

            var col = cols.Value.GetArrayElementOrNull(columnIndex);
            if (col == null) return null;

            return col.Value.GetPropertyOrNull(InnerTubeTokens.MusicResponsiveListItemFlexColumnRenderer)
                ?.GetPropertyOrNull(InnerTubeTokens.Text)
                ?.GetPropertyOrNull(InnerTubeTokens.Runs);
        }

        /// <summary>
        /// Собирает текст всех runs через централизованный стек-хелпер без лишних аллокаций.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string? GetRunText(JsonElement item, int columnIndex)
        {
            var runsElement = GetRunsElement(item, columnIndex);
            return YoutubeParsingHelpers.ConcatTextRuns(runsElement);
        }
    }

    internal sealed class PlaylistData
    {
        public string? Id { get; init; }
        public string? Title { get; init; }
        public string? Author { get; init; }
        public IReadOnlyList<ThumbnailData> Thumbnails { get; init; } = [];

        public PlaylistData(JsonElement content, bool isYtm = false)
        {
            Id = content.GetPropertyOrNull(InnerTubeTokens.PlaylistId)?.GetStringOrNull() ??
                 content.GetPropertyOrNull(InnerTubeTokens.ContentId)?.GetStringOrNull() ??
                 (isYtm ? content.GetPropertyOrNull(InnerTubeTokens.NavigationEndpoint)
                     ?.GetPropertyOrNull(InnerTubeTokens.BrowseEndpoint)
                     ?.GetPropertyOrNull(InnerTubeTokens.BrowseId)?.GetStringOrNull() : null);

            var titleProp = content.GetPropertyOrNull(InnerTubeTokens.Title);
            if (titleProp.HasValue)
            {
                Title = titleProp.Value.GetPropertyOrNull(InnerTubeTokens.SimpleText)?.GetStringOrNull()
                    ?? YoutubeParsingHelpers.ConcatTextRuns(titleProp.Value.GetPropertyOrNull(InnerTubeTokens.Runs));
            }

            if (Title is null)
            {
                var lockupTitle = content.GetPropertyOrNull(InnerTubeTokens.Metadata)
                    ?.GetPropertyOrNull(InnerTubeTokens.LockupMetadataViewModel)
                    ?.GetPropertyOrNull(InnerTubeTokens.Title)
                    ?.GetPropertyOrNull(InnerTubeTokens.Content)?.GetStringOrNull();

                if (lockupTitle != null)
                    Title = lockupTitle;
                else if (isYtm)
                    Title = VideoData.GetRunText(content, 0);
            }

            var authorRuns = content.GetPropertyOrNull(InnerTubeTokens.ShortBylineText)?.GetPropertyOrNull(InnerTubeTokens.Runs);
            if (authorRuns.HasValue)
                Author = YoutubeParsingHelpers.ConcatTextRuns(authorRuns.Value);
            else if (isYtm)
                Author = VideoData.GetRunText(content, 1);

            Thumbnails = VideoData.ComputeThumbnails(content);
        }
    }

    internal sealed class ChannelData
    {
        public string? Id { get; init; }
        public string? Title { get; init; }
        public IReadOnlyList<ThumbnailData> Thumbnails { get; init; } = [];

        public ChannelData(JsonElement content, bool isYtm = false)
        {
            Id = content.GetPropertyOrNull(InnerTubeTokens.ChannelId)?.GetStringOrNull() ??
                 (isYtm ? content.GetPropertyOrNull(InnerTubeTokens.NavigationEndpoint)
                     ?.GetPropertyOrNull(InnerTubeTokens.BrowseEndpoint)
                     ?.GetPropertyOrNull(InnerTubeTokens.BrowseId)?.GetStringOrNull() : null);

            Title = content.GetPropertyOrNull(InnerTubeTokens.Title)?.GetPropertyOrNull(InnerTubeTokens.SimpleText)?.GetStringOrNull()
                ?? YoutubeParsingHelpers.ConcatTextRuns(content.GetPropertyOrNull(InnerTubeTokens.Title)?.GetPropertyOrNull(InnerTubeTokens.Runs))
                ?? (isYtm ? VideoData.GetRunText(content, 0) : null);

            Thumbnails = VideoData.ComputeThumbnails(content);
        }
    }
}