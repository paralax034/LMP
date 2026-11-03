using System.Globalization;
using System.Net.Http.Headers;
using LMP.Core.Audio.Http;
using LMP.Core.Exceptions;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    // --- Refresh Outcome & Decision Table ---

    /// <summary>
    /// Исход одной попытки обновления stream URL.
    /// </summary>
    /// <remarks>
    /// Вместе с <see cref="RangeRetryAction"/> и <see cref="RefreshDecision"/> открыт сборке тестов:
    /// закрытые методы refresh вызываются через <c>UnsafeAccessor</c> с типизированной сигнатурой, без рефлексии.
    /// </remarks>
    internal enum RefreshOutcome
    {
        /// <summary>Получен и применён новый совместимый URL со свежим сроком или новым n-token.</summary>
        NewUrl,

        /// <summary>
        /// Поставщик вернул тот же URL либо URL с прежним n-token и истёкшим сроком (устаревший session cache).
        /// </summary>
        SameUrl,

        /// <summary>URL не получен: поставщика нет, он вернул <c>null</c>, бросил исключение или вариант несовместим.</summary>
        NoUrl,

        /// <summary>
        /// HTTP 403 пришёл на URL, привязанный к другому внешнему IP (смена узла VPN/сети), и новый URL уже
        /// привязан к текущему IP. CDN отверг адрес, а не клиента: бюджет не расходуется (один раз на окно бюджета).
        /// </summary>
        EgressChanged
    }

    /// <summary>
    /// Действие с диапазоном после refresh.
    /// </summary>
    internal enum RangeRetryAction
    {
        /// <summary>Повторить запрос сразу с полным набором сетевых попыток.</summary>
        RetryNow,

        /// <summary>Повторить запрос после backoff: поставщик пока выдаёт тот же URL.</summary>
        RetryAfterBackoff,

        /// <summary>Передать управление циклу восстановления сети: URL сейчас не получить.</summary>
        DeferToNetworkRecovery
    }

    /// <summary>
    /// Решение по исходу refresh.
    /// </summary>
    /// <param name="SpendsBudget">Списывает ли попытка единицу единого бюджета.</param>
    /// <param name="Action">Действие с диапазоном.</param>
    internal readonly record struct RefreshDecision(bool SpendsBudget, RangeRetryAction Action);

    /// <summary>
    /// Единая таблица решений refresh. После HTTP 403 бюджет списывает любая попытка, кроме смены внешнего IP;
    /// проактивный TTL-refresh бюджет не трогает.
    /// </summary>
    /// <param name="outcome">Исход попытки.</param>
    /// <param name="forbiddenDriven">Refresh вызван HTTP 403 от CDN.</param>
    /// <returns>Решение для бюджета и диапазона.</returns>
    private static RefreshDecision DecideRefresh(RefreshOutcome outcome, bool forbiddenDriven) => outcome switch
    {
        RefreshOutcome.NewUrl => new(forbiddenDriven, RangeRetryAction.RetryNow),
        RefreshOutcome.EgressChanged => new(false, RangeRetryAction.RetryNow),
        RefreshOutcome.SameUrl => new(forbiddenDriven, RangeRetryAction.RetryAfterBackoff),
        _ => new(forbiddenDriven, RangeRetryAction.DeferToNetworkRecovery)
    };

    // --- URL Freshness Check ---

    /// <summary>
    /// Проактивно проверяет срок жизни URL потока и обновляет его при необходимости до старта чтения.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    public async Task EnsureStreamFreshnessAsync(CancellationToken ct)
    {
        if (_disposed || IsFullyBuffered) return;

        // Пустой URL (partial cache bootstrap): continuation URL получает single-flight EnsureUrlAvailableAsync.
        // TTL-refresh здесь стал бы параллельным запросом к InnerTube и сбросил бы session cache, нужный acquisition.
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
    /// Запрашивает новый URL через <see cref="_urlProvider"/>, применяет его и классифицирует результат.
    /// URL другого варианта (itag/clen) отвергается: смешивание байтов разных контейнеров
    /// в одной записи кэша необратимо портит partial cache.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    /// <returns><see cref="RefreshOutcome.NewUrl"/>, <see cref="RefreshOutcome.SameUrl"/> или <see cref="RefreshOutcome.NoUrl"/>.</returns>
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

        // Прежний n-token при истёкшем сроке: session cache вернул старый manifest, по сути тот же мёртвый URL.
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
    /// Бросает терминальное исключение, если после HTTP 403 единый бюджет refresh исчерпан.
    /// </summary>
    /// <exception cref="ChunkDownloadFatalException">Бюджет исчерпан без единого успешного ответа CDN.</exception>
    private void ThrowIfForbiddenRefreshBudgetExhausted()
    {
        if (Volatile.Read(ref _consecutive403Count) > 0
            && Volatile.Read(ref _forbiddenRefreshBudgetSpent) >= MaxForbiddenRefreshBudget)
        {
            throw CreateForbiddenBudgetExhaustedException();
        }
    }

    /// <summary>
    /// Списывает единицу единого бюджета и запоминает исход для диагностики fatal.
    /// </summary>
    /// <param name="outcome">Исход, израсходовавший бюджет.</param>
    /// <returns>Израсходованный бюджет после списания.</returns>
    private int SpendForbiddenRefreshBudget(RefreshOutcome outcome)
    {
        _lastBudgetRefreshOutcome = outcome;
        return Interlocked.Increment(ref _forbiddenRefreshBudgetSpent);
    }

    /// <summary>
    /// Восстанавливает единый бюджет refresh после успешного ответа CDN.
    /// </summary>
    private void ResetForbiddenRefreshBudget()
    {
        Volatile.Write(ref _forbiddenRefreshBudgetSpent, 0);
        Volatile.Write(ref _egressRefreshUsed, 0);
    }

    /// <summary>
    /// Backoff перед повтором диапазона, пока поставщик выдаёт тот же URL: 1 с, 2 с, 4 с по мере расхода бюджета.
    /// </summary>
    private int GetSameUrlBackoffMs() =>
        1000 << Math.Clamp(Volatile.Read(ref _forbiddenRefreshBudgetSpent) - 1, 0, 2);

    /// <summary>
    /// Координирует обновление stream URL при получении HTTP 403 или обнаружении истёкшего TTL.
    /// Расход бюджета определяет таблица <see cref="DecideRefresh"/>.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Исход попытки обновления URL.</returns>
    /// <exception cref="ChunkDownloadFatalException">
    /// Выбрасывается после HTTP 403, если единый бюджет refresh исчерпан.
    /// </exception>
    private async Task<RefreshOutcome> CoordinatedRefreshAsync(CancellationToken ct)
    {
        // Исчерпанный бюджет: дальнейшие запросы URL лишь сжигают квоту InnerTube и провоцируют bot detection.
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
            // Рефреш требует времени (QuickJS + BotGuard ~3-5 сек).
            // Используем lifetime токен с щедрым таймаутом, чтобы отмена отдельного Range/Seek
            // не срывала получение URL на 99% готовности.
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
    /// Ожидает завершения concurrent refresh, инициированного другим caller'ом,
    /// и возвращает его результат. Бюджет списывает только initiator.
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

        // Небольшой delay: даём CDN время "переварить" новый URL.
        if (!await ct.DelayNoThrowAsync(_config.PostRefreshDelayMs, continueOnCapturedContext: false).ConfigureAwait(false))
            return RefreshOutcome.NoUrl;

        // Успех фиксируется ТОЛЬКО если URL реально присутствует и 403 сброшен
        bool concurrentRefreshSucceeded = !string.IsNullOrWhiteSpace(_currentUrl)
                                       && Volatile.Read(ref _consecutive403Count) == 0;

        Log.Debug($"[CachingSource] [{_trackId}] Concurrent refresh result: " +
                $"{(concurrentRefreshSucceeded ? "success" : "failed")}");

        return concurrentRefreshSucceeded
            ? RefreshOutcome.NewUrl
            : RefreshOutcome.NoUrl;
    }

    /// <summary>
    /// Выполняет фактическое обновление URL (только initiator path) и применяет решение таблицы к бюджету.
    /// </summary>
    private async Task<RefreshOutcome> ExecuteRefreshAsync(CancellationToken ct)
    {
        // Cooldown: защита от refresh-шторма при параллельных 403.
        var elapsed = DateTime.UtcNow - _lastRefreshTime;
        if (elapsed.TotalMilliseconds < _config.RefreshCooldownMs)
        {
            int waitMs = _config.RefreshCooldownMs - (int)elapsed.TotalMilliseconds;
            Log.Debug($"[CachingSource] [{_trackId}] Refresh cooldown: waiting {waitMs}ms");
            if (!await ct.DelayNoThrowAsync(waitMs, continueOnCapturedContext: false).ConfigureAwait(false))
                return RefreshOutcome.NoUrl;
        }

        // Причина фиксируется до запроса: новый URL сбрасывает счётчик 403.
        bool forbiddenDriven = Volatile.Read(ref _consecutive403Count) > 0;
        string urlBeforeRefresh = _currentUrl;
        string? nTokenBefore = UrlEx.TryGetQueryParameterValue(urlBeforeRefresh, "n");

        Log.Info($"[CachingSource] [{_trackId}] Executing URL refresh ({(forbiddenDriven ? "HTTP 403" : "TTL")}). " +
                $"Current n-token: {nTokenBefore?[..Math.Min(nTokenBefore.Length, 10)] ?? "MISSING"}...");

        RefreshOutcome outcome = await RefreshUrlAsync(ct).ConfigureAwait(false);
        _lastRefreshTime = DateTime.UtcNow;

        // URL, выданный для другого внешнего IP (смена узла VPN/сети), CDN отвергает при любом клиенте.
        // Бесплатен один такой refresh на окно бюджета и только если новый URL уже привязан к текущему IP,
        // иначе чередование узлов зациклило бы source.
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

        // Небольшой delay: даём CDN время "переварить" новый URL.
        _ = await ct.DelayNoThrowAsync(_config.PostRefreshDelayMs, continueOnCapturedContext: false).ConfigureAwait(false);
        return outcome;
    }

    // --- EnsureUrlAvailable ---

    /// <summary>
    /// Гарантирует наличие валидного continuation URL перед сетевой загрузкой.
    /// Реализует single-flight модель: если URL уже есть — возвращает сразу,
    /// если нет — либо ждёт внешнего attach, либо запускает самостоятельный acquire.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    /// <returns><c>true</c> если URL доступен; <c>false</c> если получить не удалось.</returns>
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
    /// Single-flight resolution continuation URL через <see cref="_urlProvider"/>.
    /// Результат доставляется через <see cref="_continuationUrlTcs"/>.
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
    /// Проверяет, что URL-кандидат адресует тот же байтовый поток, что и source:
    /// совпадают <c>itag</c> и <c>clen</c> (сравнение выполняется, только если параметр известен с обеих сторон).
    /// </summary>
    /// <param name="candidateUrl">Новый URL от refresher, acquirer или внешнего attach.</param>
    /// <param name="rejectReason">Причина отказа для диагностики; <c>null</c>, если URL совместим.</param>
    /// <returns><c>true</c>, если URL можно применить без риска испортить байтовую раскладку кэша.</returns>
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
    /// Извлекает положительный параметр <c>clen</c> (полный размер потока) из stream URL.
    /// </summary>
    /// <param name="url">Stream URL; допускается <c>null</c>.</param>
    /// <param name="clen">Размер потока в байтах; <c>0</c>, если параметр отсутствует или некорректен.</param>
    /// <returns><c>true</c>, если параметр присутствует и больше нуля.</returns>
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
    /// Создаёт терминальное исключение исчерпанного единого бюджета refresh после HTTP 403.
    /// Сообщение называет реальную причину по исходу последней попытки, а не всегда «CDN отверг URL».
    /// </summary>
    /// <returns>Исключение с причиной <see cref="ChunkDownloadFailureReason.Forbidden403"/>.</returns>
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
    /// Логирует диагностические параметры 403-ответа: n-token, c-param, UA, тело ответа.
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
    /// Строит HTTP range-запрос с учётом хоста (YouTube vs generic CDN).
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
    /// Логирует параметры подписанного URL (n-token, c-param, sig) для диагностики.
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
    /// Добавляет YouTube-специфичные параметры <c>rn</c> и <c>rbuf</c> к URL.
    /// </summary>
    private static string BuildYouTubeRangeUrl(string baseUrl, int rn)
    {
        string url = UrlEx.SetQueryParameter(baseUrl, "rn", rn.ToString());
        url = UrlEx.SetQueryParameter(url, "rbuf", "0");
        return url;
    }
}