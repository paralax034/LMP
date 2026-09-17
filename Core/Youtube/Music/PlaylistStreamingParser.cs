using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LMP.Core.Helpers.Extensions;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Youtube.Music;

/// <summary>
/// Высокопроизводительный однопроходный потоковый парсер структуры плейлистов YouTube Music (InnerTube).
/// Читает ответы API напрямую из сетевого буфера пула памяти без аллокаций в LOH и без построения полного DOM дерева.
/// </summary>
internal static class PlaylistStreamingParser
{
    private const string GreyOutPolicy = "MUSIC_ITEM_RENDERER_DISPLAY_POLICY_GREY_OUT";

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>
    /// Выполняет потоковый разбор ответа <c>browse</c> плейлиста.
    /// Извлекает метаданные шапки, начальную партию треков и продолжение за один проход.
    /// </summary>
    /// <param name="stream">Сетевой поток HTTP-ответа.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Полный снимок плейлиста, токен продолжения и visitorData.</returns>
    public static async ValueTask<(FullPlaylistSyncData Data, string? ContinuationToken, string? VisitorData)> ParseBrowseAsync(
        Stream stream,
        CancellationToken ct = default)
    {
        using var buffer = new InnerTubeStreamBuffer();
        await buffer.ReadAllAsync(stream, ct).ConfigureAwait(false);

        var result = new FullPlaylistSyncData();
        string? continuationToken = null;
        string? visitorData = null;

        var reader = new Utf8JsonReader(buffer.WrittenSpan, ReaderOptions);

        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            // 1. Извлечение VisitorData
            if (reader.ValueTextEquals(InnerTubeTokens.VisitorData) && visitorData is null)
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.String)
                    visitorData = reader.GetString();
                continue;
            }

            // 2. Изолированный парсинг шапки (Парето-оптимизация: ~3 КБ вместо 1 МБ)
            if (reader.ValueTextEquals(InnerTubeTokens.MusicResponsiveHeaderRenderer) ||
                reader.ValueTextEquals(InnerTubeTokens.MusicDetailHeaderRenderer) ||
                reader.ValueTextEquals(InnerTubeTokens.MusicEditablePlaylistDetailHeaderRenderer))
            {
                using var headerDoc = JsonDocument.ParseValue(ref reader);
                PopulateHeaderMetadata(headerDoc.RootElement, result);
                continue;
            }

            // 3. Сканирование треков YouTube Music (~1.5 КБ мини-объект в Gen 0 без сбоев глубины)
            if (reader.ValueTextEquals(InnerTubeTokens.MusicResponsiveListItemRenderer))
            {
                using var itemDoc = JsonDocument.ParseValue(ref reader);
                if (TryParseMusicItem(itemDoc.RootElement, result.Tracks.Count, out var track))
                {
                    result.Tracks.Add(track);
                }
                continue;
            }

            // 4. Fallback: сканирование треков классического YouTube Web
            if (reader.ValueTextEquals(InnerTubeTokens.PlaylistVideoRenderer))
            {
                using var itemDoc = JsonDocument.ParseValue(ref reader);
                if (TryParseWebItem(itemDoc.RootElement, result.Tracks.Count, out var track))
                {
                    result.Tracks.Add(track);
                }
                continue;
            }

            // 5. Поиск токена продолжения
            if (reader.ValueTextEquals(InnerTubeTokens.ContinuationItemRenderer))
            {
                continuationToken ??= TryExtractContinuationItemToken(ref reader);
                continue;
            }

            if (reader.ValueTextEquals(InnerTubeTokens.NextContinuationData))
            {
                continuationToken ??= TryExtractNextContinuationDataToken(ref reader);
                continue;
            }

            // Мусорные узлы телеметрии сбрасываем мгновенно без рекурсии
            if (reader.ValueTextEquals(InnerTubeTokens.TrackingParams) ||
                reader.ValueTextEquals(InnerTubeTokens.ClickTrackingParams) ||
                reader.ValueTextEquals(InnerTubeTokens.ServiceTrackingParams) ||
                reader.ValueTextEquals(InnerTubeTokens.CommandMetadata))
            {
                reader.Skip();
            }
        }

        return (result, continuationToken, visitorData);
    }

    /// <summary>
    /// Потоковый разбор ответа продолжения пагинации (<c>continuation</c>).
    /// </summary>
    /// <param name="stream">Сетевой поток HTTP-ответа продолжения.</param>
    /// <param name="currentTrackCount">Текущее количество треков в плейлисте для индексации позиции.</param>
    /// <param name="ct">Токен отмены операции.</param>
    public static async ValueTask<(List<RemoteTrackInfo> Tracks, string? ContinuationToken, string? VisitorData)> ParseContinuationAsync(
        Stream stream,
        int currentTrackCount,
        CancellationToken ct = default)
    {
        using var buffer = new InnerTubeStreamBuffer();
        await buffer.ReadAllAsync(stream, ct).ConfigureAwait(false);

        var tracks = new List<RemoteTrackInfo>(64);
        string? continuationToken = null;
        string? visitorData = null;

        var reader = new Utf8JsonReader(buffer.WrittenSpan, ReaderOptions);

        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            if (reader.ValueTextEquals(InnerTubeTokens.VisitorData) && visitorData is null)
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.String)
                    visitorData = reader.GetString();
                continue;
            }

            if (reader.ValueTextEquals(InnerTubeTokens.MusicResponsiveListItemRenderer))
            {
                using var itemDoc = JsonDocument.ParseValue(ref reader);
                if (TryParseMusicItem(itemDoc.RootElement, currentTrackCount + tracks.Count, out var track))
                {
                    tracks.Add(track);
                }
                continue;
            }

            if (reader.ValueTextEquals(InnerTubeTokens.PlaylistVideoRenderer))
            {
                using var itemDoc = JsonDocument.ParseValue(ref reader);
                if (TryParseWebItem(itemDoc.RootElement, currentTrackCount + tracks.Count, out var track))
                {
                    tracks.Add(track);
                }
                continue;
            }

            if (reader.ValueTextEquals(InnerTubeTokens.ContinuationItemRenderer))
            {
                continuationToken ??= TryExtractContinuationItemToken(ref reader);
                continue;
            }

            if (reader.ValueTextEquals(InnerTubeTokens.NextContinuationData))
            {
                continuationToken ??= TryExtractNextContinuationDataToken(ref reader);
                continue;
            }

            if (reader.ValueTextEquals(InnerTubeTokens.TrackingParams) ||
                reader.ValueTextEquals(InnerTubeTokens.ClickTrackingParams) ||
                reader.ValueTextEquals(InnerTubeTokens.ServiceTrackingParams) ||
                reader.ValueTextEquals(InnerTubeTokens.CommandMetadata))
            {
                reader.Skip();
            }
        }

        return (tracks, continuationToken, visitorData);
    }

    #region Item Parsers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryParseMusicItem(
        JsonElement musicRenderer,
        int position,
        out RemoteTrackInfo track)
    {
        track = default;

        var playlistItemData = musicRenderer.GetPropertyOrNull("playlistItemData");
        var videoId = playlistItemData?.GetPropertyOrNull("videoId")?.GetStringOrNull()
            ?? musicRenderer.FindFirstDescendantProperty("videoId")?.GetStringOrNull();

        if (string.IsNullOrEmpty(videoId))
            return false;

        var setVideoId = playlistItemData?.GetPropertyOrNull("playlistSetVideoId")?.GetStringOrNull()
            ?? playlistItemData?.GetPropertyOrNull("setVideoId")?.GetStringOrNull()
            ?? musicRenderer.GetPropertyOrNull("playlistSetVideoId")?.GetStringOrNull()
            ?? musicRenderer.FindFirstDescendantProperty("playlistSetVideoId")?.GetStringOrNull()
            ?? musicRenderer.FindFirstDescendantProperty("setVideoId")?.GetStringOrNull();

        var flexCols = musicRenderer.GetPropertyOrNull("flexColumns");
        var title = flexCols?.GetArrayElementOrNull(0)
            ?.GetPropertyOrNull("musicResponsiveListItemFlexColumnRenderer")
            ?.GetPropertyOrNull("text")
            ?.GetPropertyOrNull("runs")
            ?.GetFirstArrayElementOrNull()
            ?.GetPropertyOrNull("text")?.GetStringOrNull() ?? string.Empty;

        string author = string.Empty;
        var metaRuns = flexCols?.GetArrayElementOrNull(1)
            ?.GetPropertyOrNull("musicResponsiveListItemFlexColumnRenderer")
            ?.GetPropertyOrNull("text")
            ?.GetPropertyOrNull("runs");

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
                    {
                        author = text;
                        break;
                    }
                }
                else if (string.IsNullOrEmpty(author) && !text.Contains("views", StringComparison.OrdinalIgnoreCase) && !text.Contains("Song", StringComparison.OrdinalIgnoreCase) && !text.Contains(':'))
                {
                    author = text;
                }
            }
        }

        int durationSeconds = 0;
        var durationText = musicRenderer.GetPropertyOrNull("fixedColumns")
            ?.GetFirstArrayElementOrNull()
            ?.GetPropertyOrNull("musicResponsiveListItemFixedColumnRenderer")
            ?.GetPropertyOrNull("text")
            ?.GetPropertyOrNull("runs")
            ?.GetFirstArrayElementOrNull()
            ?.GetPropertyOrNull("text")?.GetStringOrNull();

        if (durationText != null)
        {
            var parsedDur = YoutubeClientUtils.DurationParser.Parse(durationText);
            if (parsedDur.HasValue)
                durationSeconds = (int)parsedDur.Value.TotalSeconds;
        }

        var thumbUrl = ExtractLastThumbnailUrl(musicRenderer);
        var policy = musicRenderer.GetPropertyOrNull("musicItemRendererDisplayPolicy")?.GetStringOrNull();
        bool isPlayable = !string.Equals(policy, GreyOutPolicy, StringComparison.Ordinal);

        track = new RemoteTrackInfo(
            VideoId: videoId,
            SetVideoId: setVideoId ?? string.Empty,
            Title: title,
            Author: author,
            DurationSeconds: durationSeconds,
            ThumbnailUrl: thumbUrl,
            IsPlayable: isPlayable,
            Position: position);

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryParseWebItem(
        JsonElement renderer,
        int position,
        out RemoteTrackInfo track)
    {
        track = default;

        var videoId = renderer.GetPropertyOrNull("videoId")?.GetStringOrNull();
        if (string.IsNullOrEmpty(videoId))
            return false;

        var setVideoId = renderer.GetPropertyOrNull("playlistSetVideoId")?.GetStringOrNull()
            ?? renderer.GetPropertyOrNull("setVideoId")?.GetStringOrNull()
            ?? renderer.FindFirstDescendantProperty("playlistSetVideoId")?.GetStringOrNull()
            ?? renderer.FindFirstDescendantProperty("setVideoId")?.GetStringOrNull();

        var title = renderer.GetPropertyOrNull("title")?.GetPropertyOrNull("simpleText")?.GetStringOrNull()
            ?? YoutubeParsingHelpers.ConcatTextRuns(renderer.GetPropertyOrNull("title")?.GetPropertyOrNull("runs"))
            ?? string.Empty;

        var author = YoutubeParsingHelpers.ConcatTextRuns(renderer.GetPropertyOrNull("shortBylineText")?.GetPropertyOrNull("runs"))
            ?? string.Empty;

        int durationSeconds = 0;
        var lenStr = renderer.GetPropertyOrNull("lengthSeconds")?.GetStringOrNull();
        if (!string.IsNullOrEmpty(lenStr))
            int.TryParse(lenStr, CultureInfo.InvariantCulture, out durationSeconds);

        var thumbUrl = ExtractLastThumbnailUrl(renderer);
        var policy = renderer.GetPropertyOrNull("musicItemRendererDisplayPolicy")?.GetStringOrNull();
        bool isPlayable = !string.Equals(policy, GreyOutPolicy, StringComparison.Ordinal);

        track = new RemoteTrackInfo(
            VideoId: videoId,
            SetVideoId: setVideoId ?? string.Empty,
            Title: title,
            Author: author,
            DurationSeconds: durationSeconds,
            ThumbnailUrl: thumbUrl,
            IsPlayable: isPlayable,
            Position: position);

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string ExtractLastThumbnailUrl(JsonElement renderer)
    {
        var thumbs = (renderer.GetPropertyOrNull("thumbnail")
            ?.GetPropertyOrNull("thumbnails")) ?? renderer.FindFirstDescendantProperty("thumbnails");

        if (thumbs is null || thumbs.Value.ValueKind != JsonValueKind.Array)
            return string.Empty;

        int len = thumbs.Value.GetArrayLength();
        if (len == 0)
            return string.Empty;

        return thumbs.Value[len - 1]
            .GetPropertyOrNull("url")
            ?.GetStringOrNull() ?? string.Empty;
    }

    private static string? TryExtractContinuationItemToken(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return null;

        int depth = reader.CurrentDepth;
        while (reader.Read() && reader.CurrentDepth >= depth)
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals(InnerTubeTokens.Token))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.String)
                    return reader.GetString();
            }
        }
        return null;
    }

    private static string? TryExtractNextContinuationDataToken(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return null;

        int depth = reader.CurrentDepth;
        while (reader.Read() && reader.CurrentDepth >= depth)
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals(InnerTubeTokens.Continuation))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.String)
                    return reader.GetString();
            }
        }
        return null;
    }

    #endregion

    #region Header Extraction

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PopulateHeaderMetadata(JsonElement headerElement, FullPlaylistSyncData target)
    {
        var targetElement = headerElement;
        if (headerElement.TryGetProperty("header", out var nestedHeader))
        {
            if (nestedHeader.TryGetProperty("musicResponsiveHeaderRenderer", out var nestedResponsive))
                targetElement = nestedResponsive;
        }

        target.Title ??= targetElement.GetPropertyOrNull("title")?.GetPropertyOrNull("runs")
            ?.GetFirstArrayElementOrNull()?.GetPropertyOrNull("text")?.GetStringOrNull()
            ?? targetElement.GetPropertyOrNull("title")?.GetPropertyOrNull("simpleText")?.GetStringOrNull();

        target.Description ??= targetElement.GetPropertyOrNull("description")?.GetPropertyOrNull("runs")
            ?.GetFirstArrayElementOrNull()?.GetPropertyOrNull("text")?.GetStringOrNull()
            ?? targetElement.GetPropertyOrNull("description")?.GetPropertyOrNull("simpleText")?.GetStringOrNull();

        var thumbs = targetElement.GetPropertyOrNull("thumbnail")?.GetPropertyOrNull("musicThumbnailRenderer")
            ?.GetPropertyOrNull("thumbnail")?.GetPropertyOrNull("thumbnails")
            ?? targetElement.GetPropertyOrNull("thumbnail")?.GetPropertyOrNull("thumbnails");

        if (thumbs is { ValueKind: JsonValueKind.Array } arr && arr.GetArrayLength() > 0)
        {
            target.ThumbnailUrl = arr[arr.GetArrayLength() - 1].GetPropertyOrNull("url")?.GetStringOrNull();
        }

        var viewsText = targetElement.GetPropertyOrNull("secondSubtitle")?.GetPropertyOrNull("runs")
            ?.GetFirstArrayElementOrNull()?.GetPropertyOrNull("text")?.GetStringOrNull();

        if (!string.IsNullOrEmpty(viewsText))
            target.ViewCount = YoutubeParsingHelpers.ParseLongFromText(viewsText);

        var subtitleRuns = targetElement.GetPropertyOrNull("subtitle")?.GetPropertyOrNull("runs");
        if (subtitleRuns is { ValueKind: JsonValueKind.Array } sRuns && sRuns.GetArrayLength() > 0)
        {
            for (int i = sRuns.GetArrayLength() - 1; i >= 0; i--)
            {
                var text = sRuns[i].GetPropertyOrNull("text")?.GetStringOrNull();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var year = YoutubeParsingHelpers.ParseYearFromText(text);
                    if (year.HasValue)
                    {
                        target.ReleaseDate = new DateOnly(year.Value, 1, 1);
                        break;
                    }
                }
            }
        }
    }

    #endregion
}