using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace LMP.Core.Data.Repositories;

/// <summary>
/// Репозиторий списков воспроизведения на чистом ADO.NET SQLite.
/// Полностью лишен рантайм-генерации выражений LINQ to Entities для гарантированной совместимости с Native AOT.
/// Обеспечивает строгую изоляцию плейлистов на уровне аккаунтов.
/// </summary>
public sealed class PlaylistRepository : IPlaylistRepository
{
    private readonly ISqliteConnectionFactory _factory;

    private const string PlaylistColumnsSelect = """
        p.Id, p.Name, p.YoutubeId, p.Author, p.ThumbnailUrl, p.CustomColor,
        p.ComputedColor, p.Description, p.OwnerId, p.OwnerChannelId, p.Ownership,
        p.Visibility, p.SyncMode, p.ViewCount, p.ReleaseDate, p.CloudTrackCount,
        p.LastSyncedAtUtc, p.IsCloudUnavailable, p.CreatedAt, p.UpdatedAt
        """;

    /// <summary>
    /// Инициализирует новый экземпляр репозитория плейлистов на базе чистых подключений SQLite.
    /// </summary>
    /// <param name="factory">Фабрика нативных подключений SQLite с контролем памяти.</param>
    public PlaylistRepository(ISqliteConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsGuest(string ownerId) => string.IsNullOrEmpty(ownerId) || ownerId == "guest";

    /// <inheritdoc />
    public async Task<Playlist?> GetByIdAsync(string id, string ownerId, CancellationToken ct = default)
    {
        if (id == LibraryService.LikedPlaylistId)
        {
            var trackIds = await GetTrackIdsAsync(id, ownerId, ct).ConfigureAwait(false);
            return new Playlist
            {
                Id = LibraryService.LikedPlaylistId,
                StoredName = "Liked",
                SyncMode = PlaylistSyncMode.LocalOnly,
                Ownership = PlaylistOwnership.System,
                TrackIds = trackIds,
                TrackCount = trackIds.Count,
                OwnerId = ownerId
            };
        }

        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        Playlist? playlist = null;
        bool guest = IsGuest(ownerId);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT {PlaylistColumnsSelect}
                FROM Playlists p
                WHERE p.Id = @id AND p.Id != @likedId
                  AND ((@isGuest = 1 AND (p.OwnerId = '' OR p.OwnerId = 'guest'))
                    OR (@isGuest = 0 AND p.OwnerId = @ownerId))
                LIMIT 1;
                """;

            AddParameter(cmd, "@id", id);
            AddParameter(cmd, "@likedId", LibraryService.LikedPlaylistId);
            AddParameter(cmd, "@isGuest", guest ? 1 : 0);
            AddParameter(cmd, "@ownerId", ownerId ?? string.Empty);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                playlist = ReadPlaylist(reader);
            }
        }

        if (playlist is null) return null;

        var tracks = new List<string>();
        await using (var cmdTracks = connection.CreateCommand())
        {
            cmdTracks.CommandText = """
                SELECT TrackId
                FROM PlaylistTracks
                WHERE PlaylistId = @playlistId
                ORDER BY Position;
                """;

            AddParameter(cmdTracks, "@playlistId", id);

            await using var readerTracks = await cmdTracks.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await readerTracks.ReadAsync(ct).ConfigureAwait(false))
            {
                tracks.Add(readerTracks.GetString(0));
            }
        }

        playlist.TrackIds = tracks;
        playlist.TrackCount = tracks.Count;

        return playlist;
    }

    /// <inheritdoc />
    public async Task<List<(Playlist Playlist, int TrackCount)>> GetAllWithCountsAsync(
        string ownerId, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        bool guest = IsGuest(ownerId);
        var list = new List<(Playlist Playlist, int TrackCount)>();

        // 1. Извлекаем количество лайкнутых треков системного плейлиста
        int likedTrackCount = 0;
        await using (var cmdLiked = connection.CreateCommand())
        {
            cmdLiked.CommandText = """
                SELECT COUNT(*)
                FROM LikedTracks
                WHERE (@isGuest = 1 AND (OwnerId = '' OR OwnerId = 'guest'))
                   OR (@isGuest = 0 AND OwnerId = @ownerId);
                """;

            AddParameter(cmdLiked, "@isGuest", guest ? 1 : 0);
            AddParameter(cmdLiked, "@ownerId", ownerId ?? string.Empty);

            var scalar = await cmdLiked.ExecuteScalarAsync(ct).ConfigureAwait(false);
            likedTrackCount = Convert.ToInt32(scalar);
        }

        var likedPlaylist = new Playlist
        {
            Id = LibraryService.LikedPlaylistId,
            StoredName = "Liked",
            SyncMode = PlaylistSyncMode.LocalOnly,
            Ownership = PlaylistOwnership.System,
            TrackCount = likedTrackCount,
            OwnerChannelId = ownerId
        };
        list.Add((likedPlaylist, likedTrackCount));

        // 2. Извлекаем пользовательские плейлисты с агрегированным количеством треков
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT {PlaylistColumnsSelect},
                       (SELECT COUNT(*) FROM PlaylistTracks pt WHERE pt.PlaylistId = p.Id) AS TrackCount
                FROM Playlists p
                WHERE p.Id != @likedId
                  AND ((@isGuest = 1 AND (p.OwnerId = '' OR p.OwnerId = 'guest'))
                    OR (@isGuest = 0 AND p.OwnerId = @ownerId))
                ORDER BY p.Name;
                """;

            AddParameter(cmd, "@likedId", LibraryService.LikedPlaylistId);
            AddParameter(cmd, "@isGuest", guest ? 1 : 0);
            AddParameter(cmd, "@ownerId", ownerId ?? string.Empty);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var pl = ReadPlaylist(reader);
                int count = reader.GetInt32(20); // 19 индекс - TrackCount
                pl.TrackCount = count;
                list.Add((pl, count));
            }
        }

        return list;
    }

    /// <inheritdoc />
    public async Task<List<Playlist>> GetAllAsync(string ownerId, CancellationToken ct = default)
    {
        var withCounts = await GetAllWithCountsAsync(ownerId, ct).ConfigureAwait(false);
        var result = new List<Playlist>(withCounts.Count);
        for (int i = 0; i < withCounts.Count; i++)
        {
            var pl = withCounts[i].Playlist;
            pl.TrackCount = withCounts[i].TrackCount;
            result.Add(pl);
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<List<string>> GetTrackIdsAsync(string playlistId, string ownerId, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        var result = new List<string>();
        bool guest = IsGuest(ownerId);

        await using var cmd = connection.CreateCommand();

        if (playlistId == LibraryService.LikedPlaylistId)
        {
            cmd.CommandText = """
                SELECT TrackId
                FROM LikedTracks
                WHERE (@isGuest = 1 AND (OwnerId = '' OR OwnerId = 'guest'))
                   OR (@isGuest = 0 AND OwnerId = @ownerId)
                ORDER BY LikedAt DESC;
                """;

            AddParameter(cmd, "@isGuest", guest ? 1 : 0);
            AddParameter(cmd, "@ownerId", ownerId ?? string.Empty);
        }
        else
        {
            cmd.CommandText = """
                SELECT TrackId
                FROM PlaylistTracks
                WHERE PlaylistId = @playlistId
                ORDER BY Position;
                """;

            AddParameter(cmd, "@playlistId", playlistId);
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<List<string>> GetTrackIdsAsync(
        string playlistId, string ownerId, int limit, int offset = 0, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        var result = new List<string>(limit);
        bool guest = IsGuest(ownerId);

        await using var cmd = connection.CreateCommand();

        if (playlistId == LibraryService.LikedPlaylistId)
        {
            cmd.CommandText = """
                SELECT TrackId
                FROM LikedTracks
                WHERE (@isGuest = 1 AND (OwnerId = '' OR OwnerId = 'guest'))
                   OR (@isGuest = 0 AND OwnerId = @ownerId)
                ORDER BY LikedAt DESC
                LIMIT @limit OFFSET @offset;
                """;

            AddParameter(cmd, "@isGuest", guest ? 1 : 0);
            AddParameter(cmd, "@ownerId", ownerId ?? string.Empty);
        }
        else
        {
            cmd.CommandText = """
                SELECT TrackId
                FROM PlaylistTracks
                WHERE PlaylistId = @playlistId
                ORDER BY Position
                LIMIT @limit OFFSET @offset;
                """;

            AddParameter(cmd, "@playlistId", playlistId);
        }

        AddParameter(cmd, "@limit", limit);
        AddParameter(cmd, "@offset", offset);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<int> GetTrackCountAsync(string playlistId, string ownerId, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        bool guest = IsGuest(ownerId);
        await using var cmd = connection.CreateCommand();

        if (playlistId == LibraryService.LikedPlaylistId)
        {
            cmd.CommandText = """
                SELECT COUNT(*)
                FROM LikedTracks
                WHERE (@isGuest = 1 AND (OwnerId = '' OR OwnerId = 'guest'))
                   OR (@isGuest = 0 AND OwnerId = @ownerId);
                """;

            AddParameter(cmd, "@isGuest", guest ? 1 : 0);
            AddParameter(cmd, "@ownerId", ownerId ?? string.Empty);
        }
        else
        {
            cmd.CommandText = "SELECT COUNT(*) FROM PlaylistTracks WHERE PlaylistId = @playlistId;";
            AddParameter(cmd, "@playlistId", playlistId);
        }

        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(scalar);
    }

    /// <inheritdoc />
    public async Task UpsertAsync(Playlist playlist, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(playlist);

        if (playlist.Id == LibraryService.LikedPlaylistId)
            return;

        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        var nowStr = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Playlists (
                Id, Name, YoutubeId, Author, ThumbnailUrl, CustomColor,
                ComputedColor, Description, OwnerId, OwnerChannelId, Ownership,
                Visibility, SyncMode, ViewCount, ReleaseDate, CloudTrackCount,
                LastSyncedAtUtc, IsCloudUnavailable, CreatedAt, UpdatedAt
            )
            VALUES (
                @id, @name, @youtubeId, @author, @thumbnailUrl, @customColor,
                @computedColor, @description, @ownerId, @ownerChannelId, @ownership,
                @visibility, @syncMode, @viewCount, @releaseDate, @cloudTrackCount,
                @lastSyncedAtUtc, @isCloudUnavailable, @createdAt, @updatedAt
            )
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name,
                YoutubeId = excluded.YoutubeId,
                Author = excluded.Author,
                ThumbnailUrl = excluded.ThumbnailUrl,
                CustomColor = excluded.CustomColor,
                ComputedColor = excluded.ComputedColor,
                Description = excluded.Description,
                OwnerId = excluded.OwnerId,
                OwnerChannelId = excluded.OwnerChannelId,
                Ownership = excluded.Ownership,
                Visibility = excluded.Visibility,
                SyncMode = excluded.SyncMode,
                ViewCount = excluded.ViewCount,
                ReleaseDate = excluded.ReleaseDate,
                CloudTrackCount = excluded.CloudTrackCount,
                LastSyncedAtUtc = excluded.LastSyncedAtUtc,
                IsCloudUnavailable = excluded.IsCloudUnavailable,
                UpdatedAt = excluded.UpdatedAt;
            """;

        var nameToPersist = !string.IsNullOrEmpty(playlist.StoredName)
            ? playlist.StoredName
            : playlist.Name;

        AddParameter(cmd, "@id", playlist.Id);
        AddParameter(cmd, "@name", nameToPersist);
        AddParameter(cmd, "@youtubeId", (object?)playlist.YoutubeId ?? DBNull.Value);
        AddParameter(cmd, "@author", (object?)playlist.Author ?? DBNull.Value);
        AddParameter(cmd, "@thumbnailUrl", (object?)playlist.ThumbnailUrl ?? DBNull.Value);
        AddParameter(cmd, "@customColor", (object?)playlist.CustomColor ?? DBNull.Value);
        AddParameter(cmd, "@computedColor", (object?)playlist.ComputedColor ?? DBNull.Value);
        AddParameter(cmd, "@description", (object?)playlist.Description ?? DBNull.Value);
        AddParameter(cmd, "@ownerId", playlist.OwnerId);
        AddParameter(cmd, "@ownerChannelId", (object?)playlist.OwnerChannelId ?? DBNull.Value);
        AddParameter(cmd, "@ownership", (int)playlist.Ownership);
        AddParameter(cmd, "@visibility", (int)playlist.Visibility);
        AddParameter(cmd, "@syncMode", (int)playlist.SyncMode);
        AddParameter(cmd, "@viewCount", (object?)playlist.ViewCount ?? DBNull.Value);

        object releaseDateVal = playlist.ReleaseDate.HasValue
            ? playlist.ReleaseDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : DBNull.Value;
        AddParameter(cmd, "@releaseDate", releaseDateVal);

        AddParameter(cmd, "@cloudTrackCount", (object?)playlist.CloudTrackCount ?? DBNull.Value);

        object lastSyncedVal = playlist.LastSyncedAtUtc.HasValue
            ? playlist.LastSyncedAtUtc.Value.ToString("o", CultureInfo.InvariantCulture)
            : DBNull.Value;
        AddParameter(cmd, "@lastSyncedAtUtc", lastSyncedVal);

        AddParameter(cmd, "@isCloudUnavailable", playlist.IsCloudUnavailable ? 1 : 0);
        AddParameter(cmd, "@createdAt", nowStr);
        AddParameter(cmd, "@updatedAt", nowStr);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            await using (var cmdTracks = connection.CreateCommand())
            {
                cmdTracks.Transaction = transaction;
                cmdTracks.CommandText = "DELETE FROM PlaylistTracks WHERE PlaylistId = @id;";
                AddParameter(cmdTracks, "@id", id);
                await cmdTracks.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var cmdPl = connection.CreateCommand())
            {
                cmdPl.Transaction = transaction;
                cmdPl.CommandText = "DELETE FROM Playlists WHERE Id = @id;";
                AddParameter(cmdPl, "@id", id);
                await cmdPl.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
    public async Task RenameAsync(string id, string newName, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        var nowStr = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Playlists
            SET Name = @newName,
                UpdatedAt = @updatedAt
            WHERE Id = @id;
            """;

        AddParameter(cmd, "@newName", newName);
        AddParameter(cmd, "@updatedAt", nowStr);
        AddParameter(cmd, "@id", id);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddTrackAsync(string playlistId, string trackId, string ownerId, int? position = null, CancellationToken ct = default)
    {
        if (playlistId == LibraryService.LikedPlaylistId)
        {
            await SetLikedDirectAsync(trackId, ownerId, true, ct).ConfigureAwait(false);
            return;
        }

        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            // Проверка на дубликат связки
            await using (var cmdExists = connection.CreateCommand())
            {
                cmdExists.Transaction = transaction;
                cmdExists.CommandText = "SELECT 1 FROM PlaylistTracks WHERE PlaylistId = @pId AND TrackId = @tId LIMIT 1;";
                AddParameter(cmdExists, "@pId", playlistId);
                AddParameter(cmdExists, "@tId", trackId);
                var scalarExists = await cmdExists.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (scalarExists != null && scalarExists != DBNull.Value)
                    return;
            }

            // Проверка существования трека
            await using (var cmdTrack = connection.CreateCommand())
            {
                cmdTrack.Transaction = transaction;
                cmdTrack.CommandText = "SELECT 1 FROM Tracks WHERE Id = @tId LIMIT 1;";
                AddParameter(cmdTrack, "@tId", trackId);
                var scalarTrack = await cmdTrack.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (scalarTrack == null || scalarTrack == DBNull.Value)
                {
                    Log.Warn($"[PlaylistRepo] Cannot add track {trackId} to playlist {playlistId} - track does not exist");
                    return;
                }
            }

            int targetPos;
            if (position.HasValue)
            {
                targetPos = position.Value;
            }
            else
            {
                await using var cmdPos = connection.CreateCommand();
                cmdPos.Transaction = transaction;
                cmdPos.CommandText = "SELECT COALESCE(MAX(Position), -1) + 1 FROM PlaylistTracks WHERE PlaylistId = @pId;";
                AddParameter(cmdPos, "@pId", playlistId);
                var maxPosObj = await cmdPos.ExecuteScalarAsync(ct).ConfigureAwait(false);
                targetPos = Convert.ToInt32(maxPosObj);
            }

            await using (var cmdInsert = connection.CreateCommand())
            {
                cmdInsert.Transaction = transaction;
                cmdInsert.CommandText = """
                    INSERT INTO PlaylistTracks (PlaylistId, TrackId, Position, SetVideoId)
                    VALUES (@pId, @tId, @pos, NULL);
                    """;
                AddParameter(cmdInsert, "@pId", playlistId);
                AddParameter(cmdInsert, "@tId", trackId);
                AddParameter(cmdInsert, "@pos", targetPos);
                await cmdInsert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var cmdUpdatePl = connection.CreateCommand())
            {
                cmdUpdatePl.Transaction = transaction;
                cmdUpdatePl.CommandText = "UPDATE Playlists SET UpdatedAt = @updatedAt WHERE Id = @id;";
                AddParameter(cmdUpdatePl, "@updatedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                AddParameter(cmdUpdatePl, "@id", playlistId);
                await cmdUpdatePl.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
    public async Task<int> AddTracksAsync(string playlistId, IEnumerable<string> trackIds, string ownerId, CancellationToken ct = default)
    {
        if (playlistId == LibraryService.LikedPlaylistId)
        {
            var idList = trackIds as IList<string> ?? [.. trackIds];
            if (idList.Count == 0) return 0;

            await using var likedConn = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var likedTx = (SqliteTransaction)await likedConn.BeginTransactionAsync(ct).ConfigureAwait(false);

            try
            {
                var baseTime = DateTime.UtcNow;
                int added = 0;

                await using var cmd = likedConn.CreateCommand();
                cmd.Transaction = likedTx;
                cmd.CommandText = """
                    INSERT OR IGNORE INTO LikedTracks (OwnerId, TrackId, LikedAt)
                    VALUES (@ownerId, @trackId, @likedAt);
                    """;

                var pOwner = cmd.CreateParameter();
                pOwner.ParameterName = "@ownerId";
                pOwner.Value = ownerId ?? string.Empty;
                cmd.Parameters.Add(pOwner);

                var pTrack = cmd.CreateParameter();
                pTrack.ParameterName = "@trackId";
                cmd.Parameters.Add(pTrack);

                var pLikedAt = cmd.CreateParameter();
                pLikedAt.ParameterName = "@likedAt";
                cmd.Parameters.Add(pLikedAt);

                for (int i = 0; i < idList.Count; i++)
                {
                    pTrack.Value = idList[i];
                    // Монотонно убывающий timestamp: idList[0] (самый свежий) получает наибольший timestamp
                    pLikedAt.Value = baseTime.AddMilliseconds(-i).ToString("o", CultureInfo.InvariantCulture);
                    added += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await likedTx.CommitAsync(ct).ConfigureAwait(false);
                return added;
            }
            catch
            {
                await likedTx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        }

        var trackIdList = trackIds as IList<string> ?? [.. trackIds];
        if (trackIdList.Count == 0) return 0;

        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            var existingTrackIds = new HashSet<string>(StringComparer.Ordinal);
            var alreadyLinked = new HashSet<string>(StringComparer.Ordinal);

            const int chunkSize = 500;
            for (int i = 0; i < trackIdList.Count; i += chunkSize)
            {
                int count = Math.Min(chunkSize, trackIdList.Count - i);

                // Существующие треки
                await using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    var paramNames = new string[count];
                    for (int j = 0; j < count; j++)
                    {
                        var paramName = $"@t{j}";
                        paramNames[j] = paramName;
                        AddParameter(cmd, paramName, trackIdList[i + j]);
                    }

                    cmd.CommandText = $"SELECT Id FROM Tracks WHERE Id IN ({string.Join(',', paramNames)});";
                    await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        existingTrackIds.Add(reader.GetString(0));
                    }
                }

                // Уже привязанные к этому плейлисту
                await using (var cmdLinked = connection.CreateCommand())
                {
                    cmdLinked.Transaction = transaction;
                    var paramNames = new string[count];
                    for (int j = 0; j < count; j++)
                    {
                        var paramName = $"@lp{j}";
                        paramNames[j] = paramName;
                        AddParameter(cmdLinked, paramName, trackIdList[i + j]);
                    }

                    cmdLinked.CommandText = $"""
                        SELECT TrackId FROM PlaylistTracks
                        WHERE PlaylistId = @pId AND TrackId IN ({string.Join(',', paramNames)});
                        """;

                    AddParameter(cmdLinked, "@pId", playlistId);

                    await using var readerLinked = await cmdLinked.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await readerLinked.ReadAsync(ct).ConfigureAwait(false))
                    {
                        alreadyLinked.Add(readerLinked.GetString(0));
                    }
                }
            }

            int maxPos = -1;
            await using (var cmdPos = connection.CreateCommand())
            {
                cmdPos.Transaction = transaction;
                cmdPos.CommandText = "SELECT COALESCE(MAX(Position), -1) FROM PlaylistTracks WHERE PlaylistId = @pId;";
                AddParameter(cmdPos, "@pId", playlistId);
                var scalar = await cmdPos.ExecuteScalarAsync(ct).ConfigureAwait(false);
                maxPos = Convert.ToInt32(scalar);
            }

            int addedCount = 0;
            await using (var cmdInsert = connection.CreateCommand())
            {
                cmdInsert.Transaction = transaction;
                cmdInsert.CommandText = """
                    INSERT INTO PlaylistTracks (PlaylistId, TrackId, Position, SetVideoId)
                    VALUES (@pId, @tId, @pos, NULL);
                    """;

                var pPlaylist = cmdInsert.CreateParameter();
                pPlaylist.ParameterName = "@pId";
                pPlaylist.Value = playlistId;
                cmdInsert.Parameters.Add(pPlaylist);

                var pTrack = cmdInsert.CreateParameter();
                pTrack.ParameterName = "@tId";
                cmdInsert.Parameters.Add(pTrack);

                var pPos = cmdInsert.CreateParameter();
                pPos.ParameterName = "@pos";
                cmdInsert.Parameters.Add(pPos);

                for (int i = 0; i < trackIdList.Count; i++)
                {
                    var trackId = trackIdList[i];
                    if (!existingTrackIds.Contains(trackId) || alreadyLinked.Contains(trackId))
                        continue;

                    maxPos++;
                    pTrack.Value = trackId;
                    pPos.Value = maxPos;
                    await cmdInsert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    alreadyLinked.Add(trackId);
                    addedCount++;
                }
            }

            if (addedCount > 0)
            {
                await using var cmdUpdate = connection.CreateCommand();
                cmdUpdate.Transaction = transaction;
                cmdUpdate.CommandText = "UPDATE Playlists SET UpdatedAt = @updatedAt WHERE Id = @pId;";
                AddParameter(cmdUpdate, "@updatedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                AddParameter(cmdUpdate, "@pId", playlistId);
                await cmdUpdate.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return addedCount;
        }
        catch
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task RemoveTrackAsync(string playlistId, string trackId, string ownerId, CancellationToken ct = default)
    {
        if (playlistId == LibraryService.LikedPlaylistId)
        {
            await SetLikedDirectAsync(trackId, ownerId, false, ct).ConfigureAwait(false);
            return;
        }

        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            int? removedPos = null;
            await using (var cmdPos = connection.CreateCommand())
            {
                cmdPos.Transaction = transaction;
                cmdPos.CommandText = "SELECT Position FROM PlaylistTracks WHERE PlaylistId = @pId AND TrackId = @tId LIMIT 1;";
                AddParameter(cmdPos, "@pId", playlistId);
                AddParameter(cmdPos, "@tId", trackId);
                var scalar = await cmdPos.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (scalar != null && scalar != DBNull.Value)
                {
                    removedPos = Convert.ToInt32(scalar);
                }
            }

            if (removedPos is null) return;

            await using (var cmdDel = connection.CreateCommand())
            {
                cmdDel.Transaction = transaction;
                cmdDel.CommandText = "DELETE FROM PlaylistTracks WHERE PlaylistId = @pId AND TrackId = @tId;";
                AddParameter(cmdDel, "@pId", playlistId);
                AddParameter(cmdDel, "@tId", trackId);
                await cmdDel.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var cmdShift = connection.CreateCommand())
            {
                cmdShift.Transaction = transaction;
                cmdShift.CommandText = """
                    UPDATE PlaylistTracks
                    SET Position = Position - 1
                    WHERE PlaylistId = @pId AND Position > @removedPos;
                    """;
                AddParameter(cmdShift, "@pId", playlistId);
                AddParameter(cmdShift, "@removedPos", removedPos.Value);
                await cmdShift.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
    public async Task MoveTrackAsync(string playlistId, int oldIndex, int newIndex, CancellationToken ct = default)
    {
        if (oldIndex == newIndex) return;

        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            var trackIds = new List<string>();
            await using (var cmdTracks = connection.CreateCommand())
            {
                cmdTracks.Transaction = transaction;
                cmdTracks.CommandText = "SELECT TrackId FROM PlaylistTracks WHERE PlaylistId = @pId ORDER BY Position;";
                AddParameter(cmdTracks, "@pId", playlistId);

                await using var reader = await cmdTracks.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    trackIds.Add(reader.GetString(0));
                }
            }

            if (oldIndex < 0 || oldIndex >= trackIds.Count || newIndex < 0 || newIndex >= trackIds.Count)
                return;

            var movingTrack = trackIds[oldIndex];
            trackIds.RemoveAt(oldIndex);
            trackIds.Insert(newIndex, movingTrack);

            await using (var cmdUpdate = connection.CreateCommand())
            {
                cmdUpdate.Transaction = transaction;
                cmdUpdate.CommandText = "UPDATE PlaylistTracks SET Position = @pos WHERE PlaylistId = @pId AND TrackId = @tId;";

                var pPos = cmdUpdate.CreateParameter();
                pPos.ParameterName = "@pos";
                cmdUpdate.Parameters.Add(pPos);

                var pPlaylist = cmdUpdate.CreateParameter();
                pPlaylist.ParameterName = "@pId";
                pPlaylist.Value = playlistId;
                cmdUpdate.Parameters.Add(pPlaylist);

                var pTrack = cmdUpdate.CreateParameter();
                pTrack.ParameterName = "@tId";
                cmdUpdate.Parameters.Add(pTrack);

                for (int i = 0; i < trackIds.Count; i++)
                {
                    pPos.Value = i;
                    pTrack.Value = trackIds[i];
                    await cmdUpdate.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
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
    public async Task<bool> ContainsTrackAsync(string playlistId, string trackId, string ownerId, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        bool guest = IsGuest(ownerId);
        await using var cmd = connection.CreateCommand();

        if (playlistId == LibraryService.LikedPlaylistId)
        {
            cmd.CommandText = """
                SELECT 1 FROM LikedTracks
                WHERE TrackId = @tId
                  AND ((@isGuest = 1 AND (OwnerId = '' OR OwnerId = 'guest'))
                    OR (@isGuest = 0 AND OwnerId = @ownerId))
                LIMIT 1;
                """;

            AddParameter(cmd, "@tId", trackId);
            AddParameter(cmd, "@isGuest", guest ? 1 : 0);
            AddParameter(cmd, "@ownerId", ownerId ?? string.Empty);
        }
        else
        {
            cmd.CommandText = "SELECT 1 FROM PlaylistTracks WHERE PlaylistId = @pId AND TrackId = @tId LIMIT 1;";
            AddParameter(cmd, "@pId", playlistId);
            AddParameter(cmd, "@tId", trackId);
        }

        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return scalar != null && scalar != DBNull.Value;
    }

    /// <inheritdoc />
    public async Task<HashSet<string>> GetPlaylistsForTrackAsync(string trackId, string ownerId, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        var set = new HashSet<string>(StringComparer.Ordinal);
        bool guest = IsGuest(ownerId);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT pt.PlaylistId
                FROM PlaylistTracks pt
                INNER JOIN Playlists p ON pt.PlaylistId = p.Id
                WHERE pt.TrackId = @tId
                  AND pt.PlaylistId != @likedId
                  AND ((@isGuest = 1 AND (p.OwnerId = '' OR p.OwnerId = 'guest'))
                    OR (@isGuest = 0 AND p.OwnerId = @ownerId));
                """;

            AddParameter(cmd, "@tId", trackId);
            AddParameter(cmd, "@likedId", LibraryService.LikedPlaylistId);
            AddParameter(cmd, "@isGuest", guest ? 1 : 0);
            AddParameter(cmd, "@ownerId", ownerId ?? string.Empty);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                set.Add(reader.GetString(0));
            }
        }

        // Проверка на статус Liked
        await using (var cmdLiked = connection.CreateCommand())
        {
            cmdLiked.CommandText = """
                SELECT 1 FROM LikedTracks
                WHERE TrackId = @tId
                  AND ((@isGuest = 1 AND (OwnerId = '' OR OwnerId = 'guest'))
                    OR (@isGuest = 0 AND OwnerId = @ownerId))
                LIMIT 1;
                """;

            AddParameter(cmdLiked, "@tId", trackId);
            AddParameter(cmdLiked, "@isGuest", guest ? 1 : 0);
            AddParameter(cmdLiked, "@ownerId", ownerId ?? string.Empty);

            var scalar = await cmdLiked.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (scalar != null && scalar != DBNull.Value)
            {
                set.Add(LibraryService.LikedPlaylistId);
            }
        }

        return set;
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, HashSet<string>>> GetPlaylistsForTracksAsync(
        IEnumerable<string> trackIds, string ownerId, CancellationToken ct = default)
    {
        var ids = trackIds as IList<string> ?? [.. trackIds];
        if (ids.Count == 0) return [];

        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        var result = new Dictionary<string, HashSet<string>>(ids.Count, StringComparer.Ordinal);
        bool guest = IsGuest(ownerId);

        const int chunkSize = 500;
        for (int i = 0; i < ids.Count; i += chunkSize)
        {
            int count = Math.Min(chunkSize, ids.Count - i);

            // Выборка плейлистов
            await using (var cmd = connection.CreateCommand())
            {
                var paramNames = new string[count];
                for (int j = 0; j < count; j++)
                {
                    var paramName = $"@t{j}";
                    paramNames[j] = paramName;
                    AddParameter(cmd, paramName, ids[i + j]);
                }

                cmd.CommandText = $"""
                    SELECT pt.TrackId, pt.PlaylistId
                    FROM PlaylistTracks pt
                    INNER JOIN Playlists p ON pt.PlaylistId = p.Id
                    WHERE pt.TrackId IN ({string.Join(',', paramNames)})
                      AND pt.PlaylistId != @likedId
                      AND ((@isGuest = 1 AND (p.OwnerId = '' OR p.OwnerId = 'guest'))
                        OR (@isGuest = 0 AND p.OwnerId = @ownerId));
                    """;

                AddParameter(cmd, "@likedId", LibraryService.LikedPlaylistId);
                AddParameter(cmd, "@isGuest", guest ? 1 : 0);
                AddParameter(cmd, "@ownerId", ownerId ?? string.Empty);

                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var tid = reader.GetString(0);
                    var pid = reader.GetString(1);
                    if (!result.TryGetValue(tid, out var set))
                    {
                        set = new HashSet<string>(StringComparer.Ordinal);
                        result[tid] = set;
                    }
                    set.Add(pid);
                }
            }

            // Выборка лайков для переданных ID
            await using (var cmdLiked = connection.CreateCommand())
            {
                var paramNames = new string[count];
                for (int j = 0; j < count; j++)
                {
                    var paramName = $"@lt{j}";
                    paramNames[j] = paramName;
                    AddParameter(cmdLiked, paramName, ids[i + j]);
                }

                cmdLiked.CommandText = $"""
                    SELECT TrackId
                    FROM LikedTracks
                    WHERE TrackId IN ({string.Join(',', paramNames)})
                      AND ((@isGuest = 1 AND (OwnerId = '' OR OwnerId = 'guest'))
                        OR (@isGuest = 0 AND OwnerId = @ownerId));
                    """;

                AddParameter(cmdLiked, "@isGuest", guest ? 1 : 0);
                AddParameter(cmdLiked, "@ownerId", ownerId ?? string.Empty);

                await using var readerLiked = await cmdLiked.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await readerLiked.ReadAsync(ct).ConfigureAwait(false))
                {
                    var tid = readerLiked.GetString(0);
                    if (!result.TryGetValue(tid, out var set))
                    {
                        set = new HashSet<string>(StringComparer.Ordinal);
                        result[tid] = set;
                    }
                    set.Add(LibraryService.LikedPlaylistId);
                }
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<long> GetTotalDurationTicksAsync(string playlistId, string ownerId, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        bool guest = IsGuest(ownerId);
        await using var cmd = connection.CreateCommand();

        if (playlistId == LibraryService.LikedPlaylistId)
        {
            cmd.CommandText = """
                SELECT COALESCE(SUM(t.DurationTicks), 0)
                FROM LikedTracks lt
                INNER JOIN Tracks t ON lt.TrackId = t.Id
                WHERE (@isGuest = 1 AND (lt.OwnerId = '' OR lt.OwnerId = 'guest'))
                   OR (@isGuest = 0 AND lt.OwnerId = @ownerId);
                """;

            AddParameter(cmd, "@isGuest", guest ? 1 : 0);
            AddParameter(cmd, "@ownerId", ownerId ?? string.Empty);
        }
        else
        {
            cmd.CommandText = """
                SELECT COALESCE(SUM(t.DurationTicks), 0)
                FROM PlaylistTracks pt
                INNER JOIN Playlists p ON pt.PlaylistId = p.Id
                INNER JOIN Tracks t ON pt.TrackId = t.Id
                WHERE pt.PlaylistId = @pId
                  AND pt.PlaylistId != @likedId
                  AND ((@isGuest = 1 AND (p.OwnerId = '' OR p.OwnerId = 'guest'))
                    OR (@isGuest = 0 AND p.OwnerId = @ownerId));
                """;

            AddParameter(cmd, "@pId", playlistId);
            AddParameter(cmd, "@likedId", LibraryService.LikedPlaylistId);
            AddParameter(cmd, "@isGuest", guest ? 1 : 0);
            AddParameter(cmd, "@ownerId", ownerId ?? string.Empty);
        }

        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(scalar);
    }

    /// <inheritdoc />
    public async Task<long> GetTotalLibraryDurationAsync(string ownerId, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        bool guest = IsGuest(ownerId);
        await using var cmd = connection.CreateCommand();

        cmd.CommandText = """
            SELECT COALESCE(SUM(t.DurationTicks), 0)
            FROM Tracks t
            WHERE t.Id IN (
                SELECT pt.TrackId
                FROM PlaylistTracks pt
                INNER JOIN Playlists p ON pt.PlaylistId = p.Id
                WHERE pt.PlaylistId != @likedId
                  AND ((@isGuest = 1 AND (p.OwnerId = '' OR p.OwnerId = 'guest'))
                    OR (@isGuest = 0 AND p.OwnerId = @ownerId))
                UNION
                SELECT lt.TrackId
                FROM LikedTracks lt
                WHERE (@isGuest = 1 AND (lt.OwnerId = '' OR lt.OwnerId = 'guest'))
                   OR (@isGuest = 0 AND lt.OwnerId = @ownerId)
            );
            """;

        AddParameter(cmd, "@likedId", LibraryService.LikedPlaylistId);
        AddParameter(cmd, "@isGuest", guest ? 1 : 0);
        AddParameter(cmd, "@ownerId", ownerId ?? string.Empty);

        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(scalar);
    }

    /// <inheritdoc />
    public async Task<int> AdoptOrphanPlaylistsAsync(string newOwnerId, CancellationToken ct = default)
    {
        if (IsGuest(newOwnerId)) return 0;

        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        var nowStr = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Playlists
            SET OwnerId = @newOwnerId,
                UpdatedAt = @updatedAt
            WHERE OwnerId = '' OR OwnerId = 'guest';
            """;

        AddParameter(cmd, "@newOwnerId", newOwnerId);
        AddParameter(cmd, "@updatedAt", nowStr);

        int adopted = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (adopted > 0)
        {
            Log.Info($"[PlaylistRepo] Adopted {adopted} orphan playlist(s) for owner {newOwnerId}");
        }

        return adopted;
    }

    private async Task SetLikedDirectAsync(string trackId, string ownerId, bool liked, CancellationToken ct)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        bool guest = IsGuest(ownerId);
        await using var cmd = connection.CreateCommand();

        if (liked)
        {
            cmd.CommandText = """
                INSERT OR IGNORE INTO LikedTracks (OwnerId, TrackId, LikedAt)
                VALUES (@ownerId, @trackId, @likedAt);
                """;

            AddParameter(cmd, "@ownerId", ownerId ?? string.Empty);
            AddParameter(cmd, "@trackId", trackId);
            AddParameter(cmd, "@likedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        }
        else
        {
            cmd.CommandText = """
                DELETE FROM LikedTracks
                WHERE TrackId = @trackId
                  AND ((@isGuest = 1 AND (OwnerId = '' OR OwnerId = 'guest'))
                    OR (@isGuest = 0 AND OwnerId = @ownerId));
                """;

            AddParameter(cmd, "@trackId", trackId);
            AddParameter(cmd, "@isGuest", guest ? 1 : 0);
            AddParameter(cmd, "@ownerId", ownerId ?? string.Empty);
        }

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    #region SetVideoId

    public async Task<string?> GetSetVideoIdAsync(string playlistId, string trackId, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT SetVideoId
            FROM PlaylistTracks
            WHERE PlaylistId = @pId AND TrackId = @tId
            LIMIT 1;
            """;

        AddParameter(cmd, "@pId", playlistId);
        AddParameter(cmd, "@tId", trackId);

        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return scalar is string setVideoId ? setVideoId : null;
    }

    public async Task UpdateSetVideoIdAsync(string playlistId, string trackId, string setVideoId, CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE PlaylistTracks
            SET SetVideoId = @setVideoId
            WHERE PlaylistId = @pId AND TrackId = @tId;
            """;

        AddParameter(cmd, "@setVideoId", setVideoId);
        AddParameter(cmd, "@pId", playlistId);
        AddParameter(cmd, "@tId", trackId);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Выполняет пакетное обновление соответствий setVideoId для группы треков в плейлисте в рамках транзакции.
    /// </summary>
    /// <param name="playlistId">Идентификатор плейлиста.</param>
    /// <param name="mappings">Коллекция пар: Идентификатор трека -> setVideoId.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    public async Task UpdateSetVideoIdsAsync(
        string playlistId,
        IReadOnlyList<(string TrackId, string SetVideoId)> mappings,
        CancellationToken ct = default)
    {
        if (mappings.Count == 0) return;

        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                UPDATE PlaylistTracks
                SET SetVideoId = @setVideoId
                WHERE PlaylistId = @pId AND TrackId = @tId;
                """;

            var pPlaylist = cmd.CreateParameter();
            pPlaylist.ParameterName = "@pId";
            pPlaylist.Value = playlistId;
            cmd.Parameters.Add(pPlaylist);

            var pTrack = cmd.CreateParameter();
            pTrack.ParameterName = "@tId";
            cmd.Parameters.Add(pTrack);

            var pSetVideo = cmd.CreateParameter();
            pSetVideo.ParameterName = "@setVideoId";
            cmd.Parameters.Add(pSetVideo);

            for (int i = 0; i < mappings.Count; i++)
            {
                var (trackId, setVideoId) = mappings[i];
                pTrack.Value = trackId;
                pSetVideo.Value = setVideoId;
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

    #endregion

    #region ADO.NET Вспомогательные методы

    /// <summary>
    /// Вычитывает модель <see cref="Playlist"/> напрямую из активного чтения <see cref="DbDataReader"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Playlist ReadPlaylist(DbDataReader reader)
    {
        var id = reader.GetString(0);
        var name = reader.GetString(1);
        var youtubeId = reader.IsDBNull(2) ? null : reader.GetString(2);
        var author = reader.IsDBNull(3) ? null : reader.GetString(3);
        var thumbnailUrl = reader.IsDBNull(4) ? null : reader.GetString(4);
        var customColor = reader.IsDBNull(5) ? null : reader.GetString(5);
        var computedColor = reader.IsDBNull(6) ? null : reader.GetString(6);
        var description = reader.IsDBNull(7) ? null : reader.GetString(7);
        var ownerId = reader.GetString(8);
        var ownerChannelId = reader.IsDBNull(9) ? null : reader.GetString(9);
        var ownership = reader.GetInt32(10);
        var visibility = reader.GetInt32(11);
        var syncMode = reader.GetInt32(12);
        long? viewCount = reader.IsDBNull(13) ? null : reader.GetInt64(13);

        DateOnly? releaseDate = null;
        if (!reader.IsDBNull(14))
        {
            var str = reader.GetString(14);
            if (DateOnly.TryParseExact(str, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
            {
                releaseDate = parsedDate;
            }
        }

        int? cloudTrackCount = reader.IsDBNull(15) ? null : reader.GetInt32(15);

        DateTime? lastSyncedAtUtc = null;
        if (!reader.IsDBNull(16))
        {
            var str = reader.GetString(16);
            if (DateTime.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedSynced))
            {
                lastSyncedAtUtc = parsedSynced;
            }
        }

        bool isCloudUnavailable = reader.GetInt32(17) != 0;

        DateTime createdAt = DateTime.UtcNow;
        if (!reader.IsDBNull(18))
        {
            var str = reader.GetString(18);
            if (DateTime.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedCreated))
            {
                createdAt = parsedCreated;
            }
        }

        DateTime updatedAt = DateTime.UtcNow;
        if (!reader.IsDBNull(19))
        {
            var str = reader.GetString(19);
            if (DateTime.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedUpdated))
            {
                updatedAt = parsedUpdated;
            }
        }

        return new Playlist
        {
            Id = id,
            Name = name,
            StoredName = name,
            YoutubeId = youtubeId,
            Author = author,
            ThumbnailUrl = thumbnailUrl,
            CustomColor = customColor,
            ComputedColor = computedColor,
            Description = description,
            SyncMode = (PlaylistSyncMode)syncMode,
            ViewCount = viewCount,
            ReleaseDate = releaseDate,
            OwnerId = ownerId,
            OwnerChannelId = ownerChannelId,
            Ownership = (PlaylistOwnership)ownership,
            Visibility = (PlaylistVisibility)visibility,
            CloudTrackCount = cloudTrackCount,
            LastSyncedAtUtc = lastSyncedAtUtc,
            IsCloudUnavailable = isCloudUnavailable,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddParameter(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    #endregion
}