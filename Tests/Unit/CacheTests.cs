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