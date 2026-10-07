using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LMP.Core.Models;

namespace LMP.Core.Services;

/// <summary>
/// Чистый клиент API Яндекс Музыки без привязки к плееру.
/// </summary>
public sealed class YandexMusicClient : IExternalMusicSource
{
    public string ProviderName => "Yandex Music";

    private long _bytesReceived;
    private long _bytesSent;

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(15)
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public (long BytesReceived, long BytesSent) GetTrafficUsage() => (_bytesReceived, _bytesSent);

    public async Task<ExternalUserProfile> GetProfileAsync(string token, CancellationToken ct = default)
    {
        using var req = CreateRequest("https://api.music.yandex.net/account/status", token, HttpMethod.Get);
        using var resp = await SendMeasuredAsync(req, ct).ConfigureAwait(false);

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException("Токен недействителен или срок его действия истёк.");
        }

        resp.EnsureSuccessStatusCode();

        var bytes = await ReadMeasuredBytesAsync(resp, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(bytes);

        var account = doc.RootElement.GetProperty("result").GetProperty("account");
        long uid = account.GetProperty("uid").GetInt64();
        string login = account.TryGetProperty("login", out var lp) ? lp.GetString() ?? "User" : "User";
        string fullName = account.TryGetProperty("fullName", out var fp) ? fp.GetString() ?? login : login;

        return new ExternalUserProfile(uid, login, fullName);
    }

    public async Task<IReadOnlyList<ExternalPlaylist>> GetPlaylistsAsync(string token, long uid, CancellationToken ct = default)
    {
        var result = new List<ExternalPlaylist>();

        // 1. «Мне нравится» (системный плейлист)
        try
        {
            using var likesReq = CreateRequest($"https://api.music.yandex.net/users/{uid}/likes/tracks", token, HttpMethod.Get);
            using var likesResp = await SendMeasuredAsync(likesReq, ct).ConfigureAwait(false);

            if (likesResp.IsSuccessStatusCode)
            {
                var bytes = await ReadMeasuredBytesAsync(likesResp, ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(bytes);

                if (doc.RootElement.TryGetProperty("result", out var res) &&
                    res.TryGetProperty("library", out var lib) &&
                    lib.TryGetProperty("tracks", out var arr))
                {
                    result.Add(new ExternalPlaylist(
                        Id: "likes",
                        Title: "Мне нравится",
                        Description: "Понравившиеся треки из Яндекс Музыки",
                        CoverUrl: null,
                        HexColor: "#E02E2E",
                        TrackCount: arr.GetArrayLength()));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[YandexClient] Не удалось прочитать лайки: {ex.Message}");
        }

        // 2. Пользовательские плейлисты
        try
        {
            using var req = CreateRequest($"https://api.music.yandex.net/users/{uid}/playlists/list", token, HttpMethod.Get);
            using var resp = await SendMeasuredAsync(req, ct).ConfigureAwait(false);

            if (resp.IsSuccessStatusCode)
            {
                var bytes = await ReadMeasuredBytesAsync(resp, ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(bytes);

                if (doc.RootElement.TryGetProperty("result", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in arr.EnumerateArray())
                    {
                        var kind = p.GetProperty("kind").GetInt64().ToString();
                        var title = p.TryGetProperty("title", out var tp) ? tp.GetString() ?? "Плейлист" : "Плейлист";
                        var count = p.TryGetProperty("trackCount", out var cp) ? cp.GetInt32() : 0;
                        var desc = p.TryGetProperty("description", out var dp) ? dp.GetString() : null;

                        string? cover = ExtractCoverUrl(p);
                        string? hexColor = ExtractDerivedColor(p);

                        result.Add(new ExternalPlaylist(kind, title, desc, cover, hexColor, count));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[YandexClient] Не удалось получить плейлисты: {ex.Message}");
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
            using var likesReq = CreateRequest($"https://api.music.yandex.net/users/{uid}/likes/tracks", token, HttpMethod.Get);
            using var likesResp = await SendMeasuredAsync(likesReq, ct).ConfigureAwait(false);
            likesResp.EnsureSuccessStatusCode();

            var bytes = await ReadMeasuredBytesAsync(likesResp, ct).ConfigureAwait(false);
            using var d = JsonDocument.Parse(bytes);

            var trackIds = new List<string>(2500);
            var tracksArr = d.RootElement.GetProperty("result").GetProperty("library").GetProperty("tracks");

            foreach (var t in tracksArr.EnumerateArray())
            {
                string? id = t.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                if (!string.IsNullOrEmpty(id))
                {
                    string? albumId = t.TryGetProperty("albumId", out var aProp) ? aProp.GetString() : null;
                    trackIds.Add(albumId != null ? $"{id}:{albumId}" : id);
                }
            }

            var tracks = await FetchTracksByIdsChunkedAsync(token, trackIds, ct).ConfigureAwait(false);
            return (targetPlaylist, tracks);
        }

        using var req = CreateRequest($"https://api.music.yandex.net/users/{uid}/playlists/{targetPlaylist.Id}", token, HttpMethod.Get);
        using var resp = await SendMeasuredAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var plBytes = await ReadMeasuredBytesAsync(resp, ct).ConfigureAwait(false);
        using var plDoc = JsonDocument.Parse(plBytes);

        var resultEl = plDoc.RootElement.GetProperty("result");

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
                    var id = idProp.GetString();
                    if (!string.IsNullOrEmpty(id)) unexpandedIds.Add(id);
                }
            }
        }

        if (unexpandedIds.Count > 0)
        {
            var extra = await FetchTracksByIdsChunkedAsync(token, unexpandedIds, ct).ConfigureAwait(false);
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

    private async Task<List<ExternalTrack>> FetchTracksByIdsChunkedAsync(
        string token,
        IEnumerable<string> trackIds,
        CancellationToken ct)
    {
        const int batchSize = 400;
        var result = new List<ExternalTrack>();

        // Нарезка через BCL .Chunk() за один проход без GC Pressure
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

            var bytes = await ReadMeasuredBytesAsync(resp, ct).ConfigureAwait(false);
            using var d = JsonDocument.Parse(bytes);

            if (d.RootElement.TryGetProperty("result", out var arr) && arr.ValueKind == JsonValueKind.Array)
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

        int durationMs = 0;
        if (el.TryGetProperty("durationMs", out var dp) && dp.TryGetInt32(out var ms))
        {
            durationMs = ms;
        }

        string? cover = ExtractCoverUrl(el);
        string? id = el.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

        return new ExternalTrack(title, artist, TimeSpan.FromMilliseconds(durationMs), id, null, cover);
    }

    private static string? ExtractCoverUrl(JsonElement el)
    {
        string? uri = null;
        if (el.TryGetProperty("ogImage", out var og)) uri = og.GetString();
        else if (el.TryGetProperty("coverUri", out var cu)) uri = cu.GetString();

        if (string.IsNullOrEmpty(uri)) return null;

        uri = uri.Replace("%%", "m1000x1000");
        return uri.StartsWith("http") ? uri : $"https://{uri}";
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
        var req = new HttpRequestMessage(method, url);
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
        return await Http.SendAsync(req, ct).ConfigureAwait(false);
    }

    private async Task<byte[]> ReadMeasuredBytesAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        Interlocked.Add(ref _bytesReceived, bytes.Length);
        return bytes;
    }
}
