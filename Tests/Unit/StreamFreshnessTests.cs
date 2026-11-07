using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Sources;
using LMP.Tests.Framework;
using RefreshOutcome = LMP.Core.Audio.Sources.CachingStreamSource.RefreshOutcome;

namespace LMP.Tests.Unit;

/// <summary>
/// Unit tests for URL lifetime (expire) parsing and proactive stream refresh logic.
/// All tests are self-contained, deterministic, and offline-capable.
/// </summary>
public static class StreamFreshnessTests
{
    [TestMethod(TestCategory.Unit, "UrlEx Expire Parameter & TTL Math", Group = TestGroups.Network, Order = 1)]
    public static Task TestUrlExExpireParsingAsync()
    {
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 1. Already expired URL
        var expiredUrl = $"https://rr1.googlevideo.com/videoplayback?expire={nowUnix - 120}&n=token123&itag=251";
        TestAssert.True(UrlEx.TryGetExpireUtc(expiredUrl, out var exp1));
        TestAssert.True(exp1 < DateTime.UtcNow);
        TestAssert.True(UrlEx.IsUrlExpiredOrExpiringSoon(expiredUrl, TimeSpan.FromMinutes(5)));

        // 2. Expires in 2 minutes (threshold 5 minutes -> expiring soon)
        var expiringSoonUrl = $"https://rr1.googlevideo.com/videoplayback?expire={nowUnix + 120}&n=token123&itag=251";
        TestAssert.True(UrlEx.IsUrlExpiredOrExpiringSoon(expiringSoonUrl, TimeSpan.FromMinutes(5)));
        TestAssert.False(UrlEx.IsUrlExpiredOrExpiringSoon(expiringSoonUrl, TimeSpan.FromMinutes(1)));

        // 3. Fresh URL for 6 hours ahead
        var freshUrl = $"https://rr1.googlevideo.com/videoplayback?expire={nowUnix + 21600}&n=token123&itag=251";
        TestAssert.True(UrlEx.TryGetExpireUtc(freshUrl, out var exp3));
        TestAssert.True(exp3 > DateTime.UtcNow.AddHours(5));
        TestAssert.False(UrlEx.IsUrlExpiredOrExpiringSoon(freshUrl, TimeSpan.FromMinutes(5)));

        // 4. URL without the expire parameter
        var noExpireUrl = "https://example.com/audio/track.opus";
        TestAssert.False(UrlEx.TryGetExpireUtc(noExpireUrl, out _));
        TestAssert.False(UrlEx.IsUrlExpiredOrExpiringSoon(noExpireUrl));

        // 5. Empty string
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
            urlProvider: (_, ct) => new ValueTask<string?>(MockRefresher(ct)));

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
            urlProvider: (_, _) =>
            {
                Interlocked.Increment(ref refreshCalls);
                return ValueTask.FromResult<string?>(freshUrl);
            });

        await source.EnsureStreamFreshnessAsync(CancellationToken.None).ConfigureAwait(false);

        TestAssert.Equal(1, refreshCalls);
    }

    [TestMethod(TestCategory.Unit, "403 Refresh Outcomes: NoUrl Network Deferral, SameUrl Budget & SessionCache Purge", Group = TestGroups.Network, Order = 4)]
    public static async Task Test403RefreshOutcomesAndSessionCachePurgeAsync()
    {
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const string trackId = "yt_403_edge_test";
        var initialUrl = $"https://rr1---sn-test.googlevideo.com/videoplayback?expire={nowUnix + 21600}&n=initialNToken12&itag=251&clen=1000000";

        // 1. Verify that 403 diagnostics immediately purges the expired manifest from SessionCacheStore
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
        int mode = 0; // 0 = NoUrl (null), 1 = SameUrl (expired + same n), 2 = Fresh URL
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
            urlProvider: (_, _) =>
            {
                Interlocked.Increment(ref refreshCount);
                return mode switch
                {
                    0 => ValueTask.FromResult<string?>(null),
                    1 => ValueTask.FromResult<string?>($"https://rr1---sn-test.googlevideo.com/videoplayback?expire={nowUnix - 600}&n=initialNToken12&sig=stale_{refreshCount}&itag=251"),
                    _ => ValueTask.FromResult<string?>($"https://rr2---sn-test.googlevideo.com/videoplayback?expire={nowUnix + 21600}&n=freshNToken999&sig=ok&itag=251")
                };
            });

        try
        {
            // Invoke LogAndDiagnose403Async to verify immediate SessionCacheStore invalidation
            using (var req = new HttpRequestMessage(HttpMethod.Get, initialUrl))
            using (var resp = new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden) { Content = new StringContent("") })
            {
                await CachingStreamSourceAccessors.LogAndDiagnose403Async(source, 0, req, resp).ConfigureAwait(false);
            }

            TestAssert.Null(
                Core.Audio.Http.SessionCacheStore.GetManifest(trackId),
                "HTTP 403 must immediately invalidate SessionCacheStore entry so Partial Cache Fast Start never reuses dead URL");

            // 2. Proactive refresh (no HTTP 403) during network outage: provider returned null -> NoUrl, budget is not consumed
            for (int i = 0; i < 5; i++)
            {
                var outcomeNoUrl = await CachingStreamSourceAccessors.CoordinatedRefreshAsync(source, CancellationToken.None).ConfigureAwait(false);
                TestAssert.Equal(RefreshOutcome.NoUrl, outcomeNoUrl, "Null provider result must yield NoUrl (transient network outage)");
            }

            // 3. After HTTP 403, a stale URL (previous n-token, expired expire) -> SameUrl consumes the shared budget;
            //    after exhaustion (3 = CachingStreamSource.MaxForbiddenRefreshBudget), refresh escalates to Forbidden403.
            Volatile.Write(ref CachingStreamSourceAccessors.Consecutive403Count(source), 1);

            mode = 1;
            for (int i = 0; i < 3; i++)
            {
                var outcomeSame = await CachingStreamSourceAccessors.CoordinatedRefreshAsync(source, CancellationToken.None).ConfigureAwait(false);
                TestAssert.Equal(RefreshOutcome.SameUrl, outcomeSame, $"Expected SameUrl on iteration {i + 1}");
            }

            Core.Exceptions.ChunkDownloadFatalException? fatal = null;
            try
            {
                _ = await CachingStreamSourceAccessors.CoordinatedRefreshAsync(source, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Core.Exceptions.ChunkDownloadFatalException ex) when (ex.Reason == Core.Exceptions.ChunkDownloadFailureReason.Forbidden403)
            {
                fatal = ex;
            }

            TestAssert.NotNull(fatal, "Exhausted refresh budget after HTTP 403 must escalate to fatal Forbidden403");
            TestAssert.True(fatal!.Message.Contains("same or stale stream URL", StringComparison.Ordinal),
                $"Fatal message must name the stale URL cause instead of blaming the CDN, got: {fatal.Message}");
        }
        finally
        {
            Core.Audio.Http.SessionCacheStore.Invalidate(trackId);
        }
    }

    [TestMethod(TestCategory.Unit, "Resume Freshness Skips Empty URL During Acquisition", Group = TestGroups.Network, Order = 5)]
    public static async Task TestResumeFreshnessSkipsEmptyUrlAsync()
    {
        int providerCalls = 0;
        using var tempCache = new TempCacheManager();

        using var source = new CachingStreamSource(
            cacheKey: "test_empty_url_resume",
            trackId: "yt_empty_url_resume",
            url: string.Empty,
            contentLength: 5_000_000,
            format: AudioFormat.WebM,
            codec: AudioCodec.Opus,
            bitrate: 160,
            cacheManager: tempCache.Manager,
            config: new StreamingConfig(),
            urlProvider: (_, _) =>
            {
                Interlocked.Increment(ref providerCalls);
                return ValueTask.FromResult<string?>(null);
            });

        // Partial cache bootstrap: URL has not been acquired yet, it is owned by single-flight acquisition.
        await source.EnsureStreamFreshnessAsync(CancellationToken.None).ConfigureAwait(false);

        TestAssert.Equal(0, Volatile.Read(ref providerCalls),
            "Resume freshness check must not issue a TTL refresh while the source has no URL yet");
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