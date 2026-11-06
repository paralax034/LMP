using System.Data.Common;
using System.Text.Json;
using LMP.Core.Data;
using Microsoft.Data.Sqlite;

namespace LMP.Core.Services;

/// <summary>
/// Table row change for <c>CloudMutationOutbox</c>: <see cref="State"/> = <see langword="null"/> indicates deletion.
/// </summary>
public readonly record struct CloudOutboxRowChange(string OwnerId, string TargetId, CloudPlaylistOutboxState? State);

/// <summary>
/// SQLite store for the cloud mutation queue with AOT-safe serialization via <see cref="AppJsonContext"/>.
/// </summary>
public sealed class CloudOutboxStore(ISqliteConnectionFactory factory)
{
    /// <summary>
    /// Loads all non-empty queue states. Corrupted rows are skipped without interrupting hydration.
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
    /// Atomically applies a batch of changes (upsert/delete) in a single SQLite transaction.
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
/// Unified Outbox queue persistence pump: coalesces dirty keys within a debouncing window
/// and writes them in a single transaction. Reads the latest state at write time, preventing
/// upsert/delete order inversion. While the pump is paused (before hydration), writing is suppressed
/// so that early mutations do not overwrite rows from the previous session.
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
    /// Creates a persistence pump.
    /// </summary>
    /// <param name="snapshotProvider">Returns a clone of the current key state, or <see langword="null"/> for deletion.</param>
    /// <param name="writer">Transactional batch writer for changes.</param>
    /// <param name="coalesceWindowMs">Record coalescing window.</param>
    /// <param name="startPaused">Do not write until <see cref="Resume"/> is called.</param>
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

    /// <summary>Number of successfully committed transactional batches.</summary>
    public int BatchesWritten => Volatile.Read(ref _batchesWritten);

    /// <summary>Number of keys pending persistence.</summary>
    public int PendingCount
    {
        get
        {
            lock (_lock)
                return _dirty.Count;
        }
    }

    /// <summary>
    /// Marks a queue key as dirty. Non-blocking and performs no I/O.
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
    /// Unpauses the pump (after hydration) and starts persisting accumulated keys.
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
    /// Flushes accumulated keys immediately and waits for the pump to go idle, up to <paramref name="timeoutMs"/>.
    /// </summary>
    /// <returns><see langword="true"/> if all changes were persisted successfully.</returns>
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