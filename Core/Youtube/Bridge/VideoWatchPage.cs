using System.Text.RegularExpressions;
using LMP.Core.Helpers.Extensions;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Youtube.Bridge;

internal partial class VideoWatchPage
{
    private readonly string _rawContent;
    private PlayerResponse? _cachedPlayerResponse;
    private bool _playerResponseParsed;

    public VideoWatchPage(string rawContent)
    {
        _rawContent = rawContent;
    }

    public bool IsAvailable => !_rawContent.Contains("og:url") || _rawContent.Contains("video_id");

    public DateTimeOffset? UploadDate =>
        MyRegex().Match(_rawContent)
            .Groups[1].Value.NullIfWhiteSpace()
            ?.Pipe(s => DateTimeOffset.TryParse(s, out var d) ? d : (DateTimeOffset?)null);

    /// <summary>
    /// Парсинг количества лайков с использованием высокопроизводительного Span-парсера без аллокаций.
    /// </summary>
    public long? LikeCount
    {
        get
        {
            var matchJson = LikeRegex1().Match(_rawContent);
            if (matchJson.Success)
            {
                var val = YoutubeParsingHelpers.ParseLongFromText(matchJson.Groups[1].ValueSpan);
                if (val.HasValue) return val.Value;
            }

            var matchText = LikeRegex2().Match(_rawContent);
            if (matchText.Success)
            {
                var val = YoutubeParsingHelpers.ParseLongFromText(matchText.Groups[1].ValueSpan);
                if (val.HasValue) return val.Value;
            }

            return null;
        }
    }

    public const long DislikeCount = 0;

    /// <summary>
    /// Возвращает разобранный ответ плеера с кэшированием в поле экземпляра для исключения повторного парсинга.
    /// </summary>
    public PlayerResponse? PlayerResponse
    {
        get
        {
            if (_playerResponseParsed)
                return _cachedPlayerResponse;

            _cachedPlayerResponse = ParsePlayerResponseInternal();
            _playerResponseParsed = true;
            return _cachedPlayerResponse;
        }
    }

    private PlayerResponse? ParsePlayerResponseInternal()
    {
        var json = InitialYTRegex().Match(_rawContent).Groups[1].Value;
        if (!string.IsNullOrWhiteSpace(json))
        {
            return PlayerResponse.Parse(json);
        }

        var configJson = PlayerResponseOldRegex().Match(_rawContent).Groups[1].Value;
        if (!string.IsNullOrWhiteSpace(configJson))
        {
            var config = Json.TryParse(configJson);
            var argsResponse = config?.GetPropertyOrNull("args")?.GetPropertyOrNull("player_response")?.GetStringOrNull();
            if (!string.IsNullOrWhiteSpace(argsResponse))
            {
                return PlayerResponse.Parse(argsResponse);
            }
        }

        return null;
    }

    public static VideoWatchPage? TryParse(string raw)
    {
        if (!raw.Contains("ytInitialPlayerResponse") && !raw.Contains("ytplayer.config"))
            return null;

        return new VideoWatchPage(raw);
    }

    [GeneratedRegex(@"ytplayer\.config\s*=\s*(\{.*?\});", RegexOptions.Singleline)]
    private static partial Regex PlayerResponseOldRegex();
    [GeneratedRegex(@"itemprop=""datePublished"" content=""(.*?)(?:"")")]
    private static partial Regex MyRegex();
    [GeneratedRegex(@"""likeCount""\s*:\s*""(\d+)""")]
    private static partial Regex LikeRegex1();
    [GeneratedRegex(@"([\d,\.]+)\s+likes")]
    private static partial Regex LikeRegex2();
    [GeneratedRegex(@"var\s+ytInitialPlayerResponse\s*=\s*(\{.*?\});", RegexOptions.Singleline)]
    private static partial Regex InitialYTRegex();
}