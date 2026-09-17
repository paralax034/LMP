using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Helpers.Extensions;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Youtube.Bridge;

internal partial class PlayerResponse
{
    /// <inheritdoc/>
    private string? PlayabilityStatus { get; init; }

    /// <inheritdoc/>
    public string? PlayabilityError { get; init; }

    /// <summary>
    /// Требуется ли авторизация для просмотра (LOGIN_REQUIRED).
    /// </summary>
    public bool IsLoginRequired =>
        string.Equals(PlayabilityStatus, "LOGIN_REQUIRED", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Причина требования авторизации.
    /// </summary>
    public LoginRequiredReason LoginRequiredReason { get; init; } = LoginRequiredReason.Unknown;

    /// <inheritdoc/>
    public string? Category { get; init; }

    /// <inheritdoc/>
    public bool IsMusic { get; init; }

    /// <inheritdoc/>
    public bool IsAvailable { get; init; }

    /// <inheritdoc/>
    public bool IsPlayable { get; init; }

    /// <inheritdoc/>
    public string? Title { get; init; }

    /// <inheritdoc/>
    public string? ChannelId { get; init; }

    /// <inheritdoc/>
    public string? Author { get; init; }

    /// <inheritdoc/>
    public DateTimeOffset? UploadDate { get; init; }

    /// <inheritdoc/>
    public TimeSpan? Duration { get; init; }

    /// <inheritdoc/>
    public IReadOnlyList<ThumbnailData> Thumbnails { get; init; } = [];

    /// <inheritdoc/>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <inheritdoc/>
    public string? Description { get; init; }

    /// <inheritdoc/>
    public long? ViewCount { get; init; }

    /// <inheritdoc/>
    public string? PreviewVideoId { get; init; }

    public string? DashManifestUrl { get; init; }

    public string? HlsManifestUrl { get; init; }

    public IReadOnlyList<IStreamData> Streams { get; init; } = [];

    public IReadOnlyList<ClosedCaptionTrackData> ClosedCaptionTracks { get; init; } = [];

    /// <summary>
    /// Integrated loudness трека в LUFS из <c>playerConfig.audioConfig.perceptualLoudnessDb</c>.
    /// <c>float.NaN</c> если поле отсутствует.
    /// </summary>
    public float PerceptualLoudnessDb { get; init; } = float.NaN;

    public PlayerResponse(JsonElement content)
    {
        var playability = content.GetPropertyOrNull("playabilityStatus");
        PlayabilityStatus = playability?.GetPropertyOrNull("status")?.GetStringOrNull();
        PlayabilityError = playability?.GetPropertyOrNull("reason")?.GetStringOrNull();

        if (IsLoginRequired)
        {
            var reason = PlayabilityError ?? "";

            // 1. Bot detection — ОБЯЗАТЕЛЬНО первым
            // YouTube ANDROID_VR возвращает "Sign in to confirm you're not a bot"
            // со статусом LOGIN_REQUIRED. Слово "confirm" ранее ложно
            // срабатывало на ветку AgeRestricted.
            if (reason.Contains("bot", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("не робот", StringComparison.OrdinalIgnoreCase))
            {
                LoginRequiredReason = LoginRequiredReason.BotDetection;
            }
            else
            {
                // 2. Age gate
                var desktopAgeGateReason = playability
                    ?.GetPropertyOrNull("desktopLegacyAgeGateReason")
                    ?.GetInt32OrNull();

                if (desktopAgeGateReason == 1 ||
                    reason.Contains("age", StringComparison.OrdinalIgnoreCase))
                {
                    LoginRequiredReason = LoginRequiredReason.AgeRestricted;
                }
                // 3. Private
                else if (reason.Contains("private", StringComparison.OrdinalIgnoreCase))
                {
                    LoginRequiredReason = LoginRequiredReason.Private;
                }
                // 4. Members only
                else if (reason.Contains("members", StringComparison.OrdinalIgnoreCase) ||
                         reason.Contains("member", StringComparison.OrdinalIgnoreCase))
                {
                    LoginRequiredReason = LoginRequiredReason.MembersOnly;
                }
            }
        }

        Category = content
            .GetPropertyOrNull("microformat")
            ?.GetPropertyOrNull("playerMicroformatRenderer")
            ?.GetPropertyOrNull("category")
            ?.GetStringOrNull();

        var details = content.GetPropertyOrNull("videoDetails");

        IsMusic = string.Equals(Category, "Music", StringComparison.OrdinalIgnoreCase) ||
                  details?.GetPropertyOrNull("musicVideoType") != null;

        IsAvailable = !string.Equals(PlayabilityStatus, "error", StringComparison.OrdinalIgnoreCase)
                      && details is not null;

        IsPlayable = string.Equals(PlayabilityStatus, "ok", StringComparison.OrdinalIgnoreCase);

        Title = details?.GetPropertyOrNull("title")?.GetStringOrNull();
        ChannelId = details?.GetPropertyOrNull("channelId")?.GetStringOrNull();
        Author = details?.GetPropertyOrNull("author")?.GetStringOrNull();

        UploadDate = content
            .GetPropertyOrNull("microformat")
            ?.GetPropertyOrNull("playerMicroformatRenderer")
            ?.GetPropertyOrNull("uploadDate")
            ?.GetDateTimeOffset();

        var lengthSecStr = details?.GetPropertyOrNull("lengthSeconds")?.GetStringOrNull();
        if (lengthSecStr is not null && double.TryParse(lengthSecStr, CultureInfo.InvariantCulture, out var seconds))
        {
            Duration = TimeSpan.FromSeconds(seconds);
        }

        var thumbsArray = details
            ?.GetPropertyOrNull("thumbnail")
            ?.GetPropertyOrNull("thumbnails");

        if (thumbsArray is { ValueKind: JsonValueKind.Array } thumbsEl)
        {
            int len = thumbsEl.GetArrayLength();
            if (len > 0)
            {
                var result = new ThumbnailData[len];
                for (int i = 0; i < len; i++)
                    result[i] = new ThumbnailData(thumbsEl[i]);
                Thumbnails = result;
            }
        }

        var keywordsArray = details?.GetPropertyOrNull("keywords");
        if (keywordsArray is { ValueKind: JsonValueKind.Array } kwEl)
        {
            int len = kwEl.GetArrayLength();
            if (len > 0)
            {
                var result = new List<string>(len);
                for (int i = 0; i < len; i++)
                {
                    var s = kwEl[i].GetStringOrNull();
                    if (s is not null) result.Add(s);
                }
                if (result.Count > 0) Keywords = result;
            }
        }

        Description = details?.GetPropertyOrNull("shortDescription")?.GetStringOrNull();

        var viewCountStr = details?.GetPropertyOrNull("viewCount")?.GetStringOrNull();
        if (viewCountStr is not null && long.TryParse(viewCountStr, CultureInfo.InvariantCulture, out var vc))
        {
            ViewCount = vc;
        }

        PreviewVideoId =
            playability
                ?.GetPropertyOrNull("errorScreen")
                ?.GetPropertyOrNull("playerLegacyDesktopYpcTrailerRenderer")
                ?.GetPropertyOrNull("trailerVideoId")
                ?.GetStringOrNull()
            ?? (playability
                ?.GetPropertyOrNull("errorScreen")
                ?.GetPropertyOrNull("ypcTrailerRenderer")
                ?.GetPropertyOrNull("playerVars")
                ?.GetStringOrNull() is { } playerVars
                    ? UrlEx.GetQueryParameters(playerVars).GetValueOrDefault("video_id")
                    : null)
            ?? (playability
                ?.GetPropertyOrNull("errorScreen")
                ?.GetPropertyOrNull("ypcTrailerRenderer")
                ?.GetPropertyOrNull("playerResponse")
                ?.GetStringOrNull() is { } encoded
                    ? DecodePreviewVideoId(encoded)
                    : null);

        var streamingData = content.GetPropertyOrNull("streamingData");

        DashManifestUrl = streamingData?.GetPropertyOrNull("dashManifestUrl")?.GetStringOrNull();
        HlsManifestUrl = streamingData?.GetPropertyOrNull("hlsManifestUrl")?.GetStringOrNull();

        var streamsList = new List<IStreamData>(32);

        var formats = streamingData?.GetPropertyOrNull("formats");
        if (formats is { ValueKind: JsonValueKind.Array } formatsEl)
        {
            foreach (var j in formatsEl.EnumerateArray())
                streamsList.Add(new StreamData(j));
        }

        var adaptiveFormats = streamingData?.GetPropertyOrNull("adaptiveFormats");
        if (adaptiveFormats is { ValueKind: JsonValueKind.Array } adaptiveEl)
        {
            foreach (var j in adaptiveEl.EnumerateArray())
                streamsList.Add(new StreamData(j));
        }

        if (streamsList.Count > 0)
            Streams = streamsList;

        var tracks = content
            .GetPropertyOrNull("captions")
            ?.GetPropertyOrNull("playerCaptionsTracklistRenderer")
            ?.GetPropertyOrNull("captionTracks");

        if (tracks is { ValueKind: JsonValueKind.Array } tracksEl)
        {
            int len = tracksEl.GetArrayLength();
            if (len > 0)
            {
                var captionList = new List<ClosedCaptionTrackData>(len);
                foreach (var j in tracksEl.EnumerateArray())
                    captionList.Add(new ClosedCaptionTrackData(j));
                ClosedCaptionTracks = captionList;
            }
        }

        var audioConfig = content
            .GetPropertyOrNull("playerConfig"u8)
            ?.GetPropertyOrNull("audioConfig"u8);

        if (audioConfig.HasValue)
        {
            var val = audioConfig.Value
                .GetPropertyOrNull("perceptualLoudnessDb"u8)
                ?.GetDoubleOrNull();

            if (val.HasValue && double.IsFinite(val.Value))
                PerceptualLoudnessDb = (float)val.Value;
        }
    }

    /// <summary>
    /// Декодирует YouTube-специфичную base64-строку PlayerResponse и извлекает video_id.
    /// YouTube использует URL-safe base64 (- вместо +, _ вместо /).
    /// </summary>
    private static string? DecodePreviewVideoId(string encoded)
    {
        var bytes = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/'));
        var decoded = Encoding.UTF8.GetString(bytes);
        return MyRegex().Match(decoded).Groups[1].Value.NullIfWhiteSpace();
    }

    [GeneratedRegex(@"video_id=(.{11})")]
    private static partial Regex MyRegex();
}

internal partial class PlayerResponse
{
    public sealed class ClosedCaptionTrackData
    {
        /// <inheritdoc/>
        public string? Url { get; init; }

        /// <inheritdoc/>
        public string? LanguageCode { get; init; }

        /// <inheritdoc/>
        public string? LanguageName { get; init; }

        /// <inheritdoc/>
        public bool IsAutoGenerated { get; init; }

        public ClosedCaptionTrackData(JsonElement content)
        {
            Url = content.GetPropertyOrNull("baseUrl")?.GetStringOrNull();
            LanguageCode = content.GetPropertyOrNull("languageCode")?.GetStringOrNull();

            var name = content.GetPropertyOrNull("name");
            if (name is not null)
            {
                LanguageName = name.Value.GetPropertyOrNull("simpleText")?.GetStringOrNull()
                    ?? YoutubeParsingHelpers.ConcatTextRuns(name.Value.GetPropertyOrNull("runs"));
            }

            IsAutoGenerated = content
                .GetPropertyOrNull("vssId")
                ?.GetStringOrNull()
                ?.StartsWith("a.", StringComparison.OrdinalIgnoreCase) ?? false;
        }
    }
}

internal partial class PlayerResponse
{
    /// <summary>
    /// Представляет данные одного медиапотока из InnerTube PlayerResponse.
    /// Поддерживает как прямые URL (ANDROID_VR, авторизованный WEB_REMIX),
    /// так и зашифрованные через <c>signatureCipher</c> (WEB_REMIX без авторизации, WEB).
    /// </summary>
    public sealed class StreamData : IStreamData
    {
        /// <inheritdoc/>
        public int? Itag { get; init; }

        /// <inheritdoc/>
        public string? Url { get; init; }

        /// <inheritdoc/>
        public string? Signature { get; init; }

        /// <inheritdoc/>
        public string? SignatureParameter { get; init; }

        /// <inheritdoc/>
        public long? ContentLength { get; init; }

        /// <inheritdoc/>
        public long? Bitrate { get; init; }

        /// <inheritdoc/>
        public string? MimeType { get; init; }

        /// <inheritdoc/>
        public string? Container { get; init; }

        /// <summary>Все кодеки из MIME-типа.</summary>
        public string? Codecs { get; init; }

        /// <inheritdoc/>
        public string? AudioCodec { get; init; }

        /// <inheritdoc/>
        public string? AudioLanguageCode { get; init; }

        /// <inheritdoc/>
        public string? AudioLanguageName { get; init; }

        /// <inheritdoc/>
        public bool? IsAudioLanguageDefault { get; init; }

        /// <inheritdoc/>
        public string? VideoCodec { get; init; }

        /// <inheritdoc/>
        public string? VideoQualityLabel { get; init; }

        /// <inheritdoc/>
        public int? VideoWidth { get; init; }

        /// <inheritdoc/>
        public int? VideoHeight { get; init; }

        /// <inheritdoc/>
        public int? VideoFramerate { get; init; }

        /// <summary>Создаёт экземпляр данных потока из JSON-элемента PlayerResponse.</summary>
        /// <param name="content">JSON-элемент одного формата из <c>formats</c> / <c>adaptiveFormats</c>.</param>
        public StreamData(JsonElement content)
        {
            Itag = content.GetPropertyOrNull("itag")?.GetInt32OrNull();

            var cipherStr =
                content.GetPropertyOrNull("cipher")?.GetStringOrNull()
                ?? content.GetPropertyOrNull("signatureCipher")?.GetStringOrNull();

            IReadOnlyDictionary<string, string>? cipherData = null;
            if (cipherStr is not null)
            {
                cipherData = ParseCipherString(cipherStr);
                Signature = cipherData.GetValueOrDefault("s");
                SignatureParameter = cipherData.GetValueOrDefault("sp");
            }

            Url = content.GetPropertyOrNull("url")?.GetStringOrNull()
                ?? cipherData?.GetValueOrDefault("url");

            // Остальные свойства без изменений ↓
            ContentLength =
                content
                    .GetPropertyOrNull("contentLength")
                    ?.GetStringOrNull()
                    ?.Pipe(s => long.TryParse(s, CultureInfo.InvariantCulture, out var r) ? r : (long?)null)
                ?? Url
                    ?.Pipe(s => UrlEx.TryGetQueryParameterValue(s, "clen"))
                    ?.NullIfWhiteSpace()
                    ?.Pipe(s => long.TryParse(s, CultureInfo.InvariantCulture, out var r) ? r : (long?)null);

            Bitrate = content.GetPropertyOrNull("bitrate")?.GetInt64OrNull();
            MimeType = content.GetPropertyOrNull("mimeType")?.GetStringOrNull();

            bool isAudioOnly = MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ?? false;

            Container = MimeType?.SubstringUntil(";").SubstringAfter("/");
            Codecs = MimeType?.SubstringAfter("codecs=\"").SubstringUntil("\"");

            AudioCodec = isAudioOnly ? Codecs : Codecs?.SubstringAfter(", ").NullIfWhiteSpace();

            AudioLanguageCode = content
                .GetPropertyOrNull("audioTrack")
                ?.GetPropertyOrNull("id")
                ?.GetStringOrNull()
                ?.SubstringUntil(".");

            AudioLanguageName = content
                .GetPropertyOrNull("audioTrack")
                ?.GetPropertyOrNull("displayName")
                ?.GetStringOrNull();

            IsAudioLanguageDefault = content
                .GetPropertyOrNull("audioTrack")
                ?.GetPropertyOrNull("audioIsDefault")
                ?.GetBooleanOrNull();

            var rawVideoCodec = isAudioOnly ? null : Codecs?.SubstringUntil(", ").NullIfWhiteSpace();
            VideoCodec = string.Equals(rawVideoCodec, "unknown", StringComparison.OrdinalIgnoreCase)
                ? "av01.0.05M.08"
                : rawVideoCodec;

            VideoQualityLabel = content.GetPropertyOrNull("qualityLabel")?.GetStringOrNull();
            VideoWidth = content.GetPropertyOrNull("width")?.GetInt32OrNull();
            VideoHeight = content.GetPropertyOrNull("height")?.GetInt32OrNull();
            VideoFramerate = content.GetPropertyOrNull("fps")?.GetInt32OrNull();
        }

        /// <summary>
        /// Парсит строку <c>signatureCipher</c>/<c>cipher</c> в словарь параметров.
        /// </summary>
        /// <remarks>
        /// <para>Использует <see cref="ReadOnlySpan{T}"/> для zero-alloc разбора без промежуточных
        /// строк, защищая SOH/LOH от лишних аллокаций.</para>
        /// <para>Корректно обрабатывает <c>=</c> внутри значений (base64 padding <c>==</c>
        /// в подписи): разделяет только по первому вхождению <c>=</c>.</para>
        /// <para>Использует <see cref="Uri.UnescapeDataString"/> вместо
        /// <see cref="System.Net.WebUtility.UrlDecode"/>: последний заменяет <c>+</c> на пробел,
        /// что ломает base64-подписи содержащие символ <c>+</c>.</para>
        /// </remarks>
        /// <param name="cipher">URL-encoded строка вида <c>key1=val1&amp;key2=val2</c>.</param>
        /// <returns>Словарь декодированных пар ключ/значение.</returns>
        private static IReadOnlyDictionary<string, string> ParseCipherString(string cipher)
        {
            if (string.IsNullOrEmpty(cipher))
                return new Dictionary<string, string>(0);

            var result = new Dictionary<string, string>(3, StringComparer.Ordinal);
            var span = cipher.AsSpan();
            int start = 0;

            while (start < span.Length)
            {
                int ampIdx = span[start..].IndexOf('&');
                var pair = ampIdx < 0 ? span[start..] : span.Slice(start, ampIdx);

                int eqIdx = pair.IndexOf('=');
                if (eqIdx >= 0)
                {
                    var key = pair[..eqIdx].ToString();
                    var valEncoded = pair[(eqIdx + 1)..].ToString();
                    result[key] = Uri.UnescapeDataString(valEncoded);
                }

                if (ampIdx < 0) break;
                start += ampIdx + 1;
            }

            return result;
        }
    }
}

internal partial class PlayerResponse
{
    /// <summary>
    /// Парсит ответ PlayerResponse из сырого текста с мгновенным освобождением <see cref="JsonDocument"/>.
    /// </summary>
    public static PlayerResponse Parse(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return new PlayerResponse(doc.RootElement);
    }

    /// <summary>
    /// Парсит ответ PlayerResponse напрямую из сетевого потока без промежуточных строк в LOH.
    /// Освобождает <see cref="JsonDocument"/> сразу после формирования модели.
    /// </summary>
    /// <param name="stream">Сетевой поток HTTP-ответа.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Экземпляр плоского ответа PlayerResponse.</returns>
    public static async ValueTask<PlayerResponse> ParseAsync(Stream stream, CancellationToken ct = default)
    {
        using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
        return new PlayerResponse(doc.RootElement);
    }
}