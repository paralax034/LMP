using LMP.Core.Youtube.Search;

namespace LMP.Core.Models;

/// <summary>
/// Источник поиска контента.
/// </summary>
public enum SearchSource
{
    /// <summary>
    /// Стандартный YouTube (видео, все типы контента).
    /// </summary>
    YouTube,

    /// <summary>
    /// YouTube Music (песни, альбомы, музыкальный контент).
    /// </summary>
    YouTubeMusic,

    /// <summary>
    /// Только плейлисты.
    /// </summary>
    Playlists
}

public static class SearchSourceExtensions
{
    /// <summary>
    /// Ключ для кэша.
    /// </summary>
    public static string ToCacheKey(this SearchSource source) => source switch
    {
        SearchSource.YouTube => "yt",
        SearchSource.YouTubeMusic => "ytm",
        SearchSource.Playlists => "pl",
        _ => "yt"
    };

    /// <summary>
    /// Контекст YouTube Music (WEB_REMIX)?
    /// </summary>
    public static bool IsMusicContext(this SearchSource source) =>
        source == SearchSource.YouTubeMusic;
}