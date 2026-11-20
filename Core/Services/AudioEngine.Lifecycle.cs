using LMP.Core.Audio.Http;
using LMP.Core.Exceptions;

namespace LMP.Core.Services;

public sealed partial class AudioEngine
{
    #region ISuspendable Implementation

    /// <inheritdoc />
    public void OnSuspend(SuspendLevel level)
    {
        _isSuspended = true;
        _player.SetUiTimersSuspended(true);

        if (ShouldKeepSourceActiveWhileSuspended())
        {
            Log.Debug("[AudioEngine] Suspend policy: source remains active due to active playback/buffering");
            return;
        }

        ApplyLifecycleSourceSuspendPolicy();
    }

    /// <inheritdoc />
    public void OnResume(SuspendLevel previousLevel)
    {
        _isSuspended = false;
        _player.SetUiTimersSuspended(false);
        ApplyLifecycleSourceSuspendPolicy();

        AudioSourceFactory.PreWarmCdnConnections(
            _networkManager.AudioClient, _lifetimeCts.Token);
    }

    #endregion

    #region Source Lifecycle Policy

    private bool ShouldKeepSourceActiveWhileSuspended()
    {
        var playbackState = _player.State;
        var detailedState = _player.DetailedState;

        return playbackState is PlaybackState.Loading
            or PlaybackState.Buffering
            or PlaybackState.Playing
            || detailedState == PlayerState.Seeking;
    }

    private void ApplyLifecycleSourceSuspendPolicy()
    {
        if (_player.GetActivePipeline()?.Source is not Audio.Sources.CachingStreamSource cs)
        {
            Volatile.Write(ref _sourceLifecycleSuspended, 0);
            return;
        }

        if (!_isSuspended || ShouldKeepSourceActiveWhileSuspended())
        {
            if (Interlocked.Exchange(ref _sourceLifecycleSuspended, 0) != 0)
                cs.Resume();

            return;
        }

        if (Interlocked.Exchange(ref _sourceLifecycleSuspended, 1) == 0)
            cs.Suspend();
    }

    #endregion

    #region Failure Barrier

    private bool IsSealedFailedTrack(string? trackId)
    {
        var sealed_ = Interlocked.CompareExchange(ref _sealedFailedTrackId, null, null);
        return !string.IsNullOrEmpty(trackId) && !string.IsNullOrEmpty(sealed_)
            && string.Equals(sealed_, trackId, StringComparison.Ordinal);
    }

    private void ResetSealedFailedTrack() => Volatile.Write(ref _sealedFailedTrackId, null);

    private void SealFailedTrack(string? trackId)
    {
        if (!string.IsNullOrEmpty(trackId))
            Volatile.Write(ref _sealedFailedTrackId, trackId);
    }

    private void AbortCurrentTrackPlaybackAfterFatalError(string? trackId)
    {
        if (string.IsNullOrEmpty(trackId)) return;
        SealFailedTrack(trackId);

        if (!string.Equals(CurrentTrack?.Id, trackId, StringComparison.Ordinal)) return;

        lock (_playerDispatchLock)
        {
            EndPlaybackSessionLocked(TrackEndReason.Error);

            _playIntent = false;
            Interlocked.Increment(ref _pauseEpoch);
            BeginNewSession();
            _player.Stop();
        }
    }

    /// <summary>
    /// Stops the active stream on critical error while keeping the current track in the player and queue
    /// so it does not disappear from the UI and the user can resume playback.
    /// </summary>
    public void StopAfterFatalPlaybackError()
    {
        AbortCurrentTrackPlaybackAfterFatalError(CurrentTrack?.Id);

        RaiseOnUI(() =>
        {
            OnPlaybackStateChanged?.Invoke(false, true);
            OnLoadingStateChanged?.Invoke(false);
        });
    }

    #endregion

    #region Error Handling

    private void HandleCacheInvalidated(CacheInvalidatedException cacheEx, TimeSpan? resumePositionOverride = null)
    {
        var trackId = cacheEx.TrackId ?? CurrentTrack?.Id;

        if (cacheEx.IsRecoverable && _cacheRetryCount < MaxCacheAutoRetries)
        {
            int retryNumber = Interlocked.Increment(ref _cacheRetryCount);
            var resumePosition = resumePositionOverride ?? CurrentPosition;
            var track = CurrentTrack;

            Log.Info($"[AudioEngine] Cache auto-retry #{retryNumber}/{MaxCacheAutoRetries}: track={trackId}, kind={cacheEx.Kind}, pos={resumePosition}");

            if (cacheEx.Kind is CacheInvalidationKind.FileDeleted)
            {
                if (!string.IsNullOrEmpty(trackId))
                {
                    try
                    {
                        AudioSourceFactory.GlobalCache.RemoveTrackCache(trackId);
                        Log.Info($"[AudioEngine] Removed missing cache registry for retry: {trackId}");
                    }
                    catch (Exception removeEx)
                    {
                        Log.Warn($"[AudioEngine] Failed to remove cache: {removeEx.Message}");
                    }
                }
            }
            else if (cacheEx.Kind is CacheInvalidationKind.ParserResync or CacheInvalidationKind.ShortRead)
            {
                Log.Info($"[AudioEngine] Surgical patch in progress. Preserving existing cache file for: {trackId}");
            }

            if (track != null)
            {
                ResetSealedFailedTrack();
                int session = BeginNewSession();
                EnqueueCommand(new PlayTrackCommand(track, session, resumePosition, IsRetry: true));
            }
            return;
        }

        Log.Warn($"[AudioEngine] Cache error non-recoverable or retry budget exhausted (retries={_cacheRetryCount}, kind={cacheEx.Kind}): {cacheEx.Message}");

        if (!string.IsNullOrEmpty(trackId))
        {
            try { AudioSourceFactory.GlobalCache.RemoveTrackCache(trackId); }
            catch (Exception ex) { Log.Warn($"[AudioEngine] Failed to remove cache: {ex.Message}"); }
        }

        AbortCurrentTrackPlaybackAfterFatalError(trackId);
        RaiseError(new CacheInvalidatedException(cacheEx.Message, cacheEx.InnerException));
    }

    /// <summary>
    /// Handles confirmed container corruption inside an exported local download file (<c>Downloads</c>):
    /// clears stale <see cref="TrackInfo.IsDownloaded"/>/<see cref="TrackInfo.LocalPath"/> metadata
    /// (without deleting the user's file on disk) and retries playback cleanly from cache/network.
    /// </summary>
    private bool HandleCorruptedLocalDownload(Exception ex)
    {
        var track = CurrentTrack;
        if (track == null || !track.IsDownloaded || string.IsNullOrEmpty(track.LocalPath))
            return false;

        string trackId = track.Id;
        string brokenPath = track.LocalPath;
        var resumePosition = CurrentPosition;

        Log.Warn($"[AudioEngine] Corrupted local download detected for {trackId} ('{brokenPath}'): {ex.Message}. " +
                 "Invalidating IsDownloaded status to allow clean playback recovery.");

        InvalidateTrackDownloadStatus(track);

        if (_cacheRetryCount < MaxCacheAutoRetries)
        {
            int retryNumber = Interlocked.Increment(ref _cacheRetryCount);
            Log.Info($"[AudioEngine] Local download recovery retry #{retryNumber}/{MaxCacheAutoRetries}: track={trackId}, pos={resumePosition}");

            ResetSealedFailedTrack();
            int session = BeginNewSession();
            EnqueueCommand(new PlayTrackCommand(track, session, resumePosition, IsRetry: true, BypassLocalDownload: true));
            return true;
        }

        AbortCurrentTrackPlaybackAfterFatalError(trackId);
        return false;
    }

    private void InvalidateTrackDownloadStatus(TrackInfo track)
    {
        static void ClearDownloadFlags(TrackInfo target)
        {
            target.IsDownloaded = false;
            target.LocalPath = null;
        }

        ClearDownloadFlags(track);

        var registryTrack = _trackRegistry.TryGet(track.Id);
        if (registryTrack != null && !ReferenceEquals(registryTrack, track))
            ClearDownloadFlags(registryTrack);

        _ = _library.AddOrUpdateTrackAsync(registryTrack ?? track);
    }

    private void RaiseError(Exception exception)
    {
        RaiseOnUI(() => OnErrorOccurred?.Invoke(exception));
    }

    #endregion

    #region IDisposable & IAsyncDisposable Implementation

    private void Dispose(bool disposing)
    {
        if (!disposing || Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        LifecycleRegistry.Instance?.UnregisterBackgroundSuspendable(this);

        _library.OnInitialized -= InitializeFromSettings;
        _youtube.OnNTokenDecryptionStarted -= HandleNTokenDecryptionStarted;
        CdnConnectionPreWarmer.OnTunnelDeadDetected -= HandleCdnTunnelDead;
        Audio.Sources.CachingStreamSource.OnNetworkStalled -= HandleSourceNetworkStalled;
        Audio.Sources.CachingStreamSource.OnNetworkRecovered -= HandleSourceNetworkRecovered;
        UnsubscribeNetworkManagerEvents();

        lock (_sessionLock)
        {
            var scts = _sessionCts;
            _sessionCts = null;
            try { scts?.Cancel(); } catch (ObjectDisposedException) { }
            try { scts?.Dispose(); } catch (ObjectDisposedException) { }
        }

        try
        {
            FlushPendingEqualizerSave();
            lock (_eqPersistenceLock)
            {
                _eqSaveTimer?.Dispose();
                _eqSaveTimer = null;
            }
            FlushPendingNormalizationWritesSync();
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioEngine] Sync normalization flush on dispose failed: {ex.Message}");
        }

        _commandQueue.Writer.TryComplete();
        _lifetimeCts.Cancel();

        try { _commandProcessorTask?.Wait(millisecondsTimeout: 500); } catch { }
        try { _volumeSaveTask?.Wait(millisecondsTimeout: 200); } catch { }
        try { Volatile.Read(ref _activePlayTask)?.Wait(millisecondsTimeout: 500); } catch { }

        lock (_playerDispatchLock)
        {
            EndPlaybackSessionLocked(TrackEndReason.Stopped);
        }

        _lifecycleEvents.Writer.TryComplete();
        try { _lifecycleDispatcherTask?.Wait(millisecondsTimeout: 500); } catch { }

        _player.Dispose();
        DisposeRetiredSessionCts();
        _persistenceSignal.Dispose();
        _lifetimeCts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        LifecycleRegistry.Instance?.UnregisterBackgroundSuspendable(this);

        _library.OnInitialized -= InitializeFromSettings;
        _youtube.OnNTokenDecryptionStarted -= HandleNTokenDecryptionStarted;
        CdnConnectionPreWarmer.OnTunnelDeadDetected -= HandleCdnTunnelDead;
        Audio.Sources.CachingStreamSource.OnNetworkStalled -= HandleSourceNetworkStalled;
        Audio.Sources.CachingStreamSource.OnNetworkRecovered -= HandleSourceNetworkRecovered;
        UnsubscribeNetworkManagerEvents();

        CancellationTokenSource? sessionCts;
        lock (_sessionLock)
        {
            sessionCts = _sessionCts;
            _sessionCts = null;
            try { sessionCts?.Cancel(); } catch (ObjectDisposedException) { }
        }

        try
        {
            FlushPendingEqualizerSave();
            lock (_eqPersistenceLock)
            {
                _eqSaveTimer?.Dispose();
                _eqSaveTimer = null;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioEngine] Equalizer flush on async dispose: {ex.Message}");
        }

        using (var flushCts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        {
            try
            {
                await FlushPendingNormalizationWritesAsync(flushCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"[AudioEngine] Normalization flush on async dispose: {ex.Message}");
            }
        }

        _commandQueue.Writer.TryComplete();
        _lifetimeCts.Cancel();

        const int loopDrainTimeoutMs = 2_000;
        if (_commandProcessorTask != null)
        {
            try
            {
                await _commandProcessorTask
                    .WaitAsync(TimeSpan.FromMilliseconds(loopDrainTimeoutMs))
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            { Log.Warn("[AudioEngine] Command processor did not finish within dispose timeout"); }
            catch (Exception ex) when (ex is OperationCanceledException or AggregateException) { }
        }

        if (_volumeSaveTask != null)
        {
            try
            {
                await _volumeSaveTask
                    .WaitAsync(TimeSpan.FromMilliseconds(loopDrainTimeoutMs))
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            { Log.Warn("[AudioEngine] Volume save loop did not finish within dispose timeout"); }
            catch (Exception ex) when (ex is OperationCanceledException or AggregateException) { }
        }

        var activePlay = Volatile.Read(ref _activePlayTask);
        if (activePlay != null && !activePlay.IsCompleted)
        {
            try
            {
                await activePlay
                    .WaitAsync(TimeSpan.FromMilliseconds(loopDrainTimeoutMs))
                    .ConfigureAwait(false);
            }
            catch { }
        }

        lock (_playerDispatchLock)
        {
            EndPlaybackSessionLocked(TrackEndReason.Stopped);
        }

        _lifecycleEvents.Writer.TryComplete();
        if (_lifecycleDispatcherTask != null)
        {
            try
            {
                await _lifecycleDispatcherTask
                    .WaitAsync(TimeSpan.FromMilliseconds(loopDrainTimeoutMs))
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Log.Warn("[AudioEngine] Lifecycle event dispatcher did not finish within dispose timeout");
            }
            catch { }
        }

        await _player.DisposeAsync().ConfigureAwait(false);
        try { sessionCts?.Dispose(); } catch (ObjectDisposedException) { }
        DisposeRetiredSessionCts();
        _persistenceSignal.Dispose();
        _lifetimeCts.Dispose();
    }

    public void Dispose()
    {
        Dispose(true);
    }

    #endregion
}