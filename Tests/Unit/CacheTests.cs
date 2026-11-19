using LMP.Core.Audio.Http;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Tests.Framework;
using Microsoft.Extensions.DependencyInjection;

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

    // Destructive for running app: ClearMemoryCache() resets covers of active ImageCacheService
    [TestMethod(TestCategory.Action, "ImageCache Memory LRU & Disk Stats Verification", Group = TestGroups.Storage, Order = 7)]
    public static Task TestImageCacheLimitsAndStatsAsync(IServiceProvider services)
    {
        var imageCache = services.GetRequiredService<ImageCacheService>();
        var library = services.GetRequiredService<LibraryService>();

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

    // Destructive for running app: temporarily overrides DownloadPath in shared settings during test
    [TestMethod(TestCategory.Action, "DownloadService Storage Limit Rejection", Group = TestGroups.Storage, Order = 8)]
    public static Task TestDownloadServiceQuotaCheckAsync(IServiceProvider services)
    {
        var library = services.GetRequiredService<LibraryService>();
        var youtube = services.GetRequiredService<YoutubeProvider>();

        string tempDownloads = Path.Combine(Path.GetTempPath(), "LMP_DownloadQuotaTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDownloads);

        try
        {
            string dummyFile = Path.Combine(tempDownloads, "existing_track.mp3");
            byte[] dummyData = new byte[2 * 1024 * 1024];
            File.WriteAllBytes(dummyFile, dummyData);

            // Modify only in-memory fields Settings.DownloadPath and DownloadedTracksLimitMb without calling SaveSettingsImmediate()
            string origPath = library.Settings.DownloadPath;
            int origLimit = library.Settings.Storage.DownloadedTracksLimitMb;

            library.Settings.DownloadPath = tempDownloads;
            library.Settings.Storage.DownloadedTracksLimitMb = 1;

            try
            {
                var downloadService = new DownloadService(youtube, library);

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

    [TestMethod(TestCategory.Unit, "WebMParser Rejects Misaligned Seam, Corrupt Seek, Zero Hole & Sync Invalid VINT", Group = TestGroups.Audio, Order = 11)]
    public static async Task TestWebMParserCorruptionDetectionAsync()
    {
        // Build a valid WebM stream with 2 clusters and CuePoints
        byte[] validWebm = BuildSyntheticWebM(out _, out long cluster2AbsolutePos);

        // 0. Sanity check: unmodified synthetic WebM parses both clusters and seeks cleanly
        using (var ms = new MemoryStream(validWebm))
        using (var containerParser = new Core.Audio.Parsers.WebMContainerParser(ms))
        {
            TestAssert.True(await containerParser.ParseHeadersAsync().ConfigureAwait(false), "Valid WebM headers must parse");
            TestAssert.Equal(1000L, containerParser.DurationMs);
            TestAssert.Equal(48000, containerParser.SampleRate);
            TestAssert.Equal(2, containerParser.Channels);

            var f1 = await containerParser.ReadNextFrameAsync().ConfigureAwait(false);
            var f2 = await containerParser.ReadNextFrameAsync().ConfigureAwait(false);
            var eof = await containerParser.ReadNextFrameAsync().ConfigureAwait(false);
            TestAssert.True(f1.HasValue && f1.Value.TimestampMs == 0, "Cluster 1 frame must decode at 0ms");
            TestAssert.True(f2.HasValue && f2.Value.TimestampMs == 200, "Cluster 2 frame must decode at 200ms");
            TestAssert.False(eof.HasValue, "Clean WebM must reach EOF after cluster 2");

            var seekPos = containerParser.FindSeekPosition(200);
            TestAssert.True(seekPos.HasValue, "CuePoint #2 must resolve");
            ms.Position = seekPos!.Value.BytePosition;
            containerParser.Reset();
            var seekedFrame = await containerParser.ReadNextFrameAsync().ConfigureAwait(false);
            TestAssert.True(seekedFrame.HasValue && seekedFrame.Value.TimestampMs == 200, "Seek to CuePoint #2 must decode cluster 2");
        }

        // 1. Shift cluster 2 by +3 bytes (hybrid file seam after ContentLength mismatch):
        //    insert [0x92, 0x4F, 0xFF] before cluster 2 => 1-byte unknown EBML ID 0x92 + 2-byte size 0x0FFF (4095 > stream.Length).
        byte[] shiftedWebm = new byte[validWebm.Length + 3];
        Buffer.BlockCopy(validWebm, 0, shiftedWebm, 0, (int)cluster2AbsolutePos);
        shiftedWebm[cluster2AbsolutePos] = 0x92;
        shiftedWebm[cluster2AbsolutePos + 1] = 0x4F;
        shiftedWebm[cluster2AbsolutePos + 2] = 0xFF;
        Buffer.BlockCopy(validWebm, (int)cluster2AbsolutePos, shiftedWebm, (int)cluster2AbsolutePos + 3, validWebm.Length - (int)cluster2AbsolutePos);

        // 1a. Sequential read across the seam must throw ParserCorruptionException (NOT return null / silent EOF!)
        using (var ms = new MemoryStream(shiftedWebm))
        using (var containerParser = new Core.Audio.Parsers.WebMContainerParser(ms))
        {
            TestAssert.True(await containerParser.ParseHeadersAsync().ConfigureAwait(false));
            var firstFrame = await containerParser.ReadNextFrameAsync().ConfigureAwait(false);
            TestAssert.True(firstFrame.HasValue, "First cluster frame must decode");

            bool threwCorruption = false;
            try
            {
                _ = await containerParser.ReadNextFrameAsync().ConfigureAwait(false);
            }
            catch (Core.Exceptions.ParserCorruptionException)
            {
                threwCorruption = true;
            }
            TestAssert.True(threwCorruption, "Oversized unknown EBML skip past EOF at seam must throw ParserCorruptionException instead of silent EOF");
        }

        // 1b. Seek to CuePoint #2 on shifted file must throw ParserCorruptionException (missing Cluster header at Cue offset)
        using (var ms = new MemoryStream(shiftedWebm))
        using (var containerParser = new Core.Audio.Parsers.WebMContainerParser(ms))
        {
            TestAssert.True(await containerParser.ParseHeadersAsync().ConfigureAwait(false));
            var seekPos = containerParser.FindSeekPosition(200);
            TestAssert.True(seekPos.HasValue, "CuePoint seek position must resolve");

            ms.Position = seekPos!.Value.BytePosition;
            containerParser.Reset();

            bool threwCorruption = false;
            try
            {
                _ = await containerParser.ReadNextFrameAsync().ConfigureAwait(false);
            }
            catch (Core.Exceptions.ParserCorruptionException)
            {
                threwCorruption = true;
            }
            TestAssert.True(threwCorruption, "Seek landing on non-Cluster bytes must throw ParserCorruptionException instead of silent EOF");
        }

        // 2. Sync fast-path invalid EBML ID VINT length (0x08 -> idLength=5 inside already-buffered memory)
        //    must wrap InvalidDataException into ParserCorruptionException for SelfHealing!
        byte[] syncCorruptWebm = (byte[])validWebm.Clone();
        syncCorruptWebm[cluster2AbsolutePos] = 0x08;
        using (var ms = new MemoryStream(syncCorruptWebm))
        using (var parser = new Core.Audio.Helpers.WebMParser(ms))
        {
            TestAssert.True(await parser.ParseHeadersAsync().ConfigureAwait(false));
            var b1 = await parser.ReadNextBlockAsync().ConfigureAwait(false);
            TestAssert.True(b1.HasValue, "First WebM block must decode");
            b1!.Value.Owner.Dispose();

            bool threwCorruption = false;
            try
            {
                _ = await parser.ReadNextBlockAsync().ConfigureAwait(false);
            }
            catch (Core.Exceptions.ParserCorruptionException)
            {
                threwCorruption = true;
            }
            TestAssert.True(threwCorruption, "Sync fast-path Invalid EBML ID VINT length must throw ParserCorruptionException, not raw InvalidDataException");
        }

        // 3. Excessive zero-byte hole (>16 consecutive 0x00) must throw ParserCorruptionException rather than skipping to EOF
        byte[] zeroHoleWebm = new byte[validWebm.Length + 24];
        Buffer.BlockCopy(validWebm, 0, zeroHoleWebm, 0, (int)cluster2AbsolutePos);
        Buffer.BlockCopy(validWebm, (int)cluster2AbsolutePos, zeroHoleWebm, (int)cluster2AbsolutePos + 24, validWebm.Length - (int)cluster2AbsolutePos);
        using (var ms = new MemoryStream(zeroHoleWebm))
        using (var parser = new Core.Audio.Helpers.WebMParser(ms))
        {
            TestAssert.True(await parser.ParseHeadersAsync().ConfigureAwait(false));
            var b1 = await parser.ReadNextBlockAsync().ConfigureAwait(false);
            TestAssert.True(b1.HasValue);
            b1!.Value.Owner.Dispose();

            bool threwCorruption = false;
            try
            {
                _ = await parser.ReadNextBlockAsync().ConfigureAwait(false);
            }
            catch (Core.Exceptions.ParserCorruptionException)
            {
                threwCorruption = true;
            }
            TestAssert.True(threwCorruption, "Zero-filled hole (>16 zero bytes) must throw ParserCorruptionException");
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

            byte[] validWebm = BuildSyntheticWebM(out _, out long cluster2Pos);
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

    private static byte[] BuildSyntheticWebM(out long cluster2SegmentOffset, out long cluster2AbsolutePos)
    {
        using var ms = new MemoryStream();

        static void WriteBytes(Stream s, ReadOnlySpan<byte> bytes) => s.Write(bytes);
        static void WriteUInt16BE(Stream s, ushort v)
        {
            Span<byte> b = stackalloc byte[2];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(b, v);
            s.Write(b);
        }
        static void WriteSingleBE(Stream s, float v)
        {
            Span<byte> b = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b, v);
            s.Write(b);
        }

        // EBML (0x1A45DFA3), size 0 (0x80)
        WriteBytes(ms, [0x1A, 0x45, 0xDF, 0xA3, 0x80]);

        // Segment (0x18538067), unknown size (0xFF)
        WriteBytes(ms, [0x18, 0x53, 0x80, 0x67, 0xFF]);
        long segmentStart = ms.Position;

        // Info (0x1549A966): TimecodeScale (7B) + Duration (7B) => 14 bytes (0x8E)
        WriteBytes(ms, [0x15, 0x49, 0xA9, 0x66, 0x8E]);
        WriteBytes(ms, [0x2A, 0xD7, 0xB1, 0x83, 0x0F, 0x42, 0x40]);
        WriteBytes(ms, [0x44, 0x89, 0x84]);
        WriteSingleBE(ms, 1000.0f);

        // Tracks (0x1654AE6B) -> TrackEntry (0xAE):
        //   TrackNumber (0xD7, 0x81, 0x01) = 3B
        //   TrackType   (0x83, 0x81, 0x02) = 3B
        //   Audio (0xE1, 0x89): SamplingFreq (0xB5, 0x84, 48000f) = 6B + Channels (0x9F, 0x81, 0x02) = 3B => 11B
        // TrackEntry payload = 17B (0x91); Tracks payload = 19B (0x93)
        WriteBytes(ms, [0x16, 0x54, 0xAE, 0x6B, 0x93]);
        WriteBytes(ms, [0xAE, 0x91]);
        WriteBytes(ms, [0xD7, 0x81, 0x01, 0x83, 0x81, 0x02]);
        WriteBytes(ms, [0xE1, 0x89, 0xB5, 0x84]);
        WriteSingleBE(ms, 48000.0f);
        WriteBytes(ms, [0x9F, 0x81, 0x02]);

        // Cues (0x1C53BB6B): 2 CuePoints, each 12B -> payload 24B (0x98), total element 29B (5 + 24)
        // CuePoint (0xBB, 0x8A): CueTime (0xB3, 0x82, u16) = 4B + CueTrackPositions (0xB7, 0x84): CueClusterPosition (0xF1, 0x82, u16) = 6B => 10B
        long cuesHeaderPos = ms.Position;
        WriteBytes(ms, new byte[29]); // placeholder for Cues (5 + 24 bytes)

        long cluster1SegmentOffset = ms.Position - segmentStart;
        // Cluster 1 (0x1F43B675, unknown 0xFF): Timecode 0 (0xE7, 0x81, 0x00) + SimpleBlock (0xA3, 0x88, track=0x81, off=0, flags=0x80, 4B payload)
        WriteBytes(ms, [0x1F, 0x43, 0xB6, 0x75, 0xFF]);
        WriteBytes(ms, [0xE7, 0x81, 0x00]);
        WriteBytes(ms, [0xA3, 0x88, 0x81, 0x00, 0x00, 0x80, 0xAA, 0xBB, 0xCC, 0xDD]);

        cluster2AbsolutePos = ms.Position;
        cluster2SegmentOffset = ms.Position - segmentStart;
        // Cluster 2
        WriteBytes(ms, [0x1F, 0x43, 0xB6, 0x75, 0xFF]);
        WriteBytes(ms, [0xE7, 0x81, 0xC8]);
        WriteBytes(ms, [0xA3, 0x88, 0x81, 0x00, 0x00, 0x80, 0x11, 0x22, 0x33, 0x44]);

        long endPos = ms.Position;
        ms.Position = cuesHeaderPos;
        WriteBytes(ms, [0x1C, 0x53, 0xBB, 0x6B, 0x98]);
        // CuePoint 0 (0ms)
        WriteBytes(ms, [0xBB, 0x8A, 0xB3, 0x82]);
        WriteUInt16BE(ms, 0);
        WriteBytes(ms, [0xB7, 0x84, 0xF1, 0x82]);
        WriteUInt16BE(ms, (ushort)cluster1SegmentOffset);
        // CuePoint 1 (200ms)
        WriteBytes(ms, [0xBB, 0x8A, 0xB3, 0x82]);
        WriteUInt16BE(ms, 200);
        WriteBytes(ms, [0xB7, 0x84, 0xF1, 0x82]);
        WriteUInt16BE(ms, (ushort)cluster2SegmentOffset);
        ms.Position = endPos;

        return ms.ToArray();
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