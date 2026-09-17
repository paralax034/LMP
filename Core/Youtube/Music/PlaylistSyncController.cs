using System.Buffers;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LMP.Core.Youtube.Utils;
using LMP.Core.Helpers.Extensions;
using System.Globalization;

namespace LMP.Core.Youtube.Music;

internal sealed class PlaylistSyncController(HttpClient http)
{
    private const string WebApiUrl = "https://www.youtube.com/youtubei/v1";
    private const string MusicApiUrl = "https://music.youtube.com/youtubei/v1";
    private const string ConstantParams = "qAIC";

    private static readonly MediaTypeHeaderValue JsonContentType = new("application/json");

    private static readonly byte[] Utf8Context = "context"u8.ToArray();
    private static readonly byte[] Utf8Client = "client"u8.ToArray();
    private static readonly byte[] Utf8ClientName = "clientName"u8.ToArray();
    private static readonly byte[] Utf8ClientVersion = "clientVersion"u8.ToArray();
    private static readonly byte[] Utf8Request = "request"u8.ToArray();
    private static readonly byte[] Utf8UseSsl = "useSsl"u8.ToArray();
    private static readonly byte[] Utf8Web = "WEB"u8.ToArray();
    private static readonly byte[] Utf8WebRemix = "WEB_REMIX"u8.ToArray();
    private static readonly byte[] Utf8Hl = "hl"u8.ToArray();
    private static readonly byte[] Utf8Gl = "gl"u8.ToArray();
    private static readonly byte[] Utf8VisitorData = "visitorData"u8.ToArray();

    #region Context Writers

    /// <summary>
    /// Minimal WEB context for playlist listing.
    /// </summary>
    private static void WriteWebContext(Utf8JsonWriter writer)
    {
        writer.WritePropertyName(Utf8Context);
        writer.WriteStartObject();

        writer.WritePropertyName(Utf8Client);
        writer.WriteStartObject();
        writer.WriteString(Utf8ClientName, Utf8Web);
        writer.WriteString(Utf8ClientVersion, YoutubeHttpHandler.WebClientVersion);
        writer.WriteEndObject();

        writer.WritePropertyName(Utf8Request);
        writer.WriteStartObject();
        writer.WriteBoolean(Utf8UseSsl, false);
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    /// <summary>
    /// WEB_REMIX context for YouTube Music browse requests.
    /// </summary>
    private static void WriteWebRemixContext(Utf8JsonWriter writer)
    {
        writer.WritePropertyName(Utf8Context);
        writer.WriteStartObject();

        writer.WritePropertyName(Utf8Client);
        writer.WriteStartObject();
        writer.WriteString(Utf8ClientName, Utf8WebRemix);
        writer.WriteString(Utf8ClientVersion, YoutubeHttpHandler.MusicClientVersion);
        writer.WriteString(Utf8Hl, YoutubeHttpHandler.GetHl());
        writer.WriteString(Utf8Gl, YoutubeHttpHandler.GetGl());

        var visitorData = YoutubeClientUtils.VisitorData;
        if (!string.IsNullOrEmpty(visitorData))
            writer.WriteString(Utf8VisitorData, visitorData);
        else
            writer.WriteNull(Utf8VisitorData);

        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    #endregion

    #region HTTP

    private static ReadOnlyMemoryContent CreateJsonContent(Action<Utf8JsonWriter> writeBody)
    {
        var bufferWriter = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(bufferWriter))
        {
            writer.WriteStartObject();
            writeBody(writer);
            writer.WriteEndObject();
        }

        // Оптимизация: нулевое копирование промежуточного массива
        var content = new ReadOnlyMemoryContent(bufferWriter.WrittenMemory);
        content.Headers.ContentType = JsonContentType;
        return content;
    }

    private async Task<JsonElement> PostWebAsync(
        string endpoint,
        Action<Utf8JsonWriter> writeBody,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{WebApiUrl}/{endpoint}?prettyPrint=false");
        request.Content = CreateJsonContent(writer =>
        {
            WriteWebContext(writer);
            writeBody(writer);
        });

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await Json.ParseAsync(stream, ct);
    }

    private async Task<(HttpResponseMessage Response, Stream Stream)> PostMusicStreamAsync(
        string endpoint,
        Action<Utf8JsonWriter> writeBody,
        CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{MusicApiUrl}/{endpoint}?prettyPrint=false");

        var visitorData = YoutubeClientUtils.VisitorData;
        if (!string.IsNullOrEmpty(visitorData))
            request.Options.Set(YoutubeHttpHandler.VisitorDataKey, visitorData);

        request.Content = CreateJsonContent(writer =>
        {
            WriteWebRemixContext(writer);
            writeBody(writer);
        });

        var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return (response, stream);
    }

    #endregion

    #region Full Playlist Data (WEB_REMIX Streaming)

    /// <summary>
    /// Получает полный снимок плейлиста за минимальное количество HTTP-запросов.
    /// Использует потоковый конвейер без промежуточных LOH-строк и DOM-документов.
    /// </summary>
    public async Task<FullPlaylistSyncData> GetFullPlaylistDataAsync(
      string playlistId,
      CancellationToken ct = default)
    {
        var browseId = playlistId.StartsWith("VL", StringComparison.Ordinal)
            ? playlistId
            : "VL" + playlistId;

        FullPlaylistSyncData playlistData;
        string? continuationToken;
        string? freshVisitor;

        {
            var (response, stream) = await PostMusicStreamAsync("browse", writer =>
            {
                writer.WriteString("browseId", browseId);
            }, ct).ConfigureAwait(false);

            using (response)
            await using (stream.ConfigureAwait(false))
            {
                (playlistData, continuationToken, freshVisitor) =
                    await PlaylistStreamingParser.ParseBrowseAsync(stream, ct).ConfigureAwait(false);
            }
        }

        if (!string.IsNullOrWhiteSpace(freshVisitor) && freshVisitor != YoutubeClientUtils.VisitorData)
            YoutubeClientUtils.VisitorData = freshVisitor;

        int page = 0;

        while (!string.IsNullOrEmpty(continuationToken) && page < 50)
        {
            ct.ThrowIfCancellationRequested();

            var (contResponse, contStream) = await PostMusicStreamAsync("browse", writer =>
            {
                writer.WriteString("continuation", continuationToken);
            }, ct).ConfigureAwait(false);

            List<RemoteTrackInfo> continuationTracks;
            string? nextContinuation;

            using (contResponse)
            await using (contStream.ConfigureAwait(false))
            {
                (continuationTracks, nextContinuation, freshVisitor) =
                    await PlaylistStreamingParser.ParseContinuationAsync(contStream, playlistData.Tracks.Count, ct).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(freshVisitor) && freshVisitor != YoutubeClientUtils.VisitorData)
                YoutubeClientUtils.VisitorData = freshVisitor;

            if (continuationTracks.Count == 0)
                break;

            playlistData.Tracks.AddRange(continuationTracks);
            continuationToken = nextContinuation;
            page++;
        }

        Log.Debug($"[PlaylistSync] Streamed header: title='{playlistData.Title ?? ""}', " +
                  $"desc='{playlistData.Description?.Length ?? 0} chars', thumb='{playlistData.ThumbnailUrl ?? "null"}', " +
                  $"views={playlistData.ViewCount?.ToString() ?? "null"}, date='{playlistData.ReleaseDate ?? null}'");
        Log.Debug($"[PlaylistSync] WEB_REMIX: streamed {playlistData.Tracks.Count} tracks for {playlistId}");

        return playlistData;
    }

    #endregion

    #region User Playlists (WEB client)

    /// <summary>
    /// Gets all user playlists via WEB client with pagination.
    /// Uses FEplaylist_aggregation with "qAIC" filter (Music playlists).
    /// Parses lockupViewModel for compact data.
    /// </summary>
    public async IAsyncEnumerable<List<RemotePlaylistInfo>> GetUserPlaylistsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var root = await PostWebAsync("browse", writer =>
        {
            writer.WriteString("browseId", "FEplaylist_aggregation");
            writer.WriteString("params", ConstantParams);
        }, ct);

        var batch = ParseLockupViewModels(root);
        if (batch.Count > 0)
            yield return batch;

        var continuation = ExtractWebContinuationToken(root);
        int page = 0;

        while (!string.IsNullOrEmpty(continuation) && page < 50)
        {
            ct.ThrowIfCancellationRequested();

            var contRoot = await PostWebAsync("browse", writer =>
            {
                writer.WriteString("continuation", continuation);
            }, ct);

            var contBatch = ParseLockupViewModelsFromContinuation(contRoot);
            if (contBatch.Count == 0)
                break;

            yield return contBatch;

            continuation = ExtractWebContinuationToken(contRoot);
            page++;
        }
    }

    private static List<RemotePlaylistInfo> ParseLockupViewModels(JsonElement root)
    {
        var results = new List<RemotePlaylistInfo>(32);
        var lockups = new List<JsonElement>(32);
        root.EnumerateDescendantProperties("lockupViewModel", lockups);

        for (int i = 0; i < lockups.Count; i++)
        {
            var info = ParseSingleLockup(lockups[i]);
            if (info.HasValue)
                results.Add(info.Value);
        }

        return results;
    }

    private static List<RemotePlaylistInfo> ParseLockupViewModelsFromContinuation(JsonElement root)
    {
        var results = new List<RemotePlaylistInfo>(32);

        var commands = root.GetPropertyOrNull("onResponseReceivedCommands");
        if (commands?.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var cmd in commands.Value.EnumerateArray())
        {
            var items = cmd.GetPropertyOrNull("appendContinuationItemsAction")
                ?.GetPropertyOrNull("continuationItems");
            if (items?.ValueKind != JsonValueKind.Array)
                continue;

            var lockups = new List<JsonElement>(32);
            items.Value.EnumerateDescendantProperties("lockupViewModel", lockups);

            for (int i = 0; i < lockups.Count; i++)
            {
                var info = ParseSingleLockup(lockups[i]);
                if (info.HasValue)
                    results.Add(info.Value);
            }
        }

        return results;
    }

    private static RemotePlaylistInfo? ParseSingleLockup(JsonElement lockup)
    {
        var contentId = lockup.GetPropertyOrNull("contentId")?.GetStringOrNull();
        if (string.IsNullOrEmpty(contentId))
            return null;

        var title = lockup.GetPropertyOrNull("metadata")
            ?.GetPropertyOrNull("lockupMetadataViewModel")
            ?.GetPropertyOrNull("title")
            ?.GetPropertyOrNull("content")
            ?.GetStringOrNull() ?? "";

        var badgeText = lockup.GetPropertyOrNull("contentImage")
            ?.GetPropertyOrNull("collectionThumbnailViewModel")
            ?.GetPropertyOrNull("primaryThumbnail")
            ?.GetPropertyOrNull("thumbnailViewModel")
            ?.GetPropertyOrNull("overlays")
            ?.GetFirstArrayElementOrNull()
            ?.GetPropertyOrNull("thumbnailOverlayBadgeViewModel")
            ?.GetPropertyOrNull("thumbnailBadges")
            ?.GetFirstArrayElementOrNull()
            ?.GetPropertyOrNull("thumbnailBadgeViewModel")
            ?.GetPropertyOrNull("text")
            ?.GetStringOrNull();

        int trackCount = ParseTrackCount(badgeText);

        string? thumbUrl = lockup.GetPropertyOrNull("contentImage")
            ?.GetPropertyOrNull("collectionThumbnailViewModel")
            ?.GetPropertyOrNull("primaryThumbnail")
            ?.GetPropertyOrNull("thumbnailViewModel")
            ?.GetPropertyOrNull("image")
            ?.GetPropertyOrNull("sources")
            ?.GetFirstArrayElementOrNull()
            ?.GetPropertyOrNull("url")
            ?.GetStringOrNull();

        return new RemotePlaylistInfo(contentId, title, trackCount, thumbUrl);
    }

    /// <summary>
    /// Проверяет, является ли строка относительным форматом указания времени.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsRelativeDate(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        var span = text.AsSpan();
        return span.Contains("сегодня".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("today".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("вчера".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("yesterday".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("назад".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("ago".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("час".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("hour".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("минут".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("minute".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("день".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("day".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("недел".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("week".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("месяц".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Contains("month".AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractWebContinuationToken(JsonElement root)
    {
        var commands = root.GetPropertyOrNull("onResponseReceivedCommands");
        if (commands?.ValueKind == JsonValueKind.Array)
        {
            foreach (var cmd in commands.Value.EnumerateArray())
            {
                var items = cmd.GetPropertyOrNull("appendContinuationItemsAction")
                    ?.GetPropertyOrNull("continuationItems");
                if (items?.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var item in items.Value.EnumerateArray())
                {
                    var token = item.GetPropertyOrNull("continuationItemRenderer")
                        ?.GetPropertyOrNull("continuationEndpoint")
                        ?.GetPropertyOrNull("continuationCommand")
                        ?.GetPropertyOrNull("token")
                        ?.GetStringOrNull();

                    if (!string.IsNullOrEmpty(token))
                        return token;
                }
            }
        }

        var found = root.FindFirstDescendantProperty("continuationCommand");
        return found?.GetPropertyOrNull("token")?.GetStringOrNull();
    }

    /// <summary>
    /// Парсит количество треков из локализованных строк вида "4 видео", "5,000 videos".
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ParseTrackCount(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        int result = 0;
        bool foundDigit = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsAsciiDigit(c))
            {
                result = result * 10 + (c - '0');
                foundDigit = true;
            }
            else if (foundDigit && c != ',' && c != '.' && c != ' ' && c != '\u00A0')
            {
                break;
            }
        }

        return result;
    }

    #endregion

    #region Legacy Wrapper

    /// <summary>
    /// Fetches playlist tracks with setVideoId for sync/removal.
    /// </summary>
    /// <remarks>
    /// Wrapper над <see cref="GetFullPlaylistDataAsync"/> для обратной совместимости.
    /// </remarks>
    public async Task<List<RemoteTrackInfo>> GetPlaylistTracksAsync(
        string playlistId,
        CancellationToken ct = default)
    {
        var data = await GetFullPlaylistDataAsync(playlistId, ct);
        return data.Tracks;
    }

    #endregion
}

/// <summary>
/// Compact playlist info from WEB client lockupViewModel.
/// </summary>
public readonly record struct RemotePlaylistInfo(
    string PlaylistId,
    string Title,
    int TrackCount,
    string? ThumbnailUrl);

/// <summary>
/// Трек плейлиста с метаданными и setVideoId для sync/removal операций.
/// IsPlayable=false — трек недоступен в YTM, но присутствует в плейлисте.
/// </summary>
public readonly record struct RemoteTrackInfo(
    string VideoId,
    string SetVideoId,
    string Title,
    string Author,
    int DurationSeconds,
    string ThumbnailUrl,
    bool IsPlayable,
    int Position);