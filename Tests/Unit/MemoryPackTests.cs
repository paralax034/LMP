using System.Text.Json;
using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Http;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Data.Repositories;
using LMP.Tests.Framework;
using MemoryPack;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Tests.Unit;

/// <summary>
/// Модульные и интеграционные тесты подсистемы сериализации MemoryPack.
/// Проверяют отказоустойчивость (.bak fallback), миграцию legacy JSON и аллокации кучи.
/// </summary>
public static class MemoryPackTests
{
    private const int AllocationMeasurementIterations = 1_000;
    private const int AllocationWarmupIterations = 10;

    /// <summary>
    /// Проверяет отказоустойчивость бинарного чтения: при нулевом размере целевого файла
    /// данные восстанавливаются из .bak и основной файл перезаписывается (Auto-Heal).
    /// </summary>
    [TestMethod(TestCategory.Unit, "AtomicFile: Binary .bak fallback & Auto-Heal", Group = TestGroups.Cache, Order = 1)]
    public static Task TestAtomicFileBinaryBakRecoveryAsync()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"lmp_atomic_test_{Guid.NewGuid():N}.bin");
        var tempBak = tempFile + ".bak";

        try
        {
            byte[] expectedPayload = [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04];
            byte[] secondPayload = [0xAA, 0xBB, 0xCC, 0xDD];

            // 1. Первичная запись для создания файла
            AtomicFile.WriteBytes(tempFile, expectedPayload, createBackup: false);

            // 2. Повторная запись с флагом createBackup=true гарантирует создание .bak с expectedPayload
            AtomicFile.WriteBytes(tempFile, secondPayload, createBackup: true);

            // 3. Имитируем torn write: повреждаем основной файл до 0 байт
            File.WriteAllBytes(tempFile, []);

            // 4. Вызываем чтение с fallback
            var actualBytes = AtomicFile.ReadBytesWithFallback(tempFile, out bool recovered);

            if (!recovered)
                throw new InvalidOperationException("[Assertion Failed] Expected recoveredFromBackup to be true.");

            if (actualBytes == null || !actualBytes.AsSpan().SequenceEqual(expectedPayload))
                throw new InvalidOperationException("[Assertion Failed] Recovered payload does not match expected bytes.");

            // 5. Проверяем Auto-Heal: основной файл должен быть восстановлен из бэкапа
            var healedBytes = File.ReadAllBytes(tempFile);
            if (!healedBytes.AsSpan().SequenceEqual(expectedPayload))
                throw new InvalidOperationException("[Assertion Failed] Target file was not auto-healed after fallback.");

            Log.Info("[MemoryPackTests] AtomicFile binary .bak recovery and auto-heal verified successfully.");
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            try { if (File.Exists(tempBak)) File.Delete(tempBak); } catch { }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Проверяет бинарный roundtrip индекса аудиокэша и замеряет сокращение аллокаций кучи
    /// по сравнению с System.Text.Json.
    /// </summary>
    [TestMethod(TestCategory.Unit, "AudioCache: MemoryPack roundtrip & heap allocations", Group = TestGroups.Cache, Order = 2)]
    public static Task TestAudioCacheBinaryRoundtripAndAllocationAsync()
    {
        var envelope = CreateSampleCacheIndex(entriesCount: 50);

        // 1. Проверка корректности Roundtrip
        byte[] binaryData = MemoryPackSerializer.Serialize(envelope);
        var deserialized = MemoryPackSerializer.Deserialize<AudioCacheManager.AudioCacheIndexEnvelope>(binaryData);

        if (deserialized == null || deserialized.Entries.Count != envelope.Entries.Count)
            throw new InvalidOperationException("[Assertion Failed] MemoryPack roundtrip corrupted entry count.");

        if (deserialized.Entries[0].CacheKey != envelope.Entries[0].CacheKey ||
            deserialized.Entries[0].TotalSize != envelope.Entries[0].TotalSize)
        {
            throw new InvalidOperationException("[Assertion Failed] Entry properties mismatch after MemoryPack roundtrip.");
        }

        // 2. Подготовка JSON-пейлоуда для сравнительного замера
        string jsonText = JsonSerializer.Serialize(envelope, AppJsonContext.Default.AudioCacheIndexEnvelope);

        // 3. Прогрев JIT
        for (int i = 0; i < AllocationWarmupIterations; i++)
        {
            _ = MemoryPackSerializer.Deserialize<AudioCacheManager.AudioCacheIndexEnvelope>(binaryData);
            _ = JsonSerializer.Deserialize(jsonText, AppJsonContext.Default.AudioCacheIndexEnvelope);
        }

        // 4. Замер аллокаций кучи вызывающего потока для MemoryPack
        long memPackAllocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < AllocationMeasurementIterations; i++)
        {
            _ = MemoryPackSerializer.Deserialize<AudioCacheManager.AudioCacheIndexEnvelope>(binaryData);
        }
        long memPackAllocated = GC.GetAllocatedBytesForCurrentThread() - memPackAllocBefore;

        // 5. Замер аллокаций кучи вызывающего потока для System.Text.Json
        long jsonAllocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < AllocationMeasurementIterations; i++)
        {
            _ = JsonSerializer.Deserialize(jsonText, AppJsonContext.Default.AudioCacheIndexEnvelope);
        }
        long jsonAllocated = GC.GetAllocatedBytesForCurrentThread() - jsonAllocBefore;

        double memPackPerCall = (double)memPackAllocated / AllocationMeasurementIterations;
        double jsonPerCall = (double)jsonAllocated / AllocationMeasurementIterations;
        double ratio = jsonPerCall / Math.Max(1, memPackPerCall);

        Log.Info($"[MemoryPackTests] Deserialization allocations ({envelope.Entries.Count} entries): " +
                 $"MemoryPack = {memPackPerCall:F0} B/call, JSON = {jsonPerCall:F0} B/call. " +
                 $"Reduction: {ratio:F1}x less GC pressure.");

        if (memPackAllocated >= jsonAllocated)
            throw new InvalidOperationException($"[Assertion Failed] MemoryPack should allocate significantly less than JSON ({memPackAllocated} >= {jsonAllocated})");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Проверяет бинарный roundtrip кэша манифестов сессий.
    /// </summary>
    [TestMethod(TestCategory.Unit, "SessionCache: MemoryPack roundtrip", Group = TestGroups.Cache, Order = 3)]
    public static Task TestSessionCacheBinaryRoundtripAsync()
    {
        var envelope = new SessionCacheEnvelope
        {
            LastCleanupUtc = DateTime.UtcNow,
            Manifests =
            [
                new TrackManifestEntry
                {
                    TrackId = "yt_test_track_1",
                    CdnHost = "rr1---sn-4g5ednle.googlevideo.com",
                    ExpireUtc = DateTime.UtcNow.AddHours(6),
                    IntegratedLufs = -14.2f,
                    SavedAtUtc = DateTime.UtcNow,
                    Variants =
                    [
                        new VariantEntry
                        {
                            Itag = 251,
                            Url = "https://rr1---sn-4g5ednle.googlevideo.com/videoplayback?id=123",
                            Container = "webm",
                            Codec = "opus",
                            Bitrate = 160_000,
                            Clen = 5_242_880,
                            LanguageCode = "en",
                            IsDefaultLanguage = true
                        }
                    ]
                }
            ]
        };

        byte[] binary = MemoryPackSerializer.Serialize(envelope);
        var restored = MemoryPackSerializer.Deserialize<SessionCacheEnvelope>(binary);

        if (restored is null || restored.Manifests.Count != 1)
            throw new InvalidOperationException("[Assertion Failed] SessionCacheEnvelope manifests count mismatch.");

        var manifest = restored.Manifests[0];
        if (manifest.TrackId != "yt_test_track_1" || manifest.Variants.Count != 1)
            throw new InvalidOperationException("[Assertion Failed] Manifest variant data corrupted.");

        var variant = manifest.Variants[0];
        if (variant.Itag != 251 || variant.Format != AudioFormat.WebM || variant.CodecType != AudioCodec.Opus)
            throw new InvalidOperationException("[Assertion Failed] Computed properties (Format/CodecType) mismatch.");

        Log.Info("[MemoryPackTests] SessionCache MemoryPack serialization verified successfully.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Проверяет прозрачную миграцию настроек: запись legacy JSON в SQLite,
    /// последующее считывание через репозиторий и подтверждение автоматической конвертации в BLOB.
    /// </summary>
    [TestMethod(TestCategory.Integration, "Settings: SQLite JSON -> MemoryPack BLOB migration", Group = TestGroups.Cache, Order = 4)]
    public static async Task TestSettingsRepositoryBinaryBlobAndLegacyMigrationAsync(IServiceProvider services)
    {
        var settingsRepo = services.GetRequiredService<ISettingsRepository>();
        var connectionFactory = services.GetRequiredService<LMP.Core.Data.ISqliteConnectionFactory>();

        const string testKey = "MigrationTest_AppSettings";

        // 1. Имитируем устаревшую запись: вставляем сырой JSON-текст напрямую в SQLite
        var legacyJson = JsonSerializer.Serialize(new AppSettings
        {
            Volume = 84,
            DownloadPath = "D:\\LegacyDownloads"
        }, AppJsonContext.Default.AppSettings);

        await using (var conn = await connectionFactory.OpenConnectionAsync())
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO Settings (Key, Value) VALUES (@key, @value) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";

            var pKey = cmd.CreateParameter();
            pKey.ParameterName = "@key";
            pKey.Value = testKey;
            cmd.Parameters.Add(pKey);

            var pVal = cmd.CreateParameter();
            pVal.ParameterName = "@value";
            pVal.Value = legacyJson;
            cmd.Parameters.Add(pVal);

            await cmd.ExecuteNonQueryAsync();
        }

        // 2. Считываем через SettingsRepository (должна сработать автомиграция в MemoryPack)
        var loaded = await settingsRepo.GetAsync<AppSettings>(testKey);

        if (loaded == null || loaded.Volume != 84 || loaded.DownloadPath != "D:\\LegacyDownloads")
            throw new InvalidOperationException("[Assertion Failed] Failed to deserialize legacy JSON through SettingsRepository.");

        // 3. Проверяем, что значение в БД было прозрачно перезаписано как бинарный BLOB
        await using (var conn = await connectionFactory.OpenConnectionAsync())
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT typeof(Value), Value FROM Settings WHERE Key = @key LIMIT 1;";

            var pKey = cmd.CreateParameter();
            pKey.ParameterName = "@key";
            pKey.Value = testKey;
            cmd.Parameters.Add(pKey);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                throw new InvalidOperationException("[Assertion Failed] Setting row missing after migration.");

            string typeName = reader.GetString(0);
            object rawValue = reader.GetValue(1);

            if (!string.Equals(typeName, "blob", StringComparison.OrdinalIgnoreCase) && rawValue is not byte[])
                throw new InvalidOperationException($"[Assertion Failed] Expected SQLite type to be 'blob', got '{typeName}'.");

            Log.Info($"[MemoryPackTests] Verified: SQLite setting migrated from TEXT JSON to BLOB ({((byte[])rawValue).Length} bytes).");
        }

        // 4. Очистка тестового ключа
        await using (var conn = await connectionFactory.OpenConnectionAsync())
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Settings WHERE Key = @key;";
            var pKey = cmd.CreateParameter();
            pKey.ParameterName = "@key";
            pKey.Value = testKey;
            cmd.Parameters.Add(pKey);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static AudioCacheManager.AudioCacheIndexEnvelope CreateSampleCacheIndex(int entriesCount)
    {
        var entries = new List<AudioCacheEntry>(entriesCount);

        for (int i = 0; i < entriesCount; i++)
        {
            entries.Add(new AudioCacheEntry
            {
                CacheKey = $"yt_track_{i}_WebM_160",
                TrackId = $"yt_track_{i}",
                OriginalUrl = $"https://rr1.googlevideo.com/videoplayback?id={i}",
                TotalSize = 10_485_760,
                ActualFileSize = 10_485_760,
                Bitrate = 160,
                DurationMs = 240_000,
                AlignmentBytes = 65_536,
                Format = AudioFormat.WebM,
                Codec = AudioCodec.Opus,
                CreatedAt = DateTime.UtcNow.AddDays(-1),
                LastAccessedAt = DateTime.UtcNow,
                IsComplete = true,
                IntegratedLufs = -14.0f,
                IntegratedLufsSource = 1,
                DownloadedRangesData =
                [
                    new SerializedDownloadedRange { Start = 0, EndExclusive = 5_242_880 },
                    new SerializedDownloadedRange { Start = 5_242_880, EndExclusive = 10_485_760 }
                ]
            });
        }

        return new AudioCacheManager.AudioCacheIndexEnvelope
        {
            SchemaVersion = 4,
            Entries = entries
        };
    }
}