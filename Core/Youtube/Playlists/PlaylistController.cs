using System.Buffers;
using System.Net.Http.Headers;
using System.Text.Json;
using LMP.Core.Youtube.Bridge;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Videos;

namespace LMP.Core.Youtube.Playlists;

internal class PlaylistController(HttpClient http)
{
    private static readonly MediaTypeHeaderValue JsonContentType = new("application/json");

    public async ValueTask<PlaylistBrowseResponse> GetPlaylistBrowseResponseAsync(
        PlaylistId playlistId,
        CancellationToken cancellationToken = default
    )
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://www.youtube.com/youtubei/v1/browse?prettyPrint=false"
        );

        string browseId = YoutubeIdHelper.NormalizePlaylistBrowseId(playlistId.Value);

        var bufferWriter = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(bufferWriter))
        {
            writer.WriteStartObject();
            writer.WriteString(InnerTubeTokens.BrowseId, browseId);

            writer.WritePropertyName(InnerTubeTokens.Context);
            writer.WriteStartObject();

            writer.WritePropertyName(InnerTubeTokens.Client);
            writer.WriteStartObject();
            writer.WriteString(InnerTubeTokens.ClientName, InnerTubeTokens.Web);
            writer.WriteString(InnerTubeTokens.ClientVersion, YoutubeHttpHandler.WebClientVersion);
            writer.WriteString(InnerTubeTokens.Hl, YoutubeHttpHandler.InvariantHl);
            writer.WriteString(InnerTubeTokens.Gl, YoutubeHttpHandler.InvariantGl);
            writer.WriteNumber(InnerTubeTokens.UtcOffsetMinutes, 0);
            writer.WriteEndObject();

            writer.WriteEndObject();

            writer.WriteString(InnerTubeTokens.Params, InnerTubeConstants.Params.PlaylistVideoList);
            writer.WriteEndObject();
        }

        var content = new ReadOnlyMemoryContent(bufferWriter.WrittenMemory);
        content.Headers.ContentType = JsonContentType;
        request.Content = content;

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var playlistResponse = await PlaylistBrowseResponse.ParseAsync(stream, cancellationToken).ConfigureAwait(false);

        if (!playlistResponse.IsAvailable && browseId != "VLLL")
            throw new PlaylistUnavailableException($"Playlist '{playlistId}' isnt avaliable.");

        return playlistResponse;
    }

    /// <summary>
    /// Получает следующую страницу видео плейлиста через continuation token
    /// </summary>
    public async ValueTask<PlaylistContinuationResponse> GetPlaylistContinuationAsync(
        string continuationToken,
        string? visitorData = null,
        CancellationToken cancellationToken = default
    )
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://www.youtube.com/youtubei/v1/browse?prettyPrint=false"
        );

        if (!string.IsNullOrEmpty(visitorData))
        {
            request.Options.Set(YoutubeHttpHandler.VisitorDataKey, visitorData);
        }

        var bufferWriter = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(bufferWriter))
        {
            writer.WriteStartObject();
            writer.WriteString(InnerTubeTokens.Continuation, continuationToken);

            writer.WritePropertyName(InnerTubeTokens.Context);
            writer.WriteStartObject();

            writer.WritePropertyName(InnerTubeTokens.Client);
            writer.WriteStartObject();
            writer.WriteString(InnerTubeTokens.ClientName, InnerTubeTokens.Web);
            writer.WriteString(InnerTubeTokens.ClientVersion, YoutubeHttpHandler.WebClientVersion);
            writer.WriteString(InnerTubeTokens.Hl, YoutubeHttpHandler.InvariantHl);
            writer.WriteString(InnerTubeTokens.Gl, YoutubeHttpHandler.InvariantGl);
            writer.WriteNumber(InnerTubeTokens.UtcOffsetMinutes, 0);

            if (!string.IsNullOrEmpty(visitorData))
                writer.WriteString(InnerTubeTokens.VisitorData, visitorData);

            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        var content = new ReadOnlyMemoryContent(bufferWriter.WrittenMemory);
        content.Headers.ContentType = JsonContentType;
        request.Content = content;

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await PlaylistContinuationResponse.ParseAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Получает ответ Next плейлиста.
    /// </summary>
    public async ValueTask<PlaylistNextResponse> GetPlaylistNextResponseAsync(
        PlaylistId playlistId,
        VideoId? videoId = null,
        int index = 0,
        string? visitorData = null,
        CancellationToken cancellationToken = default
    )
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://www.youtube.com/youtubei/v1/next?prettyPrint=false"
        );

        if (!string.IsNullOrEmpty(visitorData))
        {
            request.Options.Set(YoutubeHttpHandler.VisitorDataKey, visitorData);
        }

        var bufferWriter = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(bufferWriter))
        {
            writer.WriteStartObject();
            writer.WriteString(InnerTubeTokens.PlaylistId, playlistId.Value);

            if (videoId.HasValue)
                writer.WriteString(InnerTubeTokens.VideoId, videoId.Value.Value);

            writer.WriteNumber(InnerTubeTokens.PlaylistIndex, index);

            writer.WritePropertyName(InnerTubeTokens.Context);
            writer.WriteStartObject();

            writer.WritePropertyName(InnerTubeTokens.Client);
            writer.WriteStartObject();
            writer.WriteString(InnerTubeTokens.ClientName, InnerTubeTokens.Web);
            writer.WriteString(InnerTubeTokens.ClientVersion, YoutubeHttpHandler.WebClientVersion);
            writer.WriteString(InnerTubeTokens.Hl, YoutubeHttpHandler.InvariantHl);
            writer.WriteString(InnerTubeTokens.Gl, YoutubeHttpHandler.InvariantGl);
            writer.WriteNumber(InnerTubeTokens.UtcOffsetMinutes, 0);

            if (!string.IsNullOrEmpty(visitorData))
                writer.WriteString(InnerTubeTokens.VisitorData, visitorData);

            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        var content = new ReadOnlyMemoryContent(bufferWriter.WrittenMemory);
        content.Headers.ContentType = JsonContentType;
        request.Content = content;

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var playlistResponse = await PlaylistNextResponse.ParseAsync(stream, cancellationToken).ConfigureAwait(false);

        if (!playlistResponse.IsAvailable)
        {
            throw new PlaylistUnavailableException($"Playlist '{playlistId}' isnt avaliable.");
        }

        return playlistResponse;
    }

    public async ValueTask<IPlaylistData> GetPlaylistResponseAsync(
        PlaylistId playlistId,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            return await GetPlaylistBrowseResponseAsync(playlistId, cancellationToken);
        }
        catch (PlaylistUnavailableException)
        {
            return await GetPlaylistNextResponseAsync(playlistId, null, 0, null, cancellationToken);
        }
    }
}