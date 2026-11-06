namespace LMP.Core.Models;

/// <summary>
/// Application content source.
/// </summary>
public enum ContentSource
{
    /// <summary>
    /// YouTube Music API (WEB_REMIX) — tracks, albums, music playlists.
    /// Recommended for music.
    /// </summary>
    YouTubeMusic,
    
    /// <summary>
    /// Standard YouTube API (WEB) — all videos.
    /// Used for advanced search.
    /// </summary>
    YouTube,
    
    /// <summary>
    /// Local files on disk.
    /// Fully offline, independent of internet connection.
    /// </summary>
    Local
}

/// <summary>
/// Like synchronization mode.
/// </summary>
public enum LikeSyncMode
{
    /// <summary>
    /// YouTube Music Likes (LM) — music only.
    /// </summary>
    MusicOnly,
    
    /// <summary>
    /// YouTube Liked Videos (LL) — all videos.
    /// </summary>
    AllVideos,
    
    /// <summary>
    /// No synchronization — local likes only.
    /// </summary>
    LocalOnly
}