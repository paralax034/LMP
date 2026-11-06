using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.ExceptionServices;
using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Sources;
using LMP.Core.Exceptions;
using LMP.Core.Youtube.Utils;
using LMP.Tests.Framework;
using RangeRetryAction = LMP.Core.Audio.Sources.CachingStreamSource.RangeRetryAction;
using RefreshDecision = LMP.Core.Audio.Sources.CachingStreamSource.RefreshDecision;
using RefreshOutcome = LMP.Core.Audio.Sources.CachingStreamSource.RefreshOutcome;

namespace LMP.Tests.Unit;

/// <summary>
/// Regression tests for the CachingStreamSource refresh pipeline resilience:
/// bounded 403-cycle budget, stream variant protection, and non-throwing wait cancellation.
/// All tests are self-contained: HTTP is replaced with an in-memory handler; no network is used.
/// </summary>
public static class StreamRefreshResilienceTests
{
    /// <summary>Must match CachingStreamSource.MaxForbiddenRefreshBudget.</summary>
    private const int ExpectedForbiddenRefreshBudget = 3;

    private const long ContentLength = 1_000_000;

    /// <summary>Async stream tag for the test: filters out first-chance exceptions from parallel tests and background tasks.</summary>
    private static readonly AsyncLocal<bool> FlowScope = new();

    [TestMethod(TestCategory.Unit, "403 Loop With Fresh URLs Escalates To Fatal Within Budget", Group = TestGroups.Network, Order = 30)]
    public static async Task TestForbiddenLoopWithFreshUrlsIsBoundedAsync()
    {
        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int refreshCount = 0;

        using var handler = new AlwaysForbiddenHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        using var tempCache = new TempCacheManager();
        using var source = CreateSource(
            tempCache,
            httpClient,
            BuildUrl(nowUnix, "initTok", 251, ContentLength),
            _ =>
            {
                int n = Interlocked.Increment(ref refreshCount);
                return Task.FromResult<string?>(BuildUrl(nowUnix, $"freshTok{n}", 251, ContentLength));
            });

        var run = await RunReadAtUntilFatalAsync(source, timeoutMs: 15_000).ConfigureAwait(false);

        TestAssert.Equal(ChunkDownloadFailureReason.Forbidden403, run.Fatal.Reason, "Exhausted 403 budget must surface as Forbidden403");
        TestAssert.Equal(403, run.Fatal.HttpStatusCode ?? 0, "Fatal exception must carry HTTP 403");
        TestAssert.True(run.Fatal.Message.Contains("CDN rejected freshly issued stream URLs", StringComparison.Ordinal),
            $"Fatal message must name CDN rejection of fresh URLs, got: {run.Fatal.Message}");
        TestAssert.Equal(ExpectedForbiddenRefreshBudget, Volatile.Read(ref refreshCount),
            "Refresh success must not reset the 403 budget: only a successful CDN response may do that");
        TestAssert.Equal(ExpectedForbiddenRefreshBudget + 1, handler.Requests,
            "Exactly one range request per URL is expected before escalation");
        TestAssert.True(run.ElapsedMs < 10_000, $"403 escalation took {run.ElapsedMs}ms, expected bounded time");
        TestAssert.Equal(0, run.CancelExceptions, "403 refresh loop must not raise first-chance OperationCanceledException");
    }

    [TestMethod(TestCategory.Unit, "403 Loop With NoUrl Provider Escalates To Fatal", Group = TestGroups.Network, Order = 31)]
    public static async Task TestForbiddenLoopWithNoUrlProviderIsBoundedAsync()
    {
        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int refreshCount = 0;

        using var handler = new AlwaysForbiddenHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        using var tempCache = new TempCacheManager();
        using var source = CreateSource(
            tempCache,
            httpClient,
            BuildUrl(nowUnix, "initTok", 251, ContentLength),
            _ =>
            {
                Interlocked.Increment(ref refreshCount);
                return Task.FromResult<string?>(null);
            });

        var run = await RunReadAtUntilFatalAsync(source, timeoutMs: 20_000).ConfigureAwait(false);

        TestAssert.Equal(ChunkDownloadFailureReason.Forbidden403, run.Fatal.Reason, "403 + NoUrl must not degrade into an endless network-recovery loop");
        TestAssert.Equal(ExpectedForbiddenRefreshBudget, Volatile.Read(ref refreshCount),
            "NoUrl refreshes after HTTP 403 must consume the same single budget");
        TestAssert.True(run.Fatal.Message.Contains("provider returned no stream URL", StringComparison.Ordinal),
            $"Fatal message must not blame the CDN when no URL was issued, got: {run.Fatal.Message}");
        TestAssert.True(run.ElapsedMs < 15_000, $"403 + NoUrl escalation took {run.ElapsedMs}ms, expected bounded time");
        TestAssert.Equal(0, run.CancelExceptions, "403 + NoUrl loop must not raise first-chance OperationCanceledException");
    }

    [TestMethod(TestCategory.Unit, "Refresh Rejects Variant Mismatch (itag/clen)", Group = TestGroups.Network, Order = 32)]
    public static async Task TestRefreshRejectsVariantMismatchAsync()
    {
        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const long WebmLength = 2_711_112;
        const long Mp4Length = 2_785_782;

        string webmUrl = BuildUrl(nowUnix, "webmTok", 251, WebmLength);
        string? nextUrl = null;

        using var handler = new AlwaysForbiddenHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        using var tempCache = new TempCacheManager();
        using var source = CreateSource(
            tempCache,
            httpClient,
            webmUrl,
            _ => Task.FromResult(nextUrl),
            contentLength: WebmLength);

        // 1. MP4 (itag 140) instead of WebM (itag 251): exact scenario of partial cache corruption from the log.
        nextUrl = BuildUrl(nowUnix, "mp4Tok", 140, Mp4Length);
        var outcome = await CachingStreamSourceAccessors.CoordinatedRefreshAsync(source, CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(RefreshOutcome.NoUrl, outcome, "Foreign itag must be rejected as NoUrl");
        TestAssert.Equal(webmUrl, source.CurrentUrl, "Current URL must stay on the original variant");

        // 2. Same itag, but different clen (e.g. DRC version): byte offsets are incompatible.
        nextUrl = BuildUrl(nowUnix, "drcTok", 251, Mp4Length);
        outcome = await CachingStreamSourceAccessors.CoordinatedRefreshAsync(source, CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(RefreshOutcome.NoUrl, outcome, "Same itag with foreign clen must be rejected");
        TestAssert.Equal(webmUrl, source.CurrentUrl, "Current URL must not change on clen mismatch");

        // 3. Compatible fresh URL is accepted.
        string compatibleUrl = BuildUrl(nowUnix, "okTok", 251, WebmLength);
        nextUrl = compatibleUrl;
        outcome = await CachingStreamSourceAccessors.CoordinatedRefreshAsync(source, CancellationToken.None).ConfigureAwait(false);
        TestAssert.Equal(RefreshOutcome.NewUrl, outcome, "Compatible URL must be accepted");
        TestAssert.Equal(compatibleUrl, source.CurrentUrl, "Compatible URL must become current");

        // 4. External UpdateUrl (AudioPipeline) is protected by the same guard.
        bool applied = source.UpdateUrl(BuildUrl(nowUnix, "mp4Tok2", 140, Mp4Length));
        TestAssert.False(applied, "UpdateUrl must reject a foreign variant");
        TestAssert.Equal(compatibleUrl, source.CurrentUrl, "UpdateUrl rejection must keep current URL");
    }

    [TestMethod(TestCategory.Unit, "CDN 403 Fallback Order Excludes Failed Clients", Group = TestGroups.Network, Order = 33)]
    public static Task TestCdnForbiddenFallbackOrderAsync()
    {
        var afterRemix = YoutubeClientUtils.GetStreamFallbackClientsAfterCdnForbidden([YoutubeClientNames.WebRemix]);
        TestAssert.Equal(2, afterRemix.Length, "WEB_REMIX must be excluded");
        TestAssert.Equal(YoutubeClientNames.Web, afterRemix[0], "WEB must be tried first after CDN 403");
        TestAssert.Equal(YoutubeClientNames.AndroidVr, afterRemix[1], "ANDROID_VR must be the last resort (bot detection risk)");

        var afterVr = YoutubeClientUtils.GetStreamFallbackClientsAfterCdnForbidden([YoutubeClientNames.AndroidVr]);
        TestAssert.Equal(2, afterVr.Length, "ANDROID_VR must be excluded");
        TestAssert.Equal(YoutubeClientNames.Web, afterVr[0], "WEB must lead the chain");
        TestAssert.Equal(YoutubeClientNames.WebRemix, afterVr[1], "WEB_REMIX must follow WEB");

        var caseInsensitive = YoutubeClientUtils.GetStreamFallbackClientsAfterCdnForbidden(["web_remix", "web"]);
        TestAssert.Equal(1, caseInsensitive.Length, "URL c-parameter comparison must be case-insensitive");
        TestAssert.Equal(YoutubeClientNames.AndroidVr, caseInsensitive[0], "Only ANDROID_VR must remain");

        var none = YoutubeClientUtils.GetStreamFallbackClientsAfterCdnForbidden(
            [YoutubeClientNames.Web, YoutubeClientNames.WebRemix, YoutubeClientNames.AndroidVr]);
        TestAssert.Equal(0, none.Length, "Fully excluded chain must be empty so the caller fails fast");

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Refresh Request Carries Source Variant Without Active Pipeline", Group = TestGroups.Network, Order = 35)]
    public static async Task TestRefreshRequestCarriesSourceVariantAsync()
    {
        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const long WebmLength = 2_711_112;
        var requests = new ConcurrentQueue<StreamUrlRequest>();
        int refreshCount = 0;

        string initialUrl = BuildUrl(nowUnix, "initTok", 251, WebmLength);

        using var handler = new AlwaysForbiddenHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        using var tempCache = new TempCacheManager();
        using var source = CreateSourceWithProvider(
            tempCache,
            httpClient,
            initialUrl,
            (request, _) =>
            {
                requests.Enqueue(request);
                int n = Interlocked.Increment(ref refreshCount);
                return ValueTask.FromResult<string?>(BuildUrl(nowUnix, $"freshTok{n}", 251, WebmLength));
            },
            contentLength: WebmLength);

        // Source outside AudioPlayer: exact situation of 403 on the first range during InitializeAsync.
        await RunReadAtUntilFatalAsync(source, timeoutMs: 15_000).ConfigureAwait(false);

        TestAssert.True(requests.TryDequeue(out var first), "Provider must be invoked after CDN 403");
        TestAssert.Equal(StreamUrlRequestKind.Refresh, first!.Kind, "CDN 403 must produce a refresh request");
        TestAssert.Equal("yt_refresh_resilience", first.TrackId, "Request must identify the source track");
        TestAssert.Equal(AudioFormat.WebM, first.Format, "Request must carry the cached container");
        TestAssert.Equal(160, first.BitrateKbps, "Request must carry the source bitrate");
        TestAssert.Equal(WebmLength, first.ContentLength, "Request must carry the cached variant size (clen)");
        TestAssert.True(first.IsForbiddenDriven, "403-driven refresh must be flagged for client rotation");
        TestAssert.Equal(initialUrl, first.CurrentUrl, "Request must carry the URL rejected by CDN");
        TestAssert.True(first.CurrentUrl.Contains("&c=WEB_REMIX", StringComparison.Ordinal),
            "Rejected URL must expose the InnerTube client (c parameter) for exclusion");
    }

    [TestMethod(TestCategory.Unit, "Source Builds Explicit URL Request (Idle)", Group = TestGroups.Network, Order = 36)]
    public static Task TestSourceBuildsIdleUrlRequestAsync()
    {
        const long WebmLength = 2_711_112;

        // Partial cache prior to continuation URL: request carries variant, but is not considered 403-driven.
        using var handler = new AlwaysForbiddenHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        using var tempCache = new TempCacheManager();
        using var source = CreateSource(
            tempCache,
            httpClient,
            "",
            _ => Task.FromResult<string?>(null),
            contentLength: WebmLength);

        var idle = source.CreateUrlRequest(StreamUrlRequestKind.Acquire);
        TestAssert.Equal(StreamUrlRequestKind.Acquire, idle.Kind, "Request must keep the requested kind");
        TestAssert.Equal("yt_refresh_resilience", idle.TrackId, "Request must identify the source track");
        TestAssert.Equal(string.Empty, idle.CurrentUrl, "Source without URL must report an empty URL");
        TestAssert.False(idle.IsForbiddenDriven, "Acquire without CDN 403 must not trigger client exclusion");
        TestAssert.Equal(WebmLength, idle.ContentLength, "Content length must fall back to the constructor value");
        TestAssert.Equal(new StreamSelectionHint(AudioFormat.WebM, 160), idle.ToSelectionHint(),
            "Selection hint must address exactly the source variant");

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Continuation Variant Selection By Exact Clen", Group = TestGroups.Network, Order = 37)]
    public static Task TestContinuationVariantSelectionByClenAsync()
    {
        const long Mp4Length = 2_785_782;
        const long WebmLength = 2_711_112;

        // Order matches session cache from the log: MP4 first, then WebM by ascending bitrate.
        var variants = new List<LMP.Core.Audio.Http.VariantEntry>
        {
            new() { Itag = 140, Url = BuildVariantUrl(140), Container = "mp4", Bitrate = 128_000, Clen = Mp4Length },
            new() { Itag = 249, Url = BuildVariantUrl(249), Container = "webm", Bitrate = 50_000, Clen = 1_050_000 },
            new() { Itag = 250, Url = BuildVariantUrl(250), Container = "webm", Bitrate = 70_000, Clen = 1_400_000 },
            new() { Itag = 251, Url = BuildVariantUrl(251), Container = "webm", Bitrate = 129_000, Clen = WebmLength },
        };

        var webmHint = new StreamSelectionHint(AudioFormat.WebM, 129);

        var exact = AudioEngine.SelectContinuationVariantFromEntry(variants, webmHint, WebmLength);
        TestAssert.Equal(251, exact?.Itag ?? 0,
            "Partial WebM cache must continue with the variant whose clen matches, not the first WebM variant");

        var unknownSize = AudioEngine.SelectContinuationVariantFromEntry(variants, webmHint, 1_234_567);
        TestAssert.Null(unknownSize, "Missing clen match must not fall back to a foreign variant");

        var foreignContainer = AudioEngine.SelectContinuationVariantFromEntry(
            variants, new StreamSelectionHint(AudioFormat.Mp4, 128), WebmLength);
        TestAssert.Null(foreignContainer, "Container mismatch must be rejected even with equal clen");

        var legacy = AudioEngine.SelectContinuationVariantFromEntry(variants, webmHint, 0);
        TestAssert.Equal(AudioFormat.WebM, legacy?.Format ?? AudioFormat.Unknown,
            "Without clen the legacy container-based selection must be kept");

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Refresh Descriptor Compatibility With Source Snapshot", Group = TestGroups.Network, Order = 38)]
    public static Task TestRefreshDescriptorCompatibilityAsync()
    {
        const long Mp4Length = 2_785_782;
        const long WebmLength = 2_711_112;
        var snapshot = new StreamUrlRequest(StreamUrlRequestKind.Refresh, "yt_compat", AudioFormat.WebM, 129, WebmLength, "", true);

        TestAssert.False(snapshot.IsCompatible(AudioFormat.Mp4, Mp4Length), "MP4 descriptor must not continue a WebM source");
        TestAssert.False(snapshot.IsCompatible(AudioFormat.WebM, 1_400_000), "Another WebM itag (foreign clen) must be rejected");
        TestAssert.True(snapshot.IsCompatible(AudioFormat.WebM, WebmLength), "Same container and clen must be accepted");
        TestAssert.True(snapshot.IsCompatible(AudioFormat.WebM, 0), "Unknown candidate size is left to the source URL guard");

        var unknownSource = snapshot with { ContentLength = 0 };
        TestAssert.True(unknownSource.IsCompatible(AudioFormat.WebM, Mp4Length), "Unknown source size accepts any size of the same container");
        TestAssert.False(unknownSource.IsCompatible(AudioFormat.Mp4, Mp4Length), "Container check must apply even without size");

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Concurrent Refresh Waiter Cancels Without Exceptions", Group = TestGroups.Network, Order = 34)]
    public static async Task TestConcurrentRefreshWaiterCancellationNoThrowAsync()
    {
        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var refresherEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresherGate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var config = new StreamingConfig
        {
            RefreshCooldownMs = 0,
            PostRefreshDelayMs = 1_500
        };

        using var handler = new AlwaysForbiddenHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        using var tempCache = new TempCacheManager();
        using var source = CreateSource(
            tempCache,
            httpClient,
            BuildUrl(nowUnix, "initTok", 251, ContentLength),
            _ =>
            {
                refresherEntered.TrySetResult();
                return refresherGate.Task;
            },
            config: config);

        int cancelExceptions = 0;
        void OnFirstChance(object? sender, FirstChanceExceptionEventArgs e)
        {
            if (FlowScope.Value && e.Exception is OperationCanceledException)
                Interlocked.Increment(ref cancelExceptions);
        }

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        FlowScope.Value = true;
        try
        {
            // Initiator holds _refreshLock until refresher is released.
            var initiatorTask = CachingStreamSourceAccessors.CoordinatedRefreshAsync(source, CancellationToken.None);
            await refresherEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            using var waiterCts = new CancellationTokenSource();
            var waiterTask = CachingStreamSourceAccessors.WaitForConcurrentRefreshAsync(source, waiterCts.Token);

            refresherGate.SetResult(BuildUrl(nowUnix, "freshTok", 251, ContentLength));
            var initiatorOutcome = await initiatorTask.ConfigureAwait(false);
            TestAssert.Equal(RefreshOutcome.NewUrl, initiatorOutcome, "Initiator refresh must succeed");

            // Waiter has already acquired the lock and is inside PostRefreshDelay (1500 ms) — cancel mid-way.
            waiterCts.CancelAfter(300);

            RefreshOutcome waiterOutcome;
            try
            {
                waiterOutcome = await waiterTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TestAssert.Fail("WaitForConcurrentRefreshAsync must not throw on cancellation (DelayNoThrowAsync contract)");
                throw new UnreachableException();
            }

            TestAssert.Equal(RefreshOutcome.NoUrl, waiterOutcome, "Cancelled waiter must report NoUrl");
        }
        finally
        {
            FlowScope.Value = false;
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
        }

        TestAssert.Equal(0, Volatile.Read(ref cancelExceptions), "Refresh waits must not raise first-chance OperationCanceledException");
    }

    [TestMethod(TestCategory.Unit, "Stream URL Egress IP Binding Guard", Group = TestGroups.Network, Order = 39)]
    public static Task TestStreamUrlEgressGuardAsync()
    {
        const string NodeA = "144.31.76.43";
        const string NodeB = "77.239.103.183";

        var guard = new StreamUrlEgressGuard();

        TestAssert.True(guard.IsBoundToCurrentEgress(BuildEgressUrl(NodeB)),
            "Cold start (no fresh URL observed yet) must accept cached URLs");
        TestAssert.False(guard.ObserveFreshUrl(BuildEgressUrl(NodeA)), "First observation is not an egress change");
        TestAssert.Equal(NodeA, guard.ObservedIp ?? "<null>", "Observed egress must come from the fresh URL ip parameter");

        TestAssert.True(guard.IsBoundToCurrentEgress(BuildEgressUrl(NodeA)), "URL bound to the current egress must be accepted");
        TestAssert.False(guard.IsBoundToCurrentEgress(BuildEgressUrl(NodeB)),
            "URL issued for another VPN node must be rejected (CDN answers 403)");
        TestAssert.True(guard.IsBoundToCurrentEgress(BuildVariantUrl(251)), "URL without ip parameter must not be rejected");
        TestAssert.True(guard.IsBoundToCurrentEgress(BuildEgressUrl(NodeB) + "&ipbits=0"),
            "URL with ipbits=0 is not IP-bound and must be accepted");
        TestAssert.False(guard.ObserveFreshUrl(BuildVariantUrl(251)), "Fresh URL without ip must not change the observed egress");
        TestAssert.Equal(NodeA, guard.ObservedIp ?? "<null>", "Observed egress must survive a fresh URL without ip");

        TestAssert.True(guard.ObserveFreshUrl(BuildEgressUrl(NodeB)), "Different ip in a fresh URL is an egress change");
        TestAssert.False(guard.IsBoundToCurrentEgress(BuildEgressUrl(NodeA)), "After the node switch old-node URLs must be rejected");
        TestAssert.True(StreamUrlEgressGuard.IsBoundTo(BuildEgressUrl(NodeA), null), "Unknown egress must never reject");

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Acquire Request Carries Source Variant", Group = TestGroups.Network, Order = 40)]
    public static async Task TestAcquireRequestCarriesSourceVariantAsync()
    {
        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const long WebmLength = 2_711_112;
        var requests = new ConcurrentQueue<StreamUrlRequest>();
        string acquiredUrl = BuildUrl(nowUnix, "acqTok", 251, WebmLength);

        using var handler = new AlwaysForbiddenHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        using var tempCache = new TempCacheManager();

        // Partial cache without URL: track and variant originate from the request made by source itself,
        // decoupled from AudioPlayer state (previously — _callbackTrackId, Stop/Play race condition).
        var source = CreateSourceWithProvider(
            tempCache,
            httpClient,
            "",
            (request, _) =>
            {
                requests.Enqueue(request);
                return ValueTask.FromResult<string?>(acquiredUrl);
            },
            contentLength: WebmLength);

        try
        {
            bool urlReady = await CachingStreamSourceAccessors.EnsureUrlAvailableAsync(source, CancellationToken.None)
                .ConfigureAwait(false);

            TestAssert.True(urlReady, "Compatible acquired URL must make the source URL available");
            TestAssert.Equal(acquiredUrl, source.CurrentUrl, "Acquired URL must be applied to the source");
            TestAssert.Equal(1, requests.Count, "Single-flight acquire must call the provider exactly once");
            TestAssert.True(requests.TryDequeue(out var acquire), "Provider must receive the acquire request");
            TestAssert.Equal(StreamUrlRequestKind.Acquire, acquire!.Kind, "Missing URL must produce an acquire request");
            TestAssert.Equal("yt_refresh_resilience", acquire.TrackId, "Request must identify the source track");
            TestAssert.Equal(AudioFormat.WebM, acquire.Format, "Request must carry the cached container");
            TestAssert.Equal(WebmLength, acquire.ContentLength, "Request must carry the cached variant size (clen)");
            TestAssert.False(acquire.IsForbiddenDriven, "Acquire must not be flagged as CDN 403");
            TestAssert.Equal(0, handler.Requests, "Acquire must not touch the network");
        }
        finally
        {
            source.Dispose();
        }
    }

    [TestMethod(TestCategory.Unit, "Refresh Decision Table Uses Single Budget", Group = TestGroups.Network, Order = 41)]
    public static Task TestRefreshDecisionTableAsync()
    {
        static RefreshDecision Decide(RefreshOutcome outcome, bool forbiddenDriven) =>
            CachingStreamSourceAccessors.DecideRefresh(null, outcome, forbiddenDriven);

        // After HTTP 403, any attempt (except changing external IP) consumes one unit of a single shared budget.
        TestAssert.Equal(new RefreshDecision(true, RangeRetryAction.RetryNow), Decide(RefreshOutcome.NewUrl, true),
            "Fresh URL after 403 must spend the budget and retry at once");
        TestAssert.Equal(new RefreshDecision(true, RangeRetryAction.RetryAfterBackoff), Decide(RefreshOutcome.SameUrl, true),
            "Same or stale URL after 403 must spend the budget and back off");
        TestAssert.Equal(new RefreshDecision(true, RangeRetryAction.DeferToNetworkRecovery), Decide(RefreshOutcome.NoUrl, true),
            "Missing URL after 403 must spend the budget and defer to network recovery");
        TestAssert.Equal(new RefreshDecision(false, RangeRetryAction.RetryNow), Decide(RefreshOutcome.EgressChanged, true),
            "Egress change is not a CDN rejection and must not spend the budget");

        // Proactive TTL refresh does not consume budget.
        TestAssert.Equal(new RefreshDecision(false, RangeRetryAction.RetryNow), Decide(RefreshOutcome.NewUrl, false),
            "TTL refresh must not spend the budget");
        TestAssert.Equal(new RefreshDecision(false, RangeRetryAction.RetryAfterBackoff), Decide(RefreshOutcome.SameUrl, false),
            "Stale TTL refresh must not spend the budget");
        TestAssert.Equal(new RefreshDecision(false, RangeRetryAction.DeferToNetworkRecovery), Decide(RefreshOutcome.NoUrl, false),
            "Failed TTL refresh must not spend the budget");

        return Task.CompletedTask;
    }

    // --- Helpers ---

    /// <summary>
    /// Result of running ReadAtAsync until a terminal exception.
    /// </summary>
    private readonly record struct FatalRun(ChunkDownloadFatalException Fatal, long ElapsedMs, int CancelExceptions);

    /// <summary>
    /// Invokes ReadAtAsync and expects ChunkDownloadFatalException; timeout indicates an infinite retry loop.
    /// </summary>
    private static async Task<FatalRun> RunReadAtUntilFatalAsync(CachingStreamSource source, int timeoutMs)
    {
        byte[] buffer = new byte[4096];
        int cancelExceptions = 0;

        void OnFirstChance(object? sender, FirstChanceExceptionEventArgs e)
        {
            if (FlowScope.Value && e.Exception is OperationCanceledException)
                Interlocked.Increment(ref cancelExceptions);
        }

        using var cts = new CancellationTokenSource(timeoutMs);
        var sw = Stopwatch.StartNew();

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        FlowScope.Value = true;
        try
        {
            int read = await source.ReadAtAsync(0L, buffer, cts.Token).ConfigureAwait(false);
            TestAssert.Fail($"ReadAtAsync returned {read} bytes from an always-403 CDN instead of escalating");
        }
        catch (ChunkDownloadFatalException ex)
        {
            sw.Stop();
            FlowScope.Value = false;
            return new FatalRun(ex, sw.ElapsedMilliseconds, Volatile.Read(ref cancelExceptions));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            TestAssert.Fail($"ReadAtAsync kept retrying for {timeoutMs}ms: the 403 refresh loop is unbounded");
        }
        finally
        {
            FlowScope.Value = false;
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
        }

        throw new UnreachableException();
    }

    private static CachingStreamSource CreateSource(
        TempCacheManager tempCache,
        HttpClient httpClient,
        string url,
        Func<CancellationToken, Task<string?>> refresher,
        long contentLength = ContentLength,
        StreamingConfig? config = null) =>
        CreateSourceWithProvider(
            tempCache,
            httpClient,
            url,
            (_, ct) => new ValueTask<string?>(refresher(ct)),
            contentLength,
            config);

    private static CachingStreamSource CreateSourceWithProvider(
        TempCacheManager tempCache,
        HttpClient httpClient,
        string url,
        StreamUrlProvider provider,
        long contentLength = ContentLength,
        StreamingConfig? config = null)
    {
        return new CachingStreamSource(
            cacheKey: $"yt_refresh_resilience_{Guid.NewGuid():N}_WebM_160",
            trackId: "yt_refresh_resilience",
            url: url,
            contentLength: contentLength,
            format: AudioFormat.WebM,
            codec: AudioCodec.Opus,
            bitrate: 160,
            cacheManager: tempCache.Manager,
            config: config ?? new StreamingConfig { RefreshCooldownMs = 10, PostRefreshDelayMs = 5 },
            urlProvider: provider,
            httpClient: httpClient);
    }

    private static string BuildUrl(long nowUnix, string nToken, int itag, long clen) =>
        $"https://rr1---sn-test.googlevideo.com/videoplayback?expire={nowUnix + 21_600}" +
        $"&n={nToken}&sig=sig_{nToken}&itag={itag}&clen={clen}&c=WEB_REMIX";

    private static string BuildVariantUrl(int itag) =>
        $"https://rr1---sn-test.googlevideo.com/videoplayback?itag={itag}&c=WEB_REMIX";

    private static string BuildEgressUrl(string ip) =>
        $"https://rr1---sn-test.googlevideo.com/videoplayback?expire=1&ip={ip}&itag=251&c=WEB_REMIX";

    /// <summary>
    /// HTTP handler that responds with 403 Forbidden to every request (models a CDN rejecting all URLs).
    /// </summary>
    private sealed class AlwaysForbiddenHandler : HttpMessageHandler
    {
        private int _requests;

        /// <summary>Number of received requests.</summary>
        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new ByteArrayContent([]),
                RequestMessage = request
            });
        }
    }

    /// <summary>
    /// Temporary disk cache removed after the test.
    /// </summary>
    private sealed class TempCacheManager : IDisposable
    {
        private readonly string _tempDir;

        /// <summary>Cache manager in a temporary directory.</summary>
        public AudioCacheManager Manager { get; }

        public TempCacheManager()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"lmp_refresh_test_{Guid.NewGuid():N}");
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
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}