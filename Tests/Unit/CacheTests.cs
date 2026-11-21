using LMP.Core.Audio.Http;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Unit tests for in-memory and file-based caches: DecryptorCache and CdnBlacklist.
/// Executed completely offline without accessing network interfaces.
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

        // 1. Addition and verification
        blacklist.MarkBlocked("rr1---sn-blocked.googlevideo.com");
        TestAssert.True(blacklist.IsBlocked("rr1---sn-blocked.googlevideo.com"), "Blocked host should be blocked");
        TestAssert.False(blacklist.IsBlocked("rr2---sn-clean.googlevideo.com"), "Clean host should not be blocked");

        // 2. Case insensitivity
        TestAssert.True(blacklist.IsBlocked("RR1---SN-BLOCKED.GOOGLEVIDEO.COM"), "Host check must be case-insensitive");

        // 3. URL filtering
        TestAssert.True(
            blacklist.IsBlockedUrl("https://rr1---sn-blocked.googlevideo.com/videoplayback?id=1"),
            "URL with blocked host should be blocked");
        TestAssert.False(
            blacklist.IsBlockedUrl("https://rr2---sn-clean.googlevideo.com/videoplayback?id=1"),
            "URL with clean host should not be blocked");
        TestAssert.False(blacklist.IsBlockedUrl(""), "Empty URL must not be blocked");

        // 4. TTL expiration
        await Task.Delay(300).ConfigureAwait(false);
        TestAssert.False(blacklist.IsBlocked("rr1---sn-blocked.googlevideo.com"), "Host must expire after TTL");
        TestAssert.Equal(0, blacklist.Count);

        // 5. Manual unblock and cleanup
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

            // 1. Replicate exact session from log:
            // opened MP4/127kbps, downloaded header (0..48KB), and executed Seek to 66th second (writing chunk 1474KB..1884KB).
            // Between 48KB and 1474KB on disk there is a huge zero-byte hole, but physical file size on NTFS became 1884KB!
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

            // 2. Call HydrateCacheStatus / RecoverOrphansForTracks:
            // partial entry in index MUST NOT be promoted to IsComplete just because fi.Length == 1884KB!
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

            // 3. Simulate situation where process was killed before index save (no index entry, but sparse 1884KB file remained on disk)
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
            // Create cache manager with 3 MB limit
            using var manager = new Core.Audio.Cache.AudioCacheManager(tempDir, maxCacheSizeMb: 3);

            // 1. Create 3 complete tracks of 1 MB each
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

            // 2. Add 4th track (1 MB) -> total 4 MB with 3 MB limit
            string key4 = AudioSourceFactory.BuildCacheKey("track4", AudioFormat.WebM, 160);
            var entry4 = manager.CreateOrUpdate(key4, "track4", "https://url4", trackSize, AudioFormat.WebM, Core.Audio.Interfaces.AudioCodec.Opus, 160);
            entry4.LastAccessedAt = DateTime.UtcNow;
            await manager.WriteRangeAsync(key4, 0, dummyData).ConfigureAwait(false);

            // WriteRangeAsync triggers EnforceLimitsAsync
            // Oldest by LastAccessedAt is key1 (30 minutes ago). It should be evicted!
            TestAssert.False(manager.IsFullyCached(key1), "Key1 (oldest LRU) must be evicted");
            TestAssert.False(File.Exists(manager.GetCachePath(key1)), "Key1 file must be deleted from disk");

            TestAssert.True(manager.IsFullyCached(key2), "Key2 must be preserved");
            TestAssert.True(manager.IsFullyCached(key3), "Key3 must be preserved");
            TestAssert.True(manager.IsFullyCached(key4), "Key4 must be preserved");

            // 3. Lease protection test against eviction:
            // Lock key2 via AcquireLease.
            manager.AcquireLease(key2);
            try
            {
                // Lower limit to 1 MB
                manager.UpdateMaxCacheSizeMb(1);
                // Wait for cleanup completion
                await manager.EnforceLimitsAsync().ConfigureAwait(false);

                // key2 is held by Lease -> must not be deleted!
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

            var settings = new AppSettings
            {
                Storage = new StorageSettings
                {
                    AudioCacheLimitMb = 3500,
                    ImageCacheLimitMb = 750,
                    DownloadedTracksLimitMb = 12000,
                    MaxBitmapCacheItems = 50,
                    AutoSaveToDownloads = true
                }
            };

            await settingsRepo.SetAsync(testKey, settings).ConfigureAwait(false);

            var loaded = await settingsRepo.GetAsync<AppSettings>(testKey).ConfigureAwait(false);
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

    [TestMethod(TestCategory.Unit, "AudioCache Concurrent Write/Remove Race & GetCachePath Determinism", Group = TestGroups.Storage, Order = 9)]
    public static async Task TestAudioCacheConcurrentWriteRemoveAndPathAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "LMP_CacheRaceTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            await using var manager = new Core.Audio.Cache.AudioCacheManager(tempDir, maxCacheSizeMb: 16);

            const string key = "yt_race123_WebM_160";
            string pathA = manager.GetCachePath(key);
            string pathB = manager.GetCachePath(key);
            TestAssert.Equal(pathA, pathB, "GetCachePath must be deterministic");
            TestAssert.True(pathA.EndsWith(".audio", StringComparison.Ordinal));

            byte[] chunk = new byte[8_192];
            Array.Fill(chunk, (byte)0x3C);

            for (int i = 0; i < 25; i++)
            {
                string iterKey = $"yt_race_{i}_WebM_160";
                string trackId = $"yt_race_{i}";
                _ = manager.CreateOrUpdate(
                    iterKey, trackId, "https://example.com/a.webm", 65_536,
                    AudioFormat.WebM, Core.Audio.Interfaces.AudioCodec.Opus, 160);

                var writeTask = manager.WriteRangeAsync(iterKey, 0, chunk).AsTask();
                manager.RemoveTrackCache(trackId);
                await writeTask.ConfigureAwait(false);
            }

            TestAssert.Equal(0, Directory.GetFiles(tempDir, "*.audio").Length, "Removed cache entries must delete their .audio file once in-flight WriteRangeAsync completes");
            await manager.ClearAllAsync().ConfigureAwait(false);
            TestAssert.Equal(0, Directory.GetFiles(tempDir, "*.audio").Length, "No orphan .audio files must remain after ClearAllAsync");
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [TestMethod(TestCategory.Unit, "AudioCache CreateOrUpdate & ResetPartialEntry Preserve Leases On Size Mismatch", Group = TestGroups.Storage, Order = 10)]
    public static async Task TestCreateOrUpdateResetsPartialCacheOnSizeMismatchAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "LMP_CacheSizeChangeTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            using var manager = new Core.Audio.Cache.AudioCacheManager(tempDir, maxCacheSizeMb: 16);
            const string trackId = "yt_DiDUIfMk7go";
            string key = AudioSourceFactory.BuildCacheKey(trackId, AudioFormat.WebM, 142);

            _ = manager.CreateOrUpdate(
                key, trackId, "https://rr1.googlevideo.com/v1", 6_511_915,
                AudioFormat.WebM, Core.Audio.Interfaces.AudioCodec.Opus, 142, alignmentBytes: 16_384);

            byte[] prefix = new byte[32_768];
            Array.Fill(prefix, (byte)0xAB);
            await manager.WriteRangeAsync(key, 0, prefix).ConfigureAwait(false);
            TestAssert.Equal(32_768L, manager.GetCacheInfo(key)!.DownloadedBytes);

            // 1. ContentLength grew (+3 bytes) while lease is active (real CachingStreamSource.InitializeAsync order)
            manager.AcquireLease(key);
            try
            {
                var grown = manager.CreateOrUpdate(
                    key, trackId, "https://rr2.googlevideo.com/v2", 6_511_918,
                    AudioFormat.WebM, Core.Audio.Interfaces.AudioCodec.Opus, 142, alignmentBytes: 16_384);
                TestAssert.Equal(6_511_918L, grown.TotalSize);
                TestAssert.Equal(0L, grown.DownloadedBytes);
                TestAssert.Equal(0L, new FileInfo(manager.GetCachePath(key)).Length);

                // 2. Write new prefix and test ContentLength shrink (-3 bytes, yt_vy63u2hKoPE scenario)
                await manager.WriteRangeAsync(key, 0, prefix).ConfigureAwait(false);
                var shrunk = manager.CreateOrUpdate(
                    key, trackId, "https://rr3.googlevideo.com/v3", 6_511_915,
                    AudioFormat.WebM, Core.Audio.Interfaces.AudioCodec.Opus, 142, alignmentBytes: 16_384);
                TestAssert.Equal(6_511_915L, shrunk.TotalSize);
                TestAssert.Equal(0L, shrunk.DownloadedBytes);
                TestAssert.Equal(0L, new FileInfo(manager.GetCachePath(key)).Length);

                // 3. ResetPartialEntry while old lease #1 is held, then new session acquires lease #2 before #1 releases:
                //    releasing lease #1 must NOT steal lease #2 or close the file handle under session #2.
                await manager.WriteRangeAsync(key, 0, prefix).ConfigureAwait(false);
                TestAssert.True(manager.ResetPartialEntry(key, 6_511_918, alignmentBytes: 16_384));
                TestAssert.Equal(0L, manager.GetCacheInfo(key)!.DownloadedBytes);
                TestAssert.Equal(6_511_918L, manager.GetCacheInfo(key)!.TotalSize);
                TestAssert.Equal(0L, new FileInfo(manager.GetCachePath(key)).Length);

                manager.AcquireLease(key); // session #2
            }
            finally
            {
                manager.ReleaseLease(key); // session #1 disposes
            }

            try
            {
                await manager.WriteRangeAsync(key, 0, prefix).ConfigureAwait(false);
                byte[] readBack = new byte[4096];
                int read = await manager.ReadRangeIntoAsync(key, 0, readBack).ConfigureAwait(false);
                TestAssert.Equal(4096, read, "Session #2 handle must remain valid after session #1 releases its lease");
                TestAssert.Equal((byte)0xAB, readBack[0]);
            }
            finally
            {
                manager.ReleaseLease(key); // session #2 disposes
            }
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [TestMethod(TestCategory.Unit, "LocalFileSource Surgical Invalidation Isolation (CacheKey vs Downloads)", Group = TestGroups.Storage, Order = 12)]
    public static async Task TestLocalFileSourceSurgicalInvalidationIsolationAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "LMP_LocalSourceIsolation_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            using var cacheManager = new Core.Audio.Cache.AudioCacheManager(tempDir, maxCacheSizeMb: 16);
            const string trackId = "yt_isolation_test";
            string webmKey = AudioSourceFactory.BuildCacheKey(trackId, AudioFormat.WebM, 142);
            string mp4Key = AudioSourceFactory.BuildCacheKey(trackId, AudioFormat.Mp4, 128);

            byte[] validWebm = WebMParserTests.BuildSyntheticWebM(out _, out long cluster2Pos);
            byte[] corruptWebm = (byte[])validWebm.Clone();
            corruptWebm[cluster2Pos] = 0x08; // invalid EBML ID VINT length = 5

            _ = cacheManager.CreateOrUpdate(webmKey, trackId, "https://example.com/w", corruptWebm.Length,
                AudioFormat.WebM, Core.Audio.Interfaces.AudioCodec.Opus, 142, alignmentBytes: 16_384);
            await cacheManager.WriteRangeAsync(webmKey, 0, corruptWebm).ConfigureAwait(false);

            byte[] validMp4Stub = new byte[4096];
            Array.Fill(validMp4Stub, (byte)0x55);
            _ = cacheManager.CreateOrUpdate(mp4Key, trackId, "https://example.com/m", validMp4Stub.Length,
                AudioFormat.Mp4, Core.Audio.Interfaces.AudioCodec.Aac, 128, alignmentBytes: 16_384);
            await cacheManager.WriteRangeAsync(mp4Key, 0, validMp4Stub).ConfigureAwait(false);

            // 1. User download (cacheManager: null): must NOT throw CacheInvalidatedException or touch AudioCache
            string downloadPath = Path.Combine(tempDir, "download_corrupt.webm");
            await File.WriteAllBytesAsync(downloadPath, corruptWebm).ConfigureAwait(false);

            using (var dlSource = new Core.Audio.Sources.LocalFileSource(
                downloadPath, corruptWebm.Length, trackId, cacheManager: null, cacheKey: null))
            {
                TestAssert.False(dlSource.IsCacheBacked, "Download LocalFileSource must report IsCacheBacked == false");
                TestAssert.True(await dlSource.InitializeAsync().ConfigureAwait(false));
                _ = await dlSource.ReadFrameAsync().ConfigureAwait(false); // frame 1

                bool threwCacheInvalidated = false;
                try
                {
                    _ = await dlSource.ReadFrameAsync().ConfigureAwait(false);
                }
                catch (Core.Exceptions.CacheInvalidatedException)
                {
                    threwCacheInvalidated = true;
                }
                catch (InvalidDataException)
                {
                    // Expected when unrecoverable in non-cache local file
                }

                TestAssert.False(threwCacheInvalidated, "Non-cache LocalFileSource must never throw CacheInvalidatedException");
                TestAssert.True(cacheManager.IsFullyCached(webmKey), "Non-cache LocalFileSource must not invalidate WebM AudioCache");
                TestAssert.True(cacheManager.IsFullyCached(mp4Key), "Non-cache LocalFileSource must not invalidate MP4 AudioCache");
            }

            // 2. Cache-backed LocalFileSource: invalidates ONLY its own _cacheKey, leaving sibling Mp4_128 intact
            using (var cacheSource = new Core.Audio.Sources.LocalFileSource(
                cacheManager.GetCachePath(webmKey), corruptWebm.Length, trackId, cacheManager, webmKey))
            {
                TestAssert.True(cacheSource.IsCacheBacked, "Cache LocalFileSource must report IsCacheBacked == true");
                TestAssert.True(await cacheSource.InitializeAsync().ConfigureAwait(false));
                _ = await cacheSource.ReadFrameAsync().ConfigureAwait(false);

                bool threwCacheInvalidated = false;
                try
                {
                    _ = await cacheSource.ReadFrameAsync().ConfigureAwait(false);
                }
                catch (Core.Exceptions.CacheInvalidatedException)
                {
                    threwCacheInvalidated = true;
                }

                TestAssert.True(threwCacheInvalidated, "Cache-backed LocalFileSource must throw CacheInvalidatedException");
                TestAssert.False(cacheManager.IsFullyCached(webmKey), "Target WebM cache entry must be surgically invalidated");
                TestAssert.True(cacheManager.IsFullyCached(mp4Key), "Sibling MP4 cache entry must NOT be invalidated");
            }
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