using System.Buffers;
using System.Collections.Frozen;
using System.Net.Http.Headers;
using System.Text.Json;
using LMP.Core.Youtube.Bridge;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Youtube.Search;

internal class SearchController(HttpClient http)
{
    private static readonly FrozenDictionary<SearchFilter, string> MusicFilterParams = new Dictionary<SearchFilter, string>
    {
        [SearchFilter.Music] = InnerTubeConstants.Params.MusicFilterGeneral,
        [SearchFilter.MusicSong] = InnerTubeConstants.Params.MusicFilterSong,
        [SearchFilter.MusicVideo] = InnerTubeConstants.Params.MusicFilterVideo,
        [SearchFilter.MusicAlbum] = InnerTubeConstants.Params.MusicFilterAlbum,
        [SearchFilter.MusicArtist] = InnerTubeConstants.Params.MusicFilterArtist,
        [SearchFilter.MusicPlaylist] = InnerTubeConstants.Params.MusicFilterPlaylist,
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<SearchFilter, string> WebFilterParams = new Dictionary<SearchFilter, string>
    {
        [SearchFilter.Video] = InnerTubeConstants.Params.WebFilterVideo,
        [SearchFilter.Playlist] = InnerTubeConstants.Params.WebFilterPlaylist,
        [SearchFilter.Channel] = InnerTubeConstants.Params.WebFilterChannel,
    }.ToFrozenDictionary();

    private static readonly FrozenSet<SearchFilter> MusicFilters = new[]
    {
        SearchFilter.Music, SearchFilter.MusicSong, SearchFilter.MusicVideo,
        SearchFilter.MusicAlbum, SearchFilter.MusicArtist, SearchFilter.MusicPlaylist
    }.ToFrozenSet();

    private static readonly MediaTypeHeaderValue JsonContentType = new("application/json");

    public async ValueTask<SearchResponse> GetSearchResponseAsync(
        string searchQuery,
        SearchFilter searchFilter,
        string? continuationToken,
        CancellationToken cancellationToken = default)
    {
        bool isMusicContext = MusicFilters.Contains(searchFilter);

        string? searchParams = continuationToken == null
            ? GetSearchParams(searchFilter, isMusicContext)
            : null;

        var url = isMusicContext
                    ? "https://music.youtube.com/youtubei/v1/search?prettyPrint=false"
                    : "https://www.youtube.com/youtubei/v1/search?prettyPrint=false";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);

        // Поисковый запрос явно объявляет системный язык пользователя в заголовках,
        // предотвращая навязывание словарей опечаток сторонних регионов.
        var userHl = YoutubeHttpHandler.GetHl();
        var userGl = YoutubeHttpHandler.GetGl();
        request.Headers.AcceptLanguage.Clear();
        request.Headers.AcceptLanguage.ParseAdd($"{userHl}-{userGl},{userHl};q=0.9,en;q=0.8");

        var bufferWriter = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(bufferWriter))
        {
            writer.WriteStartObject();

            if (continuationToken != null)
            {
                writer.WriteString(InnerTubeTokens.Continuation, continuationToken);
            }
            else
            {
                writer.WriteString(InnerTubeTokens.Query, searchQuery);
                if (searchParams != null)
                    writer.WriteString(InnerTubeTokens.Params, searchParams);
            }

            writer.WritePropertyName(InnerTubeTokens.Context);
            writer.WriteStartObject();

            writer.WritePropertyName(InnerTubeTokens.Client);
            writer.WriteStartObject();

            if (isMusicContext)
            {
                writer.WriteString(InnerTubeTokens.ClientName, InnerTubeTokens.WebRemix);
                writer.WriteString(InnerTubeTokens.ClientVersion, YoutubeHttpHandler.MusicClientVersion);
            }
            else
            {
                writer.WriteString(InnerTubeTokens.ClientName, InnerTubeTokens.Web);
                writer.WriteString(InnerTubeTokens.ClientVersion, YoutubeHttpHandler.WebClientVersion);
            }

            // Поисковый запрос должен учитывать локаль и регион пользователя системы,
            // иначе англоязычный спеллчекер США искажает специфичные имена и никнеймы артистов.
            writer.WriteString(InnerTubeTokens.Hl, YoutubeHttpHandler.GetHl());
            writer.WriteString(InnerTubeTokens.Gl, YoutubeHttpHandler.GetGl());

            writer.WriteEndObject(); // client

            if (isMusicContext)
            {
                writer.WritePropertyName(InnerTubeTokens.User);
                writer.WriteStartObject();
                writer.WriteEndObject(); // user
            }

            writer.WriteEndObject(); // context
            writer.WriteEndObject(); // root
        }

        var content = new ReadOnlyMemoryContent(bufferWriter.WrittenMemory);
        content.Headers.ContentType = JsonContentType;
        request.Content = content;

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await SearchResponse.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }

    private static string? GetSearchParams(SearchFilter filter, bool isMusicContext)
    {
        if (isMusicContext)
            return MusicFilterParams.GetValueOrDefault(filter);

        return WebFilterParams.GetValueOrDefault(filter);
    }
}