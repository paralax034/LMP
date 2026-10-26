using LMP.Core.Audio.Http;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Модульные тесты in-memory и файловых кэшей: DecryptorCache и CdnBlacklist.
/// Выполняются полностью оффлайн без обращения к сетевым интерфейсам.
/// </summary>
public static class CacheTests
{
    private const int MaxMemorySize = 100;
    private const int MaxDiskSize = 50;
    private const string TestVersion = "test_player_v1";

    [TestMethod(TestCategory.Unit, "DecryptorCache Memory Operations", Group = TestGroups.Storage, Order = 1)]
    public static Task TestMemoryCacheOperationsAsync()
    {
        using var tempFile = new TempFileCookie();
        var cache = new DecryptorCache(
            tempFile.Path,
            maxMemory: MaxMemorySize,
            maxDisk: MaxDiskSize);

        cache.Set("key1", "value1");
        TestAssert.True(cache.TryGet("key1", out var v1) && v1 == "value1", "Failed to retrieve key1");
        TestAssert.False(cache.TryGet("nonexistent", out _), "Retrieved nonexistent key");

        cache.Set("key1", "value2");
        TestAssert.True(cache.TryGet("key1", out var v2) && v2 == "value2", "Failed to overwrite key1");

        cache.Set("key2", "value2");
        cache.Set("key3", "value3");
        TestAssert.Equal(3, cache.Count);

        cache.Clear();
        TestAssert.Equal(0, cache.Count);

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "DecryptorCache Disk Persistence Roundtrip", Group = TestGroups.Storage, Order = 2)]
    public static async Task TestDiskRoundtripAsync()
    {
        using var tempFile = new TempFileCookie();

        var writeCache = new DecryptorCache(tempFile.Path, MaxMemorySize, MaxDiskSize);
        const int testEntriesCount = 10;

        for (int i = 0; i < testEntriesCount; i++)
            writeCache.Set($"key{i}", $"value{i}");

        await writeCache.SaveAsync().ConfigureAwait(false);

        var readCache = new DecryptorCache(tempFile.Path, MaxMemorySize, MaxDiskSize);
        await readCache.LoadAsync(TestVersion).ConfigureAwait(false);

        TestAssert.Equal(testEntriesCount, readCache.Count);
    }

    [TestMethod(TestCategory.Unit, "CdnBlacklist TTL Expiration & URL Filtering", Group = TestGroups.Storage, Order = 3)]
    public static async Task TestCdnBlacklistLogicAsync()
    {
        var blacklist = new CdnBlacklist(ttl: TimeSpan.FromMilliseconds(200));

        // 1. Добавление и проверка
        blacklist.MarkBlocked("rr1---sn-blocked.googlevideo.com");
        TestAssert.True(blacklist.IsBlocked("rr1---sn-blocked.googlevideo.com"), "Blocked host should be blocked");
        TestAssert.False(blacklist.IsBlocked("rr2---sn-clean.googlevideo.com"), "Clean host should not be blocked");

        // 2. Регистронезависимость
        TestAssert.True(blacklist.IsBlocked("RR1---SN-BLOCKED.GOOGLEVIDEO.COM"), "Host check must be case-insensitive");

        // 3. Фильтрация URL
        TestAssert.True(
            blacklist.IsBlockedUrl("https://rr1---sn-blocked.googlevideo.com/videoplayback?id=1"),
            "URL with blocked host should be blocked");
        TestAssert.False(
            blacklist.IsBlockedUrl("https://rr2---sn-clean.googlevideo.com/videoplayback?id=1"),
            "URL with clean host should not be blocked");
        TestAssert.False(blacklist.IsBlockedUrl(""), "Empty URL must not be blocked");

        // 4. Истечение TTL
        await Task.Delay(300).ConfigureAwait(false);
        TestAssert.False(blacklist.IsBlocked("rr1---sn-blocked.googlevideo.com"), "Host must expire after TTL");
        TestAssert.Equal(0, blacklist.Count);

        // 5. Ручная разблокировка и очистка
        blacklist.MarkBlocked("rr3---sn-temp.googlevideo.com");
        blacklist.Unblock("rr3---sn-temp.googlevideo.com");
        TestAssert.False(blacklist.IsBlocked("rr3---sn-temp.googlevideo.com"));

        blacklist.MarkBlocked("host-a.googlevideo.com");
        blacklist.MarkBlocked("host-b.googlevideo.com");
        TestAssert.Equal(2, blacklist.GetBlockedHosts().Count);

        blacklist.Clear();
        TestAssert.Equal(0, blacklist.Count);
    }

    [TestMethod(TestCategory.Unit, "AudioCache Sparse Seek Hole Guard & Orphan Verification", Group = TestGroups.Storage, Order = 4)]
    public static async Task TestSparseSeekFileNotPromotedToCompleteCacheAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "LMP_SparseCacheTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            const string trackId = "yt_eTYwNRybxQ8";
            string cacheKey = AudioSourceFactory.BuildCacheKey(trackId, AudioFormat.Mp4, 127); // yt_eTYwNRybxQ8_Mp4_128
            const long totalSize = 2_139_242;

            using var manager = new Core.Audio.Cache.AudioCacheManager(tempDir, maxCacheSizeMb: 32);

            // 1. Имитируем точь-в-точь сессию из лога:
            // открыли MP4/127kbps, скачали шапку (0..48KB) и сделали Seek на 66-ю секунду (записав чанк 1474KB..1884KB).
            // Между 48KB и 1474KB на диске зияет огромная дыра из нулей, но физический размер файла на NTFS стал 1884KB!
            _ = manager.CreateOrUpdate(
                cacheKey,
                trackId,
                "https://rr2.googlevideo.com/videoplayback?itag=140",
                totalSize,
                AudioFormat.Mp4,
                Core.Audio.Interfaces.AudioCodec.Aac,
                bitrate: 127,
                durationMs: 132_121,
                alignmentBytes: 16384);

            byte[] nonZeroChunk = new byte[49_152];
            Array.Fill(nonZeroChunk, (byte)0x5A);

            await manager.WriteRangeAsync(cacheKey, offset: 0, nonZeroChunk).ConfigureAwait(false);
            await manager.WriteRangeAsync(cacheKey, offset: 1_835_008, nonZeroChunk).ConfigureAwait(false);

            string filePath = manager.GetCachePath(cacheKey);
            TestAssert.True(File.Exists(filePath), "Sparse cache file must exist on disk");
            TestAssert.Equal(1_884_160L, new FileInfo(filePath).Length, "NTFS file length must equal highest written offset (1884KB)");
            TestAssert.False(manager.IsFullyCached(cacheKey), "Partial sparse entry must not report IsFullyCached");

            // 2. Вызываем HydrateCacheStatus / RecoverOrphansForTracks:
            // partial-запись в индексе НЕ должна быть повышена до IsComplete из-за того, что fi.Length == 1884KB!
            var track = new TrackInfo
            {
                Id = trackId,
                Title = "les - silence",
                Duration = TimeSpan.FromMilliseconds(132_121)
            };
            manager.HydrateCacheStatus([track]);

            TestAssert.False(
                manager.IsFullyCached(cacheKey),
                "RecoverOrphansForTracks falsely promoted a partial sparse file (with zero holes) to IsComplete!");

            // 3. Имитируем ситуацию, когда процесс был убит до сохранения индекса (записи в индексе нет, а дырявый файл 1884KB остался на диске)
            using var freshManager = new Core.Audio.Cache.AudioCacheManager(tempDir, maxCacheSizeMb: 32);
            bool recovered = freshManager.RecoverOrphansForTracks(
                [trackId],
                new Dictionary<string, TimeSpan> { [trackId] = TimeSpan.FromMilliseconds(132_121) });

            TestAssert.False(recovered, "Orphan recovery must reject and delete sparse files containing zero holes");
            TestAssert.False(File.Exists(filePath), "Sparse orphan file with zero holes must be deleted from disk");
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private sealed class TempFileCookie : IDisposable
    {
        public string Path { get; } = System.IO.Path.GetTempFileName();

        public void Dispose()
        {
            try
            {
                if (File.Exists(Path))
                    File.Delete(Path);
            }
            catch { }
        }
    }
}