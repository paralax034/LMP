using System.Net;
using System.Text.RegularExpressions;

namespace LMP.Core.Youtube.Videos;

/// <summary>
/// Represents a syntactically valid YouTube video ID.
/// </summary>
public readonly partial struct VideoId(string value)
{
    /// <summary>
    /// Raw ID value.
    /// </summary>
    public string Value { get; } = value;

    /// <inheritdoc />
    public override string ToString() => Value;
}

public partial struct VideoId
{
    private static bool IsValid(string videoId)
    {
        if (videoId.Length != 11) return false;
        foreach (var c in videoId.AsSpan())
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')) return false;
        return true;
    }


    private static readonly Regex[] UrlRegexes =
    [
        WatchUrlRegex(),
        ShortWatchUrlRegex(),
        ShortUrlRegex(),
        EmbedUrlRegex(),
        LegacyVUrlRegex(),
        ShortsUrlRegex(),
        LiveUrlRegex()
    ];

    private static string? TryNormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        for (int i = 0; i < UrlRegexes.Length; i++)
        {
            var match = UrlRegexes[i].Match(url);
            if (!match.Success) continue;

            var id = WebUtility.UrlDecode(match.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(id) && IsValid(id)) return id;
        }

        return null;
    }

    private static string? TryNormalize(string? videoIdOrUrl)
    {
        if (string.IsNullOrWhiteSpace(videoIdOrUrl)) return null;

        if (IsValid(videoIdOrUrl)) return videoIdOrUrl;

        return TryNormalizeUrl(videoIdOrUrl);
    }

    /// <summary>
    /// Attempts to extract a video ID strictly from a YouTube URL (rejects bare IDs and search terms).
    /// </summary>
    public static VideoId? TryParseUrl(string? url) =>
        TryNormalizeUrl(url) is { } id ? new VideoId(id) : (VideoId?)null;

    /// <summary>
    /// Attempts to parse the specified string as a video ID or URL.
    /// Returns null in case of failure.
    /// </summary>
    public static VideoId? TryParse(string? videoIdOrUrl) =>
        TryNormalize(videoIdOrUrl) is { } id ? new VideoId(id) : (VideoId?)null;

    /// <summary>
    /// Parses the specified string as a YouTube video ID or URL.
    /// Throws an exception in case of failure.
    /// </summary>
    public static VideoId Parse(string videoIdOrUrl) =>
        TryParse(videoIdOrUrl)
        ?? throw new ArgumentException($"Invalid YouTube video ID or URL '{videoIdOrUrl}'.");

    /// <summary>
    /// Converts string to ID.
    /// </summary>
    public static implicit operator VideoId(string videoIdOrUrl) => Parse(videoIdOrUrl);

    /// <summary>
    /// Converts ID to string.
    /// </summary>
    public static implicit operator string(VideoId videoId) => videoId.ToString();

    [GeneratedRegex(@"youtube\..+?/watch[^#]*?[?&]v=([^&#/]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WatchUrlRegex();
    [GeneratedRegex(@"youtu\.be/watch[^#]*?[?&]v=([^&#/]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShortWatchUrlRegex();
    [GeneratedRegex(@"youtu\.be/([^?&#/]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShortUrlRegex();
    [GeneratedRegex(@"youtube\..+?/embed/([^?&#/]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmbedUrlRegex();
    [GeneratedRegex(@"youtube\..+?/v/([^?&#/]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegacyVUrlRegex();
    [GeneratedRegex(@"youtube\..+?/shorts/([^?&#/]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShortsUrlRegex();
    [GeneratedRegex(@"youtube\..+?/live/([^?&#/]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LiveUrlRegex();
}

public partial struct VideoId : IEquatable<VideoId>
{
    /// <inheritdoc />
    public bool Equals(VideoId other) =>
        string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is VideoId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);

    /// <summary>
    /// Equality check.
    /// </summary>
    public static bool operator ==(VideoId left, VideoId right) => left.Equals(right);

    /// <summary>
    /// Equality check.
    /// </summary>
    public static bool operator !=(VideoId left, VideoId right) => !(left == right);
}
