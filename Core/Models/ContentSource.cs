namespace LMP.Core.Models;

/// <summary>
/// Источник контента для приложения.
/// </summary>
public enum ContentSource
{
    /// <summary>
    /// YouTube Music API (WEB_REMIX) — песни, альбомы, музыкальные плейлисты.
    /// Рекомендуется для музыки.
    /// </summary>
    YouTubeMusic,
    
    /// <summary>
    /// Стандартный YouTube API (WEB) — все видео.
    /// Для расширенного поиска.
    /// </summary>
    YouTube,
    
    /// <summary>
    /// Локальные файлы на диске.
    /// Полностью оффлайн, не зависит от интернета.
    /// </summary>
    Local
}

/// <summary>
/// Режим синхронизации лайков.
/// </summary>
public enum LikeSyncMode
{
    /// <summary>
    /// YouTube Music Likes (LM) — только музыка.
    /// </summary>
    MusicOnly,
    
    /// <summary>
    /// YouTube Liked Videos (LL) — все видео.
    /// </summary>
    AllVideos,
    
    /// <summary>
    /// Без синхронизации — только локальные лайки.
    /// </summary>
    LocalOnly
}