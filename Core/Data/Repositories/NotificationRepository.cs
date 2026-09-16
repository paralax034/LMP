using System.Collections.ObjectModel;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LMP.Core.Models;
using LMP.Core.Services;

namespace LMP.Core.Data.Repositories;

/// <summary>
/// Реализация персистентного хранилища уведомлений на базе чистого ADO.NET SQLite.
/// Полностью совместима с Native AOT и сборкой с агрессивным триммингом (zero reflection).
/// </summary>
public sealed class NotificationRepository : INotificationRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;

    private const string NotificationColumnsSelect = """
        n.Id, n.TitleKey, n.TitleRaw, n.MessageKey, n.MessageRaw,
        n.MessageArgsJson, n.RecommendationKey, n.Severity, n.IsRead,
        n.TrackId, n.TrackTitle, n.ExceptionDetails, n.AttemptsJson, n.CreatedAt
        """;

    /// <summary>
    /// Инициализирует новый экземпляр репозитория уведомлений на чистом ADO.NET.
    /// </summary>
    /// <param name="connectionFactory">Фабрика нативных подключений SQLite.</param>
    public NotificationRepository(ISqliteConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public async Task<List<Notification>> GetRecentAsync(int limit = 50, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);

        var result = new List<Notification>(limit);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {NotificationColumnsSelect}
            FROM Notifications n
            ORDER BY n.CreatedAt DESC
            LIMIT @limit;
            """;

        AddParameter(cmd, "@limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(ReadNotification(reader));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task AddAsync(Notification notification, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);

        string? attemptsJson = null;
        if (notification.Attempts is { Count: > 0 })
        {
            var dtos = new List<NotificationService.AttemptDto>(notification.Attempts.Count);
            for (int i = 0; i < notification.Attempts.Count; i++)
            {
                var a = notification.Attempts[i];
                dtos.Add(new NotificationService.AttemptDto(a.ClientName, a.Success, a.ErrorMessage, a.Timestamp));
            }
            attemptsJson = JsonSerializer.Serialize(dtos, AppJsonContext.Default.ListAttemptDto);
        }

        string? argsJson = null;
        if (notification.MessageArgs is { Length: > 0 })
        {
            var stringArgs = new string?[notification.MessageArgs.Length];
            for (int i = 0; i < notification.MessageArgs.Length; i++)
            {
                stringArgs[i] = notification.MessageArgs[i]?.ToString();
            }
            argsJson = JsonSerializer.Serialize(stringArgs, AppJsonContext.Default.StringArray);
        }

        var createdAtStr = notification.Timestamp.ToString("o", CultureInfo.InvariantCulture);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Notifications (
                Id, TitleKey, TitleRaw, MessageKey, MessageRaw,
                MessageArgsJson, RecommendationKey, Severity, IsRead,
                TrackId, TrackTitle, ExceptionDetails, AttemptsJson, CreatedAt
            )
            VALUES (
                @id, @titleKey, @titleRaw, @messageKey, @messageRaw,
                @messageArgsJson, @recommendationKey, @severity, @isRead,
                @trackId, @trackTitle, @exceptionDetails, @attemptsJson, @createdAt
            );
            """;

        AddParameter(cmd, "@id", notification.Id.ToString());
        AddParameter(cmd, "@titleKey", (object?)notification.TitleKey ?? DBNull.Value);
        AddParameter(cmd, "@titleRaw", (object?)notification.TitleRaw ?? DBNull.Value);
        AddParameter(cmd, "@messageKey", (object?)notification.MessageKey ?? DBNull.Value);
        AddParameter(cmd, "@messageRaw", (object?)notification.MessageRaw ?? DBNull.Value);
        AddParameter(cmd, "@messageArgsJson", (object?)argsJson ?? DBNull.Value);
        AddParameter(cmd, "@recommendationKey", (object?)notification.RecommendationKey ?? DBNull.Value);
        AddParameter(cmd, "@severity", (int)notification.Severity);
        AddParameter(cmd, "@isRead", notification.IsRead ? 1 : 0);
        AddParameter(cmd, "@trackId", (object?)notification.TrackId ?? DBNull.Value);
        AddParameter(cmd, "@trackTitle", (object?)notification.TrackTitle ?? DBNull.Value);
        AddParameter(cmd, "@exceptionDetails", (object?)notification.ExceptionDetails ?? DBNull.Value);
        AddParameter(cmd, "@attemptsJson", (object?)attemptsJson ?? DBNull.Value);
        AddParameter(cmd, "@createdAt", createdAtStr);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MarkAllAsReadAsync(CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE Notifications SET IsRead = 1 WHERE IsRead = 0;";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Notifications;";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PruneAsync(int keepCount = 100, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM Notifications
            WHERE Id IN (
                SELECT Id FROM Notifications
                ORDER BY CreatedAt DESC
                LIMIT -1 OFFSET @keepCount
            );
            """;

        AddParameter(cmd, "@keepCount", keepCount);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteOlderThanAsync(DateTime threshold, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);

        var thresholdStr = threshold.ToString("o", CultureInfo.InvariantCulture);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Notifications WHERE CreatedAt < @threshold;";
        AddParameter(cmd, "@threshold", thresholdStr);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    #region ADO.NET Вспомогательные методы

    /// <summary>
    /// Считывает модель уведомления напрямую из активного чтения SQLite без рефлексии.
    /// </summary>
    /// <param name="reader">Экземпляр открытого ридера базы данных.</param>
    /// <returns>Экземпляр доменной модели <see cref="Notification"/>.</returns>
    /// <remarks>
    /// Десериализация JSON-блоков истории попыток и аргументов выполняется строго через Source Generated контекст <see cref="AppJsonContext"/>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Notification ReadNotification(DbDataReader reader)
    {
        var idStr = reader.GetString(0);
        var titleKey = reader.IsDBNull(1) ? null : reader.GetString(1);
        var titleRaw = reader.IsDBNull(2) ? null : reader.GetString(2);
        var messageKey = reader.IsDBNull(3) ? null : reader.GetString(3);
        var messageRaw = reader.IsDBNull(4) ? null : reader.GetString(4);
        var messageArgsJson = reader.IsDBNull(5) ? null : reader.GetString(5);
        var recommendationKey = reader.IsDBNull(6) ? null : reader.GetString(6);
        var severity = reader.GetInt32(7);
        var isRead = reader.GetInt32(8) != 0;
        var trackId = reader.IsDBNull(9) ? null : reader.GetString(9);
        var trackTitle = reader.IsDBNull(10) ? null : reader.GetString(10);
        var exceptionDetails = reader.IsDBNull(11) ? null : reader.GetString(11);
        var attemptsJson = reader.IsDBNull(12) ? null : reader.GetString(12);

        DateTime createdAt = DateTime.UtcNow;
        if (!reader.IsDBNull(13))
        {
            var createdAtStr = reader.GetString(13);
            if (DateTime.TryParse(createdAtStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedDt))
            {
                createdAt = parsedDt;
            }
        }

        ObservableCollection<AttemptRecord>? attempts = null;
        if (!string.IsNullOrEmpty(attemptsJson))
        {
            try
            {
                var dtos = JsonSerializer.Deserialize(attemptsJson, AppJsonContext.Default.ListAttemptDto);
                if (dtos is { Count: > 0 })
                {
                    attempts = new ObservableCollection<AttemptRecord>();
                    for (int i = 0; i < dtos.Count; i++)
                    {
                        var d = dtos[i];
                        attempts.Add(new AttemptRecord(d.ClientName, d.Success, d.ErrorMessage, d.Timestamp));
                    }
                }
            }
            catch { }
        }

        object[]? args = null;
        if (!string.IsNullOrEmpty(messageArgsJson))
        {
            try
            {
                var strArray = JsonSerializer.Deserialize(messageArgsJson, AppJsonContext.Default.StringArray);
                if (strArray is { Length: > 0 })
                {
                    args = new object[strArray.Length];
                    for (int i = 0; i < strArray.Length; i++)
                    {
                        args[i] = strArray[i] ?? string.Empty;
                    }
                }
            }
            catch { }
        }

        return new Notification
        {
            Id = Guid.TryParse(idStr, out var parsedGuid) ? parsedGuid : Guid.NewGuid(),
            Timestamp = createdAt,
            TitleKey = titleKey,
            TitleRaw = titleRaw,
            MessageKey = messageKey,
            MessageRaw = messageRaw,
            MessageArgs = args,
            RecommendationKey = recommendationKey,
            Severity = (NotificationSeverity)severity,
            IsRead = isRead,
            TrackId = trackId,
            TrackTitle = trackTitle,
            ExceptionDetails = exceptionDetails,
            Attempts = attempts
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