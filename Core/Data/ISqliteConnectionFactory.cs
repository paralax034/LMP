using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace LMP.Core.Data;

/// <summary>
/// Contract for a SQLite connection factory optimized for minimal memory footprint and Native AOT.
/// </summary>
public interface ISqliteConnectionFactory
{
    /// <summary>
    /// Returns a configured connection string to the local database.
    /// </summary>
    string ConnectionString { get; }

    /// <summary>
    /// Creates a closed <see cref="SqliteConnection"/> instance.
    /// </summary>
    /// <returns>A connection instance configured with pooling and foreign keys.</returns>
    SqliteConnection CreateConnection();

    /// <summary>
    /// Creates, opens a connection, and applies SQLite session memory limits.
    /// </summary>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>An open <see cref="SqliteConnection"/> ready for command execution.</returns>
    ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken ct = default);

    /// <summary>
    /// Forces flushing native SQLite cache pages back to OS RAM.
    /// </summary>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>A task representing asynchronous page flushing and passive WAL checkpoint execution.</returns>
    /// <remarks>
    /// Invoked when the application is minimized to the system tray or enters idle mode.
    /// </remarks>
    ValueTask ShrinkMemoryAsync(CancellationToken ct = default);
}

/// <summary>
/// High-performance SQLite connection factory with process Working Set control.
/// </summary>
public sealed class LowMemorySqliteConnectionFactory : ISqliteConnectionFactory
{
    private readonly string _connectionString;

    /// <summary>
    /// Initializes the SQLite connection factory with low-footprint pooling settings.
    /// </summary>
    /// <param name="databaseFilePath">Path to the SQLite database file on disk.</param>
    public LowMemorySqliteConnectionFactory(string databaseFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseFilePath);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
            ForeignKeys = true
        };

        _connectionString = builder.ToString();
    }

    /// <inheritdoc />
    public string ConnectionString => _connectionString;

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SqliteConnection CreateConnection()
    {
        return new SqliteConnection(_connectionString);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await ApplySessionPragmasAsync(connection, ct).ConfigureAwait(false);

        return connection;
    }

    /// <inheritdoc />
    public async ValueTask ShrinkMemoryAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA shrink_memory; PRAGMA wal_checkpoint(PASSIVE);";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies lightweight PRAGMA directives to an open SQLite connection.
    /// </summary>
    /// <param name="connection">An open database connection.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>A task representing the connection settings application.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static async ValueTask ApplySessionPragmasAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA synchronous = NORMAL;
            PRAGMA cache_size = -4000;
            PRAGMA temp_store = FILE;
            PRAGMA mmap_size = 0;
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}