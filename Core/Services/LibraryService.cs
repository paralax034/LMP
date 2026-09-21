using System.Text.Json;
using LMP.Core.Data;
using LMP.Core.Data.Repositories;
using Microsoft.Data.Sqlite;

namespace LMP.Core.Services;

/// <summary>
/// Сервис управления глобальным кэшем треков, настройками приложения, историей и системными лайками.
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

    private readonly SemaphoreSlim _settingsLock = new(1, 1);
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
        CookieAuthService auth)
    {
        _registry = registry;
        _tracks = tracks;
        _playlists = playlists;
        _settings = settings;
        _connectionFactory = connectionFactory;
        _auth = auth;

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
            await Task.Delay(HydrationDebounceMs, ct).ConfigureAwait(false);

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

        var jsonPath = G.FilePath.LegacyDatabase;
        if (File.Exists(jsonPath))
        {
            await MigrateFromJsonAsync(jsonPath, ct).ConfigureAwait(false);
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

    private async Task MigrateFromJsonAsync(string path, CancellationToken ct)
    {
        try
        {
            Log.Info("[Migration] Starting JSON -> SQLite migration...");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var legacy = JsonSerializer.Deserialize(json, AppJsonContext.Default.LegacyLibraryData);
            if (legacy == null) return;

            var migratedTrackIds = new HashSet<string>();

            if (legacy.Tracks?.Count > 0)
            {
                foreach (var track in legacy.Tracks.Values)
                {
                    if (string.IsNullOrEmpty(track.Id)) continue;
                    await _tracks.UpsertAsync(track, ct).ConfigureAwait(false);
                    migratedTrackIds.Add(track.Id);
                }
            }

            if (legacy.Playlists?.Count > 0)
            {
                foreach (var legacyPl in legacy.Playlists.Values)
                {
                    var playlist = legacyPl.ToPlaylist();
                    playlist.OwnerId = CurrentOwnerId;
                    await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);

                    var validTrackIds = legacyPl.TrackIds.Where(migratedTrackIds.Contains).ToList();
                    await _playlists.AddTracksAsync(playlist.Id, validTrackIds, CurrentOwnerId, ct).ConfigureAwait(false);
                }
            }

            if (legacy.RecentlyPlayedIds?.Count > 0)
            {
                foreach (var id in legacy.RecentlyPlayedIds.AsEnumerable().Reverse().Take(100))
                {
                    if (migratedTrackIds.Contains(id))
                        await _tracks.AddToHistoryAsync(id, CurrentOwnerId, ct).ConfigureAwait(false);
                }
            }

            Settings = MapLegacySettings(legacy);
            await _settings.SetAsync("AppSettings", Settings, ct).ConfigureAwait(false);

            var backup = path + $".migrated.{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(path, backup);

            sw.Stop();
            Log.Info($"[Migration] Complete in {sw.ElapsedMilliseconds}ms.");
        }
        catch (Exception ex)
        {
            Log.Error($"[Migration] Failed: {ex.Message}");
        }
    }

    private static AppSettings MapLegacySettings(LegacyLibraryData d) => new()
    {
        Volume = d.Volume > 0 && d.Volume <= 1.0f ? (int)(d.Volume * 100) : (int)d.Volume,
        ShuffleEnabled = d.ShuffleEnabled,
        RepeatMode = d.RepeatMode,
        MaxVolumeLimit = d.MaxVolumeLimit,
        TargetGainDb = d.TargetGainDb,
        QualityPreference = d.QualityPreference,
        RememberTrackFormat = d.RememberTrackFormat,
        InternetProfile = d.InternetProfile,
        LanguageCode = d.LanguageCode,
        DownloadPath = d.DownloadPath,
        DiscordRpcEnabled = d.DiscordRpcEnabled,
        AutoPlayOnUrlPaste = d.AutoPlayOnUrlPaste,
        LoadBatchSize = d.LoadBatchSize,
        SearchBatchSize = d.SearchBatchSize,
        EnableSearchCache = d.EnableSearchCache,
        SearchCacheTtlMinutes = d.SearchCacheTtlMinutes,
        PlaylistHeaderHeight = d.PlaylistHeaderHeight
    };

    #endregion

    #region Треки

    /// <summary>
    /// Добавляет или обновляет метаданные трека в L1-кэше реестра и сохраняет их в локальную базу данных.
    /// </summary>
    /// <param name="track">Экземпляр трека.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    public async Task AddOrUpdateTrackAsync(TrackInfo track, CancellationToken ct = default)
    {
        track.InPlaylists = await _playlists.GetPlaylistsForTrackAsync(track.Id, CurrentOwnerId, ct).ConfigureAwait(false);
        var canonical = _registry.RegisterOrUpdate(track);
        await _tracks.UpsertAsync(canonical, ct).ConfigureAwait(false);
        OnTrackUpdated?.Invoke(canonical);
    }

    /// <summary>
    /// Извлекает трек из оперативного L1-кэша реестра без обращения к базе данных.
    /// </summary>
    /// <param name="id">Идентификатор трека.</param>
    /// <returns>Экземпляр модели трека либо <c>null</c>.</returns>
    public TrackInfo? GetTrack(string id) => _registry.TryGet(id);

    /// <summary>
    /// Получает трек из оперативного кэша либо асинхронно загружает его из базы данных.
    /// </summary>
    /// <param name="id">Идентификатор трека.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Загруженный экземпляр модели трека.</returns>
    public async Task<TrackInfo?> GetTrackAsync(string id, CancellationToken ct = default) =>
        await _registry.GetOrLoadAsync(id, ct).ConfigureAwait(false);

    /// <summary>
    /// Выполняет пакетную гидратацию связей с плейлистами и регистрацию треков в L1-реестре.
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
    /// Выполняет полнотекстовый поиск треков в базе данных по имени или автору.
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
    /// Возвращает суммарную продолжительность всех уникальных треков в библиотеке пользователя.
    /// </summary>
    public async Task<long> GetTotalLibraryDurationAsync(CancellationToken ct = default) =>
        await _playlists.GetTotalLibraryDurationAsync(CurrentOwnerId, ct).ConfigureAwait(false);

    /// <summary>
    /// Извлекает список всех треков базы данных с пагинацией.
    /// </summary>
    public async Task<List<TrackInfo>> GetAllTracksAsync(int limit = 10000, int offset = 0, CancellationToken ct = default)
    {
        var tracks = await _tracks.GetAllAsync(CurrentOwnerId, limit, offset, ct).ConfigureAwait(false);
        await HydrateAndRegisterTracksAsync(tracks, ct).ConfigureAwait(false);
        return tracks;
    }

    /// <summary>
    /// Возвращает список локальных файлов и загруженных треков.
    /// </summary>
    public async Task<List<TrackInfo>> GetLocalTracksAsync(int limit = 1000, int offset = 0, CancellationToken ct = default)
    {
        var tracks = await _tracks.GetLocalTracksAsync(CurrentOwnerId, limit, offset, ct).ConfigureAwait(false);
        await HydrateAndRegisterTracksAsync(tracks, ct).ConfigureAwait(false);
        return tracks;
    }

    /// <summary>
    /// Возвращает общее количество треков в базе данных.
    /// </summary>
    public async Task<int> GetTrackCountAsync(CancellationToken ct = default) =>
        await _tracks.CountAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Возвращает общее количество загруженных и локальных треков.
    /// </summary>
    public async Task<int> GetLocalTrackCountAsync(CancellationToken ct = default) =>
        await _tracks.CountLocalAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Выполняет локальный поиск по загруженным и физическим аудиофайлам устройства.
    /// </summary>
    /// <param name="query">Поисковый запрос.</param>
    /// <param name="limit">Максимальное число возвращаемых треков.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Список отфильтрованных локальных треков.</returns>
    public async Task<List<TrackInfo>> SearchLocalTracksAsync(string query, int limit = 100, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return await GetLocalTracksAsync(limit, 0, ct).ConfigureAwait(false);

        var allLocal = await _tracks.GetLocalTracksAsync(CurrentOwnerId, limit * 2, 0, ct).ConfigureAwait(false);

        var filtered = allLocal
            .Where(t =>
                t.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                t.Author.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToList();

        await HydrateAndRegisterTracksAsync(filtered, ct).ConfigureAwait(false);
        return filtered;
    }

    /// <summary>
    /// Сохраняет метаданные нормализации громкости (Integrated LUFS) для трека.
    /// </summary>
    public Task SaveTrackNormalizationMetadataAsync(
        string trackId,
        float integratedLufs,
        int source,
        CancellationToken ct = default) =>
        _tracks.SaveNormalizationMetadataAsync(trackId, integratedLufs, source, ct);

    #endregion

    #region Лайки

    /// <summary>
    /// Устанавливает статус отметки «Мне нравится» для трека с синхронизацией системного плейлиста Liked.
    /// Является низкоуровневым методом фиксации состояния в SQLite и L1-реестре треков.
    /// </summary>
    /// <param name="track">Экземпляр трека.</param>
    /// <param name="isLiked">Устанавливаемое состояние лайка.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    public async Task SetLikeStateAsync(TrackInfo track, bool isLiked, CancellationToken ct = default)
    {
        track.InPlaylists = await _playlists.GetPlaylistsForTrackAsync(track.Id, CurrentOwnerId, ct).ConfigureAwait(false);
        var canonical = _registry.RegisterOrUpdate(track);

        if (canonical.IsLiked == isLiked) return;

        canonical.IsLiked = isLiked;
        if (isLiked) canonical.IsDisliked = false;

        await _tracks.UpsertAsync(canonical, ct).ConfigureAwait(false);

        if (isLiked)
        {
            await _playlists.AddTrackAsync(LikedPlaylistId, canonical.Id, CurrentOwnerId, 0, ct).ConfigureAwait(false);
            canonical.InPlaylists.Add(LikedPlaylistId);
        }
        else
        {
            await _playlists.RemoveTrackAsync(LikedPlaylistId, canonical.Id, CurrentOwnerId, ct).ConfigureAwait(false);
            canonical.InPlaylists.Remove(LikedPlaylistId);
        }

        _registry.UpdatePinStatus(canonical);

        OnDataChanged?.Invoke();
        OnTrackUpdated?.Invoke(canonical);
    }

    /// <summary>
    /// Возвращает список понравившихся треков текущего пользователя.
    /// </summary>
    /// <param name="limit">Максимальное количество возвращаемых записей.</param>
    /// <param name="offset">Смещение выборки.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Список моделей треков.</returns>
    public async Task<List<TrackInfo>> GetLikedTracksAsync(int limit = 100, int offset = 0, CancellationToken ct = default)
    {
        var tracks = await _tracks.GetLikedAsync(CurrentOwnerId, limit, offset, ct).ConfigureAwait(false);
        await HydrateAndRegisterTracksAsync(tracks, ct).ConfigureAwait(false);
        return tracks;
    }

    /// <summary>
    /// Возвращает общее количество понравившихся треков текущего пользователя.
    /// </summary>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Количество лайкнутых треков.</returns>
    public async Task<int> GetLikedCountAsync(CancellationToken ct = default) =>
        await _tracks.CountLikedAsync(CurrentOwnerId, ct).ConfigureAwait(false);

    /// <summary>
    /// Проверяет, является ли переданный идентификатор системным плейлистом «Понравившиеся».
    /// </summary>
    /// <param name="id">Идентификатор плейлиста.</param>
    /// <returns><c>true</c>, если идентификатор равен <see cref="LikedPlaylistId"/>; иначе — <c>false</c>.</returns>
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

    public Task<List<string>> GetPlaylistTrackIdsAsync(string playlistId, CancellationToken ct = default) =>
        _playlists.GetTrackIdsAsync(playlistId, CurrentOwnerId, ct);

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

    public Task<Playlist?> GetPlaylistAsync(string id, CancellationToken ct = default) =>
        _playlists.GetByIdAsync(id, CurrentOwnerId, ct);

    public Task<List<Playlist>> GetAllPlaylistsAsync(CancellationToken ct = default) =>
        _playlists.GetAllAsync(CurrentOwnerId, ct);

    public Task<List<(Playlist Playlist, int TrackCount)>> GetAllPlaylistsWithCountsAsync(CancellationToken ct = default) =>
        _playlists.GetAllWithCountsAsync(CurrentOwnerId, ct);

    public Task<bool> IsTrackInPlaylistAsync(string trackId, string playlistId, CancellationToken ct = default) =>
        _playlists.ContainsTrackAsync(playlistId, trackId, CurrentOwnerId, ct);

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

    public async Task ResetAsync(CancellationToken ct = default)
    {
        _registry.Clear();

        SqliteConnection.ClearAllPools();
        var dbPath = G.FilePath.Database;
        if (File.Exists(dbPath))
        {
            try { File.Delete(dbPath); } catch { }
            try { File.Delete(dbPath + "-wal"); } catch { }
            try { File.Delete(dbPath + "-shm"); } catch { }
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await connection.EnsureTablesCreatedAsync(ct).ConfigureAwait(false);
        await connection.MigrateSchemaAsync(ct).ConfigureAwait(false);
        await connection.OptimizeAsync(ct).ConfigureAwait(false);
        await connection.EnsureFtsTablesAsync(ct).ConfigureAwait(false);
        await connection.SetDatabaseVersionAsync(DatabaseExtensions.CurrentDbVersion, ct).ConfigureAwait(false);

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

        GC.SuppressFinalize(this);
    }

    #endregion
}