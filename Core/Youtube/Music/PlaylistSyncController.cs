using System.Buffers;
using System.Net.Http.Headers;
using System.Text.Json;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Youtube.Music;

internal sealed class PlaylistSyncController(HttpClient http)
{
    private const string MusicApiUrl = "https://music.youtube.com/youtubei/v1";
    private const string WebApiUrl = "https://www.youtube.com/youtubei/v1";

    private static readonly MediaTypeHeaderValue JsonContentType = new("application/json");

    #region Context Writers

    /// <summary>
    /// WEB_REMIX context for YouTube Music browse requests.
    /// </summary>
    private static void WriteWebRemixContext(Utf8JsonWriter writer)
    {
        writer.WritePropertyName(InnerTubeTokens.Context);
        writer.WriteStartObject();

        writer.WritePropertyName(InnerTubeTokens.Client);
        writer.WriteStartObject();
        writer.WriteString(InnerTubeTokens.ClientName, InnerTubeTokens.WebRemix);
        writer.WriteString(InnerTubeTokens.ClientVersion, YoutubeHttpHandler.MusicClientVersion);
        writer.WriteString(InnerTubeTokens.Hl, YoutubeHttpHandler.InvariantHl);
        writer.WriteString(InnerTubeTokens.Gl, YoutubeHttpHandler.InvariantGl);

        var visitorData = YoutubeClientUtils.VisitorData;
        if (!string.IsNullOrEmpty(visitorData))
            writer.WriteString(InnerTubeTokens.VisitorData, visitorData);
        else
            writer.WriteNull(InnerTubeTokens.VisitorData);

        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    /// <summary>
    /// WEB context for standard YouTube browse requests (exact views, release date, visibility, author).
    /// </summary>
    private static void WriteWebContext(Utf8JsonWriter writer)
    {
        writer.WritePropertyName(InnerTubeTokens.Context);
        writer.WriteStartObject();

        writer.WritePropertyName(InnerTubeTokens.Client);
        writer.WriteStartObject();
        writer.WriteString(InnerTubeTokens.ClientName, InnerTubeTokens.Web);
        writer.WriteString(InnerTubeTokens.ClientVersion, YoutubeHttpHandler.WebClientVersion);
        writer.WriteString(InnerTubeTokens.Hl, YoutubeHttpHandler.InvariantHl);
        writer.WriteString(InnerTubeTokens.Gl, YoutubeHttpHandler.InvariantGl);
        writer.WriteNumber(InnerTubeTokens.UtcOffsetMinutes, 0);

        var visitorData = YoutubeClientUtils.VisitorData;
        if (!string.IsNullOrEmpty(visitorData))
            writer.WriteString(InnerTubeTokens.VisitorData, visitorData);

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

        var content = new ReadOnlyMemoryContent(bufferWriter.WrittenMemory);
        content.Headers.ContentType = JsonContentType;
        return content;
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
        var browseId = YoutubeIdHelper.NormalizePlaylistBrowseId(playlistId);

        // Запускаем чтение шапки из стандартного YouTube WEB параллельно с основным потоком WEB_REMIX:
        // WEB отдаёт точные до единицы просмотры, точную дату обновления (stats[2]), приватность и канал автора.
        var webMetadataTask = TryFetchWebBrowseMetadataAsync(browseId, ct);

        FullPlaylistSyncData playlistData;
        string? continuationToken;
        string? freshVisitor;

        {
            var (response, stream) = await PostMusicStreamAsync("browse", writer =>
            {
                writer.WriteString(InnerTubeTokens.BrowseId, browseId);
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
                writer.WriteString(InnerTubeTokens.Continuation, continuationToken);
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

        var webMeta = await webMetadataTask.ConfigureAwait(false);
        if (webMeta is { IsAvailable: true })
        {
            if (string.IsNullOrEmpty(playlistData.Title) && !string.IsNullOrEmpty(webMeta.Title))
                playlistData.Title = webMeta.Title;

            if (string.IsNullOrEmpty(playlistData.Description) && !string.IsNullOrEmpty(webMeta.Description))
                playlistData.Description = webMeta.Description;

            if (webMeta.ViewCount.HasValue)
                playlistData.ViewCount = webMeta.ViewCount;

            playlistData.ReleaseDate ??= webMeta.ReleaseDate;

            if (!string.IsNullOrEmpty(webMeta.Author))
                playlistData.Author = webMeta.Author;

            if (!string.IsNullOrEmpty(webMeta.ChannelId))
                playlistData.OwnerChannelId = webMeta.ChannelId;

            if (webMeta.Visibility != PlaylistVisibility.Unknown)
                playlistData.Visibility = webMeta.Visibility;

            if (webMeta.IsEditable)
                playlistData.IsEditable = true;
        }

        Log.Info($"[PlaylistSync] Cloud snapshot for '{playlistId}': title='{playlistData.Title ?? ""}', " +
                 $"tracks={playlistData.Tracks.Count}, views={playlistData.ViewCount?.ToString() ?? "null"}, " +
                 $"date={playlistData.ReleaseDate?.ToString("yyyy-MM-dd") ?? "null"}, " +
                 $"visibility={playlistData.Visibility}, editable={playlistData.IsEditable}");
        Log.Debug($"[PlaylistSync] Header details for '{playlistId}': author='{playlistData.Author ?? "null"}', " +
                  $"channelId='{playlistData.OwnerChannelId ?? "null"}', descLen={playlistData.Description?.Length ?? 0}, " +
                  $"thumb='{playlistData.ThumbnailUrl ?? "null"}'");

        return playlistData;
    }

    private async Task<Bridge.PlaylistBrowseResponse?> TryFetchWebBrowseMetadataAsync(
        string browseId,
        CancellationToken ct)
    {
        if (string.Equals(browseId, "VLLM", StringComparison.Ordinal) ||
            string.Equals(browseId, "VLLL", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{WebApiUrl}/browse?prettyPrint=false");

            var visitorData = YoutubeClientUtils.VisitorData;
            if (!string.IsNullOrEmpty(visitorData))
                request.Options.Set(YoutubeHttpHandler.VisitorDataKey, visitorData);

            request.Content = CreateJsonContent(writer =>
            {
                writer.WriteString(InnerTubeTokens.BrowseId, browseId);
                WriteWebContext(writer);
                writer.WriteString(InnerTubeTokens.Params, InnerTubeConstants.Params.PlaylistVideoList);
            });

            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await Bridge.PlaylistBrowseResponse.ParseAsync(stream, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug($"[PlaylistSync] Non-fatal WEB browse metadata enrichment skipped for '{browseId}': {ex.Message}");
            return null;
        }
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