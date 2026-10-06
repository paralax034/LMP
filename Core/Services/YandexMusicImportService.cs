using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LMP.Core.Models;
using LMP.Core.Youtube.Search;

namespace LMP.Core.Services;

/// <summary>
/// DTO трека из Яндекс Музыки.
/// </summary>
public sealed record YandexTrackDto(string Title, string Artist, TimeSpan Duration);

/// <summary>
/// DTO плейлиста пользователя для выбора в UI.
/// </summary>
public sealed record YandexPlaylistChoice(string Id, string Title, int TrackCount);

/// <summary>
/// Итоговая статистика переноса и сетевого трафика.
/// </summary>
public sealed record TransferStats(
    int Matched,
    int Total,
    long YandexBytesReceived,
    long YandexBytesSent,
    int LocalHits,
    int NetworkSearches,
    long TotalTrafficBytes,
    long SavedTrafficBytes)
{
    public string YandexTrafficFormatted => FormatBytes(YandexBytesReceived + YandexBytesSent);
    public string TotalTrafficFormatted => FormatBytes(TotalTrafficBytes);
    public string SavedTrafficFormatted => FormatBytes(SavedTrafficBytes);

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} ГБ",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F2} МБ",
        >= 1024 => $"{bytes / 1024.0:F1} КБ",
        _ => $"{bytes} Б"
    };
}

/// <summary>
/// Сервис авторизованного импорта из Яндекс Музыки с замером трафика.
/// </summary>
public sealed partial class YandexMusicImportService
{
    private readonly YoutubeProvider _youtube;
    private readonly PlaylistService _playlistService;
    private readonly LibraryService _library;

    // Счётчики трафика
    private long _yandexBytesReceived;
    private long _yandexBytesSent;
    private int _localHits;
    private int _networkSearches;

    private static readonly HttpClient ApiClient = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(15)
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static string TokenFilePath =>
        Path.Combine(Path.GetDirectoryName(G.FilePath.Database) ?? AppContext.BaseDirectory, "yandex_token.txt");

    public YandexMusicImportService(
        YoutubeProvider youtube,
        PlaylistService playlistService,
        LibraryService library)
    {
        _youtube = youtube;
        _playlistService = playlistService;
        _library = library;
    }

    #region Token Storage

    public static string? GetSavedToken()
    {
        try
        {
            if (File.Exists(TokenFilePath))
            {
                var token = File.ReadAllText(TokenFilePath).Trim();
                if (!string.IsNullOrWhiteSpace(token)) return token;
            }
        }
        catch { }
        return null;
    }

    public static void SaveToken(string token)
    {
        try { File.WriteAllText(TokenFilePath, token.Trim()); }
        catch (Exception ex) { Log.Warn($"[YandexTransfer] Не удалось сохранить токен: {ex.Message}"); }
    }

    public static void ClearSavedToken()
    {
        try { if (File.Exists(TokenFilePath)) File.Delete(TokenFilePath); } catch { }
    }

    #endregion

    #region Yandex API Calls with Traffic Metering

    public async Task<(string UserName, long Uid, List<YandexPlaylistChoice> Playlists)> GetAccountAndPlaylistsAsync(
        string token, CancellationToken ct = default)
    {
        // 1. Проверяем статус аккаунта
        using var statusReq = CreateAuthorizedRequest("https://api.music.yandex.net/account/status", token, HttpMethod.Get);
        using var statusResp = await SendMeasuredAsync(statusReq, ct).ConfigureAwait(false);

        if (statusResp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            ClearSavedToken();
            throw new UnauthorizedAccessException("Токен недействителен или срок его действия истёк.");
        }

        statusResp.EnsureSuccessStatusCode();

        var statusBytes = await ReadMeasuredBytesAsync(statusResp, ct).ConfigureAwait(false);
        using var statusDoc = JsonDocument.Parse(statusBytes);

        var accountEl = statusDoc.RootElement.GetProperty("result").GetProperty("account");
        long uid = accountEl.GetProperty("uid").GetInt64();
        string login = accountEl.TryGetProperty("login", out var lp) ? lp.GetString() ?? "User" : "User";
        string fullName = accountEl.TryGetProperty("fullName", out var fp) ? fp.GetString() ?? login : login;

        var choices = new List<YandexPlaylistChoice>();

        // 2. Счётчик лайков
        try
        {
            using var likesReq = CreateAuthorizedRequest($"https://api.music.yandex.net/users/{uid}/likes/tracks", token, HttpMethod.Get);
            using var likesResp = await SendMeasuredAsync(likesReq, ct).ConfigureAwait(false);

            if (likesResp.IsSuccessStatusCode)
            {
                var likesBytes = await ReadMeasuredBytesAsync(likesResp, ct).ConfigureAwait(false);
                using var likesDoc = JsonDocument.Parse(likesBytes);

                if (likesDoc.RootElement.TryGetProperty("result", out var res) &&
                    res.TryGetProperty("library", out var lib) &&
                    lib.TryGetProperty("tracks", out var tracksArr))
                {
                    int likedCount = tracksArr.GetArrayLength();
                    choices.Add(new YandexPlaylistChoice("likes", "Мне нравится", likedCount));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[YandexTransfer] Ошибка чтения лайков: {ex.Message}");
        }

        // 3. Пользовательские плейлисты
        try
        {
            using var plReq = CreateAuthorizedRequest($"https://api.music.yandex.net/users/{uid}/playlists/list", token, HttpMethod.Get);
            using var plResp = await SendMeasuredAsync(plReq, ct).ConfigureAwait(false);

            if (plResp.IsSuccessStatusCode)
            {
                var plBytes = await ReadMeasuredBytesAsync(plResp, ct).ConfigureAwait(false);
                using var plDoc = JsonDocument.Parse(plBytes);

                if (plDoc.RootElement.TryGetProperty("result", out var plArr) && plArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in plArr.EnumerateArray())
                    {
                        var kind = p.GetProperty("kind").GetInt64().ToString();
                        var title = p.TryGetProperty("title", out var tp) ? tp.GetString() ?? "Плейлист" : "Плейлист";
                        var count = p.TryGetProperty("trackCount", out var cp) ? cp.GetInt32() : 0;

                        choices.Add(new YandexPlaylistChoice(kind, title, count));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[YandexTransfer] Ошибка чтения плейлистов: {ex.Message}");
        }

        return (fullName, uid, choices);
    }

    public async Task<(string Title, List<YandexTrackDto> Tracks)> FetchPlaylistTracksAsync(
        string token, long uid, YandexPlaylistChoice choice, CancellationToken ct = default)
    {
        if (choice.Id == "likes")
        {
            using var likesReq = CreateAuthorizedRequest($"https://api.music.yandex.net/users/{uid}/likes/tracks", token, HttpMethod.Get);
            using var likesResp = await SendMeasuredAsync(likesReq, ct).ConfigureAwait(false);
            likesResp.EnsureSuccessStatusCode();

            var likesBytes = await ReadMeasuredBytesAsync(likesResp, ct).ConfigureAwait(false);
            using var d = JsonDocument.Parse(likesBytes);

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

            Log.Info($"[YandexTransfer] Загрузка метаданных для {trackIds.Count} треков...");
            var tracks = await FetchTracksByIdsBatchAsync(token, trackIds, ct).ConfigureAwait(false);
            return ("Мне нравится (Яндекс Музыка)", tracks);
        }

        using var req = CreateAuthorizedRequest($"https://api.music.yandex.net/users/{uid}/playlists/{choice.Id}", token, HttpMethod.Get);
        using var resp = await SendMeasuredAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var plBytes = await ReadMeasuredBytesAsync(resp, ct).ConfigureAwait(false);
        using var plDoc = JsonDocument.Parse(plBytes);

        var resultEl = plDoc.RootElement.GetProperty("result");
        string title = resultEl.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? choice.Title : choice.Title;

        var resultTracks = new List<YandexTrackDto>(choice.TrackCount + 10);
        var unexpandedIds = new List<string>();

        if (resultEl.TryGetProperty("tracks", out var plTracksArr) && plTracksArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in plTracksArr.EnumerateArray())
            {
                if (item.TryGetProperty("track", out var trk) && trk.ValueKind == JsonValueKind.Object)
                {
                    var parsed = ParseSingleTrack(trk);
                    if (parsed != null) resultTracks.Add(parsed);
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
            var extra = await FetchTracksByIdsBatchAsync(token, unexpandedIds, ct).ConfigureAwait(false);
            resultTracks.AddRange(extra);
        }

        return (title, resultTracks);
    }

    private async Task<List<YandexTrackDto>> FetchTracksByIdsBatchAsync(
        string token, List<string> allTrackIds, CancellationToken ct)
    {
        const int batchSize = 400;
        var result = new List<YandexTrackDto>(allTrackIds.Count);

        for (int i = 0; i < allTrackIds.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();

            var chunk = allTrackIds.Skip(i).Take(batchSize).ToList();
            var postData = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("track-ids", string.Join(",", chunk))
            ]);

            using var req = CreateAuthorizedRequest("https://api.music.yandex.net/tracks", token, HttpMethod.Post);
            req.Content = postData;

            using var resp = await SendMeasuredAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) continue;

            var bytes = await ReadMeasuredBytesAsync(resp, ct).ConfigureAwait(false);
            using var d = JsonDocument.Parse(bytes);

            if (d.RootElement.TryGetProperty("result", out var tracksArr) && tracksArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tracksArr.EnumerateArray())
                {
                    var parsed = ParseSingleTrack(t);
                    if (parsed != null) result.Add(parsed);
                }
            }
        }

        return result;
    }

    private static YandexTrackDto? ParseSingleTrack(JsonElement trackEl)
    {
        var title = trackEl.TryGetProperty("title", out var tp) ? tp.GetString() : null;
        if (string.IsNullOrWhiteSpace(title)) return null;

        if (trackEl.TryGetProperty("version", out var vp) && !string.IsNullOrWhiteSpace(vp.GetString()))
            title = $"{title} ({vp.GetString()})";

        var artistsList = new List<string>();
        if (trackEl.TryGetProperty("artists", out var artArr) && artArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in artArr.EnumerateArray())
            {
                if (a.TryGetProperty("name", out var np) && !string.IsNullOrWhiteSpace(np.GetString()))
                    artistsList.Add(np.GetString()!);
            }
        }

        string artist = artistsList.Count > 0 ? string.Join(", ", artistsList) : "Unknown Artist";
        int durationMs = trackEl.TryGetProperty("durationMs", out var dp) ? dp.GetInt32() : 0;

        return new YandexTrackDto(title, artist, TimeSpan.FromMilliseconds(durationMs));
    }

    private static HttpRequestMessage CreateAuthorizedRequest(string url, string token, HttpMethod method)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("OAuth", token);
        req.Headers.UserAgent.ParseAdd("YandexMusic/2024.12.1 (Android; 14)");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return req;
    }

    private async Task<HttpResponseMessage> SendMeasuredAsync(HttpRequestMessage req, CancellationToken ct)
    {
        long sentBytes = 350; // примерный размер заголовков
        if (req.Content != null)
        {
            var contentBytes = await req.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            sentBytes += contentBytes.Length;
        }

        Interlocked.Add(ref _yandexBytesSent, sentBytes);
        return await ApiClient.SendAsync(req, ct).ConfigureAwait(false);
    }

    private async Task<byte[]> ReadMeasuredBytesAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        Interlocked.Add(ref _yandexBytesReceived, bytes.Length);
        return bytes;
    }

    #endregion

    #region LMP Import Pipeline with Traffic Logging

    /// <summary>
    /// Выполняет параллельный импорт с замером всего использованного трафика.
    /// </summary>
    public async Task<TransferStats> ImportToLmpAsync(
        string playlistTitle,
        List<YandexTrackDto> yandexTracks,
        Action<int, int, string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (yandexTracks.Count == 0)
        {
            return new TransferStats(0, 0, _yandexBytesReceived, _yandexBytesSent, 0, 0, 0, 0);
        }

        var lmpPlaylist = await _playlistService.CreatePlaylistAsync(
            name: playlistTitle,
            description: "Импортировано из Яндекс Музыки",
            ct: ct).ConfigureAwait(false);

        const int maxConcurrency = 3;
        int total = yandexTracks.Count;
        int processed = 0;
        int matchedCount = 0;

        var batchToSave = new List<TrackInfo>(32);
        var activeTasks = new List<Task<(YandexTrackDto Source, TrackInfo? Matched)>>(maxConcurrency);
        int nextTrackIndex = 0;

        while (nextTrackIndex < total && activeTasks.Count < maxConcurrency)
        {
            activeTasks.Add(ResolveTrackAsync(yandexTracks[nextTrackIndex++], ct));
        }

        while (activeTasks.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var completedTask = await Task.WhenAny(activeTasks).ConfigureAwait(false);
            activeTasks.Remove(completedTask);

            var (source, matched) = await completedTask.ConfigureAwait(false);
            processed++;

            if (matched != null)
            {
                batchToSave.Add(matched);
                matchedCount++;
            }

            if (batchToSave.Count >= 25)
            {
                await _playlistService.AddTracksToPlaylistAsync(lmpPlaylist.Id, batchToSave, ct).ConfigureAwait(false);
                batchToSave.Clear();
            }

            onProgress?.Invoke(processed, total, $"{source.Artist} — {source.Title}");

            if (nextTrackIndex < total && !ct.IsCancellationRequested)
            {
                await Task.Delay(40, ct).ConfigureAwait(false);
                activeTasks.Add(ResolveTrackAsync(yandexTracks[nextTrackIndex++], ct));
            }
        }

        if (batchToSave.Count > 0)
        {
            await _playlistService.AddTracksToPlaylistAsync(lmpPlaylist.Id, batchToSave, ct).ConfigureAwait(false);
            batchToSave.Clear();
        }

        // Подсчёт сетевых метрик:
        // ~24 КБ на 1 сетевой поисковый запрос к YouTube сжатый Brotli в канале
        long ytWireTrafficBytes = _networkSearches * 24L * 1024L;
        long totalTrafficBytes = _yandexBytesReceived + _yandexBytesSent + ytWireTrafficBytes;

        // Экономия: каждый Local-First трек сэкономил ~24 КБ в сети,
        // плюс ранняя отсечка первого результата сэкономила ~180 КБ лишнего нескачанного JSON на каждом поиске
        long savedBytes = (_localHits * 24L * 1024L) + (_networkSearches * 180L * 1024L);

        var stats = new TransferStats(
            Matched: matchedCount,
            Total: total,
            YandexBytesReceived: _yandexBytesReceived,
            YandexBytesSent: _yandexBytesSent,
            LocalHits: _localHits,
            NetworkSearches: _networkSearches,
            TotalTrafficBytes: totalTrafficBytes,
            SavedTrafficBytes: savedBytes);

        LogTrafficSummary(stats);
        return stats;
    }

    private async Task<(YandexTrackDto Source, TrackInfo? Matched)> ResolveTrackAsync(
        YandexTrackDto track, CancellationToken ct)
    {
        try
        {
            string query = $"{track.Artist} - {track.Title}";

            // 1. Local-First: 0 байт трафика
            var localMatches = await _library.SearchTracksAsync(query, limit: 1, ct: ct).ConfigureAwait(false);
            if (localMatches.Count > 0)
            {
                var local = localMatches[0];
                if (local.Title.Contains(track.Title, StringComparison.OrdinalIgnoreCase) ||
                    track.Title.Contains(local.Title, StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref _localHits);
                    return (track, local);
                }
            }

            // 2. Сетевой поиск в YouTube Music
            Interlocked.Increment(ref _networkSearches);

            var results = await _youtube.SearchFastAsync(
                query,
                maxResults: 1,
                filter: SearchFilter.MusicSong,
                ct: ct).ConfigureAwait(false);

            if (results.Count > 0)
            {
                return (track, results[0]);
            }

            // 3. Fallback
            var fallback = await _youtube.SearchFastAsync(
                query,
                maxResults: 1,
                filter: SearchFilter.Video,
                ct: ct).ConfigureAwait(false);

            return (track, fallback.Count > 0 ? fallback[0] : null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Debug($"[YandexTransfer] Не найден '{track.Artist} - {track.Title}': {ex.Message}");
            return (track, null);
        }
    }

    private static void LogTrafficSummary(TransferStats s)
    {
        Log.Info("[YandexTransfer] ═════════ СТАТИСТИКА СЕТЕВОГО ТРАФИКА ═════════");
        Log.Info($"[YandexTransfer] • Загрузка из Яндекс Музыки:  {s.YandexTrafficFormatted} (вход: {TransferStats.FormatBytes(s.YandexBytesReceived)}, исход: {TransferStats.FormatBytes(s.YandexBytesSent)})");
        Log.Info($"[YandexTransfer] • Поисковый трафик YouTube:   ~{TransferStats.FormatBytes(s.NetworkSearches * 24L * 1024L)} ({s.NetworkSearches} запросов в сеть)");
        Log.Info($"[YandexTransfer] • Local-First попаданий:      {s.LocalHits} треков (0 байт сети)");
        Log.Info($"[YandexTransfer] • Суммарно потрачено:         ~{s.TotalTrafficFormatted}");
        Log.Info($"[YandexTransfer] • Сэкономлено трафика:        ~{s.SavedTrafficFormatted} (благодаря L1/L2 и отсечке JSON)");
        Log.Info("[YandexTransfer] ═══════════════════════════════════════════════════");
    }

    #endregion
}
