using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using LMP.Core.Models;
using MemoryPack;

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
        CancellationToken ct = default) where T : class
    {
        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        object? rawValue;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT Value FROM Settings WHERE Key = @key LIMIT 1;";

            var param = cmd.CreateParameter();
            param.ParameterName = "@key";
            param.Value = key;
            cmd.Parameters.Add(param);

            rawValue = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        }

        if (rawValue is null or DBNull)
            return null;

        // 1. Основной путь: персистентный JSON (согласно схеме Value TEXT)
        if (rawValue is string jsonValue)
        {
            return DeserializeJson<T>(jsonValue);
        }

        // 2. Двусторонний мост миграции: бинарный BLOB (legacy MemoryPack или UTF-8 JSON)
        if (rawValue is byte[] binaryData)
        {
            T? result = null;
            bool shouldMigrateToJson = false;

            // Проверяем, не является ли BLOB сохранённым UTF-8 JSON
            if (binaryData.Length > 0 && (binaryData[0] == (byte)'{' || binaryData[0] == (byte)'['))
            {
                result = DeserializeJsonBytes<T>(binaryData);
            }

            // Если не JSON — десериализуем через MemoryPack (точная схема для миграции в JSON)
            if (result == null)
            {
                try
                {
                    result = MemoryPackSerializer.Deserialize<T>(binaryData);
                    if (result != null)
                    {
                        shouldMigrateToJson = true;
                    }
                }
                catch (MemoryPackSerializationException ex)
                {
                    Log.Warn($"[SettingsRepository] Legacy binary layout mismatch for '{key}': {ex.Message}. Falling back to default.");
                }
                catch (Exception ex)
                {
                    Log.Error($"[SettingsRepository] Unexpected error reading binary '{key}': {ex.Message}");
                }
            }

            // Автоматически фиксируем данные в надёжном JSON, освобождая SQLite от бинарной привязки
            if (shouldMigrateToJson && result != null)
            {
                Log.Info($"[SettingsRepository] Auto-migrating setting '{key}' from binary BLOB to resilient JSON format...");
                try
                {
                    await SetAsync(key, result, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warn($"[SettingsRepository] Failed to persist migrated JSON for '{key}': {ex.Message}");
                }
            }

            return result;
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<T> GetOrDefaultAsync<T>(
        string key,
        T defaultValue,
        CancellationToken ct = default) where T : class
    {
        return await GetAsync<T>(key, ct).ConfigureAwait(false) ?? defaultValue;
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(
         string key,
         T value,
         CancellationToken ct = default)
    {
        string jsonPayload = SerializeJson(value);

        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO Settings (Key, Value) VALUES (@key, @value) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";

        var pKey = cmd.CreateParameter();
        pKey.ParameterName = "@key";
        pKey.Value = key;
        cmd.Parameters.Add(pKey);

        var pValue = cmd.CreateParameter();
        pValue.ParameterName = "@value";
        pValue.Value = jsonPayload;
        cmd.Parameters.Add(pValue);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        try
        {
            await using var walCmd = connection.CreateCommand();
            walCmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
            await walCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch { }

        Log.Info($"[SettingsRepository] Successfully committed '{key}' to database (JSON)");
    }

    /// <inheritdoc />
    public void Set<T>(
        string key,
        T value)
    {
        string jsonPayload = SerializeJson(value);

        using var connection = _factory.CreateConnection();
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO Settings (Key, Value) VALUES (@key, @value) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";

        var pKey = cmd.CreateParameter();
        pKey.ParameterName = "@key";
        pKey.Value = key;
        cmd.Parameters.Add(pKey);

        var pValue = cmd.CreateParameter();
        pValue.ParameterName = "@value";
        pValue.Value = jsonPayload;
        cmd.Parameters.Add(pValue);

        cmd.ExecuteNonQuery();

        try
        {
            using var walCmd = connection.CreateCommand();
            walCmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
            walCmd.ExecuteNonQuery();
        }
        catch { }

        Log.Info($"[SettingsRepository] Successfully committed '{key}' (sync) to database (JSON)");
    }

    #region Serialization Helpers

    private static string SerializeJson<T>(T value)
    {
        if (AppJsonContext.DefaultCompact.GetTypeInfo(typeof(T)) is JsonTypeInfo<T> jsonTypeInfo)
        {
            return JsonSerializer.Serialize(value, jsonTypeInfo);
        }

        return JsonSerializer.Serialize(value);
    }

    private static T? DeserializeJson<T>(string json) where T : class
    {
        try
        {
            if (AppJsonContext.DefaultCompact.GetTypeInfo(typeof(T)) is JsonTypeInfo<T> jsonTypeInfo)
            {
                return JsonSerializer.Deserialize(json, jsonTypeInfo);
            }

            return JsonSerializer.Deserialize<T>(json);
        }
        catch (Exception ex)
        {
            Log.Error($"[SettingsRepository] JSON deserialization failed for {typeof(T).Name}: {ex.Message}");
            return null;
        }
    }

    private static T? DeserializeJsonBytes<T>(byte[] bytes) where T : class
    {
        try
        {
            if (AppJsonContext.DefaultCompact.GetTypeInfo(typeof(T)) is JsonTypeInfo<T> jsonTypeInfo)
            {
                return JsonSerializer.Deserialize(bytes, jsonTypeInfo);
            }

            return JsonSerializer.Deserialize<T>(bytes);
        }
        catch
        {
            return null;
        }
    }

    #endregion
}