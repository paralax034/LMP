using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LMP.Core.Exceptions;
using LMP.Core.Models;

namespace LMP.Core.Services;

/// <summary>
/// Клиент API Яндекс Музыки. Потоковое чтение, поддержка HTTP/2 и строгая декомпозиция.
/// </summary>
public sealed class YandexMusicClient : IExternalMusicSource
{
    public string ProviderName => "Yandex Music";

    private readonly HttpClient _http;
    private long _bytesReceived;
    private long _bytesSent;

    public YandexMusicClient(NetworkManager networkManager)
    {
        ArgumentNullException.ThrowIfNull(networkManager);
        _http = networkManager.ApiClient;
    }

    public (long BytesReceived, long BytesSent) GetTrafficUsage() => (_bytesReceived, _bytesSent);

    public async Task<ExternalUserProfile> GetProfileAsync(string token, CancellationToken ct = default)
    {
        using var req = CreateRequest("https://api.music.yandex.net/account/status", token, HttpMethod.Get);
        using var resp = await SendMeasuredAsync(req, ct).ConfigureAwait(false);

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new YandexAuthException();
        }

        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("result", out var res) ||
            !res.TryGetProperty("account", out var account))
        {
            throw new YandexMusicException("Invalid account status response format.");
        }

        long uid = account.TryGetProperty("uid", out var uidProp) ? uidProp.GetInt64() : 0;
        string login = account.TryGetProperty("login", out var lp) ? lp.GetString() ?? "User" : "User";
        string fullName = account.TryGetProperty("fullName", out var fp) ? fp.GetString() ?? login : login;

        return new ExternalUserProfile(uid, login, fullName);
    }

    public async Task<IReadOnlyList<ExternalPlaylist>> GetPlaylistsAsync(string token, long uid, CancellationToken ct = default)
    {
        var result = new List<ExternalPlaylist>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        // 1. Системный плейлист «Мне нравится»
        var likedPlaylist = await FetchLikedPlaylistHeaderAsync(token, uid, ct).ConfigureAwait(false);
        if (likedPlaylist != null)
        {
            result.Add(likedPlaylist);
            seenKeys.Add("likes");
        }

        // 2. Созданные пользователем плейлисты
        var userPlaylists = await FetchUserPlaylistsHeadersAsync(token, uid, ct).ConfigureAwait(false);
        foreach (var p in userPlaylists)
        {
            if (seenKeys.Add($"{p.OwnerUid ?? uid}:{p.Id}"))
            {
                result.Add(p);
            }
        }

        // 3. Любимые (сохранённые) плейлисты других авторов и кураторов
        var favoritePlaylists = await FetchFavoritePlaylistsHeadersAsync(token, uid, ct).ConfigureAwait(false);
        foreach (var p in favoritePlaylists)
        {
            if (seenKeys.Add($"{p.OwnerUid ?? uid}:{p.Id}"))
            {
                result.Add(p);
            }
        }

        return result;
    }

    public async Task<(ExternalPlaylist Metadata, IReadOnlyList<ExternalTrack> Tracks)> FetchPlaylistTracksAsync(
        string token,
        long uid,
        ExternalPlaylist targetPlaylist,
        CancellationToken ct = default)
    {
        if (targetPlaylist.Id == "likes")
        {
            var likedIds = await FetchLikedTrackIdsAsync(token, uid, ct).ConfigureAwait(false);
            var tracks = await FetchTracksChunkedAsync(token, likedIds, ct).ConfigureAwait(false);
            return (targetPlaylist, tracks);
        }

        return await FetchCustomPlaylistWithTracksAsync(token, uid, targetPlaylist, ct).ConfigureAwait(false);
    }

    #region Decomposition Helpers

    private async Task<ExternalPlaylist?> FetchLikedPlaylistHeaderAsync(string token, long uid, CancellationToken ct)
    {
        try
        {
            using var req = CreateRequest($"https://api.music.yandex.net/users/{uid}/likes/tracks", token, HttpMethod.Get);
            using var resp = await SendMeasuredAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("result", out var res) &&
                res.TryGetProperty("library", out var lib) &&
                lib.TryGetProperty("tracks", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                return new ExternalPlaylist(
                    Id: "likes",
                    Title: LocalizationService.Instance["Import_Yandex_LikedTitle"],
                    Description: LocalizationService.Instance["Import_Yandex_LikedDesc"],
                    CoverUrl: null,
                    HexColor: "#E02E2E",
                    TrackCount: arr.GetArrayLength(),
                    OwnerUid: uid);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[YandexClient] Failed to fetch liked tracks header: {ex.Message}");
        }

        return null;
    }

    private async Task<List<ExternalPlaylist>> FetchUserPlaylistsHeadersAsync(string token, long uid, CancellationToken ct)
    {
        var list = new List<ExternalPlaylist>();
        try
        {
            using var req = CreateRequest($"https://api.music.yandex.net/users/{uid}/playlists/list", token, HttpMethod.Get);
            using var resp = await SendMeasuredAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return list;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("result", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                string defaultTitle = LocalizationService.Instance["Import_Yandex_DefaultPlaylistTitle"];

                foreach (var p in arr.EnumerateArray())
                {
                    if (!p.TryGetProperty("kind", out var kindProp)) continue;
                    var kind = kindProp.GetInt64().ToString();
                    var title = p.TryGetProperty("title", out var tp) ? tp.GetString() ?? defaultTitle : defaultTitle;
                    var count = p.TryGetProperty("trackCount", out var cp) ? cp.GetInt32() : 0;
                    var desc = p.TryGetProperty("description", out var dp) ? dp.GetString() : null;

                    string? cover = ExtractCoverUrl(p);
                    string? hexColor = ExtractDerivedColor(p);

                    list.Add(new ExternalPlaylist(kind, title, desc, cover, hexColor, count, OwnerUid: uid));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[YandexClient] Failed to fetch user playlists: {ex.Message}");
        }

        return list;
    }

    /// <summary>
    /// Выгружает заголовки любимых (сохранённых) плейлистов из /users/{uid}/likes/playlists.
    /// </summary>
    private async Task<List<ExternalPlaylist>> FetchFavoritePlaylistsHeadersAsync(string token, long uid, CancellationToken ct)
    {
        var list = new List<ExternalPlaylist>();
        try
        {
            using var req = CreateRequest($"https://api.music.yandex.net/users/{uid}/likes/playlists", token, HttpMethod.Get);
            using var resp = await SendMeasuredAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return list;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("result", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                string defaultTitle = LocalizationService.Instance["Import_Yandex_DefaultPlaylistTitle"];

                foreach (var item in arr.EnumerateArray())
                {
                    // В likes/playlists объект плейлиста вложен в свойство "playlist"
                    var p = item.TryGetProperty("playlist", out var plObj) && plObj.ValueKind == JsonValueKind.Object
                        ? plObj
                        : item;

                    if (!p.TryGetProperty("kind", out var kindProp)) continue;
                    var kind = kindProp.GetInt64().ToString();
                    var title = p.TryGetProperty("title", out var tp) ? tp.GetString() ?? defaultTitle : defaultTitle;
                    var count = p.TryGetProperty("trackCount", out var cp) ? cp.GetInt32() : 0;
                    var desc = p.TryGetProperty("description", out var dp) ? dp.GetString() : null;

                    long? playlistOwnerUid = null;
                    string? ownerName = null;

                    if (p.TryGetProperty("owner", out var ownerEl) && ownerEl.ValueKind == JsonValueKind.Object)
                    {
                        if (ownerEl.TryGetProperty("uid", out var ownerUidProp))
                            playlistOwnerUid = ownerUidProp.GetInt64();

                        if (ownerEl.TryGetProperty("name", out var nameProp))
                            ownerName = nameProp.GetString();
                        else if (ownerEl.TryGetProperty("login", out var loginProp))
                            ownerName = loginProp.GetString();
                    }

                    // Если у плейлиста нет своего описания, но есть автор — формируем «от ИмяАвтора»
                    if (string.IsNullOrWhiteSpace(desc) && !string.IsNullOrWhiteSpace(ownerName))
                    {
                        desc = string.Format(LocalizationService.Instance["Playlist_ByAuthor"], ownerName);
                    }

                    string? cover = ExtractCoverUrl(p);
                    string? hexColor = ExtractDerivedColor(p);

                    list.Add(new ExternalPlaylist(
                        Id: kind,
                        Title: title,
                        Description: desc,
                        CoverUrl: cover,
                        HexColor: hexColor,
                        TrackCount: count,
                        OwnerUid: playlistOwnerUid ?? uid,
                        AuthorName: ownerName));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[YandexClient] Failed to fetch favorite/liked playlists: {ex.Message}");
        }

        return list;
    }

    private async Task<List<string>> FetchLikedTrackIdsAsync(string token, long uid, CancellationToken ct)
    {
        using var req = CreateRequest($"https://api.music.yandex.net/users/{uid}/likes/tracks", token, HttpMethod.Get);
        using var resp = await SendMeasuredAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);

        var trackIds = new List<string>(2500);
        if (doc.RootElement.TryGetProperty("result", out var res) &&
            res.TryGetProperty("library", out var lib) &&
            lib.TryGetProperty("tracks", out var arr) &&
            arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in arr.EnumerateArray())
            {
                if (t.TryGetProperty("id", out var idProp))
                {
                    string id = idProp.GetString() ?? idProp.GetInt64().ToString();
                    string? albumId = t.TryGetProperty("albumId", out var aProp) ? aProp.GetString() ?? aProp.GetInt64().ToString() : null;
                    trackIds.Add(albumId != null ? $"{id}:{albumId}" : id);
                }
            }
        }

        return trackIds;
    }

    private async Task<(ExternalPlaylist Metadata, IReadOnlyList<ExternalTrack> Tracks)> FetchCustomPlaylistWithTracksAsync(
        string token, long uid, ExternalPlaylist targetPlaylist, CancellationToken ct)
    {
        // Подставляем реальный ownerUid плейлиста (критично для любимых плейлистов других авторов)
        long effectiveOwnerUid = targetPlaylist.OwnerUid ?? uid;

        using var req = CreateRequest($"https://api.music.yandex.net/users/{effectiveOwnerUid}/playlists/{targetPlaylist.Id}", token, HttpMethod.Get);
        using var resp = await SendMeasuredAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("result", out var resultEl))
        {
            throw new YandexMusicException("Invalid playlist response payload.");
        }

        string title = resultEl.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? targetPlaylist.Title : targetPlaylist.Title;
        string? desc = resultEl.TryGetProperty("description", out var descProp) ? descProp.GetString() : targetPlaylist.Description;
        string? cover = ExtractCoverUrl(resultEl) ?? targetPlaylist.CoverUrl;
        string? color = ExtractDerivedColor(resultEl) ?? targetPlaylist.HexColor;

        var tracksList = new List<ExternalTrack>(targetPlaylist.TrackCount + 10);
        var unexpandedIds = new List<string>();

        if (resultEl.TryGetProperty("tracks", out var plTracksArr) && plTracksArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in plTracksArr.EnumerateArray())
            {
                if (item.TryGetProperty("track", out var trk) && trk.ValueKind == JsonValueKind.Object)
                {
                    var parsed = ParseTrack(trk);
                    if (parsed != null) tracksList.Add(parsed);
                }
                else if (item.TryGetProperty("id", out var idProp))
                {
                    var id = idProp.GetString() ?? idProp.GetInt64().ToString();
                    if (!string.IsNullOrEmpty(id)) unexpandedIds.Add(id);
                }
            }
        }

        if (unexpandedIds.Count > 0)
        {
            var extra = await FetchTracksChunkedAsync(token, unexpandedIds, ct).ConfigureAwait(false);
            tracksList.AddRange(extra);
        }

        var fullMetadata = targetPlaylist with
        {
            Title = title,
            Description = desc,
            CoverUrl = cover,
            HexColor = color,
            TrackCount = tracksList.Count
        };

        return (fullMetadata, tracksList);
    }

    private async Task<List<ExternalTrack>> FetchTracksChunkedAsync(
        string token, IEnumerable<string> trackIds, CancellationToken ct)
    {
        const int batchSize = 400;
        var result = new List<ExternalTrack>();

        foreach (var chunk in trackIds.Chunk(batchSize))
        {
            ct.ThrowIfCancellationRequested();

            var postData = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("track-ids", string.Join(",", chunk))
            ]);

            using var req = CreateRequest("https://api.music.yandex.net/tracks", token, HttpMethod.Post);
            req.Content = postData;

            using var resp = await SendMeasuredAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) continue;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("result", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in arr.EnumerateArray())
                {
                    var parsed = ParseTrack(t);
                    if (parsed != null) result.Add(parsed);
                }
            }
        }

        return result;
    }

    private static ExternalTrack? ParseTrack(JsonElement el)
    {
        var title = el.TryGetProperty("title", out var tp) ? tp.GetString() : null;
        if (string.IsNullOrWhiteSpace(title)) return null;

        if (el.TryGetProperty("version", out var vp) && !string.IsNullOrWhiteSpace(vp.GetString()))
        {
            title = $"{title} ({vp.GetString()})";
        }

        var artistsList = new List<string>();
        if (el.TryGetProperty("artists", out var artArr) && artArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in artArr.EnumerateArray())
            {
                if (a.TryGetProperty("name", out var np) && !string.IsNullOrWhiteSpace(np.GetString()))
                {
                    artistsList.Add(np.GetString()!);
                }
            }
        }

        string artist = artistsList.Count > 0 ? string.Join(", ", artistsList) : "Unknown Artist";
        int durationMs = el.TryGetProperty("durationMs", out var dp) ? dp.GetInt32() : 0;
        string? cover = ExtractCoverUrl(el);
        string? id = el.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? idProp.GetInt64().ToString() : null;

        return new ExternalTrack(title, artist, TimeSpan.FromMilliseconds(durationMs), id, null, cover);
    }

    private static string? ExtractCoverUrl(JsonElement el)
    {
        string? uri = null;
        if (el.TryGetProperty("ogImage", out var og)) uri = og.GetString();
        else if (el.TryGetProperty("coverUri", out var cu)) uri = cu.GetString();

        if (string.IsNullOrEmpty(uri)) return null;

        uri = uri.Replace("%%", "m1000x1000");
        return uri.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? uri : $"https://{uri}";
    }

    private static string? ExtractDerivedColor(JsonElement el)
    {
        if (el.TryGetProperty("derivedColors", out var dc) &&
            dc.TryGetProperty("average", out var avg))
        {
            var hex = avg.GetString();
            if (!string.IsNullOrWhiteSpace(hex) && hex.StartsWith('#'))
            {
                return hex;
            }
        }

        return null;
    }

    private static HttpRequestMessage CreateRequest(string url, string token, HttpMethod method)
    {
        var req = new HttpRequestMessage(method, url)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        req.Headers.Authorization = new AuthenticationHeaderValue("OAuth", token);
        req.Headers.UserAgent.ParseAdd("YandexMusic/2024.12.1 (Android; 14)");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return req;
    }

    private async Task<HttpResponseMessage> SendMeasuredAsync(HttpRequestMessage req, CancellationToken ct)
    {
        long sentBytes = 350;
        if (req.Content != null)
        {
            var contentBytes = await req.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            sentBytes += contentBytes.Length;
        }

        Interlocked.Add(ref _bytesSent, sentBytes);
        var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

        if (resp.Content.Headers.ContentLength.HasValue)
        {
            Interlocked.Add(ref _bytesReceived, resp.Content.Headers.ContentLength.Value);
        }

        return resp;
    }

    #endregion
}
