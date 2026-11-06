namespace LMP.Core.Data;

/// <summary>
/// Legacy playlist model for JSON migration (without JsonIgnore on TrackIds).
/// </summary>
public sealed class LegacyPlaylist
{
    public string Id { get; set; } = string.Empty;
    
    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string Name { get; set; } = "New Playlist";
    
    public string? ThumbnailUrl { get; set; }
    public string? Author { get; set; }
    public string? Description { get; set; }
    public int SyncMode { get; set; }
    public string? YoutubeId { get; set; }
    public string? ETag { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
    // NO [JsonIgnore] - will be deserialized!
    public List<string> TrackIds { get; set; } = [];
    
    /// <summary>
    /// Converts to domain Playlist model (without TrackIds - they go to junction table).
    /// </summary>
    public Playlist ToPlaylist() => new()
    {
        Id = Id,
        StoredName = Name,
        ThumbnailUrl = ThumbnailUrl,
        Author = Author,
        Description = Description,
        SyncMode = (PlaylistSyncMode)SyncMode,
        YoutubeId = YoutubeId,
        ETag = ETag,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt
        // ComputedColor = null — will be calculated on first open
    };
}

/// <summary>
/// Legacy model for JSON migration. Matches old LibraryData structure.
/// </summary>
public sealed class LegacyLibraryData
{
    public Dictionary<string, TrackInfo>? Tracks { get; set; }
    public Dictionary<string, LegacyPlaylist>? Playlists { get; set; }
    public List<string>? LikedTrackIds { get; set; }
    public List<string>? RecentlyPlayedIds { get; set; }
    public List<string>? SearchHistory { get; set; }
    public string? FakeAccountChannelUrl { get; set; }
    
    // Audio
    public float Volume { get; set; } = 0.5f;
    public int LastVolume { get; set; } = 50;
    public bool ShuffleEnabled { get; set; }
    public RepeatMode RepeatMode { get; set; } = RepeatMode.None;
    public int MaxVolumeLimit { get; set; } = 100;
    public float TargetGainDb { get; set; } = 0f;
    public AudioQualityPreference QualityPreference { get; set; } = AudioQualityPreference.BestAvailable;
    public bool RememberTrackFormat { get; set; } = true;
    
    // Network
    public InternetProfile InternetProfile { get; set; } = InternetProfile.Medium;
    public ProxySettings Proxy { get; set; } = new();
    public StorageSettings Storage { get; set; } = new();
    
    // UI
    public double PlaylistHeaderHeight { get; set; } = 320;
    public string LanguageCode { get; set; } = "en";
    public string DownloadPath { get; set; } = string.Empty;
    public bool DiscordRpcEnabled { get; set; } = true;
    public bool AutoPlayOnUrlPaste { get; set; } = true;
    public int LoadBatchSize { get; set; } = 20;
    public int SearchBatchSize { get; set; } = 30;
    public bool EnableSearchCache { get; set; } = true;
    public int SearchCacheTtlMinutes { get; set; } = 120;
}