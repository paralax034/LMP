namespace LMP.Core.Audio.Sources;

internal sealed partial class CachingStreamSource
{
    // --- Epoch-Based Cancellation ---

    /// <summary>
    /// Defers <see cref="IDisposable.Dispose"/> for the CTS,
    /// preventing <see cref="ObjectDisposedException"/> in concurrent execution paths.
    /// </summary>
    private sealed class DeferredCtsTimerCleanup(CancellationTokenSource cts)
    {
        private Timer? _timer;

        public void Schedule(int delayMs) =>
            _timer = new Timer(static state =>
            {
                var self = (DeferredCtsTimerCleanup)state!;
                try { self._timer?.Dispose(); } catch { }
                try { self.Cts.Dispose(); } catch (ObjectDisposedException) { }
            }, this, delayMs, Timeout.Infinite);

        private CancellationTokenSource Cts => cts;
    }

    private static void DeferDisposeCancellationTokenSource(
        CancellationTokenSource? cts, int delayMs)
    {
        if (cts == null) return;
        new DeferredCtsTimerCleanup(cts).Schedule(delayMs);
    }

    /// <summary>Cancels all downloads for the current epoch and creates a new one.</summary>
    private CancellationToken ResetDownloadEpoch()
    {
        lock (_epochLock)
        {
            var oldCts = _downloadCts;

            _downloadCts = _lifetimeCts != null
                ? CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token)
                : new CancellationTokenSource();

            Interlocked.Increment(ref _downloadEpoch);

            if (oldCts != null)
            {
                ThreadPool.UnsafeQueueUserWorkItem(static state =>
                {
                    try
                    {
                        ((CancellationTokenSource)state!).Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }, oldCts);

                DeferDisposeCancellationTokenSource(oldCts, DeferredEpochDisposeDelayMs);
            }

            return _downloadCts.Token;
        }
    }

    /// <summary>Initializes the first loading epoch.</summary>
    private void InitializeFirstEpoch()
    {
        lock (_epochLock)
        {
            _downloadCts = _lifetimeCts != null
                ? CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token)
                : new CancellationTokenSource();
            _downloadEpoch = 1;
        }
    }

    /// <summary>CancellationToken for the current loading epoch. Thread-safe.</summary>
    private CancellationToken CurrentDownloadToken
    {
        get
        {
            lock (_epochLock)
                return _downloadCts?.Token ?? CancellationToken.None;
        }
    }

    /// <summary>Instantly cancels active stream reads without disposing the source.</summary>
    public void CancelActiveReads() => _readStream?.CancelActiveReads();

    // --- Dispose ---

    /// <summary>Common dispose preamble: unblocking gates, canceling epoch + lifetime.</summary>
    private void BeginDispose()
    {
        _suspendGate.Set();
        _playbackGate.Set();

        CancellationTokenSource? downloadCtsToDispose;

        lock (_epochLock)
        {
            downloadCtsToDispose = _downloadCts;
            _downloadCts = null;
        }

        if (downloadCtsToDispose != null)
        {
            try
            {
                downloadCtsToDispose.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            DeferDisposeCancellationTokenSource(downloadCtsToDispose, DeferredEpochDisposeDelayMs);
        }

        try
        {
            _lifetimeCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Common dispose epilogue: releasing all resources.</summary>
    private void DisposeSharedResources()
    {
        try
        {
            _lifetimeCts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        _readStream?.Dispose();
        DisposeAllRamChunks();

        lock (_continuationLock)
        {
            var tcs = _continuationUrlTcs;
            _continuationUrlTcs = null;
            tcs?.TrySetResult(null);
        }

        try
        {
            _refreshLock.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        _suspendGate.Set();
        _playbackGate.Set();

        if (_leaseAcquired)
            _cacheManager.ReleaseLease(_cacheKey);
    }

    /// <summary>Disposes all blocks in the RAM cache.</summary>
    private void DisposeAllRamChunks() => _ramCache.DisposeAll();

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        BeginDispose();
        _parser?.Dispose();
        DrainPendingDiskWritesSync();
        DisposeSharedResources();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        BeginDispose();

        if (_preloadTask != null)
        {
            try
            {
                await _preloadTask
                    .WaitAsync(TimeSpan.FromMilliseconds(PreloadTaskDisposeWaitTimeoutMs))
                    .ConfigureAwait(false);
            }
            catch
            {
            }
        }

        if (Volatile.Read(ref _pendingDiskWrites) > 0)
        {
            await DrainPendingDiskWritesAsync().ConfigureAwait(false);
            await Task.Delay(DisposalDelayMs).ConfigureAwait(false);
        }

        if (_parser != null)
            await _parser.DisposeAsync().ConfigureAwait(false);

        DisposeSharedResources();
    }

    // --- Drain ---

    /// <summary>
    /// Awaits completion of all background disk-write operations before releasing the lease.
    /// </summary>
    private async Task DrainPendingDiskWritesAsync()
    {
        const int maxWaitMs = 2000;
        const int pollIntervalMs = 25;
        int elapsed = 0;

        while (Volatile.Read(ref _pendingDiskWrites) > 0 && elapsed < maxWaitMs)
        {
            await Task.Delay(pollIntervalMs).ConfigureAwait(false);
            elapsed += pollIntervalMs;
        }

        int remaining = Volatile.Read(ref _pendingDiskWrites);
        if (remaining > 0)
            Log.Warn(
                $"[CachingSource] {remaining} pending disk writes not drained within {maxWaitMs}ms");
    }

    /// <summary>Sync fallback for draining pending writes.</summary>
    private void DrainPendingDiskWritesSync()
    {
        const int maxWaitMs = 500;
        const int pollIntervalMs = 10;
        int elapsed = 0;

        while (Volatile.Read(ref _pendingDiskWrites) > 0 && elapsed < maxWaitMs)
        {
            Thread.Sleep(pollIntervalMs);
            elapsed += pollIntervalMs;
        }
    }
}
