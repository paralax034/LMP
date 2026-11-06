using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using LMP.Core.Audio.Cache;
using LMP.Core.Data.Repositories;

namespace LMP.Core.Services;

/// <summary>
/// Track registry (Identity Map / L1 Cache) for managing in-memory <see cref="TrackInfo"/> instances.
/// Guarantees track object reference uniqueness during concurrent requests.
/// </summary>
public sealed class TrackRegistry
{
    private const int CleanupThreshold = 512;

    private readonly ConcurrentDictionary<string, WeakReference<TrackInfo>> _cache =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TrackInfo> _pinned =
        new(StringComparer.Ordinal);

    private readonly Lock _registryLock = new();

    private readonly ITrackRepository? _repository;
    private readonly IPlaylistRepository? _playlists;
    private readonly CookieAuthService? _auth;

    private int _cleanupCounter;

    /// <summary>
    /// Initializes a new instance of the track registry.
    /// </summary>
    /// <param name="repository">Track metadata repository.</param>
    /// <param name="playlists">Playlist relation management repository.</param>
    /// <param name="auth">Authentication service for extracting active user context.</param>
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
    /// Active account identifier for end-to-end contextual data mapping.
    /// </summary>
    private string CurrentOwnerId => _auth?.State?.DisplayId ?? "guest";

    /// <summary>
    /// Registers a new track in the cache or updates metadata of an existing canonical instance.
    /// </summary>
    /// <param name="incoming">Incoming track instance with updated metadata.</param>
    /// <returns>Canonical (unique) track instance from the memory cache.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TrackInfo RegisterOrUpdate(TrackInfo incoming) =>
        RegisterOrUpdate(incoming, hasUserContext: false);

    /// <summary>
    /// Registers a new track in the cache or updates metadata of an existing canonical instance taking into account user context.
    /// </summary>
    /// <param name="incoming">Incoming track instance with updated metadata.</param>
    /// <param name="hasUserContext">Indicates whether data originated from a trusted source with authorized user context (DB, likes sync).</param>
    /// <returns>Canonical (unique) track instance from the memory cache.</returns>
    /// <remarks>
    /// Performs track pinning synchronization in strong memory: if a track loses its status as liked, downloaded, or added to a playlist,
    /// it is automatically evicted from the strong reference dictionary and moves to the weak cache for timely garbage collection.
    /// </remarks>
    public TrackInfo RegisterOrUpdate(TrackInfo incoming, bool hasUserContext)
    {
        if (string.IsNullOrEmpty(incoming.Id)) return incoming;

        if ((Interlocked.Increment(ref _cleanupCounter) & (CleanupThreshold - 1)) == 0)
        {
            Task.Run(CleanupDeadReferences);
        }

        var activeCache = AudioSourceFactory.TryGetInitializedGlobalCache();

        lock (_registryLock)
        {
            if (_pinned.TryGetValue(incoming.Id, out var pinned))
            {
                pinned.UpdateMetadata(incoming, hasUserContext);
                HydrateTrackFromAudioCache(pinned, activeCache);
                UpdatePinStatusInternal(pinned);
                return pinned;
            }

            if (_cache.TryGetValue(incoming.Id, out var weakRef) && weakRef.TryGetTarget(out var cached))
            {
                cached.UpdateMetadata(incoming, hasUserContext);
                HydrateTrackFromAudioCache(cached, activeCache);
                UpdatePinStatusInternal(cached);
                return cached;
            }

            _cache[incoming.Id] = new WeakReference<TrackInfo>(incoming);
            HydrateTrackFromAudioCache(incoming, activeCache);
            UpdatePinStatusInternal(incoming);
            return incoming;
        }
    }

    /// <summary>
    /// Enriches the runtime track model with local cache status and metadata normalization.
    /// In the LUFS model, the source of truth is strictly <see cref="TrackInfo.IntegratedLufs"/>.
    /// </summary>
    /// <param name="track">Canonical track instance.</param>
    /// <param name="audioCache">Audio cache manager.</param>
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
    /// Performs a fast attempt to retrieve a track from the in-memory L1 cache without database calls.
    /// </summary>
    /// <param name="id">Unique track identifier.</param>
    /// <returns>Canonical track instance, or <c>null</c> if it was collected by GC.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TrackInfo? TryGet(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;

        if (_pinned.TryGetValue(id, out var pinned)) return pinned;
        if (_cache.TryGetValue(id, out var weak) && weak.TryGetTarget(out var cached)) return cached;

        return null;
    }

    /// <summary>
    /// Returns the canonical track instance, fetching it from the database on cache miss.
    /// </summary>
    /// <param name="id">Track identifier.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>Track instance, or <c>null</c> if the track does not exist in the DB.</returns>
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
    /// Loads tracks from the DB, registers them in the cache, and returns the loaded instances directly,
    /// preserving the order of the input identifier list.
    /// <para>
    /// Unlike <see cref="PreloadAsync"/> + <see cref="TryGet"/>, the result
    /// is unaffected by concurrent <see cref="Clear"/> operations — all tracks are returned
    /// as a local collection rather than retrieved from the cache after an <c>await</c> boundary.
    /// </para>
    /// </summary>
    /// <param name="ids">Ordered list of track identifiers.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>List of tracks matching the order of <paramref name="ids"/>. Identifiers missing from the DB are omitted.</returns>
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

        var result = new List<TrackInfo>(ids.Count);
        for (int i = 0; i < ids.Count; i++)
        {
            if (found.TryGetValue(ids[i], out var track))
                result.Add(track);
        }

        return result;
    }

    /// <summary>
    /// Internal method for updating strong memory pinning of a track.
    /// Pins a track in strong references if it is liked, downloaded, or linked to user playlists.
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
    /// Updates the strong pin status of a track in RAM.
    /// </summary>
    /// <param name="track">Track instance.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdatePinStatus(TrackInfo track)
    {
        UpdatePinStatusInternal(track);
    }

    /// <summary>
    /// Warmup/populates the L1 cache during library initialization or account switching.
    /// Optimized to prevent memory leaks when dealing with recent track lists.
    /// </summary>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
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

        var initializedCache = AudioSourceFactory.TryGetInitializedGlobalCache();
        if (initializedCache != null)
        {
            initializedCache.HydrateCacheStatus(_pinned.Values);
        }
        else
        {
            _ = Task.Run(() =>
            {
                try
                {
                    var cache = AudioSourceFactory.GlobalCache;
                    cache.HydrateCacheStatus(_pinned.Values);
                    SubscribeToCacheEvents();
                }
                catch (Exception ex)
                {
                    Log.Warn($"[TrackRegistry] Deferred audio cache hydration failed: {ex.Message}");
                }
            }, CancellationToken.None);
        }

        sw.Stop();
        Log.Info($"[TrackRegistry] Hydrated {_pinned.Count} pinned tracks in {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Flushes modified metadata of memory-pinned tracks to persistent storage.
    /// </summary>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
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
    /// Returns all tracks currently held in memory by strong references.
    /// </summary>
    /// <returns>An enumerable of pinned tracks.</returns>
    public IEnumerable<TrackInfo> GetPinnedTracks() => _pinned.Values;

    /// <summary>
    /// Cleans up weak references to tracks that have already been collected by garbage collection.
    /// </summary>
    /// <returns>The number of keys successfully purged from the registry.</returns>
    public int CleanupDeadReferences()
    {
        var maxDeadCount = _cache.Count;
        if (maxDeadCount == 0) return 0;

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
    /// Completely purges all in-memory cache levels.
    /// Invoked when switching user accounts.
    /// </summary>
    public void Clear()
    {
        lock (_registryLock)
        {
            _cache.Clear();
            _pinned.Clear();
        }
        Log.Debug("[TrackRegistry] Memory caches successfully cleared on profile transition.");
    }

    /// <summary>
    /// Subscribes to global audio cache change events on the device.
    /// </summary>
    private int _isSubscribedToCacheEvents;

    /// <summary>
    /// Subscribes to global audio cache change events on the device.
    /// </summary>
    public void SubscribeToCacheEvents()
    {
        var cache = AudioSourceFactory.TryGetInitializedGlobalCache();
        if (cache == null) return;

        if (Interlocked.CompareExchange(ref _isSubscribedToCacheEvents, 1, 0) != 0)
            return;

        cache.OnCacheCleared += HandleCacheCleared;
        cache.OnFormatCached += HandleFormatCached;
        cache.OnEntryEvicted += HandleEntryEvicted;

        Log.Info("[TrackRegistry] Subscribed to AudioCache events");
    }

    /// <summary>
    /// Resets the IsCached track status upon eviction from the LRU cache.
    /// </summary>
    private void HandleEntryEvicted(string trackId)
    {
        if (TryGet(trackId) is { IsCached: true, IsDownloaded: false } track
            && AudioSourceFactory.GlobalCache.FindBestCacheByTrackId(trackId) == null)
        {
            track.ClearCacheStatus();
            Log.Debug($"[TrackRegistry] Evicted cache status reset for track {trackId}");
        }
    }

    /// <summary>
    /// Resets the local cache flag when the disk Cache directory is cleared.
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
    /// Handles successful completion of local track caching by updating its runtime properties.
    /// </summary>
    private void HandleFormatCached(string trackId, AudioFormat format, int bitrate, bool isDownloaded)
    {
        if (TryGet(trackId) is not { } track) return;

        if (isDownloaded)
            track.MarkAsDownloaded(track.LocalPath ?? "", format, bitrate);
        else
            track.MarkAsCached(format, bitrate);

        Log.Debug($"[TrackRegistry] Marked track {trackId} as cached ({format}/{bitrate}kbps)");
    }
}