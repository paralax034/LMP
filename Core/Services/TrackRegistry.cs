using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using LMP.Core.Audio.Cache;
using LMP.Core.Data.Repositories;

namespace LMP.Core.Services;

/// <summary>
/// Реестр треков (Identity Map / L1 Cache) для управления экземплярами <see cref="TrackInfo"/> в памяти.
/// Гарантирует уникальность ссылки на объект трека при параллельных запросах.
/// </summary>
public sealed class TrackRegistry
{
    private const int CleanupThreshold = 512;

    private readonly ConcurrentDictionary<string, WeakReference<TrackInfo>> _cache =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TrackInfo> _pinned =
        new(StringComparer.Ordinal);

    private readonly ITrackRepository? _repository;
    private readonly IPlaylistRepository? _playlists;
    private readonly CookieAuthService? _auth;

    private int _cleanupCounter;

    /// <summary>
    /// Инициализирует новый экземпляр реестра треков.
    /// </summary>
    /// <param name="repository">Репозиторий метаданных треков.</param>
    /// <param name="playlists">Репозиторий управления связями плейлистов.</param>
    /// <param name="auth">Служба аутентификации для извлечения контекста активного пользователя</param>
    public TrackRegistry(
        ITrackRepository? repository = null,
        IPlaylistRepository? playlists = null,
        CookieAuthService? auth = null)
    {
        _repository = repository;
        _playlists = playlists;
        _auth = auth;
    }

    /// <summary>
    /// Идентификатор активного аккаунта для сквозного контекстного маппинга данных
    /// </summary>
    private string CurrentOwnerId => _auth?.State?.DisplayId ?? "guest";

    /// <summary>
    /// Извлекает глобальный экземпляр менеджера кэша аудиофайлов.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static AudioCacheManager? GetAudioCache() => AudioSourceFactory.GlobalCache;

    /// <summary>
    /// Регистрирует новый трек в кэше или обновляет метаданные существующего канонического экземпляра.
    /// </summary>
    /// <param name="incoming">Входящий экземпляр трека с новыми метаданными.</param>
    /// <returns>Канонический (уникальный) экземпляр трека из кэша памяти.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TrackInfo RegisterOrUpdate(TrackInfo incoming) =>
        RegisterOrUpdate(incoming, hasUserContext: false);

    /// <summary>
    /// Регистрирует новый трек в кэше или обновляет метаданные существующего канонического экземпляра с учетом контекста пользователя.
    /// </summary>
    /// <param name="incoming">Входящий экземпляр трека с новыми метаданными.</param>
    /// <param name="hasUserContext">Указывает, получены ли данные из доверенного источника с авторизованным контекстом пользователя (БД, синхронизация лайков).</param>
    /// <returns>Канонический (уникальный) экземпляр трека из кэша памяти.</returns>
    /// <remarks>
    /// Выполняет синхронизацию закрепления трека в сильной памяти: если трек теряет статус лайкнутого, скачанного или добавленного в плейлист,
    /// он автоматически исключается из словаря сильных ссылок и переходит в weak-кэш для своевременной сборки мусора.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TrackInfo RegisterOrUpdate(TrackInfo incoming, bool hasUserContext)
    {
        if (string.IsNullOrEmpty(incoming.Id)) return incoming;

        if ((Interlocked.Increment(ref _cleanupCounter) & (CleanupThreshold - 1)) == 0)
        {
            Task.Run(CleanupDeadReferences);
        }

        var audioCache = GetAudioCache();

        if (_pinned.TryGetValue(incoming.Id, out var pinned))
        {
            pinned.UpdateMetadata(incoming, hasUserContext);
            HydrateTrackFromAudioCache(pinned, audioCache);
            UpdatePinStatusInternal(pinned);
            return pinned;
        }

        if (_cache.TryGetValue(incoming.Id, out var weakRef) && weakRef.TryGetTarget(out var cached))
        {
            cached.UpdateMetadata(incoming, hasUserContext);
            HydrateTrackFromAudioCache(cached, audioCache);
            UpdatePinStatusInternal(cached);
            return cached;
        }

        _cache[incoming.Id] = new WeakReference<TrackInfo>(incoming);
        HydrateTrackFromAudioCache(incoming, audioCache);
        UpdatePinStatusInternal(incoming);
        return incoming;
    }

    /// <summary>
    /// Дополняет рантайм-модель трека статусом локального кэша и metadata нормализации.
    /// В LUFS-модели source of truth — только <see cref="TrackInfo.IntegratedLufs"/>.
    /// </summary>
    /// <param name="track">Канонический экземпляр трека.</param>
    /// <param name="audioCache">Менеджер аудио-кэша.</param>
    private static void HydrateTrackFromAudioCache(TrackInfo track, AudioCacheManager? audioCache)
    {
        if (audioCache == null || string.IsNullOrEmpty(track.Id))
            return;

        AudioCacheEntry? completeEntry = null;

        if (!track.IsDownloaded && !track.IsCached)
        {
            completeEntry = audioCache.FindBestCacheByTrackId(track.Id);
            if (completeEntry != null)
                track.MarkAsCached(completeEntry.Format, completeEntry.Bitrate);
        }

        if (track.HasIntegratedLufs)
            return;

        var metadataEntry = completeEntry
            ?? audioCache.FindBestCacheByTrackId(track.Id)
            ?? audioCache.FindBestStartupCache(track.Id, 0);

        if (metadataEntry == null)
            return;

        TrackNormalizationHydrator.HydrateNormalization(track, metadataEntry);
    }

    /// <summary>
    /// Выполняет быструю попытку извлечь трек из кэша первого уровня в памяти без обращений к базе данных.
    /// </summary>
    /// <param name="id">Уникальный идентификатор трека.</param>
    /// <returns>Канонический экземпляр трека или <c>null</c>, если он вытеснен сборщиком мусора.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TrackInfo? TryGet(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;

        if (_pinned.TryGetValue(id, out var pinned)) return pinned;
        if (_cache.TryGetValue(id, out var weak) && weak.TryGetTarget(out var cached)) return cached;

        return null;
    }

    /// <summary>
    /// Возвращает канонический экземпляр трека, загружая его из базы данных при промахе кэша.
    /// </summary>
    /// <param name="id">Идентификатор трека.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Экземпляр трека или <c>null</c>, если трек отсутствует в БД.</returns>
    public async ValueTask<TrackInfo?> GetOrLoadAsync(string id, CancellationToken ct = default)
    {
        var cached = TryGet(id);
        if (cached != null) return cached;

        if (_repository == null) return null;

        var fromDb = await _repository.GetByIdAsync(id, CurrentOwnerId, ct).ConfigureAwait(false);
        if (fromDb == null) return null;

        if (_playlists != null)
        {
            fromDb.InPlaylists = await _playlists.GetPlaylistsForTrackAsync(id, CurrentOwnerId, ct).ConfigureAwait(false);
        }

        var canonical = RegisterOrUpdate(fromDb, hasUserContext: true);
        UpdatePinStatusInternal(canonical);

        return canonical;
    }

    /// <summary>
    /// Загружает треки из БД, регистрирует в кэше и возвращает загруженные экземпляры напрямую,
    /// сохраняя порядок входного списка идентификаторов.
    /// <para>
    /// В отличие от <see cref="PreloadAsync"/> + <see cref="TryGet"/>, результат
    /// не зависит от параллельного <see cref="Clear"/> — все треки возвращаются
    /// как локальная коллекция, а не извлекаются из кэша после <c>await</c>-границы.
    /// </para>
    /// </summary>
    /// <param name="ids">Упорядоченный список идентификаторов треков.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Список треков в порядке <paramref name="ids"/>. Отсутствующие в БД пропускаются.</returns>
    public async Task<List<TrackInfo>> PreloadAndReturnAsync(
        IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        if (_repository == null || ids.Count == 0) return [];

        var found = new Dictionary<string, TrackInfo>(ids.Count, StringComparer.Ordinal);
        var toLoad = new List<string>();

        for (int i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            var cached = TryGet(id);
            if (cached != null)
                found[id] = cached;
            else
                toLoad.Add(id);
        }

        if (toLoad.Count > 0)
        {
            var loaded = await _repository.GetByIdsAsync(toLoad, CurrentOwnerId, ct).ConfigureAwait(false);

            Dictionary<string, HashSet<string>>? playlistsMap = null;
            if (_playlists != null && loaded.Count > 0)
            {
                var loadedIds = new List<string>(loaded.Count);
                for (int i = 0; i < loaded.Count; i++)
                    loadedIds.Add(loaded[i].Id);

                playlistsMap = await _playlists.GetPlaylistsForTracksAsync(
                    loadedIds, CurrentOwnerId, ct).ConfigureAwait(false);
            }

            for (int i = 0; i < loaded.Count; i++)
            {
                var track = loaded[i];

                if (playlistsMap?.TryGetValue(track.Id, out var pls) == true)
                    track.InPlaylists = pls;

                var canonical = RegisterOrUpdate(track, hasUserContext: true);
                UpdatePinStatusInternal(canonical);
                found[canonical.Id] = canonical;
            }
        }

        // Восстанавливаем порядок входного списка
        var result = new List<TrackInfo>(ids.Count);
        for (int i = 0; i < ids.Count; i++)
        {
            if (found.TryGetValue(ids[i], out var track))
                result.Add(track);
        }

        return result;
    }

    /// <summary>
    /// Внутренний метод обновления закрепления трека в сильной памяти.
    /// Закрепляет трек в сильных ссылках, если он лайкнут, скачан или привязан к плейлистам пользователя.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool UpdatePinStatusInternal(TrackInfo track)
    {
        bool shouldPin = track.IsLiked ||
                        track.IsDownloaded ||
                        track.IsDisliked ||
                        track.InPlaylists.Count > 0;

        if (shouldPin)
        {
            _pinned.TryAdd(track.Id, track);
        }
        else
        {
            _pinned.TryRemove(track.Id, out _);
        }

        return shouldPin;
    }

    /// <summary>
    /// Обновляет статус сильного закрепления трека в оперативной памяти.
    /// </summary>
    /// <param name="track">Экземпляр трека.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdatePinStatus(TrackInfo track)
    {
        UpdatePinStatusInternal(track);
    }

    /// <summary>
    /// Наполняет кэш первого уровня при инициализации библиотеки или смене аккаунта.
    /// Оптимизировано для предотвращения утечек памяти при работе со списками недавних треков.
    /// </summary>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    public async Task HydrateAsync(CancellationToken ct = default)
    {
        if (_repository == null) return;

        Log.Info("[TrackRegistry] Hydrating from database...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var likedTask = _repository.GetLikedAsync(CurrentOwnerId, 1000, 0, ct);
        var downloadTask = _repository.GetDownloadedAsync(CurrentOwnerId, 1000, 0, ct);
        var recentTask = _repository.GetRecentlyPlayedAsync(CurrentOwnerId, 100, ct);

        await Task.WhenAll(likedTask, downloadTask, recentTask).ConfigureAwait(false);

        var pinnedCandidates = new List<TrackInfo>(
            likedTask.Result.Count + downloadTask.Result.Count);

        pinnedCandidates.AddRange(likedTask.Result);
        pinnedCandidates.AddRange(downloadTask.Result);

        var allIds = new HashSet<string>(
            pinnedCandidates.Count + recentTask.Result.Count,
            StringComparer.Ordinal);

        for (int i = 0; i < pinnedCandidates.Count; i++)
            allIds.Add(pinnedCandidates[i].Id);

        for (int i = 0; i < recentTask.Result.Count; i++)
            allIds.Add(recentTask.Result[i].Id);

        Dictionary<string, HashSet<string>>? playlistsMap = null;
        if (_playlists != null && allIds.Count > 0)
            playlistsMap = await _playlists.GetPlaylistsForTracksAsync(allIds, CurrentOwnerId, ct).ConfigureAwait(false);

        for (int i = 0; i < pinnedCandidates.Count; i++)
        {
            var t = pinnedCandidates[i];

            if (playlistsMap != null && playlistsMap.TryGetValue(t.Id, out var pls))
                t.InPlaylists = pls;

            var canonical = RegisterOrUpdate(t, hasUserContext: true);
            UpdatePinStatusInternal(canonical);
        }

        for (int i = 0; i < recentTask.Result.Count; i++)
        {
            var t = recentTask.Result[i];

            if (playlistsMap != null && playlistsMap.TryGetValue(t.Id, out var pls))
                t.InPlaylists = pls;

            var canonical = RegisterOrUpdate(t, hasUserContext: true);

            if (canonical.IsLiked || canonical.IsDownloaded ||
                canonical.IsDisliked || canonical.InPlaylists.Count > 0)
            {
                UpdatePinStatusInternal(canonical);
            }
        }

        var audioCache = GetAudioCache();
        audioCache?.HydrateCacheStatus(_pinned.Values);

        sw.Stop();
        Log.Info($"[TrackRegistry] Hydrated {_pinned.Count} pinned tracks in {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Сбрасывает измененные метаданные закрепленных в памяти треков в локальное хранилище.
    /// </summary>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_repository == null) return;

        var tracks = _pinned.Values.ToList();
        if (tracks.Count == 0) return;

        try
        {
            await _repository.UpsertBatchAsync(tracks, ct).ConfigureAwait(false);
            Log.Info($"[TrackRegistry] Flushed {tracks.Count} tracks to database");
        }
        catch (Exception ex)
        {
            Log.Error($"[TrackRegistry] Flush failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Возвращает все треки, удерживаемые в памяти сильной ссылкой.
    /// </summary>
    /// <returns>Перечисление закрепленных треков.</returns>
    public IEnumerable<TrackInfo> GetPinnedTracks() => _pinned.Values;

    /// <summary>
    /// Очищает слабые ссылки на треки, которые уже были собраны сборщиком мусора.
    /// </summary>
    /// <returns>Количество успешно выгруженных из реестра ключей.</returns>
    public int CleanupDeadReferences()
    {
        var maxDeadCount = _cache.Count;

        var deadKeysArray = ArrayPool<string>.Shared.Rent(maxDeadCount);
        int deadCount = 0;

        try
        {
            foreach (var kvp in _cache)
            {
                if (!kvp.Value.TryGetTarget(out _) && !_pinned.ContainsKey(kvp.Key))
                {
                    if (deadCount < deadKeysArray.Length)
                    {
                        deadKeysArray[deadCount++] = kvp.Key;
                    }
                }
            }

            for (int i = 0; i < deadCount; i++)
            {
                _cache.TryRemove(deadKeysArray[i], out _);
            }

            return deadCount;
        }
        finally
        {
            ArrayPool<string>.Shared.Return(deadKeysArray, clearArray: true);
        }
    }

    /// <summary>
    /// Полностью очищает все уровни кэша в оперативной памяти.
    /// Вызывается при переключении пользовательских аккаунтов.
    /// </summary>
    public void Clear()
    {
        _cache.Clear();
        _pinned.Clear();
        Log.Debug("[TrackRegistry] Memory caches successfully cleared on profile transition.");
    }

    /// <summary>
    /// Подписывается на глобальные события изменений кэша аудиофайлов на устройстве.
    /// </summary>
    public void SubscribeToCacheEvents()
    {
        var audioCache = GetAudioCache();
        if (audioCache == null)
        {
            Log.Warn("[TrackRegistry] AudioCache not available for event subscription");
            return;
        }

        audioCache.OnCacheCleared += HandleCacheCleared;
        audioCache.OnFormatCached += HandleFormatCached;

        Log.Info("[TrackRegistry] Subscribed to AudioCache events");
    }

    /// <summary>
    /// Сбрасывает флаг локального кэша при очистке дисковой папки Cache.
    /// </summary>
    private void HandleCacheCleared()
    {
        int cleared = 0;

        foreach (var track in _pinned.Values)
        {
            if (track.IsCached && !track.IsDownloaded)
            {
                track.ClearCacheStatus();
                cleared++;
            }
        }

        foreach (var weakRef in _cache.Values)
        {
            if (weakRef.TryGetTarget(out var track) && track.IsCached && !track.IsDownloaded)
            {
                if (!_pinned.ContainsKey(track.Id))
                {
                    track.ClearCacheStatus();
                    cleared++;
                }
            }
        }

        Log.Info($"[TrackRegistry] Cache cleared: reset IsCached on {cleared} tracks");
    }

    /// <summary>
    /// Реагирует на успешное завершение локального кэширования трека, обновляя его рантайм-свойства.
    /// </summary>
    private void HandleFormatCached(string trackId, AudioFormat format, int bitrate, bool isDownloaded)
    {
        if (string.IsNullOrEmpty(trackId)) return;

        if (_pinned.TryGetValue(trackId, out var pinned))
        {
            if (isDownloaded)
                pinned.MarkAsDownloaded(pinned.LocalPath ?? "", format, bitrate);
            else
                pinned.MarkAsCached(format, bitrate);

            Log.Debug($"[TrackRegistry] Marked pinned track {trackId} as cached ({format}/{bitrate}kbps)");
            return;
        }

        if (_cache.TryGetValue(trackId, out var weakRef) && weakRef.TryGetTarget(out var cached))
        {
            if (isDownloaded)
                cached.MarkAsDownloaded(cached.LocalPath ?? "", format, bitrate);
            else
                cached.MarkAsCached(format, bitrate);

            Log.Debug($"[TrackRegistry] Marked cached track {trackId} as cached ({format}/{bitrate}kbps)");
        }
    }
}