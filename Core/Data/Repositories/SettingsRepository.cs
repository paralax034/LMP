using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MemoryPack;

namespace LMP.Core.Data.Repositories;

/// <summary>
/// SQLite-based settings repository.
/// Implemented using raw ADO.NET for full Native AOT compatibility without LINQ trees.
/// JSON processing strictly uses <see cref="AppJsonContext"/> metadata, ensuring identical behavior in Debug and Release.
/// </summary>
public sealed class SettingsRepository(ISqliteConnectionFactory factory) : ISettingsRepository
{
    private readonly ISqliteConnectionFactory _factory = factory;

    /// <inheritdoc />
    public async Task<T?> GetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(
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

        // 1. Primary path: persistent JSON (according to Value TEXT schema)
        if (rawValue is string jsonValue)
        {
            return DeserializeJson<T>(jsonValue);
        }

        // 2. Two-way migration bridge: binary BLOB (legacy MemoryPack or UTF-8 JSON)
        if (rawValue is byte[] binaryData)
        {
            T? result = null;
            bool shouldMigrateToJson = false;

            // Check if BLOB is a stored UTF-8 JSON
            if (binaryData.Length > 0 && (binaryData[0] == (byte)'{' || binaryData[0] == (byte)'['))
            {
                result = DeserializeJsonBytes<T>(binaryData);
            }

            // If not JSON — deserialize via MemoryPack (exact schema for migration to JSON)
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

            // Automatically persist data as reliable JSON, decoupling SQLite from binary dependencies
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
    public async Task<T> GetOrDefaultAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(
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

    // A type missing [JsonSerializable] in AppJsonContext is a registration bug, not a reason to silently fall back to reflection:
    // reflection fallback is disabled in Release, so Debug must fail the same way.
    private static JsonTypeInfo<T> ResolveTypeInfo<T>() =>
        AppJsonContext.DefaultCompact.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new NotSupportedException(
            $"Type '{typeof(T).FullName}' is not registered in AppJsonContext. Add [JsonSerializable(typeof({typeof(T).Name}))].");

    private static string SerializeJson<T>(T value) =>
        JsonSerializer.Serialize(value, ResolveTypeInfo<T>());

    private static T? DeserializeJson<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(json, ResolveTypeInfo<T>());
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
            return JsonSerializer.Deserialize(bytes, ResolveTypeInfo<T>());
        }
        catch
        {
            return null;
        }
    }

    #endregion
}