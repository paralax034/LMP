using System.Buffers;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Youtube.Music;

internal sealed class PlaylistSyncController(HttpClient http)
{
    private const string MusicApiUrl = "https://music.youtube.com/youtubei/v1";

    private static readonly MediaTypeHeaderValue JsonContentType = new("application/json");

    private static readonly byte[] Utf8Context = "context"u8.ToArray();
    private static readonly byte[] Utf8Client = "client"u8.ToArray();
    private static readonly byte[] Utf8ClientName = "clientName"u8.ToArray();
    private static readonly byte[] Utf8ClientVersion = "clientVersion"u8.ToArray();
    private static readonly byte[] Utf8WebRemix = "WEB_REMIX"u8.ToArray();
    private static readonly byte[] Utf8Hl = "hl"u8.ToArray();
    private static readonly byte[] Utf8Gl = "gl"u8.ToArray();
    private static readonly byte[] Utf8VisitorData = "visitorData"u8.ToArray();

    #region Context Writers

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