using System.Net;
using System.Text.RegularExpressions;

namespace LMP.Core.Youtube.Playlists;

/// <summary>
/// Represents a syntactically valid YouTube playlist ID.
/// </summary>
public readonly partial struct PlaylistId(string value)
{
    /// <summary>
    /// Raw ID value.
    /// </summary>
    public string Value { get; } = value;

    /// <inheritdoc />
    public override string ToString() => Value;
}

public partial struct PlaylistId
{
    private static bool IsValid(string playlistId)
    {
        if (playlistId.Length < 2) return false;
        foreach (var c in playlistId.AsSpan())
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')) return false;
        return true;
    }

    private static readonly Regex[] UrlRegexes =
    [
        PlaylistUrlRegex(),
        WatchListUrlRegex(),
        ShortListUrlRegex(),
        EmbedListUrlRegex()
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

    private static string? TryNormalize(string? playlistIdOrUrl)
    {
        if (string.IsNullOrWhiteSpace(playlistIdOrUrl)) return null;

        if (IsValid(playlistIdOrUrl)) return playlistIdOrUrl;

        return TryNormalizeUrl(playlistIdOrUrl);
    }

    /// <summary>
    /// Attempts to extract a playlist ID strictly from a YouTube URL (rejects bare IDs and search terms).
    /// </summary>
    public static PlaylistId? TryParseUrl(string? url) =>
        TryNormalizeUrl(url) is { } id ? new PlaylistId(id) : (PlaylistId?)null;

    /// <summary>
    /// Attempts to parse the specified string as a YouTube playlist ID or URL.
    /// Returns null in case of failure.
    /// </summary>
    public static PlaylistId? TryParse(string? playlistIdOrUrl) =>
        TryNormalize(playlistIdOrUrl) is { } id ? new PlaylistId(id) : (PlaylistId?)null;

    /// <summary>
    /// Parses the specified string as a YouTube playlist ID or URL.
    /// </summary>
    public static PlaylistId Parse(string playlistIdOrUrl) =>
        TryParse(playlistIdOrUrl)
        ?? throw new ArgumentException($"Invalid YouTube playlist ID or URL '{playlistIdOrUrl}'.");

    /// <summary>
    /// Converts string to ID.
    /// </summary>
    public static implicit operator PlaylistId(string playlistIdOrUrl) => Parse(playlistIdOrUrl);

    /// <summary>
    /// Converts ID to string.
    /// </summary>
    public static implicit operator string(PlaylistId playlistId) => playlistId.ToString();

    [GeneratedRegex(@"youtube\..+?/playlist[^#]*?[?&]list=([^&#/]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlaylistUrlRegex();
    [GeneratedRegex(@"youtube\..+?/watch[^#]*?[?&]list=([^&#/]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WatchListUrlRegex();
    [GeneratedRegex(@"youtu\.be/[^#]*?[?&]list=([^&#/]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShortListUrlRegex();
    [GeneratedRegex(@"youtube\..+?/(?:embed|shorts|live)/[^#]*?[?&]list=([^&#/]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmbedListUrlRegex();
}

public partial struct PlaylistId : IEquatable<PlaylistId>
{
    /// <inheritdoc />
    public bool Equals(PlaylistId other) =>
        string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PlaylistId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);

    /// <summary>
    /// Equality check.
    /// </summary>
    public static bool operator ==(PlaylistId left, PlaylistId right) => left.Equals(right);

    /// <summary>
    /// Equality check.
    /// </summary>
    public static bool operator !=(PlaylistId left, PlaylistId right) => !(left == right);
}
