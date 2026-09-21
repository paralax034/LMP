using LMP.Core.Data.Repositories;

namespace LMP.Core.Services;

/// <summary>
/// Единый централизованный сервис управления жизненным циклом плейлистов.
/// Является Single Source of Truth для CRUD-операций, связей треков и двусторонней облачной синхронизации.
/// </summary>
public sealed class PlaylistService
{
    private readonly IPlaylistRepository _playlists;
    private readonly ITrackRepository _tracks;
    private readonly TrackRegistry _registry;
    private readonly CookieAuthService _auth;
    private readonly PlaylistSyncService _syncService;

    public event Action<Playlist>? OnPlaylistChanged;
    public event Action<string>? OnPlaylistRemoved;

    private string CurrentOwnerId => _auth.State.DisplayId;

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
    }

    #region Read API

    public async Task<Playlist?> GetPlaylistAsync(string id, CancellationToken ct = default)
    {
        if (id == LibraryService.LikedPlaylistId)
        {
            int count = await _tracks.CountLikedAsync(CurrentOwnerId, ct).ConfigureAwait(false);
            return new Playlist
            {
                Id = LibraryService.LikedPlaylistId,
                StoredName = "Liked",
                SyncMode = PlaylistSyncMode.LocalOnly,
                Ownership = PlaylistOwnership.System,
                TrackCount = count,
                OwnerId = CurrentOwnerId
            };
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

        var likedPlaylist = new Playlist
        {
            Id = LibraryService.LikedPlaylistId,
            StoredName = "Liked",
            SyncMode = PlaylistSyncMode.LocalOnly,
            Ownership = PlaylistOwnership.System,
            TrackCount = likedCount,
            OwnerChannelId = CurrentOwnerId,
            OwnerId = CurrentOwnerId
        };

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
            var likedTracks = await _tracks.GetLikedAsync(CurrentOwnerId, 10000, 0, ct).ConfigureAwait(false);
            var ids = new List<string>(likedTracks.Count);
            for (int i = 0; i < likedTracks.Count; i++)
                ids.Add(likedTracks[i].Id);
            return ids;
        }

        return await _playlists.GetTrackIdsAsync(playlistId, CurrentOwnerId, ct).ConfigureAwait(false);
    }

    public async Task<List<TrackInfo>> GetPlaylistTracksAsync(string playlistId, CancellationToken ct = default)
    {
        if (playlistId == LibraryService.LikedPlaylistId)
        {
            var liked = await _tracks.GetLikedAsync(CurrentOwnerId, 10000, 0, ct).ConfigureAwait(false);
            for (int i = 0; i < liked.Count; i++)
            {
                var canonical = _registry.RegisterOrUpdate(liked[i]);
                canonical.IsLiked = true;
                liked[i] = canonical;
            }
            return liked;
        }

        var trackIds = await _playlists.GetTrackIdsAsync(playlistId, CurrentOwnerId, ct).ConfigureAwait(false);
        if (trackIds.Count == 0) return [];

        return await _registry.PreloadAndReturnAsync(trackIds, ct).ConfigureAwait(false);
    }

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
            var liked = await _tracks.GetLikedAsync(CurrentOwnerId, 10000, 0, ct).ConfigureAwait(false);
            long sumTicks = 0;
            for (int i = 0; i < liked.Count; i++)
                sumTicks += liked[i].Duration.Ticks;
            return TimeSpan.FromTicks(sumTicks);
        }

        var ticks = await _playlists.GetTotalDurationTicksAsync(playlistId, CurrentOwnerId, ct).ConfigureAwait(false);
        return TimeSpan.FromTicks(ticks);
    }

    #endregion

    #region Mutation CRUD API

    /// <summary>
    /// Создает новый локальный плейлист с правами текущего пользователя.
    /// </summary>
    public async Task<Playlist> CreatePlaylistAsync(string name, CancellationToken ct = default)
    {
        var playlist = new Playlist
        {
            Name = name,
            SyncMode = PlaylistSyncMode.LocalOnly,
            Ownership = PlaylistOwnership.Mine,
            OwnerId = CurrentOwnerId
        };

        await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);
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
    /// Создает удаленный аналог плейлиста на YouTube Music и переводит его в режим двусторонней синхронизации.
    /// </summary>
    public async Task<bool> LinkToCloudAsync(string playlistId, CancellationToken ct = default)
    {
        var playlist = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
        if (playlist == null) return false;

        bool linked = await _syncService.LinkToCloudAsync(playlist, ct).ConfigureAwait(false);
        if (linked)
            OnPlaylistChanged?.Invoke(playlist);

        return linked;
    }

    public async Task UnlinkFromCloudAsync(string playlistId, CancellationToken ct = default)
    {
        var playlist = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
        if (playlist == null) return;

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
        }

        OnPlaylistChanged?.Invoke(playlist);
    }

    public async Task RenamePlaylistAsync(string playlistId, string newName, CancellationToken ct = default)
    {
        if (playlistId == LibraryService.LikedPlaylistId) return;

        await _playlists.RenameAsync(playlistId, newName, ct).ConfigureAwait(false);
        var updated = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
        if (updated != null)
            OnPlaylistChanged?.Invoke(updated);
    }

    /// <summary>
    /// Удаляет плейлист локально и, опционально, из облачного аккаунта YouTube Music.
    /// </summary>
    /// <param name="playlistId">Идентификатор удаляемого плейлиста.</param>
    /// <param name="deleteFromCloud">Флаг необходимости удаления плейлиста из облака YouTube Music.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Задача, представляющая асинхронную операцию удаления.</returns>
    public async Task DeletePlaylistAsync(string playlistId, bool deleteFromCloud = false, CancellationToken ct = default)
    {
        if (playlistId == LibraryService.LikedPlaylistId) return;

        var playlist = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
        if (playlist == null) return;

        foreach (var track in _registry.GetPinnedTracks())
            track.InPlaylists.Remove(playlistId);

        await _playlists.DeleteAsync(playlistId, ct).ConfigureAwait(false);

        if (deleteFromCloud && !string.IsNullOrEmpty(playlist.YoutubeId) && playlist.SyncMode == PlaylistSyncMode.TwoWaySync)
        {
            await _syncService.DeleteCloudPlaylistAsync(playlist.YoutubeId).ConfigureAwait(false);
        }

        OnPlaylistRemoved?.Invoke(playlistId);
    }

    /// <summary>
    /// Добавляет трек в локальный плейлист с немедленной синхронизацией мутации в облако.
    /// </summary>
    public async Task AddTrackToPlaylistAsync(string playlistId, TrackInfo track, CancellationToken ct = default)
    {
        if (playlistId == LibraryService.LikedPlaylistId)
        {
            await _tracks.SetLikedAsync(track.Id, CurrentOwnerId, true, ct: ct).ConfigureAwait(false);
            track.IsLiked = true;
            track.InPlaylists.Add(LibraryService.LikedPlaylistId);
            _registry.UpdatePinStatus(track);
            return;
        }

        var playlist = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
        if (playlist == null || !playlist.CanEditTracks) return;

        var canonical = _registry.RegisterOrUpdate(track);
        await _tracks.UpsertAsync(canonical, ct).ConfigureAwait(false);

        bool alreadyIn = await _playlists.ContainsTrackAsync(playlistId, canonical.Id, CurrentOwnerId, ct).ConfigureAwait(false);
        if (!alreadyIn)
        {
            await _playlists.AddTrackAsync(playlistId, canonical.Id, CurrentOwnerId, null, ct).ConfigureAwait(false);
            canonical.InPlaylists.Add(playlistId);
            _registry.UpdatePinStatus(canonical);

            await _syncService.AddTrackToCloudAsync(playlist, canonical.Id, ct).ConfigureAwait(false);
        }

        OnPlaylistChanged?.Invoke(playlist);
    }

    /// <summary>
    /// Пакетно добавляет набор треков в локальный плейлист с последующей синхронизацией мутации в облако.
    /// </summary>
    public async Task AddTracksToPlaylistAsync(string playlistId, IReadOnlyList<TrackInfo> tracks, CancellationToken ct = default)
    {
        if (tracks.Count == 0) return;

        if (playlistId == LibraryService.LikedPlaylistId)
        {
            for (int i = 0; i < tracks.Count; i++)
            {
                var canonical = _registry.RegisterOrUpdate(tracks[i]);
                await _tracks.UpsertAsync(canonical, ct).ConfigureAwait(false);
                await _tracks.SetLikedAsync(canonical.Id, CurrentOwnerId, true, ct: ct).ConfigureAwait(false);
                canonical.IsLiked = true;
                canonical.InPlaylists.Add(LibraryService.LikedPlaylistId);
                _registry.UpdatePinStatus(canonical);
            }
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

        await _playlists.AddTracksAsync(playlistId, trackIds, CurrentOwnerId, ct).ConfigureAwait(false);

        await _syncService.AddTracksToCloudAsync(playlist, trackIds, ct).ConfigureAwait(false);

        OnPlaylistChanged?.Invoke(playlist);
    }

    /// <summary>
    /// Удаляет трек из локального плейлиста и синхронизирует удаление (с использованием SetVideoId) в облако.
    /// </summary>
    /// <param name="playlistId">Идентификатор плейлиста.</param>
    /// <param name="trackId">Идентификатор удаляемого трека.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Задача, представляющая асинхронную операцию удаления.</returns>
    public async Task RemoveTrackFromPlaylistAsync(string playlistId, string trackId, CancellationToken ct = default)
    {
        if (playlistId == LibraryService.LikedPlaylistId)
        {
            await _tracks.SetLikedAsync(trackId, CurrentOwnerId, false, ct: ct).ConfigureAwait(false);
            var trackInfo = _registry.TryGet(trackId);
            if (trackInfo != null)
            {
                trackInfo.IsLiked = false;
                trackInfo.InPlaylists.Remove(LibraryService.LikedPlaylistId);
                _registry.UpdatePinStatus(trackInfo);
            }
            return;
        }

        var playlist = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
        if (playlist == null || !playlist.CanEditTracks) return;

        string? setVideoId = await _syncService.GetOrFetchSetVideoIdAsync(playlist, trackId, ct).ConfigureAwait(false);

        await _playlists.RemoveTrackAsync(playlistId, trackId, CurrentOwnerId, ct).ConfigureAwait(false);

        var track = _registry.TryGet(trackId);
        if (track != null)
        {
            track.InPlaylists.Remove(playlistId);
            _registry.UpdatePinStatus(track);
        }

        if (!string.IsNullOrEmpty(setVideoId))
        {
            await _syncService.RemoveTrackFromCloudAsync(playlist, setVideoId).ConfigureAwait(false);
        }

        OnPlaylistChanged?.Invoke(playlist);
    }

    /// <summary>
    /// Изменяет индекс трека в локальном плейлисте и синхронизирует этот сдвиг в облачном представлении.
    /// </summary>
    public async Task MovePlaylistTrackAsync(string playlistId, int oldIndex, int newIndex, CancellationToken ct = default)
    {
        if (playlistId == LibraryService.LikedPlaylistId || oldIndex == newIndex) return;

        var playlist = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
        var trackIds = await _playlists.GetTrackIdsAsync(playlistId, CurrentOwnerId, ct).ConfigureAwait(false);

        if (oldIndex < 0 || oldIndex >= trackIds.Count || newIndex < 0 || newIndex >= trackIds.Count)
            return;

        var movingTrackId = trackIds[oldIndex];
        await _playlists.MoveTrackAsync(playlistId, oldIndex, newIndex, ct).ConfigureAwait(false);

        if (playlist != null)
        {
            trackIds.RemoveAt(oldIndex);
            trackIds.Insert(newIndex, movingTrackId);

            await _syncService.MoveTrackInCloudAsync(playlist, movingTrackId, newIndex, trackIds, ct).ConfigureAwait(false);
            OnPlaylistChanged?.Invoke(playlist);
        }
    }

    #endregion

    #region Delegated Cloud Operations

    /// <summary>
    /// Перенаправляет запрос на формирование снимка различий в сервис синхронизации.
    /// </summary>
    public async Task<PlaylistSyncPreview?> BuildPreviewAsync(string playlistId, CancellationToken ct = default)
    {
        var playlist = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
        return playlist == null ? null : await _syncService.BuildPreviewAsync(playlist, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Перенаправляет запрос на применение параметров синхронизации в сервис синхронизации.
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
            var updated = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false) ?? playlist;
            OnPlaylistChanged?.Invoke(updated);
        }

        return result;
    }

    /// <summary>
    /// Делегирует прямую синхронизацию плейлиста в сервис синхронизации.
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
            OnPlaylistChanged?.Invoke(playlist);

        return result;
    }

    /// <summary>
    /// Делегирует синхронизацию понравившихся треков в сервис синхронизации.
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
    /// Делегирует загрузку плейлиста в облачный аккаунт YouTube.
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
    /// Конвертирует удаленный плейлист в локальный формат со сбросом облачных параметров.
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
    /// Объединяет треки двух локальных плейлистов.
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