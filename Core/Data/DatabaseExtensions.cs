using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace LMP.Core.Data;

public static class DatabaseExtensions
{
    /// <summary>
    /// Текущая версия схемы базы данных.
    /// </summary>
    public const int CurrentDbVersion = 4;

    /// <summary>
    /// Идемпотентно создает все необходимые таблицы и индексы базы данных в режиме Native AOT.
    /// </summary>
    /// <param name="context">Экземпляр контекста базы данных <see cref="LibraryDbContext"/>.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Задача, представляющая асинхронную операцию выполнения DDL-пакета.</returns>
    /// <remarks>
    /// Заменяет заблокированный в Native AOT метод <c>Database.EnsureCreated()</c>.
    /// Выполняет нативный составной SQL-скрипт схемы без обращения к design-time метаданным и рефлексии.
    /// Безопасен для многократного вызова благодаря директивам <c>IF NOT EXISTS</c>.
    /// </remarks>
    public static async Task EnsureTablesCreatedAsync(this SqliteConnection context, CancellationToken ct = default)
    {
        const string ddlSql = """
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

        bool closeOnExit = await EnsureConnectionOpenAsync(context, ct).ConfigureAwait(false);
        try
        {
            await using var cmd = context.CreateCommand();
            cmd.CommandText = ddlSql;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (closeOnExit)
                await context.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Извлекает текущую целочисленную версию схемы SQLite из заголовка базы данных.
    /// </summary>
    /// <param name="context">Активное подключение к базе данных SQLite.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Целочисленное значение прагмы user_version базы данных.</returns>
    /// <remarks>
    /// Выполняет прямой скалярный SQL-запрос без выделения промежуточных буферов памяти.
    /// </remarks>
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

    /// <summary>
    /// Записывает новое целочисленное значение версии схемы SQLite в заголовок файла БД.
    /// </summary>
    /// <param name="context">Активное подключение к базе данных SQLite.</param>
    /// <param name="version">Новый порядковый номер версии схемы.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Задача, представляющая асинхронную операцию фиксации версии базы данных.</returns>
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

    /// <summary>
    /// Применяет инкрементальные обновления колонок, индексов и данных существующей схемы.
    /// </summary>
    /// <param name="context">Активное подключение к базе данных SQLite.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Задача, представляющая асинхронную операцию миграции структуры таблиц.</returns>
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

        bool closeOnExit = await EnsureConnectionOpenAsync(context, ct).ConfigureAwait(false);
        try
        {
            await using var cmd = context.CreateCommand();
            cmd.CommandText = "UPDATE Playlists SET ReleaseDate = NULL WHERE ReleaseDate IS NOT NULL AND ReleaseDate NOT GLOB '????-??-??';";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (closeOnExit)
                await context.CloseAsync().ConfigureAwait(false);
        }

        await MigrateCloudPublicOwnershipAsync(context, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Выполняет внутреннюю миграцию флагов владения и видимости для публичных облачных плейлистов.
    /// </summary>
    /// <param name="context">Активное подключение к базе данных SQLite.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Задача, представляющая асинхронную операцию миграции флагов плейлистов.</returns>
    private static async Task MigrateCloudPublicOwnershipAsync(
        SqliteConnection context, CancellationToken ct)
    {
        try
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
        catch (Exception ex)
        {
            Log.Warn($"[DB] CloudPublic ownership migration warning: {ex.Message}");
        }
    }

    /// <summary>
    /// Проверяет наличие колонки через PRAGMA table_info и добавляет её при отсутствии через ALTER TABLE.
    /// </summary>
    /// <param name="context">Активное подключение к базе данных SQLite.</param>
    /// <param name="tableName">Имя проверяемой таблицы.</param>
    /// <param name="columnName">Имя добавляемой колонки.</param>
    /// <param name="columnType">Тип данных и ограничения SQLite для создаваемой колонки.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Задача, представляющая асинхронную операцию проверки и изменения схемы таблицы.</returns>
    private static async Task AddColumnIfNotExistsAsync(
        SqliteConnection context,
        string tableName,
        string columnName,
        string columnType,
        CancellationToken ct)
    {
        try
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

                    await using var alterCmd = context.CreateCommand();
                    alterCmd.CommandText = alterSql;
                    await alterCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                    Log.Info(string.Concat("[DB] Added column ", tableName, ".", columnName, " (", columnType, ")"));
                }
            }
            finally
            {
                if (closeOnExit && context.State == System.Data.ConnectionState.Open)
                    await context.CloseAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Error(string.Concat("[DB] Migration failed for ", tableName, ".", columnName, ": ", ex.Message));
        }
    }

    /// <summary>
    /// Создает персистентную таблицу LikedTracks и первичный индекс по пользователю при их отсутствии.
    /// </summary>
    /// <param name="context">Активное подключение к базе данных SQLite.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Задача, представляющая асинхронную операцию создания таблицы лайков.</returns>
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

    /// <summary>
    /// Переносит исторические лайки из устаревшей колонки Tracks.IsLiked в таблицу LikedTracks.
    /// </summary>
    /// <param name="context">Активное подключение к базе данных SQLite.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Задача, представляющая асинхронную операцию миграции устаревших лайков.</returns>
    private static async Task MigrateLegacyLikesAsync(SqliteConnection context, CancellationToken ct)
    {
        try
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

                if (hasIsLiked)
                {
                    Log.Info("[DB] Migrating legacy likes from Tracks.IsLiked to LikedTracks...");

                    int migrated = 0;
                    await using (var cmd = context.CreateCommand())
                    {
                        cmd.CommandText = """
                                INSERT OR IGNORE INTO LikedTracks (OwnerId, TrackId, LikedAt)
                                SELECT '', Id, datetime('now') FROM Tracks WHERE IsLiked = 1;
                                """;
                        migrated = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        if (migrated > 0)
                        {
                            Log.Info($"[DB] Successfully migrated {migrated} legacy likes.");
                            await SaveSuccessMigrationNotificationAsync(context, ct).ConfigureAwait(false);
                        }
                    }

                    try
                    {
                        await using var cmdDropIdx = context.CreateCommand();
                        cmdDropIdx.CommandText = "DROP INDEX IF EXISTS IX_Tracks_IsLiked;";
                        await cmdDropIdx.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        Log.Info("[DB] Index IX_Tracks_IsLiked dropped to prevent schema lock during migration.");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[DB] Failed to drop index IX_Tracks_IsLiked: {ex.Message}");
                    }

                    try
                    {
                        await using var cmdDropCol = context.CreateCommand();
                        cmdDropCol.CommandText = "ALTER TABLE Tracks DROP COLUMN IsLiked;";
                        await cmdDropCol.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        Log.Info("[DB] Legacy column Tracks.IsLiked dropped successfully.");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[DB] Could not drop legacy column Tracks.IsLiked: {ex.Message}");
                    }
                }
            }
            finally
            {
                if (closeOnExit && context.State == System.Data.ConnectionState.Open)
                    await context.CloseAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[DB] Legacy likes migration failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Сохраняет системное уведомление об успешной миграции структуры лайков в базу данных.
    /// </summary>
    /// <param name="context">Активное подключение к базе данных SQLite.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Задача, представляющая асинхронную операцию сохранения уведомления.</returns>
    private static async Task SaveSuccessMigrationNotificationAsync(SqliteConnection context, CancellationToken ct)
    {
        try
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
        catch (Exception ex)
        {
            Log.Warn($"[DB] Failed to save success migration notification: {ex.Message}");
        }
    }

    /// <summary>
    /// Применяет персистентные оптимизации базы данных SQLite (WAL-журналирование и лимиты памяти).
    /// </summary>
    /// <param name="context">Активное подключение к базе данных SQLite.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Задача, представляющая асинхронную операцию оптимизации параметров базы данных.</returns>
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

    /// <summary>
    /// Гарантирует перевод подключения SQLite в открытое состояние без дублирующих системных вызовов.
    /// </summary>
    /// <param name="connection">Экземпляр подключения <see cref="SqliteConnection"/>.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns><c>true</c>, если подключение было открыто данным методом; <c>false</c>, если оно уже было открыто.</returns>
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

    /// <summary>
    /// Добавляет типизированный параметр к выполняемой команде SQLite.
    /// </summary>
    /// <param name="cmd">Команда базы данных <see cref="DbCommand"/>.</param>
    /// <param name="name">Уникальное имя SQL-параметра.</param>
    /// <param name="value">Значение параметра или <see cref="DBNull.Value"/> при его отсутствии.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddParameter(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}