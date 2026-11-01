using System.Data.Common;
using System.Text.Json;
using LMP.Core.Data;
using Microsoft.Data.Sqlite;

namespace LMP.Core.Services;

/// <summary>
/// Изменение строки таблицы <c>CloudMutationOutbox</c>: <see cref="State"/> = <see langword="null"/> означает удаление.
/// </summary>
public readonly record struct CloudOutboxRowChange(string OwnerId, string TargetId, CloudPlaylistOutboxState? State);

/// <summary>
/// SQLite-хранилище очереди облачных мутаций с AOT-безопасной сериализацией через <see cref="AppJsonContext"/>.
/// </summary>
public sealed class CloudOutboxStore(ISqliteConnectionFactory factory)
{
    /// <summary>
    /// Загружает все непустые состояния очереди. Повреждённые строки пропускаются без прерывания гидратации.
    /// </summary>
    public async Task<List<CloudPlaylistOutboxState>> LoadAllAsync(CancellationToken ct = default)
    {
        var result = new List<CloudPlaylistOutboxState>();

        await using var connection = await factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT TargetId, OwnerId, PayloadJson, UpdatedAtTicks, RetryCount
            FROM CloudMutationOutbox
            ORDER BY UpdatedAtTicks;
            """;

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var targetId = reader.GetString(0);
            var ownerId = reader.GetString(1);

            CloudPlaylistOutboxState? restored;
            try
            {
                restored = JsonSerializer.Deserialize(reader.GetString(2), AppJsonContext.DefaultCompact.CloudPlaylistOutboxState);
            }
            catch (JsonException ex)
            {
                Log.Warn($"[PlaylistOutbox] Skipping corrupted outbox row '{targetId}': {ex.Message}");
                continue;
            }

            if (restored == null || restored.IsEmpty ||
                !string.Equals(restored.TargetId, targetId, StringComparison.Ordinal) ||
                !string.Equals(restored.OwnerId, ownerId, StringComparison.Ordinal))
            {
                continue;
            }

            long updatedTicks = reader.GetInt64(3);
            restored.UpdatedAtUtcTicks = updatedTicks;
            if (restored.FirstEnqueuedAtUtcTicks <= 0)
                restored.FirstEnqueuedAtUtcTicks = updatedTicks;
            restored.RetryCount = reader.GetInt32(4);
            result.Add(restored);
        }

        return result;
    }

    /// <summary>
    /// Атомарно применяет пакет изменений (upsert/delete) в одной транзакции SQLite.
    /// </summary>
    public async Task WriteBatchAsync(IReadOnlyList<CloudOutboxRowChange> changes, CancellationToken ct = default)
    {
        if (changes.Count == 0)
            return;

        await using var connection = await factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO CloudMutationOutbox (TargetId, OwnerId, PayloadJson, UpdatedAtTicks, RetryCount)
            VALUES (@tId, @oId, @json, @ticks, @retry)
            ON CONFLICT(TargetId, OwnerId) DO UPDATE SET
                PayloadJson = excluded.PayloadJson,
                UpdatedAtTicks = excluded.UpdatedAtTicks,
                RetryCount = excluded.RetryCount;
            """;
        var upTarget = AddParameter(upsert, "@tId");
        var upOwner = AddParameter(upsert, "@oId");
        var upJson = AddParameter(upsert, "@json");
        var upTicks = AddParameter(upsert, "@ticks");
        var upRetry = AddParameter(upsert, "@retry");

        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM CloudMutationOutbox WHERE TargetId = @tId AND OwnerId = @oId;";
        var delTarget = AddParameter(delete, "@tId");
        var delOwner = AddParameter(delete, "@oId");

        for (int i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            if (change.State is { IsEmpty: false } state)
            {
                upTarget.Value = change.TargetId;
                upOwner.Value = change.OwnerId;
                upJson.Value = JsonSerializer.Serialize(state, AppJsonContext.DefaultCompact.CloudPlaylistOutboxState);
                upTicks.Value = state.UpdatedAtUtcTicks;
                upRetry.Value = state.RetryCount;
                await upsert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            else
            {
                delTarget.Value = change.TargetId;
                delOwner.Value = change.OwnerId;
                await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private static DbParameter AddParameter(DbCommand cmd, string name)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        cmd.Parameters.Add(p);
        return p;
    }
}

/// <summary>
/// Единый насос персистентности очереди Outbox: схлопывает «грязные» ключи в окне коалесценции
/// и пишет их одной транзакцией. Читает самое свежее состояние в момент записи, поэтому порядок
/// upsert/delete не может инвертироваться. Пока насос на паузе (до гидратации), запись не выполняется,
/// чтобы ранние мутации не перезаписали строки предыдущей сессии.
/// </summary>
public sealed class CloudOutboxPersistencePump
{
    private const int MaxWriteAttempts = 3;

    private readonly Func<string, string, CloudPlaylistOutboxState?> _snapshotProvider;
    private readonly Func<IReadOnlyList<CloudOutboxRowChange>, CancellationToken, Task> _writer;
    private readonly int _coalesceWindowMs;
    private readonly Lock _lock = new();

    private HashSet<(string OwnerId, string TargetId)> _dirty = [];
    private TaskCompletionSource _idleTcs = CreateCompletedTcs();
    private TaskCompletionSource _flushNowTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _isRunning;
    private bool _isPaused;
    private int _batchesWritten;

    /// <summary>
    /// Создаёт насос персистентности.
    /// </summary>
    /// <param name="snapshotProvider">Возвращает клон актуального состояния ключа либо <see langword="null"/> для удаления.</param>
    /// <param name="writer">Транзакционная запись пакета изменений.</param>
    /// <param name="coalesceWindowMs">Окно схлопывания записей.</param>
    /// <param name="startPaused">Не писать до вызова <see cref="Resume"/>.</param>
    public CloudOutboxPersistencePump(
        Func<string, string, CloudPlaylistOutboxState?> snapshotProvider,
        Func<IReadOnlyList<CloudOutboxRowChange>, CancellationToken, Task> writer,
        int coalesceWindowMs = 300,
        bool startPaused = false)
    {
        _snapshotProvider = snapshotProvider;
        _writer = writer;
        _coalesceWindowMs = Math.Max(0, coalesceWindowMs);
        _isPaused = startPaused;
    }

    /// <summary>Количество успешно записанных транзакционных пакетов.</summary>
    public int BatchesWritten => Volatile.Read(ref _batchesWritten);

    /// <summary>Количество ключей, ожидающих записи.</summary>
    public int PendingCount
    {
        get
        {
            lock (_lock)
                return _dirty.Count;
        }
    }

    /// <summary>
    /// Помечает ключ очереди как изменённый. Не блокирует вызывающий поток и не выполняет I/O.
    /// </summary>
    public void MarkDirty(string ownerId, string targetId)
    {
        lock (_lock)
        {
            _dirty.Add((ownerId, targetId));
            StartPumpUnderLock();
        }
    }

    /// <summary>
    /// Снимает паузу (после гидратации) и запускает запись накопленных ключей.
    /// </summary>
    public void Resume()
    {
        lock (_lock)
        {
            _isPaused = false;
            StartPumpUnderLock();
        }
    }

    /// <summary>
    /// Немедленно сбрасывает накопленные ключи и ожидает простоя насоса не дольше <paramref name="timeoutMs"/>.
    /// </summary>
    /// <returns><see langword="true"/>, если все изменения записаны.</returns>
    public async Task<bool> DrainAsync(int timeoutMs)
    {
        Task idleTask;
        lock (_lock)
        {
            if (_isPaused)
                return _dirty.Count == 0;

            _flushNowTcs.TrySetResult();
            idleTask = _idleTcs.Task;
        }

        if (!idleTask.IsCompleted)
            await Task.WhenAny(idleTask, Task.Delay(timeoutMs)).ConfigureAwait(false);

        lock (_lock)
            return idleTask.IsCompleted && _dirty.Count == 0;
    }

    private void StartPumpUnderLock()
    {
        if (_isRunning || _isPaused || _dirty.Count == 0)
            return;

        _isRunning = true;
        _idleTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(PumpLoopAsync);
    }

    private async Task PumpLoopAsync()
    {
        int failedAttempts = 0;

        while (true)
        {
            Task flushNowTask;
            lock (_lock)
                flushNowTask = _flushNowTcs.Task;

            if (!flushNowTask.IsCompleted && _coalesceWindowMs > 0)
                await Task.WhenAny(flushNowTask, Task.Delay(_coalesceWindowMs)).ConfigureAwait(false);

            HashSet<(string OwnerId, string TargetId)> batchKeys;
            lock (_lock)
            {
                if (_dirty.Count == 0 || _isPaused)
                {
                    _isRunning = false;
                    if (_flushNowTcs.Task.IsCompleted)
                        _flushNowTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _idleTcs.TrySetResult();
                    return;
                }

                batchKeys = _dirty;
                _dirty = new HashSet<(string OwnerId, string TargetId)>(batchKeys.Count);
            }

            try
            {
                var changes = new List<CloudOutboxRowChange>(batchKeys.Count);
                foreach (var (ownerId, targetId) in batchKeys)
                    changes.Add(new CloudOutboxRowChange(ownerId, targetId, _snapshotProvider(ownerId, targetId)));

                await _writer(changes, CancellationToken.None).ConfigureAwait(false);
                Interlocked.Increment(ref _batchesWritten);
                failedAttempts = 0;
            }
            catch (Exception ex)
            {
                failedAttempts++;
                if (failedAttempts < MaxWriteAttempts)
                {
                    Log.Debug($"[PlaylistOutbox] Persist batch failed (attempt {failedAttempts}): {ex.Message}");
                    lock (_lock)
                    {
                        foreach (var key in batchKeys)
                            _dirty.Add(key);
                    }

                    await Task.Delay(100 * failedAttempts).ConfigureAwait(false);
                }
                else
                {
                    Log.Warn($"[PlaylistOutbox] Dropping persist batch of {batchKeys.Count} row(s) after {failedAttempts} attempts: {ex.Message}");
                    failedAttempts = 0;
                }
            }
        }
    }

    private static TaskCompletionSource CreateCompletedTcs()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.TrySetResult();
        return tcs;
    }
}