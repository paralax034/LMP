using LMP.Core.Data.Repositories;
using LMP.Core.Models;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Music;
using LMP.Core.Youtube.Search;
using LMP.Core.Youtube.Utils;

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
    private readonly YoutubeProvider _youtube;
    private readonly CookieAuthService _auth;
    private readonly YoutubeUserDataService _ytUser;
    private readonly NotificationService _notifications;
    private readonly INetworkManager _networkManager;

    public event Action<Playlist>? OnPlaylistChanged;
    public event Action<string>? OnPlaylistRemoved;

    private string CurrentOwnerId => _auth.State.DisplayId;

    public PlaylistService(
        IPlaylistRepository playlists,
        ITrackRepository tracks,
        TrackRegistry registry,
        YoutubeProvider youtube,
        CookieAuthService auth,
        YoutubeUserDataService ytUser,
        NotificationService notifications,
        INetworkManager networkManager)
    {
        _playlists = playlists;
        _tracks = tracks;
        _registry = registry;
        _youtube = youtube;
        _auth = auth;
        _ytUser = ytUser;
        _notifications = notifications;
        _networkManager = networkManager;
    }

    #region Read API

    public async Task<Playlist?> GetPlaylistAsync(string id, CancellationToken ct = default) =>
        await _playlists.GetByIdAsync(id, CurrentOwnerId, ct).ConfigureAwait(false);

    public async Task<(Playlist Playlist, int TrackCount)?> GetPlaylistWithCountAsync(string playlistId, CancellationToken ct = default)
    {
        var playlist = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
        if (playlist == null) return null;

        var count = await _playlists.GetTrackCountAsync(playlistId, CurrentOwnerId, ct).ConfigureAwait(false);
        return (playlist, count);
    }

    public async Task<List<(Playlist Playlist, int TrackCount)>> GetAllPlaylistsWithCountsAsync(CancellationToken ct = default) =>
        await _playlists.GetAllWithCountsAsync(CurrentOwnerId, ct).ConfigureAwait(false);

    public async Task<List<Playlist>> GetAllPlaylistsAsync(CancellationToken ct = default) =>
        await _playlists.GetAllAsync(CurrentOwnerId, ct).ConfigureAwait(false);

    public async Task<List<string>> GetPlaylistTrackIdsAsync(string playlistId, CancellationToken ct = default) =>
        await _playlists.GetTrackIdsAsync(playlistId, CurrentOwnerId, ct).ConfigureAwait(false);

    public async Task<List<TrackInfo>> GetPlaylistTracksAsync(string playlistId, CancellationToken ct = default)
    {
        var trackIds = await _playlists.GetTrackIdsAsync(playlistId, CurrentOwnerId, ct).ConfigureAwait(false);
        if (trackIds.Count == 0) return [];

        return await _registry.PreloadAndReturnAsync(trackIds, ct).ConfigureAwait(false);
    }

    public async Task<List<TrackInfo>> GetPlaylistTracksAsync(string playlistId, int limit, int offset = 0, CancellationToken ct = default)
    {
        var trackIds = await _playlists.GetTrackIdsAsync(playlistId, CurrentOwnerId, limit, offset, ct).ConfigureAwait(false);
        if (trackIds.Count == 0) return [];

        return await _registry.PreloadAndReturnAsync(trackIds, ct).ConfigureAwait(false);
    }

    public async Task<TimeSpan> GetPlaylistTotalDurationAsync(string playlistId, CancellationToken ct = default)
    {
        var ticks = await _playlists.GetTotalDurationTicksAsync(playlistId, CurrentOwnerId, ct).ConfigureAwait(false);
        return TimeSpan.FromTicks(ticks);
    }

    public async Task<bool> IsTrackInPlaylistAsync(string trackId, string playlistId, CancellationToken ct = default) =>
        await _playlists.ContainsTrackAsync(playlistId, trackId, CurrentOwnerId, ct).ConfigureAwait(false);

    #endregion

    #region Mutation CRUD API

    /// <summary>
    /// Создает новый локальный плейлист с правами текущего пользователя.
    /// </summary>
    /// <param name="name">Название создаваемого плейлиста.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Созданный экземпляр модели <see cref="Playlist"/>.</returns>
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
        var originalTrackIds = await _playlists.GetTrackIdsAsync(sourcePlaylistId, CurrentOwnerId, ct).ConfigureAwait(false);

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
    /// <param name="playlistId">Локальный идентификатор плейлиста.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns><c>true</c>, если привязка к облаку выполнена успешно; иначе — <c>false</c>.</returns>
    public async Task<bool> LinkToCloudAsync(string playlistId, CancellationToken ct = default)
    {
        var playlist = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
        if (playlist == null || !_auth.IsAuthenticated) return false;

        var ytId = await _youtube.CreatePlaylistAsync(playlist.Name).ConfigureAwait(false);
        if (string.IsNullOrEmpty(ytId)) return false;

        playlist.YoutubeId = ytId;
        playlist.SyncMode = PlaylistSyncMode.TwoWaySync;
        playlist.Ownership = PlaylistOwnership.Mine;
        playlist.UpdatedAt = DateTime.Now;
        await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);

        var trackIds = await _playlists.GetTrackIdsAsync(playlistId, CurrentOwnerId, ct).ConfigureAwait(false);
        if (trackIds.Count > 0)
        {
            var syncOptions = new PlaylistSyncOptions
            {
                Strategy = PlaylistSyncStrategy.ReplaceCloud,
                SyncName = false,
                SyncDescription = false,
                SyncThumbnail = false,
                SyncTracks = true
            };

            await SyncDirectAsync(playlistId, syncOptions, ct).ConfigureAwait(false);
        }

        OnPlaylistChanged?.Invoke(playlist);
        return true;
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

    public async Task DeletePlaylistAsync(string playlistId, bool deleteFromCloud = false, CancellationToken ct = default)
    {
        if (playlistId == LibraryService.LikedPlaylistId) return;

        var playlist = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
        if (playlist == null) return;

        foreach (var track in _registry.GetPinnedTracks())
            track.InPlaylists.Remove(playlistId);

        await _playlists.DeleteAsync(playlistId, ct).ConfigureAwait(false);

        if (deleteFromCloud
            && playlist.SyncMode == PlaylistSyncMode.TwoWaySync
            && !string.IsNullOrEmpty(playlist.YoutubeId)
            && _auth.IsAuthenticated)
        {
            try
            {
                await _youtube.DeletePlaylistAsync(playlist.YoutubeId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error($"[PlaylistService] Failed to delete remote playlist {playlist.YoutubeId}: {ex.Message}");
            }
        }

        OnPlaylistRemoved?.Invoke(playlistId);
    }

    public async Task AddTrackToPlaylistAsync(string playlistId, TrackInfo track, CancellationToken ct = default)
    {
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
        }

        if (playlist.SyncMode == PlaylistSyncMode.TwoWaySync
            && !string.IsNullOrEmpty(playlist.YoutubeId)
            && _auth.IsAuthenticated)
        {
            try
            {
                var setVideoId = await _youtube.AddToPlaylistAsync(playlist.YoutubeId, canonical.Id).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(setVideoId))
                {
                    await _playlists.UpdateSetVideoIdAsync(playlistId, canonical.Id, setVideoId, ct).ConfigureAwait(false);
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                await DowngradeDeadCloudPlaylistAsync(playlist, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error($"[PlaylistService] Add track to cloud failed: {ex.Message}");
            }
        }

        OnPlaylistChanged?.Invoke(playlist);
    }

    public async Task AddTracksToPlaylistAsync(string playlistId, IReadOnlyList<TrackInfo> tracks, CancellationToken ct = default)
    {
        if (tracks.Count == 0) return;

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

        if (playlist.SyncMode == PlaylistSyncMode.TwoWaySync
            && !string.IsNullOrEmpty(playlist.YoutubeId)
            && _auth.IsAuthenticated)
        {
            try
            {
                var rawIds = new List<string>(tracks.Count);
                for (int i = 0; i < tracks.Count; i++)
                    rawIds.Add(tracks[i].GetRawId());

                var newSetVideoIds = await _youtube.AddTracksToPlaylistAsync(playlist.YoutubeId, rawIds).ConfigureAwait(false);
                var mappings = new List<(string TrackId, string SetVideoId)>(newSetVideoIds.Count);

                for (int i = 0; i < newSetVideoIds.Count && i < trackIds.Count; i++)
                {
                    var sid = newSetVideoIds[i];
                    if (!string.IsNullOrEmpty(sid))
                        mappings.Add((trackIds[i], sid));
                }

                if (mappings.Count > 0)
                {
                    await _playlists.UpdateSetVideoIdsAsync(playlistId, mappings, ct).ConfigureAwait(false);
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                await DowngradeDeadCloudPlaylistAsync(playlist, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error($"[PlaylistService] Batch add tracks to cloud failed: {ex.Message}");
            }
        }

        OnPlaylistChanged?.Invoke(playlist);
    }

    /// <summary>
    /// Удаляет трек из плейлиста с немедленной синхронизацией удаления в облако при активном TwoWaySync.
    /// </summary>
    /// <param name="playlistId">Идентификатор плейлиста.</param>
    /// <param name="trackId">Идентификатор удаляемого трека.</param>
    /// <param name="ct">Токен отмены операции.</param>
    public async Task RemoveTrackFromPlaylistAsync(string playlistId, string trackId, CancellationToken ct = default)
    {
        var playlist = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
        if (playlist == null || !playlist.CanEditTracks) return;

        string? setVideoId = null;
        bool needsYoutubeSync = (playlist.CanSyncToCloud || playlist.SyncMode == PlaylistSyncMode.TwoWaySync)
                                && !string.IsNullOrEmpty(playlist.YoutubeId)
                                && _auth.IsAuthenticated;

        if (needsYoutubeSync)
        {
            setVideoId = await _playlists.GetSetVideoIdAsync(playlistId, trackId, ct).ConfigureAwait(false);

            if (string.IsNullOrEmpty(setVideoId))
            {
                try
                {
                    var fullData = await _youtube.GetFullPlaylistDataAsync(playlist.YoutubeId!, ct).ConfigureAwait(false);
                    if (fullData?.Tracks is { Count: > 0 } remoteTracks)
                    {
                        var mappings = new List<(string TrackId, string SetVideoId)>(remoteTracks.Count);
                        string? targetSetVideoId = null;

                        for (int i = 0; i < remoteTracks.Count; i++)
                        {
                            var item = remoteTracks[i];
                            var localId = "yt_" + item.VideoId;
                            mappings.Add((localId, item.SetVideoId));

                            if (string.Equals(localId, trackId, StringComparison.Ordinal)
                                || string.Equals(item.VideoId, trackId, StringComparison.Ordinal))
                            {
                                targetSetVideoId = item.SetVideoId;
                            }
                        }

                        await _playlists.UpdateSetVideoIdsAsync(playlistId, mappings, ct).ConfigureAwait(false);
                        setVideoId = targetSetVideoId;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"[PlaylistService] Failed to fetch setVideoIds for track deletion: {ex.Message}");
                }
            }
        }

        await _playlists.RemoveTrackAsync(playlistId, trackId, CurrentOwnerId, ct).ConfigureAwait(false);

        var track = _registry.TryGet(trackId);
        if (track != null)
        {
            track.InPlaylists.Remove(playlistId);
            _registry.UpdatePinStatus(track);
        }

        if (needsYoutubeSync && !string.IsNullOrEmpty(setVideoId))
        {
            try
            {
                await _youtube.RemoveFromPlaylistAsync(playlist.YoutubeId!, setVideoId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error($"[PlaylistService] Failed to remove track from cloud: {ex.Message}");
            }
        }

        OnPlaylistChanged?.Invoke(playlist);
    }

    public async Task MovePlaylistTrackAsync(string playlistId, int oldIndex, int newIndex, CancellationToken ct = default)
    {
        if (oldIndex == newIndex) return;

        var playlist = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
        var trackIds = await _playlists.GetTrackIdsAsync(playlistId, CurrentOwnerId, ct).ConfigureAwait(false);

        if (oldIndex < 0 || oldIndex >= trackIds.Count || newIndex < 0 || newIndex >= trackIds.Count)
            return;

        var movingTrackId = trackIds[oldIndex];
        await _playlists.MoveTrackAsync(playlistId, oldIndex, newIndex, ct).ConfigureAwait(false);

        if (playlist != null && playlist.SyncMode == PlaylistSyncMode.TwoWaySync && !string.IsNullOrEmpty(playlist.YoutubeId) && _auth.IsAuthenticated)
        {
            var movingSetVideoId = await _playlists.GetSetVideoIdAsync(playlistId, movingTrackId, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(movingSetVideoId))
            {
                trackIds.RemoveAt(oldIndex);
                trackIds.Insert(newIndex, movingTrackId);

                string? predecessor = null;
                string? successor = null;

                if (newIndex == 0)
                {
                    if (trackIds.Count > 1)
                        successor = await _playlists.GetSetVideoIdAsync(playlistId, trackIds[1], ct).ConfigureAwait(false);
                }
                else
                {
                    predecessor = await _playlists.GetSetVideoIdAsync(playlistId, trackIds[newIndex - 1], ct).ConfigureAwait(false);
                }

                if (!string.IsNullOrEmpty(predecessor) || !string.IsNullOrEmpty(successor))
                {
                    try
                    {
                        await _youtube.MoveTracksInPlaylistAsync(
                            playlist.YoutubeId!,
                            [(movingSetVideoId, predecessor, successor)],
                            ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[PlaylistService] Remote move sync failed: {ex.Message}");
                    }
                }
            }
        }

        if (playlist != null)
            OnPlaylistChanged?.Invoke(playlist);
    }

    #endregion

    #region Synchronization Engine

    /// <summary>
    /// Формирует снимок различий между локальным плейлистом и удаленным представлением на YouTube.
    /// </summary>
    /// <param name="playlistId">Локальный идентификатор плейлиста.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Модель диффа <see cref="PlaylistSyncPreview"/> либо <c>null</c> при недоступности облака.</returns>
    public async Task<PlaylistSyncPreview?> BuildPreviewAsync(string playlistId, CancellationToken ct = default)
    {
        var playlist = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
        if (playlist == null || string.IsNullOrEmpty(playlist.YoutubeId))
            return null;

        try
        {
            YoutubeProvider.ThrowIfInCooldown();

            var fullDataTask = _youtube.GetFullPlaylistDataAsync(playlist.YoutubeId, ct);
            var localTrackIdsTask = _playlists.GetTrackIdsAsync(playlist.Id, CurrentOwnerId, ct);

            await Task.WhenAll(fullDataTask, localTrackIdsTask).ConfigureAwait(false);

            var fullData = await fullDataTask.ConfigureAwait(false);
            var localTrackIds = await localTrackIdsTask.ConfigureAwait(false);

            if (fullData == null)
            {
                playlist.IsCloudUnavailable = true;
                await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);
                OnPlaylistChanged?.Invoke(playlist);
                return null;
            }

            if (playlist.IsCloudUnavailable)
            {
                playlist.IsCloudUnavailable = false;
                await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);
                OnPlaylistChanged?.Invoke(playlist);
            }

            var localIdSet = new HashSet<string>(localTrackIds.Count, StringComparer.Ordinal);
            for (int i = 0; i < localTrackIds.Count; i++)
            {
                var rawId = YoutubeIdHelper.ExtractRawId(localTrackIds[i]);
                if (!string.IsNullOrEmpty(rawId))
                    localIdSet.Add(rawId);
            }

            int commonCount = 0;
            for (int i = 0; i < fullData.Tracks.Count; i++)
            {
                if (localIdSet.Contains(fullData.Tracks[i].VideoId))
                    commonCount++;
            }

            int cloudOnlyCount = fullData.Tracks.Count - commonCount;
            int localOnlyCount = localIdSet.Count - commonCount;

            return new PlaylistSyncPreview
            {
                LocalName = playlist.Name,
                CloudName = fullData.Title ?? playlist.Name,
                LocalDescription = playlist.Description,
                CloudDescription = fullData.Description,
                LocalThumbnailUrl = playlist.ThumbnailUrl,
                CloudThumbnailUrl = fullData.ThumbnailUrl,
                LocalOnlyTrackCount = localOnlyCount,
                CloudOnlyTrackCount = cloudOnlyCount,
                CommonTrackCount = commonCount,
                YoutubePlaylistId = playlist.YoutubeId,
                CachedCloudData = fullData
            };
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistService] Preview failed: {ex.Message}");
            if (ex is PlaylistUnavailableException or HttpRequestException)
            {
                playlist.IsCloudUnavailable = true;
                try { await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false); } catch { }
                OnPlaylistChanged?.Invoke(playlist);
            }
            return null;
        }
    }

    /// <summary>
    /// Применяет выбранные параметры синхронизации к локальному и облачному плейлистам.
    /// </summary>
    /// <param name="playlistId">Локальный идентификатор плейлиста.</param>
    /// <param name="preview">Снимок различий с кэшированными данными облака.</param>
    /// <param name="options">Набор флагов и стратегия синхронизации.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Результат выполнения операции <see cref="PlaylistSyncResult"/>.</returns>
    /// <remarks>
    /// Метод полностью потокобезопасен и stateless: оперирует переданным снимком без удержания
    /// долгоживущих ссылок внутри сервиса.
    /// </remarks>
    public async Task<PlaylistSyncResult> ApplySyncAsync(
        string playlistId,
        PlaylistSyncPreview preview,
        PlaylistSyncOptions options,
        CancellationToken ct = default)
    {
        var playlist = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
        if (playlist == null)
            return PlaylistSyncResult.Fail("Playlist not found");

        try
        {
            bool metadataChanged = await SyncMetadataAsync(playlist, preview, options, ct).ConfigureAwait(false);
            int addedLocally = 0, addedToCloud = 0, removedLocally = 0, removedFromCloud = 0;

            var cloudData = preview.CachedCloudData
                ?? await _youtube.GetFullPlaylistDataAsync(playlist.YoutubeId!, ct).ConfigureAwait(false);

            if (options.SyncTracks && cloudData != null)
            {
                (addedLocally, addedToCloud, removedLocally, removedFromCloud) = options.Strategy switch
                {
                    PlaylistSyncStrategy.ReplaceLocal => await ReplaceLocalTracksAsync(playlist, cloudData, ct).ConfigureAwait(false),
                    PlaylistSyncStrategy.ReplaceCloud => await ReplaceCloudTracksAsync(playlist, cloudData, ct).ConfigureAwait(false),
                    PlaylistSyncStrategy.Merge => await MergeTracksAsync(playlist, cloudData, ct).ConfigureAwait(false),
                    _ => (0, 0, 0, 0)
                };
            }

            if (cloudData != null)
            {
                if (cloudData.ViewCount.HasValue && playlist.ViewCount != cloudData.ViewCount)
                    playlist.ViewCount = cloudData.ViewCount;

                if (playlist.ReleaseDate != cloudData.ReleaseDate)
                    playlist.ReleaseDate = cloudData.ReleaseDate;

                if (string.IsNullOrEmpty(playlist.ThumbnailUrl) && !string.IsNullOrEmpty(cloudData.ThumbnailUrl))
                {
                    var normCloud = ThumbnailComparer.Normalize(cloudData.ThumbnailUrl);
                    if (!string.IsNullOrEmpty(normCloud))
                    {
                        playlist.ThumbnailUrl = cloudData.ThumbnailUrl;
                        playlist.ComputedColor = null;
                        metadataChanged = true;
                    }
                }
            }

            playlist.LastSyncedAtUtc = DateTime.UtcNow;
            playlist.IsCloudUnavailable = false;
            playlist.UpdatedAt = DateTime.Now;

            await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);
            OnPlaylistChanged?.Invoke(playlist);

            return new PlaylistSyncResult
            {
                Success = true,
                MetadataChanged = metadataChanged,
                TracksAddedLocally = addedLocally,
                TracksAddedToCloud = addedToCloud,
                TracksRemovedLocally = removedLocally,
                TracksRemovedFromCloud = removedFromCloud
            };
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistService] Apply failed: {ex.Message}");
            return PlaylistSyncResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Выполняет прямое синхронизирование плейлиста без показа предварительного UI-диалога.
    /// </summary>
    /// <param name="playlistId">Идентификатор плейлиста.</param>
    /// <param name="options">Параметры синхронизации.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Результат выполнения синхронизации.</returns>
    public async Task<PlaylistSyncResult> SyncDirectAsync(
        string playlistId,
        PlaylistSyncOptions options,
        CancellationToken ct = default)
    {
        var preview = await BuildPreviewAsync(playlistId, ct).ConfigureAwait(false);
        if (preview == null)
            return PlaylistSyncResult.Fail("Failed to generate preview from YouTube");

        return await ApplySyncAsync(playlistId, preview, options, ct).ConfigureAwait(false);
    }

    public async Task SyncLikedTracksAsync(CancellationToken ct = default)
    {
        if (!_auth.IsAuthenticated) return;

        try
        {
            Log.Info("[PlaylistService] Starting liked videos sync from YouTube...");
            var likedTracks = await _ytUser.GetLikedTracksAsync(_youtube).ConfigureAwait(false);
            if (likedTracks.Count == 0) return;

            var localLikedIds = await _playlists.GetTrackIdsAsync(LibraryService.LikedPlaylistId, CurrentOwnerId, ct).ConfigureAwait(false);
            var existingIds = new HashSet<string>(localLikedIds, StringComparer.Ordinal);
            var newTracks = new List<TrackInfo>();

            for (int i = 0; i < likedTracks.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                var track = likedTracks[i];
                track.IsLiked = true;

                if (existingIds.Add(track.Id))
                    newTracks.Add(track);
            }

            if (newTracks.Count > 0)
            {
                for (int i = 0; i < newTracks.Count; i++)
                {
                    var canonical = _registry.RegisterOrUpdate(newTracks[i]);
                    await _tracks.UpsertAsync(canonical, ct).ConfigureAwait(false);
                }

                var idsToAdd = newTracks.Select(t => t.Id).ToList();
                await _playlists.AddTracksAsync(LibraryService.LikedPlaylistId, idsToAdd, CurrentOwnerId, ct).ConfigureAwait(false);
            }

            Log.Info($"[PlaylistService] Liked sync complete. Added {newTracks.Count} new tracks.");
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistService] Liked tracks sync failed: {ex.Message}");
        }
    }

    public async Task ConvertToLocalAsync(string playlistId, CancellationToken ct = default)
    {
        var pl = await GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
        if (pl == null) return;

        var trackIds = await _playlists.GetTrackIdsAsync(playlistId, CurrentOwnerId, ct).ConfigureAwait(false);

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

    public async Task<bool> MergePlaylistsAsync(string sourceId, string targetId, CancellationToken ct = default)
    {
        var source = await GetPlaylistAsync(sourceId, ct).ConfigureAwait(false);
        var target = await GetPlaylistAsync(targetId, ct).ConfigureAwait(false);
        if (source == null || target == null || !target.IsLocal) return false;

        var sourceTrackIds = await _playlists.GetTrackIdsAsync(sourceId, CurrentOwnerId, ct).ConfigureAwait(false);
        var targetTrackIds = await _playlists.GetTrackIdsAsync(targetId, CurrentOwnerId, ct).ConfigureAwait(false);
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

    public async Task UploadPlaylistToAccountAsync(string localPlaylistId, CancellationToken ct = default)
    {
        if (!_auth.IsAuthenticated) return;

        var localPl = await GetPlaylistAsync(localPlaylistId, ct).ConfigureAwait(false);
        if (localPl == null || localPl.SyncMode != PlaylistSyncMode.LocalOnly) return;

        try
        {
            var trackIds = await _playlists.GetTrackIdsAsync(localPlaylistId, CurrentOwnerId, ct).ConfigureAwait(false);
            var rawVideoIds = new List<string>(trackIds.Count);

            for (int i = 0; i < trackIds.Count; i++)
            {
                var id = trackIds[i];
                if (id.StartsWith("yt_", StringComparison.Ordinal))
                    rawVideoIds.Add(YoutubeIdHelper.ExtractRawId(id));
            }

            var ytId = await _youtube.CreatePlaylistAsync(localPl.Name, rawVideoIds.Count > 0 ? rawVideoIds : null).ConfigureAwait(false);
            if (string.IsNullOrEmpty(ytId))
                throw new InvalidOperationException("YouTube returned empty playlist ID.");

            localPl.YoutubeId = ytId;
            localPl.SyncMode = PlaylistSyncMode.TwoWaySync;
            await AddOrUpdatePlaylistAsync(localPl, ct).ConfigureAwait(false);

            if (rawVideoIds.Count > 0)
            {
                try
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    var fullData = await _youtube.GetFullPlaylistDataAsync(ytId, ct).ConfigureAwait(false);
                    if (fullData?.Tracks is { Count: > 0 } tracks)
                    {
                        var mappings = new List<(string TrackId, string SetVideoId)>(tracks.Count);
                        for (int i = 0; i < tracks.Count; i++)
                            mappings.Add(("yt_" + tracks[i].VideoId, tracks[i].SetVideoId));

                        await _playlists.UpdateSetVideoIdsAsync(localPlaylistId, mappings, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"[PlaylistService] Failed to cache setVideoIds after upload: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistService] Upload failed: {ex.Message}");
        }
    }

    #endregion

    #region Private Sync Helpers

    private async Task DowngradeDeadCloudPlaylistAsync(Playlist playlist, CancellationToken ct)
    {
        playlist.SyncMode = PlaylistSyncMode.LocalOnly;
        playlist.YoutubeId = null;
        playlist.IsCloudUnavailable = false;
        await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);
        OnPlaylistChanged?.Invoke(playlist);

        Log.Warn($"[PlaylistService] Playlist '{playlist.Name}' (ID: {playlist.Id}) returned 404. Converted to LocalOnly.");

        await _notifications.ShowToastAsync(
            titleKey: "Dialog_Warning_Title",
            messageKey: "Playlist_NotFoundOnCloud_Downgraded",
            severity: NotificationSeverity.Warning,
            durationMs: 6000,
            messageArgs: [playlist.Name],
            ct: ct).ConfigureAwait(false);
    }

    private async Task<bool> SyncMetadataAsync(
        Playlist playlist,
        PlaylistSyncPreview preview,
        PlaylistSyncOptions options,
        CancellationToken ct)
    {
        bool changed = false;
        bool isCloudSource = options.Strategy != PlaylistSyncStrategy.ReplaceCloud;

        if (options.SyncName && preview.NameDiffers)
        {
            if (isCloudSource)
            {
                playlist.Name = preview.CloudName;
                changed = true;
            }
            else
            {
                try { await _youtube.RenamePlaylistAsync(playlist.YoutubeId!, playlist.Name).ConfigureAwait(false); }
                catch (Exception ex) { Log.Error($"[PlaylistService] Rename on YouTube failed: {ex.Message}"); }
            }
        }

        if (options.SyncDescription && preview.DescriptionDiffers)
        {
            if (isCloudSource)
            {
                playlist.Description = preview.CloudDescription ?? string.Empty;
                changed = true;
            }
            else
            {
                try
                {
                    var targetDesc = playlist.Description?.Trim() ?? string.Empty;
                    await _youtube.EditPlaylistDescriptionAsync(playlist.YoutubeId!, targetDesc).ConfigureAwait(false);
                }
                catch (Exception ex) { Log.Error($"[PlaylistService] Description update on YouTube failed: {ex.Message}"); }
            }
        }

        if (options.SyncThumbnail && preview.ThumbnailDiffers)
        {
            if (isCloudSource)
            {
                if (!string.IsNullOrEmpty(preview.CloudThumbnailUrl))
                {
                    playlist.ThumbnailUrl = preview.CloudThumbnailUrl;
                    playlist.ComputedColor = null;
                    changed = true;
                }
            }
            else if (!string.IsNullOrEmpty(playlist.ThumbnailUrl))
            {
                try
                {
                    var uploaded = await UploadThumbnailToYoutubeAsync(playlist.YoutubeId!, playlist.ThumbnailUrl, ct).ConfigureAwait(false);
                    if (uploaded && !string.IsNullOrEmpty(preview.CloudThumbnailUrl))
                    {
                        playlist.ThumbnailUrl = preview.CloudThumbnailUrl;
                        changed = true;
                    }
                }
                catch (Exception ex) { Log.Error($"[PlaylistService] Thumbnail upload failed: {ex.Message}"); }
            }
        }

        return changed;
    }

    /// <summary>
    /// Заменяет локальные треки плейлиста треками из снимка облака YouTube.
    /// </summary>
    private async Task<(int AddedLocally, int AddedToCloud, int RemovedLocally, int RemovedFromCloud)>
        ReplaceLocalTracksAsync(Playlist playlist, FullPlaylistSyncData fullData, CancellationToken ct)
    {
        var localTrackIds = await _playlists.GetTrackIdsAsync(playlist.Id, CurrentOwnerId, ct).ConfigureAwait(false);

        if (fullData == null) return (0, 0, 0, 0);

        int removedLocally = 0;
        for (int i = 0; i < localTrackIds.Count; i++)
        {
            await _playlists.RemoveTrackAsync(playlist.Id, localTrackIds[i], CurrentOwnerId, ct).ConfigureAwait(false);
            removedLocally++;
        }

        int addedLocally = 0;
        var mappings = new List<(string TrackId, string SetVideoId)>(fullData.Tracks.Count);

        for (int i = 0; i < fullData.Tracks.Count; i++)
        {
            var remote = fullData.Tracks[i];
            var track = CreateTrackInfo(remote);
            var canonical = _registry.RegisterOrUpdate(track);

            await _tracks.UpsertAsync(canonical, ct).ConfigureAwait(false);
            await _playlists.AddTrackAsync(playlist.Id, canonical.Id, CurrentOwnerId, null, ct).ConfigureAwait(false);
            addedLocally++;

            if (!string.IsNullOrEmpty(remote.SetVideoId))
                mappings.Add(("yt_" + remote.VideoId, remote.SetVideoId));
        }

        if (mappings.Count > 0)
            await _playlists.UpdateSetVideoIdsAsync(playlist.Id, mappings, ct).ConfigureAwait(false);

        return (addedLocally, 0, removedLocally, 0);
    }

    /// <summary>
    /// Заменяет облачный список треков на YouTube локальным содержимым плейлиста.
    /// </summary>
    private async Task<(int AddedLocally, int AddedToCloud, int RemovedLocally, int RemovedFromCloud)>
        ReplaceCloudTracksAsync(Playlist playlist, FullPlaylistSyncData fullData, CancellationToken ct)
    {
        var youtubeId = playlist.YoutubeId!;
        var localTrackIds = await _playlists.GetTrackIdsAsync(playlist.Id, CurrentOwnerId, ct).ConfigureAwait(false);

        if (fullData == null) return (0, 0, 0, 0);

        var localRawIdMap = new Dictionary<string, string>(localTrackIds.Count, StringComparer.Ordinal);
        for (int i = 0; i < localTrackIds.Count; i++)
        {
            var raw = YoutubeIdHelper.ExtractRawId(localTrackIds[i]);
            if (!string.IsNullOrEmpty(raw))
                localRawIdMap.TryAdd(raw, localTrackIds[i]);
        }

        var toRemoveSetVideoIds = new List<string>();
        for (int i = 0; i < fullData.Tracks.Count; i++)
        {
            var cloudTrack = fullData.Tracks[i];
            var cloudRawId = YoutubeIdHelper.ExtractRawId(cloudTrack.VideoId);

            if (!localRawIdMap.ContainsKey(cloudRawId) &&
                !string.IsNullOrEmpty(cloudTrack.SetVideoId) &&
                !cloudTrack.SetVideoId.Equals("to_be_updated_by_client", StringComparison.OrdinalIgnoreCase))
            {
                toRemoveSetVideoIds.Add(cloudTrack.SetVideoId);
            }
        }

        int removedFromCloud = 0;
        if (toRemoveSetVideoIds.Count > 0)
        {
            await _youtube.RemoveTracksFromPlaylistAsync(youtubeId, toRemoveSetVideoIds).ConfigureAwait(false);
            removedFromCloud = toRemoveSetVideoIds.Count;
        }

        var currentCloudTracks = fullData.Tracks
            .Where(t => localRawIdMap.ContainsKey(YoutubeIdHelper.ExtractRawId(t.VideoId)))
            .ToList();

        var trackToSetVideoId = new Dictionary<string, string>(localTrackIds.Count, StringComparer.Ordinal);
        for (int i = 0; i < currentCloudTracks.Count; i++)
        {
            var t = currentCloudTracks[i];
            var raw = YoutubeIdHelper.ExtractRawId(t.VideoId);
            if (!string.IsNullOrEmpty(t.SetVideoId) && localRawIdMap.TryGetValue(raw, out var originalLocalId))
                trackToSetVideoId[originalLocalId] = t.SetVideoId;
        }

        var cloudRawIdSet = new HashSet<string>(fullData.Tracks.Count, StringComparer.Ordinal);
        for (int i = 0; i < fullData.Tracks.Count; i++)
        {
            var raw = YoutubeIdHelper.ExtractRawId(fullData.Tracks[i].VideoId);
            if (!string.IsNullOrEmpty(raw))
                cloudRawIdSet.Add(raw);
        }

        var toUpload = new List<string>();
        for (int i = 0; i < localTrackIds.Count; i++)
        {
            var raw = YoutubeIdHelper.ExtractRawId(localTrackIds[i]);
            if (!cloudRawIdSet.Contains(raw))
                toUpload.Add(localTrackIds[i]);
        }

        int addedToCloud = 0;
        if (toUpload.Count > 0)
        {
            var newSetVideoIds = await _youtube.AddTracksToPlaylistAsync(youtubeId, toUpload).ConfigureAwait(false);
            addedToCloud = toUpload.Count;

            for (int i = 0; i < newSetVideoIds.Count && i < toUpload.Count; i++)
            {
                if (!string.IsNullOrEmpty(newSetVideoIds[i]))
                    trackToSetVideoId[toUpload[i]] = newSetVideoIds[i]!;
            }
        }

        if (trackToSetVideoId.Count > 0)
        {
            var mappings = trackToSetVideoId.Select(kvp => (kvp.Key, kvp.Value)).ToList();
            await _playlists.UpdateSetVideoIdsAsync(playlist.Id, mappings, ct).ConfigureAwait(false);
        }

        var simulatedCloudOrder = currentCloudTracks
            .Select(t => t.SetVideoId)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();

        for (int i = 0; i < toUpload.Count; i++)
        {
            if (trackToSetVideoId.TryGetValue(toUpload[i], out var sid))
                simulatedCloudOrder.Add(sid);
        }

        var desiredOrder = new List<string>(localTrackIds.Count);
        for (int i = 0; i < localTrackIds.Count; i++)
        {
            if (trackToSetVideoId.TryGetValue(localTrackIds[i], out var sid))
                desiredOrder.Add(sid);
        }

        var reorderMoves = new List<(string SetVideoId, string? Predecessor, string? Successor)>();
        for (int targetIdx = 0; targetIdx < desiredOrder.Count; targetIdx++)
        {
            var desiredSid = desiredOrder[targetIdx];
            int currentIdx = simulatedCloudOrder.IndexOf(desiredSid);

            if (currentIdx == -1 || currentIdx == targetIdx)
                continue;

            simulatedCloudOrder.RemoveAt(currentIdx);
            simulatedCloudOrder.Insert(targetIdx, desiredSid);

            if (targetIdx == 0)
            {
                var successor = simulatedCloudOrder[targetIdx + 1];
                reorderMoves.Add((desiredSid, null, successor));
            }
            else
            {
                var predecessor = simulatedCloudOrder[targetIdx - 1];
                reorderMoves.Add((desiredSid, predecessor, null));
            }
        }

        if (reorderMoves.Count > 0)
        {
            await _youtube.MoveTracksInPlaylistAsync(youtubeId, reorderMoves, ct).ConfigureAwait(false);
        }

        return (0, addedToCloud, 0, removedFromCloud);
    }

    /// <summary>
    /// Выполняет объединение треков между локальным плейлистом и удаленным списком YouTube без удаления.
    /// </summary>
    private async Task<(int AddedLocally, int AddedToCloud, int RemovedLocally, int RemovedFromCloud)>
        MergeTracksAsync(Playlist playlist, FullPlaylistSyncData fullData, CancellationToken ct)
    {
        var youtubeId = playlist.YoutubeId!;
        var localTrackIds = await _playlists.GetTrackIdsAsync(playlist.Id, CurrentOwnerId, ct).ConfigureAwait(false);

        if (fullData == null) return (0, 0, 0, 0);

        var localIdSet = new HashSet<string>(localTrackIds, StringComparer.Ordinal);
        var cloudIdSet = new HashSet<string>(fullData.Tracks.Count, StringComparer.Ordinal);
        for (int i = 0; i < fullData.Tracks.Count; i++)
            cloudIdSet.Add("yt_" + fullData.Tracks[i].VideoId);

        int addedLocally = 0;
        var setVideoIdMappings = new List<(string TrackId, string SetVideoId)>(fullData.Tracks.Count);

        for (int i = 0; i < fullData.Tracks.Count; i++)
        {
            var remote = fullData.Tracks[i];
            var fullId = "yt_" + remote.VideoId;

            if (!string.IsNullOrEmpty(remote.SetVideoId))
                setVideoIdMappings.Add((fullId, remote.SetVideoId));

            if (!localIdSet.Contains(fullId))
            {
                var track = CreateTrackInfo(remote);
                var canonical = _registry.RegisterOrUpdate(track);
                await _tracks.UpsertAsync(canonical, ct).ConfigureAwait(false);
                await _playlists.AddTrackAsync(playlist.Id, canonical.Id, CurrentOwnerId, null, ct).ConfigureAwait(false);
                addedLocally++;
            }
        }

        var toUpload = new List<string>();
        for (int i = 0; i < localTrackIds.Count; i++)
        {
            var trackId = localTrackIds[i];
            if (trackId.StartsWith("yt_", StringComparison.Ordinal) && !cloudIdSet.Contains(trackId))
                toUpload.Add(trackId);
        }

        int addedToCloud = 0;
        if (toUpload.Count > 0)
        {
            var newSetVideoIds = await _youtube.AddTracksToPlaylistAsync(youtubeId, toUpload).ConfigureAwait(false);
            addedToCloud = toUpload.Count;

            for (int i = 0; i < newSetVideoIds.Count && i < toUpload.Count; i++)
            {
                if (!string.IsNullOrEmpty(newSetVideoIds[i]))
                    setVideoIdMappings.Add((toUpload[i], newSetVideoIds[i]!));
            }
        }

        if (setVideoIdMappings.Count > 0)
            await _playlists.UpdateSetVideoIdsAsync(playlist.Id, setVideoIdMappings, ct).ConfigureAwait(false);

        return (addedLocally, addedToCloud, 0, 0);
    }

    private static TrackInfo CreateTrackInfo(RemoteTrackInfo remote) => new()
    {
        Id = "yt_" + remote.VideoId,
        Title = remote.Title,
        Author = remote.Author,
        Duration = TimeSpan.FromSeconds(remote.DurationSeconds),
        ThumbnailUrl = remote.ThumbnailUrl,
        IsMusic = true,
        Url = $"https://music.youtube.com/watch?v={remote.VideoId}"
    };

    private async Task<bool> UploadThumbnailToYoutubeAsync(string youtubePlaylistId, string thumbnailUrl, CancellationToken ct)
    {
        byte[] imageData;

        if (thumbnailUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            imageData = await _networkManager.ImageClient.GetByteArrayAsync(thumbnailUrl, linkedCts.Token).ConfigureAwait(false);
        }
        else if (thumbnailUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(thumbnailUrl);
            var localPath = uri.LocalPath;
            if (!File.Exists(localPath)) return false;
            imageData = await File.ReadAllBytesAsync(localPath, ct).ConfigureAwait(false);
        }
        else if (Path.IsPathRooted(thumbnailUrl) && File.Exists(thumbnailUrl))
        {
            imageData = await File.ReadAllBytesAsync(thumbnailUrl, ct).ConfigureAwait(false);
        }
        else
        {
            return false;
        }

        if (imageData.Length == 0 || imageData.Length > 20 * 1024 * 1024)
            return false;

        return await _youtube.UploadPlaylistThumbnailAsync(youtubePlaylistId, imageData).ConfigureAwait(false);
    }

    #endregion
}