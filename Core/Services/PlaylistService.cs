using LMP.Core.Data.Repositories;

namespace LMP.Core.Services;

/// <summary>
/// Unified centralized service for managing the playlist lifecycle.
/// Serves as the Single Source of Truth for CRUD operations, track bindings, and two-way cloud synchronization.
/// </summary>
public sealed class PlaylistService
{
    private readonly IPlaylistRepository _playlists;
    private readonly ITrackRepository _tracks;
    private readonly TrackRegistry _registry;
    private readonly CookieAuthService _auth;
    private readonly PlaylistSyncService _syncService;

    private readonly Dictionary<string, HashSet<string>> _playlistTrackIndex = new(StringComparer.Ordinal);
    private readonly Lock _indexLock = new();
    private readonly SemaphoreSlim _indexInitLock = new(1, 1);
    private volatile bool _isIndexInitialized;

    public event Action<Playlist>? OnPlaylistChanged;
    public event Action<string>? OnPlaylistRemoved;

    private string CurrentOwnerId => _auth.State.DisplayId;

    private static Playlist CreateLikedPlaylist(string ownerId, int trackCount) => new()
    {
        Id = LibraryService.LikedPlaylistId,
        StoredName = "Liked",
        SyncMode = PlaylistSyncMode.LocalOnly,
        Ownership = PlaylistOwnership.System,
        TrackCount = trackCount,
        OwnerChannelId = ownerId,
        OwnerId = ownerId
    };

    public PlaylistService(
        IPlaylistRepository playlists,
        ITrackRepository tracks,
        TrackRegistry registry,
        CookieAuthService auth,
        PlaylistSyncService syncService)
    {
        ArgumentNullException.ThrowIfNull(playlists);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(syncService);

        _playlists = playlists;
        _tracks = tracks;
        _registry = registry;
        _auth = auth;
        _syncService = syncService;

        _auth.OnAuthStateChanged += InvalidateIndex;
        _syncService.OnCloudPlaylistStateChanged += playlist => OnPlaylistChanged?.Invoke(playlist);
    }

    #region Read API

    public async Task<Playlist?> GetPlaylistAsync(string id, CancellationToken ct = default)
    {
        if (id == LibraryService.LikedPlaylistId)
        {
            int count = await _tracks.CountLikedAsync(CurrentOwnerId, ct).ConfigureAwait(false);
            return CreateLikedPlaylist(CurrentOwnerId, count);
        }

        return await _playlists.GetByIdAsync(id, CurrentOwnerId, ct).ConfigureAwait(false);
    }

    public async Task<(Playlist Playlist, int TrackCount)?> GetPlaylistWithCountAsync(string playlistId, CancellationToken ct = default)
    {
        if (playlistId == LibraryService.LikedPlaylistId)
        {
            var pl = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
            if (pl == null) return null;
            return (pl, pl.TrackCount);
        }

        var playlist = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
        if (playlist == null) return null;

        var count = await _playlists.GetTrackCountAsync(playlistId, CurrentOwnerId, ct).ConfigureAwait(false);
        return (playlist, count);
    }

    public async Task<List<(Playlist Playlist, int TrackCount)>> GetAllPlaylistsWithCountsAsync(CancellationToken ct = default)
    {
        var localList = await _playlists.GetAllWithCountsAsync(CurrentOwnerId, ct).ConfigureAwait(false);
        int likedCount = await _tracks.CountLikedAsync(CurrentOwnerId, ct).ConfigureAwait(false);

        var likedPlaylist = CreateLikedPlaylist(CurrentOwnerId, likedCount);

        var result = new List<(Playlist Playlist, int TrackCount)>(localList.Count + 1)
        {
            (likedPlaylist, likedCount)
        };
        result.AddRange(localList);

        return result;
    }

    public async Task<List<Playlist>> GetAllPlaylistsAsync(CancellationToken ct = default)
    {
        var withCounts = await GetAllPlaylistsWithCountsAsync(ct).ConfigureAwait(false);
        var result = new List<Playlist>(withCounts.Count);
        for (int i = 0; i < withCounts.Count; i++)
        {
            var pl = withCounts[i].Playlist;
            pl.TrackCount = withCounts[i].TrackCount;
            result.Add(pl);
        }
        return result;
    }

    public async Task<List<string>> GetPlaylistTrackIdsAsync(string playlistId, CancellationToken ct = default)
    {
        if (playlistId == LibraryService.LikedPlaylistId)
        {
            return await _tracks.GetLikedTrackIdsAsync(CurrentOwnerId, 10000, 0, ct).ConfigureAwait(false);
        }

        return await _playlists.GetTrackIdsAsync(playlistId, CurrentOwnerId, ct).ConfigureAwait(false);
    }

    public Task<List<TrackInfo>> GetPlaylistTracksAsync(string playlistId, CancellationToken ct = default) =>
            GetPlaylistTracksAsync(playlistId, limit: 10000, offset: 0, ct);

    public async Task<List<TrackInfo>> GetPlaylistTracksAsync(string playlistId, int limit, int offset = 0, CancellationToken ct = default)
    {
        if (playlistId == LibraryService.LikedPlaylistId)
        {
            var liked = await _tracks.GetLikedAsync(CurrentOwnerId, limit, offset, ct).ConfigureAwait(false);
            for (int i = 0; i < liked.Count; i++)
            {
                var canonical = _registry.RegisterOrUpdate(liked[i]);
                canonical.IsLiked = true;
                liked[i] = canonical;
            }
            return liked;
        }

        var trackIds = await _playlists.GetTrackIdsAsync(playlistId, CurrentOwnerId, limit, offset, ct).ConfigureAwait(false);
        if (trackIds.Count == 0) return [];

        return await _registry.PreloadAndReturnAsync(trackIds, ct).ConfigureAwait(false);
    }

    public async Task<TimeSpan> GetPlaylistTotalDurationAsync(string playlistId, CancellationToken ct = default)
    {
        if (playlistId == LibraryService.LikedPlaylistId)
        {
            var ticks = await _tracks.GetLikedTotalDurationTicksAsync(CurrentOwnerId, ct).ConfigureAwait(false);
            return TimeSpan.FromTicks(ticks);
        }

        var ticksTotal = await _playlists.GetTotalDurationTicksAsync(playlistId, CurrentOwnerId, ct).ConfigureAwait(false);
        return TimeSpan.FromTicks(ticksTotal);
    }

    public async Task<List<Playlist>> GetEditablePlaylistsAsync(CancellationToken ct = default)
    {
        var all = await GetAllPlaylistsAsync(ct).ConfigureAwait(false);
        var editable = new List<Playlist>(all.Count);
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            if (p.Id != LibraryService.LikedPlaylistId && p.CanEditTracks)
                editable.Add(p);
        }
        return editable;
    }

    #endregion

    #region In-Memory Reverse Index & Membership Status

    public async Task EnsureIndexInitializedAsync(CancellationToken ct = default)
    {
        if (_isIndexInitialized) return;

        await _indexInitLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_isIndexInitialized) return;

            var ownerId = CurrentOwnerId;
            var playlists = await _playlists.GetAllAsync(ownerId, ct).ConfigureAwait(false);
            var batchTrackIdsMap = await _playlists.GetAllPlaylistTrackIdsAsync(ownerId, ct).ConfigureAwait(false);

            lock (_indexLock)
            {
                if (!string.Equals(CurrentOwnerId, ownerId, StringComparison.Ordinal))
                    return;

                for (int i = 0; i < playlists.Count; i++)
                {
                    var p = playlists[i];
                    if (!p.IsEditable) continue;

                    if (!batchTrackIdsMap.TryGetValue(p.Id, out var loadedTrackIds))
                    {
                        loadedTrackIds = new HashSet<string>(StringComparer.Ordinal);
                    }

                    if (_playlistTrackIndex.TryGetValue(p.Id, out var existingSet))
                    {
                        existingSet.UnionWith(loadedTrackIds);
                    }
                    else
                    {
                        _playlistTrackIndex[p.Id] = loadedTrackIds;
                    }
                }

                _isIndexInitialized = true;
            }
        }
        finally
        {
            _indexInitLock.Release();
        }
    }

    public void InvalidateIndex()
    {
        lock (_indexLock)
        {
            _playlistTrackIndex.Clear();
            _isIndexInitialized = false;
        }
    }

    public (PlaylistMembershipState State, int IncludedCount, int TotalCount) GetMembershipStatus(
        string playlistId,
        IReadOnlyList<TrackInfo> targets)
    {
        if (targets.Count == 0)
            return (PlaylistMembershipState.None, 0, 0);

        HashSet<string>? trackIds;
        lock (_indexLock)
        {
            _playlistTrackIndex.TryGetValue(playlistId, out trackIds);
        }

        if (trackIds == null || trackIds.Count == 0)
            return (PlaylistMembershipState.None, 0, targets.Count);

        int matchCount = 0;
        for (int i = 0; i < targets.Count; i++)
        {
            if (trackIds.Contains(targets[i].Id))
                matchCount++;
        }

        if (matchCount == 0)
            return (PlaylistMembershipState.None, 0, targets.Count);

        if (matchCount == targets.Count)
            return (PlaylistMembershipState.All, matchCount, targets.Count);

        return (PlaylistMembershipState.Indeterminate, matchCount, targets.Count);
    }

    public async Task<PlaylistMembershipState> ToggleTracksMembershipAsync(
        string playlistId,
        IReadOnlyList<TrackInfo> targets,
        CancellationToken ct = default)
    {
        if (targets.Count == 0)
            return PlaylistMembershipState.None;

        await EnsureIndexInitializedAsync(ct).ConfigureAwait(false);

        var (state, _, _) = GetMembershipStatus(playlistId, targets);

        if (state == PlaylistMembershipState.All)
        {
            await RemoveTracksFromPlaylistAsync(playlistId, targets, ct).ConfigureAwait(false);
            return PlaylistMembershipState.None;
        }
        else
        {
            HashSet<string>? currentIds;
            lock (_indexLock)
            {
                _playlistTrackIndex.TryGetValue(playlistId, out currentIds);
            }

            var toAdd = new List<TrackInfo>(targets.Count);
            for (int i = 0; i < targets.Count; i++)
            {
                if (currentIds == null || !currentIds.Contains(targets[i].Id))
                    toAdd.Add(targets[i]);
            }

            if (toAdd.Count > 0)
            {
                await AddTracksToPlaylistAsync(playlistId, toAdd, ct).ConfigureAwait(false);
            }

            return PlaylistMembershipState.All;
        }
    }

    #endregion

    #region Mutation CRUD API

    /// <summary>
    /// Creates a new local playlist with the current user's permissions and full metadata.
    /// </summary>
    public async Task<Playlist> CreatePlaylistAsync(
        string name,
        string? description = null,
        string? thumbnailUrl = null,
        string? customColor = null,
        string? computedColor = null,
        CancellationToken ct = default)
    {
        var playlist = new Playlist
        {
            Name = name,
            Description = description,
            ThumbnailUrl = thumbnailUrl,
            CustomColor = customColor,
            ComputedColor = computedColor,
            SyncMode = PlaylistSyncMode.LocalOnly,
            Ownership = PlaylistOwnership.Mine,
            OwnerId = CurrentOwnerId,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };

        await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);

        lock (_indexLock)
        {
            _playlistTrackIndex[playlist.Id] = new HashSet<string>(StringComparer.Ordinal);
        }

        OnPlaylistChanged?.Invoke(playlist);
        return playlist;
    }

    public async Task<Playlist> CreateCopyAsync(string sourcePlaylistId, string copyName, string? description, string? customColor, string? computedColor, string? thumbnailUrl, CancellationToken ct = default)
    {
        var originalTrackIds = await GetPlaylistTrackIdsAsync(sourcePlaylistId, ct).ConfigureAwait(false);

        var copy = new Playlist
        {
            Name = copyName,
            ThumbnailUrl = thumbnailUrl,
            CustomColor = customColor,
            Description = description,
            ComputedColor = computedColor,
            SyncMode = PlaylistSyncMode.LocalOnly,
            YoutubeId = null,
            TrackIds = [.. originalTrackIds],
            OwnerId = CurrentOwnerId,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };

        await AddOrUpdatePlaylistAsync(copy, ct).ConfigureAwait(false);
        return copy;
    }

    /// <summary>
    /// Creates a remote playlist counterpart on YouTube Music and switches it to two-way synchronization mode.
    /// </summary>
    public async Task<bool> LinkToCloudAsync(string playlistId, CancellationToken ct = default)
    {
        try
        {
            var playlist = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
            if (playlist == null) return false;

            bool linked = await _syncService.LinkToCloudAsync(playlist, ct).ConfigureAwait(false);
            if (linked)
                OnPlaylistChanged?.Invoke(playlist);

            return linked;
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistService] LinkToCloudAsync failed for '{playlistId}': {ex.Message}");
            return false;
        }
    }

    public async Task UnlinkFromCloudAsync(string playlistId, CancellationToken ct = default)
    {
        var playlist = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
        if (playlist == null) return;

        _syncService.DiscardPlaylistOutbox(playlistId);

        playlist.SyncMode = PlaylistSyncMode.LocalOnly;
        playlist.YoutubeId = null;
        playlist.UpdatedAt = DateTime.Now;

        await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);
        OnPlaylistChanged?.Invoke(playlist);
    }

    public async Task AddOrUpdatePlaylistAsync(Playlist playlist, CancellationToken ct = default)
    {
        playlist.OwnerId = CurrentOwnerId;
        await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);

        if (playlist.TrackIds.Count > 0)
        {
            var existingTrackIds = await _playlists.GetTrackIdsAsync(playlist.Id, CurrentOwnerId, ct).ConfigureAwait(false);
            var existingSet = new HashSet<string>(existingTrackIds, StringComparer.Ordinal);

            var newTrackIds = new List<string>(playlist.TrackIds.Count);
            for (int i = 0; i < playlist.TrackIds.Count; i++)
            {
                var id = playlist.TrackIds[i];
                if (existingSet.Add(id))
                    newTrackIds.Add(id);
            }

            if (newTrackIds.Count > 0)
            {
                await _playlists.AddTracksAsync(playlist.Id, newTrackIds, CurrentOwnerId, ct).ConfigureAwait(false);
            }

            lock (_indexLock)
            {
                _playlistTrackIndex[playlist.Id] = existingSet;
            }
        }
        else
        {
            lock (_indexLock)
            {
                if (!_playlistTrackIndex.ContainsKey(playlist.Id))
                    _playlistTrackIndex[playlist.Id] = new HashSet<string>(StringComparer.Ordinal);
            }
        }

        OnPlaylistChanged?.Invoke(playlist);
    }

    public async Task RenamePlaylistAsync(string playlistId, string newName, CancellationToken ct = default)
    {
        if (playlistId == LibraryService.LikedPlaylistId) return;

        var existing = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
        if (existing == null) return;

        var baselineName = existing.Name;
        await _playlists.RenameAsync(playlistId, newName, ct).ConfigureAwait(false);

        var updated = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
        if (updated != null)
        {
            if (updated.IsCloudBound)
            {
                _syncService.EnqueueMetadataUpdate(updated, newName: newName, baselineName: baselineName);
            }

            OnPlaylistChanged?.Invoke(updated);
        }
    }

    /// <summary>
    /// Enqueues cloud playlist metadata updates (name, description, cover) into the background Outbox queue using a Last-Write-Wins strategy.
    /// </summary>
    public void EnqueueCloudMetadataUpdate(
        Playlist playlist,
        string? newName = null,
        string? baselineName = null,
        string? newDescription = null,
        string? baselineDescription = null,
        string? newThumbnailUrl = null,
        bool thumbnailChanged = false)
    {
        _syncService.EnqueueMetadataUpdate(
            playlist,
            newName,
            baselineName,
            newDescription,
            baselineDescription,
            newThumbnailUrl,
            thumbnailChanged);
    }

    /// <summary>
    /// Deletes a playlist locally and, optionally, from the YouTube Music cloud account.
    /// </summary>
    /// <param name="playlistId">The identifier of the playlist to delete.</param>
    /// <param name="deleteFromCloud">Flag indicating whether to delete the playlist from YouTube Music cloud.</param>
    /// <param name="ct">Cancellation token to cancel the asynchronous operation.</param>
    /// <returns>A task representing the asynchronous delete operation.</returns>
    public async Task DeletePlaylistAsync(string playlistId, bool deleteFromCloud = false, CancellationToken ct = default)
    {
        if (playlistId == LibraryService.LikedPlaylistId) return;

        var playlist = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
        if (playlist == null) return;

        foreach (var track in _registry.GetPinnedTracks())
            track.InPlaylists.Remove(playlistId);

        lock (_indexLock)
        {
            _playlistTrackIndex.Remove(playlistId);
        }

        await _playlists.DeleteAsync(playlistId, ct).ConfigureAwait(false);

        if (deleteFromCloud && playlist.SyncMode == PlaylistSyncMode.TwoWaySync)
        {
            _syncService.EnqueueDeletePlaylist(playlist);
        }
        else
        {
            _syncService.DiscardPlaylistOutbox(playlistId);
        }

        OnPlaylistRemoved?.Invoke(playlistId);
    }

    /// <summary>
    /// Adds a track to the local playlist with immediate mutation synchronization to the cloud.
    /// </summary>
    public Task AddTrackToPlaylistAsync(string playlistId, TrackInfo track, CancellationToken ct = default) =>
        AddTracksToPlaylistAsync(playlistId, [track], ct);

    /// <summary>
    /// Batch-adds a set of tracks to the local playlist followed by cloud mutation synchronization.
    /// </summary>
    public async Task AddTracksToPlaylistAsync(string playlistId, IReadOnlyList<TrackInfo> tracks, CancellationToken ct = default)
    {
        if (tracks.Count == 0) return;

        if (playlistId == LibraryService.LikedPlaylistId)
        {
            for (int i = 0; i < tracks.Count; i++)
            {
                var canonical = _registry.RegisterOrUpdate(tracks[i]);
                bool wasLiked = canonical.IsLiked;
                await _tracks.UpsertAsync(canonical, ct).ConfigureAwait(false);
                await _tracks.SetLikedAsync(canonical.Id, CurrentOwnerId, true, ct: ct).ConfigureAwait(false);
                canonical.SetLikedState(true);
                canonical.InPlaylists.Add(LibraryService.LikedPlaylistId);
                _registry.UpdatePinStatus(canonical);

                _syncService.EnqueueLikeMutation(canonical.Id, targetLiked: true, baselineLiked: wasLiked);
            }

            var likedPlaylist = await GetPlaylistAsync(LibraryService.LikedPlaylistId, ct).ConfigureAwait(false);
            if (likedPlaylist != null)
                OnPlaylistChanged?.Invoke(likedPlaylist);
            return;
        }

        var playlist = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
        if (playlist == null || !playlist.CanEditTracks) return;

        var trackIds = new List<string>(tracks.Count);
        for (int i = 0; i < tracks.Count; i++)
        {
            var canonical = _registry.RegisterOrUpdate(tracks[i]);
            await _tracks.UpsertAsync(canonical, ct).ConfigureAwait(false);
            trackIds.Add(canonical.Id);
            canonical.InPlaylists.Add(playlistId);
            _registry.UpdatePinStatus(canonical);
        }

        lock (_indexLock)
        {
            if (!_playlistTrackIndex.TryGetValue(playlistId, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                _playlistTrackIndex[playlistId] = set;
            }
            for (int i = 0; i < trackIds.Count; i++)
                set.Add(trackIds[i]);
        }

        await _playlists.AddTracksAsync(playlistId, trackIds, CurrentOwnerId, ct).ConfigureAwait(false);

        // IsCloudBound instead of CanSyncToCloud: edits to a playlist pending creation must not be lost
        if (playlist.IsCloudBound)
        {
            _syncService.EnqueueAddTracks(playlist, trackIds);
        }

        OnPlaylistChanged?.Invoke(playlist);
    }

    /// <summary>
    /// Removes a track from the local playlist and enqueues the removal (with preserved SetVideoId) into the background Outbox queue.
    /// </summary>
    /// <param name="playlistId">The playlist identifier.</param>
    /// <param name="trackId">The identifier of the track to remove.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>A task representing the asynchronous delete operation.</returns>
    public Task RemoveTrackFromPlaylistAsync(string playlistId, string trackId, CancellationToken ct = default)
    {
        var track = _registry.TryGet(trackId) ?? new TrackInfo { Id = trackId };
        return RemoveTracksFromPlaylistAsync(playlistId, [track], ct);
    }

    /// <summary>
    /// Batch-removes a collection of tracks from the local playlist and non-blockingly enqueues the removal into the cloud Outbox queue.
    /// Extracts preserved <c>SetVideoId</c> values before deleting entries from the <c>PlaylistTracks</c> table.
    /// </summary>
    public async Task RemoveTracksFromPlaylistAsync(string playlistId, IReadOnlyList<TrackInfo> tracks, CancellationToken ct = default)
    {
        if (tracks.Count == 0) return;

        if (playlistId == LibraryService.LikedPlaylistId)
        {
            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i];
                var trackInfo = _registry.TryGet(t.Id) ?? t;
                bool wasLiked = trackInfo.IsLiked;

                await _tracks.SetLikedAsync(t.Id, CurrentOwnerId, false, ct: ct).ConfigureAwait(false);
                trackInfo.SetLikedState(false);
                trackInfo.InPlaylists.Remove(LibraryService.LikedPlaylistId);
                _registry.UpdatePinStatus(trackInfo);

                _syncService.EnqueueLikeMutation(trackInfo.Id, targetLiked: false, baselineLiked: wasLiked);
            }

            var likedPlaylist = await GetPlaylistAsync(LibraryService.LikedPlaylistId, ct).ConfigureAwait(false);
            if (likedPlaylist != null)
                OnPlaylistChanged?.Invoke(likedPlaylist);

            return;
        }

        var playlist = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
        if (playlist == null || !playlist.CanEditTracks) return;

        var trackIds = new List<string>(tracks.Count);
        for (int i = 0; i < tracks.Count; i++)
        {
            var id = tracks[i].Id;
            trackIds.Add(id);

            var track = _registry.TryGet(id) ?? tracks[i];
            track.InPlaylists.Remove(playlistId);
            _registry.UpdatePinStatus(track);
        }

        lock (_indexLock)
        {
            if (_playlistTrackIndex.TryGetValue(playlistId, out var set))
            {
                for (int i = 0; i < trackIds.Count; i++)
                    set.Remove(trackIds[i]);
            }
        }

        // Extract SetVideoId from the DB BEFORE deleting rows from PlaylistTracks
        bool isCloudBound = playlist.IsCloudBound;
        Dictionary<string, string?>? existingSetVideoIds = null;
        if (isCloudBound)
        {
            existingSetVideoIds = await _playlists.GetSetVideoIdsAsync(playlistId, trackIds, ct).ConfigureAwait(false);
        }

        await _playlists.RemoveTracksAsync(playlistId, trackIds, CurrentOwnerId, ct).ConfigureAwait(false);

        if (isCloudBound)
        {
            var removedEntries = new List<(string TrackId, string? SetVideoId)>(trackIds.Count);
            for (int i = 0; i < trackIds.Count; i++)
            {
                var tId = trackIds[i];
                string? svId = null;
                existingSetVideoIds?.TryGetValue(tId, out svId);
                removedEntries.Add((tId, svId));
            }

            _syncService.EnqueueRemoveTracks(playlist, removedEntries);
        }

        OnPlaylistChanged?.Invoke(playlist);
    }

    /// <summary>
    /// Changes a track index in the local playlist and enqueues the compacted reorder mutation into the cloud Outbox queue.
    /// </summary>
    public async Task MovePlaylistTrackAsync(string playlistId, int oldIndex, int newIndex, CancellationToken ct = default)
    {
        if (playlistId == LibraryService.LikedPlaylistId || oldIndex == newIndex) return;

        var playlist = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
        if (playlist == null || !playlist.CanEditTracks) return;

        var trackIds = await _playlists.GetTrackIdsAsync(playlistId, CurrentOwnerId, ct).ConfigureAwait(false);

        if (oldIndex < 0 || oldIndex >= trackIds.Count || newIndex < 0 || newIndex >= trackIds.Count)
            return;

        var baselineOrder = new List<string>(trackIds);
        var movingTrackId = trackIds[oldIndex];
        await _playlists.MoveTrackAsync(playlistId, oldIndex, newIndex, ct).ConfigureAwait(false);

        trackIds.RemoveAt(oldIndex);
        trackIds.Insert(newIndex, movingTrackId);

        if (playlist.IsCloudBound)
        {
            _syncService.EnqueueReorderTracks(playlist, baselineOrder, trackIds);
        }
        OnPlaylistChanged?.Invoke(playlist);
    }

    #endregion

    #region Delegated Cloud Operations

    /// <summary>
    /// Forwards the diff snapshot generation request to the synchronization service.
    /// </summary>
    public async Task<PlaylistSyncPreview?> BuildPreviewAsync(string playlistId, CancellationToken ct = default)
    {
        var playlist = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
        return playlist == null ? null : await _syncService.BuildPreviewAsync(playlist, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Forwards the sync settings application request to the synchronization service.
    /// </summary>
    public async Task<PlaylistSyncResult> ApplySyncAsync(
        string playlistId,
        PlaylistSyncPreview preview,
        PlaylistSyncOptions options,
        CancellationToken ct = default)
    {
        var playlist = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
        if (playlist == null) return PlaylistSyncResult.Fail("Playlist not found");

        var result = await _syncService.ApplySyncAsync(playlist, preview, options, ct).ConfigureAwait(false);
        if (result.Success)
        {
            InvalidateIndex();
            var updated = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false) ?? playlist;
            OnPlaylistChanged?.Invoke(updated);
        }

        return result;
    }

    /// <summary>
    /// Delegates direct playlist synchronization to the synchronization service.
    /// </summary>
    public async Task<PlaylistSyncResult> SyncDirectAsync(
        string playlistId,
        PlaylistSyncOptions options,
        CancellationToken ct = default)
    {
        var playlist = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
        if (playlist == null) return PlaylistSyncResult.Fail("Playlist not found");

        var result = await _syncService.SyncDirectAsync(playlist, options, ct).ConfigureAwait(false);
        if (result.Success)
        {
            InvalidateIndex();
            OnPlaylistChanged?.Invoke(playlist);
        }

        return result;
    }

    /// <summary>
    /// Delegates liked tracks synchronization to the synchronization service.
    /// </summary>
    public async Task SyncLikedTracksAsync(CancellationToken ct = default)
    {
        await _syncService.SyncLikedTracksAsync(ct).ConfigureAwait(false);
        var liked = await GetPlaylistAsync(LibraryService.LikedPlaylistId, ct).ConfigureAwait(false);
        if (liked != null)
        {
            OnPlaylistChanged?.Invoke(liked);
        }
    }

    /// <summary>
    /// Delegates playlist upload to the YouTube cloud account.
    /// </summary>
    public async Task UploadPlaylistToAccountAsync(string localPlaylistId, CancellationToken ct = default)
    {
        var playlist = await GetPlaylistAsync(localPlaylistId, ct).ConfigureAwait(false);
        if (playlist != null)
        {
            await _syncService.UploadPlaylistToAccountAsync(playlist, ct).ConfigureAwait(false);
            OnPlaylistChanged?.Invoke(playlist);
        }
    }

    /// <summary>
    /// Converts a remote playlist to local format, resetting cloud parameters.
    /// </summary>
    public async Task ConvertToLocalAsync(string playlistId, CancellationToken ct = default)
    {
        var pl = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
        if (pl == null) return;

        var trackIds = await GetPlaylistTrackIdsAsync(playlistId, ct).ConfigureAwait(false);

        var copy = new Playlist
        {
            Name = pl.Name + " (Local)",
            SyncMode = PlaylistSyncMode.LocalOnly,
            TrackIds = trackIds,
            ThumbnailUrl = pl.ThumbnailUrl,
            CustomColor = pl.CustomColor,
            ComputedColor = pl.ComputedColor,
            Author = "Me",
            OwnerId = CurrentOwnerId
        };

        await AddOrUpdatePlaylistAsync(copy, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Merges tracks from two local playlists.
    /// </summary>
    public async Task<bool> MergePlaylistsAsync(string sourceId, string targetId, CancellationToken ct = default)
    {
        var source = await GetPlaylistAsync(sourceId, ct).ConfigureAwait(false);
        var target = await GetPlaylistAsync(targetId, ct).ConfigureAwait(false);
        if (source == null || target == null || !target.IsLocal) return false;

        var sourceTrackIds = await GetPlaylistTrackIdsAsync(sourceId, ct).ConfigureAwait(false);
        var targetTrackIds = await GetPlaylistTrackIdsAsync(targetId, ct).ConfigureAwait(false);
        var existing = new HashSet<string>(targetTrackIds, StringComparer.Ordinal);
        var toAdd = new List<TrackInfo>();

        for (int i = 0; i < sourceTrackIds.Count; i++)
        {
            var id = sourceTrackIds[i];
            if (existing.Add(id))
            {
                var track = await _registry.GetOrLoadAsync(id, ct).ConfigureAwait(false);
                if (track != null) toAdd.Add(track);
            }
        }

        if (toAdd.Count > 0)
        {
            await AddTracksToPlaylistAsync(targetId, toAdd, ct).ConfigureAwait(false);
        }

        return true;
    }

    #endregion
}