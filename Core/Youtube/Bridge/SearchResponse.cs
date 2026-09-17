using System.Buffers;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LMP.Core.Youtube.Utils;
using LMP.Core.Helpers.Extensions;

namespace LMP.Core.Youtube.Bridge;

internal partial class SearchResponse
{
    private static readonly FrozenSet<string> ItemRendererNames = new[]
    {
        "musicResponsiveListItemRenderer",
        "videoRenderer",
        "playlistRenderer",
        "channelRenderer",
        "shortsLockupViewModel",
        "reelItemRenderer",
        "continuationItemRenderer",
        "lockupViewModel"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> ContainerNames = new[]
    {
        "contents", "items", "primaryContents", "secondaryContents",
        "twoColumnSearchResultsRenderer", "sectionListRenderer",
        "itemSectionRenderer", "musicShelfRenderer", "richGridRenderer",
        "shelfRenderer", "tabbedSearchResultsRenderer", "tabRenderer",
        "tabs", "content", "continuations", "onResponseReceivedCommands",
        "onResponseReceivedActions", "appendContinuationItemsAction",
        "continuationItems", "continuationContents", "musicShelfContinuation",
        "musicPlaylistShelfContinuation", "sectionListContinuation",
        "itemSectionContinuation"
    }.ToFrozenSet(StringComparer.Ordinal);

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

        // Используем ArrayPool для стека обхода
        CollectAndClassify(content, videos, playlists, channels, ref foundToken);

        Videos = videos;
        Playlists = playlists;
        Channels = channels;
        ContinuationToken = foundToken ?? ExtractContinuationTokenFast(content);
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
        // Используем массив из пула вместо Stack<JsonElement>
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

                    // Гарантируем достаточный размер стека
                    if (stackTop + len > stackBuffer.Length)
                    {
                        var newBuffer = ArrayPool<JsonElement>.Shared.Rent(stackBuffer.Length * 2);
                        Array.Copy(stackBuffer, newBuffer, stackTop);
                        ArrayPool<JsonElement>.Shared.Return(stackBuffer);
                        stackBuffer = newBuffer;
                    }

                    // Пушим в обратном порядке для правильной последовательности
                    for (int i = len - 1; i >= 0; i--)
                        stackBuffer[stackTop++] = current[i];

                    continue;
                }

                if (current.ValueKind != JsonValueKind.Object)
                    continue;

                // Проверяем, является ли это целевым элементом
                bool isItem = false;
                foreach (var prop in current.EnumerateObject())
                {
                    if (ItemRendererNames.Contains(prop.Name))
                    {
                        if (prop.Name == "lockupViewModel")
                        {
                            if (prop.Value.TryGetProperty("contentId", out _))
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
                    // Классифицируем и извлекаем inline
                    ClassifyAndExtract(current, videos, playlists, channels, ref token);
                    continue;
                }

                // Добавляем дочерние контейнеры
                foreach (var prop in current.EnumerateObject())
                {
                    if (ContainerNames.Contains(prop.Name))
                    {
                        // Проверяем размер стека
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
            switch (prop.Name)
            {
                case "continuationItemRenderer":
                    token ??= prop.Value.GetPropertyOrNull("continuationEndpoint")
                        ?.GetPropertyOrNull("continuationCommand")
                        ?.GetPropertyOrNull("token")?.GetStringOrNull();
                    return;

                case "musicResponsiveListItemRenderer":
                    ProcessMusicItem(prop.Value, videos, playlists, channels);
                    return;

                case "videoRenderer":
                case "shortsLockupViewModel":
                case "reelItemRenderer":
                    {
                        var videoData = new VideoData(prop.Value, isYtm: false);
                        if (!string.IsNullOrEmpty(videoData.Id))
                            videos.Add(videoData);
                        return;
                    }

                case "lockupViewModel":
                    {
                        var contentId = prop.Value.GetPropertyOrNull("contentId")?.GetStringOrNull();
                        if (!string.IsNullOrEmpty(contentId) && IsPlaylistId(contentId))
                            playlists.Add(new PlaylistData(prop.Value));
                        return;
                    }

                case "playlistRenderer":
                    playlists.Add(new PlaylistData(prop.Value));
                    return;

                case "channelRenderer":
                    channels.Add(new ChannelData(prop.Value));
                    return;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPlaylistId(string id)
    {
        // Span-based проверка без аллокации
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
                    foreach (var prop in current.EnumerateObject())
                    {
                        if (prop.Name == "continuationCommand")
                        {
                            var token = prop.Value.GetPropertyOrNull("token")?.GetStringOrNull();
                            if (token != null) return token;
                        }
                        else if (prop.Name == "nextContinuationData")
                        {
                            var token = prop.Value.GetPropertyOrNull("continuation")?.GetStringOrNull();
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

    // VideoData с кэшированием свойств при первом доступе
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
                (content.TryGetProperty("shortsLockupViewModel", out _) ||
                 content.TryGetProperty("reelItemRenderer", out _));
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
                var vid = content.GetPropertyOrNull("playlistItemData")
                    ?.GetPropertyOrNull("videoId")?.GetStringOrNull();
                if (!string.IsNullOrEmpty(vid)) return vid;

                var nav = content.GetPropertyOrNull("playNavigationEndpoint")
                    ?? content.GetPropertyOrNull("navigationEndpoint");
                return nav?.GetPropertyOrNull("watchEndpoint")
                    ?.GetPropertyOrNull("videoId")?.GetStringOrNull();
            }

            var id = content.GetPropertyOrNull("videoId")?.GetStringOrNull();
            if (!string.IsNullOrEmpty(id)) return id;

            return content.GetPropertyOrNull("onTap")
                ?.GetPropertyOrNull("innertubeCommand")
                ?.GetPropertyOrNull("reelWatchEndpoint")
                ?.GetPropertyOrNull("videoId")?.GetStringOrNull();
        }

        private static string? ComputeTitle(JsonElement content, bool isYtm)
        {
            if (isYtm) return GetRunText(content, 0);

            var titleProp = content.GetPropertyOrNull("title");
            if (titleProp.HasValue)
            {
                // Без LINQ: берём первый run вручную
                var firstRun = titleProp.Value.GetPropertyOrNull("runs")
                    ?.GetFirstArrayElementOrNull();
                if (firstRun.HasValue)
                    return firstRun.Value.GetPropertyOrNull("text")?.GetStringOrNull();

                return titleProp.Value.GetPropertyOrNull("simpleText")?.GetStringOrNull();
            }

            return content.GetPropertyOrNull("overlayMetadata")
                ?.GetPropertyOrNull("primaryText")
                ?.GetPropertyOrNull("content")?.GetStringOrNull();
        }

        private static string? ComputeAuthor(JsonElement content, bool isYtm)
        {
            if (isYtm)
            {
                var runsElement = GetRunsElement(content, 1);
                if (runsElement == null) return null;

                // Первый проход: ищем артиста
                foreach (var run in runsElement.Value.EnumerateArray())
                {
                    var pageType = run.GetPropertyOrNull("navigationEndpoint")
                        ?.GetPropertyOrNull("browseEndpoint")
                        ?.GetPropertyOrNull("browseEndpointContextSupportedConfigs")
                        ?.GetPropertyOrNull("browseEndpointContextMusicConfig")
                        ?.GetPropertyOrNull("pageType")?.GetStringOrNull();

                    if (pageType is "MUSIC_PAGE_TYPE_ARTIST" or "MUSIC_PAGE_TYPE_USER_CHANNEL")
                        return run.GetPropertyOrNull("text")?.GetStringOrNull();
                }

                // Второй проход: первый текст
                var first = runsElement.Value.GetFirstArrayElementOrNull();
                return first?.GetPropertyOrNull("text")?.GetStringOrNull();
            }

            var ownerFirstRun = content.GetPropertyOrNull("ownerText")
                ?.GetPropertyOrNull("runs")?.GetFirstArrayElementOrNull();
            if (ownerFirstRun.HasValue)
                return ownerFirstRun.Value.GetPropertyOrNull("text")?.GetStringOrNull();

            var bylineFirstRun = content.GetPropertyOrNull("shortBylineText")
                ?.GetPropertyOrNull("runs")?.GetFirstArrayElementOrNull();
            if (bylineFirstRun.HasValue)
                return bylineFirstRun.Value.GetPropertyOrNull("text")?.GetStringOrNull();

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
                    var id = run.GetPropertyOrNull("navigationEndpoint")
                        ?.GetPropertyOrNull("browseEndpoint")
                        ?.GetPropertyOrNull("browseId")?.GetStringOrNull();

                    if (id != null && id.AsSpan().StartsWith("UC"))
                        return id;
                }
                return null;
            }

            var ownerRuns = content.GetPropertyOrNull("ownerText")
                ?.GetPropertyOrNull("runs");
            if (ownerRuns.HasValue)
            {
                foreach (var run in ownerRuns.Value.EnumerateArrayOrEmpty())
                {
                    var id = run.GetPropertyOrNull("navigationEndpoint")
                        ?.GetPropertyOrNull("browseEndpoint")
                        ?.GetPropertyOrNull("browseId")?.GetStringOrNull();
                    if (id != null) return id;
                }
            }

            return content.GetPropertyOrNull("channelThumbnailSupportedRenderers")
                ?.GetPropertyOrNull("channelThumbnailWithLinkRenderer")
                ?.GetPropertyOrNull("navigationEndpoint")
                ?.GetPropertyOrNull("browseEndpoint")
                ?.GetPropertyOrNull("browseId")?.GetStringOrNull();
        }

        private static bool ComputeIsOfficialArtist(JsonElement content, bool isYtm)
        {
            if (isYtm) return true;

            var badges = content.GetPropertyOrNull("ownerBadges");
            if (badges == null) return false;

            foreach (var badge in badges.Value.EnumerateArrayOrEmpty())
            {
                var iconType = badge.GetPropertyOrNull("metadataBadgeRenderer")
                    ?.GetPropertyOrNull("icon")
                    ?.GetPropertyOrNull("iconType")?.GetStringOrNull();
                if (iconType == "AUDIO_BADGE") return true;
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
                    var text = run.GetPropertyOrNull("text")?.GetStringOrNull();
                    if (text != null && text.Contains(':') && !text.Contains('•'))
                    {
                        var ts = YoutubeClientUtils.DurationParser.Parse(text);
                        if (ts.HasValue) return ts;
                    }
                }
                return null;
            }

            var textDuration = content.GetPropertyOrNull("lengthText")
                ?.GetPropertyOrNull("simpleText")?.GetStringOrNull();

            return textDuration != null ? YoutubeClientUtils.DurationParser.Parse(textDuration) : null;
        }

        internal static IReadOnlyList<ThumbnailData> ComputeThumbnails(JsonElement content)
        {
            var thumbsElement = content.GetPropertyOrNull("thumbnail")
                ?.GetPropertyOrNull("musicThumbnailRenderer")
                ?.GetPropertyOrNull("thumbnail")
                ?.GetPropertyOrNull("thumbnails");

            thumbsElement ??= content.GetPropertyOrNull("thumbnail")
                ?.GetPropertyOrNull("thumbnails");

            thumbsElement ??= content.GetPropertyOrNull("thumbnailViewModel")
                ?.GetPropertyOrNull("image")
                ?.GetPropertyOrNull("sources");

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

            var pageType = content.GetPropertyOrNull("navigationEndpoint")
                ?.GetPropertyOrNull("browseEndpoint")
                ?.GetPropertyOrNull("browseEndpointContextSupportedConfigs")
                ?.GetPropertyOrNull("browseEndpointContextMusicConfig")
                ?.GetPropertyOrNull("pageType")?.GetStringOrNull();

            return pageType == "MUSIC_PAGE_TYPE_ALBUM" || (id == null && title != null);
        }

        private static bool ComputeIsArtistContext(JsonElement content, bool isYtm)
        {
            if (!isYtm) return false;

            var pageType = content.GetPropertyOrNull("navigationEndpoint")
                ?.GetPropertyOrNull("browseEndpoint")
                ?.GetPropertyOrNull("browseEndpointContextSupportedConfigs")
                ?.GetPropertyOrNull("browseEndpointContextMusicConfig")
                ?.GetPropertyOrNull("pageType")?.GetStringOrNull();

            return pageType == "MUSIC_PAGE_TYPE_ARTIST";
        }

        /// <summary>
        /// Возвращает JsonElement массива runs вместо IEnumerable — без аллокации.
        /// </summary>
        private static JsonElement? GetRunsElement(JsonElement item, int columnIndex)
        {
            var cols = item.GetPropertyOrNull("flexColumns");
            if (cols == null) return null;

            var col = cols.Value.GetArrayElementOrNull(columnIndex);
            if (col == null) return null;

            return col.Value.GetPropertyOrNull("musicResponsiveListItemFlexColumnRenderer")
                ?.GetPropertyOrNull("text")
                ?.GetPropertyOrNull("runs");
        }

        /// <summary>
        /// Собирает текст всех runs без лишних аллокаций.
        /// </summary>
        public static string? GetRunText(JsonElement item, int columnIndex)
        {
            var runsElement = GetRunsElement(item, columnIndex);
            if (runsElement == null) return null;

            var runs = runsElement.Value;
            var len = runs.GetArrayLength();
            if (len == 0) return null;

            // Быстрый путь: один run
            if (len == 1)
                return runs[0].GetPropertyOrNull("text")?.GetStringOrNull();

            // Несколько runs: используем StringBuilder
            StringBuilder? sb = null;
            string? first = null;

            for (int i = 0; i < len; i++)
            {
                var text = runs[i].GetPropertyOrNull("text")?.GetStringOrNull();
                if (text == null) continue;

                if (first == null)
                {
                    first = text;
                }
                else
                {
                    sb ??= new StringBuilder(first.Length + text.Length * (len - 1));
                    if (sb.Length == 0) sb.Append(first);
                    sb.Append(text);
                }
            }

            return sb?.ToString() ?? first;
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
            Id = content.GetPropertyOrNull("playlistId")?.GetStringOrNull() ??
                 content.GetPropertyOrNull("contentId")?.GetStringOrNull() ??
                 (isYtm ? content.GetPropertyOrNull("navigationEndpoint")
                     ?.GetPropertyOrNull("browseEndpoint")
                     ?.GetPropertyOrNull("browseId")?.GetStringOrNull() : null);

            var titleProp = content.GetPropertyOrNull("title");
            if (titleProp.HasValue)
            {
                var simple = titleProp.Value.GetPropertyOrNull("simpleText")?.GetStringOrNull();
                if (simple != null)
                {
                    Title = simple;
                }
                else
                {
                    var firstRun = titleProp.Value.GetPropertyOrNull("runs")?.GetFirstArrayElementOrNull();
                    if (firstRun.HasValue)
                        Title = firstRun.Value.GetPropertyOrNull("text")?.GetStringOrNull();
                }
            }

            if (Title is null)
            {
                var lockupTitle = content.GetPropertyOrNull("metadata")
                    ?.GetPropertyOrNull("lockupMetadataViewModel")
                    ?.GetPropertyOrNull("title")
                    ?.GetPropertyOrNull("content")?.GetStringOrNull();

                if (lockupTitle != null)
                    Title = lockupTitle;
                else if (isYtm)
                    Title = VideoData.GetRunText(content, 0);
            }

            var firstRunAuthor = content.GetPropertyOrNull("shortBylineText")
                ?.GetPropertyOrNull("runs")?.GetFirstArrayElementOrNull();
            if (firstRunAuthor.HasValue)
                Author = firstRunAuthor.Value.GetPropertyOrNull("text")?.GetStringOrNull();
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
            Id = content.GetPropertyOrNull("channelId")?.GetStringOrNull() ??
                 (isYtm ? content.GetPropertyOrNull("navigationEndpoint")
                     ?.GetPropertyOrNull("browseEndpoint")
                     ?.GetPropertyOrNull("browseId")?.GetStringOrNull() : null);

            Title = content.GetPropertyOrNull("title")?.GetPropertyOrNull("simpleText")?.GetStringOrNull() ??
                    (isYtm ? VideoData.GetRunText(content, 0) : null);

            Thumbnails = VideoData.ComputeThumbnails(content);
        }
    }
}