using System.Text.Json;
using LMP.Core.Data;
using LMP.Core.Data.Repositories;

namespace LMP.Core.Services;

/// <summary>
/// One-time migration service for legacy library data from JSON format to a local SQLite database.
/// </summary>
/// <remarks>
/// Isolates legacy data structure migration logic to prevent bloating library core services.
/// Invoked exclusively during the application initialization phase when a legacy database file is detected.
/// </remarks>
public sealed class LegacyMigrationService
{
    private readonly ITrackRepository _tracks;
    private readonly IPlaylistRepository _playlists;
    private readonly ISettingsRepository _settings;
    private readonly CookieAuthService _auth;

    /// <summary>
    /// Initializes a new instance of the legacy data migration service.
    /// </summary>
    /// <param name="tracks">Track metadata repository.</param>
    /// <param name="playlists">Playlist management repository.</param>
    /// <param name="settings">Application settings repository.</param>
    /// <param name="auth">Authentication service to determine data owner context.</param>
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
    /// Asynchronously migrates data from a legacy library JSON file to the SQLite database.
    /// </summary>
    /// <param name="path">Physical path to the JSON database file.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>An instance of converted <see cref="AppSettings"/>, or <c>null</c> if migration failed.</returns>
    /// <remarks>
    /// The operation is file-level transactionally safe: upon successful completion,
    /// the source JSON file is renamed with a backup timestamp.
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
    /// Converts a legacy library settings structure into the current <see cref="AppSettings"/> model.
    /// </summary>
    /// <param name="d">Instance of the legacy data model.</param>
    /// <returns>An initialized application settings model.</returns>
    private static AppSettings MapLegacySettings(LegacyLibraryData d) => new()
    {
        Volume = d.Volume > 0 && d.Volume <= 1.0f ? (int)(d.Volume * 100) : (int)d.Volume,
        ShuffleEnabled = d.ShuffleEnabled,
        RepeatMode = d.RepeatMode,
        MaxVolumeLimit = d.MaxVolumeLimit,
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