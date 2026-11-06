using System.Buffers;
using System.Net.Sockets;
using LMP.Core.Exceptions;

namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    // --- RangeDownloadResult ---

    /// <summary>
    /// Outcome of an HTTP range request download attempt.
    /// </summary>
    private enum RangeDownloadResult
    {
        /// <summary>Range downloaded successfully and available locally.</summary>
        Success,

        /// <summary>Server returned HTTP 403 Forbidden — URL expired.</summary>
        Forbidden403,

        /// <summary>Transient network error (timeout, socket, IO).</summary>
        NetworkError,

        /// <summary>Fatal unrecoverable error (UMP format, circuit breaker).</summary>
        Fatal,

        /// <summary>Operation canceled (epoch change, dispose, external CancellationToken).</summary>
        Cancelled,

        /// <summary>Failed to acquire semaphore slot within allotted time.</summary>
        SlotTimeout,

        /// <summary>Position is out of bounds of the content.</summary>
        OutOfRange
    }

    // --- Adaptive Metrics ---

    /// <summary>
    /// Records an RTT measurement using a sliding window of three values.
    /// </summary>
    /// <param name="latencyMs">Measured latency in milliseconds.</param>
    private void SaveLatency(double latencyMs)
    {
        double currentAverage;
        bool isBootstrap;
        lock (_latencyLock)
        {
            _latency2 = _latency1;
            _latency1 = _latency0;
            _latency0 = latencyMs;
            currentAverage = GetAverageLatencyInternal();
            isBootstrap = _bandwidthSampleCount <= _config.BandwidthBootstrapSampleCount;
        }

        if (isBootstrap)
            Log.Debug($"[CachingSource] Latency: {latencyMs:F1}ms (Average Trend: {currentAverage:F1}ms)");
    }

    /// <summary>
    /// Records a throughput measurement using an exponential moving average (EMA).
    /// </summary>
    /// <param name="speedBytesPerSec">Measured speed in bytes/sec.</param>
    /// <param name="bytesTransferred">Transferred data volume determining measurement weight.</param>
    private void SaveBandwidth(double speedBytesPerSec, int bytesTransferred)
    {
        double currentSpeed;
        int sampleCount;
        lock (_latencyLock)
        {
            if (_estimatedBandwidthBytesPerSec <= 0)
            {
                _estimatedBandwidthBytesPerSec = speedBytesPerSec;
                _bandwidthSampleCount = 1;
            }
            else
            {
                _bandwidthSampleCount++;

                double weight = _bandwidthSampleCount <= _config.BandwidthBootstrapSampleCount
                    ? _config.BandwidthBootstrapWeight
                    : Math.Clamp(bytesTransferred / (double)(256 * 1024), min: 0.05, max: 0.50);

                _estimatedBandwidthBytesPerSec =
                    (weight * speedBytesPerSec) + ((1.0 - weight) * _estimatedBandwidthBytesPerSec);
            }

            currentSpeed = _estimatedBandwidthBytesPerSec;
            sampleCount = _bandwidthSampleCount;
        }

        if (sampleCount <= _config.BandwidthBootstrapSampleCount)
        {
            double speedMbps = currentSpeed * 8.0 / 1_000_000.0;
            Log.Debug(
                $"[CachingSource] Throughput: {speedMbps:F2} Mbps ({currentSpeed / 1024.0:F1} KB/s, " +
                $"sample={sampleCount}, phase=bootstrap)");
        }
    }

    // --- ReadAtAsync ---

    /// <summary>
    /// Creates a fatal exception for a range that the source failed to obtain
    /// after exhausting all local and network strategies.
    /// </summary>
    /// <param name="position">Read position where the terminal failure occurred.</param>
    /// <returns>
    /// A <see cref="ChunkDownloadFatalException"/> instance signaling the upper layer
    /// that reading this same range should not be retried at the decoder loop level.
    /// </returns>
    private ChunkDownloadFatalException CreateReadAtFatalException(long position)
    {
        long alignedStart = AlignDown(position, _requestAlignmentBytes);
        int logicalIndex = (int)(alignedStart / _requestAlignmentBytes);
        int consecutive403 = Volatile.Read(ref _consecutive403Count);

        Exception inner = _lastDownloadException
            ?? new IOException($"Failed to load range at {position} after {ReadAtMaxEpochRetries} retries");

        return new ChunkDownloadFatalException(
            message: $"Failed to load range at {position} after {ReadAtMaxEpochRetries} retries",
            chunkIndex: logicalIndex,
            consecutiveFailures: consecutive403,
            reason: ChunkDownloadFailureReason.MaxRetriesExceeded,
            trackId: _trackId,
            httpStatusCode: null,
            innerException: inner);
    }

    /// <summary>
    /// Reads data from an arbitrary position, prioritizing RAM cache, disk, and network.
    /// Implements infinite retry with exponential backoff for transient network errors.
    /// On epoch change (URL refresh), waits for the update to complete without premature abort.
    /// </summary>
    /// <param name="position">Absolute read position within content.</param>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of bytes read; <c>0</c> upon reaching end of content.</returns>
    internal async Task<int> ReadAtAsync(long position, Memory<byte> buffer, CancellationToken ct)
    {
        if (position >= _contentLength) return 0;
        int requiredLength = (int)Math.Min(buffer.Length, _contentLength - position);

        if (_ramCache.TryRead(position, buffer, out int ramRead)) return ramRead;

        int diskRead = await TryLoadRangeFromDiskAsync(position, requiredLength, buffer, ct)
            .ConfigureAwait(false);
        if (diskRead > 0) return diskRead;

        int epochRetries = 0;
        const int MaxEpochRetries = 15;
        const int EpochRetryDelayMs = 250;
        int consecutiveNetworkFailures = 0;
        bool networkStallPublished = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // Critical decoder read uses only ct (canceled on Seek or Dispose),
                // so that ResetDownloadEpoch() on HTTP 403 cancels background preloads without killing ReadAtAsync itself.
                var result = await EnsureRangeAsync(position, requiredLength, ct, isCritical: true)
                    .ConfigureAwait(false);

                switch (result)
                {
                    case RangeDownloadResult.OutOfRange:
                        return 0;

                    case RangeDownloadResult.Success:
                        {
                            if (networkStallPublished)
                            {
                                networkStallPublished = false;
                                consecutiveNetworkFailures = 0;
                                PublishNetworkRecovered();
                            }

                            if (_ramCache.TryRead(position, buffer, out ramRead)) return ramRead;

                            diskRead = await TryLoadRangeFromDiskAsync(position, requiredLength, buffer, ct)
                                .ConfigureAwait(false);
                            if (diskRead > 0) return diskRead;

                            Log.Debug($"[CachingSource] ReadAt {position}: data not at expected " +
                                        "offset after successful download, retrying alignment...");
                            if (!await ct.DelayNoThrowAsync(ReadAtEpochRetryDelayMs, continueOnCapturedContext: false).ConfigureAwait(false))
                                ct.ThrowIfCancellationRequested();
                            continue;
                        }

                    case RangeDownloadResult.NetworkError:
                    case RangeDownloadResult.SlotTimeout:
                        {
                            consecutiveNetworkFailures++;
                            CheckAndPublishNetworkStall(
                                ref networkStallPublished, consecutiveNetworkFailures, position);

                            int backoffMs = ComputeNetworkRetryBackoff(consecutiveNetworkFailures);
                            if (!await ct.DelayNoThrowAsync(backoffMs, continueOnCapturedContext: false).ConfigureAwait(false))
                                ct.ThrowIfCancellationRequested();
                            continue;
                        }

                    case RangeDownloadResult.Cancelled:
                        ct.ThrowIfCancellationRequested();
                        consecutiveNetworkFailures++;
                        CheckAndPublishNetworkStall(
                            ref networkStallPublished, consecutiveNetworkFailures, position);
                        if (!await ct.DelayNoThrowAsync(
                            ComputeNetworkRetryBackoff(consecutiveNetworkFailures), continueOnCapturedContext: false)
                            .ConfigureAwait(false))
                        {
                            ct.ThrowIfCancellationRequested();
                        }
                        continue;

                    default:
                        consecutiveNetworkFailures++;
                        if (!await ct.DelayNoThrowAsync(ComputeNetworkRetryBackoff(consecutiveNetworkFailures), continueOnCapturedContext: false)
                            .ConfigureAwait(false))
                        {
                            ct.ThrowIfCancellationRequested();
                        }
                        continue;
                }
            }
            catch (ChunkDownloadFatalException)
            {
                throw;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                epochRetries++;
                if (epochRetries >= MaxEpochRetries)
                {
                    Log.Warn($"[CachingSource] ReadAt {position}: epoch retries exhausted ({MaxEpochRetries})");
                    throw CreateReadAtFatalException(position);
                }

                Log.Debug($"[CachingSource] ReadAt at {position}: epoch changed, " +
                          $"retry {epochRetries}/{MaxEpochRetries}");
                if (!await ct.DelayNoThrowAsync(EpochRetryDelayMs, continueOnCapturedContext: false).ConfigureAwait(false))
                    ct.ThrowIfCancellationRequested();
            }
        }
    }

    /// <summary>
    /// Attempts to load range from disk cache into RAM and read data from it.
    /// Read window is bounded by the free gap between adjacent RAM blocks: the block is guaranteed
    /// to commit to RAM, preventing subsequent misses at adjacent positions from hitting disk again.
    /// </summary>
    /// <param name="position">Absolute read start position.</param>
    /// <param name="minimumLength">Minimum required data length.</param>
    /// <param name="target">Destination buffer.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of bytes read; <c>0</c> if range is unavailable on disk.</returns>
    private async Task<int> TryLoadRangeFromDiskAsync(
        long position, int minimumLength, Memory<byte> target, CancellationToken ct)
    {
        if (_cacheEntry == null) return 0;
        if (!_cacheEntry.TryGetContainingRange(position, out long rangeStart, out long rangeEndExclusive))
            return 0;

        // Concurrent download already committed position to RAM: disk not needed.
        if (!_ramCache.TryGetFreeWindow(position, out long freeStart, out long freeEndExclusive))
            return _ramCache.TryRead(position, target, out int ramRead) ? ramRead : 0;

        int desiredLength = AlignUp(Math.Max(minimumLength, _config.MinRequestSizeBytes), _requestAlignmentBytes);
        if (!TryComputeDiskLoadWindow(
                position, desiredLength, _requestAlignmentBytes,
                rangeStart, rangeEndExclusive, freeStart, freeEndExclusive,
                out long loadStart, out int loadLength))
            return 0;

        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(loadLength);
        RamRangeBlock? block = null;
        try
        {
            int length = await _cacheManager
                .ReadRangeIntoAsync(_cacheKey, loadStart, rentedBuffer.AsMemory(0, loadLength), ct)
                .ConfigureAwait(false);

            if (length > 0)
                block = new RamRangeBlock(loadStart, rentedBuffer, length);
        }
        finally
        {
            // Buffer ownership transfers to block only upon successful read.
            if (block == null)
                ArrayPool<byte>.Shared.Return(rentedBuffer);
        }

        if (block == null) return 0;

        int copied = CopyFromBlock(block.Memory.Span, (int)(position - loadStart), target);

        if (!_ramCache.TryAdd(block)) block.Dispose();
        return copied;
    }

    /// <summary>
    /// Computes disk read window for RAM miss: aligns start down, then caps window
    /// by disk range and free gap between adjacent RAM blocks.
    /// </summary>
    /// <param name="position">Miss position.</param>
    /// <param name="desiredLength">Desired aligned window length.</param>
    /// <param name="alignment">Request alignment size (bytes).</param>
    /// <param name="rangeStart">Start of disk range containing position.</param>
    /// <param name="rangeEndExclusive">Exclusive boundary of disk range.</param>
    /// <param name="freeStart">End of nearest RAM block to the left.</param>
    /// <param name="freeEndExclusive">Start of nearest RAM block to the right.</param>
    /// <param name="loadStart">Window start.</param>
    /// <param name="loadLength">Window length.</param>
    /// <returns><c>false</c> if window does not contain <paramref name="position"/>.</returns>
    internal static bool TryComputeDiskLoadWindow(
        long position,
        int desiredLength,
        int alignment,
        long rangeStart,
        long rangeEndExclusive,
        long freeStart,
        long freeEndExclusive,
        out long loadStart,
        out int loadLength)
    {
        loadStart = AlignDown(position, alignment);
        if (loadStart < rangeStart) loadStart = rangeStart;
        if (loadStart < freeStart) loadStart = freeStart;

        long loadEnd = Math.Min(rangeEndExclusive, freeEndExclusive);
        long available = loadEnd - loadStart;
        if (available <= 0 || position < loadStart || position >= loadEnd)
        {
            loadLength = 0;
            return false;
        }

        loadLength = desiredLength < available ? desiredLength : (int)available;
        return position < loadStart + loadLength;
    }

    private static int CopyFromBlock(ReadOnlySpan<byte> blockData, int offset, Memory<byte> buffer)
    {
        int available = Math.Min(buffer.Length, blockData.Length - offset);
        if (available <= 0) return 0;
        blockData.Slice(offset, available).CopyTo(buffer.Span);
        return available;
    }

    // --- EnsureRangeAsync ---

    /// <summary>
    /// Guarantees data range availability, preventing retry storms.
    /// <para>
    /// Triggers <see cref="CoordinatedRefreshAsync"/> on 403; quadratic backoff
    /// on network error; rethrows <see cref="ChunkDownloadFatalException"/> on fatal.
    /// </para>
    /// </summary>
    /// <param name="position">Range start position.</param>
    /// <param name="minimumLength">Minimum required length.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="isCritical">Priority: critical (playback) or background (preload).</param>
    private async Task<RangeDownloadResult> EnsureRangeAsync(
        long position,
        int minimumLength,
        CancellationToken ct,
        bool isCritical = false)
    {
        if (position < 0 || position >= _contentLength)
            return RangeDownloadResult.OutOfRange;

        if (minimumLength <= 0)
            return RangeDownloadResult.Success;

        minimumLength = (int)Math.Min(minimumLength, _contentLength - position);

        if (IsRangeLocallyAvailable(position, minimumLength))
            return RangeDownloadResult.Success;

        ct.ThrowIfCancellationRequested();

        // Proactive check: refresh URL BEFORE sending request only if URL exists but is expiring.
        // If URL is empty (partial cache bootstrap), fetch proceeds normally via EnsureUrlAvailableAsync.
        if (!string.IsNullOrWhiteSpace(_currentUrl) && UrlEx.IsUrlExpiredOrExpiringSoon(_currentUrl, TimeSpan.FromMinutes(5)))
        {
            Log.Info($"[CachingSource] [{_trackId}] URL expired/expiring soon. Proactive refresh before request...");
            var proactiveOutcome = await CoordinatedRefreshAsync(ct).ConfigureAwait(false);
            if (proactiveOutcome is RefreshOutcome.NewUrl or RefreshOutcome.EgressChanged)
            {
                Log.Info($"[CachingSource] [{_trackId}] Proactive refresh succeeded before range {position}");
            }
        }

        // Source-level circuit breaker: do not enter retry loop if breaker is open.
        if (IsSourceCircuitBreakerOpen(out int cbRemainingMs))
        {
            int cbDelay = Math.Min(cbRemainingMs, 1000);
            if (!await ct.DelayNoThrowAsync(cbDelay, continueOnCapturedContext: false).ConfigureAwait(false))
                return RangeDownloadResult.Cancelled;
            return RangeDownloadResult.NetworkError;
        }

        int maxAttempts = _config.MaxNetworkRetries;
        int chunkIoExceptions = 0;
        bool warningPublished = false;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (IsRangeLocallyAvailable(position, minimumLength))
                return RangeDownloadResult.Success;

            if (TryGetOverlappingActiveDownload(position, minimumLength, out var overlapping))
            {
                await JoinActiveDownloadAsync(overlapping!, isCritical, ct).ConfigureAwait(false);
                continue;
            }

            var plan = BuildDownloadPlan(position, minimumLength, isCritical);
            ActiveRangeDownload? candidate = null;
            var ownerLazy = new Lazy<Task<RangeDownloadResult>>(
                () => DownloadRangeCoreAsync(plan, candidate!, ct),
                LazyThreadSafetyMode.ExecutionAndPublication);

            candidate = new ActiveRangeDownload(plan.Start, plan.Length, isCritical, ownerLazy);
            var actual = RegisterOrGetActiveDownload(candidate);

            if (!ReferenceEquals(actual, candidate))
            {
                await JoinActiveDownloadAsync(actual, isCritical, ct).ConfigureAwait(false);
                continue;
            }

            RangeDownloadResult result;
            try
            {
                result = await actual.LazyTask.Value.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Epoch changed (seek/refresh canceled stale downloads) — normal transient.
                continue;
            }
            finally
            {
                RemoveActiveDownloadIfOwner(plan.Start, actual);
            }

            switch (result)
            {
                case RangeDownloadResult.Success:
                    return RangeDownloadResult.Success;

                case RangeDownloadResult.Forbidden403:
                    {
                        if (isCritical && !warningPublished)
                        {
                            warningPublished = true;
                            PublishSourceWarning(
                                new UnauthorizedAccessException(
                                    $"Critical range {plan.Start}-{plan.Start + plan.Length - 1L} " +
                                    $"received HTTP 403 and requires stream URL refresh"));
                        }

                        // Cancel ALL concurrent preloads: they share the same dead URL.
                        ResetDownloadEpoch();

                        Log.Warn($"[CachingSource] [{_trackId}] HTTP 403 on range {plan.Start}-{plan.Start + plan.Length - 1L}. " +
                                $"Refreshing URL (refresh budget {Volatile.Read(ref _forbiddenRefreshBudgetSpent)}/{MaxForbiddenRefreshBudget})...");

                        // Single budget exhaustion throws ChunkDownloadFatalException(Forbidden403) from CoordinatedRefreshAsync.
                        RefreshOutcome outcome = await CoordinatedRefreshAsync(ct).ConfigureAwait(false);

                        switch (DecideRefresh(outcome, forbiddenDriven: true).Action)
                        {
                            case RangeRetryAction.RetryNow:
                                chunkIoExceptions = 0;
                                // Refresh count is bounded by source budget, so new URL gets full set of network retries.
                                attempt = -1;
                                Log.Info($"[CachingSource] [{_trackId}] URL refreshed ({outcome}), retrying range {plan.Start} with fresh attempts");
                                continue;

                            case RangeRetryAction.RetryAfterBackoff:
                                {
                                    int backoffMs = GetSameUrlBackoffMs();
                                    Log.Warn($"[CachingSource] [{_trackId}] Refresh returned the same URL. " +
                                            $"Backing off {backoffMs}ms before retry...");
                                    if (!await ct.DelayNoThrowAsync(backoffMs, continueOnCapturedContext: false).ConfigureAwait(false))
                                        return RangeDownloadResult.Cancelled;
                                    continue;
                                }

                            default:
                                Log.Warn($"[CachingSource] [{_trackId}] Refresh returned no URL (provider unavailable or network down). " +
                                        $"Deferring to network recovery loop...");
                                return RangeDownloadResult.NetworkError;
                        }
                    }

                case RangeDownloadResult.NetworkError:
                    {
                        chunkIoExceptions++;

                        if (isCritical && !warningPublished && chunkIoExceptions >= 2)
                        {
                            warningPublished = true;
                            PublishSourceWarning(
                                new IOException(
                                    $"Critical range {plan.Start}-{plan.Start + plan.Length - 1L} " +
                                    $"failed repeatedly and playback may stall"));
                        }

                        // Quadratic backoff: 100ms, 400ms, 900ms, 1600ms, 2000ms (cap)
                        int delay = (int)Math.Min(2000, 100 * Math.Pow(attempt + 1, 2));
                        if (!await ct.DelayNoThrowAsync(delay, continueOnCapturedContext: false).ConfigureAwait(false))
                            return RangeDownloadResult.Cancelled;
                        continue;
                    }

                case RangeDownloadResult.Cancelled:
                    ct.ThrowIfCancellationRequested();
                    return result;

                default:
                    return result;
            }
        }

        return RangeDownloadResult.NetworkError;
    }

    // --- Source Circuit Breaker ---

    /// <summary>
    /// Checks if source-level circuit breaker is open.
    /// Transitions to half-open when backoff expires (allows single request).
    /// </summary>
    /// <param name="remainingMs">Remaining backoff in ms (0 if closed or half-open).</param>
    /// <returns><c>true</c> if request should be rejected.</returns>
    private bool IsSourceCircuitBreakerOpen(out int remainingMs)
    {
        lock (_circuitBreakerLock)
        {
            remainingMs = 0;
            if (!_circuitBreakerIsOpen) return false;

            long elapsed = Environment.TickCount64 - _circuitBreakerOpenedAtTick;
            remainingMs = _circuitBreakerBackoffMs - (int)elapsed;

            if (remainingMs <= 0)
            {
                _circuitBreakerIsOpen = false;
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Registers successful network operation: resets failure counter, closes breaker.
    /// </summary>
    private void OnSourceNetworkSuccess()
    {
        bool wasActive;
        lock (_circuitBreakerLock)
        {
            wasActive = _circuitBreakerIsOpen || _consecutiveSourceNetworkFailures > 0;
            _consecutiveSourceNetworkFailures = 0;
            _circuitBreakerIsOpen = false;
            _circuitBreakerBackoffMs = 0;
        }

        if (wasActive)
            Log.Info($"[CachingSource] [{_trackId}] Source circuit breaker CLOSED — network recovered");
    }

    /// <summary>
    /// Registers network error. Opens breaker with exponential backoff when threshold is met.
    /// </summary>
    private void OnSourceNetworkFailure()
    {
        lock (_circuitBreakerLock)
        {
            int failures = ++_consecutiveSourceNetworkFailures;

            if (!_circuitBreakerIsOpen && failures >= SourceCircuitBreakerThreshold)
            {
                _circuitBreakerIsOpen = true;
                _circuitBreakerOpenedAtTick = Environment.TickCount64;
                _circuitBreakerBackoffMs = SourceCircuitBreakerInitialBackoffMs;

                Log.Warn($"[CachingSource] [{_trackId}] Source circuit breaker OPEN: " +
                         $"{failures} consecutive failures, backoff={_circuitBreakerBackoffMs}ms");
            }
            else if (_circuitBreakerIsOpen)
            {
                _circuitBreakerOpenedAtTick = Environment.TickCount64;
                _circuitBreakerBackoffMs = Math.Min(
                    _circuitBreakerBackoffMs * 2,
                    SourceCircuitBreakerMaxBackoffMs);

                Log.Debug($"[CachingSource] [{_trackId}] Circuit breaker re-opened: " +
                          $"backoff={_circuitBreakerBackoffMs}ms");
            }
        }
    }

    // --- DownloadRangeCoreAsync ---

    /// <summary>
    /// Orchestrates range download: circuit breaker → semaphore slot → URL guard → HTTP.
    /// Background download joined by critical reader stops waiting for slot.
    /// </summary>
    /// <param name="plan">Download byte range plan.</param>
    /// <param name="download">Download descriptor in registry: priority and escalation signal.</param>
    /// <param name="ct">Cancellation token for current operation.</param>
    private async Task<RangeDownloadResult> DownloadRangeCoreAsync(
        DownloadPlan plan, ActiveRangeDownload download, CancellationToken ct)
    {
        if (_disposed) return RangeDownloadResult.Cancelled;
        if (IsRangeLocallyAvailable(plan.Start, plan.Length)) return RangeDownloadResult.Success;

        if (IsSourceCircuitBreakerOpen(out int remainingBackoffMs))
        {
            int delay = Math.Min(remainingBackoffMs, 500);
            if (!await ct.DelayNoThrowAsync(delay, continueOnCapturedContext: false).ConfigureAwait(false))
                return RangeDownloadResult.Cancelled;
            return RangeDownloadResult.NetworkError;
        }

        bool gotSlot = false;
        try
        {
            if (!download.IsCritical)
            {
                var slotOutcome = await WaitForSlotOrPromotionAsync(
                        _downloadSlots, _config.DownloadSlotTimeoutMs, download.PromotionTask, ct)
                    .ConfigureAwait(false);

                switch (slotOutcome)
                {
                    case SlotWaitOutcome.Acquired:
                        gotSlot = true;
                        break;
                    case SlotWaitOutcome.Promoted:
                        Log.Debug($"[CachingSource] [{_trackId}] Range {plan.Start}: joined by critical reader, slot wait skipped");
                        break;
                    case SlotWaitOutcome.TimedOut:
                        return RangeDownloadResult.SlotTimeout;
                    default:
                        return RangeDownloadResult.Cancelled;
                }
            }

            if (_disposed) return RangeDownloadResult.Cancelled;
            if (ct.IsCancellationRequested) return RangeDownloadResult.Cancelled;
            if (IsRangeLocallyAvailable(plan.Start, plan.Length)) return RangeDownloadResult.Success;

            if (string.IsNullOrWhiteSpace(_currentUrl))
            {
                bool urlReady = await EnsureUrlAvailableAsync(ct).ConfigureAwait(false);
                if (!urlReady)
                {
                    Log.Warn($"[CachingSource] Range {plan.Start}: continuation URL is unavailable");
                    OnSourceNetworkFailure();
                    return ct.IsCancellationRequested
                        ? RangeDownloadResult.Cancelled
                        : RangeDownloadResult.NetworkError;
                }
            }

            var result = await DownloadRangeHttpAsync(plan, ct).ConfigureAwait(false);

            switch (result)
            {
                case RangeDownloadResult.Success:
                case RangeDownloadResult.OutOfRange:
                    OnSourceNetworkSuccess();
                    break;
                case RangeDownloadResult.NetworkError:
                    OnSourceNetworkFailure();
                    break;
            }

            return result;
        }
        catch (ChunkDownloadFatalException) { throw; }
        catch (ObjectDisposedException) when (_disposed || ct.IsCancellationRequested)
        { return RangeDownloadResult.Cancelled; }
        catch (ObjectDisposedException)
        { return RangeDownloadResult.NetworkError; }
        catch (OperationCanceledException)
        { return RangeDownloadResult.Cancelled; }
        catch (Exception) when (ct.IsCancellationRequested || _disposed)
        { return RangeDownloadResult.Cancelled; }
        catch (Exception ex)
        {
            Log.Warn($"[CachingSource] Range {plan.Start} unexpected: {ex.Message}");
            OnSourceNetworkFailure();
            return RangeDownloadResult.NetworkError;
        }
        finally
        {
            if (gotSlot && !_disposed)
            {
                try { _downloadSlots.Release(); }
                catch (ObjectDisposedException) { }
            }
        }
    }

    /// <summary>
    /// Computes prefix length of downloaded block that can safely commit to RAM without overlap.
    /// </summary>
    /// <param name="start">Downloaded range start.</param>
    /// <param name="actualLength">Actual downloaded length.</param>
    /// <returns>
    /// Length of initial non-overlapping gap.
    /// May be smaller than <paramref name="actualLength"/> if tail of range is already present locally.
    /// </returns>
    private int ComputeRamCommitLength(long start, int actualLength)
    {
        if (actualLength <= 0) return 0;
        return TrimLengthToFirstKnownCoverage(start, actualLength, includeInflight: false);
    }

    // --- DownloadRangeHttpAsync ---

    /// <summary>
    /// Executes HTTP range request, writes data to RAM and disk.
    /// </summary>
    /// <param name="plan">Download byte range plan.</param>
    /// <param name="ct">Cancellation token for current operation.</param>
    /// <returns>Range download execution result.</returns>
    private async Task<RangeDownloadResult> DownloadRangeHttpAsync(DownloadPlan plan, CancellationToken ct)
    {
        int rn = Interlocked.Increment(ref _requestSequenceNumber);
        long end = plan.Start + plan.Length - 1;
        int logicalIndex = (int)(plan.Start / _requestAlignmentBytes);

        Log.Debug($"[CachingSource] Range {plan.Start}-{end}: GET rn={rn}");

        // Base request URL: 403 on an already replaced URL must not consume refresh budget.
        string requestBaseUrl = _currentUrl;
        using var request = CreateRangeRequest(logicalIndex, plan.Start, end, rn);
        HttpResponseMessage response;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        using var sendTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        sendTimeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            response = await CurrentHttpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, sendTimeoutCts.Token)
                .ConfigureAwait(false);
            sw.Stop();
            if (response.IsSuccessStatusCode) SaveLatency(sw.Elapsed.TotalMilliseconds);
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested || _disposed)
        { return RangeDownloadResult.Cancelled; }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested || _disposed)
        { return RangeDownloadResult.Cancelled; }
        catch (HttpRequestException ex) when (IsCancelledSendFailure(ex, ct, _disposed))
        { return RangeDownloadResult.Cancelled; }
        catch (TaskCanceledException ex)
        {
            _lastDownloadException = ex;
            Log.Warn($"[CachingSource] [{_trackId}] SEND timeout range {plan.Start}-{end}: " +
                     $"{NetworkErrorHelper.DescribeTransportFailure(ex)} ({sw.ElapsedMilliseconds}ms)");
            return RangeDownloadResult.NetworkError;
        }
        catch (HttpRequestException ex)
        {
            _lastDownloadException = ex;
            Log.Warn($"[CachingSource] [{_trackId}] SEND failed range {plan.Start}-{end}: " +
                     $"{NetworkErrorHelper.DescribeTransportFailure(ex)} ({sw.ElapsedMilliseconds}ms)");
            return RangeDownloadResult.NetworkError;
        }
        catch (IOException ex)
        {
            _lastDownloadException = ex;
            Log.Warn($"[CachingSource] [{_trackId}] SEND io range {plan.Start}-{end}: " +
                     $"{NetworkErrorHelper.DescribeTransportFailure(ex)} ({sw.ElapsedMilliseconds}ms)");
            return RangeDownloadResult.NetworkError;
        }

        using (response)
        {
            if (ct.IsCancellationRequested) return RangeDownloadResult.Cancelled;

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                if (!ReferenceEquals(requestBaseUrl, _currentUrl))
                {
                    // URL already replaced by concurrent refresh: this 403 belongs to old URL.
                    Log.Debug($"[CachingSource] [{_trackId}] Stale 403 for replaced URL on range {plan.Start}-{end}, retrying with current URL");
                    return RangeDownloadResult.NetworkError;
                }

                Interlocked.Increment(ref _consecutive403Count);
                await LogAndDiagnose403Async(logicalIndex, request, response).ConfigureAwait(false);
                return RangeDownloadResult.Forbidden403;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                Log.Debug($"[CachingSource] Range {plan.Start}-{end}: 416 (EOF)");
                return RangeDownloadResult.OutOfRange;
            }

            Volatile.Write(ref _consecutive403Count, 0);
            if (response.IsSuccessStatusCode)
                ResetForbiddenRefreshBudget();

            // After first successful response, remember CDN node for speculative pre-warm.
            Http.CdnConnectionPreWarmer.RecordHost(_currentUrl);

            if (response.Content.Headers.ContentType?.MediaType?.Contains("yt-ump") == true)
                throw new ChunkDownloadFatalException(
                    "YouTube returned encrypted UMP format",
                    chunkIndex: logicalIndex,
                    consecutiveFailures: 0,
                    reason: ChunkDownloadFailureReason.UmpFormat,
                    trackId: _trackId);

            response.EnsureSuccessStatusCode();

            try
            {
                using var contentStream = await response.Content
                    .ReadAsStreamAsync(ct)
                    .ConfigureAwait(false);

                byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(plan.Length);
                int actualLength;
                var bodySw = System.Diagnostics.Stopwatch.StartNew();

                // SendAsync timeout covers headers only: the body gets its own idle deadline,
                // which resets after each chunk of data.
                using var bodyIdleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                try
                {
                    actualLength = await ReadStreamFullyAsync(
                        contentStream, rentedBuffer.AsMemory(0, plan.Length), bodyIdleCts, BodyIdleTimeoutMs)
                        .ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    ArrayPool<byte>.Shared.Return(rentedBuffer);
                    return RangeDownloadResult.Cancelled;
                }
                catch (OperationCanceledException)
                {
                    ArrayPool<byte>.Shared.Return(rentedBuffer);
                    return RangeDownloadResult.Cancelled;
                }
                catch (Exception ex) when (
                                    ct.IsCancellationRequested || _disposed || (_lifetimeCts?.IsCancellationRequested == true)
                                    || ex is IOException
                                    || ex is System.Net.Sockets.SocketException)
                {
                    _lastDownloadException = ex;
                    ArrayPool<byte>.Shared.Return(rentedBuffer);

                    // If cancellation was requested externally (track switch) — instant exit without retries
                    if (ct.IsCancellationRequested || _disposed || (_lifetimeCts?.IsCancellationRequested == true))
                        return RangeDownloadResult.Cancelled;

                    Log.Warn($"[CachingSource] Range {plan.Start}-{end} read I/O error: {ex.Message}");
                    return RangeDownloadResult.NetworkError;
                }
                catch (Exception ex)
                {
                    _lastDownloadException = ex;
                    ArrayPool<byte>.Shared.Return(rentedBuffer);
                    Log.Warn($"[CachingSource] Range {plan.Start}-{end} read error: {ex.Message}");
                    return RangeDownloadResult.NetworkError;
                }
                finally
                {
                    bodySw.Stop();
                }

                // Idle deadline triggered while external token is active: this is a hung connection, not a cancellation.
                if (actualLength < plan.Length
                    && bodyIdleCts.IsCancellationRequested
                    && !ct.IsCancellationRequested
                    && !_disposed
                    && _lifetimeCts?.IsCancellationRequested != true)
                {
                    ArrayPool<byte>.Shared.Return(rentedBuffer);
                    _lastDownloadException = new TimeoutException(
                        $"Range {plan.Start}-{end} body idle for more than {BodyIdleTimeoutMs}ms");
                    Log.Warn($"[CachingSource] [{_trackId}] Range {plan.Start}-{end} body stalled: " +
                             $"{actualLength}/{plan.Length} bytes, idle>{BodyIdleTimeoutMs}ms");
                    return RangeDownloadResult.NetworkError;
                }

                // Ignore micro-measurements (< 16KB or < 20ms) — false spikes of TCP slow-start.
                if (actualLength >= 16384 && bodySw.Elapsed.TotalMilliseconds >= 20)
                {
                    double speedBytesPerSec = actualLength / (bodySw.Elapsed.TotalMilliseconds / 1000.0);
                    SaveBandwidth(speedBytesPerSec, actualLength);
                }

                if (ct.IsCancellationRequested || _disposed || (_lifetimeCts?.IsCancellationRequested == true))
                {
                    ArrayPool<byte>.Shared.Return(rentedBuffer);
                    return RangeDownloadResult.Cancelled;
                }

                if (actualLength == 0)
                {
                    ArrayPool<byte>.Shared.Return(rentedBuffer);
                    Log.Warn($"[CachingSource] Range {plan.Start}-{end}: empty response");
                    return RangeDownloadResult.NetworkError;
                }

                if (actualLength < plan.Length)
                {
                    bool isCleanServerEof = false;

                    // If the server transmitted exactly the number of bytes specified in Content-Length,
                    // and the delta to the estimated end of file is within alignment tolerance — this is a legitimate EOF
                    if (response.Content.Headers.ContentLength is { } expectedLen && actualLength == expectedLen)
                    {
                        long delta = _contentLength - (plan.Start + actualLength);
                        if (delta >= 0 && delta <= _requestAlignmentBytes * 4)
                        {
                            isCleanServerEof = true;
                        }
                    }

                    bool isNearEof = isCleanServerEof || (plan.Start + actualLength >= _contentLength);
                    if (!isNearEof)
                    {
                        ArrayPool<byte>.Shared.Return(rentedBuffer);
                        Log.Warn($"[CachingSource] Range {plan.Start}-{end} incomplete: {actualLength}/{plan.Length}");
                        return RangeDownloadResult.NetworkError;
                    }
                }

                int ramCommitLength = ComputeRamCommitLength(plan.Start, actualLength);
                var block = new RamRangeBlock(plan.Start, rentedBuffer, Math.Max(0, ramCommitLength));

                // Capture a second reference to the same buffer for async disk write (Zero-Copy: no second Rent and no Span.CopyTo!)
                if (block.TryRetainForDiskWrite(actualLength, out var diskMemory))
                {
                    _ = WriteToDiskTrackedAsync(plan.Start, block, diskMemory);
                }

                bool committedToRam = false;
                if (ramCommitLength > 0 && _ramCache.TryAdd(block))
                {
                    committedToRam = true;
                }
                else
                {
                    // If the block didn't fit into the RAM cache, release the RAM cache reference;
                    // the buffer will return to ArrayPool automatically when WriteToDiskTrackedAsync completes.
                    block.Dispose();
                }

                if (!committedToRam)
                {
                    Log.Debug($"[CachingSource] Range {plan.Start}-{end}: RAM overlap avoided, " +
                              $"committed_prefix={ramCommitLength}/{actualLength}, disk_write=scheduled");
                }

                if (_ramCache.TotalBytes > _config.MaxRamBytes)
                    _ramCache.Trim(
                        Volatile.Read(ref _currentReadOffset),
                        _config.RamEvictionWindowBytes,
                        _config.MaxRamBytes);

                return RangeDownloadResult.Success;
            }
            catch (OperationCanceledException)
            {
                return RangeDownloadResult.Cancelled;
            }
        }
    }

    /// <summary>
    /// Reads the stream fully into the buffer until its end or the stream EOF is reached.
    /// Before each read, the idle deadline <paramref name="idleCts"/> is reset: a slow but active
    /// transfer is not interrupted, while a hung connection is cancelled via <paramref name="idleTimeoutMs"/>.
    /// </summary>
    /// <param name="stream">Input HTTP response data stream.</param>
    /// <param name="buffer">Target in-memory buffer.</param>
    /// <param name="idleCts">Cancellation token source linked to the external token; owned by the caller.</param>
    /// <param name="idleTimeoutMs">Maximum pause between data chunks (ms).</param>
    /// <returns>Actual number of successfully read bytes.</returns>
    internal static async ValueTask<int> ReadStreamFullyAsync(
         Stream stream, Memory<byte> buffer, CancellationTokenSource idleCts, int idleTimeoutMs)
    {
        CancellationToken ct = idleCts.Token;
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read;
            try
            {
                idleCts.CancelAfter(idleTimeoutMs);
                read = await stream.ReadAsync(buffer[totalRead..], ct).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (
                ct.IsCancellationRequested ||
                ex is IOException { InnerException: ObjectDisposedException } ||
                ex is SocketException { SocketErrorCode: SocketError.OperationAborted or SocketError.Interrupted })
            {
                break;
            }

            if (read == 0) break;
            totalRead += read;
        }

        // Cancel the deadline: triggering after exiting the loop should not look like a hang.
        if (!ct.IsCancellationRequested)
            idleCts.CancelAfter(Timeout.Infinite);

        return totalRead;
    }

    /// <summary>
    /// Writes downloaded data to disk while tracking pending-count
    /// for safe lease disposal when source is disposed.
    /// </summary>
    private async Task WriteToDiskTrackedAsync(long offset, RamRangeBlock block, ReadOnlyMemory<byte> diskMemory)
    {
        Interlocked.Increment(ref _pendingDiskWrites);
        try
        {
            await _cacheManager.WriteRangeAsync(
                _cacheKey, offset, diskMemory, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            Log.Warn($"[CachingSource] Disk write range {offset}-{offset + diskMemory.Length - 1}: {ex.Message}");
        }
        finally
        {
            block.ReleaseReference();
            Interlocked.Decrement(ref _pendingDiskWrites);
        }
    }

    // --- Network Retry Helpers ---

    /// <summary>
    /// Capped exponential backoff for transient network retry.
    /// Progression: 500ms → 1s → 2s → 4s → 5s (cap).
    /// </summary>
    private static int ComputeNetworkRetryBackoff(int consecutiveFailures)
    {
        int exponent = Math.Min(consecutiveFailures - 1, 3);
        int backoffMs = NetworkRetryBaseMs * (1 << Math.Max(0, exponent));
        return Math.Min(backoffMs, NetworkRetryMaxBackoffMs);
    }

    /// <summary>
    /// One-time network stall event publication when threshold is reached.
    /// </summary>
    private void CheckAndPublishNetworkStall(
        ref bool published, int consecutiveFailures, long position)
    {
        if (published || consecutiveFailures < NetworkStallThreshold) return;

        published = true;
        Log.Warn($"[CachingSource] [{_trackId}] Network stall at position {position} " +
                 $"after {consecutiveFailures} consecutive failures. " +
                 "Waiting for recovery (infinite retry)...");

        try { OnNetworkStalled?.Invoke(_trackId); }
        catch (Exception ex) { Log.Warn($"[CachingSource] NetworkStalled handler: {ex.Message}"); }
    }

    /// <summary>
    /// Publishes a network recovery event after a stall.
    /// </summary>
    private void PublishNetworkRecovered()
    {
        Log.Info($"[CachingSource] [{_trackId}] Network recovered — data flow restored");
        try { OnNetworkRecovered?.Invoke(_trackId); }
        catch (Exception ex) { Log.Warn($"[CachingSource] NetworkRecovered handler: {ex.Message}"); }
    }
}