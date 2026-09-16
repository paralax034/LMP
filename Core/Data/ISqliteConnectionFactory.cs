using System.Runtime.CompilerServices;
using global::Microsoft.Data.Sqlite;

namespace LMP.Core.Data;

/// <summary>
/// Контракт фабрики подключений SQLite, оптимизированной для минимального потребления памяти и Native AOT.
/// </summary>
public interface ISqliteConnectionFactory
{
    /// <summary>
    /// Возвращает сконфигурированную строку подключения к локальной базе данных.
    /// </summary>
    string ConnectionString { get; }

    /// <summary>
    /// Создает закрытый экземпляр подключения <see cref="SqliteConnection"/>.
    /// </summary>
    /// <returns>Экземпляр подключения с настроенным пулингом и внешними ключами.</returns>
    SqliteConnection CreateConnection();

    /// <summary>
    /// Создает, открывает подключение и применяет сессионные ограничения памяти SQLite.
    /// </summary>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Открытое и готовое к выполнению команд подключение <see cref="SqliteConnection"/>.</returns>
    ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken ct = default);

    /// <summary>
    /// Принудительно сбрасывает страницы нативного кэша SQLite обратно в оперативную память ОС.
    /// </summary>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Задача асинхронного сброса страниц и выполнения пассивного чекпоинта WAL.</returns>
    /// <remarks>
    /// Вызывается при минимизации приложения в системный трей или переходе в режим простоя.
    /// </remarks>
    ValueTask ShrinkMemoryAsync(CancellationToken ct = default);
}

/// <summary>
/// Высокопроизводительная фабрика подключений SQLite с контролем Working Set процесса.
/// </summary>
public sealed class LowMemorySqliteConnectionFactory : ISqliteConnectionFactory
{
    private readonly string _connectionString;

    /// <summary>
    /// Инициализирует фабрику подключений SQLite с низкопотребляющими параметрами пулинга.
    /// </summary>
    /// <param name="databaseFilePath">Путь к файлу базы данных SQLite на диске.</param>
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
    /// Применяет легковесные PRAGMA-директивы к открытому соединению SQLite.
    /// </summary>
    /// <param name="connection">Открытое соединение базы данных.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Задача применения параметров соединения.</returns>
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