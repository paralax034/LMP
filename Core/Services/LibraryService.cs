using LMP.Core.Data;
using LMP.Core.Data.Repositories;
using Microsoft.Data.Sqlite;

namespace LMP.Core.Services;

/// <summary>
/// Service for managing the global track cache, application settings, history, and system likes.
/// </summary>
public sealed class LibraryService : IAsyncDisposable, IDisposable
{
    public const string LikedPlaylistId = "liked";

    private const int HydrationDebounceMs = 150;
    private const int SettingsSaveDebounceMs = 1500;
    private const int SettingsSaveTimeout = 2000;

    private readonly TrackRegistry _registry;
    private readonly ITrackRepository _tracks;
    private readonly IPlaylistRepository _playlists;
    private readonly ISettingsRepository _settings;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly CookieAuthService _auth;
    private readonly LegacyMigrationService _migration;

    private readonly SemaphoreSlim _settingsLock = new(1, 1);

    // Serializes like operations: rapid clicks write the latest state to SQLite rather than task completion order
    private readonly SemaphoreSlim _likeWriteGate = new(1, 1);
    private readonly Timer _saveDebounceTimer;

    private string _lastHydratedOwnerId = string.Empty;
    private CancellationTokenSource? _hydrationCts;
    private readonly Lock _hydrationLock = new();

    public AppSettings Settings { get; private set; } = new();
    public bool IsInitialized { get; private set; }

    public event Action? OnDataChanged;
    public event Action<TrackInfo>? OnTrackUpdated;
    public event Action? OnAccountHydrated;
    public event Action? OnInitialized;

    private string CurrentOwnerId => _auth.State.DisplayId;

    public LibraryService(
        TrackRegistry registry,
        ITrackRepository tracks,
        IPlaylistRepository playlists,
        ISettingsRepository settings,
        ISqliteConnectionFactory connectionFactory,
        CookieAuthService auth,
        LegacyMigrationService migration)
    {
        _registry = registry;
        _tracks = tracks;
        _playlists = playlists;
        _settings = settings;
        _connectionFactory = connectionFactory;
        _auth = auth;
        _migration = migration;

        _saveDebounceTimer = new Timer(OnSaveTimerCallback, null, Timeout.Infinite, Timeout.Infinite);

        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
        _auth.OnAuthStateChanged += HandleAuthStateChanged;
    }

    private void HandleAuthStateChanged() => _ = HandleAuthStateChangedAsync();

    private async Task HandleAuthStateChangedAsync()
    {
        CancellationTokenSource cts;
        lock (_hydrationLock)
        {
            _hydrationCts?.Cancel();
            _hydrationCts?.Dispose();
            cts = _hydrationCts = new CancellationTokenSource();
        }

        var ct = cts.Token;

        try
        {
            if (!await ct.DelayNoThrowAsync(HydrationDebounceMs, continueOnCapturedContext: false))
                return;

            var ownerId = CurrentOwnerId;
            if (string.Equals(ownerId, _lastHydratedOwnerId, StringComparison.Ordinal))
                return;

            Log.Info($"[LibraryService] Auth state stabilized. Hydrating for owner: {ownerId}");

            if (!string.IsNullOrEmpty(ownerId) && ownerId != "guest")
            {
                await _playlists.AdoptOrphanPlaylistsAsync(ownerId, ct).ConfigureAwait(false);
            }

            _registry.Clear();
            await _registry.HydrateAsync(ct).ConfigureAwait(false);

            if (ct.IsCancellationRequested) return;

            _lastHydratedOwnerId = ownerId;

            OnAccountHydrated?.Invoke();
            OnDataChanged?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error($"[LibraryService] Hydration failed: {ex.Message}");
        }
        finally
        {
            lock (_hydrationLock)
            {
                if (_hydrationCts == cts)
                {
                    cts.Dispose();
                    _hydrationCts = null;
                }
            }
        }
    }

    #region Инициализация

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        Settings = await _settings.GetOrDefaultAsync("AppSettings", new AppSettings(), ct).ConfigureAwait(false);

        bool requireSave = false;

#if DEBUG
        if (Settings.InternetProfile != InternetProfile.Medium)
        {
            Settings.InternetProfile = InternetProfile.Medium;
            requireSave = true;
        }
#else
        if (BootstrapSettings.Current.AppUpdatedThisRun && Settings.InternetProfile != InternetProfile.Medium)
        {
            Settings.InternetProfile = InternetProfile.Medium;
            requireSave = true;
            Log.Info("[LibraryService] App updated, resetting InternetProfile to default.");
        }
#endif

        if (requireSave)
        {
            await _settings.SetAsync("AppSettings", Settings, ct).ConfigureAwait(false);
        }

        AudioSourceFactory.ApplyInternetProfile(Settings.InternetProfile);
        AudioSourceFactory.TryGetInitializedGlobalCache()?.UpdateMaxCacheSizeMb(Settings.Storage.AudioCacheLimitMb);

        var jsonPath = G.FilePath.LegacyDatabase;
        if (File.Exists(jsonPath))
        {
            var migratedSettings = await _migration.MigrateFromJsonAsync(jsonPath, ct).ConfigureAwait(false);
            if (migratedSettings != null)
                Settings = migratedSettings;
        }

        var initialOwnerId = CurrentOwnerId;
        if (!string.IsNullOrEmpty(initialOwnerId) && initialOwnerId != "guest")
        {
            await _playlists.AdoptOrphanPlaylistsAsync(initialOwnerId, ct).ConfigureAwait(false);
        }

        await _registry.HydrateAsync(ct).ConfigureAwait(false);
        _lastHydratedOwnerId = initialOwnerId;

        _registry.SubscribeToCacheEvents();

        sw.Stop();
        Log.Info($"[LibraryService] Initialized in {sw.ElapsedMilliseconds}ms");

        IsInitialized = true;
        OnInitialized?.Invoke();
    }

    #endregion

    #region Треки

    /// <summary>
    /// Adds or updates track metadata in the registry L1 cache and persists it to the local database.
    /// </summary>
    /// <param name="track">The track instance.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    public async Task AddOrUpdateTrackAsync(TrackInfo track, CancellationToken ct = default)
    {
        track.InPlaylists = await _playlists.GetPlaylistsForTrackAsync(track.Id, CurrentOwnerId, ct).ConfigureAwait(false);
        var canonical = _registry.RegisterOrUpdate(track);
        await _tracks.UpsertAsync(canonical, ct).ConfigureAwait(false);
        OnTrackUpdated?.Invoke(canonical);
    }

    /// <summary>
    /// Batch registers and saves a list of tracks in a single SQLite transaction without N+1 queries.
    /// </summary>
    public async Task AddOrUpdateTracksBatchAsync(IReadOnlyList<TrackInfo> tracks, CancellationToken ct = default)
    {
        if (tracks.Count == 0) return;

        var trackIds = new List<string>(tracks.Count);
        for (int i = 0; i < tracks.Count; i++)
            trackIds.Add(tracks[i].Id);

        var playlistsMap = await _playlists.GetPlaylistsForTracksAsync(trackIds, CurrentOwnerId, ct).ConfigureAwait(false);
        var canonicalList = new List<TrackInfo>(tracks.Count);

        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            t.InPlaylists = playlistsMap.TryGetValue(t.Id, out var pls) ? pls : [];
            var canonical = _registry.RegisterOrUpdate(t);
            canonicalList.Add(canonical);
        }

        await _tracks.UpsertBatchAsync(canonicalList, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Retrieves a track from the in-memory registry L1 cache without hitting the database.
    /// </summary>
    /// <param name="id">The track identifier.</param>
    /// <returns>The track model instance or <c>null</c>.</returns>
    public TrackInfo? GetTrack(string id) => _registry.TryGet(id);

    /// <summary>
    /// Retrieves a track from the in-memory cache or asynchronously loads it from the database.
    /// </summary>
    /// <param name="id">The track identifier.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>The loaded track model instance.</returns>
    public async Task<TrackInfo?> GetTrackAsync(string id, CancellationToken ct = default) =>
        await _registry.GetOrLoadAsync(id, ct).ConfigureAwait(false);

    /// <summary>
    /// Performs batch hydration of playlist relationships and registers tracks in the L1 registry.
    /// </summary>
    private async Task HydrateAndRegisterTracksAsync(List<TrackInfo> tracks, CancellationToken ct)
    {
        if (tracks.Count == 0) return;

        var trackIds = new List<string>(tracks.Count);
        for (int i = 0; i < tracks.Count; i++)
            trackIds.Add(tracks[i].Id);

        var playlistsMap = await _playlists.GetPlaylistsForTracksAsync(trackIds, CurrentOwnerId, ct).ConfigureAwait(false);

        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            t.InPlaylists = playlistsMap.TryGetValue(t.Id, out var pls) ? pls : [];
            _registry.RegisterOrUpdate(t);
        }
    }

    /// <summary>
    /// Performs full-text search for tracks in the database by title or artist.
    /// </summary>
    public async Task<List<TrackInfo>> SearchTracksAsync(
        string query, int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        var tracks = await _tracks.SearchAsync(query, CurrentOwnerId, limit, offset, ct).ConfigureAwait(false);
        if (tracks.Count == 0) return tracks;

        await HydrateAndRegisterTracksAsync(tracks, ct).ConfigureAwait(false);
        return tracks;
    }

    /// <summary>
    /// Returns the total duration of all unique tracks in the user's library.
    /// </summary>
    public async Task<long> GetTotalLibraryDurationAsync(CancellationToken ct = default) =>
        await _playlists.GetTotalLibraryDurationAsync(CurrentOwnerId, ct).ConfigureAwait(false);

    /// <summary>
    /// Retrieves a paginated list of all tracks from the database.
    /// </summary>
    public async Task<List<TrackInfo>> GetAllTracksAsync(int limit = 10000, int offset = 0, CancellationToken ct = default)
    {
        var tracks = await _tracks.GetAllAsync(CurrentOwnerId, limit, offset, ct).ConfigureAwait(false);
        await HydrateAndRegisterTracksAsync(tracks, ct).ConfigureAwait(false);
        return tracks;
    }

    /// <summary>
    /// Returns a list of local files and downloaded tracks.
    /// </summary>
    public async Task<List<TrackInfo>> GetLocalTracksAsync(int limit = 1000, int offset = 0, CancellationToken ct = default)
    {
        var tracks = await _tracks.GetLocalTracksAsync(CurrentOwnerId, limit, offset, ct).ConfigureAwait(false);
        await HydrateAndRegisterTracksAsync(tracks, ct).ConfigureAwait(false);
        return tracks;
    }

    /// <summary>
    /// Resets the local download flag and file path for all downloaded tracks in the L1 cache and SQLite DB.
    /// </summary>
    public async Task ClearDownloadsMetadataAsync(CancellationToken ct = default)
    {
        var downloadedInDb = await _tracks.GetDownloadedAsync(CurrentOwnerId, 10000, 0, ct).ConfigureAwait(false);
        var updatedIds = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < downloadedInDb.Count; i++)
        {
            var t = downloadedInDb[i];
            updatedIds.Add(t.Id);
            await _tracks.SetDownloadedAsync(t.Id, false, null, ct).ConfigureAwait(false);

            var inMem = _registry.TryGet(t.Id);
            if (inMem != null)
            {
                inMem.IsDownloaded = false;
                inMem.LocalPath = null;
                _registry.UpdatePinStatus(inMem);
            }
        }

        foreach (var pinned in _registry.GetPinnedTracks())
        {
            if (pinned.IsDownloaded && updatedIds.Add(pinned.Id))
            {
                pinned.IsDownloaded = false;
                pinned.LocalPath = null;
                await _tracks.SetDownloadedAsync(pinned.Id, false, null, ct).ConfigureAwait(false);
                _registry.UpdatePinStatus(pinned);
            }
        }

        OnDataChanged?.Invoke();
    }

    /// <summary>
    /// Returns the total number of tracks in the database.
    /// </summary>
    public async Task<int> GetTrackCountAsync(CancellationToken ct = default) =>
        await _tracks.CountAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Saves loudness normalization metadata (Integrated LUFS) for a track.
    /// </summary>
    public Task SaveTrackNormalizationMetadataAsync(
        string trackId,
        float integratedLufs,
        int source,
        CancellationToken ct = default) =>
        _tracks.SaveNormalizationMetadataAsync(trackId, integratedLufs, source, ct);

    /// <summary>
    /// Sets or resets a custom equalizer preset for a track in the L1 cache and SQLite DB.
    /// </summary>
    public async Task SetTrackEqualizerPresetAsync(
        TrackInfo track,
        string? presetId,
        CancellationToken ct = default)
    {
        var canonical = _registry.RegisterOrUpdate(track, hasUserContext: true);
        canonical.EqualizerPresetId = string.IsNullOrWhiteSpace(presetId) ? null : presetId;

        await _tracks.UpsertAsync(canonical, ct).ConfigureAwait(false);
        await _tracks.SetEqualizerPresetAsync(canonical.Id, canonical.EqualizerPresetId, ct).ConfigureAwait(false);
        OnTrackUpdated?.Invoke(canonical);
    }

    #endregion

    #region Лайки

    /// <summary>
    /// Sets the "Liked" status for a track and synchronizes the system Liked playlist.
    /// Low-level method for persisting state in SQLite and the L1 track registry.
    /// </summary>
    /// <param name="track">The track instance.</param>
    /// <param name="isLiked">The target liked state.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    public async Task SetLikeStateAsync(TrackInfo track, bool isLiked, CancellationToken ct = default)
    {
        var canonical = _registry.RegisterOrUpdate(track, hasUserContext: true);

        // Optimistic L1 state is set before waiting for the gate: UI reflects the click instantly
        canonical.SetLikedState(isLiked);

        await _likeWriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Last-Write-Wins: write the actual current value, which may have been changed by a later click
            bool latestLiked = canonical.IsLiked;

            await _tracks.UpsertAsync(canonical, ct).ConfigureAwait(false);
            await _tracks.SetLikedAsync(canonical.Id, CurrentOwnerId, latestLiked, ct: ct).ConfigureAwait(false);

            canonical.InPlaylists = await _playlists.GetPlaylistsForTrackAsync(canonical.Id, CurrentOwnerId, ct).ConfigureAwait(false);
            if (canonical.IsLiked)
                canonical.InPlaylists.Add(LikedPlaylistId);
            else
                canonical.InPlaylists.Remove(LikedPlaylistId);
        }
        finally
        {
            _likeWriteGate.Release();
        }

        _registry.UpdatePinStatus(canonical);

        OnDataChanged?.Invoke();
        OnTrackUpdated?.Invoke(canonical);
    }

    /// <summary>
    /// Returns a list of the current user's liked tracks.
    /// </summary>
    /// <param name="limit">Maximum number of records to return.</param>
    /// <param name="offset">Selection offset.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>List of track models.</returns>
    public async Task<List<TrackInfo>> GetLikedTracksAsync(int limit = 100, int offset = 0, CancellationToken ct = default)
    {
        var tracks = await _tracks.GetLikedAsync(CurrentOwnerId, limit, offset, ct).ConfigureAwait(false);
        await HydrateAndRegisterTracksAsync(tracks, ct).ConfigureAwait(false);
        return tracks;
    }

    /// <summary>
    /// Checks whether the specified identifier is the system "Liked" playlist.
    /// </summary>
    /// <param name="id">Playlist identifier.</param>
    /// <returns><c>true</c> if the identifier equals <see cref="LikedPlaylistId"/>; otherwise, <c>false</c>.</returns>
    public static bool IsSystemPlaylist(string id) => id == LikedPlaylistId;

    #endregion

    #region История

    public async Task AddToRecentlyPlayedAsync(TrackInfo track, CancellationToken ct = default)
    {
        await AddOrUpdateTrackAsync(track, ct).ConfigureAwait(false);
        await _tracks.AddToHistoryAsync(track.Id, CurrentOwnerId, ct).ConfigureAwait(false);
    }

    public async Task<List<TrackInfo>> GetRecentlyPlayedAsync(int count = 20, CancellationToken ct = default)
    {
        var tracks = await _tracks.GetRecentlyPlayedAsync(CurrentOwnerId, count, ct).ConfigureAwait(false);
        for (int i = 0; i < tracks.Count; i++)
            _registry.RegisterOrUpdate(tracks[i]);
        return tracks;
    }

    public async Task ClearHistoryAsync(CancellationToken ct = default)
    {
        await _tracks.ClearHistoryAsync(CurrentOwnerId, ct).ConfigureAwait(false);
        OnDataChanged?.Invoke();
    }

    #endregion

    #region Плейлисты

    public Task<string?> GetSetVideoIdAsync(string playlistId, string trackId, CancellationToken ct = default) =>
        _playlists.GetSetVideoIdAsync(playlistId, trackId, ct);

    public Task UpdateSetVideoIdAsync(string playlistId, string trackId, string setVideoId, CancellationToken ct = default) =>
        _playlists.UpdateSetVideoIdAsync(playlistId, trackId, setVideoId, ct);

    public Task UpdateSetVideoIdsAsync(string playlistId, IReadOnlyList<(string TrackId, string SetVideoId)> mappings, CancellationToken ct = default) =>
        _playlists.UpdateSetVideoIdsAsync(playlistId, mappings, ct);

    #endregion

    #region Настройки

    public string DownloadPath
    {
        get => string.IsNullOrEmpty(Settings.DownloadPath) ? G.Folder.Downloads : Settings.DownloadPath;
        set { Settings.DownloadPath = value; SaveSettingsImmediate(); }
    }

    public void UpdateSettings(Action<AppSettings> update)
    {
        update(Settings);
        _saveDebounceTimer.Change(SettingsSaveDebounceMs, Timeout.Infinite);
    }

    private void OnSaveTimerCallback(object? state) => _ = SaveSettingsAsync();

    private async Task SaveSettingsAsync(CancellationToken ct = default)
    {
        if (!await _settingsLock.WaitAsync(SettingsSaveTimeout, ct).ConfigureAwait(false))
            return;

        try
        {
            await _settings.SetAsync("AppSettings", Settings, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[LibraryService] Debounced settings save failed: {ex.Message}");
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    private void SaveSettingsSync()
    {
        _saveDebounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _settingsLock.Wait();
        try
        {
            _settings.Set("AppSettings", Settings);
        }
        catch (Exception ex)
        {
            Log.Error($"[LibraryService] Sync settings save failed: {ex.Message}");
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    public void SaveSettingsImmediate() => SaveSettingsSync();

    #endregion

    #region Поиск

    public async Task<List<string>> GetSearchHistoryAsync(CancellationToken ct = default)
    {
        var key = $"SearchHistory_{CurrentOwnerId}";
        return await _settings.GetOrDefaultAsync<List<string>>(key, [], ct).ConfigureAwait(false);
    }

    public async Task SaveSearchHistoryAsync(List<string> history, CancellationToken ct = default)
    {
        var key = $"SearchHistory_{CurrentOwnerId}";
        await _settings.SetAsync(key, history, ct).ConfigureAwait(false);
    }

    #endregion

    #region События

    private void OnLanguageChanged(object? _, string __) => OnDataChanged?.Invoke();

    #endregion

    #region Очистка и завершение

    /// <summary>
    /// Fully resets the library: deletes the database file along with WAL/SHM logs and atomically creates a clean schema.
    /// </summary>
    /// <remarks>
    /// Deletion errors are not suppressed: if a file is locked, the old database would remain intact,
    /// and any leftover <c>-wal</c> file could be applied to the new database upon opening.
    /// </remarks>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        _registry.Clear();

        SqliteConnection.ClearAllPools();
        var dbPath = G.FilePath.Database;
        File.Delete(dbPath + "-wal"); // Write-ahead logs first: if a file is busy, the main database remains untouched
        File.Delete(dbPath + "-shm");
        File.Delete(dbPath);

        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await connection.ApplySchemaMigrationAsync(DatabaseExtensions.CurrentDbVersion, ct).ConfigureAwait(false);
        await connection.OptimizeAsync(ct).ConfigureAwait(false);

        Settings = new AppSettings();
        OnDataChanged?.Invoke();
    }

    public void Dispose()
    {
        SaveSettingsSync();
        _saveDebounceTimer.Dispose();
        _settingsLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;
        _auth.OnAuthStateChanged -= HandleAuthStateChanged;

        lock (_hydrationLock)
        {
            _hydrationCts?.Cancel();
            _hydrationCts?.Dispose();
            _hydrationCts = null;
        }

        await _registry.FlushAsync().ConfigureAwait(false);

        SaveSettingsSync();
        await _saveDebounceTimer.DisposeAsync().ConfigureAwait(false);
        _settingsLock.Dispose();
    }

    #endregion
}