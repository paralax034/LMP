namespace LMP.Core.Models;

/// <summary>
/// Content search source.
/// </summary>
public enum SearchSource
{
    /// <summary>
    /// Standard YouTube (videos, all content types).
    /// </summary>
    YouTube,

    /// <summary>
    /// YouTube Music (songs, albums, music content).
    /// </summary>
    YouTubeMusic,

    /// <summary>
    /// Playlists only.
    /// </summary>
    Playlists
}

public static class SearchSourceExtensions
{
    /// <summary>
    /// Cache key.
    /// </summary>
    public static string ToCacheKey(this SearchSource source) => source switch
    {
        SearchSource.YouTube => "yt",
        SearchSource.YouTubeMusic => "ytm",
        SearchSource.Playlists => "pl",
        _ => "yt"
    };

    /// <summary>
    /// YouTube Music context (WEB_REMIX)?
    /// </summary>
    public static bool IsMusicContext(this SearchSource source) =>
        source == SearchSource.YouTubeMusic;
}