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
    private string? PlayabilityStatus { get; init; }
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
    public string? Category { get; init; }
    public bool IsMusic { get; init; }
    public bool IsAvailable { get; init; }
    public bool IsPlayable { get; init; }
    public string? Title { get; init; }
    public string? ChannelId { get; init; }
    public string? Author { get; init; }
    public DateTimeOffset? UploadDate { get; init; }
    public TimeSpan? Duration { get; init; }
    public IReadOnlyList<ThumbnailData> Thumbnails { get; init; } = [];
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public string? Description { get; init; }
    public long? ViewCount { get; init; }
    public string? PreviewVideoId { get; init; }

    public IReadOnlyList<IStreamData> Streams { get; init; } = [];

    public IReadOnlyList<ClosedCaptionTrackData> ClosedCaptionTracks { get; init; } = [];

    /// <summary>
    /// Integrated loudness трека в LUFS из <c>playerConfig.audioConfig.perceptualLoudnessDb</c>.
    /// <c>float.NaN</c> если поле отсутствует.
    /// </summary>
    public float PerceptualLoudnessDb { get; init; } = float.NaN;

    public PlayerResponse(JsonElement content)
    {
        var playability = content.GetPropertyOrNull(InnerTubeTokens.PlayabilityStatus);
        PlayabilityStatus = playability?.GetPropertyOrNull(InnerTubeTokens.Status)?.GetStringOrNull();
        PlayabilityError = playability?.GetPropertyOrNull(InnerTubeTokens.Reason)?.GetStringOrNull();

        if (IsLoginRequired)
        {
            var reason = PlayabilityError ?? "";

            // 1. Bot detection (InnerTube возвращает детерминированную английскую ошибку при InvariantHl)
            if (reason.Contains("bot", StringComparison.OrdinalIgnoreCase))
            {
                LoginRequiredReason = LoginRequiredReason.BotDetection;
            }
            else
            {
                // 2. Age gate
                var desktopAgeGateReason = playability
                    ?.GetPropertyOrNull(InnerTubeTokens.DesktopLegacyAgeGateReason)
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
            .GetPropertyOrNull(InnerTubeTokens.Microformat)
            ?.GetPropertyOrNull(InnerTubeTokens.PlayerMicroformatRenderer)
            ?.GetPropertyOrNull(InnerTubeTokens.Category)
            ?.GetStringOrNull();

        var details = content.GetPropertyOrNull(InnerTubeTokens.VideoDetails);

        var categoryId = details?.GetPropertyOrNull(InnerTubeTokens.CategoryId)?.GetStringOrNull();

        IsMusic = string.Equals(Category, "Music", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(categoryId, InnerTubeConstants.MusicCategoryId, StringComparison.Ordinal) ||
                  details?.GetPropertyOrNull(InnerTubeTokens.MusicVideoType) != null;

        IsAvailable = !string.Equals(PlayabilityStatus, "error", StringComparison.OrdinalIgnoreCase)
                      && details is not null;

        IsPlayable = string.Equals(PlayabilityStatus, "ok", StringComparison.OrdinalIgnoreCase);

        Title = details?.GetPropertyOrNull(InnerTubeTokens.Title)?.GetStringOrNull();
        ChannelId = details?.GetPropertyOrNull(InnerTubeTokens.ChannelId)?.GetStringOrNull();
        Author = details?.GetPropertyOrNull(InnerTubeTokens.Author)?.GetStringOrNull();

        UploadDate = content
            .GetPropertyOrNull(InnerTubeTokens.Microformat)
            ?.GetPropertyOrNull(InnerTubeTokens.PlayerMicroformatRenderer)
            ?.GetPropertyOrNull(InnerTubeTokens.UploadDate)
            ?.GetDateTimeOffset();

        var lengthSecStr = details?.GetPropertyOrNull(InnerTubeTokens.LengthSeconds)?.GetStringOrNull();
        if (lengthSecStr is not null && double.TryParse(lengthSecStr, CultureInfo.InvariantCulture, out var seconds))
        {
            Duration = TimeSpan.FromSeconds(seconds);
        }

        var thumbsArray = details
            ?.GetPropertyOrNull(InnerTubeTokens.Thumbnail)
            ?.GetPropertyOrNull(InnerTubeTokens.Thumbnails);

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

        var keywordsArray = details?.GetPropertyOrNull(InnerTubeTokens.Keywords);
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

        Description = details?.GetPropertyOrNull(InnerTubeTokens.ShortDescription)?.GetStringOrNull();

        var viewCountStr = details?.GetPropertyOrNull(InnerTubeTokens.ViewCount)?.GetStringOrNull();
        if (viewCountStr is not null && long.TryParse(viewCountStr, CultureInfo.InvariantCulture, out var vc))
        {
            ViewCount = vc;
        }
        PreviewVideoId =
                    playability
                        ?.GetPropertyOrNull(InnerTubeTokens.ErrorScreen)
                        ?.GetPropertyOrNull(InnerTubeTokens.PlayerLegacyDesktopYpcTrailerRenderer)
                        ?.GetPropertyOrNull(InnerTubeTokens.TrailerVideoId)
                        ?.GetStringOrNull()
                    ?? (playability
                        ?.GetPropertyOrNull(InnerTubeTokens.ErrorScreen)
                        ?.GetPropertyOrNull(InnerTubeTokens.YpcTrailerRenderer)
                        ?.GetPropertyOrNull(InnerTubeTokens.PlayerVars)
                        ?.GetStringOrNull() is { } playerVars
                            ? UrlEx.GetQueryParameters(playerVars).GetValueOrDefault("video_id")
                            : null)
                    ?? (playability
                        ?.GetPropertyOrNull(InnerTubeTokens.ErrorScreen)
                        ?.GetPropertyOrNull(InnerTubeTokens.YpcTrailerRenderer)
                        ?.GetPropertyOrNull(InnerTubeTokens.PlayerResponseProperty)
                        ?.GetStringOrNull() is { } encoded
                            ? DecodePreviewVideoId(encoded)
                            : null);

        var streamingData = content.GetPropertyOrNull(InnerTubeTokens.StreamingData);

        var streamsList = new List<IStreamData>(32);

        var formats = streamingData?.GetPropertyOrNull(InnerTubeTokens.Formats);
        if (formats is { ValueKind: JsonValueKind.Array } formatsEl)
        {
            foreach (var j in formatsEl.EnumerateArray())
                streamsList.Add(new StreamData(j));
        }

        var adaptiveFormats = streamingData?.GetPropertyOrNull(InnerTubeTokens.AdaptiveFormats);
        if (adaptiveFormats is { ValueKind: JsonValueKind.Array } adaptiveEl)
        {
            foreach (var j in adaptiveEl.EnumerateArray())
                streamsList.Add(new StreamData(j));
        }

        if (streamsList.Count > 0)
            Streams = streamsList;

        var tracks = content
            .GetPropertyOrNull(InnerTubeTokens.Captions)
            ?.GetPropertyOrNull(InnerTubeTokens.PlayerCaptionsTracklistRenderer)
            ?.GetPropertyOrNull(InnerTubeTokens.CaptionTracks);

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
            .GetPropertyOrNull(InnerTubeTokens.PlayerConfig)
            ?.GetPropertyOrNull(InnerTubeTokens.AudioConfig);

        if (audioConfig.HasValue)
        {
            var val = audioConfig.Value
                .GetPropertyOrNull(InnerTubeTokens.PerceptualLoudnessDb)
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
        public string? Url { get; init; }
        public string? LanguageCode { get; init; }
        public string? LanguageName { get; init; }
        public bool IsAutoGenerated { get; init; }

        public ClosedCaptionTrackData(JsonElement content)
        {
            Url = content.GetPropertyOrNull(InnerTubeTokens.BaseUrl)?.GetStringOrNull();
            LanguageCode = content.GetPropertyOrNull(InnerTubeTokens.LanguageCode)?.GetStringOrNull();

            var name = content.GetPropertyOrNull(InnerTubeTokens.Name);
            if (name is not null)
            {
                LanguageName = name.Value.GetPropertyOrNull(InnerTubeTokens.SimpleText)?.GetStringOrNull()
                    ?? YoutubeParsingHelpers.ConcatTextRuns(name.Value.GetPropertyOrNull(InnerTubeTokens.Runs));
            }

            IsAutoGenerated = content
                .GetPropertyOrNull(InnerTubeTokens.VssId)
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
        public int? Itag { get; init; }
        public string? Url { get; init; }
        public string? Signature { get; init; }
        public string? SignatureParameter { get; init; }
        public long? ContentLength { get; init; }
        public long? Bitrate { get; init; }
        public string? MimeType { get; init; }
        public string? Container { get; init; }

        /// <summary>Все кодеки из MIME-типа.</summary>
        public string? Codecs { get; init; }
        public string? AudioCodec { get; init; }
        public int AudioChannels { get; init; } = 2;
        public string? AudioLanguageCode { get; init; }
        public string? AudioLanguageName { get; init; }
        public bool? IsAudioLanguageDefault { get; init; }
        public string? VideoCodec { get; init; }
        public string? VideoQualityLabel { get; init; }
        public int? VideoWidth { get; init; }
        public int? VideoHeight { get; init; }
        public int? VideoFramerate { get; init; }

        /// <summary>Создаёт экземпляр данных потока из JSON-элемента PlayerResponse.</summary>
        /// <param name="content">JSON-элемент одного формата из <c>formats</c> / <c>adaptiveFormats</c>.</param>
        public StreamData(JsonElement content)
        {
            Itag = content.GetPropertyOrNull(InnerTubeTokens.Itag)?.GetInt32OrNull();

            var cipherStr =
                content.GetPropertyOrNull(InnerTubeTokens.Cipher)?.GetStringOrNull()
                ?? content.GetPropertyOrNull(InnerTubeTokens.SignatureCipher)?.GetStringOrNull();

            if (cipherStr is not null)
            {
                ParseCipherStringInto(cipherStr, out var s, out var sp, out var u);
                Signature = s;
                SignatureParameter = sp;
                if (!string.IsNullOrEmpty(u))
                    Url = u;
            }

            Url ??= content.GetPropertyOrNull(InnerTubeTokens.Url)?.GetStringOrNull();

            ContentLength =
                content
                    .GetPropertyOrNull(InnerTubeTokens.ContentLength)
                    ?.GetStringOrNull()
                    ?.Pipe(static s => long.TryParse(s, CultureInfo.InvariantCulture, out var r) ? r : (long?)null)
                ?? Url
                    ?.Pipe(static s => UrlEx.TryGetQueryParameterValue(s, "clen"))
                    ?.NullIfWhiteSpace()
                    ?.Pipe(static s => long.TryParse(s, CultureInfo.InvariantCulture, out var r) ? r : (long?)null);

            Bitrate = content.GetPropertyOrNull(InnerTubeTokens.Bitrate)?.GetInt64OrNull();
            MimeType = content.GetPropertyOrNull(InnerTubeTokens.MimeType)?.GetStringOrNull();

            bool isAudioOnly = MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ?? false;

            if (MimeType is not null)
            {
                ExtractContainerAndCodecs(MimeType, isAudioOnly, out var container, out var codecs, out var audioCodec, out var videoCodec);
                Container = container;
                Codecs = codecs;
                AudioCodec = audioCodec;
                VideoCodec = videoCodec;
            }

            AudioChannels = content.GetPropertyOrNull(InnerTubeTokens.AudioChannels)?.GetInt32OrNull() ?? 2;

            var audioTrack = content.GetPropertyOrNull(InnerTubeTokens.AudioTrack);
            if (audioTrack.HasValue)
            {
                var trackId = audioTrack.Value.GetPropertyOrNull(InnerTubeTokens.Id)?.GetStringOrNull();
                if (trackId != null)
                {
                    int dotIdx = trackId.IndexOf('.');
                    AudioLanguageCode = dotIdx >= 0 ? trackId[..dotIdx] : trackId;
                }

                AudioLanguageName = audioTrack.Value.GetPropertyOrNull(InnerTubeTokens.DisplayName)?.GetStringOrNull();
                IsAudioLanguageDefault = audioTrack.Value.GetPropertyOrNull(InnerTubeTokens.AudioIsDefault)?.GetBooleanOrNull();
            }

            VideoQualityLabel = content.GetPropertyOrNull(InnerTubeTokens.QualityLabel)?.GetStringOrNull();
            VideoWidth = content.GetPropertyOrNull(InnerTubeTokens.Width)?.GetInt32OrNull();
            VideoHeight = content.GetPropertyOrNull(InnerTubeTokens.Height)?.GetInt32OrNull();
            VideoFramerate = content.GetPropertyOrNull(InnerTubeTokens.Fps)?.GetInt32OrNull();
        }

        private static void ExtractContainerAndCodecs(
            string mimeType,
            bool isAudioOnly,
            out string? container,
            out string? codecs,
            out string? audioCodec,
            out string? videoCodec)
        {
            var span = mimeType.AsSpan();

            int slashIdx = span.IndexOf('/');
            int semiIdx = span.IndexOf(';');

            if (slashIdx >= 0)
            {
                container = semiIdx > slashIdx
                    ? span.Slice(slashIdx + 1, semiIdx - slashIdx - 1).ToString()
                    : span[(slashIdx + 1)..].ToString();
            }
            else
            {
                container = null;
            }

            codecs = null;
            audioCodec = null;
            videoCodec = null;

            int codecsPrefixIdx = span.IndexOf("codecs=\"".AsSpan(), StringComparison.Ordinal);
            if (codecsPrefixIdx >= 0)
            {
                var afterCodecs = span[(codecsPrefixIdx + 8)..];
                int quoteIdx = afterCodecs.IndexOf('"');
                if (quoteIdx >= 0)
                {
                    var codecsSpan = afterCodecs[..quoteIdx];
                    codecs = codecsSpan.ToString();

                    if (isAudioOnly)
                    {
                        audioCodec = codecs;
                    }
                    else
                    {
                        int commaIdx = codecsSpan.IndexOf(", ".AsSpan(), StringComparison.Ordinal);
                        if (commaIdx >= 0)
                        {
                            var vSpan = codecsSpan[..commaIdx].Trim();
                            var aSpan = codecsSpan[(commaIdx + 2)..].Trim();

                            audioCodec = aSpan.Length > 0 ? aSpan.ToString() : null;
                            videoCodec = vSpan.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                                ? "av01.0.05M.08"
                                : vSpan.Length > 0 ? vSpan.ToString() : null;
                        }
                        else
                        {
                            videoCodec = codecsSpan.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                                ? "av01.0.05M.08"
                                : codecsSpan.Length > 0 ? codecsSpan.ToString() : null;
                        }
                    }
                }
            }
        }

        private static void ParseCipherStringInto(
            string cipher,
            out string? signature,
            out string? signatureParameter,
            out string? url)
        {
            signature = null;
            signatureParameter = null;
            url = null;

            if (string.IsNullOrEmpty(cipher)) return;

            var span = cipher.AsSpan();
            int start = 0;

            while (start < span.Length)
            {
                int ampIdx = span[start..].IndexOf('&');
                var pair = ampIdx < 0 ? span[start..] : span.Slice(start, ampIdx);

                int eqIdx = pair.IndexOf('=');
                if (eqIdx >= 0)
                {
                    var keySpan = pair[..eqIdx];
                    var valSpan = pair[(eqIdx + 1)..];

                    if (keySpan.SequenceEqual("s".AsSpan()))
                        signature = Uri.UnescapeDataString(valSpan.ToString());
                    else if (keySpan.SequenceEqual("sp".AsSpan()))
                        signatureParameter = Uri.UnescapeDataString(valSpan.ToString());
                    else if (keySpan.SequenceEqual("url".AsSpan()))
                        url = Uri.UnescapeDataString(valSpan.ToString());
                }

                if (ampIdx < 0) break;
                start += ampIdx + 1;
            }
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