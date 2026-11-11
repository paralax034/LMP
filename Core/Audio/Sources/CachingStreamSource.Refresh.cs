using System.Globalization;
using System.Net.Http.Headers;
using LMP.Core.Audio.Http;
using LMP.Core.Exceptions;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Audio.Sources;

internal sealed partial class CachingStreamSource
{
    // --- Refresh Outcome & Decision Table ---

    /// <summary>
    /// Outcome of a single stream URL refresh attempt.
    /// </summary>
    /// <remarks>
    /// Exposed to the test assembly along with <see cref="RangeRetryAction"/> and <see cref="RefreshDecision"/>:
    /// private refresh methods are invoked via <c>UnsafeAccessor</c> with a strongly-typed signature, bypassing reflection.
    /// </remarks>
    internal enum RefreshOutcome
    {
        /// <summary>A new compatible URL with a fresh expiration time or new n-token was received and applied.</summary>
        NewUrl,

        /// <summary>
        /// The provider returned the same URL or a URL with the former n-token and an expired lifetime (stale session cache).
        /// </summary>
        SameUrl,

        /// <summary>URL not acquired: provider missing, returned <c>null</c>, threw an exception, or variant is incompatible.</summary>
        NoUrl,

        /// <summary>
        /// HTTP 403 occurred on a URL bound to a different external IP (VPN/network node change), and the new URL is already
        /// bound to the current IP. The CDN rejected the address, not the client: budget is not consumed (once per budget window).
        /// </summary>
        EgressChanged
    }

    /// <summary>
    /// Action to perform on the range request after refresh.
    /// </summary>
    internal enum RangeRetryAction
    {
        /// <summary>Retry the request immediately with a full set of network retries.</summary>
        RetryNow,

        /// <summary>Retry the request after backoff: the provider is currently serving the same URL.</summary>
        RetryAfterBackoff,

        /// <summary>Hand over control to the network recovery loop: URL cannot be acquired right now.</summary>
        DeferToNetworkRecovery
    }

    /// <summary>
    /// Decision based on the refresh outcome.
    /// </summary>
    /// <param name="SpendsBudget">Whether the attempt consumes one unit from the unified budget.</param>
    /// <param name="Action">Action to take for the range request.</param>
    internal readonly record struct RefreshDecision(bool SpendsBudget, RangeRetryAction Action);

    /// <summary>
    /// Unified refresh decision matrix. Following HTTP 403, any attempt consumes budget except external IP changes;
    /// proactive TTL refresh does not consume budget.
    /// </summary>
    /// <param name="outcome">Outcome of the refresh attempt.</param>
    /// <param name="forbiddenDriven">Refresh was triggered by an HTTP 403 from the CDN.</param>
    /// <returns>Decision for budget consumption and range retry action.</returns>
    private static RefreshDecision DecideRefresh(RefreshOutcome outcome, bool forbiddenDriven) => outcome switch
    {
        RefreshOutcome.NewUrl => new(forbiddenDriven, RangeRetryAction.RetryNow),
        RefreshOutcome.EgressChanged => new(false, RangeRetryAction.RetryNow),
        RefreshOutcome.SameUrl => new(forbiddenDriven, RangeRetryAction.RetryAfterBackoff),
        _ => new(forbiddenDriven, RangeRetryAction.DeferToNetworkRecovery)
    };

    // --- URL Freshness Check ---

    /// <summary>
    /// Proactively checks the stream URL expiration time and refreshes it if necessary before reading starts.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task EnsureStreamFreshnessAsync(CancellationToken ct)
    {
        if (_disposed || IsFullyBuffered) return;

        // Empty URL (partial cache bootstrap): continuation URL is fetched via single-flight EnsureUrlAvailableAsync.
        // TTL refresh here would trigger a concurrent request to InnerTube and invalidate the session cache required by acquisition.
        if (string.IsNullOrWhiteSpace(_currentUrl))
            return;

        if (UrlEx.IsUrlExpiredOrExpiringSoon(_currentUrl, TimeSpan.FromMinutes(5)))
        {
            Log.Info($"[CachingSource] [{_trackId}] URL expiring soon on resume. Executing proactive refresh...");
            try
            {
                await CoordinatedRefreshAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"[CachingSource] [{_trackId}] Proactive refresh on resume failed: {ex.Message}");
            }
        }
    }

    // --- URL Refresh ---

    /// <summary>
    /// Requests a new URL via <see cref="_urlProvider"/>, applies it, and classifies the result.
    /// A URL for a different variant (itag/clen) is rejected: mixing bytes from different containers
    /// in a single cache entry irreversibly corrupts the partial cache.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see cref="RefreshOutcome.NewUrl"/>, <see cref="RefreshOutcome.SameUrl"/>, or <see cref="RefreshOutcome.NoUrl"/>.</returns>
    private async Task<RefreshOutcome> RefreshUrlAsync(CancellationToken ct)
    {
        if (_urlProvider == null)
        {
            Log.Warn($"[CachingSource] [{_trackId}] URL refresh skipped: source has no URL provider");
            return RefreshOutcome.NoUrl;
        }

        string? newUrl;
        try
        {
            newUrl = await RequestUrlAsync(StreamUrlRequestKind.Refresh, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"[CachingSource] [{_trackId}] URL refresh failed: {ex.Message}");
            return RefreshOutcome.NoUrl;
        }

        if (string.IsNullOrEmpty(newUrl))
            return RefreshOutcome.NoUrl;

        if (!IsUrlVariantCompatible(newUrl, out string? rejectReason))
        {
            Log.Warn($"[CachingSource] [{_trackId}] Refreshed URL rejected: variant mismatch ({rejectReason})");
            return RefreshOutcome.NoUrl;
        }

        string urlBefore = _currentUrl;
        _currentUrl = newUrl;

        if (string.Equals(urlBefore, newUrl, StringComparison.Ordinal))
            return RefreshOutcome.SameUrl;

        // Same n-token with an expired lifetime: session cache returned an old manifest, effectively the same dead URL.
        string? nTokenBefore = UrlEx.TryGetQueryParameterValue(urlBefore, "n");
        string? nTokenAfter = UrlEx.TryGetQueryParameterValue(newUrl, "n");
        bool hasValidNewExpire = UrlEx.TryGetExpireUtc(newUrl, out var expAfter)
            && expAfter > DateTime.UtcNow.AddMinutes(5);
        bool nTokenChanged = !string.IsNullOrEmpty(nTokenAfter)
            && !string.Equals(nTokenBefore, nTokenAfter, StringComparison.Ordinal);

        return !hasValidNewExpire && !nTokenChanged && !string.IsNullOrEmpty(nTokenBefore)
            ? RefreshOutcome.SameUrl
            : RefreshOutcome.NewUrl;
    }

    // --- Forbidden Refresh Budget ---

    /// <summary>
    /// Throws a terminal exception if the unified refresh budget is exhausted after an HTTP 403.
    /// </summary>
    /// <exception cref="ChunkDownloadFatalException">Budget exhausted without a single successful CDN response.</exception>
    private void ThrowIfForbiddenRefreshBudgetExhausted()
    {
        if (Volatile.Read(ref _consecutive403Count) > 0
            && Volatile.Read(ref _forbiddenRefreshBudgetSpent) >= MaxForbiddenRefreshBudget)
        {
            throw CreateForbiddenBudgetExhaustedException();
        }
    }

    /// <summary>
    /// Consumes one unit of the unified budget and remembers the outcome for fatal diagnostics.
    /// </summary>
    /// <param name="outcome">The outcome that consumed the budget.</param>
    /// <returns>Remaining budget after consumption.</returns>
    private int SpendForbiddenRefreshBudget(RefreshOutcome outcome)
    {
        _lastBudgetRefreshOutcome = outcome;
        return Interlocked.Increment(ref _forbiddenRefreshBudgetSpent);
    }

    /// <summary>
    /// Restores the unified refresh budget after a successful CDN response.
    /// </summary>
    private void ResetForbiddenRefreshBudget()
    {
        Volatile.Write(ref _forbiddenRefreshBudgetSpent, 0);
        Volatile.Write(ref _egressRefreshUsed, 0);
    }

    /// <summary>
    /// Backoff before retrying the range request while the provider returns the same URL: 1s, 2s, 4s as budget is consumed.
    /// </summary>
    private int GetSameUrlBackoffMs() =>
        1000 << Math.Clamp(Volatile.Read(ref _forbiddenRefreshBudgetSpent) - 1, 0, 2);

    /// <summary>
    /// Coordinates stream URL refresh upon receiving HTTP 403 or detecting an expired TTL.
    /// Budget consumption is determined by the <see cref="DecideRefresh"/> decision matrix.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Outcome of the URL refresh attempt.</returns>
    /// <exception cref="ChunkDownloadFatalException">
    /// Thrown after HTTP 403 if the unified refresh budget is exhausted.
    /// </exception>
    private async Task<RefreshOutcome> CoordinatedRefreshAsync(CancellationToken ct)
    {
        // Exhausted budget: further URL requests only burn InnerTube quota and trigger bot detection.
        ThrowIfForbiddenRefreshBudgetExhausted();

        if (_disposed)
            return RefreshOutcome.NoUrl;

        bool isInitiator;
        try
        {
            isInitiator = await _refreshLock.WaitAsync(0, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return RefreshOutcome.NoUrl;
        }

        if (!isInitiator)
            return await WaitForConcurrentRefreshAsync(ct).ConfigureAwait(false);

        try
        {
            // Refresh takes time (QuickJS + BotGuard ~3-5 sec).
            // Use a lifetime token with a generous timeout so that canceling an individual Range/Seek
            // does not abort URL retrieval at 99% progress.
            using var refreshCts = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCts?.Token ?? CancellationToken.None);
            refreshCts.CancelAfter(TimeSpan.FromSeconds(20));

            return await ExecuteRefreshAsync(refreshCts.Token).ConfigureAwait(false);
        }
        finally
        {
            try { _refreshLock.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Awaits completion of a concurrent refresh initiated by another caller
    /// and returns its result. Only the initiator consumes budget.
    /// </summary>
    private async Task<RefreshOutcome> WaitForConcurrentRefreshAsync(CancellationToken ct)
    {
        Log.Debug($"[CachingSource] [{_trackId}] Waiting for concurrent URL refresh...");

        try
        {
            await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
            _refreshLock.Release();
        }
        catch (ObjectDisposedException)
        {
            return RefreshOutcome.NoUrl;
        }

        ThrowIfForbiddenRefreshBudgetExhausted();

        // Short delay: give CDN time to "digest" the new URL.
        if (!await ct.DelayNoThrowAsync(_config.PostRefreshDelayMs, continueOnCapturedContext: false).ConfigureAwait(false))
            return RefreshOutcome.NoUrl;

        // Success is recorded ONLY if the URL is actually present and 403 was cleared
        bool concurrentRefreshSucceeded = !string.IsNullOrWhiteSpace(_currentUrl)
                                       && Volatile.Read(ref _consecutive403Count) == 0;

        Log.Debug($"[CachingSource] [{_trackId}] Concurrent refresh result: " +
                $"{(concurrentRefreshSucceeded ? "success" : "failed")}");

        return concurrentRefreshSucceeded
            ? RefreshOutcome.NewUrl
            : RefreshOutcome.NoUrl;
    }

    /// <summary>
    /// Executes the actual URL refresh (initiator path only) and applies the decision matrix result to the budget.
    /// </summary>
    private async Task<RefreshOutcome> ExecuteRefreshAsync(CancellationToken ct)
    {
        // Cooldown: protection against refresh storms during concurrent 403s.
        var elapsed = DateTime.UtcNow - _lastRefreshTime;
        if (elapsed.TotalMilliseconds < _config.RefreshCooldownMs)
        {
            int waitMs = _config.RefreshCooldownMs - (int)elapsed.TotalMilliseconds;
            Log.Debug($"[CachingSource] [{_trackId}] Refresh cooldown: waiting {waitMs}ms");
            if (!await ct.DelayNoThrowAsync(waitMs, continueOnCapturedContext: false).ConfigureAwait(false))
                return RefreshOutcome.NoUrl;
        }

        // Cause is recorded before the request: a new URL resets the 403 counter.
        bool forbiddenDriven = Volatile.Read(ref _consecutive403Count) > 0;
        string urlBeforeRefresh = _currentUrl;
        string? nTokenBefore = UrlEx.TryGetQueryParameterValue(urlBeforeRefresh, "n");

        Log.Info($"[CachingSource] [{_trackId}] Executing URL refresh ({(forbiddenDriven ? "HTTP 403" : "TTL")}). " +
                $"Current n-token: {nTokenBefore?[..Math.Min(nTokenBefore.Length, 10)] ?? "MISSING"}...");

        RefreshOutcome outcome = await RefreshUrlAsync(ct).ConfigureAwait(false);
        _lastRefreshTime = DateTime.UtcNow;

        // A URL issued for a different external IP (VPN/network node switch) is rejected by CDN for any client.
        // One such refresh is free per budget window, and only if the new URL is already bound to the current IP,
        // otherwise node toggling would infinite-loop the source.
        if (outcome == RefreshOutcome.NewUrl
            && forbiddenDriven
            && !StreamUrlEgressGuard.Shared.IsBoundToCurrentEgress(urlBeforeRefresh)
            && StreamUrlEgressGuard.Shared.IsBoundToCurrentEgress(_currentUrl)
            && Interlocked.Exchange(ref _egressRefreshUsed, 1) == 0)
        {
            outcome = RefreshOutcome.EgressChanged;
        }

        int spent = DecideRefresh(outcome, forbiddenDriven).SpendsBudget
            ? SpendForbiddenRefreshBudget(outcome)
            : Volatile.Read(ref _forbiddenRefreshBudgetSpent);

        switch (outcome)
        {
            case RefreshOutcome.NewUrl:
            case RefreshOutcome.EgressChanged:
                {
                    Volatile.Write(ref _consecutive403Count, 0);
                    string? nTokenAfter = UrlEx.TryGetQueryParameterValue(_currentUrl, "n");
                    Log.Info($"[CachingSource] [{_trackId}] URL refresh: {outcome}, " +
                             $"new n-token: {nTokenAfter?[..Math.Min(nTokenAfter.Length, 10)] ?? "?"}..., " +
                             $"refresh budget {spent}/{MaxForbiddenRefreshBudget} (resets only after a successful range response)");
                    break;
                }

            case RefreshOutcome.SameUrl:
                Log.Warn($"[CachingSource] [{_trackId}] URL refresh: SameUrl (same URL or unchanged n-token with expired TTL, " +
                         $"likely stale session cache), refresh budget {spent}/{MaxForbiddenRefreshBudget}");
                break;

            default:
                Log.Warn($"[CachingSource] [{_trackId}] URL refresh: NoUrl (provider unavailable or network down), " +
                         $"refresh budget {spent}/{MaxForbiddenRefreshBudget}");
                return outcome;
        }

        // Short delay: give CDN time to "digest" the new URL.
        _ = await ct.DelayNoThrowAsync(_config.PostRefreshDelayMs, continueOnCapturedContext: false).ConfigureAwait(false);
        return outcome;
    }

    // --- EnsureUrlAvailable ---

    /// <summary>
    /// Ensures a valid continuation URL exists before initiating a network download.
    /// Implements a single-flight model: if the URL already exists, returns immediately;
    /// if not, either waits for an external attach or initiates an independent acquire.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if the URL is available; <c>false</c> if acquisition failed.</returns>
    private async Task<bool> EnsureUrlAvailableAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_currentUrl))
            return true;

        Log.Debug($"[CachingSource] [{_trackId}] EnsureUrlAvailable: no URL yet, " +
                $"hasProvider={_urlProvider != null}");

        Task<string?> waitTask;
        bool isInitiator = false;

        lock (_continuationLock)
        {
            if (!string.IsNullOrWhiteSpace(_currentUrl))
                return true;

            if (_continuationUrlTcs != null)
            {
                waitTask = _continuationUrlTcs.Task;
            }
            else
            {
                var tcs = new TaskCompletionSource<string?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _continuationUrlTcs = tcs;
                waitTask = tcs.Task;
                isInitiator = true;
            }
        }

        if (isInitiator && _urlProvider != null)
            _ = ResolveContinuationUrlSingleFlightAsync(ct);

        try
        {
            await waitTask.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return !string.IsNullOrWhiteSpace(_currentUrl);
        }

        Log.Info($"[CachingSource] [{_trackId}] EnsureUrlAvailable resolved: " +
                $"hasUrl={!string.IsNullOrWhiteSpace(_currentUrl)}");

        if (string.IsNullOrWhiteSpace(_currentUrl))
        {
            Log.Warn($"[CachingSource] [{_trackId}] URL acquirer returned null. " +
                    $"Delaying to prevent retry storm...");
            _ = await ct.DelayNoThrowAsync(1500, continueOnCapturedContext: false).ConfigureAwait(false);
        }

        return !string.IsNullOrWhiteSpace(_currentUrl);
    }

    /// <summary>
    /// Single-flight resolution of the continuation URL via <see cref="_urlProvider"/>.
    /// The result is delivered via <see cref="_continuationUrlTcs"/>.
    /// </summary>
    private async Task ResolveContinuationUrlSingleFlightAsync(CancellationToken ct)
    {
        string? resolvedUrl = null;

        try
        {
            Log.Debug($"[CachingSource] [{_trackId}] Acquiring continuation URL (single-flight)...");
            resolvedUrl = await RequestUrlAsync(StreamUrlRequestKind.Acquire, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"[CachingSource] [{_trackId}] Continuation URL resolution failed: {ex.Message}");
        }

        TaskCompletionSource<string?>? tcs;

        lock (_continuationLock)
        {
            tcs = _continuationUrlTcs;
            _continuationUrlTcs = null;

            if (!string.IsNullOrEmpty(resolvedUrl) && string.IsNullOrWhiteSpace(_currentUrl))
            {
                if (IsUrlVariantCompatible(resolvedUrl, out string? rejectReason))
                {
                    _currentUrl = resolvedUrl;

                    _cacheEntry?.OriginalUrl = resolvedUrl;

                    Log.Info($"[CachingSource] [{_trackId}] Continuation URL resolved via single-flight");
                }
                else
                {
                    Log.Warn($"[CachingSource] [{_trackId}] Continuation URL rejected: variant mismatch ({rejectReason})");
                    resolvedUrl = null;
                }
            }
        }

        tcs?.TrySetResult(resolvedUrl);
    }

    // --- Variant Guard & 403 Budget ---

    /// <summary>
    /// Validates that the candidate URL targets the exact same byte stream as source:
    /// matching <c>itag</c> and <c>clen</c> (comparison is performed only if the parameter is known on both sides).
    /// </summary>
    /// <param name="candidateUrl">New URL from a refresher, acquirer, or external attach.</param>
    /// <param name="rejectReason">Rejection reason for diagnostics; <c>null</c> if the URL is compatible.</param>
    /// <returns><c>true</c> if the URL can be safely applied without corrupting the cache byte layout.</returns>
    private bool IsUrlVariantCompatible(string candidateUrl, out string? rejectReason)
    {
        rejectReason = null;

        string? referenceUrl = !string.IsNullOrWhiteSpace(_currentUrl)
            ? _currentUrl
            : _cacheEntry?.OriginalUrl;

        string? candidateItag = UrlEx.TryGetQueryParameterValue(candidateUrl, "itag");
        string? referenceItag = string.IsNullOrWhiteSpace(referenceUrl)
            ? null
            : UrlEx.TryGetQueryParameterValue(referenceUrl, "itag");

        if (candidateItag is not null && referenceItag is not null
            && !string.Equals(candidateItag, referenceItag, StringComparison.Ordinal))
        {
            rejectReason = $"itag {candidateItag} != {referenceItag}";
            return false;
        }

        if (!TryParseClen(candidateUrl, out long candidateClen))
            return true;

        long referenceClen = TryParseClen(referenceUrl, out long urlClen)
            ? urlClen
            : _cacheEntry?.TotalSize ?? 0;

        if (referenceClen > 0 && candidateClen != referenceClen)
        {
            rejectReason = $"clen {candidateClen} != {referenceClen}";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Extracts a positive <c>clen</c> parameter (total stream size) from a stream URL.
    /// </summary>
    /// <param name="url">Stream URL; can be <c>null</c>.</param>
    /// <param name="clen">Stream size in bytes; <c>0</c> if the parameter is missing or invalid.</param>
    /// <returns><c>true</c> if the parameter is present and greater than zero.</returns>
    private static bool TryParseClen(string? url, out long clen)
    {
        clen = 0;
        if (string.IsNullOrWhiteSpace(url)) return false;

        string? raw = UrlEx.TryGetQueryParameterValue(url, "clen");
        return raw is not null
            && long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out clen)
            && clen > 0;
    }

    /// <summary>
    /// Creates a terminal exception indicating the unified refresh budget was exhausted after HTTP 403.
    /// The message indicates the actual root cause based on the last attempt outcome, rather than generically stating "CDN rejected URL".
    /// </summary>
    /// <returns>An exception with reason <see cref="ChunkDownloadFailureReason.Forbidden403"/>.</returns>
    private ChunkDownloadFatalException CreateForbiddenBudgetExhaustedException()
    {
        int spent = Volatile.Read(ref _forbiddenRefreshBudgetSpent);
        RefreshOutcome last = _lastBudgetRefreshOutcome;
        string cause = last switch
        {
            RefreshOutcome.NewUrl => "CDN rejected freshly issued stream URLs",
            RefreshOutcome.SameUrl => "provider kept returning the same or stale stream URL",
            _ => "provider returned no stream URL"
        };

        Log.Error($"[CachingSource] [{_trackId}] HTTP 403 persists, refresh budget exhausted " +
                  $"({spent}/{MaxForbiddenRefreshBudget}, last outcome: {last}): {cause}. Escalating to fatal.");

        return new ChunkDownloadFatalException(
            message: $"Stream URL refresh budget exhausted after HTTP 403 ({spent}/{MaxForbiddenRefreshBudget}): {cause}",
            chunkIndex: -1,
            consecutiveFailures: Volatile.Read(ref _consecutive403Count),
            reason: ChunkDownloadFailureReason.Forbidden403,
            trackId: _trackId,
            httpStatusCode: 403);
    }

    // --- 403 Diagnostics ---

    /// <summary>
    /// Logs diagnostic parameters of a 403 response: n-token, c-param, UA, response body.
    /// </summary>
    private async Task LogAndDiagnose403Async(
        int logicalIndex, HttpRequestMessage request, HttpResponseMessage response)
    {
        int count = Volatile.Read(ref _consecutive403Count);
        SessionCacheStore.Invalidate(_trackId);

        var nParam = UrlEx.TryGetQueryParameterValue(_currentUrl, "n");
        var cParam = UrlEx.TryGetQueryParameterValue(_currentUrl, "c");
        var reqUa = request.Headers.UserAgent.ToString();

        Log.Warn($"[CachingSource] 403 DIAGNOSTIC range@{logicalIndex} (consecutive={count})");
        Log.Warn($"[CachingSource]   c={cParam ?? "?"}, UA={reqUa[..Math.Min(reqUa.Length, 50)]}...");
        Log.Warn($"[CachingSource]   n-token: {nParam ?? "MISSING"} " +
                 $"(len={nParam?.Length ?? 0}, " +
                 $"looks_encrypted={nParam?.Length is > 15 and < 25})");

        if (response.Headers.TryGetValues("X-Restrict-Formats-Hint", out var hints))
            Log.Warn($"[CachingSource]   Restrict-Hint: {string.Join(", ", hints)}");

        string responseBody = "";
        try
        {
            responseBody = await response.Content
                .ReadAsStringAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch { }

        if (responseBody.Length > 0)
            Log.Warn($"[CachingSource]   Body: {responseBody[..Math.Min(responseBody.Length, 200)]}");

        Log.Warn("[CachingSource]══");
    }

    // --- HTTP Request Building ---

    /// <summary>
    /// Builds an HTTP range request considering the host type (YouTube vs generic CDN).
    /// </summary>
    private HttpRequestMessage CreateRangeRequest(int logicalIndex, long start, long end, int rn)
    {
        bool isYouTube = _currentUrl.Contains(
            "googlevideo.com/videoplayback", StringComparison.Ordinal);

        if (isYouTube)
        {
            string url = BuildYouTubeRangeUrl(_currentUrl, rn);
            LogRangeRequestParams(logicalIndex, url);
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(start, end);
            SharedHttpClient.ApplyUserAgentFromUrl(request, url);
            string ua = request.Headers.UserAgent.ToString();
            Log.Debug($"[CachingSource] Range {logicalIndex} UA: {ua[..Math.Min(60, ua.Length)]}...");
            return request;
        }

        var genericRequest = new HttpRequestMessage(HttpMethod.Get, _currentUrl);
        genericRequest.Headers.Range = new RangeHeaderValue(start, end);
        genericRequest.Headers.TryAddWithoutValidation("User-Agent", YoutubeClientUtils.UaWebRemix);
        return genericRequest;
    }

    /// <summary>
    /// Logs signed URL parameters (n-token, c-param, sig) for diagnostics.
    /// </summary>
    private static void LogRangeRequestParams(int logicalIndex, string url)
    {
        var nParam = UrlEx.TryGetQueryParameterValue(url, "n");
        var cParam = UrlEx.TryGetQueryParameterValue(url, "c");
        var sigParam = UrlEx.TryGetQueryParameterValue(url, "sig");
        Log.Debug($"[CachingSource] Range {logicalIndex} URL: {url[..Math.Min(url.Length, 300)]}");
        Log.Debug($"[CachingSource] Range {logicalIndex} params: " +
                  $"c={cParam ?? "MISSING"}, " +
                  $"n={nParam?[..Math.Min(nParam.Length, 15)] ?? "MISSING"}..., " +
                  $"sig={(sigParam is not null ? $"{sigParam.Length}chars" : "MISSING")}");
    }

    /// <summary>
    /// Appends YouTube-specific <c>rn</c> and <c>rbuf</c> parameters to the URL.
    /// </summary>
    private static string BuildYouTubeRangeUrl(string baseUrl, int rn)
    {
        string url = UrlEx.SetQueryParameter(baseUrl, "rn", rn.ToString());
        url = UrlEx.SetQueryParameter(url, "rbuf", "0");
        return url;
    }
}