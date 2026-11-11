using LMP.Core.Exceptions;

namespace LMP.Core.Audio.Sources;

internal sealed partial class CachingStreamSource
{
    // --- ActiveRangeDownload ---

    /// <summary>
    /// Active HTTP range download descriptor used for deduplication
    /// in the <see cref="_activeDownloads"/> registry.
    /// <para>
    /// A background download waits for a semaphore slot. A critical reader joining it
    /// promotes its priority via <see cref="PromoteToCritical"/>, and slot waiting terminates immediately.
    /// </para>
    /// </summary>
    private sealed class ActiveRangeDownload
    {
        private readonly TaskCompletionSource? _promotion;

        /// <summary>Start offset of the range to download (bytes).</summary>
        public long Start { get; }

        /// <summary>Length of the range to download (bytes).</summary>
        public int Length { get; }

        /// <summary>Exclusive right bound of the range.</summary>
        public long EndExclusive => Start + Length;

        /// <summary>Download was initiated by a critical reader and does not occupy a semaphore slot.</summary>
        public bool IsCritical { get; }

        /// <summary>
        /// Priority promotion signal: completes when a critical reader joins a background download.
        /// Already completed for critical downloads.
        /// </summary>
        public Task PromotionTask => _promotion?.Task ?? Task.CompletedTask;

        /// <summary>
        /// Lazy download task. Started on the first access to <see cref="Lazy{T}.Value"/>.
        /// <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/> guarantees a single execution.
        /// </summary>
        public Lazy<Task<RangeDownloadResult>> LazyTask { get; }

        /// <summary>
        /// Creates an active download descriptor.
        /// </summary>
        /// <param name="start">Range start offset.</param>
        /// <param name="length">Range length.</param>
        /// <param name="isCritical">Download priority at registration time.</param>
        /// <param name="lazyTask">Download task factory.</param>
        public ActiveRangeDownload(long start, int length, bool isCritical, Lazy<Task<RangeDownloadResult>> lazyTask)
        {
            Start = start;
            Length = length;
            IsCritical = isCritical;
            LazyTask = lazyTask;
            if (!isCritical)
                _promotion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>
        /// Promotes a background download to critical: semaphore slot waiting terminates immediately.
        /// Subsequent calls are safe.
        /// </summary>
        public void PromoteToCritical() => _promotion?.TrySetResult();
    }

    // --- Coordination Methods ---

    /// <summary>
    /// Returns the number of registered active downloads.
    /// </summary>
    private int GetActiveDownloadCount()
    {
        lock (_activeDownloadsLock) return _activeDownloads.Count;
    }

    /// <summary>
    /// Strict protection against overlapping downloads.
    /// Critically important on weak networks: prevents duplicate downloads of the same bytes.
    /// </summary>
    /// <param name="start">Start offset of the range to check.</param>
    /// <param name="length">Length of the range to check.</param>
    /// <param name="active">First overlapping download found; <c>null</c> if none.</param>
    /// <returns><c>true</c> if an overlap is found.</returns>
    private bool TryGetOverlappingActiveDownload(long start, int length, out ActiveRangeDownload? active)
    {
        long endExclusive = start + length;
        lock (_activeDownloadsLock)
        {
            foreach (var current in _activeDownloads.Values)
            {
                if (start < current.EndExclusive && endExclusive > current.Start)
                {
                    active = current;
                    return true;
                }
            }
        }
        active = null;
        return false;
    }

    /// <summary>
    /// Registers a download candidate in the active registry or returns
    /// an existing overlapping download (dedup).
    /// </summary>
    /// <param name="candidate">Candidate to register.</param>
    /// <returns>
    /// Returns <paramref name="candidate"/> if registration succeeded;
    /// otherwise, an existing download overlapping the same range.
    /// </returns>
    private ActiveRangeDownload RegisterOrGetActiveDownload(ActiveRangeDownload candidate)
    {
        long candidateEnd = candidate.EndExclusive;
        lock (_activeDownloadsLock)
        {
            foreach (var current in _activeDownloads.Values)
            {
                if (candidate.Start < current.EndExclusive && candidateEnd > current.Start)
                    return current;
            }

            if (_activeDownloads.TryGetValue(candidate.Start, out var sameStart))
                return sameStart;

            _activeDownloads.Add(candidate.Start, candidate);
            return candidate;
        }
    }

    /// <summary>
    /// Removes a download from the registry only if the current caller is its owner.
    /// Prevents removing another caller's download during race conditions.
    /// </summary>
    /// <param name="key">Download key (range start offset).</param>
    /// <param name="owner">Expected entry owner.</param>
    private void RemoveActiveDownloadIfOwner(long key, ActiveRangeDownload owner)
    {
        lock (_activeDownloadsLock)
        {
            if (_activeDownloads.TryGetValue(key, out var current) && ReferenceEquals(current, owner))
                _activeDownloads.Remove(key);
        }
    }

    /// <summary>
    /// Awaits completion of another caller's download task, suppressing all exceptions except
    /// <see cref="ChunkDownloadFatalException"/> and external cancellation.
    /// </summary>
    /// <param name="task">Task of the range owner.</param>
    /// <param name="ct">Caller cancellation token.</param>
    private static async Task WaitForActiveDownloadAsync(Task task, CancellationToken ct)
    {
        try { await task.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (ChunkDownloadFatalException) { throw; }
        catch { }
    }

    /// <summary>
    /// Joins another caller's range download. A critical reader promotes
    /// its priority beforehand to avoid waiting for a semaphore slot behind background preload (priority inversion).
    /// </summary>
    /// <param name="active">Download owner of the overlapping range.</param>
    /// <param name="isCritical">Priority of the joining reader.</param>
    /// <param name="ct">Caller cancellation token.</param>
    private static Task JoinActiveDownloadAsync(ActiveRangeDownload active, bool isCritical, CancellationToken ct)
    {
        if (isCritical)
            active.PromoteToCritical();

        return WaitForActiveDownloadAsync(active.LazyTask.Value, ct);
    }

    /// <summary>Result of background download slot wait.</summary>
    internal enum SlotWaitOutcome : byte
    {
        /// <summary>Slot acquired; caller must release it back to the semaphore.</summary>
        Acquired,

        /// <summary>Priority promoted to critical: download proceeds without a slot.</summary>
        Promoted,

        /// <summary>Slot was not acquired within the allocated time.</summary>
        TimedOut,

        /// <summary>Wait canceled by token.</summary>
        Cancelled
    }

    /// <summary>
    /// Waits for a semaphore slot or a priority promotion signal without throwing exceptions.
    /// If promoted earlier, abandoned waiting will return the slot automatically when acquired.
    /// </summary>
    /// <param name="slots">Concurrent background download semaphore.</param>
    /// <param name="timeoutMs">Maximum slot wait time (ms).</param>
    /// <param name="promotion">Priority promotion signal.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Wait outcome.</returns>
    internal static async Task<SlotWaitOutcome> WaitForSlotOrPromotionAsync(
        SemaphoreSlim slots, int timeoutMs, Task promotion, CancellationToken ct)
    {
        if (promotion.IsCompleted)
            return SlotWaitOutcome.Promoted;

        Task<bool> slotTask = slots.WaitAsync(timeoutMs, ct);
        if (!slotTask.IsCompleted)
            _ = await Task.WhenAny(slotTask, promotion).ConfigureAwait(false);

        if (!slotTask.IsCompleted)
        {
            ReturnSlotWhenGranted(slotTask, slots);
            return SlotWaitOutcome.Promoted;
        }

        if (!slotTask.IsCompletedSuccessfully)
        {
            // Observe the canceled wait exception to prevent UnobservedTaskException.
            _ = slotTask.Exception;
            return SlotWaitOutcome.Cancelled;
        }

        if (slotTask.Result)
            return SlotWaitOutcome.Acquired;

        return promotion.IsCompleted ? SlotWaitOutcome.Promoted : SlotWaitOutcome.TimedOut;
    }

    /// <summary>
    /// Returns a slot to the semaphore if the abandoned wait task acquired it later.
    /// </summary>
    /// <param name="slotTask">Abandoned slot wait task.</param>
    /// <param name="slots">Semaphore owning the slot.</param>
    private static void ReturnSlotWhenGranted(Task<bool> slotTask, SemaphoreSlim slots)
    {
        _ = slotTask.ContinueWith(
            static (task, state) =>
            {
                if (!task.IsCompletedSuccessfully)
                {
                    _ = task.Exception;
                    return;
                }

                if (!task.Result)
                    return;

                try { ((SemaphoreSlim)state!).Release(); }
                catch (ObjectDisposedException) { }
            },
            slots,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    // --- Helpers ---

    /// <summary>
    /// Determines whether an <see cref="HttpRequestException"/> is a consequence of operation cancellation
    /// (client dispose, epoch cancel, socket abort) rather than a genuine network error.
    /// </summary>
    /// <param name="exception">Caught exception.</param>
    /// <param name="ct">Caller cancellation token.</param>
    /// <param name="disposed">Source disposed flag.</param>
    /// <returns><c>true</c> if the exception is a cancellation artifact.</returns>
    private static bool IsCancelledSendFailure(HttpRequestException exception, CancellationToken ct, bool disposed)
    {
        if (disposed || ct.IsCancellationRequested) return true;
        Exception? current = exception;
        while (current != null)
        {
            switch (current)
            {
                case ObjectDisposedException:
                    return true;
                case System.Net.Sockets.SocketException socketEx
                    when socketEx.SocketErrorCode is System.Net.Sockets.SocketError.OperationAborted
                                                  or System.Net.Sockets.SocketError.Interrupted:
                    return true;
            }
            current = current.InnerException;
        }
        return false;
    }
}