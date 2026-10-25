using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Sources;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Модульные тесты парсинга времени жизни URL (expire) и логики превентивного обновления потоков.
/// Все тесты являются автономными, детерминированными и не требуют интернета.
/// </summary>
public static class StreamFreshnessTests
{
    [TestMethod(TestCategory.Unit, "UrlEx Expire Parameter & TTL Math", Group = TestGroups.Network, Order = 1)]
    public static Task TestUrlExExpireParsingAsync()
    {
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 1. Уже истекший URL
        var expiredUrl = $"https://rr1.googlevideo.com/videoplayback?expire={nowUnix - 120}&n=token123&itag=251";
        TestAssert.True(UrlEx.TryGetExpireUtc(expiredUrl, out var exp1));
        TestAssert.True(exp1 < DateTime.UtcNow);
        TestAssert.True(UrlEx.IsUrlExpiredOrExpiringSoon(expiredUrl, TimeSpan.FromMinutes(5)));

        // 2. Истекает через 2 минуты (порог 5 минут -> expiring soon)
        var expiringSoonUrl = $"https://rr1.googlevideo.com/videoplayback?expire={nowUnix + 120}&n=token123&itag=251";
        TestAssert.True(UrlEx.IsUrlExpiredOrExpiringSoon(expiringSoonUrl, TimeSpan.FromMinutes(5)));
        TestAssert.False(UrlEx.IsUrlExpiredOrExpiringSoon(expiringSoonUrl, TimeSpan.FromMinutes(1)));

        // 3. Свежий URL на 6 часов вперед
        var freshUrl = $"https://rr1.googlevideo.com/videoplayback?expire={nowUnix + 21600}&n=token123&itag=251";
        TestAssert.True(UrlEx.TryGetExpireUtc(freshUrl, out var exp3));
        TestAssert.True(exp3 > DateTime.UtcNow.AddHours(5));
        TestAssert.False(UrlEx.IsUrlExpiredOrExpiringSoon(freshUrl, TimeSpan.FromMinutes(5)));

        // 4. URL без параметра expire
        var noExpireUrl = "https://example.com/audio/track.opus";
        TestAssert.False(UrlEx.TryGetExpireUtc(noExpireUrl, out _));
        TestAssert.False(UrlEx.IsUrlExpiredOrExpiringSoon(noExpireUrl));

        // 5. Пустая строка
        TestAssert.True(UrlEx.IsUrlExpiredOrExpiringSoon(""));

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Stream Refresh with Unchanged N-Token", Group = TestGroups.Network, Order = 2)]
    public static async Task TestRefreshWithUnchangedNTokenAndValidExpireAsync()
    {
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const string sharedNToken = "SameStaticNToken123";

        var oldUrl = $"https://rr1.googlevideo.com/videoplayback?expire={nowUnix - 3600}&n={sharedNToken}&sig=SIG1&itag=251";
        var newFreshUrl = $"https://rr1.googlevideo.com/videoplayback?expire={nowUnix + 21600}&n={sharedNToken}&sig=SIG2&itag=251";

        int refreshCallCount = 0;
        Task<string?> MockRefresher(CancellationToken _)
        {
            Interlocked.Increment(ref refreshCallCount);
            return Task.FromResult<string?>(newFreshUrl);
        }

        using var tempCache = new TempCacheManager();
        var source = new CachingStreamSource(
            cacheKey: "test_freshness_track",
            trackId: "yt_test_track",
            url: oldUrl,
            contentLength: 10_000_000,
            format: AudioFormat.WebM,
            codec: AudioCodec.Opus,
            bitrate: 160,
            cacheManager: tempCache.Manager,
            config: new StreamingConfig(),
            urlRefresher: MockRefresher);

        await source.EnsureStreamFreshnessAsync(CancellationToken.None).ConfigureAwait(false);

        TestAssert.Equal(1, refreshCallCount);
        TestAssert.False(UrlEx.IsUrlExpiredOrExpiringSoon(source.CacheKey));
    }

    [TestMethod(TestCategory.Unit, "Proactive URL Refresh on Resume", Group = TestGroups.Network, Order = 3)]
    public static async Task TestProactiveStreamFreshnessOnResumeAsync()
    {
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expiringUrl = $"https://rr1.googlevideo.com/videoplayback?expire={nowUnix + 120}&n=tok&itag=251";
        var freshUrl = $"https://rr1.googlevideo.com/videoplayback?expire={nowUnix + 21600}&n=tok&itag=251";

        int refreshCalls = 0;
        using var tempCache = new TempCacheManager();

        var source = new CachingStreamSource(
            cacheKey: "test_proactive_track",
            trackId: "yt_proactive_track",
            url: expiringUrl,
            contentLength: 5_000_000,
            format: AudioFormat.WebM,
            codec: AudioCodec.Opus,
            bitrate: 160,
            cacheManager: tempCache.Manager,
            config: new StreamingConfig(),
            urlRefresher: _ =>
            {
                Interlocked.Increment(ref refreshCalls);
                return Task.FromResult<string?>(freshUrl);
            });

        await source.EnsureStreamFreshnessAsync(CancellationToken.None).ConfigureAwait(false);

        TestAssert.Equal(1, refreshCalls);
    }

    private sealed class TempCacheManager : IDisposable
    {
        private readonly string _tempDir;
        public AudioCacheManager Manager { get; }

        public TempCacheManager()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"lmp_cache_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
            Manager = new AudioCacheManager(_tempDir, maxCacheSizeMb: 16);
        }

        public void Dispose()
        {
            try
            {
                Manager.Dispose();
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, recursive: true);
            }
            catch { }
        }
    }
}