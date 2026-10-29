using LMP.Core.Audio.Http;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Tests.Framework;
using Microsoft.Extensions.DependencyInjection;

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
        await writeCache.LoadAsync(TestVersion).ConfigureAwait(false);
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

    [TestMethod(TestCategory.Unit, "AudioCache LRU Eviction & Limit Enforcement", Group = TestGroups.Storage, Order = 5)]
    public static async Task TestAudioCacheLruEvictionAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "LMP_AudioCacheLruTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Создаем кэш-менеджер с лимитом 3 MB
            using var manager = new Core.Audio.Cache.AudioCacheManager(tempDir, maxCacheSizeMb: 3);

            // 1. Создаем 3 полных трека по 1 MB каждый
            const int trackSize = 1024 * 1024;
            byte[] dummyData = new byte[trackSize];
            Array.Fill(dummyData, (byte)0x42);

            string key1 = AudioSourceFactory.BuildCacheKey("track1", AudioFormat.WebM, 160);
            string key2 = AudioSourceFactory.BuildCacheKey("track2", AudioFormat.WebM, 160);
            string key3 = AudioSourceFactory.BuildCacheKey("track3", AudioFormat.WebM, 160);

            var entry1 = manager.CreateOrUpdate(key1, "track1", "https://url1", trackSize, AudioFormat.WebM, Core.Audio.Interfaces.AudioCodec.Opus, 160);
            entry1.LastAccessedAt = DateTime.UtcNow.AddMinutes(-30);
            await manager.WriteRangeAsync(key1, 0, dummyData).ConfigureAwait(false);

            var entry2 = manager.CreateOrUpdate(key2, "track2", "https://url2", trackSize, AudioFormat.WebM, Core.Audio.Interfaces.AudioCodec.Opus, 160);
            entry2.LastAccessedAt = DateTime.UtcNow.AddMinutes(-20);
            await manager.WriteRangeAsync(key2, 0, dummyData).ConfigureAwait(false);

            var entry3 = manager.CreateOrUpdate(key3, "track3", "https://url3", trackSize, AudioFormat.WebM, Core.Audio.Interfaces.AudioCodec.Opus, 160);
            entry3.LastAccessedAt = DateTime.UtcNow.AddMinutes(-10);
            await manager.WriteRangeAsync(key3, 0, dummyData).ConfigureAwait(false);

            TestAssert.True(manager.IsFullyCached(key1), "Key1 should be fully cached");
            TestAssert.True(manager.IsFullyCached(key2), "Key2 should be fully cached");
            TestAssert.True(manager.IsFullyCached(key3), "Key3 should be fully cached");

            // 2. Добавляем 4-й трек (1 MB) -> суммарно 4 MB при лимите 3 MB
            string key4 = AudioSourceFactory.BuildCacheKey("track4", AudioFormat.WebM, 160);
            var entry4 = manager.CreateOrUpdate(key4, "track4", "https://url4", trackSize, AudioFormat.WebM, Core.Audio.Interfaces.AudioCodec.Opus, 160);
            entry4.LastAccessedAt = DateTime.UtcNow;
            await manager.WriteRangeAsync(key4, 0, dummyData).ConfigureAwait(false);

            // WriteRangeAsync триггерит EnforceLimitsAsync
            // Самый старый по LastAccessedAt — key1 (30 минут назад). Он должен быть вытеснен!
            TestAssert.False(manager.IsFullyCached(key1), "Key1 (oldest LRU) must be evicted");
            TestAssert.False(File.Exists(manager.GetCachePath(key1)), "Key1 file must be deleted from disk");

            TestAssert.True(manager.IsFullyCached(key2), "Key2 must be preserved");
            TestAssert.True(manager.IsFullyCached(key3), "Key3 must be preserved");
            TestAssert.True(manager.IsFullyCached(key4), "Key4 must be preserved");

            // 3. Проверка защиты Lease от вытеснения:
            // Блокируем key2 через AcquireLease.
            manager.AcquireLease(key2);
            try
            {
                // Снижаем лимит до 1 MB
                manager.UpdateMaxCacheSizeMb(1);
                // Ожидаем завершения очистки
                await manager.EnforceLimitsAsync().ConfigureAwait(false);

                // key2 удерживается Lease -> не должен быть удален!
                TestAssert.True(manager.IsFullyCached(key2), "Key2 with active lease must NOT be evicted");
                TestAssert.True(File.Exists(manager.GetCachePath(key2)), "Key2 file must remain on disk while leased");
            }
            finally
            {
                manager.ReleaseLease(key2);
            }
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [TestMethod(TestCategory.Unit, "Storage Settings SQLite Roundtrip & Validation", Group = TestGroups.Storage, Order = 6)]
    public static async Task TestStorageSettingsPersistenceAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "LMP_StorageSettingsTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string tempDbPath = Path.Combine(tempDir, "test_settings.db");

        try
        {
            var factory = new Core.Data.LowMemorySqliteConnectionFactory(tempDbPath);
            await using (var conn = await factory.OpenConnectionAsync().ConfigureAwait(false))
            {
                await Core.Data.DatabaseExtensions.EnsureTablesCreatedAsync(conn, CancellationToken.None).ConfigureAwait(false);
            }

            var settingsRepo = new Core.Data.Repositories.SettingsRepository(factory);
            const string testKey = "Test_StorageLimits_Settings";

            var settings = new Core.Models.AppSettings
            {
                Storage = new Core.Models.StorageSettings
                {
                    AudioCacheLimitMb = 3500,
                    ImageCacheLimitMb = 750,
                    DownloadedTracksLimitMb = 12000,
                    MaxBitmapCacheItems = 50,
                    AutoSaveToDownloads = true
                }
            };

            await settingsRepo.SetAsync(testKey, settings).ConfigureAwait(false);

            var loaded = await settingsRepo.GetAsync<Core.Models.AppSettings>(testKey).ConfigureAwait(false);
            TestAssert.NotNull(loaded, "Failed to load saved AppSettings from isolated repository");
            TestAssert.Equal(3500, loaded.Storage.AudioCacheLimitMb);
            TestAssert.Equal(750, loaded.Storage.ImageCacheLimitMb);
            TestAssert.Equal(12000, loaded.Storage.DownloadedTracksLimitMb);
            TestAssert.Equal(50, loaded.Storage.MaxBitmapCacheItems);
            TestAssert.True(loaded.Storage.AutoSaveToDownloads);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [TestMethod(TestCategory.Unit, "ImageCache Memory LRU & Disk Stats Verification", Group = TestGroups.Storage, Order = 7)]
    public static Task TestImageCacheLimitsAndStatsAsync(IServiceProvider services)
    {
        var imageCache = services.GetRequiredService<Core.Services.ImageCacheService>();
        var library = services.GetRequiredService<Core.Services.LibraryService>();

        int origMaxItems = library.Settings.Storage.MaxBitmapCacheItems;
        try
        {
            var (memItems, _, diskFiles, diskMb) = imageCache.GetStats();
            TestAssert.True(memItems >= 0, "Memory items count must be >= 0");
            TestAssert.True(diskFiles >= 0, "Disk files count must be >= 0");
            TestAssert.True(diskMb >= 0, "Disk MB must be >= 0");

            library.Settings.Storage.MaxBitmapCacheItems = 1;
            imageCache.EnforceLimits();

            var (enforcedMem, _, _, _) = imageCache.GetStats();
            TestAssert.True(enforcedMem <= 1, "EnforceLimits must evict RAM bitmaps down to MaxBitmapCacheItems");

            imageCache.ClearMemoryCache();
            var (clearedMem, _, _, _) = imageCache.GetStats();
            TestAssert.Equal(0, clearedMem);
        }
        finally
        {
            library.Settings.Storage.MaxBitmapCacheItems = origMaxItems;
        }

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "DownloadService Storage Limit Rejection", Group = TestGroups.Storage, Order = 8)]
    public static Task TestDownloadServiceQuotaCheckAsync(IServiceProvider services)
    {
        var library = services.GetRequiredService<Core.Services.LibraryService>();
        var youtube = services.GetRequiredService<Core.Services.YoutubeProvider>();

        string tempDownloads = Path.Combine(Path.GetTempPath(), "LMP_DownloadQuotaTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDownloads);

        try
        {
            string dummyFile = Path.Combine(tempDownloads, "existing_track.mp3");
            byte[] dummyData = new byte[2 * 1024 * 1024];
            File.WriteAllBytes(dummyFile, dummyData);

            // Меняем только in-memory поля Settings.DownloadPath и DownloadedTracksLimitMb без вызова SaveSettingsImmediate()
            string origPath = library.Settings.DownloadPath;
            int origLimit = library.Settings.Storage.DownloadedTracksLimitMb;

            library.Settings.DownloadPath = tempDownloads;
            library.Settings.Storage.DownloadedTracksLimitMb = 1;

            try
            {
                var downloadService = new Core.Services.DownloadService(youtube, library);

                var track = new TrackInfo
                {
                    Id = "quota_test_track",
                    Title = "Test Quota Track"
                };

                bool completedCalled = false;
                bool completedSuccess = true;

                downloadService.OnCompleted += (id, success, _) =>
                {
                    if (id == track.Id)
                    {
                        completedCalled = true;
                        completedSuccess = success;
                    }
                };

                downloadService.StartDownload(track);

                TestAssert.True(completedCalled, "OnCompleted must be invoked when quota is exceeded");
                TestAssert.False(completedSuccess, "Download must fail when quota is exceeded");
                TestAssert.False(downloadService.IsDownloading(track.Id), "Track must not remain in active tasks");
            }
            finally
            {
                library.Settings.DownloadPath = origPath;
                library.Settings.Storage.DownloadedTracksLimitMb = origLimit;
            }
        }
        finally
        {
            try { if (Directory.Exists(tempDownloads)) Directory.Delete(tempDownloads, recursive: true); } catch { }
        }

        return Task.CompletedTask;
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