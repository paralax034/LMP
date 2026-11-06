using System.Text.Json.Serialization;
using LMP.Core.Youtube.Search;

namespace LMP.Core.Models;

/// <summary>
/// Playlist storage and synchronization mode.
/// Responsible exclusively for sync behavior, not ownership.
/// </summary>
public enum PlaylistSyncMode
{
    LocalOnly,
    TwoWaySync,
    CloudPublic
}

/// <summary>
/// Playlist ownership from YouTube's perspective.
/// Determines whether the current user can edit the content.
/// </summary>
public enum PlaylistOwnership
{
    /// <summary>Owner data is missing (legacy records, fallback).</summary>
    Unknown = 0,

    /// <summary>Playlist belongs to the currently authenticated user.</summary>
    Mine = 1,

    /// <summary>Playlist belongs to another user.</summary>
    Foreign = 2,

    /// <summary>System playlist (Liked, Watch Later).</summary>
    System = 3
}

/// <summary>
/// Playlist privacy level on YouTube.
/// </summary>
public enum PlaylistVisibility
{
    /// <summary>Visibility data is missing.</summary>
    Unknown = 0,

    /// <summary>Accessible only to the owner.</summary>
    Private = 1,

    /// <summary>Accessible via direct link.</summary>
    Unlisted = 2,

    /// <summary>Accessible to everyone.</summary>
    Public = 3
}

/// <summary>
/// Playlist domain model.
/// Stores real information about the playlist; permissions and restrictions are calculated from it.
/// </summary>
public sealed class Playlist : IBatchItem, ISearchResult
{
    #region Identity

    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("name")]
    public string StoredName { get; set; } = "New Playlist";

    [JsonIgnore]
    public string Name
    {
        get
        {
            if (Id == LibraryService.LikedPlaylistId)
                return LocalizationService.Instance["Playlist_Liked"];
            return StoredName;
        }
        set => StoredName = value;
    }

    [JsonIgnore]
    public string Title => Name;

    [JsonIgnore]
    public string Url => YoutubeId != null
        ? $"https://www.youtube.com/playlist?list={YoutubeId}"
        : string.Empty;

    #endregion

    #region Appearance

    public string? ThumbnailUrl { get; set; }
    public string? CustomColor { get; set; }

    /// <summary>
    /// Automatically calculated dominant color from the cover art.
    /// Format: #RRGGBB. Recalculated when cover art changes.
    /// </summary>
    public string? ComputedColor { get; set; }

    /// <summary>
    /// Effective color for the header gradient.
    /// Priority: CustomColor → ComputedColor → null.
    /// </summary>
    [JsonIgnore]
    public string? EffectiveColor => CustomColor ?? ComputedColor;

    #endregion

    #region Author & Ownership

    /// <summary>
    /// Display name of the playlist author/owner.
    /// </summary>
    public string? Author { get; set; }

    /// <summary>
    /// Playlist description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// YouTube Channel ID of the playlist owner.
    /// Stable identifier for determining ownership and building the channel URL.
    /// </summary>
    public string? OwnerChannelId { get; set; }

    /// <summary>
    /// Who owns the playlist: current user, another user, or system.
    /// Determined during import by comparing <see cref="OwnerChannelId"/>
    /// with the channel identifier of the current account.
    /// </summary>
    public PlaylistOwnership Ownership { get; set; } = PlaylistOwnership.Unknown;

    /// <summary>
    /// Playlist access level on YouTube: public, unlisted, private.
    /// Populated from the YouTube API payload if data is available.
    /// </summary>
    public PlaylistVisibility Visibility { get; set; } = PlaylistVisibility.Unknown;

    #endregion

    #region Sync & Cloud

    /// <summary>
    /// Playlist view count according to YouTube Music data.
    /// Populated during synchronization (PlaylistSyncService).
    /// </summary>
    public long? ViewCount { get; set; }

    /// <summary>
    /// Playlist creation/update date according to YouTube data.
    /// </summary>
    public DateOnly? ReleaseDate { get; set; }

    /// <summary>
    /// Synchronization mode. Defines sync behavior only, not ownership.
    /// </summary>
    public PlaylistSyncMode SyncMode { get; set; } = PlaylistSyncMode.LocalOnly;

    /// <summary>
    /// YouTube playlist identifier for cloud operations.
    /// </summary>
    public string? YoutubeId { get; set; }

    /// <summary>
    /// ETag for optimistic change verification.
    /// </summary>
    public string? ETag { get; set; }

    /// <summary>
    /// Track count according to YouTube API data.
    /// Allows displaying the count before the full track list is loaded.
    /// </summary>
    public int? CloudTrackCount { get; set; }

    /// <summary>
    /// UTC timestamp of the last successful synchronization with YouTube.
    /// </summary>
    public DateTime? LastSyncedAtUtc { get; set; }

    /// <summary>
    /// Playlist is unavailable in the cloud (deleted, private, 403/404 during sync).
    /// </summary>
    public bool IsCloudUnavailable { get; set; }

    #endregion

    #region Local Ownership (account isolation)

    /// <summary>
    /// Identifier of the local record owner for cross-account isolation.
    /// Do not confuse with <see cref="OwnerChannelId"/> (YouTube owner).
    /// </summary>
    public string OwnerId { get; set; } = string.Empty;

    #endregion

    #region Sync Mode Queries (backward-compatible)

    /// <summary>
    /// Playlist has a local representation (LocalOnly or TwoWaySync).
    /// </summary>
    [JsonIgnore]
    public bool IsLocal => SyncMode is PlaylistSyncMode.LocalOnly or PlaylistSyncMode.TwoWaySync;

    /// <summary>
    /// Playlist synchronizes with a YouTube Music account.
    /// </summary>
    [JsonIgnore]
    public bool IsFromAccount => SyncMode == PlaylistSyncMode.TwoWaySync;

    #endregion

    #region Ownership Queries

    /// <summary>
    /// Playlist belongs to the currently authenticated user.
    /// </summary>
    [JsonIgnore]
    public bool IsMine => Ownership == PlaylistOwnership.Mine;

    /// <summary>
    /// Playlist belongs to another user.
    /// </summary>
    [JsonIgnore]
    public bool IsForeign => Ownership == PlaylistOwnership.Foreign;

    /// <summary>
    /// System playlist (Liked, Watch Later).
    /// </summary>
    [JsonIgnore]
    public bool IsSystem => Ownership == PlaylistOwnership.System;

    #endregion

    #region Cloud Link Queries

    /// <summary>
    /// Playlist has an active YouTube binding (ID exists and is not marked as unavailable).
    /// </summary>
    [JsonIgnore]
    public bool HasCloudLink => !string.IsNullOrEmpty(YoutubeId) && !IsCloudUnavailable;

    /// <summary>
    /// Current user can synchronize changes back to YouTube.
    /// </summary>
    [JsonIgnore]
    public bool CanSyncToCloud => (IsMine || Ownership == PlaylistOwnership.Unknown) && HasCloudLink && SyncMode == PlaylistSyncMode.TwoWaySync;

    /// <summary>
    /// The playlist is bound to the cloud for outgoing mutations, including pending creation (YoutubeId is not yet acquired).
    /// Edits to such a playlist must go into the Outbox; otherwise, they are lost before Phase 0 completes.
    /// </summary>
    [JsonIgnore]
    public bool IsCloudBound => (IsMine || Ownership == PlaylistOwnership.Unknown) && SyncMode == PlaylistSyncMode.TwoWaySync && !IsCloudUnavailable;

    /// <summary>
    /// Fresh data can be fetched from YouTube.
    /// </summary>
    [JsonIgnore]
    public bool CanPullFromCloud => !string.IsNullOrEmpty(YoutubeId);

    #endregion

    #region Permissions

    /// <summary>
    /// The playlist is read-only (belongs to another user or is CloudPublic).
    /// <para><b>Backward-compatible:</b> returns <c>false</c> for <see cref="PlaylistOwnership.Unknown"/>,
    /// preserving behavior for legacy records.</para>
    /// </summary>
    [JsonIgnore]
    public bool IsReadOnly => IsForeign || SyncMode == PlaylistSyncMode.CloudPublic;

    /// <summary>
    /// The playlist can be edited (not read-only and not system).
    /// Replaces the legacy <c>SyncMode != CloudPublic</c> logic
    /// by adding an ownership check.
    /// </summary>
    [JsonIgnore]
    public bool IsEditable => !IsReadOnly && !IsSystem;

    /// <summary>
    /// Name, description, and cover image can be modified.
    /// </summary>
    [JsonIgnore]
    public bool CanEditMetadata => IsEditable;

    /// <summary>
    /// Tracks can be added, removed, or reordered.
    /// </summary>
    [JsonIgnore]
    public bool CanEditTracks => IsEditable;

    #endregion

    #region Author URL

    /// <summary>
    /// Direct link to the playlist author's channel.
    /// <c>null</c> if <see cref="OwnerChannelId"/> is unknown.
    /// </summary>
    [JsonIgnore]
    public string? AuthorUrl => !string.IsNullOrEmpty(OwnerChannelId)
        ? $"https://www.youtube.com/channel/{OwnerChannelId}"
        : null;

    #endregion

    #region Timestamps

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    #endregion

    #region Runtime (not persisted)

    [JsonIgnore]
    public List<string> TrackIds { get; set; } = [];

    [JsonIgnore]
    public int TrackCount { get; set; }

    #endregion
}