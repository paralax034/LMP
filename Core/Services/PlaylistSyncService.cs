using LMP.Core.Data.Repositories;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Music;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Videos;

namespace LMP.Core.Services;

/// <summary>
/// Сервис синхронизации локальных плейлистов с платформой YouTube Music.
/// Инкапсулирует вычисление диффов, пакетные обновления, разрешение конфликтов и фоновую загрузку медиаданных.
/// </summary>
/// <remarks>
/// Полностью изолирует внешние HTTP-запросы и протоколы мутаций YouTube от локального CRUD-сервиса плейлистов.
/// Все сетевые операции защищены проверками Cooldown для предотвращения Bot Detection.
/// </remarks>
public sealed class PlaylistSyncService
{
    private readonly IPlaylistRepository _playlists;
    private readonly ITrackRepository _tracks;
    private readonly TrackRegistry _registry;
    private readonly YoutubeProvider _youtube;
    private readonly CookieAuthService _auth;
    private readonly YoutubeUserDataService _ytUser;
    private readonly NotificationService _notifications;
    private readonly INetworkManager _networkManager;

    private string CurrentOwnerId => _auth.State.DisplayId;

    /// <summary>
    /// Инициализирует новый экземпляр сервиса синхронизации плейлистов.
    /// </summary>
    public PlaylistSyncService(
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

    /// <summary>
    /// Создает удаленный аналог плейлиста на YouTube Music и переводит его в режим двусторонней синхронизации.
    /// </summary>
    public async Task<bool> LinkToCloudAsync(Playlist playlist, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(playlist);

        if (!_auth.IsAuthenticated) return false;

        var ytId = await _youtube.CreatePlaylistAsync(playlist.Name).ConfigureAwait(false);
        if (string.IsNullOrEmpty(ytId)) return false;

        playlist.YoutubeId = ytId;
        playlist.SyncMode = PlaylistSyncMode.TwoWaySync;
        playlist.Ownership = PlaylistOwnership.Mine;
        playlist.UpdatedAt = DateTime.Now;
        await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);

        var trackIds = await _playlists.GetTrackIdsAsync(playlist.Id, CurrentOwnerId, ct).ConfigureAwait(false);
        bool hasThumbnail = !string.IsNullOrEmpty(playlist.ThumbnailUrl);

        if (trackIds.Count > 0 || hasThumbnail)
        {
            var syncOptions = new PlaylistSyncOptions
            {
                Strategy = PlaylistSyncStrategy.ReplaceCloud,
                SyncName = false,
                SyncDescription = !string.IsNullOrWhiteSpace(playlist.Description),
                SyncThumbnail = hasThumbnail,
                SyncTracks = trackIds.Count > 0
            };

            await SyncDirectAsync(playlist, syncOptions, ct).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Формирует снимок различий между локальным плейлистом и удаленным представлением на YouTube.
    /// </summary>
    public async Task<PlaylistSyncPreview?> BuildPreviewAsync(Playlist playlist, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(playlist);

        if (string.IsNullOrEmpty(playlist.YoutubeId))
            return null;

        try
        {
            VideoController.ThrowIfInCooldown();

            var fullDataTask = _youtube.GetFullPlaylistDataAsync(playlist.YoutubeId, ct);
            var localTrackIdsTask = _playlists.GetTrackIdsAsync(playlist.Id, CurrentOwnerId, ct);

            await Task.WhenAll(fullDataTask, localTrackIdsTask).ConfigureAwait(false);

            var fullData = await fullDataTask.ConfigureAwait(false);
            var localTrackIds = await localTrackIdsTask.ConfigureAwait(false);

            if (fullData == null)
            {
                playlist.IsCloudUnavailable = true;
                await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);
                return null;
            }

            if (playlist.IsCloudUnavailable)
            {
                playlist.IsCloudUnavailable = false;
                await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);
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
            Log.Error($"[PlaylistSync] Preview failed: {ex.Message}");
            if (ex is PlaylistUnavailableException or HttpRequestException)
            {
                playlist.IsCloudUnavailable = true;
                try { await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false); } catch { }
            }
            return null;
        }
    }

    /// <summary>
    /// Применяет выбранные параметры синхронизации к локальному и облачному плейлистам.
    /// </summary>
    public async Task<PlaylistSyncResult> ApplySyncAsync(
        Playlist playlist,
        PlaylistSyncPreview preview,
        PlaylistSyncOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(options);

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
            Log.Error($"[PlaylistSync] Apply failed: {ex.Message}");
            return PlaylistSyncResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Выполняет прямое синхронизирование плейлиста без показа предварительного UI-диалога.
    /// </summary>
    public async Task<PlaylistSyncResult> SyncDirectAsync(
        Playlist playlist,
        PlaylistSyncOptions options,
        CancellationToken ct = default)
    {
        var preview = await BuildPreviewAsync(playlist, ct).ConfigureAwait(false);
        if (preview == null)
            return PlaylistSyncResult.Fail("Failed to generate preview from YouTube");

        return await ApplySyncAsync(playlist, preview, options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Синхронизирует отметки «Мне нравится» текущего аккаунта с YouTube Music.
    /// </summary>
    public async Task SyncLikedTracksAsync(CancellationToken ct = default)
    {
        if (!_auth.IsAuthenticated) return;

        try
        {
            Log.Info("[PlaylistSync] Starting liked videos sync from YouTube...");
            var likedTracks = await _ytUser.GetLikedTracksAsync(_youtube).ConfigureAwait(false);
            if (likedTracks.Count == 0) return;

            for (int i = 0; i < likedTracks.Count; i++)
            {
                likedTracks[i].SetLikedState(true);
                _registry.RegisterOrUpdate(likedTracks[i], hasUserContext: true);
            }

            // Пакетное сохранение метаданных за 1 транзакцию
            await _tracks.UpsertBatchAsync(likedTracks, ct).ConfigureAwait(false);

            var trackIds = new List<string>(likedTracks.Count);
            for (int i = 0; i < likedTracks.Count; i++)
            {
                trackIds.Add(likedTracks[i].Id);
            }

            // Пакетное сохранение порядка за 1 транзакцию с нисходящими метками времени
            await _tracks.SetLikedBatchAsync(trackIds, CurrentOwnerId, preserveOrder: true, ct).ConfigureAwait(false);

            Log.Info($"[PlaylistSync] Liked sync complete. Processed {likedTracks.Count} tracks.");
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistSync] Liked tracks sync failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Создает удаленный плейлист в аккаунте YouTube на основе состава локального плейлиста.
    /// </summary>
    public async Task UploadPlaylistToAccountAsync(Playlist playlist, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(playlist);

        if (!_auth.IsAuthenticated || playlist.SyncMode != PlaylistSyncMode.LocalOnly) return;

        try
        {
            var trackIds = await _playlists.GetTrackIdsAsync(playlist.Id, CurrentOwnerId, ct).ConfigureAwait(false);
            var rawVideoIds = new List<string>(trackIds.Count);

            for (int i = 0; i < trackIds.Count; i++)
            {
                var id = trackIds[i];
                if (id.StartsWith("yt_", StringComparison.Ordinal))
                    rawVideoIds.Add(YoutubeIdHelper.ExtractRawId(id));
            }

            var ytId = await _youtube.CreatePlaylistAsync(playlist.Name, rawVideoIds.Count > 0 ? rawVideoIds : null).ConfigureAwait(false);
            if (string.IsNullOrEmpty(ytId))
                throw new InvalidOperationException("YouTube returned empty playlist ID.");

            playlist.YoutubeId = ytId;
            playlist.SyncMode = PlaylistSyncMode.TwoWaySync;
            await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);

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

                        await _playlists.UpdateSetVideoIdsAsync(playlist.Id, mappings, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"[PlaylistSync] Failed to cache setVideoIds after upload: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistSync] Upload failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Удаляет облачный плейлист из аккаунта YouTube.
    /// </summary>
    /// <param name="youtubePlaylistId">Идентификатор плейлиста на YouTube.</param>
    /// <returns>Задача, представляющая асинхронную операцию удаления плейлиста.</returns>
    public async Task DeleteCloudPlaylistAsync(string youtubePlaylistId)
    {
        if (!_auth.IsAuthenticated) return;

        try
        {
            await _youtube.DeletePlaylistAsync(youtubePlaylistId).ConfigureAwait(false);
            Log.Info($"[PlaylistSync] Deleted playlist {youtubePlaylistId} from YouTube");
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistSync] Failed to delete remote playlist {youtubePlaylistId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Добавляет трек в облачный плейлист.
    /// </summary>
    public async Task AddTrackToCloudAsync(Playlist playlist, string trackId, CancellationToken ct = default)
    {
        if (playlist.SyncMode != PlaylistSyncMode.TwoWaySync || string.IsNullOrEmpty(playlist.YoutubeId) || !_auth.IsAuthenticated)
            return;

        try
        {
            var rawId = YoutubeIdHelper.ExtractRawId(trackId);
            var setVideoId = await _youtube.AddToPlaylistAsync(playlist.YoutubeId, rawId).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(setVideoId))
            {
                await _playlists.UpdateSetVideoIdAsync(playlist.Id, trackId, setVideoId, ct).ConfigureAwait(false);
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            await DowngradeDeadCloudPlaylistAsync(playlist, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistSync] Add track to cloud failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Пакетно добавляет треки в облачный плейлист.
    /// </summary>
    public async Task AddTracksToCloudAsync(Playlist playlist, IReadOnlyList<string> trackIds, CancellationToken ct = default)
    {
        if (playlist.SyncMode != PlaylistSyncMode.TwoWaySync || string.IsNullOrEmpty(playlist.YoutubeId) || !_auth.IsAuthenticated)
            return;

        try
        {
            var newSetVideoIds = await _youtube.AddTracksToPlaylistAsync(playlist.YoutubeId, trackIds).ConfigureAwait(false);
            var mappings = new List<(string TrackId, string SetVideoId)>(newSetVideoIds.Count);

            for (int i = 0; i < newSetVideoIds.Count && i < trackIds.Count; i++)
            {
                var sid = newSetVideoIds[i];
                if (!string.IsNullOrEmpty(sid))
                    mappings.Add((trackIds[i], sid));
            }

            if (mappings.Count > 0)
            {
                await _playlists.UpdateSetVideoIdsAsync(playlist.Id, mappings, ct).ConfigureAwait(false);
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            await DowngradeDeadCloudPlaylistAsync(playlist, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistSync] Batch add tracks to cloud failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Получает сохраненный `setVideoId` или извлекает его через API YouTube, если он не сохранен.
    /// </summary>
    public async Task<string?> GetOrFetchSetVideoIdAsync(Playlist playlist, string trackId, CancellationToken ct = default)
    {
        bool needsYoutubeSync = (playlist.CanSyncToCloud || playlist.SyncMode == PlaylistSyncMode.TwoWaySync)
                                && !string.IsNullOrEmpty(playlist.YoutubeId)
                                && _auth.IsAuthenticated;

        if (!needsYoutubeSync) return null;

        string? setVideoId = await _playlists.GetSetVideoIdAsync(playlist.Id, trackId, ct).ConfigureAwait(false);

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

                    await _playlists.UpdateSetVideoIdsAsync(playlist.Id, mappings, ct).ConfigureAwait(false);
                    setVideoId = targetSetVideoId;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[PlaylistSync] Failed to fetch setVideoIds: {ex.Message}");
            }
        }

        return setVideoId;
    }

    /// <summary>
    /// Удаляет трек из облачного плейлиста по его идентификатору связи.
    /// </summary>
    /// <param name="playlist">Модель плейлиста.</param>
    /// <param name="setVideoId">Идентификатор связи видео в облачном плейлисте.</param>
    /// <returns>Задача, представляющая асинхронную операцию удаления трека.</returns>
    public async Task RemoveTrackFromCloudAsync(Playlist playlist, string setVideoId)
    {
        if (playlist.SyncMode != PlaylistSyncMode.TwoWaySync || string.IsNullOrEmpty(playlist.YoutubeId) || !_auth.IsAuthenticated)
            return;

        try
        {
            await _youtube.RemoveFromPlaylistAsync(playlist.YoutubeId, setVideoId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistSync] Failed to remove track from cloud: {ex.Message}");
        }
    }

    /// <summary>
    /// Изменяет порядок трека в облачном плейлисте.
    /// </summary>
    public async Task MoveTrackInCloudAsync(Playlist playlist, string movingTrackId, int newIndex, List<string> newTrackIdsOrder, CancellationToken ct = default)
    {
        if (playlist.SyncMode != PlaylistSyncMode.TwoWaySync || string.IsNullOrEmpty(playlist.YoutubeId) || !_auth.IsAuthenticated)
            return;

        var movingSetVideoId = await _playlists.GetSetVideoIdAsync(playlist.Id, movingTrackId, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(movingSetVideoId)) return;

        string? predecessor = null;
        string? successor = null;

        if (newIndex == 0)
        {
            if (newTrackIdsOrder.Count > 1)
                successor = await _playlists.GetSetVideoIdAsync(playlist.Id, newTrackIdsOrder[1], ct).ConfigureAwait(false);
        }
        else
        {
            predecessor = await _playlists.GetSetVideoIdAsync(playlist.Id, newTrackIdsOrder[newIndex - 1], ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(predecessor) || !string.IsNullOrEmpty(successor))
        {
            try
            {
                await _youtube.MoveTracksInPlaylistAsync(
                    playlist.YoutubeId,
                    [(movingSetVideoId, predecessor, successor)],
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"[PlaylistSync] Remote move sync failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Переводит удаленный плейлист в локальный режим при получении статуса 404 Not Found из облака.
    /// </summary>
    public async Task DowngradeDeadCloudPlaylistAsync(Playlist playlist, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(playlist);

        playlist.SyncMode = PlaylistSyncMode.LocalOnly;
        playlist.YoutubeId = null;
        playlist.IsCloudUnavailable = false;
        await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);

        Log.Warn($"[PlaylistSync] Playlist '{playlist.Name}' (ID: {playlist.Id}) returned 404. Converted to LocalOnly.");

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
        bool isCloudSource = options.Strategy == PlaylistSyncStrategy.ReplaceLocal;

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
                catch (Exception ex) { Log.Error($"[PlaylistSync] Rename on YouTube failed: {ex.Message}"); }
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
                catch (Exception ex) { Log.Error($"[PlaylistSync] Description update on YouTube failed: {ex.Message}"); }
            }
        }

        if (options.SyncThumbnail && preview.ThumbnailDiffers)
        {
            // При ReplaceLocal явно берем облачную обложку.
            // При ReplaceCloud или Merge (при наличии локальной) отдаем приоритет локальной обложке и пушим ее в YouTube.
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
                    if (uploaded)
                    {
                        changed = true;
                    }
                }
                catch (Exception ex) { Log.Error($"[PlaylistSync] Thumbnail upload failed: {ex.Message}"); }
            }
            else if (string.IsNullOrEmpty(playlist.ThumbnailUrl) && !string.IsNullOrEmpty(preview.CloudThumbnailUrl))
            {
                playlist.ThumbnailUrl = preview.CloudThumbnailUrl;
                playlist.ComputedColor = null;
                changed = true;
            }
        }

        return changed;
    }

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
}