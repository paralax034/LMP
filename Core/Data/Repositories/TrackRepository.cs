using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using LMP.Core.Audio.Normalization;
using LMP.Core.Youtube.Utils;
using Microsoft.Data.Sqlite;

namespace LMP.Core.Data.Repositories;

/// <summary>
/// Реализация репозитория управления треками в SQLite-хранилище.
/// Использует асинхронный контекст EF Core Factory для предотвращения блокировок потоков.
/// </summary>
public sealed partial class TrackRepository : ITrackRepository
{
    private static readonly string[] ParameterNames500 = GenerateParameterNames(500);
    private static readonly string QueryChunk500 = $"SELECT {TrackColumnsSelect} FROM Tracks t WHERE t.Id IN ({string.Join(',', ParameterNames500)});";

    private readonly ISqliteConnectionFactory _factory;

    private const string TrackColumnsSelect = """
        t.Id, t.Title, t.Author, t.ChannelId, t.Url, t.DurationTicks, t.ThumbnailUrl,
        t.IsOfficialArtist, t.IsMusic, t.IsDisliked, t.IsDownloaded, t.LocalPath,
        t.PreferredContainer, t.PreferredBitrate, t.RadioSeedId, t.IntegratedLufs,
        t.IntegratedLufsSource, t.CreatedAt, t.UpdatedAt
        """;

    /// <summary>
    /// Инициализирует новый экземпляр репозитория треков на базе чистых подключений SQLite.
    /// </summary>
    /// <param name="factory">Фабрика нативных подключений SQLite с контролем памяти.</param>
    public TrackRepository(ISqliteConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    #region Read

    /// <inheritdoc />
    public async Task<TrackInfo?> GetByIdAsync(string id, string ownerId, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        TrackInfo? track = null;

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT {TrackColumnsSelect} FROM Tracks t WHERE t.Id = @id LIMIT 1;";
            AddParameter(cmd, "@id", id);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                track = ReadTrackInfo(reader);
            }
        }

        if (track is null) return null;

        track.IsLiked = await CheckIsLikedAsync(connection, id, ownerId, ct).ConfigureAwait(false);
        return track;
    }

    /// <inheritdoc />
    public async Task<List<TrackInfo>> GetByIdsAsync(IEnumerable<string> ids, string ownerId, CancellationToken ct = default)
    {
        var idList = ids as IList<string> ?? [.. ids];
        if (idList.Count == 0) return [];

        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        var entities = new Dictionary<string, TrackInfo>(idList.Count, StringComparer.Ordinal);

        // Пакетная выборка чанками по 500 для предотвращения превышения лимитов SQLite host parameters
        const int chunkSize = 500;
        for (int i = 0; i < idList.Count; i += chunkSize)
        {
            int count = Math.Min(chunkSize, idList.Count - i);
            await using var cmd = connection.CreateCommand();

            if (count == chunkSize)
            {
                cmd.CommandText = QueryChunk500;
                for (int j = 0; j < chunkSize; j++)
                {
                    AddParameter(cmd, ParameterNames500[j], idList[i + j]);
                }
            }
            else
            {
                var paramNames = new string[count];
                for (int j = 0; j < count; j++)
                {
                    var paramName = ParameterNames500[j];
                    paramNames[j] = paramName;
                    AddParameter(cmd, paramName, idList[i + j]);
                }

                cmd.CommandText = $"SELECT {TrackColumnsSelect} FROM Tracks t WHERE t.Id IN ({string.Join(',', paramNames)});";
            }

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var track = ReadTrackInfo(reader);
                entities[track.Id] = track;
            }
        }

        var likedTrackIds = await GetLikedTrackIdsSubsetAsync(connection, entities.Keys, ownerId, ct).ConfigureAwait(false);

        var result = new List<TrackInfo>(idList.Count);
        for (int i = 0; i < idList.Count; i++)
        {
            var id = idList[i];
            if (entities.TryGetValue(id, out var model))
            {
                model.IsLiked = likedTrackIds.Contains(id);
                result.Add(model);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<List<TrackInfo>> SearchAsync(string query, string ownerId, int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        var models = new List<TrackInfo>(limit);
        var pattern = $"%{query}%";

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT {TrackColumnsSelect}
                FROM Tracks t
                WHERE t.Title LIKE @pattern OR t.Author LIKE @pattern
                ORDER BY t.Title
                LIMIT @limit OFFSET @offset;
                """;

            AddParameter(cmd, "@pattern", pattern);
            AddParameter(cmd, "@limit", limit);
            AddParameter(cmd, "@offset", offset);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                models.Add(ReadTrackInfo(reader));
            }
        }

        if (models.Count > 0)
        {
            var trackIds = models.Select(m => m.Id).ToList();
            var likedTrackIds = await GetLikedTrackIdsSubsetAsync(connection, trackIds, ownerId, ct).ConfigureAwait(false);

            for (int i = 0; i < models.Count; i++)
            {
                models[i].IsLiked = likedTrackIds.Contains(models[i].Id);
            }
        }

        return models;
    }

    /// <inheritdoc />
    public async Task<List<TrackInfo>> GetLikedAsync(string ownerId, int limit = 100, int offset = 0, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        var models = new List<TrackInfo>(limit);
        bool guest = IsGuest(ownerId);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {TrackColumnsSelect}
            FROM LikedTracks lt
            INNER JOIN Tracks t ON lt.TrackId = t.Id
            WHERE (@isGuest = 1 AND (lt.OwnerId = '' OR lt.OwnerId = 'guest'))
               OR (@isGuest = 0 AND lt.OwnerId = @ownerId)
            ORDER BY lt.LikedAt DESC
            LIMIT @limit OFFSET @offset;
            """;

        AddParameter(cmd, "@isGuest", guest ? 1 : 0);
        AddParameter(cmd, "@ownerId", ownerId ?? string.Empty);
        AddParameter(cmd, "@limit", limit);
        AddParameter(cmd, "@offset", offset);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var model = ReadTrackInfo(reader);
            model.IsLiked = true;
            models.Add(model);
        }

        return models;
    }

    /// <inheritdoc />
    public async Task<List<TrackInfo>> GetDownloadedAsync(string ownerId, int limit = 100, int offset = 0, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        var models = new List<TrackInfo>(limit);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT {TrackColumnsSelect}
                FROM Tracks t
                WHERE t.IsDownloaded = 1
                ORDER BY t.UpdatedAt DESC
                LIMIT @limit OFFSET @offset;
                """;

            AddParameter(cmd, "@limit", limit);
            AddParameter(cmd, "@offset", offset);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                models.Add(ReadTrackInfo(reader));
            }
        }

        if (models.Count > 0)
        {
            var trackIds = models.Select(m => m.Id).ToList();
            var likedTrackIds = await GetLikedTrackIdsSubsetAsync(connection, trackIds, ownerId, ct).ConfigureAwait(false);

            for (int i = 0; i < models.Count; i++)
            {
                models[i].IsLiked = likedTrackIds.Contains(models[i].Id);
            }
        }

        return models;
    }

    /// <inheritdoc />
    public async Task<List<TrackInfo>> GetRecentlyPlayedAsync(string ownerId, int limit = 50, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        var recentIds = new List<string>(limit);
        bool guest = IsGuest(ownerId);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT r.TrackId
                FROM RecentlyPlayed r
                WHERE (@isGuest = 1 AND (r.OwnerId = '' OR r.OwnerId = 'guest'))
                   OR (@isGuest = 0 AND r.OwnerId = @ownerId)
                ORDER BY r.PlayedAt DESC
                LIMIT @limit;
                """;

            AddParameter(cmd, "@isGuest", guest ? 1 : 0);
            AddParameter(cmd, "@ownerId", ownerId ?? string.Empty);
            AddParameter(cmd, "@limit", limit);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                recentIds.Add(reader.GetString(0));
            }
        }

        if (recentIds.Count == 0) return [];

        return await GetByIdsAsync(recentIds, ownerId ?? string.Empty, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Tracks;";
        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(scalar);
    }

    /// <inheritdoc />
    public async Task<int> CountLikedAsync(string ownerId, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        bool guest = IsGuest(ownerId);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM LikedTracks
            WHERE (@isGuest = 1 AND (OwnerId = '' OR OwnerId = 'guest'))
               OR (@isGuest = 0 AND OwnerId = @ownerId);
            """;

        AddParameter(cmd, "@isGuest", guest ? 1 : 0);
        AddParameter(cmd, "@ownerId", ownerId ?? string.Empty);

        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(scalar);
    }

    /// <inheritdoc />
    public async Task<int> CountLocalAsync(CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Tracks WHERE Id LIKE 'local_%' OR IsDownloaded = 1;";
        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(scalar);
    }

    #endregion

    #region Write

    /// <inheritdoc />
    public async Task UpsertAsync(TrackInfo track, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = GetUpsertSql();
        BindUpsertParameters(cmd, track, DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpsertBatchAsync(IEnumerable<TrackInfo> tracks, CancellationToken ct = default)
    {
        var trackList = tracks as IList<TrackInfo> ?? [.. tracks];
        if (trackList.Count == 0) return;

        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            var now = DateTime.UtcNow;

            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = GetUpsertSql();

            for (int i = 0; i < trackList.Count; i++)
            {
                cmd.Parameters.Clear();
                BindUpsertParameters(cmd, trackList[i], now);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Tracks WHERE Id = @id;";
        AddParameter(cmd, "@id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetLikedAsync(string id, string ownerId, bool liked, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        bool guest = IsGuest(ownerId);
        await using var cmd = connection.CreateCommand();

        if (liked)
        {
            cmd.CommandText = """
                INSERT OR IGNORE INTO LikedTracks (OwnerId, TrackId, LikedAt)
                VALUES (@ownerId, @id, @likedAt);
                """;
            AddParameter(cmd, "@ownerId", ownerId ?? string.Empty);
            AddParameter(cmd, "@id", id);
            AddParameter(cmd, "@likedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        }
        else
        {
            cmd.CommandText = """
                DELETE FROM LikedTracks
                WHERE TrackId = @id
                  AND ((@isGuest = 1 AND (OwnerId = '' OR OwnerId = 'guest'))
                    OR (@isGuest = 0 AND OwnerId = @ownerId));
                """;
            AddParameter(cmd, "@id", id);
            AddParameter(cmd, "@isGuest", guest ? 1 : 0);
            AddParameter(cmd, "@ownerId", ownerId ?? string.Empty);
        }

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetDownloadedAsync(string id, bool downloaded, string? localPath, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Tracks
            SET IsDownloaded = @downloaded,
                LocalPath = @localPath,
                UpdatedAt = @updatedAt
            WHERE Id = @id;
            """;

        AddParameter(cmd, "@downloaded", downloaded ? 1 : 0);
        AddParameter(cmd, "@localPath", (object?)localPath ?? DBNull.Value);
        AddParameter(cmd, "@updatedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        AddParameter(cmd, "@id", id);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveNormalizationMetadataAsync(
        string id,
        float integratedLufs,
        int source,
        CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Tracks
            SET IntegratedLufs = @lufs,
                IntegratedLufsSource = @source,
                UpdatedAt = @updatedAt
            WHERE Id = @id;
            """;

        AddParameter(cmd, "@lufs", integratedLufs);
        AddParameter(cmd, "@source", source);
        AddParameter(cmd, "@updatedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        AddParameter(cmd, "@id", id);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    #endregion

    #region History

    /// <inheritdoc />
    public async Task AddToHistoryAsync(string trackId, string ownerId, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            bool guest = IsGuest(ownerId);
            var nowStr = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            // 1. Удаление дубликата
            await using (var cmdDel = connection.CreateCommand())
            {
                cmdDel.Transaction = transaction;
                cmdDel.CommandText = """
                    DELETE FROM RecentlyPlayed
                    WHERE TrackId = @trackId
                      AND ((@isGuest = 1 AND (OwnerId = '' OR OwnerId = 'guest'))
                        OR (@isGuest = 0 AND OwnerId = @ownerId));
                    """;
                AddParameter(cmdDel, "@trackId", trackId);
                AddParameter(cmdDel, "@isGuest", guest ? 1 : 0);
                AddParameter(cmdDel, "@ownerId", ownerId ?? string.Empty);
                await cmdDel.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // 2. Вставка новой записи
            await using (var cmdIns = connection.CreateCommand())
            {
                cmdIns.Transaction = transaction;
                cmdIns.CommandText = """
                    INSERT INTO RecentlyPlayed (TrackId, OwnerId, PlayedAt)
                    VALUES (@trackId, @ownerId, @playedAt);
                    """;
                AddParameter(cmdIns, "@trackId", trackId);
                AddParameter(cmdIns, "@ownerId", ownerId ?? string.Empty);
                AddParameter(cmdIns, "@playedAt", nowStr);
                await cmdIns.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // 3. Пакетная очистка старых записей сверх лимита 100
            await using (var cmdPrune = connection.CreateCommand())
            {
                cmdPrune.Transaction = transaction;
                cmdPrune.CommandText = """
                    DELETE FROM RecentlyPlayed
                    WHERE Id IN (
                        SELECT Id FROM RecentlyPlayed
                        WHERE (@isGuest = 1 AND (OwnerId = '' OR OwnerId = 'guest'))
                           OR (@isGuest = 0 AND OwnerId = @ownerId)
                        ORDER BY PlayedAt DESC
                        LIMIT -1 OFFSET 100
                    );
                    """;
                AddParameter(cmdPrune, "@isGuest", guest ? 1 : 0);
                AddParameter(cmdPrune, "@ownerId", ownerId ?? string.Empty);
                await cmdPrune.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task ClearHistoryAsync(string ownerId, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        bool guest = IsGuest(ownerId);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM RecentlyPlayed
            WHERE (@isGuest = 1 AND (OwnerId = '' OR OwnerId = 'guest'))
               OR (@isGuest = 0 AND OwnerId = @ownerId);
            """;
        AddParameter(cmd, "@isGuest", guest ? 1 : 0);
        AddParameter(cmd, "@ownerId", ownerId ?? string.Empty);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<List<TrackInfo>> GetAllAsync(string ownerId, int limit = 10000, int offset = 0, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        var models = new List<TrackInfo>(limit);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT {TrackColumnsSelect}
                FROM Tracks t
                ORDER BY t.UpdatedAt DESC
                LIMIT @limit OFFSET @offset;
                """;
            AddParameter(cmd, "@limit", limit);
            AddParameter(cmd, "@offset", offset);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                models.Add(ReadTrackInfo(reader));
            }
        }

        if (models.Count > 0)
        {
            var trackIds = models.Select(m => m.Id).ToList();
            var likedTrackIds = await GetLikedTrackIdsSubsetAsync(connection, trackIds, ownerId, ct).ConfigureAwait(false);

            for (int i = 0; i < models.Count; i++)
            {
                models[i].IsLiked = likedTrackIds.Contains(models[i].Id);
            }
        }

        return models;
    }

    /// <inheritdoc />
    public async Task<List<TrackInfo>> GetLocalTracksAsync(string ownerId, int limit = 1000, int offset = 0, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        var models = new List<TrackInfo>(limit);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT {TrackColumnsSelect}
                FROM Tracks t
                WHERE t.Id LIKE 'local_%' OR t.IsDownloaded = 1
                ORDER BY t.UpdatedAt DESC
                LIMIT @limit OFFSET @offset;
                """;
            AddParameter(cmd, "@limit", limit);
            AddParameter(cmd, "@offset", offset);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                models.Add(ReadTrackInfo(reader));
            }
        }

        if (models.Count > 0)
        {
            var trackIds = models.Select(m => m.Id).ToList();
            var likedTrackIds = await GetLikedTrackIdsSubsetAsync(connection, trackIds, ownerId, ct).ConfigureAwait(false);

            for (int i = 0; i < models.Count; i++)
            {
                models[i].IsLiked = likedTrackIds.Contains(models[i].Id);
            }
        }

        return models;
    }

    #endregion

    private static AudioFormat? ParsePreferredFormat(string? container)
    {
        var format = YoutubeIdHelper.MapContainerToFormat(container);
        return format == AudioFormat.Unknown ? null : format;
    }

    private static string? PersistPreferredFormat(AudioFormat? format)
    {
        return format is { } value && value != AudioFormat.Unknown
            ? value.ToContainerName()
            : null;
    }

    #region Helpers

    /// <summary>
    /// Вспомогательный предикат для выявления гостевой или пустой сессии, подлежащих слиянию в единый профиль.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsGuest(string ownerId) => string.IsNullOrEmpty(ownerId) || ownerId == "guest";

    private static string[] GenerateParameterNames(int count)
    {
        var names = new string[count];
        for (int i = 0; i < count; i++)
            names[i] = $"@p{i}";
        return names;
    }

    /// <summary>
    /// Вычитывает модель трека <see cref="TrackInfo"/> напрямую из активного чтения <see cref="DbDataReader"/>.
    /// </summary>
    /// <param name="reader">Экземпляр открытого ридера SQLite.</param>
    /// <param name="isLiked">Начальное состояние флага лайка.</param>
    /// <returns>Экземпляр модели <see cref="TrackInfo"/>.</returns>
    /// <remarks>
    /// Обеспечивает прямое сопоставление по индексам колонок без рефлексии и промежуточных аллокаций.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TrackInfo ReadTrackInfo(DbDataReader reader, bool isLiked = false)
    {
        var id = reader.GetString(0);
        var title = reader.GetString(1);
        var author = reader.GetString(2);
        var channelId = reader.IsDBNull(3) ? null : reader.GetString(3);
        var url = reader.GetString(4);
        var durationTicks = reader.GetInt64(5);
        var thumbnailUrl = reader.GetString(6);
        var isOfficialArtist = reader.GetInt32(7) != 0;
        var isMusic = reader.GetInt32(8) != 0;
        var isDisliked = reader.GetInt32(9) != 0;
        var isDownloaded = reader.GetInt32(10) != 0;
        var localPath = reader.IsDBNull(11) ? null : reader.GetString(11);
        var preferredContainer = reader.IsDBNull(12) ? null : reader.GetString(12);
        var preferredBitrate = reader.GetInt32(13);
        var radioSeedId = reader.IsDBNull(14) ? null : reader.GetString(14);
        float integratedLufs = reader.IsDBNull(15) ? float.NaN : Convert.ToSingle(reader.GetValue(15), CultureInfo.InvariantCulture);
        var integratedLufsSource = reader.GetInt32(16);

        return new TrackInfo
        {
            Id = id,
            Title = title,
            Author = author,
            ChannelId = channelId,
            Url = url,
            Duration = TimeSpan.FromTicks(durationTicks),
            ThumbnailUrl = thumbnailUrl,
            IsOfficialArtist = isOfficialArtist,
            IsMusic = isMusic,
            IsDisliked = isDisliked,
            IsDownloaded = isDownloaded,
            LocalPath = localPath,
            PreferredFormat = ParsePreferredFormat(preferredContainer),
            PreferredBitrate = preferredBitrate,
            RadioSeedId = radioSeedId,
            IntegratedLufs = integratedLufs,
            IntegratedLufsSource = (LoudnessSource)integratedLufsSource,
            IsLiked = isLiked
        };
    }

    /// <summary>
    /// Проверяет наличие отметки "Мне нравится" для трека в рамках текущего владельца.
    /// </summary>
    private static async Task<bool> CheckIsLikedAsync(DbConnection connection, string trackId, string ownerId, CancellationToken ct)
    {
        bool guest = IsGuest(ownerId);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM LikedTracks
            WHERE TrackId = @id
              AND ((@isGuest = 1 AND (OwnerId = '' OR OwnerId = 'guest'))
                OR (@isGuest = 0 AND OwnerId = @ownerId))
            LIMIT 1;
            """;

        AddParameter(cmd, "@id", trackId);
        AddParameter(cmd, "@isGuest", guest ? 1 : 0);
        AddParameter(cmd, "@ownerId", ownerId ?? string.Empty);

        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return scalar is not null && scalar != DBNull.Value;
    }

    /// <summary>
    /// Пакетно извлекает идентификаторы понравившихся треков для переданной выборки ID.
    /// </summary>
    private static async Task<HashSet<string>> GetLikedTrackIdsSubsetAsync(
        DbConnection connection,
        IEnumerable<string> trackIds,
        string ownerId,
        CancellationToken ct)
    {
        var idList = trackIds as IList<string> ?? [.. trackIds];
        if (idList.Count == 0) return [];

        var likedSet = new HashSet<string>(StringComparer.Ordinal);
        bool guest = IsGuest(ownerId);

        const int chunkSize = 500;
        for (int i = 0; i < idList.Count; i += chunkSize)
        {
            int count = Math.Min(chunkSize, idList.Count - i);
            await using var cmd = connection.CreateCommand();

            var paramNames = new string[count];
            for (int j = 0; j < count; j++)
            {
                var paramName = $"@id{j}";
                paramNames[j] = paramName;
                AddParameter(cmd, paramName, idList[i + j]);
            }

            cmd.CommandText = $"""
                SELECT TrackId FROM LikedTracks
                WHERE TrackId IN ({string.Join(',', paramNames)})
                  AND ((@isGuest = 1 AND (OwnerId = '' OR OwnerId = 'guest'))
                    OR (@isGuest = 0 AND OwnerId = @ownerId));
                """;

            AddParameter(cmd, "@isGuest", guest ? 1 : 0);
            AddParameter(cmd, "@ownerId", ownerId ?? string.Empty);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                likedSet.Add(reader.GetString(0));
            }
        }

        return likedSet;
    }

    /// <summary>
    /// Добавляет строго типизированный параметр к команде SQLite.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddParameter(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    /// <summary>
    /// Возвращает атомарный SQL запрос вставки или обновления трека.
    /// </summary>
    private static string GetUpsertSql() => """
        INSERT INTO Tracks (
            Id, Title, Author, ChannelId, Url, DurationTicks, ThumbnailUrl,
            IsOfficialArtist, IsMusic, IsDisliked, IsDownloaded, LocalPath,
            PreferredContainer, PreferredBitrate, RadioSeedId, IntegratedLufs,
            IntegratedLufsSource, CreatedAt, UpdatedAt
        )
        VALUES (
            @id, @title, @author, @channelId, @url, @durationTicks, @thumbnailUrl,
            @isOfficialArtist, @isMusic, @isDisliked, @isDownloaded, @localPath,
            @preferredContainer, @preferredBitrate, @radioSeedId, @integratedLufs,
            @integratedLufsSource, @createdAt, @updatedAt
        )
        ON CONFLICT(Id) DO UPDATE SET
            Title = excluded.Title,
            Author = excluded.Author,
            ChannelId = excluded.ChannelId,
            Url = excluded.Url,
            DurationTicks = CASE WHEN excluded.DurationTicks > 0 THEN excluded.DurationTicks ELSE Tracks.DurationTicks END,
            ThumbnailUrl = excluded.ThumbnailUrl,
            IsOfficialArtist = CASE WHEN excluded.IsOfficialArtist = 1 THEN 1 ELSE Tracks.IsOfficialArtist END,
            IsMusic = CASE WHEN excluded.IsMusic = 1 THEN 1 ELSE Tracks.IsMusic END,
            IsDisliked = excluded.IsDisliked,
            IsDownloaded = excluded.IsDownloaded,
            LocalPath = excluded.LocalPath,
            PreferredContainer = CASE WHEN excluded.PreferredContainer IS NOT NULL THEN excluded.PreferredContainer ELSE Tracks.PreferredContainer END,
            PreferredBitrate = CASE WHEN excluded.PreferredBitrate > 0 THEN excluded.PreferredBitrate ELSE Tracks.PreferredBitrate END,
            RadioSeedId = excluded.RadioSeedId,
            IntegratedLufs = CASE WHEN excluded.IntegratedLufs IS NOT NULL THEN excluded.IntegratedLufs ELSE Tracks.IntegratedLufs END,
            IntegratedLufsSource = CASE WHEN excluded.IntegratedLufs IS NOT NULL THEN excluded.IntegratedLufsSource ELSE Tracks.IntegratedLufsSource END,
            UpdatedAt = excluded.UpdatedAt;
        """;

    /// <summary>
    /// Привязывает значения модели трека к параметрам запроса Upsert.
    /// </summary>
    private static void BindUpsertParameters(DbCommand cmd, TrackInfo track, DateTime now)
    {
        var nowStr = now.ToString("o", CultureInfo.InvariantCulture);

        AddParameter(cmd, "@id", track.Id);
        AddParameter(cmd, "@title", track.Title ?? string.Empty);
        AddParameter(cmd, "@author", track.Author ?? string.Empty);
        AddParameter(cmd, "@channelId", (object?)track.ChannelId ?? DBNull.Value);
        AddParameter(cmd, "@url", track.Url ?? string.Empty);
        AddParameter(cmd, "@durationTicks", track.Duration.Ticks);
        AddParameter(cmd, "@thumbnailUrl", track.ThumbnailUrl ?? string.Empty);
        AddParameter(cmd, "@isOfficialArtist", track.IsOfficialArtist ? 1 : 0);
        AddParameter(cmd, "@isMusic", track.IsMusic ? 1 : 0);
        AddParameter(cmd, "@isDisliked", track.IsDisliked ? 1 : 0);
        AddParameter(cmd, "@isDownloaded", track.IsDownloaded ? 1 : 0);
        AddParameter(cmd, "@localPath", (object?)track.LocalPath ?? DBNull.Value);
        AddParameter(cmd, "@preferredContainer", (object?)PersistPreferredFormat(track.PreferredFormat) ?? DBNull.Value);
        AddParameter(cmd, "@preferredBitrate", track.PreferredBitrate);
        AddParameter(cmd, "@radioSeedId", (object?)track.RadioSeedId ?? DBNull.Value);

        object lufsVal = float.IsNaN(track.IntegratedLufs) || !float.IsFinite(track.IntegratedLufs)
            ? DBNull.Value
            : track.IntegratedLufs;
        AddParameter(cmd, "@integratedLufs", lufsVal);
        AddParameter(cmd, "@integratedLufsSource", (int)track.IntegratedLufsSource);
        AddParameter(cmd, "@createdAt", nowStr);
        AddParameter(cmd, "@updatedAt", nowStr);
    }

    #endregion
}