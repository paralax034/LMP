using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Sources;
using LMP.Tests.Framework;
using static LMP.Tests.Unit.StreamRefreshResilienceTests;
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

        var expiredUrl = $"https://rr1.googlevideo.com/videoplayback?expire={nowUnix - 120}&n=token123&itag=251";
        TestAssert.True(UrlEx.TryGetExpireUtc(expiredUrl, out var exp1));
        TestAssert.True(exp1 < DateTime.UtcNow);
        TestAssert.True(UrlEx.IsUrlExpiredOrExpiringSoon(expiredUrl, TimeSpan.FromMinutes(5)));

        var expiringSoonUrl = $"https://rr1.googlevideo.com/videoplayback?expire={nowUnix + 120}&n=token123&itag=251";
        TestAssert.True(UrlEx.IsUrlExpiredOrExpiringSoon(expiringSoonUrl, TimeSpan.FromMinutes(5)));
        TestAssert.False(UrlEx.IsUrlExpiredOrExpiringSoon(expiringSoonUrl, TimeSpan.FromMinutes(1)));

        var freshUrl = $"https://rr1.googlevideo.com/videoplayback?expire={nowUnix + 21600}&n=token123&itag=251";
        TestAssert.True(UrlEx.TryGetExpireUtc(freshUrl, out var exp3));
        TestAssert.True(exp3 > DateTime.UtcNow.AddHours(5));
        TestAssert.False(UrlEx.IsUrlExpiredOrExpiringSoon(freshUrl, TimeSpan.FromMinutes(5)));

        var noExpireUrl = "https://example.com/audio/track.opus";
        TestAssert.False(UrlEx.TryGetExpireUtc(noExpireUrl, out _));
        TestAssert.False(UrlEx.IsUrlExpiredOrExpiringSoon(noExpireUrl));

        TestAssert.True(UrlEx.IsUrlExpiredOrExpiringSoon(""));
        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Proactive URL Refresh on Resume & Empty URL Guard", Group = TestGroups.Network, Order = 2)]
    public static async Task TestProactiveRefreshAndEmptyUrlGuardAsync()
    {
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const string sharedNToken = "SameStaticNToken123";

        var oldUrl = $"https://rr1.googlevideo.com/videoplayback?expire={nowUnix - 3600}&n={sharedNToken}&sig=SIG1&itag=251";
        var expiringSoonUrl = $"https://rr1.googlevideo.com/videoplayback?expire={nowUnix + 120}&n={sharedNToken}&sig=SIG2&itag=251";
        var freshUrl = $"https://rr1.googlevideo.com/videoplayback?expire={nowUnix + 21600}&n={sharedNToken}&sig=SIG3&itag=251";

        using var tempCache = new TempCacheManager();

        // 1. Expired URL with unchanged N-token still refreshes to valid expire
        int expiredRefreshCalls = 0;
        using (var expiredSource = new CachingStreamSource(
            cacheKey: "test_freshness_expired",
            trackId: "yt_test_track",
            url: oldUrl,
            contentLength: 10_000_000,
            format: AudioFormat.WebM,
            codec: AudioCodec.Opus,
            bitrate: 160,
            cacheManager: tempCache.Manager,
            config: new StreamingConfig(),
            urlProvider: (_, _) =>
            {
                Interlocked.Increment(ref expiredRefreshCalls);
                return ValueTask.FromResult<string?>(freshUrl);
            }))
        {
            await expiredSource.EnsureStreamFreshnessAsync(CancellationToken.None).ConfigureAwait(false);
            TestAssert.Equal(1, Volatile.Read(ref expiredRefreshCalls));
            TestAssert.False(UrlEx.IsUrlExpiredOrExpiringSoon(expiredSource.CurrentUrl));
        }

        // 2. Expiring-soon URL proactively refreshes on resume
        int proactiveRefreshCalls = 0;
        using (var proactiveSource = new CachingStreamSource(
            cacheKey: "test_proactive_track",
            trackId: "yt_proactive_track",
            url: expiringSoonUrl,
            contentLength: 5_000_000,
            format: AudioFormat.WebM,
            codec: AudioCodec.Opus,
            bitrate: 160,
            cacheManager: tempCache.Manager,
            config: new StreamingConfig(),
            urlProvider: (_, _) =>
            {
                Interlocked.Increment(ref proactiveRefreshCalls);
                return ValueTask.FromResult<string?>(freshUrl);
            }))
        {
            await proactiveSource.EnsureStreamFreshnessAsync(CancellationToken.None).ConfigureAwait(false);
            TestAssert.Equal(1, Volatile.Read(ref proactiveRefreshCalls));
        }

        // 3. Partial cache bootstrap with empty URL must not issue a TTL refresh on resume
        int emptyUrlProviderCalls = 0;
        using (var emptySource = new CachingStreamSource(
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
                Interlocked.Increment(ref emptyUrlProviderCalls);
                return ValueTask.FromResult<string?>(null);
            }))
        {
            await emptySource.EnsureStreamFreshnessAsync(CancellationToken.None).ConfigureAwait(false);
            TestAssert.Equal(0, Volatile.Read(ref emptyUrlProviderCalls),
                "Resume freshness check must not issue a TTL refresh while the source has no URL yet");
        }
    }

    [TestMethod(TestCategory.Unit, "403 Refresh Outcomes: NoUrl Network Deferral, SameUrl Budget & SessionCache Purge", Group = TestGroups.Network, Order = 4)]
    public static async Task Test403RefreshOutcomesAndSessionCachePurgeAsync()
    {
        var savedObservedIp = Core.Youtube.Utils.StreamUrlEgressGuard.Shared.ObservedIp;
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const string trackId = "yt_403_edge_test";
        var initialUrl = $"https://rr1---sn-test.googlevideo.com/videoplayback?expire={nowUnix + 21600}&n=initialNToken12&itag=251&clen=1000000";

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
        int mode = 0;
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
            using (var req = new HttpRequestMessage(HttpMethod.Get, initialUrl))
            using (var resp = new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden) { Content = new StringContent("") })
            {
                await CachingStreamSourceAccessors.LogAndDiagnose403Async(source, 0, req, resp).ConfigureAwait(false);
            }

            TestAssert.Null(
                Core.Audio.Http.SessionCacheStore.GetManifest(trackId),
                "HTTP 403 must immediately invalidate SessionCacheStore entry so Partial Cache Fast Start never reuses dead URL");

            for (int i = 0; i < 5; i++)
            {
                var outcomeNoUrl = await CachingStreamSourceAccessors.CoordinatedRefreshAsync(source, CancellationToken.None).ConfigureAwait(false);
                TestAssert.Equal(RefreshOutcome.NoUrl, outcomeNoUrl, "Null provider result must yield NoUrl (transient network outage)");
            }

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
            Core.Youtube.Utils.StreamUrlEgressGuard.Shared.RestoreObservedIp(savedObservedIp);
        }
    }
}