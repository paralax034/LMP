namespace LMP.Core.Data.Repositories;

/// <summary>
/// Repository interface for managing persistent storage of music tracks.
/// Provides user data isolation (likes, history) between accounts.
/// </summary>
public interface ITrackRepository
{
    #region Чтение

    /// <summary>
    /// Gets track metadata by its unique identifier.
    /// </summary>
    /// <param name="id">Unique track identifier.</param>
    /// <param name="ownerId">Owner (account) identifier used to compute the liked state.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>Returns the populated <see cref="TrackInfo"/> model, or <c>null</c> if the track was not found.</returns>
    Task<TrackInfo?> GetByIdAsync(string id, string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Gets a batch of tracks by a list of their identifiers while preserving the original order.
    /// </summary>
    /// <param name="ids">Collection of track identifiers.</param>
    /// <param name="ownerId">Owner (account) identifier used to compute the liked state.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>A list of matching tracks.</returns>
    Task<List<TrackInfo>> GetByIdsAsync(IEnumerable<string> ids, string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Returns the full list of tracks registered in the database.
    /// </summary>
    /// <param name="ownerId">Owner (account) identifier used to compute the liked state.</param>
    /// <param name="limit">Maximum number of records to return (for pagination).</param>
    /// <param name="offset">Pagination offset from the start.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>A list of track models.</returns>
    Task<List<TrackInfo>> GetAllAsync(string ownerId, int limit = 10000, int offset = 0, CancellationToken ct = default);

    /// <summary>
    /// Searches for tracks in the local database by title or artist using the SQL LIKE operator.
    /// </summary>
    /// <param name="query">Search query.</param>
    /// <param name="ownerId">Owner (account) identifier used to compute the liked state.</param>
    /// <param name="limit">Maximum number of results to return.</param>
    /// <param name="offset">Pagination offset from the start.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>A list of tracks matching the search query.</returns>
    Task<List<TrackInfo>> SearchAsync(string query, string ownerId, int limit = 50, int offset = 0, CancellationToken ct = default);

    /// <summary>
    /// Returns a list of liked tracks for the specified account, sorted by date added (newest first).
    /// </summary>
    /// <param name="ownerId">Identifier of the owner (account) whose likes should be retrieved.</param>
    /// <param name="limit">Maximum number of records to return.</param>
    /// <param name="offset">Pagination offset from the start.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>A list of liked tracks.</returns>
    Task<List<TrackInfo>> GetLikedAsync(string ownerId, int limit = 100, int offset = 0, CancellationToken ct = default);

    /// <summary>
    /// Returns an ordered list of liked track identifiers without reading or materializing full models.
    /// </summary>
    /// <param name="ownerId">Owner (account) identifier.</param>
    /// <param name="limit">Maximum number of records to return.</param>
    /// <param name="offset">Pagination offset from the start.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>A list of liked track identifiers.</returns>
    Task<List<string>> GetLikedTrackIdsAsync(string ownerId, int limit = 10000, int offset = 0, CancellationToken ct = default);

    /// <summary>
    /// Returns the total duration of all liked tracks for the specified account in ticks directly from the database.
    /// </summary>
    /// <param name="ownerId">Owner (account) identifier.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>Total playback duration in ticks.</returns>
    Task<long> GetLikedTotalDurationTicksAsync(string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Returns a list of tracks fully downloaded to the device.
    /// </summary>
    /// <param name="ownerId">Owner (account) identifier used to compute the liked state.</param>
    /// <param name="limit">Maximum number of records to return.</param>
    /// <param name="offset">Pagination offset from the start.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>A list of downloaded tracks.</returns>
    Task<List<TrackInfo>> GetDownloadedAsync(string ownerId, int limit = 100, int offset = 0, CancellationToken ct = default);

    /// <summary>
    /// Returns the recently played track history for the specified account.
    /// </summary>
    /// <param name="ownerId">Identifier of the owner (account) whose history should be retrieved.</param>
    /// <param name="limit">Maximum number of history records.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>A list of recently played tracks.</returns>
    Task<List<TrackInfo>> GetRecentlyPlayedAsync(string ownerId, int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// Retrieves local track files (starting with "local_" or having a downloaded status).
    /// </summary>
    /// <param name="ownerId">Owner (account) identifier used to compute the liked state.</param>
    /// <param name="limit">Maximum number of records to return.</param>
    /// <param name="offset">Pagination offset from the start.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>A list of local tracks.</returns>
    Task<List<TrackInfo>> GetLocalTracksAsync(string ownerId, int limit = 1000, int offset = 0, CancellationToken ct = default);

    /// <summary>
    /// Returns the total count of tracks in the local database.
    /// </summary>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>Total record count.</returns>
    Task<int> CountAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the total count of liked tracks for the specified account.
    /// </summary>
    /// <param name="ownerId">Owner (account) identifier.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>Count of liked tracks.</returns>
    Task<int> CountLikedAsync(string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Returns the total count of local tracks.
    /// </summary>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>Count of local records.</returns>
    Task<int> CountLocalAsync(CancellationToken ct = default);

    #endregion

    #region Запись

    /// <summary>
    /// Upserts track metadata in the local database.
    /// </summary>
    /// <param name="track">Track data model.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    Task UpsertAsync(TrackInfo track, CancellationToken ct = default);

    /// <summary>
    /// Batch upserts a collection of tracks within a single database transaction.
    /// </summary>
    /// <param name="tracks">Collection of tracks to save.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    Task UpsertBatchAsync(IEnumerable<TrackInfo> tracks, CancellationToken ct = default);

    /// <summary>
    /// Deletes a track and all its associated relations from the database by its identifier.
    /// </summary>
    /// <param name="id">Identifier of the track to delete.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    Task DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Sets or unsets the liked status for a specific track within the context of the specified account.
    /// </summary>
    /// <param name="id">Unique track identifier.</param>
    /// <param name="ownerId">Owner (account) identifier.</param>
    /// <param name="liked"><c>true</c> if the track is marked as liked; otherwise, <c>false</c>.</param>
    /// <param name="likedAt">Optional timestamp when the track was liked. Defaults to UtcNow if null.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    Task SetLikedAsync(string id, string ownerId, bool liked, DateTime? likedAt = null, CancellationToken ct = default);

    /// <summary>
    /// Batch commits liked status for a set of tracks within a single database transaction.
    /// </summary>
    /// <param name="trackIds">Ordered collection of track identifiers (index 0 is the most recent like).</param>
    /// <param name="ownerId">Owner (account) identifier.</param>
    /// <param name="preserveOrder">Whether to preserve the original order of the collection using monotonically decreasing timestamps.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    Task SetLikedBatchAsync(IReadOnlyList<string> trackIds, string ownerId, bool preserveOrder = true, CancellationToken ct = default);

    /// <summary>
    /// Updates the local download status and physical file path of a track on the device.
    /// </summary>
    /// <param name="id">Track identifier.</param>
    /// <param name="downloaded">Indicates whether the track is downloaded to disk.</param>
    /// <param name="localPath">Full path to the local audio file.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    Task SetDownloadedAsync(string id, bool downloaded, string? localPath, CancellationToken ct = default);

    /// <summary>
    /// Persists track-level integrated loudness and its source.
    /// </summary>
    /// <param name="id">Track identifier.</param>
    /// <param name="integratedLufs">Measured integrated loudness in LUFS.</param>
    /// <param name="source">Source of the <c>IntegratedLufs</c> value.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveNormalizationMetadataAsync(
            string id,
            float integratedLufs,
            int source,
            CancellationToken ct = default);

    /// <summary>
    /// Saves or resets an individual equalizer preset for the specified track.
    /// </summary>
    /// <param name="id">Track identifier.</param>
    /// <param name="presetId">Equalizer preset identifier, or <c>null</c> to reset.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SetEqualizerPresetAsync(
        string id,
        string? presetId,
        CancellationToken ct = default);

    #endregion

    #region История

    /// <summary>
    /// Adds a track to a specific user's playback history, automatically pruning records older than the 100th entry.
    /// </summary>
    /// <param name="trackId">Identifier of the played track.</param>
    /// <param name="ownerId">Owner (account) identifier.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    Task AddToHistoryAsync(string trackId, string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Clears the playback history entirely for the specified account.
    /// </summary>
    /// <param name="ownerId">Owner (account) identifier.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    Task ClearHistoryAsync(string ownerId, CancellationToken ct = default);

    #endregion
}