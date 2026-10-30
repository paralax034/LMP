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
    private readonly NetworkManager _networkManager;

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
        NetworkManager networkManager)
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

        try
        {
            var ytId = await _youtube.CreatePlaylistAsync(
                playlist.Name,
                videoIds: null,
                description: playlist.Description).ConfigureAwait(false);

            if (string.IsNullOrEmpty(ytId)) return false;

            playlist.YoutubeId = ytId;
            playlist.SyncMode = PlaylistSyncMode.TwoWaySync;
            playlist.Ownership = PlaylistOwnership.Mine;
            playlist.UpdatedAt = DateTime.Now;
            await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);

            var trackIds = await _playlists.GetTrackIdsAsync(playlist.Id, CurrentOwnerId, ct).ConfigureAwait(false);
            bool hasThumbnail = !string.IsNullOrEmpty(playlist.ThumbnailUrl);
            bool hasDescription = !string.IsNullOrWhiteSpace(playlist.Description);

            if (trackIds.Count > 0 || hasThumbnail || hasDescription)
            {
                var syncOptions = new PlaylistSyncOptions
                {
                    Strategy = PlaylistSyncStrategy.ReplaceCloud,
                    SyncName = false,
                    SyncDescription = hasDescription,
                    SyncThumbnail = hasThumbnail,
                    SyncTracks = trackIds.Count > 0
                };

                await SyncDirectAsync(playlist, syncOptions, ct).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistSync] LinkToCloud failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Формирует снимок различий между локальным плейлистом и удаленным представлением на YouTube.
    /// При обнаружении удаления плейлиста на YouTube (404 Not Found или пустой ответ недоступности)
    /// автоматически вызывает <see cref="DowngradeDeadCloudPlaylistAsync"/> для перевода в локальный режим.
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

            // Если метаданные в облаке пустые, помечаем временную недоступность без безвозвратного отвязывания плейлиста.
            // Деструктивный даунгрейд допустим только при подтвержденных ошибках 404/Unavailable в блоках catch.
            if (string.IsNullOrEmpty(fullData.Title) && fullData.Tracks.Count == 0)
            {
                Log.Warn(
                    $"[PlaylistSync] Playlist '{playlist.Name}' ({playlist.YoutubeId}) received empty metadata from cloud. Marking unavailable without downgrade.");
                playlist.IsCloudUnavailable = true;
                await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);
                return null;
            }

            bool healedMetadata = false;
            if (ShouldUpdateViewCount(playlist.ViewCount, fullData.ViewCount))
            {
                playlist.ViewCount = fullData.ViewCount;
                healedMetadata = true;
            }

            if (fullData.ReleaseDate.HasValue && playlist.ReleaseDate != fullData.ReleaseDate)
            {
                playlist.ReleaseDate = fullData.ReleaseDate;
                healedMetadata = true;
            }
            else if (!fullData.ReleaseDate.HasValue && playlist.ReleaseDate is { Month: 1, Day: 1 })
            {
                playlist.ReleaseDate = null;
                healedMetadata = true;
            }

            if (!string.IsNullOrEmpty(fullData.Author) && !string.Equals(playlist.Author, fullData.Author, StringComparison.Ordinal))
            {
                playlist.Author = fullData.Author;
                healedMetadata = true;
            }

            if (!string.IsNullOrEmpty(fullData.OwnerChannelId) && !string.Equals(playlist.OwnerChannelId, fullData.OwnerChannelId, StringComparison.Ordinal))
            {
                playlist.OwnerChannelId = fullData.OwnerChannelId;
                healedMetadata = true;
            }

            if (fullData.Visibility != PlaylistVisibility.Unknown && playlist.Visibility != fullData.Visibility)
            {
                playlist.Visibility = fullData.Visibility;
                healedMetadata = true;
            }

            // Строгое определение прав владения по наличию musicEditablePlaylistDetailHeaderRenderer или форм редактирования WEB
            var detectedOwnership = fullData.IsEditable ? PlaylistOwnership.Mine : PlaylistOwnership.Foreign;
            if (playlist.Ownership != PlaylistOwnership.System && playlist.Ownership != detectedOwnership)
            {
                Log.Info($"[PlaylistSync] Updating ownership for '{playlist.Name}' ({playlist.YoutubeId}): {playlist.Ownership} -> {detectedOwnership}");
                playlist.Ownership = detectedOwnership;
                healedMetadata = true;
            }

            // Для чужих (read-only) плейлистов сбрасываем ошибочно назначенные локальные мозаики на облачную обложку YouTube
            if (playlist.Ownership == PlaylistOwnership.Foreign &&
                !string.IsNullOrEmpty(fullData.ThumbnailUrl) &&
                !ThumbnailComparer.IsYoutubeHosted(playlist.ThumbnailUrl))
            {
                playlist.ThumbnailUrl = fullData.ThumbnailUrl;
                playlist.ComputedColor = null;
                healedMetadata = true;
            }

            // Если локальная обложка является серверной ссылкой YouTube CDN, синхронизируем её с актуальным URL облака
            if (ThumbnailComparer.IsYoutubeHosted(playlist.ThumbnailUrl) &&
                !string.IsNullOrEmpty(fullData.ThumbnailUrl) &&
                !string.Equals(playlist.ThumbnailUrl, fullData.ThumbnailUrl, StringComparison.Ordinal))
            {
                playlist.ThumbnailUrl = fullData.ThumbnailUrl;
                healedMetadata = true;
            }

            // Если локальный файл обложки (мозаика/файл) уже был выгружен в облако и не менялся с момента синка,
            // фиксируем маркер синхронизации, чтобы не предлагать повторную Scotty-выгрузку
            bool localCoverAlreadySynced = IsLocalCoverAlreadySyncedWithCloud(playlist, fullData.ThumbnailUrl, out var normLocalThumb);
            if (localCoverAlreadySynced && !string.IsNullOrEmpty(normLocalThumb) &&
                !string.Equals(playlist.ETag, normLocalThumb, StringComparison.Ordinal))
            {
                playlist.ETag = normLocalThumb;
                healedMetadata = true;
            }

            var localIdSet = new HashSet<string>(localTrackIds.Count, StringComparer.Ordinal);
            for (int i = 0; i < localTrackIds.Count; i++)
            {
                var rawId = YoutubeIdHelper.ExtractRawId(localTrackIds[i]);
                if (!string.IsNullOrEmpty(rawId))
                    localIdSet.Add(rawId);
            }

            var cloudIdSet = new HashSet<string>(fullData.Tracks.Count, StringComparer.Ordinal);
            int commonCount = 0;
            for (int i = 0; i < fullData.Tracks.Count; i++)
            {
                var cloudRawId = YoutubeIdHelper.ExtractRawId(fullData.Tracks[i].VideoId);
                if (!string.IsNullOrEmpty(cloudRawId) && cloudIdSet.Add(cloudRawId))
                {
                    if (localIdSet.Contains(cloudRawId))
                        commonCount++;
                }
            }

            int cloudOnlyCount = cloudIdSet.Count - commonCount;
            int localOnlyCount = localIdSet.Count - commonCount;

            var preview = new PlaylistSyncPreview
            {
                LocalName = playlist.Name,
                CloudName = fullData.Title ?? playlist.Name,
                LocalDescription = playlist.Description,
                CloudDescription = fullData.Description,
                LocalThumbnailUrl = localCoverAlreadySynced ? fullData.ThumbnailUrl : playlist.ThumbnailUrl,
                CloudThumbnailUrl = fullData.ThumbnailUrl,
                LocalOnlyTrackCount = localOnlyCount,
                CloudOnlyTrackCount = cloudOnlyCount,
                CommonTrackCount = commonCount,
                YoutubePlaylistId = playlist.YoutubeId,
                CachedCloudData = fullData
            };

            // Если различий нет, плейлист полностью синхронизирован — фиксируем актуальное время синхронизации
            if (!preview.HasAnyDifference)
            {
                playlist.LastSyncedAtUtc = DateTime.UtcNow;
                healedMetadata = true;
            }

            if (playlist.IsCloudUnavailable || healedMetadata)
            {
                playlist.IsCloudUnavailable = false;
                await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);
            }

            Log.Info($"[PlaylistSync] Preview built for '{playlist.Name}' ({playlist.YoutubeId}): " +
                     $"hasDiff={preview.HasAnyDifference} (name={preview.NameDiffers}, desc={preview.DescriptionDiffers}, " +
                     $"thumb={preview.ThumbnailDiffers}, +local={localOnlyCount}, +cloud={cloudOnlyCount}, common={commonCount}), " +
                     $"ownership={playlist.Ownership}, canSyncToCloud={playlist.CanSyncToCloud}");
            Log.Debug($"[PlaylistSync] Preview thumbs for '{playlist.Name}': local='{playlist.ThumbnailUrl ?? "null"}', " +
                      $"cloud='{fullData.ThumbnailUrl ?? "null"}', alreadySyncedLocalCover={localCoverAlreadySynced}");

            return preview;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Log.Warn(
                $"[PlaylistSync] Playlist '{playlist.Name}' ({playlist.YoutubeId}) returned 404 Not Found. Downgrading to LocalOnly...");
            await DowngradeDeadCloudPlaylistAsync(playlist, ct).ConfigureAwait(false);
            return null;
        }
        catch (PlaylistUnavailableException)
        {
            Log.Warn(
                $"[PlaylistSync] Playlist '{playlist.Name}' ({playlist.YoutubeId}) is unavailable. Downgrading to LocalOnly...");
            await DowngradeDeadCloudPlaylistAsync(playlist, ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistSync] Preview failed: {ex.Message}");
            if (ex is HttpRequestException)
            {
                playlist.IsCloudUnavailable = true;
                try
                {
                    await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);
                }
                catch
                {
                }
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
                    PlaylistSyncStrategy.ReplaceLocal => await ReplaceLocalTracksAsync(playlist, cloudData, ct)
                        .ConfigureAwait(false),
                    PlaylistSyncStrategy.ReplaceCloud => await ReplaceCloudTracksAsync(playlist, cloudData, ct)
                        .ConfigureAwait(false),
                    PlaylistSyncStrategy.Merge => await MergeTracksAsync(playlist, cloudData, ct).ConfigureAwait(false),
                    _ => (0, 0, 0, 0)
                };
            }

            if (cloudData != null)
            {
                if (ShouldUpdateViewCount(playlist.ViewCount, cloudData.ViewCount))
                    playlist.ViewCount = cloudData.ViewCount;

                if (!string.IsNullOrEmpty(cloudData.Author))
                    playlist.Author = cloudData.Author;

                if (!string.IsNullOrEmpty(cloudData.OwnerChannelId))
                    playlist.OwnerChannelId = cloudData.OwnerChannelId;

                if (cloudData.Visibility != PlaylistVisibility.Unknown)
                    playlist.Visibility = cloudData.Visibility;

                if (cloudData.ReleaseDate.HasValue)
                {
                    playlist.ReleaseDate = cloudData.ReleaseDate;
                }
                else if (playlist.ReleaseDate is { Month: 1, Day: 1 })
                {
                    playlist.ReleaseDate = null;
                }

                if (addedToCloud > 0 || removedFromCloud > 0 ||
                    (metadataChanged && options.Strategy != PlaylistSyncStrategy.ReplaceLocal))
                {
                    playlist.ReleaseDate = DateOnly.FromDateTime(DateTime.UtcNow);
                }

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

            var syncResult = new PlaylistSyncResult
            {
                Success = true,
                MetadataChanged = metadataChanged,
                TracksAddedLocally = addedLocally,
                TracksAddedToCloud = addedToCloud,
                TracksRemovedLocally = removedLocally,
                TracksRemovedFromCloud = removedFromCloud
            };

            Log.Info($"[PlaylistSync] Sync completed for '{playlist.Name}' ({playlist.YoutubeId}) [Strategy={options.Strategy}]: " +
                                 $"{syncResult.Summary}, views={playlist.ViewCount?.ToString() ?? "null"}, " +
                                 $"releaseDate={playlist.ReleaseDate?.ToString("yyyy-MM-dd") ?? "null"}");

            return syncResult;
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

            var ytId = await _youtube.CreatePlaylistAsync(
                playlist.Name,
                rawVideoIds.Count > 0 ? rawVideoIds : null,
                playlist.Description).ConfigureAwait(false);

            if (string.IsNullOrEmpty(ytId))
                throw new InvalidOperationException("YouTube returned empty playlist ID.");

            playlist.YoutubeId = ytId;
            playlist.SyncMode = PlaylistSyncMode.TwoWaySync;
            await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(playlist.ThumbnailUrl))
            {
                try
                {
                    await UploadThumbnailToYoutubeAsync(ytId, playlist.ThumbnailUrl, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error($"[PlaylistSync] Failed to upload thumbnail after upload: {ex.Message}");
                }
            }

            if (rawVideoIds.Count > 0)
            {
                try
                {
                    if (!await ct.DelayNoThrowAsync(1000, continueOnCapturedContext: false).ConfigureAwait(false))
                        return;
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
    /// Пакетно добавляет треки в облачный плейлист.
    /// </summary>
    public async Task AddTracksToCloudAsync(Playlist playlist, IReadOnlyList<string> trackIds,
        CancellationToken ct = default)
    {
        if (!playlist.CanSyncToCloud || string.IsNullOrEmpty(playlist.YoutubeId) || !_auth.IsAuthenticated)
            return;

        try
        {
            var newSetVideoIds =
                await _youtube.AddTracksToPlaylistAsync(playlist.YoutubeId, trackIds).ConfigureAwait(false);
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
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            await DowngradeForeignPlaylistOnForbiddenAsync(playlist, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistSync] Batch add tracks to cloud failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Получает сохраненный `setVideoId` или извлекает его через API YouTube, если он не сохранен.
    /// </summary>
    public async Task<string?> GetOrFetchSetVideoIdAsync(Playlist playlist, string trackId,
        CancellationToken ct = default)
    {
        if (!playlist.CanSyncToCloud || string.IsNullOrEmpty(playlist.YoutubeId) || !_auth.IsAuthenticated)
            return null;

        string? setVideoId = await _playlists.GetSetVideoIdAsync(playlist.Id, trackId, ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(setVideoId))
        {
            try
            {
                var fullData = await _youtube.GetFullPlaylistDataAsync(playlist.YoutubeId!, ct).ConfigureAwait(false);
                if (fullData is { IsEditable: false })
                {
                    await DowngradeForeignPlaylistOnForbiddenAsync(playlist, ct).ConfigureAwait(false);
                    return null;
                }

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
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                await DowngradeForeignPlaylistOnForbiddenAsync(playlist, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error($"[PlaylistSync] Failed to fetch setVideoIds: {ex.Message}");
            }
        }

        return setVideoId;
    }

    /// <summary>
    /// Пакетно удаляет группу треков из облачного плейлиста по их идентификаторам связи.
    /// </summary>
    /// <param name="playlist">Модель плейлиста.</param>
    /// <param name="setVideoIds">Коллекция идентификаторов связи видео в облачном плейлисте.</param>
    /// <returns>Задача, представляющая асинхронную операцию удаления треков.</returns>
    public async Task RemoveTracksFromCloudAsync(Playlist playlist, IReadOnlyList<string> setVideoIds)
    {
        if (!playlist.CanSyncToCloud || string.IsNullOrEmpty(playlist.YoutubeId) ||
            !_auth.IsAuthenticated || setVideoIds.Count == 0)
            return;

        try
        {
            await _youtube.RemoveTracksFromPlaylistAsync(playlist.YoutubeId, setVideoIds).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            await DowngradeForeignPlaylistOnForbiddenAsync(playlist, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistSync] Failed to remove tracks from cloud: {ex.Message}");
        }
    }

    /// <summary>
    /// Изменяет порядок трека в облачном плейлисте.
    /// </summary>
    public async Task MoveTrackInCloudAsync(Playlist playlist, string movingTrackId, int newIndex,
        List<string> newTrackIdsOrder, CancellationToken ct = default)
    {
        if (!playlist.CanSyncToCloud || string.IsNullOrEmpty(playlist.YoutubeId) || !_auth.IsAuthenticated)
            return;

        var movingSetVideoId =
            await _playlists.GetSetVideoIdAsync(playlist.Id, movingTrackId, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(movingSetVideoId)) return;

        string? predecessor = null;
        string? successor = null;

        if (newIndex == 0)
        {
            if (newTrackIdsOrder.Count > 1)
                successor = await _playlists.GetSetVideoIdAsync(playlist.Id, newTrackIdsOrder[1], ct)
                    .ConfigureAwait(false);
        }
        else
        {
            predecessor = await _playlists.GetSetVideoIdAsync(playlist.Id, newTrackIdsOrder[newIndex - 1], ct)
                .ConfigureAwait(false);
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
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                await DowngradeForeignPlaylistOnForbiddenAsync(playlist, ct).ConfigureAwait(false);
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
    private async Task DowngradeForeignPlaylistOnForbiddenAsync(Playlist playlist, CancellationToken ct)
    {
        if (playlist.Ownership == PlaylistOwnership.Foreign)
            return;

        playlist.Ownership = PlaylistOwnership.Foreign;
        await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);
        Log.Warn($"[PlaylistSync] Playlist '{playlist.Name}' ({playlist.YoutubeId}) rejected cloud mutation (403 Forbidden / non-editable). Marked as Foreign read-only.");
    }

    public async Task DowngradeDeadCloudPlaylistAsync(Playlist playlist, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(playlist);

        playlist.SyncMode = PlaylistSyncMode.LocalOnly;
        playlist.YoutubeId = null;
        playlist.IsCloudUnavailable = false;
        await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);

        Log.Warn(
            $"[PlaylistSync] Playlist '{playlist.Name}' (ID: {playlist.Id}) returned 404. Converted to LocalOnly.");

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
        bool isCloudSource = options.Strategy == PlaylistSyncStrategy.ReplaceLocal || !playlist.CanSyncToCloud;

        if (options.SyncName && preview.NameDiffers)
        {
            if (isCloudSource)
            {
                playlist.Name = preview.CloudName;
                changed = true;
            }
            else
            {
                try
                {
                    await _youtube.RenamePlaylistAsync(playlist.YoutubeId!, playlist.Name).ConfigureAwait(false);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    Log.Warn($"[PlaylistSync] 403 Forbidden on rename for '{playlist.YoutubeId}'. Reverting to Foreign read-only.");
                    playlist.Ownership = PlaylistOwnership.Foreign;
                    playlist.Name = preview.CloudName;
                    isCloudSource = true;
                    changed = true;
                }
                catch (Exception ex)
                {
                    Log.Error($"[PlaylistSync] Rename on YouTube failed: {ex.Message}");
                }
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
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    Log.Warn($"[PlaylistSync] 403 Forbidden on description edit for '{playlist.YoutubeId}'. Reverting to Foreign read-only.");
                    playlist.Ownership = PlaylistOwnership.Foreign;
                    playlist.Description = preview.CloudDescription ?? string.Empty;
                    isCloudSource = true;
                    changed = true;
                }
                catch (Exception ex)
                {
                    Log.Error($"[PlaylistSync] Description update on YouTube failed: {ex.Message}");
                }
            }
        }

        if (options.SyncThumbnail && preview.ThumbnailDiffers)
        {
            // При ReplaceLocal (или если локальная обложка пуста / находится на YouTube CDN) принимаем облачную обложку.
            // При ReplaceCloud или Merge с пользовательским локальным файлом выгружаем его в YouTube через Scotty.
            if (isCloudSource || string.IsNullOrEmpty(playlist.ThumbnailUrl) || ThumbnailComparer.IsYoutubeHosted(playlist.ThumbnailUrl))
            {
                if (!string.IsNullOrEmpty(preview.CloudThumbnailUrl))
                {
                    playlist.ThumbnailUrl = preview.CloudThumbnailUrl;
                    playlist.ETag = ThumbnailComparer.Normalize(playlist.ThumbnailUrl);
                    playlist.ComputedColor = null;
                    changed = true;
                }
            }
            else
            {
                try
                {
                    var uploaded = await UploadThumbnailToYoutubeAsync(playlist.YoutubeId!, playlist.ThumbnailUrl, ct)
                        .ConfigureAwait(false);
                    if (uploaded)
                    {
                        playlist.ETag = ThumbnailComparer.Normalize(playlist.ThumbnailUrl);
                        changed = true;
                    }
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    Log.Warn($"[PlaylistSync] 403 Forbidden on thumbnail upload for '{playlist.YoutubeId}'. Reverting to Foreign read-only.");
                    playlist.Ownership = PlaylistOwnership.Foreign;
                    if (!string.IsNullOrEmpty(preview.CloudThumbnailUrl))
                    {
                        playlist.ThumbnailUrl = preview.CloudThumbnailUrl;
                        playlist.ETag = ThumbnailComparer.Normalize(playlist.ThumbnailUrl);
                        playlist.ComputedColor = null;
                    }
                    changed = true;
                }
                catch (Exception ex)
                {
                    Log.Error($"[PlaylistSync] Thumbnail upload failed: {ex.Message}");
                }
            }
        }

        return changed;
    }

    private async Task<(int AddedLocally, int AddedToCloud, int RemovedLocally, int RemovedFromCloud)>
        ReplaceLocalTracksAsync(Playlist playlist, FullPlaylistSyncData fullData, CancellationToken ct)
    {
        var localTrackIds = await _playlists.GetTrackIdsAsync(playlist.Id, CurrentOwnerId, ct).ConfigureAwait(false);

        int removedLocally = 0;
        for (int i = 0; i < localTrackIds.Count; i++)
        {
            var removedId = localTrackIds[i];
            await _playlists.RemoveTrackAsync(playlist.Id, removedId, CurrentOwnerId, ct).ConfigureAwait(false);
            if (_registry.TryGet(removedId) is { } existingTrack)
            {
                existingTrack.InPlaylists.Remove(playlist.Id);
                _registry.UpdatePinStatus(existingTrack);
            }
            removedLocally++;
        }

        int addedLocally = 0;
        var mappings = new List<(string TrackId, string SetVideoId)>(fullData.Tracks.Count);

        for (int i = 0; i < fullData.Tracks.Count; i++)
        {
            var remote = fullData.Tracks[i];
            var track = CreateTrackInfo(remote);
            var canonical = _registry.RegisterOrUpdate(track);
            canonical.InPlaylists.Add(playlist.Id);
            _registry.UpdatePinStatus(canonical);

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

        var localRawIdMap = new Dictionary<string, string>(localTrackIds.Count, StringComparer.Ordinal);
        for (int i = 0; i < localTrackIds.Count; i++)
        {
            var localId = localTrackIds[i];
            if (!localId.StartsWith("yt_", StringComparison.Ordinal))
                continue;

            var raw = YoutubeIdHelper.ExtractRawId(localId);
            if (!string.IsNullOrEmpty(raw))
                localRawIdMap.TryAdd(raw, localId);
        }

        var toRemoveSetVideoIds = new List<string>();
        var currentCloudTracks = new List<RemoteTrackInfo>(fullData.Tracks.Count);
        var trackToSetVideoId = new Dictionary<string, string>(localRawIdMap.Count, StringComparer.Ordinal);
        var cloudRawIdSet = new HashSet<string>(fullData.Tracks.Count, StringComparer.Ordinal);

        for (int i = 0; i < fullData.Tracks.Count; i++)
        {
            var cloudTrack = fullData.Tracks[i];
            var cloudRawId = YoutubeIdHelper.ExtractRawId(cloudTrack.VideoId);
            bool hasValidSetId = !string.IsNullOrEmpty(cloudTrack.SetVideoId) &&
                                 !cloudTrack.SetVideoId.Equals("to_be_updated_by_client", StringComparison.OrdinalIgnoreCase);

            if (localRawIdMap.TryGetValue(cloudRawId, out var originalLocalId))
            {
                if (cloudRawIdSet.Add(cloudRawId))
                {
                    currentCloudTracks.Add(cloudTrack);
                    if (hasValidSetId)
                        trackToSetVideoId[originalLocalId] = cloudTrack.SetVideoId;
                }
                else if (hasValidSetId)
                {
                    // Лишний дубликат того же трека в облаке подлежит удалению
                    toRemoveSetVideoIds.Add(cloudTrack.SetVideoId);
                }
            }
            else if (hasValidSetId)
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

        var toUpload = new List<string>();
        for (int i = 0; i < localTrackIds.Count; i++)
        {
            var localId = localTrackIds[i];
            if (!localId.StartsWith("yt_", StringComparison.Ordinal))
                continue;

            var raw = YoutubeIdHelper.ExtractRawId(localId);
            if (!string.IsNullOrEmpty(raw) && !cloudRawIdSet.Contains(raw))
                toUpload.Add(localId);
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
            var mappings = new List<(string TrackId, string SetVideoId)>(trackToSetVideoId.Count);
            foreach (var kvp in trackToSetVideoId)
                mappings.Add((kvp.Key, kvp.Value));

            await _playlists.UpdateSetVideoIdsAsync(playlist.Id, mappings, ct).ConfigureAwait(false);
        }

        var simulatedCloudOrder = new List<string>(currentCloudTracks.Count + toUpload.Count);
        for (int i = 0; i < currentCloudTracks.Count; i++)
        {
            var sid = currentCloudTracks[i].SetVideoId;
            if (!string.IsNullOrEmpty(sid))
                simulatedCloudOrder.Add(sid);
        }

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
                canonical.InPlaylists.Add(playlist.Id);
                _registry.UpdatePinStatus(canonical);
                await _tracks.UpsertAsync(canonical, ct).ConfigureAwait(false);
                await _playlists.AddTrackAsync(playlist.Id, canonical.Id, CurrentOwnerId, null, ct)
                    .ConfigureAwait(false);
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

    /// <summary>
    /// Определяет, нужно ли обновлять локальный счётчик просмотров значением из облака.
    /// Защищает точное число просмотров (полученное из YouTube Web) от затирания округлённым до сотен/тысяч значением из WEB_REMIX.
    /// </summary>
    private static bool ShouldUpdateViewCount(long? currentViews, long? cloudViews)
    {
        if (cloudViews is null)
            return currentViews is not null;

        if (currentViews is null or <= 0)
            return true;

        long cur = currentViews.Value;
        long cloud = cloudViews.Value;
        if (cur == cloud)
            return false;

        // Если в WEB_REMIX пришло округлённое значение (кратное 100 или 1000), а локально уже хранится точное число в том же диапазоне — сохраняем точное
        if (cloud >= 1_000 && cloud % 100 == 0 && cur % 100 != 0)
        {
            long step = cloud >= 10_000 ? 1_000 : 100;
            if (Math.Abs(cur - cloud) < step)
                return false;
        }

        return true;
    }

    private static bool IsLocalCoverAlreadySyncedWithCloud(
        Playlist playlist,
            string? cloudThumbnailUrl,
            out string? normalizedLocal)
    {
        normalizedLocal = ThumbnailComparer.Normalize(playlist.ThumbnailUrl);
        if (string.IsNullOrEmpty(normalizedLocal) || string.IsNullOrEmpty(cloudThumbnailUrl))
            return false;

        if (ThumbnailComparer.IsYoutubeHosted(normalizedLocal))
            return true;

        if (playlist.LastSyncedAtUtc is not { } lastSyncUtc)
            return false;

        string? localPath = null;
        if (normalizedLocal.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(normalizedLocal, UriKind.Absolute, out var uri))
                localPath = uri.LocalPath;
        }
        else if (Path.IsPathRooted(normalizedLocal))
        {
            localPath = normalizedLocal;
        }

        if (!string.IsNullOrEmpty(localPath))
        {
            if (!File.Exists(localPath))
                return false;

            var lastWriteUtc = File.GetLastWriteTimeUtc(localPath);
            if (lastWriteUtc > lastSyncUtc.AddSeconds(5))
                return false;

            return string.IsNullOrEmpty(playlist.ETag) ||
                   string.Equals(playlist.ETag, normalizedLocal, StringComparison.OrdinalIgnoreCase);
        }

        return !string.IsNullOrEmpty(playlist.ETag) &&
               string.Equals(playlist.ETag, normalizedLocal, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> UploadThumbnailToYoutubeAsync(string youtubePlaylistId, string thumbnailUrl,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(thumbnailUrl) || string.IsNullOrWhiteSpace(youtubePlaylistId))
            return false;

        // Обложки YouTube CDN (ytimg, ggpht, googleusercontent, gstatic) уже находятся на серверах Google и не подлежат повторной Scotty-выгрузке
        if (ThumbnailComparer.IsYoutubeHosted(thumbnailUrl))
        {
            Log.Debug($"[PlaylistSync] Skipping upload for YouTube-hosted thumbnail on playlist {youtubePlaylistId}");
            return false;
        }

        byte[] imageData;

        try
        {
            if (thumbnailUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                thumbnailUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                imageData = await _networkManager.ImageClient.GetByteArrayAsync(thumbnailUrl, linkedCts.Token)
                    .ConfigureAwait(false);
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
        }
        catch (HttpRequestException ex)
        {
            Log.Warn($"[PlaylistSync] Thumbnail source unavailable (HTTP {(int?)ex.StatusCode}): {ex.Message}");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"[PlaylistSync] Failed to read thumbnail data for upload: {ex.Message}");
            return false;
        }

        if (imageData.Length == 0 || imageData.Length > 20 * 1024 * 1024)
            return false;

        return await _youtube.UploadPlaylistThumbnailAsync(youtubePlaylistId, imageData).ConfigureAwait(false);
    }
}
