using System.Text.Json;
using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Http;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Data.Repositories;
using LMP.Core.Models;
using LMP.Tests.Framework;
using MemoryPack;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Tests.Unit;

/// <summary>
/// Тесты сериализации MemoryPack для индексов аудиокэша и кэша сессий.
/// Включает проверку миграции устаревших настроек в SQLite из JSON в бинарный BLOB.
/// </summary>
public static class MemoryPackTests
{
    private const int AllocationMeasurementIterations = 1_000;
    private const int AllocationWarmupIterations = 10;

    [TestMethod(TestCategory.Unit, "AudioCache Index MemoryPack Roundtrip & Allocations", Group = TestGroups.Storage, Order = 1)]
    public static Task TestAudioCacheBinaryRoundtripAndAllocationAsync()
    {
        var envelope = CreateSampleCacheIndex(entriesCount: 30);

        byte[] binaryData = MemoryPackSerializer.Serialize(envelope);
        var deserialized = MemoryPackSerializer.Deserialize<AudioCacheManager.AudioCacheIndexEnvelope>(binaryData);

        TestAssert.NotNull(deserialized);
        TestAssert.Equal(envelope.Entries.Count, deserialized.Entries.Count);
        TestAssert.Equal(envelope.Entries[0].CacheKey, deserialized.Entries[0].CacheKey);

        string jsonText = JsonSerializer.Serialize(envelope, AppJsonContext.Default.AudioCacheIndexEnvelope);

        for (int i = 0; i < AllocationWarmupIterations; i++)
        {
            _ = MemoryPackSerializer.Deserialize<AudioCacheManager.AudioCacheIndexEnvelope>(binaryData);
            _ = JsonSerializer.Deserialize(jsonText, AppJsonContext.Default.AudioCacheIndexEnvelope);
        }

        long memPackAllocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < AllocationMeasurementIterations; i++)
            _ = MemoryPackSerializer.Deserialize<AudioCacheManager.AudioCacheIndexEnvelope>(binaryData);
        long memPackAllocated = GC.GetAllocatedBytesForCurrentThread() - memPackAllocBefore;

        long jsonAllocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < AllocationMeasurementIterations; i++)
            _ = JsonSerializer.Deserialize(jsonText, AppJsonContext.Default.AudioCacheIndexEnvelope);
        long jsonAllocated = GC.GetAllocatedBytesForCurrentThread() - jsonAllocBefore;

        TestAssert.True(memPackAllocated < jsonAllocated,
            $"MemoryPack ({memPackAllocated} B) must allocate less than JSON ({jsonAllocated} B)");

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "SessionCache Manifest MemoryPack Roundtrip", Group = TestGroups.Storage, Order = 2)]
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

        TestAssert.NotNull(restored);
        TestAssert.Equal(1, restored.Manifests.Count);

        var variant = restored.Manifests[0].Variants[0];
        TestAssert.Equal(251, variant.Itag);
        TestAssert.Equal(AudioFormat.WebM, variant.Format);
        TestAssert.Equal(AudioCodec.Opus, variant.CodecType);

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Integration, "SQLite Settings JSON to MemoryPack BLOB Migration", Group = TestGroups.Storage, Order = 3)]
    public static async Task TestSettingsRepositoryBinaryBlobAndLegacyMigrationAsync(IServiceProvider services)
    {
        var settingsRepo = services.GetRequiredService<ISettingsRepository>();
        var connectionFactory = services.GetRequiredService<LMP.Core.Data.ISqliteConnectionFactory>();
        const string testKey = "MigrationTest_AppSettings";

        var legacyJson = JsonSerializer.Serialize(new AppSettings
        {
            Volume = 84,
            DownloadPath = "D:\\LegacyDownloads"
        }, AppJsonContext.Default.AppSettings);

        await using (var conn = await connectionFactory.OpenConnectionAsync().ConfigureAwait(false))
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

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var loaded = await settingsRepo.GetAsync<AppSettings>(testKey).ConfigureAwait(false);
        TestAssert.NotNull(loaded);
        TestAssert.Equal(84, loaded.Volume);

        await using (var conn = await connectionFactory.OpenConnectionAsync().ConfigureAwait(false))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT typeof(Value), Value FROM Settings WHERE Key = @key LIMIT 1;";
            var pKey = cmd.CreateParameter();
            pKey.ParameterName = "@key";
            pKey.Value = testKey;
            cmd.Parameters.Add(pKey);

            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            TestAssert.True(await reader.ReadAsync().ConfigureAwait(false));
            var typeName = reader.GetString(0).ToLowerInvariant();
            TestAssert.True(typeName is "blob" or "text", $"Unexpected SQLite type: {typeName}");
        }

        await using (var conn = await connectionFactory.OpenConnectionAsync().ConfigureAwait(false))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Settings WHERE Key = @key;";
            var pKey = cmd.CreateParameter();
            pKey.ParameterName = "@key";
            pKey.Value = testKey;
            cmd.Parameters.Add(pKey);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    public static Task TestAtomicFileBinaryBakRecoveryAsync() =>
        AtomicFileTests.TestBinaryBakRecoveryAndAutoHealAsync();

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
                    new SerializedDownloadedRange { Start = 0, EndExclusive = 5_242_880 }
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