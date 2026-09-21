using System.Text.Json;
using LMP.Core.Data;
using LMP.Core.Data.Repositories;

namespace LMP.Core.Services;

/// <summary>
/// Сервис однократной миграции устаревших данных библиотеки из формата JSON в локальную базу данных SQLite.
/// </summary>
/// <remarks>
/// Изолирует логику миграции устаревших структур данных, предотвращая разрастание сервисов ядра библиотеки.
/// Вызывается исключительно на этапе инициализации приложения при обнаружении файла устаревшей БД.
/// </remarks>
public sealed class LegacyMigrationService
{
    private readonly ITrackRepository _tracks;
    private readonly IPlaylistRepository _playlists;
    private readonly ISettingsRepository _settings;
    private readonly CookieAuthService _auth;

    /// <summary>
    /// Инициализирует новый экземпляр сервиса миграции устаревших данных.
    /// </summary>
    /// <param name="tracks">Репозиторий метаданных треков.</param>
    /// <param name="playlists">Репозиторий управления плейлистами.</param>
    /// <param name="settings">Репозиторий настроек приложения.</param>
    /// <param name="auth">Сервис аутентификации для определения контекста владельца данных.</param>
    public LegacyMigrationService(
        ITrackRepository tracks,
        IPlaylistRepository playlists,
        ISettingsRepository settings,
        CookieAuthService auth)
    {
        _tracks = tracks;
        _playlists = playlists;
        _settings = settings;
        _auth = auth;
    }

    private string CurrentOwnerId => _auth.State.DisplayId;

    /// <summary>
    /// Выполняет асинхронную миграцию данных из JSON-файла устаревшей библиотеки в базу данных SQLite.
    /// </summary>
    /// <param name="path">Физический путь к файлу базы данных JSON.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Экземпляр преобразованных настроек <see cref="AppSettings"/> либо <c>null</c>, если миграция не удалась.</returns>
    /// <remarks>
    /// Операция является транзакционно-безопасной на уровне файлов: при успешном завершении
    /// исходный JSON-файл переименовывается с добавлением временной метки резервной копии.
    /// </remarks>
    public async Task<AppSettings?> MigrateFromJsonAsync(string path, CancellationToken ct = default)
    {
        try
        {
            Log.Info("[Migration] Starting JSON -> SQLite migration...");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var legacy = JsonSerializer.Deserialize(json, AppJsonContext.Default.LegacyLibraryData);
            if (legacy == null) return null;

            var migratedTrackIds = new HashSet<string>(StringComparer.Ordinal);

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

            var settings = MapLegacySettings(legacy);
            await _settings.SetAsync("AppSettings", settings, ct).ConfigureAwait(false);

            var backup = path + $".migrated.{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(path, backup);

            sw.Stop();
            Log.Info($"[Migration] Complete in {sw.ElapsedMilliseconds}ms.");
            return settings;
        }
        catch (Exception ex)
        {
            Log.Error($"[Migration] Failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Преобразует устаревшую структуру настроек библиотеки в актуальную модель <see cref="AppSettings"/>.
    /// </summary>
    /// <param name="d">Экземпляр модели устаревших данных.</param>
    /// <returns>Инициализированная модель настроек приложения.</returns>
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
}