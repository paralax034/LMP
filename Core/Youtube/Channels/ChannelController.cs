using LMP.Core.Youtube.Bridge;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Utils;
using System.Text;

namespace LMP.Core.Youtube.Channels;

internal class ChannelController(HttpClient http)
{
    private async ValueTask<ChannelPage> GetChannelPageAsync(
        string channelRoute,
        CancellationToken cancellationToken = default
    )
    {
        return await ResilienceExecutor.ExecuteWithRetryAsync(async () =>
        {
            var rawHtml = await http.GetStringAsync(
                "https://www.youtube.com/" + channelRoute,
                cancellationToken
            ).ConfigureAwait(false);

            return ChannelPage.TryParse(rawHtml)
                ?? throw new YoutubeExplodeException("Channel page is broken. Please try again in a few minutes.");
        }, maxRetries: 5, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ChannelPage> GetChannelPageAsync(
        ChannelId channelId,
        CancellationToken cancellationToken = default
    ) => await GetChannelPageAsync("channel/" + channelId, cancellationToken);

    public async ValueTask<ChannelPage> GetChannelPageAsync(
        UserName userName,
        CancellationToken cancellationToken = default
    ) => await GetChannelPageAsync("user/" + userName, cancellationToken);

    public async ValueTask<ChannelPage> GetChannelPageAsync(
        ChannelSlug channelSlug,
        CancellationToken cancellationToken = default
    ) => await GetChannelPageAsync("c/" + channelSlug, cancellationToken);

    public async ValueTask<ChannelPage> GetChannelPageAsync(
        ChannelHandle channelHandle,
        CancellationToken cancellationToken = default
    ) => await GetChannelPageAsync("@" + channelHandle, cancellationToken);

    public async ValueTask<ChannelPlaylistsResponse> GetChannelPlaylistsPageAsync(
        ChannelId channelId,
        string? continuationToken,
        CancellationToken cancellationToken = default
    )
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://www.youtube.com/youtubei/v1/browse?prettyPrint=false"
        );

        string payload;

        var hl = YoutubeHttpHandler.GetHl();
        var gl = YoutubeHttpHandler.GetGl();

        if (continuationToken == null)
        {
            // Первый запрос: запрашиваем страницу канала с открытой вкладкой Плейлисты
            payload = $$"""
        {
            "browseId": "{{channelId.Value}}",
            "params": "EglwbGF5bGlzdHPyBgQKAkIA", 
            "context": {
                "client": {
                    "clientName": "WEB",
                    "clientVersion": {{YoutubeHttpHandler.WebClientVersion}},
                    "hl": {{Json.Serialize(hl)}},
                    "gl": {{Json.Serialize(gl)}}
                }
            }
        }
        """;
        }
        else
        {
            // Продолжение пагинации
            payload = $$"""
        {
            "continuation": {{Json.Serialize(continuationToken)}},
            "context": {
                "client": {
                    "clientName": "WEB",
                    "clientVersion": {{YoutubeHttpHandler.WebClientVersion}},
                    "hl": {{Json.Serialize(hl)}},
                    "gl": {{Json.Serialize(gl)}}
                }
            }
        }
        """;
        }

        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await ChannelPlaylistsResponse.ParseAsync(stream, cancellationToken).ConfigureAwait(false);
    }
}