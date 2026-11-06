namespace LMP.Core.Data.Repositories;

/// <summary>
/// Repository interface for managing playlists and their relationships with music tracks.
/// Provides strict account-level isolation of playlists.
/// </summary>
public interface IPlaylistRepository
{
    /// <summary>
    /// Gets a playlist by its unique identifier with automatic virtualization support for the "Liked" system playlist.
    /// </summary>
    /// <param name="id">Unique playlist identifier.</param>
    /// <param name="ownerId">Active owner account identifier.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>The <see cref="Playlist"/> model, or <c>null</c> if not found.</returns>
    Task<Playlist?> GetByIdAsync(string id, string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Returns a list of all playlists for the current user (including the virtual "Liked" playlist).
    /// </summary>
    /// <param name="ownerId">Active owner account identifier.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>A list of available playlists.</returns>
    Task<List<Playlist>> GetAllAsync(string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Gets an ordered list of track identifiers contained in the specified playlist.
    /// </summary>
    /// <param name="playlistId">Playlist identifier.</param>
    /// <param name="ownerId">Active owner account identifier.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>A list of track identifiers.</returns>
    Task<List<string>> GetTrackIdsAsync(string playlistId, string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Returns the total number of tracks in a specific playlist.
    /// </summary>
    /// <param name="playlistId">Playlist identifier.</param>
    /// <param name="ownerId">Active owner account identifier.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>The number of tracks in the playlist.</returns>
    Task<int> GetTrackCountAsync(string playlistId, string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Adds or updates playlist metadata in the database.
    /// </summary>
    /// <param name="playlist">Playlist model to save.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    Task UpsertAsync(Playlist playlist, CancellationToken ct = default);

    /// <summary>
    /// Point-updates the cloud binding of a playlist without overwriting other columns.
    /// Does not resurrect a deleted row or overwrite concurrent name/description edits.
    /// </summary>
    /// <param name="playlistId">Local playlist identifier.</param>
    /// <param name="youtubeId">New cloud identifier, or <see langword="null"/> to keep the current one.</param>
    /// <param name="lastSyncedAtUtc">Successful sync timestamp, or <see langword="null"/> to keep the current one.</param>
    /// <param name="isCloudUnavailable">Flag indicating cloud copy unavailability.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns><see langword="true"/> if the row exists, is in TwoWaySync mode, and is not bound to a different YoutubeId.</returns>
    Task<bool> UpdateCloudLinkStateAsync(
        string playlistId,
        string? youtubeId,
        DateTime? lastSyncedAtUtc,
        bool isCloudUnavailable,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes a playlist by its unique identifier.
    /// </summary>
    /// <param name="id">Identifier of the playlist to delete.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    Task DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Renames a local playlist.
    /// </summary>
    /// <param name="id">Playlist identifier.</param>
    /// <param name="newName">New display name.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    Task RenameAsync(string id, string newName, CancellationToken ct = default);

    /// <summary>
    /// Adds a track to a playlist at a specified or automatic (end) position.
    /// </summary>
    /// <param name="playlistId">Playlist identifier.</param>
    /// <param name="trackId">Identifier of the track to add.</param>
    /// <param name="ownerId">Active owner account identifier.</param>
    /// <param name="position">Ordinal index in the playlist. If omitted, the track is appended to the end.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    Task AddTrackAsync(string playlistId, string trackId, string ownerId, int? position = null, CancellationToken ct = default);

    /// <summary>
    /// Batch-adds a collection of tracks to a playlist within a single database transaction.
    /// </summary>
    /// <param name="playlistId">Playlist identifier.</param>
    /// <param name="trackIds">Collection of track identifiers.</param>
    /// <param name="ownerId">Active owner account identifier.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>The number of successfully linked records.</returns>
    Task<int> AddTracksAsync(string playlistId, IEnumerable<string> trackIds, string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Safely removes a track from a playlist with automatic shift of remaining track positions.
    /// </summary>
    /// <param name="playlistId">Playlist identifier.</param>
    /// <param name="trackId">Identifier of the track to remove.</param>
    /// <param name="ownerId">Active owner account identifier.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    Task RemoveTrackAsync(string playlistId, string trackId, string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Batch-removes a collection of tracks from a playlist within a single database transaction.
    /// </summary>
    /// <param name="playlistId">Playlist identifier.</param>
    /// <param name="trackIds">Collection of track identifiers to remove.</param>
    /// <param name="ownerId">Active owner account identifier.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>The number of successfully deleted records.</returns>
    Task<int> RemoveTracksAsync(string playlistId, IEnumerable<string> trackIds, string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Reorders tracks within a playlist.
    /// </summary>
    /// <param name="playlistId">Playlist identifier.</param>
    /// <param name="oldIndex">Current track index.</param>
    /// <param name="newIndex">Target track index.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    Task MoveTrackAsync(string playlistId, int oldIndex, int newIndex, CancellationToken ct = default);

    /// <summary>
    /// Rebinds orphaned playlists (with empty or guest <c>OwnerId</c>)
    /// to the specified authenticated owner.
    /// <para>
    /// Called from <see cref="LibraryService"/> on every account switch to an authenticated user.
    /// The operation is idempotent: performs a no-op if no orphans exist.
    /// </para>
    /// </summary>
    /// <param name="newOwnerId">Identifier of the new owner (GaiaId of the authenticated account).</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>The number of rebound playlists.</returns>
    Task<int> AdoptOrphanPlaylistsAsync(string newOwnerId, CancellationToken ct = default);

    /// <summary>
    /// Checks whether a specific track is included in the specified playlist.
    /// </summary>
    /// <param name="playlistId">Playlist identifier.</param>
    /// <param name="trackId">Track identifier.</param>
    /// <param name="ownerId">Active owner account identifier.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns><c>true</c> if the track is linked to the playlist; otherwise, <c>false</c>.</returns>
    Task<bool> ContainsTrackAsync(string playlistId, string trackId, string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Returns a list of all playlists of the current user that contain the specified track.
    /// </summary>
    /// <param name="trackId">Identifier of the track to check.</param>
    /// <param name="ownerId">Active owner account identifier.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>A set of playlist identifiers.</returns>
    Task<HashSet<string>> GetPlaylistsForTrackAsync(string trackId, string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Batch-retrieves playlist memberships for a set of tracks in a single query.
    /// </summary>
    /// <param name="trackIds">Collection of track identifiers.</param>
    /// <param name="ownerId">Active owner account identifier.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>A dictionary where the key is the track ID and the value is a set of playlist IDs.</returns>
    Task<Dictionary<string, HashSet<string>>> GetPlaylistsForTracksAsync(IEnumerable<string> trackIds, string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Returns all user playlists along with their track counts.
    /// </summary>
    /// <param name="ownerId">Active owner account identifier.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>A list of tuples containing the playlist model and its total track count.</returns>
    Task<List<(Playlist Playlist, int TrackCount)>> GetAllWithCountsAsync(string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Returns the total duration of all tracks in a specific playlist in Ticks.
    /// </summary>
    /// <param name="playlistId">Playlist identifier.</param>
    /// <param name="ownerId">Active owner account identifier.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>Total playback duration in Ticks.</returns>
    Task<long> GetTotalDurationTicksAsync(string playlistId, string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Returns the total duration of all unique tracks in the user's entire library.
    /// Resolves the N+1 query problem when updating sidebar statistics.
    /// </summary>
    /// <param name="ownerId">Active owner account identifier.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>Total library duration in Ticks.</returns>
    Task<long> GetTotalLibraryDurationAsync(string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Returns the YouTube relation identifier (setVideoId) for a track in a cloud playlist.
    /// Required for correct track deletion from cloud playlists via the YouTube Music API.
    /// </summary>
    /// <param name="playlistId">Playlist identifier.</param>
    /// <param name="trackId">Track identifier.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>Unique relation setVideoId, or <c>null</c> if it does not exist.</returns>
    Task<string?> GetSetVideoIdAsync(string playlistId, string trackId, CancellationToken ct = default);

    /// <summary>
    /// Updates the YouTube relation identifier (setVideoId) for a track in a cloud playlist.
    /// </summary>
    /// <param name="playlistId">Playlist identifier.</param>
    /// <param name="trackId">Track identifier.</param>
    /// <param name="setVideoId">New setVideoId received from YouTube API.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    Task UpdateSetVideoIdAsync(string playlistId, string trackId, string setVideoId, CancellationToken ct = default);

    /// <summary>
    /// Batch updates setVideoId mappings for a group of tracks in a playlist.
    /// </summary>
    /// <param name="playlistId">Playlist identifier.</param>
    /// <param name="mappings">Collection of Track ID -> setVideoId pairs.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    Task UpdateSetVideoIdsAsync(string playlistId, IReadOnlyList<(string TrackId, string SetVideoId)> mappings, CancellationToken ct = default);

    /// <summary>
    /// Retrieves playlist track IDs supporting SQL pagination (LIMIT/OFFSET).
    /// </summary>
    /// <param name="playlistId">Playlist identifier.</param>
    /// <param name="ownerId">Active owner account identifier.</param>
    /// <param name="limit">Maximum number of IDs to return.</param>
    /// <param name="offset">Offset from the beginning of the result set.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>A page of playlist track IDs.</returns>
    Task<List<string>> GetTrackIdsAsync(string playlistId, string ownerId, int limit, int offset = 0, CancellationToken ct = default);

    /// <summary>
    /// Batch fetches track IDs for all user playlists in a single SQL query.
    /// Eliminates the N+1 query problem when initializing the reverse relation index.
    /// </summary>
    /// <param name="ownerId">Active owner account identifier.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>A dictionary where the key is the playlist ID and the value is the set of contained track IDs.</returns>
    Task<Dictionary<string, HashSet<string>>> GetAllPlaylistTrackIdsAsync(string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Batch retrieves stored <c>SetVideoId</c> mapping identifiers for the specified set of playlist tracks.
    /// Must be called before deleting rows from <c>PlaylistTracks</c> to prevent losing <c>SetVideoId</c>.
    /// </summary>
    /// <param name="playlistId">Local playlist identifier.</param>
    /// <param name="trackIds">List of track identifiers.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A dictionary of <c>TrackId -&gt; SetVideoId</c> mappings.</returns>
    Task<Dictionary<string, string?>> GetSetVideoIdsAsync(
        string playlistId,
        IReadOnlyList<string> trackIds,
        CancellationToken ct = default);

    /// <summary>
    /// Returns a position-ordered list of <c>(TrackId, SetVideoId)</c> pairs for all tracks in a playlist.
    /// </summary>
    /// <param name="playlistId">Local playlist identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An ordered list of tracks with their cloud mappings.</returns>
    Task<List<(string TrackId, string? SetVideoId)>> GetTracksWithSetVideoIdsAsync(
        string playlistId,
        CancellationToken ct = default);
}