using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Youtube;

/// <summary>
/// Делегирующий обработчик HTTP-запросов для интеграции с YouTube API.
/// Настраивает заголовки авторизации, куки и контексты запросов.
/// </summary>
public partial class YoutubeHttpHandler(HttpClient http, CookieAuthService? authService, bool disposeClient = false)
    : ClientDelegatingHandler(http, disposeClient)
{
    /// <summary>Версия клиента YouTube Music.</summary>
    public const string MusicClientVersion = "1.20260126.03.00";

    /// <summary>Идентификатор клиента YouTube Music.</summary>
    public const string MusicClientName = "67";

    /// <summary>Версия клиента YouTube Web.</summary>
    public const string WebClientVersion = "2.20260126.01.00";

    /// <summary>Идентификатор клиента YouTube Web.</summary>
    public const string WebClientName = "1";

    /// <summary>Origin заголовок для YouTube Music.</summary>
    public const string MusicOrigin = "https://music.youtube.com";

    /// <summary>Origin заголовок для основного YouTube.</summary>
    public const string YoutubeOrigin = "https://www.youtube.com";

    /// <summary>Максимально время ожидания ответа от сервера</summary>
    private static readonly TimeSpan PerAttemptTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Ключ параметров для передачи Visitor Data в запросе.</summary>
    public static readonly HttpRequestOptionsKey<string> VisitorDataKey = new("VisitorData");

    /// <summary>Ключ параметров, указывающий на контекст запроса плеера.</summary>
    public static readonly HttpRequestOptionsKey<bool> IsPlayerContext = new("IsPlayerContext");

    /// <summary>Ключ параметров, указывающий на мобильный или ТВ клиент (без авторизации SAPISIDHASH).</summary>
    public static readonly HttpRequestOptionsKey<bool> IsMobileClient = new("IsMobileClient");

    /// <summary>Возвращает текущую языковую локаль системы.</summary>
    public static string GetHl() => LocalizationService.Instance.CurrentLanguageCode ?? "en";

    /// <summary>Возвращает двухбуквенный ISO-код региона системы.</summary>
    public static string GetGl()
    {
        try { return RegionInfo.CurrentRegion.TwoLetterISORegionName ?? "US"; }
        catch { return "US"; }
    }

    /// <summary>
    /// Генерация заголовка авторизации SAPISIDHASH с использованием Span и stackalloc для минимизации аллокаций.
    /// </summary>
    internal static string? GetAuthHeader(string? sapisid, string origin)
    {
        if (string.IsNullOrWhiteSpace(sapisid)) return null;

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var timestampStr = timestamp.ToString(CultureInfo.InvariantCulture);

        int payloadLen = timestampStr.Length + 1 + sapisid.Length + 1 + origin.Length;
        Span<char> payloadChars = payloadLen <= 256
            ? stackalloc char[payloadLen]
            : new char[payloadLen];

        int pos = 0;
        timestampStr.AsSpan().CopyTo(payloadChars[pos..]);
        pos += timestampStr.Length;
        payloadChars[pos++] = ' ';
        sapisid.AsSpan().CopyTo(payloadChars[pos..]);
        pos += sapisid.Length;
        payloadChars[pos++] = ' ';
        origin.AsSpan().CopyTo(payloadChars[pos..]);

        int utf8Len = Encoding.UTF8.GetByteCount(payloadChars);
        Span<byte> utf8Bytes = utf8Len <= 512
            ? stackalloc byte[utf8Len]
            : new byte[utf8Len];
        Encoding.UTF8.GetBytes(payloadChars, utf8Bytes);

        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(utf8Bytes, hash);
        var hashHex = Convert.ToHexStringLower(hash);

        return string.Concat("SAPISIDHASH ", timestampStr, "_", hashHex);
    }

    /// <summary>
    /// Настраивает заголовки, куки и параметры безопасности для исходящего запроса.
    /// </summary>
    private HttpRequestMessage HandleRequest(HttpRequestMessage request)
    {
        if (request.RequestUri is null) return request;

        var host = request.RequestUri.Host;
        bool isMusic = host.Contains("music.youtube.com");
        bool isPlayerRequest = request.Options.TryGetValue(IsPlayerContext, out var p) && p;
        bool isMobileClient = request.Options.TryGetValue(IsMobileClient, out var m) && m;

        bool isYoutubeDomain = host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase);

        if (!request.Headers.Contains("User-Agent"))
        {
            request.Headers.Add("User-Agent", YoutubeClientUtils.UaWeb);
        }

        if (!request.Headers.Contains("Accept-Language"))
            request.Headers.Add("Accept-Language", "en,ru;q=0.9");

        if (isYoutubeDomain && !isMobileClient && authService is { IsAuthenticated: true })
        {
            var cookieHeader = authService.GetCookieHeader();
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                if (request.Headers.Contains("Cookie"))
                    request.Headers.Remove("Cookie");
                request.Headers.Add("Cookie", cookieHeader);
            }
        }

        // Заголовки для YouTube Music — только для НЕ-player запросов
        if (isMusic && !isPlayerRequest)
        {
            request.Headers.Remove("X-YouTube-Client-Name");
            request.Headers.Remove("X-YouTube-Client-Version");
            request.Headers.Remove("Referer");
            request.Headers.Remove("Origin");
            request.Headers.Remove("X-Goog-AuthUser");

            request.Headers.Add("Referer", MusicOrigin);
            request.Headers.Add("X-YouTube-Client-Name", MusicClientName);
            request.Headers.Add("X-YouTube-Client-Version", MusicClientVersion);
            request.Headers.Add("X-Goog-Api-Format-Version", "1");
            request.Headers.Add("Origin", MusicOrigin);

            if (authService?.IsAuthenticated == true)
            {
                var authUser = string.IsNullOrEmpty(authService.State.AuthUser) ? AuthState.DefaultAuthUser : authService.State.AuthUser;
                request.Headers.Add("X-Goog-AuthUser", authUser);
            }
        }
        else if (!isPlayerRequest && request.Method == HttpMethod.Post)
        {
            if (!request.Headers.Contains("Origin"))
                request.Headers.Add("Origin", YoutubeOrigin);
        }

        if (request.Options.TryGetValue(VisitorDataKey, out var visitorData) && !string.IsNullOrEmpty(visitorData))
        {
            if (request.Headers.Contains("X-Goog-Visitor-Id"))
                request.Headers.Remove("X-Goog-Visitor-Id");
            request.Headers.Add("X-Goog-Visitor-Id", visitorData);
        }

        bool isYoutubeApi = isYoutubeDomain && request.RequestUri.AbsolutePath.Contains("/youtubei/v1/");

        if (request.Method == HttpMethod.Post &&
               isYoutubeApi &&
               !isMobileClient &&
               authService?.IsAuthenticated == true)
        {
            var origin = isMusic ? MusicOrigin : YoutubeOrigin;
            var sapisid = authService.GetCookieValue("SAPISID");

            if (!string.IsNullOrWhiteSpace(sapisid))
            {
                var authHeader = GetAuthHeader(sapisid, origin);
                if (!string.IsNullOrWhiteSpace(authHeader))
                {
                    if (request.Headers.Contains("Authorization"))
                        request.Headers.Remove("Authorization");
                    request.Headers.Add("Authorization", authHeader);

                    if (request.Headers.Contains("Origin"))
                        request.Headers.Remove("Origin");
                    request.Headers.Add("Origin", origin);

                    if (request.Headers.Contains("X-Origin"))
                        request.Headers.Remove("X-Origin");
                    request.Headers.Add("X-Origin", origin);

                    if (request.Headers.Contains("X-Goog-AuthUser"))
                        request.Headers.Remove("X-Goog-AuthUser");

                    var authUser = string.IsNullOrEmpty(authService.State.AuthUser) ? AuthState.DefaultAuthUser : authService.State.AuthUser;
                    request.Headers.Add("X-Goog-AuthUser", authUser);
                }
            }
        }

        return request;
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
       HttpRequestMessage request,
       CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        for (var i = 0; i < 3; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var requestClone = await CloneRequestAsync(request).ConfigureAwait(false);
            var processedRequest = HandleRequest(requestClone);

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(PerAttemptTimeout);

            try
            {
                var response = await base.SendAsync(processedRequest, attemptCts.Token).ConfigureAwait(false);

                bool cookiesUpdated = false;
                if (authService != null && response.Headers.TryGetValues("Set-Cookie", out var newCookies))
                    cookiesUpdated = authService.UpdateCookies(newCookies);

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    Log.Warn($"[YouTube] 401 Unauthorized (Attempt {i + 1}).");

                    if (cookiesUpdated)
                    {
                        await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    return response;
                }

                if ((int)response.StatusCode == 429)
                {
                    Log.Warn("[YouTube] 429 Too Many Requests. Waiting...");
                    await Task.Delay(2000 + (i * 1000), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return response;
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw;

                lastException = ex;

                if (i < 2)
                {
                    Log.Warn($"[YouTube] Network error: {ex.Message}. Retrying {i + 1}...");
                    try
                    {
                        await Task.Delay(1000 * (i + 1), cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    continue;
                }

                // Последняя попытка исчерпана — классифицируем сетевую ошибку
                var networkEx = YoutubeNetworkException.TryClassify(ex, cancellationToken);
                if (networkEx is not null)
                {
                    Log.Warn($"[YouTube] Network failure after retries: {networkEx.ErrorType} — {ex.Message}");
                    throw networkEx;
                }

                throw;
            }
        }

        // 3 итерации завершились без return (все 401 с обновлением кук / 429)
        if (lastException is not null)
        {
            var networkEx = YoutubeNetworkException.TryClassify(lastException, cancellationToken);
            if (networkEx is not null)
                throw networkEx;
        }

        throw new YoutubeExplodeException("Failed to execute request after multiple attempts.");
    }

    /// <summary>
    /// Клонирует HTTP-запрос для безопасных повторных отправлений при сбоях сети.
    /// </summary>
    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version
        };

        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        foreach (var option in request.Options)
            clone.Options.Set(new HttpRequestOptionsKey<object>(option.Key)!, option.Value);

        if (request.Content != null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            clone.Content = new ByteArrayContent(bytes);

            if (request.Content.Headers.ContentType != null)
                clone.Content.Headers.ContentType = request.Content.Headers.ContentType;
        }

        return clone;
    }
}