using LMP.Core.Exceptions;

namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    #region Suspend/Resume

    /// <summary>
    /// Suspends background prefetching (when window is minimized).
    /// Critical read-ahead continues working to ensure seamless playback.
    /// </summary>
    public void Suspend()
    {
        _suspendGate.Reset();
        Log.Debug("[CachingSource] Suspended (background fill paused, critical read-ahead active)");
    }

    /// <summary>
    /// Resumes background prefetching.
    /// </summary>
    public void Resume()
    {
        _suspendGate.Set();
        Log.Debug("[CachingSource] Resumed");
    }

    #endregion

    #region Preload Loop

    /// <summary>
    /// Background range prefetch loop.
    /// <para>
    /// The first iteration runs without delay to immediately pick up
    /// the work of <see cref="SafeStartupPrefetchAsync"/> and continue filling the buffer.
    /// Subsequent iterations are separated by <see cref="StreamingConfig.PreloadIntervalMs"/>.
    /// </para>
    /// <para>
    /// Adapts parallelism and request size to network condition.
    /// Includes opportunistic completion: if the track is almost downloaded,
    /// completes the remaining gaps until fully cached.
    /// </para>
    /// </summary>
    private async Task PreloadLoopAsync(CancellationToken ct)
    {
        bool justResumed = false;

        /// <summary>
        /// "Almost downloaded" threshold — if >= this percentage is downloaded, completion fill is enabled.
        /// </summary>
        const double CompletionFillThresholdPercent = 90.0;

        /// <summary>
        /// Maximum absolute size of remaining un-downloaded data to trigger completion fill.
        /// </summary>
        const long CompletionFillMaxRemainingBytes = 512 * 1024;

#if DEBUG
        int lastReportedProgress = -1;
#endif

        while (!ct.IsCancellationRequested && _cacheEntry is { IsComplete: false })
        {
            try
            {
                bool isSuspended = !_suspendGate.IsSet;
                bool isPaused = !_playbackGate.IsSet;

                // Paused: waiting for gate to open without periodic idle wakeups
                if (isPaused)
                {
                    try { _playbackGate.Wait(ct); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                // Suspend mode: critical read-ahead only 
                if (isSuspended)
                {
                    long current = Volatile.Read(ref _currentReadOffset);
                    long bufferedAhead = GetBufferedBytesAheadIncludingInflight(current);

                    if (bufferedAhead < _config.MinRequestSizeBytes)
                    {
                        long nextPos = current + bufferedAhead;
                        int nextLen = GetAlignedReadLength(nextPos, _config.MinRequestSizeBytes);

                        try
                        {
                            await EnsureRangeAsync(nextPos, nextLen, ct, isCritical: true)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { break; }
                        catch (ChunkDownloadFatalException ex)
                        {
                            if (ex.Reason is ChunkDownloadFailureReason.UmpFormat
                                          or ChunkDownloadFailureReason.Forbidden403)
                            {
                                Log.Warn($"[CachingSource] Permanent preload fatal in suspend: {ex.Reason}");
                                break;
                            }

                            Log.Debug($"[CachingSource] Transient preload fatal in suspend, " +
                                      $"backing off: {ex.Message}");
                            if (!await ct.DelayNoThrowAsync(3000, continueOnCapturedContext: false))
                                break;
                        }
                        catch (Exception ex)
                        {
                            Log.Debug($"[CachingSource] Suspended critical preload: {ex.Message}");
                        }
                    }

                    int bufferedAheadMs = ConvertBufferedBytesToMs(bufferedAhead);
                    int suspendWaitMs = bufferedAheadMs > 4000
                        ? 2000
                        : bufferedAheadMs > 2000
                            ? 1000
                            : PlaybackGateCriticalTimeoutMs;

                    try { _suspendGate.Wait(suspendWaitMs, ct); }
                    catch (OperationCanceledException) { break; }

                    if (_suspendGate.IsSet)
                        justResumed = true;

                    continue;
                }

                // Critical network seek operation in progress — yielding full channel bandwidth to it
                if (_seekInProgress)
                {
                    if (!await ct.DelayNoThrowAsync(_config.PreloadIntervalMs, continueOnCapturedContext: false))
                        break;
                    continue;
                }

                // Post-resume debounce 
                if (justResumed)
                {
                    justResumed = false;
                    if (!await ct.DelayNoThrowAsync(_config.PreloadIntervalMs, continueOnCapturedContext: false))
                        break;
                    continue;
                }

                if (_cacheEntry.IsComplete) break;

                // Snapshot of the current state 
                long epochAtLoopStart = Interlocked.Read(ref _downloadEpoch);
                var token = CurrentDownloadToken;
                long currentOffset = Volatile.Read(ref _currentReadOffset);

                int adaptiveMaxDownloads = GetAdaptiveMaxConcurrentDownloads();
                int pending = GetActiveDownloadCount();

                if (pending >= adaptiveMaxDownloads)
                {
                    if (!await ct.DelayNoThrowAsync(_config.PreloadIntervalMs, continueOnCapturedContext: false).ConfigureAwait(false))
                        break;
                    continue;
                }

                int adaptiveTargetBufferMs = GetAdaptiveTargetBufferMs();
                int refillStopMs = adaptiveTargetBufferMs + TargetBufferHysteresisMs;

                var degradation = GetNetworkDegradationLevel();

                // Prefetch loop: schedule requests ahead from current position 
                while (pending < adaptiveMaxDownloads)
                {
                    if (Interlocked.Read(ref _downloadEpoch) != epochAtLoopStart)
                        break;

                    long plannedAheadBytes = GetBufferedBytesAheadIncludingInflight(currentOffset);
                    int plannedAheadMs = ConvertBufferedBytesToMs(plannedAheadBytes);

                    if (plannedAheadMs >= refillStopMs)
                        break;

                    long nextPosition = currentOffset + plannedAheadBytes;
                    if (nextPosition >= _contentLength)
                        break;

                    int requestBytes = GetAlignedReadLength(nextPosition, _config.MinRequestSizeBytes);
                    if (requestBytes <= 0)
                        break;

                    bool critical =
                        plannedAheadMs < EmergencyRefillBufferMs
                        || (pending == 0 && plannedAheadMs < CriticalRefillBufferMs);

                    _ = SafeEnsureRangeAsync(nextPosition, requestBytes, token, critical);
                    pending++;

                    if (degradation != NetworkDegradationLevel.Normal)
                        break;

                    if (!critical)
                        break;
                }

                // Opportunistic completion fill: run ONLY when the entire tail from current position
                // to end of file is fully downloaded and there are no active downloads (pending == 0),
                // to avoid creating competing HTTP requests behind the cursor during seeks.
                long bufferedToTail = GetBufferedBytesAheadIncludingInflight(currentOffset);
                bool isTailFullyBuffered = currentOffset + bufferedToTail >= _contentLength;

                if (pending == 0 && isTailFullyBuffered && !_cacheEntry.IsComplete)
                {
                    long downloadedBytes = _cacheEntry.DownloadedBytes;
                    long totalSize = _cacheEntry.TotalSize;
                    long remainingBytes = totalSize - downloadedBytes;

                    bool shouldCompletionFill = totalSize > 0
                        && ((_cacheEntry.DownloadProgress >= CompletionFillThresholdPercent)
                            || (remainingBytes > 0 && remainingBytes <= CompletionFillMaxRemainingBytes));

                    if (shouldCompletionFill)
                    {
                        await TryCompletionFillAsync(token, maxRequests: 1)
                            .ConfigureAwait(false);
                    }
                }

                // Trim RAM cache 
                if (_ramCache.TotalBytes > _config.MaxRamBytes)
                    ReleaseRamBuffers();

#if DEBUG
                int progress = (int)(_cacheEntry?.DownloadProgress ?? 0);
                if (progress != lastReportedProgress)
                {
                    Log.Debug($"[CachingSource] Progress: {progress}% " +
                              $"({_cacheEntry?.DownloadedBytes ?? 0}/{_cacheEntry?.TotalSize ?? 0} bytes)");
                    lastReportedProgress = progress;
                }
#endif

                if (!await ct.DelayNoThrowAsync(_config.PreloadIntervalMs, continueOnCapturedContext: false).ConfigureAwait(false))
                    break;
            }
            catch (OperationCanceledException) { break; }
            catch { /* Protection against unexpected exceptions in the background loop */ }
        }
    }

    /// <summary>
    /// Scans all downloaded ranges and fills the first gaps found.
    /// </summary>
    /// <param name="downloadToken">Current download epoch token.</param>
    /// <param name="maxRequests">Maximum number of concurrent requests.</param>
    private async Task TryCompletionFillAsync(
        CancellationToken downloadToken,
        int maxRequests)
    {
        if (_cacheEntry == null || _cacheEntry.IsComplete || maxRequests <= 0)
            return;

        long totalSize = _cacheEntry.TotalSize;
        if (totalSize <= 0) return;

        var ranges = _cacheEntry.GetDownloadedRangesSnapshot();
        int fired = 0;

        long scanPosition = 0;

        for (int i = 0; i <= ranges.Length && fired < maxRequests; i++)
        {
            long gapStart;
            long gapEnd;

            if (i < ranges.Length)
            {
                gapStart = scanPosition;
                gapEnd = ranges[i].Start;
                scanPosition = ranges[i].EndExclusive;
            }
            else
            {
                gapStart = scanPosition;
                gapEnd = totalSize;
            }

            if (gapEnd <= gapStart)
                continue;

            long gapLength = gapEnd - gapStart;
            if (gapLength <= 0)
                continue;

            // Protection against end-of-file spam: if this is a tail gap after the last range
            // and it is smaller than alignment block size — server has delivered full file, stop retrying
            if (i == ranges.Length && gapLength <= _requestAlignmentBytes * 2 && ranges.Length > 0)
                continue;

            int requestLength = (int)Math.Min(gapLength, _config.MaxRequestSizeBytes);

            _ = SafeEnsureRangeAsync(gapStart, requestLength, downloadToken, isCritical: false);
            fired++;
        }

#if DEBUG
        if (fired > 0)
        {
            Log.Debug($"[CachingSource] Completion fill: {fired} gap request(s) fired " +
                      $"({_cacheEntry.DownloadProgress:F0}% → 100%)");
        }
#endif
    }

    /// <summary>
    /// Safe wrapper around <see cref="EnsureRangeAsync"/> for fire-and-forget calls.
    /// </summary>
    private async Task SafeEnsureRangeAsync(
        long position, int minimumLength, CancellationToken ct, bool isCritical)
    {
        try
        {
            await EnsureRangeAsync(position, minimumLength, ct, isCritical).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ChunkDownloadFatalException ex)
        {
            Log.Debug($"[Preload] Range {position}: fatal: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Debug($"[Preload] Range {position}: {ex.Message}");
        }
    }

    #endregion

    #region Buffer Ranges

    /// <summary>
    /// Merges sorted disk ranges and RAM blocks into normalized segments <c>[0.0, 1.0]</c>
    /// in a single linear pass, without intermediate collections or sorting.
    /// </summary>
    /// <param name="diskRanges">Disk ranges, sorted by start offset.</param>
    /// <param name="ramBlocks">RAM blocks, sorted by start offset.</param>
    /// <param name="contentLength">Total content length (bytes), greater than zero.</param>
    /// <param name="destination">
    /// Result buffer at least as long as count mode value; an empty buffer enables count mode.
    /// </param>
    /// <returns>Number of merged segments.</returns>
    internal static int MergeBufferedRanges(
        ReadOnlySpan<LMP.Core.Audio.Cache.CacheByteRange> diskRanges,
        ReadOnlySpan<RamRangeBlock> ramBlocks,
        long contentLength,
        Span<(double Start, double End)> destination)
    {
        bool countOnly = destination.IsEmpty;
        int count = 0;
        int diskIndex = 0;
        int ramIndex = 0;
        long mergedStart = -1;
        long mergedEnd = -1;

        while (diskIndex < diskRanges.Length || ramIndex < ramBlocks.Length)
        {
            long start;
            long end;

            if (ramIndex >= ramBlocks.Length
                || (diskIndex < diskRanges.Length && diskRanges[diskIndex].Start <= ramBlocks[ramIndex].StartOffset))
            {
                start = diskRanges[diskIndex].Start;
                end = diskRanges[diskIndex].EndExclusive;
                diskIndex++;
            }
            else
            {
                start = ramBlocks[ramIndex].StartOffset;
                end = ramBlocks[ramIndex].EndOffsetExclusive;
                ramIndex++;
            }

            if (mergedStart < 0)
            {
                mergedStart = start;
                mergedEnd = end;
                continue;
            }

            if (start <= mergedEnd)
            {
                if (end > mergedEnd)
                    mergedEnd = end;
                continue;
            }

            if (!countOnly)
                destination[count] = ((double)mergedStart / contentLength, (double)mergedEnd / contentLength);
            count++;
            mergedStart = start;
            mergedEnd = end;
        }

        if (mergedStart >= 0)
        {
            if (!countOnly)
                destination[count] = ((double)mergedStart / contentLength, (double)mergedEnd / contentLength);
            count++;
        }

        return count;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Combines ranges from disk cache and RAM cache,
    /// normalizing them to <c>[0.0, 1.0]</c> relative to <see cref="_contentLength"/>.
    /// Rebuild occurs only on version change and allocates only snapshots and an exact-sized result.
    /// </remarks>
    public IReadOnlyList<(double Start, double End)> GetBufferedRanges()
    {
        var entry = _cacheEntry;
        if (entry == null || _contentLength <= 0)
            return [];

        if (entry.IsComplete)
        {
            if (_cachedBufferedRanges.Length > 0)
            {
                lock (_bufferedRangesCacheLock)
                {
                    _cachedBufferedRanges = [];
                }
            }
            return AudioConstants.FullBufferedRanges;
        }

        long diskVer = entry.RangesVersion;
        long ramVer = _ramCache.Version;

        lock (_bufferedRangesCacheLock)
        {
            if (diskVer == _cachedDiskRangesVersion && ramVer == _cachedRamRangesVersion)
                return _cachedBufferedRanges;
        }

        var diskRanges = entry.GetDownloadedRangesSnapshot(out long snapDiskVer);
        var ramBlocks = _ramCache.GetRangesSnapshot(out long snapRamVer);

        // Both snapshots are sorted by start offset: first pass counts segments, second pass fills exact array.
        int count = MergeBufferedRanges(diskRanges, ramBlocks, _contentLength, []);
        (double Start, double End)[] merged = [];
        if (count > 0)
        {
            merged = new (double Start, double End)[count];
            _ = MergeBufferedRanges(diskRanges, ramBlocks, _contentLength, merged);
        }

        lock (_bufferedRangesCacheLock)
        {
            _cachedDiskRangesVersion = snapDiskVer;
            _cachedRamRangesVersion = snapRamVer;
            _cachedBufferedRanges = merged;
        }
            return _cachedBufferedRanges;
        }

    #endregion
}