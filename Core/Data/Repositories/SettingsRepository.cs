using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace LMP.Core.Data.Repositories;

/// <summary>
/// Репозиторий настроек на базе SQLite.
/// Реализован на прямом ADO.NET для полной совместимости с Native AOT без использования LINQ-деревьев.
/// </summary>
public sealed class SettingsRepository(ISqliteConnectionFactory factory) : ISettingsRepository
{
    private readonly ISqliteConnectionFactory _factory = factory;

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(
        string key,
        JsonTypeInfo<T> typeInfo,
        CancellationToken ct = default) where T : class
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Value FROM Settings WHERE Key = @key LIMIT 1;";

            var param = cmd.CreateParameter();
            param.ParameterName = "@key";
            param.Value = key;
            cmd.Parameters.Add(param);

            var rawValue = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (rawValue is not string jsonValue)
                return null;

            Log.Info($"[SettingsRepository] Loaded '{key}' from DB: {jsonValue}");
            return JsonSerializer.Deserialize(jsonValue, typeInfo);
        }
        finally
        {
            // Соединение возвращается в пул при Dispose
        }
    }

    /// <inheritdoc />
    public async Task<T> GetOrDefaultAsync<T>(
        string key,
        T defaultValue,
        JsonTypeInfo<T> typeInfo,
        CancellationToken ct = default) where T : class
    {
        return await GetAsync(key, typeInfo, ct).ConfigureAwait(false) ?? defaultValue;
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(
         string key,
         T value,
         JsonTypeInfo<T> typeInfo,
         CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        var json = JsonSerializer.Serialize(value, typeInfo);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO Settings (Key, Value) VALUES (@key, @value) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";

        var pKey = cmd.CreateParameter();
        pKey.ParameterName = "@key";
        pKey.Value = key;
        cmd.Parameters.Add(pKey);

        var pValue = cmd.CreateParameter();
        pValue.ParameterName = "@value";
        pValue.Value = json;
        cmd.Parameters.Add(pValue);

        // Прямой SQL Upsert в обход ChangeTracker EF Core (гарантирует реальное обновление строки в SQLite)
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // Сбрасываем страницы WAL в основной файл на диске
        try
        {
            await using var walCmd = connection.CreateCommand();
            walCmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
            await walCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch { }

        Log.Info($"[SettingsRepository] Successfully committed '{key}' to database ({json.Length} bytes)");
    }

    /// <inheritdoc />
    public void Set<T>(
        string key,
        T value,
        JsonTypeInfo<T> typeInfo)
    {
        using var connection = _factory.CreateConnection();
        connection.Open();

        var json = JsonSerializer.Serialize(value, typeInfo);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO Settings (Key, Value) VALUES (@key, @value) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";

        var pKey = cmd.CreateParameter();
        pKey.ParameterName = "@key";
        pKey.Value = key;
        cmd.Parameters.Add(pKey);

        var pValue = cmd.CreateParameter();
        pValue.ParameterName = "@value";
        pValue.Value = json;
        cmd.Parameters.Add(pValue);

        cmd.ExecuteNonQuery();

        try
        {
            using var walCmd = connection.CreateCommand();
            walCmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
            walCmd.ExecuteNonQuery();
        }
        catch { }

        Log.Info($"[SettingsRepository] Successfully committed '{key}' (sync) to database ({json.Length} bytes)");
    }
}