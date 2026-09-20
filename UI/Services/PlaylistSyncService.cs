using LMP.Core.Services;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Music;
using LMP.Core.Youtube.Search;
using LMP.Core.Youtube.Utils;
using LMP.UI.Dialogs;

namespace LMP.UI.Services;

/// <summary>
/// Централизованный сервис синхронизации и CRUD-операций плейлистов между локальной БД и YouTube.
/// Является единственным авторитетным источником синхронизации и мутаций облачных плейлистов.
/// </summary>
public sealed class PlaylistSyncService
{
    private readonly INetworkManager _networkManager;
    private readonly LibraryService _library;
    private readonly YoutubeProvider _youtube;
    private readonly CookieAuthService _auth;
    private readonly DialogService _dialog;
    private readonly YoutubeUserDataService _ytUser;

    /// <summary>
    /// Кэшированный снимок плейлиста из BuildPreviewAsync.
    /// Переиспользуется в ApplyAsync чтобы не делать повторный запрос.
    /// </summary>
    private FullPlaylistSyncData? _cachedSyncData;

    private static LocalizationService SL => LocalizationService.Instance;

    public PlaylistSyncService(
        INetworkManager networkManager,
        LibraryService library,
        YoutubeProvider youtube,
        CookieAuthService auth,
        DialogService dialog,
        YoutubeUserDataService ytUser)
    {
        _networkManager = networkManager;
        _library = library;
        _youtube = youtube;
        _auth = auth;
        _dialog = dialog;
        _ytUser = ytUser;
    }

    #region Public Sync API

    /// <summary>
    /// Полный цикл синхронизации: preview → диалог → применение.
    /// Снимок плейлиста загружается ОДИН раз и переиспользуется в Apply.
    /// </summary>
    public async Task<PlaylistSyncResult?> SyncWithDialogAsync(
        string playlistId,
        CancellationToken ct = default)
    {
        var playlist = await _library.GetPlaylistAsync(playlistId, ct);
        if (playlist == null)
            return PlaylistSyncResult.Fail("Playlist not found");

        if (playlist.SyncMode != PlaylistSyncMode.TwoWaySync ||
            string.IsNullOrEmpty(playlist.YoutubeId))
        {
            return PlaylistSyncResult.Fail(
                SL["Playlist_SyncNotLinked"] ?? "Playlist is not linked to YouTube");
        }

        if (!_auth.IsAuthenticated)
            return PlaylistSyncResult.Fail(
                SL["Playlist_SyncNotAuth"] ?? "Not authenticated");

        _cachedSyncData = null;

        var preview = await BuildPreviewAsync(playlist, ct);
        if (preview == null)
            return PlaylistSyncResult.Fail(
                SL["Playlist_SyncFetchFailed"] ?? "Failed to fetch playlist data from YouTube");

        if (!preview.HasAnyDifference)
        {
            await _dialog.ShowInfoAsync(
                SL["Playlist_SyncWithCloud"] ?? "Sync",
                SL["Playlist_SyncNoChanges"] ?? "Playlist is already in sync");
            return PlaylistSyncResult.NoChanges();
        }

        var options = await _dialog.ShowPlaylistSyncDialogAsync(preview);
        if (options == null)
            return null;

        return await ApplyAsync(playlist, preview, options, ct);
    }

    /// <summary>
    /// Синхронизация без диалога — используется при первичной привязке плейлиста к YouTube.
    /// </summary>
    public async Task<PlaylistSyncResult> SyncDirectAsync(
        string playlistId,
        PlaylistSyncOptions options,
        CancellationToken ct = default)
    {
        var playlist = await _library.GetPlaylistAsync(playlistId, ct);
        if (playlist == null)
            return PlaylistSyncResult.Fail("Playlist not found");

        if (string.IsNullOrEmpty(playlist.YoutubeId))
            return PlaylistSyncResult.Fail("No YouTube ID");

        _cachedSyncData = null;

        var preview = await BuildPreviewAsync(playlist, ct);
        if (preview == null)
            return PlaylistSyncResult.Fail("Failed to fetch YouTube data");

        return await ApplyAsync(playlist, preview, options, ct);
    }

    /// <summary>
    /// Синхронизирует понравившиеся треки из YouTube в локальный плейлист "Liked".
    /// </summary>
    /// <param name="ct">Токен отмены операции.</param>
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

    #region Single Playlist CRUD & Item Mutations

    /// <summary>
    /// Добавляет трек в плейлист с транзакционной синхронизацией в облако.
    /// </summary>
    public async Task AddTrackToPlaylistAsync(
        string playlistId,
        TrackInfo track,
        CancellationToken ct = default)
    {
        var playlist = await _library.GetPlaylistAsync(playlistId, ct);
        if (playlist == null) return;

        if (!playlist.CanEditTracks)
        {
            Log.Warn($"[PlaylistSync] Cannot add track to read-only playlist '{playlistId}' (Ownership={playlist.Ownership})");
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
                Log.Error($"[PlaylistSync] Add track to cloud failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Удаляет трек из плейлиста с транзакционной синхронизацией в облако.
    /// </summary>
    public async Task RemoveTrackFromPlaylistAsync(
        string playlistId,
        string trackId,
        CancellationToken ct = default)
    {
        var playlist = await _library.GetPlaylistAsync(playlistId, ct);

        if (playlist is { CanEditTracks: false })
        {
            Log.Warn($"[PlaylistSync] Cannot remove track from read-only playlist '{playlistId}' (Ownership={playlist.Ownership})");
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
                Log.Info($"[PlaylistSync] No cached setVideoId for {trackId}, fetching from YouTube...");
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

                        Log.Info($"[PlaylistSync] Fetched {tracks.Count} setVideoIds, target: {setVideoId ?? "not found"}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"[PlaylistSync] Failed to fetch setVideoIds: {ex.Message}");
                }
            }
        }

        await _library.RemoveTrackFromPlaylistAsync(trackId, playlistId, ct);

        if (needsYoutubeSync)
        {
            if (!string.IsNullOrEmpty(setVideoId))
            {
                try
                {
                    await _youtube.RemoveFromPlaylistAsync(playlist!.YoutubeId!, setVideoId);
                    Log.Info($"[PlaylistSync] Removed track {trackId} from YouTube playlist {playlist.YoutubeId}");
                }
                catch (Exception ex)
                {
                    Log.Error($"[PlaylistSync] Failed to remove track from cloud: {ex.Message}");
                }
            }
            else
            {
                Log.Warn($"[PlaylistSync] No setVideoId for track {trackId} in playlist {playlistId} — YouTube removal skipped");
            }
        }
    }

    /// <summary>
    /// Перемещает трек внутри плейлиста с синхронизацией позиции в YouTube.
    /// </summary>
    public async Task MovePlaylistTrackAsync(
        string playlistId,
        int oldIndex,
        int newIndex,
        CancellationToken ct = default)
    {
        if (oldIndex == newIndex) return;

        var playlist = await _library.GetPlaylistAsync(playlistId, ct);
        var trackIds = await _library.GetPlaylistTrackIdsAsync(playlistId, ct);

        if (oldIndex < 0 || oldIndex >= trackIds.Count || newIndex < 0 || newIndex >= trackIds.Count)
            return;

        var movingTrackId = trackIds[oldIndex];

        // 1. Локальное перемещение
        await _library.MoveTrackInPlaylistAsync(playlistId, oldIndex, newIndex, ct);

        // 2. Облачная синхронизация
        if (playlist != null &&
            playlist.SyncMode == PlaylistSyncMode.TwoWaySync &&
            !string.IsNullOrEmpty(playlist.YoutubeId) &&
            _auth.IsAuthenticated)
        {
            var movingSetVideoId = await _library.GetSetVideoIdAsync(playlistId, movingTrackId, ct);
            if (!string.IsNullOrEmpty(movingSetVideoId))
            {
                trackIds.RemoveAt(oldIndex);
                trackIds.Insert(newIndex, movingTrackId);

                string? predecessor = null;
                string? successor = null;

                if (newIndex == 0)
                {
                    if (trackIds.Count > 1)
                        successor = await _library.GetSetVideoIdAsync(playlistId, trackIds[1], ct);
                }
                else
                {
                    predecessor = await _library.GetSetVideoIdAsync(playlistId, trackIds[newIndex - 1], ct);
                }

                if (!string.IsNullOrEmpty(predecessor) || !string.IsNullOrEmpty(successor))
                {
                    try
                    {
                        await _youtube.MoveTracksInPlaylistAsync(
                            playlist.YoutubeId!,
                            [(movingSetVideoId, predecessor, successor)],
                            ct);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[PlaylistSync] Remote move sync failed: {ex.Message}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Удаляет плейлист локально и опционально из аккаунта YouTube.
    /// </summary>
    public async Task DeletePlaylistAsync(
        string playlistId,
        bool deleteFromCloud = false,
        CancellationToken ct = default)
    {
        var playlist = await _library.GetPlaylistAsync(playlistId, ct);
        if (playlist == null) return;

        await _library.DeletePlaylistAsync(playlistId, ct);

        if (deleteFromCloud
            && playlist.SyncMode == PlaylistSyncMode.TwoWaySync
            && !string.IsNullOrEmpty(playlist.YoutubeId)
            && _auth.IsAuthenticated)
        {
            try
            {
                await _youtube.DeletePlaylistAsync(playlist.YoutubeId);
            }
            catch (Exception ex)
            {
                Log.Error($"[PlaylistSync] Error deleting remote playlist: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Создаёт копию облачного плейлиста в локальном режиме.
    /// </summary>
    public async Task ConvertToLocalAsync(
        string playlistId,
        CancellationToken ct = default)
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

    /// <summary>
    /// Объединяет треки из исходного плейлиста в целевой локальный плейлист.
    /// </summary>
    public async Task<bool> MergePlaylistsAsync(
        string sourceId,
        string targetId,
        CancellationToken ct = default)
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

        Log.Info($"[PlaylistSync] Added {added} tracks from '{source.Name}' to '{target.Name}'");
        return true;
    }

    /// <summary>
    /// Выгружает локальный плейлист в аккаунт YouTube с двухсторонней привязкой.
    /// </summary>
    public async Task UploadPlaylistToAccountAsync(
        string localPlaylistId,
        CancellationToken ct = default)
    {
        if (!_auth.IsAuthenticated) return;

        var localPl = await _library.GetPlaylistAsync(localPlaylistId, ct);
        if (localPl == null || localPl.SyncMode != PlaylistSyncMode.LocalOnly) return;

        try
        {
            var trackIds = await _library.GetPlaylistTrackIdsAsync(localPlaylistId, ct);

            var rawVideoIds = new List<string>(trackIds.Count);
            for (int i = 0; i < trackIds.Count; i++)
            {
                var id = trackIds[i];
                if (id.StartsWith("yt_", StringComparison.Ordinal))
                {
                    rawVideoIds.Add(YoutubeIdHelper.ExtractRawId(id));
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
                try
                {
                    await Task.Delay(1000, ct);

                    var fullData = await _youtube.GetFullPlaylistDataAsync(ytId, ct);
                    if (fullData?.Tracks is { Count: > 0 } tracks)
                    {
                        var mappings = new List<(string TrackId, string SetVideoId)>(tracks.Count);
                        for (int i = 0; i < tracks.Count; i++)
                            mappings.Add(("yt_" + tracks[i].VideoId, tracks[i].SetVideoId));

                        await _library.UpdateSetVideoIdsAsync(localPlaylistId, mappings, ct);
                        Log.Info($"[PlaylistSync] Persisted {mappings.Count} setVideoIds for uploaded playlist {ytId}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"[PlaylistSync] Failed to fetch setVideoIds after upload: {ex.Message}");
                }
            }

            Log.Info($"[PlaylistSync] Uploaded playlist '{localPl.Name}' with {rawVideoIds.Count} tracks to {ytId}");
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistSync] Upload failed: {ex.Message}");
        }
    }

    #endregion

    #region Preview (Diff)

    /// <summary>
    /// Строит снимок различий между локальным и облачным состоянием плейлиста.
    /// Нормализует 11-значные идентификаторы треков и диагностирует единичные несовпадения.
    /// </summary>
    private async Task<PlaylistSyncPreview?> BuildPreviewAsync(
        Playlist playlist,
        CancellationToken ct)
    {
        try
        {
            YoutubeProvider.ThrowIfInCooldown();

            var fullDataTask = GetOrFetchFullDataAsync(playlist.YoutubeId!, ct);
            var localTrackIdsTask = _library.GetPlaylistTrackIdsAsync(playlist.Id, ct);

            await Task.WhenAll(fullDataTask, localTrackIdsTask);

            var fullData = await fullDataTask;
            var localTrackIds = await localTrackIdsTask;

            if (fullData == null)
            {
                playlist.IsCloudUnavailable = true;
                await _library.AddOrUpdatePlaylistAsync(playlist, ct);
                return null;
            }

            _cachedSyncData = fullData;

            if (playlist.IsCloudUnavailable)
            {
                playlist.IsCloudUnavailable = false;
                await _library.AddOrUpdatePlaylistAsync(playlist, ct);
            }

            // Нормализуем облачные ID до канонического 11-значного rawId
            var cloudVideoIds = new HashSet<string>(fullData.Tracks.Count, StringComparer.Ordinal);
            for (int i = 0; i < fullData.Tracks.Count; i++)
            {
                var rawId = YoutubeIdHelper.ExtractRawId(fullData.Tracks[i].VideoId);
                if (!string.IsNullOrEmpty(rawId))
                    cloudVideoIds.Add(rawId);
            }

            // Нормализуем локальные ID (отсекая yt_, yt_pl_ и возможные параметры)
            var localIdSet = new HashSet<string>(localTrackIds.Count, StringComparer.Ordinal);
            for (int i = 0; i < localTrackIds.Count; i++)
            {
                var rawId = YoutubeIdHelper.ExtractRawId(localTrackIds[i]);
                if (!string.IsNullOrEmpty(rawId))
                    localIdSet.Add(rawId);
            }

            int commonCount = 0;
            int cloudOnlyCount = 0;

            for (int i = 0; i < fullData.Tracks.Count; i++)
            {
                var rawId = YoutubeIdHelper.ExtractRawId(fullData.Tracks[i].VideoId);
                if (localIdSet.Contains(rawId))
                {
                    commonCount++;
                }
                else
                {
                    cloudOnlyCount++;
                    Log.Warn($"[PlaylistSync] Несовпадение (есть только в облаке): rawId='{rawId}', title='{fullData.Tracks[i].Title}'");
                }
            }

            int localOnlyCount = 0;
            for (int i = 0; i < localTrackIds.Count; i++)
            {
                var rawId = YoutubeIdHelper.ExtractRawId(localTrackIds[i]);
                if (!cloudVideoIds.Contains(rawId))
                {
                    localOnlyCount++;
                    Log.Warn($"[PlaylistSync] Несовпадение (есть только локально): trackId='{localTrackIds[i]}', rawId='{rawId}'");
                }
            }

            Log.Debug($"[PlaylistSync] Diff: common={commonCount}, " +
                      $"cloudOnly={cloudOnlyCount}, localOnly={localOnlyCount}");

            bool isThumbnailSynced = playlist.LastSyncedAtUtc.HasValue &&
                                     playlist.UpdatedAt <= playlist.LastSyncedAtUtc.Value.AddSeconds(5) &&
                                     !string.IsNullOrEmpty(fullData.ThumbnailUrl);

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
                IsThumbnailAlreadySynced = isThumbnailSynced,
                YoutubePlaylistId = playlist.YoutubeId
            };
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistSync] Preview failed: {ex.Message}");

            if (ex is PlaylistUnavailableException or HttpRequestException)
            {
                playlist.IsCloudUnavailable = true;
                try { await _library.AddOrUpdatePlaylistAsync(playlist, ct); } catch { }
            }

            return null;
        }
    }

    #endregion

    #region Apply Strategy

    /// <summary>
    /// Применяет выбранную стратегию синхронизации.
    /// После успешного завершения обновляет <see cref="Playlist.LastSyncedAtUtc"/>
    /// и сбрасывает <see cref="Playlist.IsCloudUnavailable"/>.
    /// </summary>
    private async Task<PlaylistSyncResult> ApplyAsync(
        Playlist playlist,
        PlaylistSyncPreview preview,
        PlaylistSyncOptions options,
        CancellationToken ct)
    {
        try
        {
            bool metadataChanged = false;
            int tracksAddedLocally = 0;
            int tracksAddedToCloud = 0;
            int tracksRemovedLocally = 0;
            int tracksRemovedFromCloud = 0;

            metadataChanged = await SyncMetadataAsync(playlist, preview, options, ct);

            if (options.SyncTracks)
            {
                (tracksAddedLocally, tracksAddedToCloud, tracksRemovedLocally, tracksRemovedFromCloud)
                    = options.Strategy switch
                    {
                        PlaylistSyncStrategy.ReplaceLocal =>
                            await ReplaceLocalTracksAsync(playlist, ct),
                        PlaylistSyncStrategy.ReplaceCloud =>
                            await ReplaceCloudTracksAsync(playlist, ct),
                        PlaylistSyncStrategy.Merge =>
                            await MergeTracksAsync(playlist, ct),
                        _ => (0, 0, 0, 0)
                    };
            }

            // Захватываем кэш до его обнуления (исправление бага с пустыми датами/просмотрами при merge)
            var cloudData = _cachedSyncData;
            _cachedSyncData = null;

            // Cloud-only stats (views, date) — pull-only из YouTube
            if (cloudData != null)
            {
                if (cloudData.ViewCount.HasValue && playlist.ViewCount != cloudData.ViewCount)
                    playlist.ViewCount = cloudData.ViewCount;

                if (playlist.ReleaseDate != cloudData.ReleaseDate)
                    playlist.ReleaseDate = cloudData.ReleaseDate;
            }

            // Sync state: всегда обновляем после успешного завершения
            playlist.LastSyncedAtUtc = DateTime.UtcNow;
            playlist.IsCloudUnavailable = false;
            playlist.UpdatedAt = DateTime.Now;
            await _library.AddOrUpdatePlaylistAsync(playlist, ct);

            var result = new PlaylistSyncResult
            {
                Success = true,
                MetadataChanged = metadataChanged,
                TracksAddedLocally = tracksAddedLocally,
                TracksAddedToCloud = tracksAddedToCloud,
                TracksRemovedLocally = tracksRemovedLocally,
                TracksRemovedFromCloud = tracksRemovedFromCloud
            };

            Log.Info($"[PlaylistSync] Completed: {result.Summary}");
            return result;
        }
        catch (Exception ex)
        {
            _cachedSyncData = null;
            Log.Error($"[PlaylistSync] Apply failed: {ex.Message}");
            return PlaylistSyncResult.Fail(ex.Message);
        }
    }

    #endregion

    #region Metadata Sync

    /// <summary>
    /// Синхронизирует метаданные по выбранным полям.
    /// </summary>
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
                Log.Info($"[PlaylistSync] Name updated locally: '{preview.CloudName}'");
            }
            else
            {
                try
                {
                    await _youtube.RenamePlaylistAsync(playlist.YoutubeId!, playlist.Name);
                    Log.Info($"[PlaylistSync] Name updated in YouTube: '{playlist.Name}'");
                }
                catch (Exception ex)
                {
                    Log.Error($"[PlaylistSync] Rename in YouTube failed: {ex.Message}");
                }
            }
        }

        if (options.SyncDescription && preview.DescriptionDiffers)
        {
            if (isCloudSource)
            {
                playlist.Description = preview.CloudDescription ?? string.Empty;
                changed = true;
                Log.Info("[PlaylistSync] Description updated locally");
            }
            else
            {
                try
                {
                    var client = _youtube.GetClient();
                    var targetDesc = playlist.Description?.Trim() ?? string.Empty;
                    await client.Mutations.SetPlaylistDescriptionAsync(playlist.YoutubeId!, targetDesc, ct);
                    playlist.Description = targetDesc;
                    Log.Info("[PlaylistSync] Description updated in YouTube");
                }
                catch (Exception ex)
                {
                    Log.Error($"[PlaylistSync] Description update in YouTube failed: {ex.Message}");
                }
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
                    Log.Info("[PlaylistSync] Thumbnail updated locally from YouTube");
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(playlist.ThumbnailUrl))
                {
                    try
                    {
                        var success = await UploadThumbnailToYoutubeAsync(
                            playlist.YoutubeId!, playlist.ThumbnailUrl, ct);

                        if (success)
                        {
                            Log.Info("[PlaylistSync] Thumbnail uploaded to YouTube");
                            // Привязываем локальный URL к студийной обложке YouTube, чтобы исключить повторную заливку
                            if (!string.IsNullOrEmpty(preview.CloudThumbnailUrl))
                            {
                                playlist.ThumbnailUrl = preview.CloudThumbnailUrl;
                                changed = true;
                            }
                        }
                        else
                        {
                            Log.Warn("[PlaylistSync] Thumbnail upload to YouTube skipped or failed");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[PlaylistSync] Thumbnail upload failed: {ex.Message}");
                    }
                }
            }
        }

        return changed;
    }

    #endregion

    #region Track Sync Strategies

    /// <summary>
    /// YouTube → Local: полностью заменить локальные треки облачными.
    /// </summary>
    private async Task<(int AddedLocally, int AddedToCloud, int RemovedLocally, int RemovedFromCloud)>
        ReplaceLocalTracksAsync(Playlist playlist, CancellationToken ct)
    {
        var youtubeId = playlist.YoutubeId!;

        var fullDataTask = GetOrFetchFullDataAsync(youtubeId, ct);
        var localTrackIdsTask = _library.GetPlaylistTrackIdsAsync(playlist.Id, ct);

        await Task.WhenAll(fullDataTask, localTrackIdsTask);

        var fullData = await fullDataTask;
        var localTrackIds = await localTrackIdsTask;

        if (fullData == null)
        {
            Log.Error("[PlaylistSync] ReplaceLocal: failed to fetch cloud data");
            return (0, 0, 0, 0);
        }

        int removedLocally = 0;
        for (int i = 0; i < localTrackIds.Count; i++)
        {
            await _library.RemoveTrackFromPlaylistAsync(localTrackIds[i], playlist.Id, ct);
            removedLocally++;
        }

        int addedLocally = 0;
        var mappings = new List<(string TrackId, string SetVideoId)>(fullData.Tracks.Count);

        for (int i = 0; i < fullData.Tracks.Count; i++)
        {
            var remote = fullData.Tracks[i];
            var track = CreateTrackInfo(remote);

            await _library.AddOrUpdateTrackAsync(track, ct);
            await _library.AddTrackToPlaylistAsync(track, playlist.Id, ct);
            addedLocally++;

            if (!string.IsNullOrEmpty(remote.SetVideoId))
                mappings.Add(("yt_" + remote.VideoId, remote.SetVideoId));
        }

        if (mappings.Count > 0)
            await _library.UpdateSetVideoIdsAsync(playlist.Id, mappings, ct);

        Log.Info($"[PlaylistSync] ReplaceLocal: removed={removedLocally}, added={addedLocally}");
        return (addedLocally, 0, removedLocally, 0);
    }

    /// <summary>
    /// Local → YouTube: полностью заменить облачные треки локальными.
    /// </summary>
    private async Task<(int AddedLocally, int AddedToCloud, int RemovedLocally, int RemovedFromCloud)>
        ReplaceCloudTracksAsync(Playlist playlist, CancellationToken ct)
    {
        var youtubeId = playlist.YoutubeId!;

        var fullDataTask = GetOrFetchFullDataAsync(youtubeId, ct);
        var localTrackIdsTask = _library.GetPlaylistTrackIdsAsync(playlist.Id, ct);

        await Task.WhenAll(fullDataTask, localTrackIdsTask);

        var fullData = await fullDataTask;
        var localTrackIds = await localTrackIdsTask;

        if (fullData == null)
        {
            Log.Error("[PlaylistSync] ReplaceCloud: failed to fetch cloud data");
            return (0, 0, 0, 0);
        }

        // Строим нормализованный справочник локальных треков (rawId -> оригинальный trackId)
        var localRawIdMap = new Dictionary<string, string>(localTrackIds.Count, StringComparer.Ordinal);
        for (int i = 0; i < localTrackIds.Count; i++)
        {
            var raw = YoutubeIdHelper.ExtractRawId(localTrackIds[i]);
            if (!string.IsNullOrEmpty(raw))
                localRawIdMap.TryAdd(raw, localTrackIds[i]);
        }

        // 1. Cloud-only треки -> удаляем из YouTube
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
            await _youtube.RemoveTracksFromPlaylistAsync(youtubeId, toRemoveSetVideoIds);
            removedFromCloud = toRemoveSetVideoIds.Count;
        }

        // 2. Текущее состояние облака после удаления
        var currentCloudTracks = fullData.Tracks
            .Where(t => localRawIdMap.ContainsKey(YoutubeIdHelper.ExtractRawId(t.VideoId)))
            .ToList();

        var trackToSetVideoId = new Dictionary<string, string>(localTrackIds.Count, StringComparer.Ordinal);
        for (int i = 0; i < currentCloudTracks.Count; i++)
        {
            var t = currentCloudTracks[i];
            var raw = YoutubeIdHelper.ExtractRawId(t.VideoId);
            if (!string.IsNullOrEmpty(t.SetVideoId) && localRawIdMap.TryGetValue(raw, out var originalLocalId))
            {
                trackToSetVideoId[originalLocalId] = t.SetVideoId;
            }
        }

        // 3. Local-only треки -> добавляем в YouTube
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
            {
                toUpload.Add(localTrackIds[i]);
            }
        }

        int addedToCloud = 0;
        if (toUpload.Count > 0)
        {
            var newSetVideoIds = await _youtube.AddTracksToPlaylistAsync(youtubeId, toUpload);
            addedToCloud = toUpload.Count;

            for (int i = 0; i < newSetVideoIds.Count && i < toUpload.Count; i++)
            {
                if (!string.IsNullOrEmpty(newSetVideoIds[i]))
                    trackToSetVideoId[toUpload[i]] = newSetVideoIds[i]!;
            }
        }

        // Сохраняем актуальные SetVideoId в SQLite
        if (trackToSetVideoId.Count > 0)
        {
            var mappings = trackToSetVideoId.Select(kvp => (kvp.Key, kvp.Value)).ToList();
            await _library.UpdateSetVideoIdsAsync(playlist.Id, mappings, ct);
        }

        // 4. Выравнивание порядка треков в облаке под локальный плейлист
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
            await _youtube.MoveTracksInPlaylistAsync(youtubeId, reorderMoves, ct);
            Log.Info($"[PlaylistSync] Applied {reorderMoves.Count} reorder move(s) to match local playlist order");
        }

        Log.Info($"[PlaylistSync] ReplaceCloud: removed={removedFromCloud}, added={addedToCloud}, reordered={reorderMoves.Count}");
        return (0, addedToCloud, 0, removedFromCloud);
    }

    /// <summary>
    /// Двусторонний merge: добавить отсутствующие треки в обе стороны без удаления.
    /// </summary>
    private async Task<(int AddedLocally, int AddedToCloud, int RemovedLocally, int RemovedFromCloud)>
        MergeTracksAsync(Playlist playlist, CancellationToken ct)
    {
        var youtubeId = playlist.YoutubeId!;

        var fullDataTask = GetOrFetchFullDataAsync(youtubeId, ct);
        var localTrackIdsTask = _library.GetPlaylistTrackIdsAsync(playlist.Id, ct);

        await Task.WhenAll(fullDataTask, localTrackIdsTask);

        var fullData = await fullDataTask;
        var localTrackIds = await localTrackIdsTask;

        if (fullData == null)
        {
            Log.Error("[PlaylistSync] Merge: failed to fetch cloud data");
            return (0, 0, 0, 0);
        }

        var localIdSet = new HashSet<string>(localTrackIds, StringComparer.Ordinal);

        // Cloud set строим из ПОЛНОГО снимка (включая greyed-out)
        var cloudIdSet = new HashSet<string>(fullData.Tracks.Count, StringComparer.Ordinal);
        for (int i = 0; i < fullData.Tracks.Count; i++)
            cloudIdSet.Add("yt_" + fullData.Tracks[i].VideoId);

        // Cloud-only → добавить локально
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
                await _library.AddOrUpdateTrackAsync(track, ct);
                await _library.AddTrackToPlaylistAsync(track, playlist.Id, ct);
                addedLocally++;
            }
        }

        // Local-only → добавить в YouTube
        var toUpload = new List<string>();
        for (int i = 0; i < localTrackIds.Count; i++)
        {
            var trackId = localTrackIds[i];
            if (trackId.StartsWith("yt_", StringComparison.Ordinal) &&
                !cloudIdSet.Contains(trackId))
            {
                toUpload.Add(trackId);
            }
        }

        int addedToCloud = 0;
        if (toUpload.Count > 0)
        {
            var newSetVideoIds = await _youtube.AddTracksToPlaylistAsync(youtubeId, toUpload);
            addedToCloud = toUpload.Count;

            for (int i = 0; i < newSetVideoIds.Count && i < toUpload.Count; i++)
            {
                if (!string.IsNullOrEmpty(newSetVideoIds[i]))
                    setVideoIdMappings.Add((toUpload[i], newSetVideoIds[i]!));
            }
        }

        if (setVideoIdMappings.Count > 0)
            await _library.UpdateSetVideoIdsAsync(playlist.Id, setVideoIdMappings, ct);

        Log.Info($"[PlaylistSync] Merge: +{addedLocally} local, +{addedToCloud} cloud");
        return (addedLocally, addedToCloud, 0, 0);
    }

    /// <summary>
    /// Создаёт TrackInfo из RemoteTrackInfo для сохранения в локальную библиотеку.
    /// </summary>
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

    #endregion

    #region Helpers

    /// <summary>
    /// Загружает локальную обложку в YouTube.
    /// Поддерживает HTTP URL (скачивает) и локальные файлы (читает напрямую).
    /// </summary>
    private async Task<bool> UploadThumbnailToYoutubeAsync(
        string youtubePlaylistId,
        string thumbnailUrl,
        CancellationToken ct)
    {
        byte[] imageData;

        if (thumbnailUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            imageData = await _networkManager.ImageClient.GetByteArrayAsync(thumbnailUrl, linkedCts.Token);
        }
        else if (thumbnailUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(thumbnailUrl);
            var localPath = uri.LocalPath;

            if (!File.Exists(localPath))
            {
                Log.Warn($"[PlaylistSync] Thumbnail file not found: {localPath}");
                return false;
            }

            imageData = await File.ReadAllBytesAsync(localPath, ct);
        }
        else if (Path.IsPathRooted(thumbnailUrl) && File.Exists(thumbnailUrl))
        {
            imageData = await File.ReadAllBytesAsync(thumbnailUrl, ct);
        }
        else
        {
            Log.Warn($"[PlaylistSync] Invalid thumbnail URL for upload: {thumbnailUrl}");
            return false;
        }

        if (imageData.Length == 0)
        {
            Log.Warn("[PlaylistSync] Thumbnail is empty, skipping upload");
            return false;
        }

        if (imageData.Length > 20 * 1024 * 1024)
        {
            Log.Warn($"[PlaylistSync] Thumbnail too large: {imageData.Length / 1024 / 1024}MB (max 20MB)");
            return false;
        }

        return await _youtube.UploadPlaylistThumbnailAsync(
            youtubePlaylistId, imageData);
    }

    /// <summary>
    /// Возвращает кэшированный снимок или делает свежий запрос.
    /// Кэш заполняется в BuildPreviewAsync и очищается после ApplyAsync.
    /// </summary>
    private async Task<FullPlaylistSyncData?> GetOrFetchFullDataAsync(
        string youtubeId, CancellationToken ct)
    {
        if (_cachedSyncData != null)
        {
            Log.Debug("[PlaylistSync] Using cached sync data (no extra request)");
            return _cachedSyncData;
        }

        return await _youtube.GetFullPlaylistDataAsync(youtubeId, ct);
    }

    #endregion
}