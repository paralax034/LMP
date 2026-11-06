using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace LMP.Core.Data;

public static class DatabaseExtensions
{
    /// <summary>
    /// Current version of the database schema.
    /// </summary>
    public const int CurrentDbVersion = 6;

    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADb = 26;

    /// <summary>
    /// Idempotent DDL script for schema tables without indexes.
    /// </summary>
    /// <remarks>
    /// Separated from <see cref="IndexesDdl"/>: in legacy databases, some columns (e.g., <c>OwnerId</c>)
    /// appear only after <see cref="MigrateSchemaAsync"/>, so their indexes are built after column migrations.
    /// </remarks>
    private const string TablesDdl = """
        CREATE TABLE IF NOT EXISTS "Notifications" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_Notifications" PRIMARY KEY,
            "TitleKey" TEXT NULL,
            "TitleRaw" TEXT NULL,
            "MessageKey" TEXT NULL,
            "MessageRaw" TEXT NULL,
            "MessageArgsJson" TEXT NULL,
            "RecommendationKey" TEXT NULL,
            "Severity" INTEGER NOT NULL,
            "IsRead" INTEGER NOT NULL,
            "TrackId" TEXT NULL,
            "TrackTitle" TEXT NULL,
            "ExceptionDetails" TEXT NULL,
            "AttemptsJson" TEXT NULL,
            "CreatedAt" TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS "Playlists" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_Playlists" PRIMARY KEY,
            "Name" TEXT NOT NULL,
            "YoutubeId" TEXT NULL,
            "Author" TEXT NULL,
            "ThumbnailUrl" TEXT NULL,
            "CustomColor" TEXT NULL,
            "ComputedColor" TEXT NULL,
            "Description" TEXT NULL,
            "OwnerId" TEXT NOT NULL DEFAULT '',
            "OwnerChannelId" TEXT NULL,
            "Ownership" INTEGER NOT NULL,
            "Visibility" INTEGER NOT NULL,
            "SyncMode" INTEGER NOT NULL,
            "ViewCount" INTEGER NULL,
            "ReleaseDate" TEXT NULL,
            "CloudTrackCount" INTEGER NULL,
            "LastSyncedAtUtc" TEXT NULL,
            "IsCloudUnavailable" INTEGER NOT NULL,
            "CreatedAt" TEXT NOT NULL,
            "UpdatedAt" TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS "RecentlyPlayed" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_RecentlyPlayed" PRIMARY KEY AUTOINCREMENT,
            "TrackId" TEXT NOT NULL,
            "PlayedAt" TEXT NOT NULL,
            "OwnerId" TEXT NOT NULL DEFAULT ''
        );

        CREATE TABLE IF NOT EXISTS "Settings" (
            "Key" TEXT NOT NULL CONSTRAINT "PK_Settings" PRIMARY KEY,
            "Value" TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS "Tracks" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_Tracks" PRIMARY KEY,
            "Title" TEXT NOT NULL,
            "Author" TEXT NOT NULL,
            "ChannelId" TEXT NULL,
            "Url" TEXT NOT NULL,
            "DurationTicks" INTEGER NOT NULL,
            "ThumbnailUrl" TEXT NOT NULL,
            "IsOfficialArtist" INTEGER NOT NULL,
            "IsMusic" INTEGER NOT NULL,
            "IsDisliked" INTEGER NOT NULL,
            "IsDownloaded" INTEGER NOT NULL,
            "LocalPath" TEXT NULL,
            "PreferredContainer" TEXT NULL,
            "PreferredBitrate" INTEGER NOT NULL,
            "RadioSeedId" TEXT NULL,
            "IntegratedLufs" REAL NULL,
            "IntegratedLufsSource" INTEGER NOT NULL DEFAULT 0,
            "EqualizerPresetId" TEXT NULL,
            "CreatedAt" TEXT NOT NULL,
            "UpdatedAt" TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS "LikedTracks" (
            "OwnerId" TEXT NOT NULL,
            "TrackId" TEXT NOT NULL,
            "LikedAt" TEXT NOT NULL,
            CONSTRAINT "PK_LikedTracks" PRIMARY KEY ("OwnerId", "TrackId"),
            CONSTRAINT "FK_LikedTracks_Tracks_TrackId" FOREIGN KEY ("TrackId") REFERENCES "Tracks" ("Id") ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS "PlaylistTracks" (
            "PlaylistId" TEXT NOT NULL,
            "TrackId" TEXT NOT NULL,
            "Position" INTEGER NOT NULL,
            "SetVideoId" TEXT NULL,
            CONSTRAINT "PK_PlaylistTracks" PRIMARY KEY ("PlaylistId", "TrackId"),
            CONSTRAINT "FK_PlaylistTracks_Playlists_PlaylistId" FOREIGN KEY ("PlaylistId") REFERENCES "Playlists" ("Id") ON DELETE CASCADE,
            CONSTRAINT "FK_PlaylistTracks_Tracks_TrackId" FOREIGN KEY ("TrackId") REFERENCES "Tracks" ("Id") ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS "CloudMutationOutbox" (
            "TargetId" TEXT NOT NULL,
            "OwnerId" TEXT NOT NULL,
            "PayloadJson" TEXT NOT NULL,
            "UpdatedAtTicks" INTEGER NOT NULL,
            "RetryCount" INTEGER NOT NULL DEFAULT 0,
            CONSTRAINT "PK_CloudMutationOutbox" PRIMARY KEY ("TargetId", "OwnerId")
        );
        """;

    /// <summary>
    /// Idempotent DDL script for schema indexes; executes only after all indexed columns are present.
    /// </summary>
    private const string IndexesDdl = """
        CREATE INDEX IF NOT EXISTS "IX_CloudMutationOutbox_OwnerId_UpdatedAtTicks" ON "CloudMutationOutbox" ("OwnerId", "UpdatedAtTicks");
        CREATE INDEX IF NOT EXISTS "IX_LikedTracks_OwnerId" ON "LikedTracks" ("OwnerId");
        CREATE INDEX IF NOT EXISTS "IX_LikedTracks_TrackId" ON "LikedTracks" ("TrackId");
        CREATE INDEX IF NOT EXISTS "IX_Notifications_CreatedAt" ON "Notifications" ("CreatedAt");
        CREATE INDEX IF NOT EXISTS "IX_Notifications_IsRead" ON "Notifications" ("IsRead");
        CREATE INDEX IF NOT EXISTS "IX_Playlists_OwnerId" ON "Playlists" ("OwnerId");
        CREATE INDEX IF NOT EXISTS "IX_PlaylistTracks_PlaylistId_Position" ON "PlaylistTracks" ("PlaylistId", "Position");
        CREATE INDEX IF NOT EXISTS "IX_PlaylistTracks_TrackId" ON "PlaylistTracks" ("TrackId");
        CREATE INDEX IF NOT EXISTS "IX_RecentlyPlayed_OwnerId" ON "RecentlyPlayed" ("OwnerId");
        CREATE INDEX IF NOT EXISTS "IX_RecentlyPlayed_PlayedAt" ON "RecentlyPlayed" ("PlayedAt");
        CREATE INDEX IF NOT EXISTS "IX_RecentlyPlayed_TrackId" ON "RecentlyPlayed" ("TrackId");
        CREATE INDEX IF NOT EXISTS "IX_Tracks_IsDownloaded" ON "Tracks" ("IsDownloaded");
        CREATE INDEX IF NOT EXISTS "IX_Tracks_Title_Author" ON "Tracks" ("Title", "Author");
        CREATE INDEX IF NOT EXISTS "IX_Tracks_UpdatedAt" ON "Tracks" ("UpdatedAt");
        """;

    public static async Task EnsureTablesCreatedAsync(this SqliteConnection context, CancellationToken ct = default)
    {
        bool closeOnExit = await EnsureConnectionOpenAsync(context, ct).ConfigureAwait(false);
        try
        {
            await ExecuteSqlAsync(context, TablesDdl, ct).ConfigureAwait(false);
            await ExecuteSqlAsync(context, IndexesDdl, ct).ConfigureAwait(false);
        }
        finally
        {
            if (closeOnExit)
                await context.CloseAsync().ConfigureAwait(false);
        }
    }

    public static async Task ApplySchemaMigrationAsync(this SqliteConnection context, int targetVersion, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetVersion);

        bool closeOnExit = await EnsureConnectionOpenAsync(context, ct).ConfigureAwait(false);
        try
        {
            await ExecuteSqlAsync(context, "BEGIN IMMEDIATE;", ct).ConfigureAwait(false);
            try
            {
                await ExecuteSqlAsync(context, TablesDdl, ct).ConfigureAwait(false);
                await context.MigrateSchemaAsync(ct).ConfigureAwait(false);
                await ExecuteSqlAsync(context, IndexesDdl, ct).ConfigureAwait(false);
                await context.SetDatabaseVersionAsync(targetVersion, ct).ConfigureAwait(false);
                await ExecuteSqlAsync(context, "COMMIT;", ct).ConfigureAwait(false);
            }
            catch
            {
                await RollbackIfActiveAsync(context).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            if (closeOnExit)
                await context.CloseAsync().ConfigureAwait(false);
        }
    }

    public static bool IsTransientLockError(this SqliteException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        int primary = ex.SqliteErrorCode & 0xFF;
        return primary is SqliteBusy or SqliteLocked;
    }

    public static bool IsCorruptionError(this SqliteException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        int primary = ex.SqliteErrorCode & 0xFF;
        return primary is SqliteCorrupt or SqliteNotADb;
    }

    /// <summary>
    /// Creates a transactionally consistent copy of the database via <c>VACUUM INTO</c>, including pages in <c>-wal</c>.
    /// </summary>
    /// <param name="dbPath">Path to the source database file.</param>
    /// <param name="backupPath">Path to the target backup file; an existing file is atomically replaced.</param>
    /// <remarks>
    /// Unlike <c>File.Copy</c>, this does not lose transactions from the WAL log and does not read the file mid-checkpoint.
    /// <c>VACUUM INTO</c> requires the target file to be absent, so the copy is written to a temporary file
    /// and renamed only upon success: an interrupted backup will not overwrite the previous one.
    /// </remarks>
    public static void CreateConsistentBackup(string dbPath, string backupPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(dbPath);
        ArgumentException.ThrowIfNullOrEmpty(backupPath);

        var tempPath = backupPath + ".tmp";
        File.Delete(tempPath);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        };

        using (var connection = new SqliteConnection(builder.ConnectionString))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "VACUUM INTO $path;";
            cmd.Parameters.AddWithValue("$path", tempPath);
            cmd.ExecuteNonQuery();
        }

        File.Move(tempPath, backupPath, overwrite: true);
    }

    public static async Task<int> GetDatabaseVersionAsync(this SqliteConnection context, CancellationToken ct = default)
    {
        bool closeOnExit = await EnsureConnectionOpenAsync(context, ct).ConfigureAwait(false);
        try
        {
            await using var cmd = context.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return result != null ? Convert.ToInt32(result, CultureInfo.InvariantCulture) : 0;
        }
        finally
        {
            if (closeOnExit)
                await context.CloseAsync().ConfigureAwait(false);
        }
    }

    public static async Task SetDatabaseVersionAsync(this SqliteConnection context, int version, CancellationToken ct = default)
    {
        bool closeOnExit = await EnsureConnectionOpenAsync(context, ct).ConfigureAwait(false);
        try
        {
            await using var cmd = context.CreateCommand();
            cmd.CommandText = $"PRAGMA user_version = {version};";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (closeOnExit)
                await context.CloseAsync().ConfigureAwait(false);
        }
    }

    public static async Task MigrateSchemaAsync(this SqliteConnection context, CancellationToken ct = default)
    {
        await AddColumnIfNotExistsAsync(context, "Playlists", "OwnerId", "TEXT NOT NULL DEFAULT ''", ct).ConfigureAwait(false);
        await AddColumnIfNotExistsAsync(context, "RecentlyPlayed", "OwnerId", "TEXT NOT NULL DEFAULT ''", ct).ConfigureAwait(false);
        await AddColumnIfNotExistsAsync(context, "Playlists", "CustomColor", "TEXT", ct).ConfigureAwait(false);
        await AddColumnIfNotExistsAsync(context, "Playlists", "ComputedColor", "TEXT", ct).ConfigureAwait(false);
        await AddColumnIfNotExistsAsync(context, "Playlists", "Description", "TEXT", ct).ConfigureAwait(false);
        await AddColumnIfNotExistsAsync(context, "PlaylistTracks", "SetVideoId", "TEXT", ct).ConfigureAwait(false);

        await EnsureLikedTracksTableAsync(context, ct).ConfigureAwait(false);
        await MigrateLegacyLikesAsync(context, ct).ConfigureAwait(false);

        await AddColumnIfNotExistsAsync(context, "Playlists", "OwnerChannelId", "TEXT", ct).ConfigureAwait(false);
        await AddColumnIfNotExistsAsync(context, "Playlists", "Ownership", "INTEGER NOT NULL DEFAULT 0", ct).ConfigureAwait(false);
        await AddColumnIfNotExistsAsync(context, "Playlists", "Visibility", "INTEGER NOT NULL DEFAULT 0", ct).ConfigureAwait(false);
        await AddColumnIfNotExistsAsync(context, "Playlists", "CloudTrackCount", "INTEGER", ct).ConfigureAwait(false);
        await AddColumnIfNotExistsAsync(context, "Playlists", "LastSyncedAtUtc", "TEXT", ct).ConfigureAwait(false);
        await AddColumnIfNotExistsAsync(context, "Playlists", "IsCloudUnavailable", "INTEGER NOT NULL DEFAULT 0", ct).ConfigureAwait(false);
        await AddColumnIfNotExistsAsync(context, "Playlists", "ViewCount", "INTEGER", ct).ConfigureAwait(false);
        await AddColumnIfNotExistsAsync(context, "Playlists", "ReleaseDate", "TEXT", ct).ConfigureAwait(false);
        await AddColumnIfNotExistsAsync(context, "Tracks", "IntegratedLufs", "REAL", ct).ConfigureAwait(false);
        await AddColumnIfNotExistsAsync(context, "Tracks", "IntegratedLufsSource", "INTEGER NOT NULL DEFAULT 0", ct).ConfigureAwait(false);
        await AddColumnIfNotExistsAsync(context, "Tracks", "EqualizerPresetId", "TEXT", ct).ConfigureAwait(false);

        bool closeOnExit = await EnsureConnectionOpenAsync(context, ct).ConfigureAwait(false);
        try
        {
            await using var cmd = context.CreateCommand();
            cmd.CommandText = """
                UPDATE Playlists SET ReleaseDate = NULL WHERE ReleaseDate IS NOT NULL AND ReleaseDate NOT GLOB '????-??-??';

                CREATE TABLE IF NOT EXISTS "CloudMutationOutbox" (
                    "TargetId" TEXT NOT NULL,
                    "OwnerId" TEXT NOT NULL,
                    "PayloadJson" TEXT NOT NULL,
                    "UpdatedAtTicks" INTEGER NOT NULL,
                    "RetryCount" INTEGER NOT NULL DEFAULT 0,
                    CONSTRAINT "PK_CloudMutationOutbox" PRIMARY KEY ("TargetId", "OwnerId")
                );
                CREATE INDEX IF NOT EXISTS "IX_CloudMutationOutbox_OwnerId_UpdatedAtTicks" ON "CloudMutationOutbox" ("OwnerId", "UpdatedAtTicks");
                """;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (closeOnExit)
                await context.CloseAsync().ConfigureAwait(false);
        }

        await MigrateCloudPublicOwnershipAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task MigrateCloudPublicOwnershipAsync(
        SqliteConnection context, CancellationToken ct)
    {
        bool closeOnExit = await EnsureConnectionOpenAsync(context, ct).ConfigureAwait(false);
        try
        {
            await using var cmd = context.CreateCommand();
            cmd.CommandText =
                "UPDATE Playlists SET Ownership = 2, Visibility = 3 WHERE SyncMode = 2 AND Ownership = 0;";
            int affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (affected > 0)
                Log.Info($"[DB] Migrated {affected} CloudPublic playlist(s) → Foreign + Public");
        }
        finally
        {
            if (closeOnExit && context.State == System.Data.ConnectionState.Open)
                await context.CloseAsync().ConfigureAwait(false);
        }
    }

    private static async Task AddColumnIfNotExistsAsync(
        SqliteConnection context,
        string tableName,
        string columnName,
        string columnType,
        CancellationToken ct)
    {
        bool closeOnExit = await EnsureConnectionOpenAsync(context, ct).ConfigureAwait(false);
        try
        {
            bool columnExists = false;
            await using (var cmd = context.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(" + tableName + ");";
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var name = reader.GetString(1);
                    if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
                    {
                        columnExists = true;
                        break;
                    }
                }
            }

            if (!columnExists)
            {
                var alterSql = string.Concat(
                    "ALTER TABLE ", tableName,
                    " ADD COLUMN ", columnName,
                    " ", columnType, ";");

                await ExecuteSqlAsync(context, alterSql, ct).ConfigureAwait(false);
                Log.Info(string.Concat("[DB] Added column ", tableName, ".", columnName, " (", columnType, ")"));
            }
        }
        finally
        {
            if (closeOnExit && context.State == System.Data.ConnectionState.Open)
                await context.CloseAsync().ConfigureAwait(false);
        }
    }

    private static async Task EnsureLikedTracksTableAsync(SqliteConnection context, CancellationToken ct)
    {
        const string sql = """
                CREATE TABLE IF NOT EXISTS LikedTracks (
                    OwnerId TEXT NOT NULL,
                    TrackId TEXT NOT NULL,
                    LikedAt TEXT NOT NULL,
                    PRIMARY KEY (OwnerId, TrackId),
                    FOREIGN KEY (TrackId) REFERENCES Tracks (Id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS IX_LikedTracks_OwnerId ON LikedTracks (OwnerId);
                """;

        bool closeOnExit = await EnsureConnectionOpenAsync(context, ct).ConfigureAwait(false);
        try
        {
            await using var cmd = context.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (closeOnExit)
                await context.CloseAsync().ConfigureAwait(false);
        }
    }

    private static async Task MigrateLegacyLikesAsync(SqliteConnection context, CancellationToken ct)
    {
        bool closeOnExit = await EnsureConnectionOpenAsync(context, ct).ConfigureAwait(false);
        try
        {
            bool hasIsLiked = false;
            await using (var cmd = context.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(Tracks);";
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (string.Equals(reader.GetString(1), "IsLiked", StringComparison.OrdinalIgnoreCase))
                    {
                        hasIsLiked = true;
                        break;
                    }
                }
            }

            if (!hasIsLiked)
                return;

            Log.Info("[DB] Migrating legacy likes from Tracks.IsLiked to LikedTracks...");

            int migrated;
            await using (var cmd = context.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT OR IGNORE INTO LikedTracks (OwnerId, TrackId, LikedAt)
                    SELECT '', Id, datetime('now') FROM Tracks WHERE IsLiked = 1;
                    """;
                migrated = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (migrated > 0)
            {
                Log.Info($"[DB] Successfully migrated {migrated} legacy likes.");
                await SaveSuccessMigrationNotificationAsync(context, ct).ConfigureAwait(false);
            }

            await ExecuteSqlAsync(context, "DROP INDEX IF EXISTS IX_Tracks_IsLiked;", ct).ConfigureAwait(false);
            await ExecuteSqlAsync(context, "ALTER TABLE Tracks DROP COLUMN IsLiked;", ct).ConfigureAwait(false);
            Log.Info("[DB] Legacy column Tracks.IsLiked and index IX_Tracks_IsLiked dropped.");
        }
        finally
        {
            if (closeOnExit && context.State == System.Data.ConnectionState.Open)
                await context.CloseAsync().ConfigureAwait(false);
        }
    }

    private static async Task SaveSuccessMigrationNotificationAsync(SqliteConnection context, CancellationToken ct)
    {
        bool closeOnExit = await EnsureConnectionOpenAsync(context, ct).ConfigureAwait(false);
        try
        {
            await using var cmd = context.CreateCommand();
            cmd.CommandText = """
                    INSERT INTO Notifications (
                        Id, TitleKey, TitleRaw, MessageKey, MessageRaw,
                        MessageArgsJson, RecommendationKey, Severity, IsRead,
                        TrackId, TrackTitle, ExceptionDetails, AttemptsJson, CreatedAt
                    )
                    VALUES (
                        @id, @titleKey, NULL, @messageKey, NULL,
                        NULL, NULL, @severity, 0,
                        NULL, NULL, NULL, NULL, @createdAt
                    );
                    """;

            AddParameter(cmd, "@id", Guid.NewGuid().ToString());
            AddParameter(cmd, "@titleKey", "Playlist_SyncComplete_Toast_Title");
            AddParameter(cmd, "@messageKey", "Sync_Success_Msg_LikedOnly");
            AddParameter(cmd, "@severity", (int)NotificationSeverity.Success);
            AddParameter(cmd, "@createdAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            Log.Info("[DB] Success migration notification saved to database");
        }
        finally
        {
            if (closeOnExit && context.State == System.Data.ConnectionState.Open)
                await context.CloseAsync().ConfigureAwait(false);
        }
    }

    public static async Task OptimizeAsync(this SqliteConnection context, CancellationToken ct = default)
    {
        bool closeOnExit = await EnsureConnectionOpenAsync(context, ct).ConfigureAwait(false);
        try
        {
            await using var cmd = context.CreateCommand();
            cmd.CommandText = """
                    PRAGMA journal_mode = WAL;
                    PRAGMA synchronous = NORMAL;
                    PRAGMA cache_size = -4000;
                    PRAGMA temp_store = FILE;
                    PRAGMA mmap_size = 0;
                    """;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (closeOnExit)
                await context.CloseAsync().ConfigureAwait(false);
        }
    }

    private static async Task ExecuteSqlAsync(SqliteConnection context, string sql, CancellationToken ct)
    {
        await using var cmd = context.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task RollbackIfActiveAsync(SqliteConnection context)
    {
        var handle = context.Handle;
        if (context.State != ConnectionState.Open || handle is null || SQLitePCL.raw.sqlite3_get_autocommit(handle) != 0)
            return;

        try
        {
            await ExecuteSqlAsync(context, "ROLLBACK;", CancellationToken.None).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            Log.Error($"[DB] Rollback of failed schema migration failed: {ex.Message}");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static async Task<bool> EnsureConnectionOpenAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddParameter(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}