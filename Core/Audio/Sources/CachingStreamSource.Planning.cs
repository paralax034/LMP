namespace LMP.Core.Audio.Sources;

internal sealed partial class CachingStreamSource
{
    // --- DownloadPlan ---

    /// <summary>
    /// The result of planning the next HTTP range request.
    /// </summary>
    private readonly record struct DownloadPlan(long Start, int Length);

    // --- Alignment Helpers ---

    /// <summary>Aligns <paramref name="value"/> down to a multiple of <paramref name="alignment"/>.</summary>
    private static long AlignDown(long value, int alignment) =>
        value - (value % alignment);

    /// <summary>Aligns <paramref name="value"/> up to a multiple of <paramref name="alignment"/>.</summary>
    private static int AlignUp(int value, int alignment)
    {
        int remainder = value % alignment;
        return remainder == 0 ? value : value + (alignment - remainder);
    }

    /// <summary>Aligns <paramref name="value"/> up to a multiple of <paramref name="alignment"/>.</summary>
    private static long AlignUp(long value, int alignment)
    {
        long remainder = value % alignment;
        return remainder == 0 ? value : value + (alignment - remainder);
    }

    /// <summary>
    /// Returns the request length from <paramref name="planStart"/> that covers the entire range
    /// <c>[requestedPosition, requestedPosition + minimumLength)</c> even for an unaligned position, without exceeding content length.
    /// </summary>
    /// <param name="requestedPosition">The position where the reader expects data.</param>
    /// <param name="minimumLength">Minimum bytes required by the reader from <paramref name="requestedPosition"/>.</param>
    /// <param name="planStart">Aligned start of the planned request.</param>
    /// <param name="contentLength">Total content length.</param>
    /// <returns>Length in bytes; <c>0</c> if <paramref name="planStart"/> lies beyond the content end.</returns>
    internal static int ComputeCriticalSpan(long requestedPosition, int minimumLength, long planStart, long contentLength)
    {
        long available = contentLength - planStart;
        if (available <= 0) return 0;

        long required = Math.Max(minimumLength, requestedPosition + minimumLength - planStart);
        return (int)Math.Min(required, available);
    }

    // --- Buffer Queries ---

    /// <summary>
    /// Returns the number of contiguous bytes forward from <paramref name="position"/>
    /// in locally available data (RAM or disk).
    /// </summary>
    /// <param name="position">Starting position.</param>
    private long GetBufferedBytesAhead(long position)
    {
        long ramBytes = _ramCache.GetContiguousBytesFrom(position);
        long diskBytes = _cacheEntry?.GetContiguousDownloadedBytesFrom(position) ?? 0;
        return ramBytes >= diskBytes ? ramBytes : diskBytes;
    }

    /// <summary>
    /// Returns the number of contiguous bytes forward from <paramref name="position"/>,
    /// including ranges covered by active in-flight downloads.
    /// </summary>
    /// <param name="position">Starting position.</param>
    private long GetBufferedBytesAheadIncludingInflight(long position)
    {
        long localBuffered = GetBufferedBytesAhead(position);
        long endExclusive = position + localBuffered;
        if (endExclusive >= _contentLength) return _contentLength - position;

        lock (_activeDownloadsLock)
        {
            if (_activeDownloads.Count == 0)
                return endExclusive - position;

            bool extended;
            do
            {
                extended = false;
                foreach (var active in _activeDownloads.Values)
                {
                    if (active.Start > endExclusive || active.EndExclusive <= endExclusive || active.EndExclusive <= position)
                        continue;
                    endExclusive = active.EndExclusive;
                    extended = true;
                }
            }
            while (extended && endExclusive < _contentLength);
        }

        return endExclusive - position;
    }

    // --- Availability Queries ---

    /// <summary>
    /// Checks whether the range is available locally (RAM or disk).
    /// </summary>
    /// <param name="position">Range start.</param>
    /// <param name="length">Range length.</param>
    private bool IsRangeLocallyAvailable(long position, int length)
    {
        if (length <= 0) return true;
        if (position < 0 || position >= _contentLength) return false;
        if (_ramCache.ContainsRange(position, length)) return true;
        return _cacheEntry?.IsRangeDownloaded(position, length) == true;
    }

    // --- Read Length ---

    /// <summary>
    /// Returns the aligned read length from <paramref name="position"/> to the end of the content.
    /// </summary>
    /// <param name="position">Starting read position.</param>
    /// <param name="minimumLength">Minimum allowed length.</param>
    private int GetAlignedReadLength(long position, int minimumLength)
    {
        if (position >= _contentLength) return 0;
        long remaining = _contentLength - position;
        int aligned = AlignUp(Math.Max(minimumLength, _config.MinRequestSizeBytes), _requestAlignmentBytes);
        if (aligned > remaining) aligned = (int)remaining;
        return aligned;
    }

    // --- Coverage Trim ---

    /// <summary>
    /// Truncates the planned download length to the first known aligned coverage ahead.
    /// </summary>
    /// <param name="start">Range start.</param>
    /// <param name="length">Original range length.</param>
    /// <param name="includeInflight">
    /// <c>true</c> — treat in-flight downloads as coverage;
    /// <c>false</c> — consider only locally available data.
    /// </param>
    /// <returns>
    /// Length of the first contiguous gap from <paramref name="start"/> to the nearest existing
    /// aligned range. If there is no coverage ahead, returns the original <paramref name="length"/>.
    /// </returns>
    private int TrimLengthToFirstKnownCoverage(long start, int length, bool includeInflight)
    {
        if (length <= _requestAlignmentBytes) return length;

        long endExclusive = start + length;
        long minProbe = start + _requestAlignmentBytes;

        long earliest = _ramCache.FindFirstCoveredProbeOffset(minProbe, endExclusive, _requestAlignmentBytes, start);

        if (_cacheEntry != null)
        {
            long diskFirst = _cacheEntry.FindFirstCoveredProbeOffset(minProbe, endExclusive, _requestAlignmentBytes, start);
            if (diskFirst < earliest) earliest = diskFirst;
        }

        if (includeInflight)
        {
            lock (_activeDownloadsLock)
            {
                foreach (var active in _activeDownloads.Values)
                {
                    if (active.Start >= endExclusive || active.EndExclusive <= minProbe)
                        continue;

                    long candidate = active.Start <= minProbe
                        ? minProbe
                        : start + AlignUp(active.Start - start, _requestAlignmentBytes);

                    if (candidate >= endExclusive || candidate >= earliest)
                        continue;

                    long probeEnd = Math.Min(endExclusive, candidate + _requestAlignmentBytes);
                    if (active.Start <= candidate && active.EndExclusive >= probeEnd)
                        earliest = candidate;
                }
            }
        }

        return earliest < endExclusive ? (int)(earliest - start) : length;
    }

    // --- Throttle ---

    /// <summary>
    /// Calculates the target download speed for throttling mode (bytes/sec).
    /// Returns <c>0</c> if throttling is disabled (<see cref="StreamingConfig.ThrottleMultiplier"/> &lt;= 0).
    /// </summary>
    private double GetThrottleTargetBytesPerSec()
    {
        double multiplier = _config.ThrottleMultiplier;
        if (multiplier <= 0) return 0;
        double bitrateBytesPerSec = Math.Max(1, _bitrate) * 1000.0 / 8.0;
        return bitrateBytesPerSec * multiplier;
    }

    // --- MAPO Planner ---

    /// <summary>
    /// Calculates the optimal range for the next HTTP range request (MAPO planner).
    /// <para>
    /// The algorithm operates in two modes depending on the buffer state:
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     <b>Startup/seek phase</b> (<c>currentBufferSec &lt; BdpFloorMaxBufferMs</c>):
    ///     BDP floor is activated — request at least one Bandwidth-Delay Product
    ///     to maximize link utilization and quickly fill the buffer.
    ///   </item>
    ///   <item>
    ///     <b>Steady-state phase</b> (sufficient buffer): pure demand-based sizing
    ///     using the BBA (Buffer-Based Adaptation) model. BDP floor is not applied —
    ///     downloading just enough to maintain
    ///     <see cref="StreamingConfig.TargetBufferMs"/> is sufficient.
    ///   </item>
    /// </list>
    /// <para>
    /// All bandwidth-based estimates apply
    /// <see cref="StreamingConfig.ThroughputSafetyFactor"/> (≤ 1.0) following the
    /// THROUGHPUT model (dash.js) — reserving part of the bandwidth to avoid saturating the link.
    /// </para>
    /// </summary>
    private DownloadPlan BuildDownloadPlan(long requestedPosition, int minimumLength, bool isCritical)
    {
        long start = AlignDown(requestedPosition, _requestAlignmentBytes);

        long localAvailable = GetBufferedBytesAhead(start);
        if (localAvailable > 0)
        {
            long adjustedStart = AlignUp(start + localAvailable, _requestAlignmentBytes);
            if (adjustedStart < _contentLength && adjustedStart <= requestedPosition + minimumLength)
                start = adjustedStart;
        }

        // Unaligned position: plan from AlignDown(position) must cover the head, otherwise the window tail causes a second critical GET
        int requiredSpan = ComputeCriticalSpan(requestedPosition, minimumLength, start, _contentLength);
        int minLengthAligned = AlignUp(Math.Max(requiredSpan, _config.MinRequestSizeBytes), _requestAlignmentBytes);

        long currentPosition = Volatile.Read(ref _currentReadOffset);
        if (currentPosition < 0 || currentPosition >= _contentLength)
            currentPosition = requestedPosition;

        long bufferedAheadBytes = GetBufferedBytesAheadIncludingInflight(currentPosition);
        double bitrateBytesPerSec = Math.Max(1, _bitrate) * 1000.0 / 8.0;
        double currentBufferSec = bufferedAheadBytes / bitrateBytesPerSec;

        int adaptiveTargetBufferMs = GetAdaptiveTargetBufferMs();
        double targetBufferSec = adaptiveTargetBufferMs / 1000.0;
        double demandBytes = bitrateBytesPerSec * Math.Max(0, targetBufferSec - currentBufferSec);

        double avgLatencyMs;
        double estimatedBandwidth;
        lock (_latencyLock)
        {
            avgLatencyMs = GetAverageLatencyInternal();
            estimatedBandwidth = _estimatedBandwidthBytesPerSec;
        }

        var degradation = GetNetworkDegradationLevel();
        int effectiveMaxLength = _config.MaxRequestSizeBytes;

        if (!isCritical)
        {
            effectiveMaxLength = degradation switch
            {
                NetworkDegradationLevel.Critical => Math.Min(effectiveMaxLength, _requestAlignmentBytes * 2),
                NetworkDegradationLevel.Degraded => Math.Min(effectiveMaxLength, _requestAlignmentBytes * 4),
                _ => effectiveMaxLength
            };
        }
        else
        {
            effectiveMaxLength = degradation switch
            {
                NetworkDegradationLevel.Critical => Math.Min(effectiveMaxLength, _requestAlignmentBytes * 4),
                NetworkDegradationLevel.Degraded => Math.Min(effectiveMaxLength, _requestAlignmentBytes * 6),
                _ => effectiveMaxLength
            };
        }

        double throttleBps = GetThrottleTargetBytesPerSec();
        if (throttleBps > 0)
        {
            double pacingWindow = Math.Max(1.0, _config.PreloadIntervalMs / 1000.0);
            int throttleCap = AlignUp((int)(throttleBps * pacingWindow), _requestAlignmentBytes);
            effectiveMaxLength = Math.Min(throttleCap, effectiveMaxLength);
        }

        int maxLength = Math.Max(minLengthAligned, effectiveMaxLength);

        // Throughput safety factor (THROUGHPUT model, dash.js):
        // do not claim 100% bandwidth, reserve margin for parallel requests.
        double safeBytes = 0;
        if (estimatedBandwidth > 0)
        {
            double effectiveBandwidth = estimatedBandwidth * _config.ThroughputSafetyFactor;
            double timeLeftSec = Math.Max(0.050, currentBufferSec - (avgLatencyMs / 1000.0));
            safeBytes = effectiveBandwidth * timeLeftSec;
        }

        long selectedBytes;
        if (safeBytes > 0 && demandBytes > 0) selectedBytes = (long)Math.Min(demandBytes, safeBytes);
        else if (demandBytes > 0) selectedBytes = (long)demandBytes;
        else if (safeBytes > 0) selectedBytes = (long)safeBytes;
        else selectedBytes = minLengthAligned;

        if (selectedBytes < minLengthAligned) selectedBytes = minLengthAligned;

        // BDP floor (startup/seek phase only):
        // activate on an empty buffer to maximize link saturation in a single RTT.
        bool isStartupOrSeekPhase = currentBufferSec < (_config.BdpFloorMaxBufferMs / 1000.0);
        if (estimatedBandwidth > 0 && avgLatencyMs > 600 && isStartupOrSeekPhase)
        {
            double latencySec = avgLatencyMs / 1000.0;
            long bdpBytes = (long)(estimatedBandwidth * _config.ThroughputSafetyFactor * latencySec);
            long bdpFloor = AlignUp(Math.Max(minLengthAligned, bdpBytes * 2), _requestAlignmentBytes);
            if (bdpFloor > selectedBytes) selectedBytes = bdpFloor;
        }

        int bufferedAheadMs = ConvertBufferedBytesToMs(bufferedAheadBytes);
        bool lowBuffer = bufferedAheadMs < CriticalRefillBufferMs;

        if (!isCritical && !lowBuffer && avgLatencyMs < 800 && demandBytes > 0)
        {
            long demandAligned = AlignUp((long)demandBytes, _requestAlignmentBytes);
            long softCap = demandAligned + _requestAlignmentBytes;
            if (selectedBytes > softCap) selectedBytes = softCap;
        }

        if (isCritical || lowBuffer)
        {
            long criticalFloor = minLengthAligned;
            if (avgLatencyMs > 1200)
                criticalFloor = Math.Max(criticalFloor, Math.Min(maxLength, minLengthAligned * 2L));
            if (selectedBytes < criticalFloor) selectedBytes = criticalFloor;
        }

        if (selectedBytes > maxLength) selectedBytes = maxLength;
        selectedBytes = AlignUp(selectedBytes, _requestAlignmentBytes);

        long remaining = _contentLength - start;
        if (remaining <= 0) return new DownloadPlan(start, 0);

        if (selectedBytes > remaining) selectedBytes = remaining;
        if (selectedBytes < requiredSpan)
            selectedBytes = Math.Min(AlignUp(requiredSpan, _requestAlignmentBytes), remaining);

        int plannedLength = (int)selectedBytes;

        int trimmedLength = TrimLengthToFirstKnownCoverage(start, plannedLength, includeInflight: true);
        if (trimmedLength > 0 && trimmedLength < plannedLength)
            plannedLength = trimmedLength;

        return new DownloadPlan(start, plannedLength);
    }
}