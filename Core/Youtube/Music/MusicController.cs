using System.Buffers;
using System.Net.Http.Headers;
using System.Text.Json;
using LMP.Core.Youtube.Utils;
using LMP.Core.Helpers.Extensions;
using LMP.Core.Youtube.Exceptions;

namespace LMP.Core.Youtube.Music;

internal class MusicController(HttpClient http)
{
    private const string ApiUrl = "https://music.youtube.com/youtubei/v1";

    private static readonly MediaTypeHeaderValue JsonContentType = new("application/json");

    private static void WriteContext(Utf8JsonWriter writer)
    {
        writer.WritePropertyName(InnerTubeTokens.Context);
        writer.WriteStartObject();

        writer.WritePropertyName(InnerTubeTokens.Client);
        writer.WriteStartObject();
        writer.WriteString(InnerTubeTokens.ClientName, InnerTubeTokens.WebRemix);
        writer.WriteString(InnerTubeTokens.ClientVersion, YoutubeHttpHandler.MusicClientVersion);
        writer.WriteString(InnerTubeTokens.Hl, YoutubeHttpHandler.GetHl());
        writer.WriteString(InnerTubeTokens.Gl, YoutubeHttpHandler.GetGl());

        var visitorData = YoutubeClientUtils.VisitorData;
        if (!string.IsNullOrEmpty(visitorData))
            writer.WriteString(InnerTubeTokens.VisitorData, visitorData);
        else
            writer.WriteNull(InnerTubeTokens.VisitorData);

        writer.WriteEndObject(); // client

        writer.WriteEndObject(); // context
    }

    private static void UpdateVisitorData(JsonElement root)
    {
        var newVisitorData = root.GetPropertyOrNull(InnerTubeTokens.ResponseContext)
            ?.GetPropertyOrNull(InnerTubeTokens.VisitorData)
            ?.GetStringOrNull();

        // Обновляем глобальный VisitorData напрямую
        if (!string.IsNullOrWhiteSpace(newVisitorData) && newVisitorData != YoutubeClientUtils.VisitorData)
        {
            YoutubeClientUtils.VisitorData = newVisitorData;
        }
    }

    private static void AttachVisitorDataToRequest(HttpRequestMessage request)
    {
        var visitorData = YoutubeClientUtils.VisitorData;
        if (!string.IsNullOrEmpty(visitorData))
        {
            request.Options.Set(YoutubeHttpHandler.VisitorDataKey, visitorData);
        }
    }

    /// <summary>
    /// Создаёт HTTP-контент с использованием ReadOnlyMemoryContent для снижения GC pressure.
    /// </summary>
    private static ReadOnlyMemoryContent CreateJsonContent(Action<Utf8JsonWriter> writeBody)
    {
        var bufferWriter = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(bufferWriter))
        {
            writer.WriteStartObject();
            WriteContext(writer);
            writeBody(writer);
            writer.WriteEndObject();
        }

        var content = new ReadOnlyMemoryContent(bufferWriter.WrittenMemory);
        content.Headers.ContentType = JsonContentType;
        return content;
    }

    private async Task<JsonElement> PostAsync(
        string endpoint,
        Action<Utf8JsonWriter> writeBody,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiUrl}/{endpoint}?prettyPrint=false");
        AttachVisitorDataToRequest(request);
        request.Content = CreateJsonContent(writeBody);

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var jsonDoc = await Json.ParseAsync(stream, cancellationToken);
        UpdateVisitorData(jsonDoc);

        return jsonDoc;
    }

    private async Task PostFireAndForgetAsync(
        string endpoint,
        Action<Utf8JsonWriter> writeBody,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiUrl}/{endpoint}?prettyPrint=false");
        AttachVisitorDataToRequest(request);
        request.Content = CreateJsonContent(writeBody);

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            UpdateVisitorData(await Json.ParseAsync(stream, cancellationToken));
        }
        catch { /* best effort */ }
    }

    #region Browse

    public async ValueTask<MusicBrowseResponse> GetBrowseAsync(
        string? browseId = null,
        string? continuation = null,
        CancellationToken cancellationToken = default)
    {
        var jsonDoc = await PostAsync("browse", writer =>
        {
            if (!string.IsNullOrEmpty(continuation))
                writer.WriteString(InnerTubeTokens.Continuation, continuation);
            else if (!string.IsNullOrEmpty(browseId))
                writer.WriteString(InnerTubeTokens.BrowseId, browseId);
        }, cancellationToken);

        return new MusicBrowseResponse(jsonDoc);
    }

    #endregion

    #region Like

    public async Task SendLikeActionAsync(
        string endpoint, string videoId, CancellationToken cancellationToken)
    {
        await PostFireAndForgetAsync(endpoint, writer =>
        {
            writer.WritePropertyName(InnerTubeTokens.Target);
            writer.WriteStartObject();
            writer.WriteString(InnerTubeTokens.VideoId, videoId);
            writer.WriteEndObject();
        }, cancellationToken);
    }

    #endregion

    #region Account

    /// <summary>
    /// Асинхронно получает структуру переключателя аккаунтов.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <exception cref="LoginRequiredException">Выбрасывается, когда сессия авторизации недействительна или истекла.</exception>
    public async Task<JsonElement> GetAccountSwitcherAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://music.youtube.com/getAccountSwitcherEndpoint");
        AttachVisitorDataToRequest(request);

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        // Перехватываем 302/307 редирект (свидетельствует об истечении сессии/кук)
        if (response.StatusCode is System.Net.HttpStatusCode.Redirect or System.Net.HttpStatusCode.Found)
        {
            Log.Warn("[MusicController] Switcher returned 302 Found redirect. Session is expired.");

            // Возвращаем точную причину — SessionExpired
            throw new LoginRequiredException(
                "Authentication is required. Current session has expired.",
                string.Empty,
                LoginRequiredReason.SessionExpired);
        }

        response.EnsureSuccessStatusCode();

        var jsonStr = await response.Content.ReadAsStringAsync(cancellationToken);

        if (jsonStr.StartsWith(")]}'"))
        {
            jsonStr = jsonStr[4..];
        }

        return Json.Parse(jsonStr);
    }

    public async Task<JsonElement> GetAccountMenuAsync(
        CancellationToken cancellationToken = default)
    {
        return await PostAsync("account/account_menu", _ => { }, cancellationToken);
    }

    #endregion
}