using LMP.Core.Youtube.Utils;

namespace LMP.Core.Services;

/// <summary>
/// Координирует операции между локальной БД и YouTube.
/// Single Responsibility: оркестрация sync-операций.
/// </summary>
public class MusicLibraryManager : ObservableObject
{
    private readonly LibraryService _library;
    private readonly YoutubeUserDataService _ytUser;
    private readonly YoutubeProvider _youtube;
    private readonly CookieAuthService _auth;

    public MusicLibraryManager(
        LibraryService library,
        YoutubeUserDataService ytUser,
        YoutubeProvider youtube,
        CookieAuthService auth)
    {
        _library = library;
        _ytUser = ytUser;
        _youtube = youtube;
        _auth = auth;
    }

    #region Likes

    /// <summary>
    /// Переключает состояние лайка с синхронизацией YouTube.
    /// </summary>
    /// <remarks>
    /// <para><b>Исправление double-toggle:</b></para>
    /// <para>Раньше: определяли newStatus, отправляли в YouTube, затем вызывали ToggleLikeAsync
    /// который делал ещё один toggle → состояние не менялось.</para>
    /// <para>Теперь: используем SetLikeStateAsync с явным целевым состоянием.</para>
    /// </remarks>
    public async Task ToggleLikeAsync(TrackInfo track, CancellationToken ct = default)
    {
        // Получаем канонический объект из registry для консистентного состояния
        var canonical = _library.GetTrack(track.Id) ?? track;

        // Определяем ЦЕЛЕВОЕ состояние ДО любых операций
        bool targetLikedState = !canonical.IsLiked;

        if (_auth.IsAuthenticated)
        {
            try
            {
                // Сначала синхронизируем с YouTube
                await _youtube.LikeTrackAsync(track.Id, targetLikedState);
                Log.Info($"[Sync] Track {track.Id} liked={targetLikedState} synced to YouTube");
            }
            catch (Exception ex)
            {
                Log.Error($"[Sync] Failed to sync like to YouTube: {ex.Message}");
                // Продолжаем с локальным обновлением даже при ошибке YouTube
            }
        }

        // Устанавливаем КОНКРЕТНОЕ состояние (не toggle!) — избегаем double-toggle
        await _library.SetLikeStateAsync(track, targetLikedState, ct);
    }

    public async Task SyncLikedTracksAsync(CancellationToken ct = default)
    {
        if (!_auth.IsAuthenticated)
        {
            Log.Info("[Sync] Not authenticated. Skipping liked videos sync.");
            return;
        }

        try
        {
            Log.Info("[Sync] Starting liked videos sync from YouTube...");

            var likedTracks = await _ytUser.GetLikedTracksAsync();
            if (likedTracks.Count == 0) return;

            var localLikedTrackIds = await _library.GetPlaylistTrackIdsAsync(
                LibraryService.LikedPlaylistId, ct);
            var existingIds = new HashSet<string>(localLikedTrackIds, StringComparer.Ordinal);

            var newTracks = new List<TrackInfo>();

            for (int i = 0; i < likedTracks.Count; i++)
            {
                if (ct.IsCancellationRequested) break;

                var track = likedTracks[i];
                track.IsLiked = true;

                if (existingIds.Add(track.Id))
                {
                    newTracks.Add(track);
                }
            }

            if (newTracks.Count > 0)
            {
                await _library.AddTracksToPlaylistAsync(
                    newTracks, LibraryService.LikedPlaylistId, ct);
            }

            Log.Info(newTracks.Count > 0
                ? $"[Sync] Added {newTracks.Count} new liked tracks."
                : "[Sync] No new liked tracks found.");
        }
        catch (Exception ex)
        {
            Log.Error($"[Sync] Liked tracks sync failed: {ex.Message}");
        }
    }

    #endregion

    #region Playlist CRUD

    /// <summary>
    /// Создаёт плейлист локально и опционально в YouTube.
    /// </summary>
    public async Task<Playlist> CreatePlaylistAsync(
      string name,
      PlaylistSyncMode mode,
      IReadOnlyList<string>? initialTrackIds = null,
      CancellationToken ct = default)
    {
        var newPlaylist = await _library.CreatePlaylistAsync(name, ct);
        newPlaylist.SyncMode = mode;

        if (mode == PlaylistSyncMode.TwoWaySync && _auth.IsAuthenticated)
        {
            try
            {
                List<string>? rawVideoIds = null;
                if (initialTrackIds is { Count: > 0 })
                {
                    rawVideoIds = new List<string>(initialTrackIds.Count);
                    for (int i = 0; i < initialTrackIds.Count; i++)
                    {
                        var id = initialTrackIds[i];
                        if (id.StartsWith("yt_", StringComparison.Ordinal))
                        {
                            rawVideoIds.Add(YoutubeIdHelper.ExtractRawId(id));
                        }
                    }
                    if (rawVideoIds.Count == 0) rawVideoIds = null;
                }

                var ytId = await _youtube.CreatePlaylistAsync(name, rawVideoIds);
                newPlaylist.YoutubeId = ytId;
            }
            catch (Exception ex)
            {
                Log.Error($"[Sync] Failed to create remote playlist: {ex.Message}");
                newPlaylist.SyncMode = PlaylistSyncMode.LocalOnly;
            }
        }

        await _library.AddOrUpdatePlaylistAsync(newPlaylist, ct);
        return newPlaylist;
    }

    public async Task DeletePlaylistAsync(
        string playlistId, CancellationToken ct = default)
    {
        var playlist = await _library.GetPlaylistAsync(playlistId, ct);
        if (playlist == null) return;

        // Удаляем локально
        await _library.DeletePlaylistAsync(playlistId, ct);

        // Удаляем из YouTube если синхронизирован
        if (playlist.SyncMode == PlaylistSyncMode.TwoWaySync
            && !string.IsNullOrEmpty(playlist.YoutubeId)
            && _auth.IsAuthenticated)
        {
            try
            {
                await _youtube.DeletePlaylistAsync(playlist.YoutubeId);
            }
            catch (Exception ex)
            {
                Log.Error($"[Sync] Error deleting remote playlist: {ex.Message}");
            }
        }
    }

    #endregion

    #region Playlist Operations

    public async Task UploadPlaylistToAccountAsync(
  string localPlaylistId, CancellationToken ct = default)
    {
        if (!_auth.IsAuthenticated) return;

        var localPl = await _library.GetPlaylistAsync(localPlaylistId, ct);
        if (localPl == null || localPl.SyncMode != PlaylistSyncMode.LocalOnly) return;

        try
        {
            var trackIds = await _library.GetPlaylistTrackIdsAsync(localPlaylistId, ct);

            var rawVideoIds = new List<string>(trackIds.Count);
            var localTrackIds = new List<string>(trackIds.Count);
            for (int i = 0; i < trackIds.Count; i++)
            {
                var id = trackIds[i];
                if (id.StartsWith("yt_", StringComparison.Ordinal))
                {
                    rawVideoIds.Add(YoutubeIdHelper.ExtractRawId(id));
                    localTrackIds.Add(id);
                }
            }

            var ytId = await _youtube.CreatePlaylistAsync(
                localPl.Name, rawVideoIds.Count > 0 ? rawVideoIds : null);

            if (string.IsNullOrEmpty(ytId))
                throw new InvalidOperationException("YouTube returned empty playlist ID.");

            localPl.YoutubeId = ytId;
            localPl.SyncMode = PlaylistSyncMode.TwoWaySync;
            await _library.AddOrUpdatePlaylistAsync(localPl, ct);

            if (rawVideoIds.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(1000);

                        var fullData = await _youtube.GetFullPlaylistDataAsync(ytId);
                        if (fullData?.Tracks is { Count: > 0 } tracks)
                        {
                            var mappings = new List<(string TrackId, string SetVideoId)>(tracks.Count);
                            for (int i = 0; i < tracks.Count; i++)
                                mappings.Add(("yt_" + tracks[i].VideoId, tracks[i].SetVideoId));

                            await _library.UpdateSetVideoIdsAsync(localPlaylistId, mappings);
                            Log.Info($"[Sync] Persisted {mappings.Count} setVideoIds for uploaded playlist {ytId}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[Sync] Failed to fetch setVideoIds after upload: {ex.Message}");
                    }
                }, CancellationToken.None);
            }

            Log.Info($"[Sync] Uploaded playlist '{localPl.Name}' " +
                     $"with {rawVideoIds.Count} tracks to {ytId}");
        }
        catch (Exception ex)
        {
            Log.Error($"Upload failed: {ex.Message}");
        }
    }

    public async Task AddTrackToPlaylistAsync(
    string playlistId, TrackInfo track, CancellationToken ct = default)
    {
        var playlist = await _library.GetPlaylistAsync(playlistId, ct);
        if (playlist == null) return;

        // Mutation guard
        if (!playlist.CanEditTracks)
        {
            Log.Warn($"[Manager] Cannot add track to read-only playlist '{playlistId}' " +
                     $"(Ownership={playlist.Ownership})");
            return;
        }

        await _library.AddOrUpdateTrackAsync(track, ct);

        bool alreadyInPlaylist = await _library.IsTrackInPlaylistAsync(track.Id, playlistId, ct);
        if (!alreadyInPlaylist)
            await _library.AddTrackToPlaylistAsync(track, playlistId, ct);

        if (playlist.SyncMode == PlaylistSyncMode.TwoWaySync
            && !string.IsNullOrEmpty(playlist.YoutubeId)
            && _auth.IsAuthenticated)
        {
            try
            {
                var setVideoId = await _youtube.AddToPlaylistAsync(playlist.YoutubeId, track.Id);
                if (!string.IsNullOrEmpty(setVideoId))
                    await _library.UpdateSetVideoIdAsync(playlistId, track.Id, setVideoId, ct);
            }
            catch (Exception ex)
            {
                Log.Error($"[Sync] Add track to cloud failed: {ex.Message}");
            }
        }
    }

    public async Task RemoveTrackFromPlaylistAsync(
     string playlistId, string trackId, CancellationToken ct = default)
    {
        var playlist = await _library.GetPlaylistAsync(playlistId, ct);

        // Mutation guard
        if (playlist is { CanEditTracks: false })
        {
            Log.Warn($"[Manager] Cannot remove track from read-only playlist '{playlistId}' " +
                     $"(Ownership={playlist.Ownership})");
            return;
        }

        string? setVideoId = null;
        bool needsYoutubeSync = playlist != null
            && playlist.CanSyncToCloud
            && !string.IsNullOrEmpty(playlist.YoutubeId)
            && _auth.IsAuthenticated;

        if (needsYoutubeSync)
        {
            setVideoId = await _library.GetSetVideoIdAsync(playlistId, trackId, ct);

            if (string.IsNullOrEmpty(setVideoId))
            {
                Log.Info($"[Sync] No cached setVideoId for {trackId}, fetching from YouTube...");
                try
                {
                    var fullData = await _youtube.GetFullPlaylistDataAsync(
                        playlist!.YoutubeId!, ct);

                    if (fullData?.Tracks is { Count: > 0 } tracks)
                    {
                        var mappings = new List<(string TrackId, string SetVideoId)>(tracks.Count);
                        string? targetSetVideoId = null;

                        for (int i = 0; i < tracks.Count; i++)
                        {
                            var item = tracks[i];
                            var localTrackId = "yt_" + item.VideoId;
                            mappings.Add((localTrackId, item.SetVideoId));

                            if (string.Equals(localTrackId, trackId, StringComparison.Ordinal)
                                || string.Equals(item.VideoId, trackId, StringComparison.Ordinal))
                            {
                                targetSetVideoId = item.SetVideoId;
                            }
                        }

                        await _library.UpdateSetVideoIdsAsync(playlistId, mappings, ct);
                        setVideoId = targetSetVideoId;

                        Log.Info($"[Sync] Fetched {tracks.Count} setVideoIds, " +
                                 $"target: {setVideoId ?? "not found"}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"[Sync] Failed to fetch setVideoIds: {ex.Message}");
                }
            }
        }

        await _library.RemoveTrackFromPlaylistAsync(trackId, playlistId, ct);

        if (needsYoutubeSync)
        {
            if (!string.IsNullOrEmpty(setVideoId))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _youtube.RemoveFromPlaylistAsync(playlist!.YoutubeId!, setVideoId);
                        Log.Info($"[Sync] Removed track {trackId} from YouTube playlist {playlist.YoutubeId}");
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[Sync] Failed to remove track from cloud: {ex.Message}");
                    }
                }, ct);
            }
            else
            {
                Log.Warn($"[Sync] No setVideoId for track {trackId} in playlist {playlistId} " +
                         $"— YouTube removal skipped");
            }
        }
    }

    public async Task MovePlaylistTrackAsync(
        string playlistId, int oldIndex, int newIndex, CancellationToken ct = default)
    {
        await _library.MoveTrackInPlaylistAsync(playlistId, oldIndex, newIndex, ct);
    }

    public async Task ConvertToLocalAsync(
        string playlistId, CancellationToken ct = default)
    {
        var pl = await _library.GetPlaylistAsync(playlistId, ct);
        if (pl == null) return;

        var trackIds = await _library.GetPlaylistTrackIdsAsync(playlistId, ct);

        var copy = new Playlist
        {
            Name = pl.Name + " (Local)",
            SyncMode = PlaylistSyncMode.LocalOnly,
            TrackIds = trackIds,
            ThumbnailUrl = pl.ThumbnailUrl,
            CustomColor = pl.CustomColor,
            Author = "Me"
        };

        await _library.AddOrUpdatePlaylistAsync(copy, ct);
    }

    public async Task<bool> MergePlaylistsAsync(
        string sourceId, string targetId, CancellationToken ct = default)
    {
        var source = await _library.GetPlaylistAsync(sourceId, ct);
        var target = await _library.GetPlaylistAsync(targetId, ct);
        if (source == null || target == null || !target.IsLocal) return false;

        var sourceTrackIds = await _library.GetPlaylistTrackIdsAsync(sourceId, ct);
        var targetTrackIds = await _library.GetPlaylistTrackIdsAsync(targetId, ct);
        var existing = new HashSet<string>(targetTrackIds, StringComparer.Ordinal);
        int added = 0;

        for (int i = 0; i < sourceTrackIds.Count; i++)
        {
            var trackId = sourceTrackIds[i];
            if (existing.Contains(trackId)) continue;

            var track = await _library.GetTrackAsync(trackId, ct);
            if (track != null)
            {
                await _library.AddTrackToPlaylistAsync(track, targetId, ct);
                added++;
            }
        }

        Log.Info($"[Merge] Added {added} tracks from '{source.Name}' to '{target.Name}'");
        return true;
    }

    #endregion
}