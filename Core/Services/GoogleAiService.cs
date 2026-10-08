using System.Buffers;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LMP.Core.Helpers;
using LMP.Core.Helpers.Extensions;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Services;

/// <summary>
/// Service providing integration with Google AI (Gemini StreamGenerate RPC) using account session cookies.
/// Provides zero-cost generative music recommendations without paid API keys.
/// </summary>
public sealed partial class GoogleAiService : IDisposable
{
    private const string GeminiHost = "https://gemini.google.com";
    private const string GeminiAppUrl = "https://gemini.google.com/app";
    private const string StreamGenerateUrl = "https://gemini.google.com/_/BardChatUi/data/assistant.lamda.BardFrontendService/StreamGenerate";

    private readonly NetworkManager _network;
    private readonly CookieAuthService _auth;
    private readonly Lock _tokenLock = new();

    private string? _cachedAtToken;
    private string? _cachedBuildLabel;
    private DateTime _tokenExpiresAtUtc = DateTime.MinValue;

    public GoogleAiService(NetworkManager network, CookieAuthService auth)
    {
        _network = network;
        _auth = auth;

        _network.NetworkRebuilt += OnNetworkRebuilt;
    }

    private void OnNetworkRebuilt()
    {
        lock (_tokenLock)
        {
            _cachedAtToken = null;
            _cachedBuildLabel = null;
            _tokenExpiresAtUtc = DateTime.MinValue;
        }
        Log.Info("[GoogleAI] Network transition detected. Session tokens invalidated.");
    }

    /// <summary>
    /// Checks whether required Google session cookies are present.
    /// </summary>
    public bool HasRequiredCookies()
    {
        var cookieHeader = _auth.GetCookieHeader();
        return !string.IsNullOrEmpty(cookieHeader) &&
               (cookieHeader.Contains("__Secure-1PSID", StringComparison.Ordinal) ||
                cookieHeader.Contains("SAPISID", StringComparison.Ordinal));
    }

    /// <summary>
    /// Requests similar tracks from Gemini and returns a normalized list of artist/title strings.
    /// Uses an optimized fast-path prompt bypassing web search grounding.
    /// </summary>
    /// <param name="artist">Reference track artist.</param>
    /// <param name="title">Reference track title.</param>
    /// <param name="count">Desired number of recommendations.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of recommended tracks in "Artist - Track" format.</returns>
    public async Task<List<string>> GetSimilarTracksAsync(
        string artist,
        string title,
        int count = 10,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artist);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var prompt = $"Recommend exactly {count} songs strictly similar in musical genre, instrumentation, tempo, and vibe to \"{artist} - {title}\". " +
                     "Do not use Google Search or web retrieval tools. Rely strictly on your internal knowledge. " +
                     "Output strictly a raw JSON array of strings formatted as [\"Artist - Track Title\", ...]. " +
                     "No markdown blocks (no ```json), no intro text, no trailing explanations. Only the raw JSON array.";

        var rawResponse = await AskAsync(prompt, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(rawResponse))
            return [];

        return ParseTrackListFromJson(rawResponse);
    }

    /// <summary>
    /// Sends a prompt to Google AI via StreamGenerate gateway and extracts the generated text.
    /// </summary>
    public async Task<string> AskAsync(string prompt, CancellationToken ct = default)
    {
        if (!HasRequiredCookies())
            throw new InvalidOperationException("Google AI: Missing required Google authentication cookies (__Secure-1PSID or SAPISID).");

        var sw = Stopwatch.StartNew();
        var (atToken, buildLabel) = await EnsureSessionTokensAsync(ct).ConfigureAwait(false);

        var reqId = Random.Shared.Next(100000, 999999);
        long randPart = Random.Shared.NextInt64(1_000_000_000_000_000_000L, long.MaxValue);
        string fsid = $"-{randPart}";

        var queryBuilder = new StringBuilder(StreamGenerateUrl)
            .Append("?_reqid=").Append(reqId)
            .Append("&f.sid=").Append(fsid)
            .Append("&hl=en")
            .Append("&rt=c");

        if (!string.IsNullOrEmpty(buildLabel))
        {
            queryBuilder.Append("&bl=").Append(Uri.EscapeDataString(buildLabel));
        }

        var editUid = Guid.NewGuid().ToString("N")[..16];
        var editHex = Guid.NewGuid().ToString("N");

        var innerPayload = new object?[]
        {
            new object?[] { prompt, 0, null, Array.Empty<object>(), null, null, 0 },
            new[] { "en" },
            new object?[] { "", "", "", null, null, Array.Empty<object>() },
            editUid,
            editHex,
            null,
            new[] { 1 },
            0,
            Array.Empty<object>(),
            Array.Empty<object>(),
            1,
            0
        };

        var innerJson = JsonSerializer.Serialize(innerPayload);
        var envelopeJson = JsonSerializer.Serialize(new object?[] { null, innerJson });

        using var request = new HttpRequestMessage(HttpMethod.Post, queryBuilder.ToString());
        ApplyPostHeaders(request);

        var formContent = new Dictionary<string, string>(2, StringComparer.Ordinal)
        {
            ["f.req"] = envelopeJson,
            ["at"] = atToken
        };
        request.Content = new FormUrlEncodedContent(formContent);

        using var response = await _network.ApiClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var result = ExtractTextFromStreamGenerateResponse(payload);

        sw.Stop();
        Log.Info($"[GoogleAI] StreamGenerate completed in {sw.ElapsedMilliseconds}ms (chars={result.Length}).");
        return result;
    }

    private async Task<(string AtToken, string? BuildLabel)> EnsureSessionTokensAsync(CancellationToken ct)
    {
        lock (_tokenLock)
        {
            if (!string.IsNullOrEmpty(_cachedAtToken) && DateTime.UtcNow < _tokenExpiresAtUtc)
                return (_cachedAtToken, _cachedBuildLabel);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, GeminiAppUrl);
        ApplyGetHeaders(request);

        using var response = await _network.ApiClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var tokenMatch = NamedAtTokenRegex().Match(html);
        string? token = tokenMatch.Success ? tokenMatch.Groups[1].Value : null;

        if (string.IsNullOrEmpty(token))
        {
            var fallbackMatch = RawAtSignatureRegex().Match(html);
            if (fallbackMatch.Success)
                token = fallbackMatch.Groups[1].Value;
        }

        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException("Google AI: Failed to extract session token (at/SNlM0e/thykhd). Session may have expired.");
        }

        string? buildLabel = null;
        var buildMatch = BuildLabelRegex().Match(html);
        if (buildMatch.Success)
            buildLabel = buildMatch.Groups[1].Value;

        lock (_tokenLock)
        {
            _cachedAtToken = token;
            _cachedBuildLabel = buildLabel;
            _tokenExpiresAtUtc = DateTime.UtcNow.AddHours(6);
        }

        Log.Info($"[GoogleAI] Session token acquired (Build: {buildLabel ?? "default"}).");
        return (token, buildLabel);
    }

    private void ApplyGetHeaders(HttpRequestMessage request)
    {
        request.Headers.UserAgent.ParseAdd(YoutubeClientUtils.UaWeb);
        request.Headers.Add("Cookie", _auth.GetCookieHeader());
        request.Headers.Add("Referer", $"{GeminiHost}/");
        request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        request.Headers.Add("sec-ch-ua", "\"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"");
        request.Headers.Add("sec-ch-ua-mobile", "?0");
        request.Headers.Add("sec-ch-ua-platform", "\"Windows\"");
        request.Headers.Add("Sec-Fetch-Dest", "document");
        request.Headers.Add("Sec-Fetch-Mode", "navigate");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
    }

    private void ApplyPostHeaders(HttpRequestMessage request)
    {
        request.Headers.UserAgent.ParseAdd(YoutubeClientUtils.UaWeb);
        request.Headers.Add("Cookie", _auth.GetCookieHeader());
        request.Headers.Add("Origin", GeminiHost);
        request.Headers.Add("Referer", GeminiAppUrl);
        request.Headers.Add("X-Same-Domain", "1");
        request.Headers.Add("sec-ch-ua", "\"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"");
        request.Headers.Add("sec-ch-ua-mobile", "?0");
        request.Headers.Add("sec-ch-ua-platform", "\"Windows\"");
        request.Headers.Add("Sec-Fetch-Dest", "empty");
        request.Headers.Add("Sec-Fetch-Mode", "cors");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
    }

    private static string ExtractTextFromStreamGenerateResponse(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return string.Empty;

        string longestText = string.Empty;

        using var reader = new StringReader(payload);
        string? line;

        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("[[\"wrb.fr\"", StringComparison.Ordinal))
                continue;

            try
            {
                using var outerDoc = JsonDocument.Parse(trimmed);
                if (outerDoc.RootElement.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var packet in outerDoc.RootElement.EnumerateArray())
                {
                    if (packet.ValueKind != JsonValueKind.Array || packet.GetArrayLength() < 3)
                        continue;

                    var innerRaw = packet[2].GetString();
                    if (string.IsNullOrEmpty(innerRaw))
                        continue;

                    using var innerDoc = JsonDocument.Parse(innerRaw);
                    if (innerDoc.RootElement.ValueKind != JsonValueKind.Array ||
                        innerDoc.RootElement.GetArrayLength() < 5)
                        continue;

                    var candidatesArray = innerDoc.RootElement[4];
                    if (candidatesArray.ValueKind != JsonValueKind.Array ||
                        candidatesArray.GetArrayLength() == 0)
                        continue;

                    var firstCandidate = candidatesArray[0];
                    if (firstCandidate.ValueKind != JsonValueKind.Array ||
                        firstCandidate.GetArrayLength() < 2)
                        continue;

                    var textParts = firstCandidate[1];
                    if (textParts.ValueKind != JsonValueKind.Array ||
                        textParts.GetArrayLength() == 0)
                        continue;

                    var text = textParts[0].GetString();
                    if (!string.IsNullOrEmpty(text) && text.Length > longestText.Length)
                    {
                        longestText = text;
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        if (!string.IsNullOrEmpty(longestText))
            return longestText;

        // Check for fatal security / geo-restriction errors
        var errorMatch = BardErrorInfoRegex().Match(payload);
        if (errorMatch.Success)
        {
            var code = errorMatch.Groups[1].Value;
            if (code == "1060")
            {
                throw new InvalidOperationException("Google AI rejected request: BardErrorInfo [1060] - Region not supported. Please ensure your VPN is active and connected to an eligible region.");
            }
            if (code == "1000")
            {
                throw new InvalidOperationException("Google AI rejected request: BardErrorInfo [1000] - Rate limit exceeded. Please try again later.");
            }

            throw new InvalidOperationException($"Google AI rejected request: BardErrorInfo [{code}] - Internal error.");
        }

        return string.Empty;
    }

    private static List<string> ParseTrackListFromJson(string rawText)
    {
        var cleaned = rawText.Trim();

        if (cleaned.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[7..];
        else if (cleaned.StartsWith("```", StringComparison.Ordinal))
            cleaned = cleaned[3..];

        if (cleaned.EndsWith("```", StringComparison.Ordinal))
            cleaned = cleaned[..^3];

        cleaned = cleaned.Trim();

        int arrayStart = cleaned.IndexOf('[');
        int arrayEnd = cleaned.LastIndexOf(']');

        if (arrayStart >= 0 && arrayEnd > arrayStart)
        {
            cleaned = cleaned.Substring(arrayStart, arrayEnd - arrayStart + 1);
        }

        if (cleaned.Contains("\\\""))
        {
            cleaned = cleaned.Replace("\\\"", "\"");
        }

        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>(doc.RootElement.GetArrayLength());
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var val = item.GetString();
                    if (!string.IsNullOrWhiteSpace(val))
                        list.Add(val.Trim());
                }
                return list;
            }
        }
        catch (JsonException ex)
        {
            Log.Warn($"[GoogleAI] Failed to parse JSON track array: {ex.Message}. Text: {cleaned}");
        }

        return [];
    }

    public void Dispose()
    {
        _network.NetworkRebuilt -= OnNetworkRebuilt;
    }

    [GeneratedRegex(@"""(?:SNlM0e|thykhd)"":""([^""]+)""", RegexOptions.Compiled)]
    private static partial Regex NamedAtTokenRegex();

    [GeneratedRegex(@"""(AFWLbD[a-zA-Z0-9_\-]{40,180})""", RegexOptions.Compiled)]
    private static partial Regex RawAtSignatureRegex();

    [GeneratedRegex(@"""cfb2h"":""([^""]+)""", RegexOptions.Compiled)]
    private static partial Regex BuildLabelRegex();

    [GeneratedRegex(@"BardErrorInfo""?\s*,\s*\[(\d+)\]", RegexOptions.Compiled)]
    private static partial Regex BardErrorInfoRegex();
}