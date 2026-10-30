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

    [TestMethod(TestCategory.Unit, "403 Refresh Outcomes: NoChange Network Deferral, StaleToken Breaker & SessionCache Purge", Group = TestGroups.Network, Order = 4)]
    public static async Task Test403RefreshOutcomesAndSessionCachePurgeAsync()
    {
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const string trackId = "yt_403_edge_test";
        var initialUrl = $"https://rr1---sn-test.googlevideo.com/videoplayback?expire={nowUnix + 21600}&n=initialNToken12&itag=251&clen=1000000";

        // 1. Проверяем, что 403 диагностика немедленно вычищает протухший манифест из SessionCacheStore
        var dummyStream = new Core.Youtube.Videos.Streams.AudioOnlyStreamInfo(
            251,
            initialUrl,
            new Core.Youtube.Videos.Streams.Container("webm"),
            new Core.Youtube.Videos.Streams.FileSize(1_000_000),
            new Core.Youtube.Videos.Streams.Bitrate(160_000),
            "opus",
            null,
            true,
            false);

        Core.Audio.Http.SessionCacheStore.RecordManifest(trackId, [dummyStream], -12.0f);
        TestAssert.NotNull(Core.Audio.Http.SessionCacheStore.GetManifest(trackId), "Manifest must be recorded in SessionCacheStore");

        using var tempCache = new TempCacheManager();
        int mode = 0; // 0 = NoChange (null), 1 = StaleToken (expired + same n), 2 = Fresh URL
        int refreshCount = 0;

        var config = new StreamingConfig
        {
            RefreshCooldownMs = 10,
            PostRefreshDelayMs = 5
        };

        using var source = new CachingStreamSource(
            cacheKey: "yt_403_edge_test_WebM_160",
            trackId: trackId,
            url: initialUrl,
            contentLength: 1_000_000,
            format: AudioFormat.WebM,
            codec: AudioCodec.Opus,
            bitrate: 160,
            cacheManager: tempCache.Manager,
            config: config,
            urlRefresher: _ =>
            {
                Interlocked.Increment(ref refreshCount);
                return mode switch
                {
                    0 => Task.FromResult<string?>(null),
                    1 => Task.FromResult<string?>($"https://rr1---sn-test.googlevideo.com/videoplayback?expire={nowUnix - 600}&n=initialNToken12&sig=stale_{refreshCount}&itag=251"),
                    _ => Task.FromResult<string?>($"https://rr2---sn-test.googlevideo.com/videoplayback?expire={nowUnix + 21600}&n=freshNToken999&sig=ok&itag=251")
                };
            });

        // Вызываем LogAndDiagnose403Async для проверки немедленной инвалидации SessionCacheStore
        var diagMethod = typeof(CachingStreamSource).GetMethod(
            "LogAndDiagnose403Async",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        using (var req = new HttpRequestMessage(HttpMethod.Get, initialUrl))
        using (var resp = new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden) { Content = new StringContent("") })
        {
            await ((Task)diagMethod.Invoke(source, [0, req, resp])!).ConfigureAwait(false);
        }

        TestAssert.Null(
            Core.Audio.Http.SessionCacheStore.GetManifest(trackId),
            "HTTP 403 must immediately invalidate SessionCacheStore entry so Partial Cache Fast Start never reuses dead URL");

        // 2. Проверяем CoordinatedRefreshAsync при обрыве интернета (refresher возвращает null -> NoChange, не инкрементирует circuit breaker)
        var coordinatedRefreshMethod = typeof(CachingStreamSource).GetMethod(
            "CoordinatedRefreshAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        var outcomeNoChange = await InvokeCoordinatedRefreshAsync(source, coordinatedRefreshMethod).ConfigureAwait(false);
        TestAssert.Equal("NoChange", outcomeNoChange, "Null refresher result must yield NoChange (transient network outage)");

        // 3. Проверяем StaleToken и открытие Circuit Breaker после 5 протухших рефрешей подряд
        mode = 1;
        for (int i = 0; i < 5; i++)
        {
            var outcomeStale = await InvokeCoordinatedRefreshAsync(source, coordinatedRefreshMethod).ConfigureAwait(false);
            TestAssert.Equal("StaleToken", outcomeStale, $"Expected StaleToken on iteration {i + 1}");
        }

        bool circuitBroke = false;
        try
        {
            _ = await InvokeCoordinatedRefreshAsync(source, coordinatedRefreshMethod).ConfigureAwait(false);
        }
        catch (Core.Exceptions.ChunkDownloadFatalException ex) when (ex.Reason == Core.Exceptions.ChunkDownloadFailureReason.Forbidden403)
        {
            circuitBroke = true;
        }

        TestAssert.True(circuitBroke, "CoordinatedRefreshAsync must open circuit breaker after 5 consecutive StaleToken refreshes");
    }

    private static async Task<string> InvokeCoordinatedRefreshAsync(
        CachingStreamSource source,
        System.Reflection.MethodInfo method)
    {
        var task = (Task)method.Invoke(source, [CancellationToken.None])!;
        await task.ConfigureAwait(false);
        var resultProp = task.GetType().GetProperty("Result")!;
        return resultProp.GetValue(task)!.ToString()!;
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