using System.Diagnostics;
using System.Runtime.CompilerServices;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Sources;
using LMP.Core.Exceptions;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio;

internal sealed partial class AudioPlayer
{
    #region Seek Constants

    private const int SeekLoopMaxIterations = 50;
    private const int ReSeekDecoderStopTimeoutMs = 200;
    private const int DeferredSeekResumeTimeoutMs = 30_000;

    /// <summary>Deferred warmup escalation threshold: UI signal + re-build (ms).</summary>
    private const int DeferredWarmupEscalationMs = 60_000;

    /// <summary>Hard cap deferred warmup: stop waiting, error event (ms).</summary>
    private const int DeferredWarmupHardCapMs = 180_000;

    #endregion

    #region Seek State

    /// <summary>CTS to interrupt the current coalescing loop iteration (network request).</summary>
    private volatile CancellationTokenSource? _seekIterationInterruptCts;

    /// <summary>CTS for the current seek.</summary>
    private CancellationTokenSource? _activeSeekCts;

    /// <summary>CTS for the current deferred-resume after seek.</summary>
    private CancellationTokenSource? _deferredResumeCts;

    private readonly Lock _seekStateLock = new();

    /// <summary>Pending seek position for latest-wins coalescing.</summary>
    private long _pendingSeekMs = -1;

    /// <summary>Indicates whether seek is active.</summary>
    private volatile bool _backgroundSeekActive;

    /// <summary>Pipeline associated with the current seek.</summary>
    private AudioPipeline? _backgroundSeekPipeline;

#if AUDIO_SPLICE_AUDIT
    private int _seekRestartCount;
#endif

    #endregion

    #region Public Seek API

    /// <summary>
    /// Initiates a seek with latest-wins coalescing.
    /// </summary>
    public ValueTask SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        if (_disposed || _state is not (PlayerState.Playing or PlayerState.Paused or PlayerState.Buffering or PlayerState.Seeking))
            return ValueTask.CompletedTask;

        if (ct.IsCancellationRequested)
            return ValueTask.CompletedTask;

        var pipeline = _activePipeline;
        if (pipeline == null || !pipeline.Source.CanSeek)
            return ValueTask.CompletedTask;

        long targetMs = (long)position.TotalMilliseconds;

        lock (_seekStateLock)
        {
            if (_backgroundSeekActive || Volatile.Read(ref _pendingSeekMs) >= 0)
            {
                Volatile.Write(ref _pendingSeekMs, targetMs);

                // Immediately cancel the current iteration (network request)
                // so that the coalescing loop instantly moves to the new position.
                var iterCts = Interlocked.Exchange(ref _seekIterationInterruptCts, null);
                CancelCtsAsync(iterCts);

                return ValueTask.CompletedTask;
            }
        }

        CancelActiveSeek();

        int seekGeneration = Interlocked.Increment(ref _seekGeneration);

        Volatile.Write(ref _pendingSeekMs, targetMs);
        _commandChannel.Writer.TryWrite(new SeekCommand(position, _session.Current, seekGeneration));

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Asynchronously cancels the current seek and deferred-resume.
    /// </summary>
    private void CancelActiveSeek()
    {
        ResetSeekState();

        var deferredResumeCts = Interlocked.Exchange(ref _deferredResumeCts, null);
        CancelCtsAsync(deferredResumeCts);

        var cts = Interlocked.Exchange(ref _activeSeekCts, null);
        if (cts == null) return;

#if AUDIO_SPLICE_AUDIT
        int restartCount = Interlocked.Increment(ref _seekRestartCount);
        if (restartCount % 5 == 0)
            Log.Warn($"[AudioPlayer] Seek restart storm: {restartCount} cancellations on current track");
#endif

        CancelCtsAsync(cts);
        var iterCts = Interlocked.Exchange(ref _seekIterationInterruptCts, null);
        CancelCtsAsync(iterCts);
    }

    private static void CancelCtsAsync(CancellationTokenSource? cts)
    {
        if (cts == null) return;

        ThreadPool.UnsafeQueueUserWorkItem(static state =>
        {
            try { ((CancellationTokenSource)state!).Cancel(); }
            catch (ObjectDisposedException) { }
        }, cts);
    }

    /// <summary>Resets seek observability counters.</summary>
    [Conditional("AUDIO_SPLICE_AUDIT")]
    internal void ResetPerTrackCounters()
    {
#if AUDIO_SPLICE_AUDIT
        Volatile.Write(ref _seekRestartCount, 0);
#endif
    }

    #endregion

    #region Seek Command Handler

    /// <summary>
    /// Handles a seek command with seamless preservation of the old wave's tail for crossfade.
    /// </summary>
    private async Task HandleSeekAsync(SeekCommand cmd)
    {
        var pipeline = _activePipeline;
        if (pipeline == null || !pipeline.Source.CanSeek)
        {
            cmd.Completion?.TrySetResult(false);
            _events.RaiseSeekCompleted(cmd.Position);
            return;
        }

        bool wasPlaying = CurrentPlaybackIntent == PlaybackIntent.Play
                       || _state is PlayerState.Playing or PlayerState.Buffering;

        var seekCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        var oldCts = Interlocked.Exchange(ref _activeSeekCts, seekCts);
        if (oldCts != null)
        {
            try { oldCts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        // Publish background seek state BEFORE draining _pendingSeekMs and awaiting StopDecodingAsync
        // so concurrent SeekAsync calls coalesce cleanly instead of dropping or dispatching a duplicate SeekCommand.
        Volatile.Write(ref _backgroundSeekPipeline, pipeline);
        _backgroundSeekActive = true;

        long latestTargetMs = DrainPendingSeekMs();
        long posMs = latestTargetMs >= 0 ? latestTargetMs : (long)cmd.Position.TotalMilliseconds;

        bool dataLocal = IsSeekDataLocallyAvailable(pipeline, posMs);
        if (dataLocal)
            SetState(PlayerState.Seeking);
        else
            SetState(PlayerState.Buffering);

        StopPositionTimer();

        pipeline.Source.SetPlaybackActive(false);

        try
        {
            if (pipeline.Source is CachingStreamSource cachingSource)
                _ = cachingSource.TryPrefetchChunkForSeekAsync(posMs, seekCts.Token);

            await pipeline.StopDecodingAsync(
                TimeSpan.FromMilliseconds(DecoderStopTimeoutSeekMs)).ConfigureAwait(false);

            if (_session.IsStale(cmd.SessionId) || seekCts.IsCancellationRequested)
            {
                cmd.Completion?.TrySetCanceled();
                _events.RaiseSeekCompleted(TimeSpan.FromMilliseconds(posMs));
                return;
            }

            // Call Flush() EXACTLY ONCE after stopping the decoder:
            // this guarantees no duplicate tail capture and no race conditions with the decoder.
            pipeline.Flush();
            pipeline.PrepareForSeek(posMs);

            await CompleteSeekWithCoalescingAsync(
                pipeline, cmd, posMs, wasPlaying, cmd.SessionId, seekCts)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cmd.Completion?.TrySetCanceled();
            _events.RaiseSeekCompleted(TimeSpan.FromMilliseconds(posMs));
        }
        catch (AudioDeviceException ex)
        {
            cmd.Completion?.TrySetException(ex);
            HandleError(ex);
            _events.RaiseSeekCompleted(TimeSpan.FromMilliseconds(posMs));
        }
        catch (Exception ex)
        {
            cmd.Completion?.TrySetException(ex);
            SetState(wasPlaying && CurrentPlaybackIntent == PlaybackIntent.Play ? PlayerState.Playing : PlayerState.Paused);

            if (wasPlaying && CurrentPlaybackIntent == PlaybackIntent.Play)
            {
                try { pipeline.Start(); }
                catch (AudioDeviceException devEx) { HandleError(devEx); return; }
            }

            _events.RaiseSeekCompleted(TimeSpan.FromMilliseconds(posMs));
        }
        finally
        {
            if (CurrentPlaybackIntent == PlaybackIntent.Play)
                RestorePlaybackActiveState(pipeline);

            StartPositionTimerDelayed();

            long lateSeekMs = -1;
            lock (_seekStateLock)
            {
                if (ReferenceEquals(Volatile.Read(ref _backgroundSeekPipeline), pipeline))
                {
                    lateSeekMs = Interlocked.Exchange(ref _pendingSeekMs, -1);
                    Volatile.Write(ref _backgroundSeekPipeline, null);
                    _backgroundSeekActive = false;
                }
            }

            if (lateSeekMs >= 0 && !_disposed && _activePipeline == pipeline && !_session.IsStale(cmd.SessionId))
            {
                int nextGen = Interlocked.Increment(ref _seekGeneration);
                Volatile.Write(ref _pendingSeekMs, lateSeekMs);
                _commandChannel.Writer.TryWrite(new SeekCommand(TimeSpan.FromMilliseconds(lateSeekMs), cmd.SessionId, nextGen));
            }

            Interlocked.CompareExchange(ref _activeSeekCts, null, seekCts);
            Interlocked.Exchange(ref _seekIterationInterruptCts, null);

            try { seekCts.Dispose(); }
            catch (ObjectDisposedException) { }
        }
    }

    #endregion

    #region Seek Coalescing Loop

    /// <summary>
    /// Performs the background phase of seek with coalescing.
    /// </summary>
    private async Task CompleteSeekWithCoalescingAsync(
        AudioPipeline pipeline,
        SeekCommand cmd,
        long initialPosMs,
        bool wasPlaying,
        int sessionAtStart,
        CancellationTokenSource seekCts)
    {
        var seekCt = seekCts.Token;
        var totalSw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            long currentTargetMs = initialPosMs;
            int iteration = 0;

            while (iteration++ < SeekLoopMaxIterations)
            {
                using var iterInterruptCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token, seekCt);
                _seekIterationInterruptCts = iterInterruptCts;
                var iterCt = iterInterruptCts.Token;

                var phaseSw = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    await pipeline.Source.SeekAsync(currentTargetMs, iterCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (iterInterruptCts.IsCancellationRequested && !seekCt.IsCancellationRequested)
                {
                    long drained = DrainPendingSeekMs();
                    if (drained >= 0)
                    {
                        currentTargetMs = drained;
                        pipeline.Flush();
                        pipeline.PrepareForSeek(currentTargetMs);
                        Log.Debug($"[Seek] Phase A interrupted, coalescing to {drained}ms");
                        continue;
                    }
                }
                catch (OperationCanceledException)
                {
                    Log.Debug($"[Seek] Phase A cancelled after {phaseSw.ElapsedMilliseconds}ms");
                    throw;
                }
                finally
                {
                    Interlocked.CompareExchange(ref _seekIterationInterruptCts, null, iterInterruptCts);
                }

                // If CachingStreamSource.SeekAsync intercepted iterCt cancellation and returned false without an exception,
                // check for a new coalesced request before state validation!
                long drainedAfterPhaseA = DrainPendingSeekMs();
                if (drainedAfterPhaseA >= 0 && !seekCt.IsCancellationRequested)
                {
                    currentTargetMs = drainedAfterPhaseA;
                    pipeline.Flush();
                    pipeline.PrepareForSeek(currentTargetMs);
                    Log.Debug($"[Seek] Phase A coalesced (no-throw) to {drainedAfterPhaseA}ms");
                    continue;
                }

                long phaseAMs = phaseSw.ElapsedMilliseconds;
                phaseSw.Restart();

                if (!ValidateSeekState(seekCt, pipeline, sessionAtStart))
                {
                    cmd.Completion?.TrySetCanceled();
                    _events.RaiseSeekCompleted(TimeSpan.FromMilliseconds(currentTargetMs));
                    return;
                }

                UpdateDecodedPosition(pipeline, currentTargetMs);

                long drainedAfterSeek = DrainPendingSeekMs();
                if (drainedAfterSeek >= 0)
                {
                    currentTargetMs = drainedAfterSeek;
                    pipeline.Flush();
                    pipeline.PrepareForSeek(currentTargetMs);
                    Log.Debug($"[Seek] Phase B: Coalesced to {drainedAfterSeek}ms");
                    continue;
                }

                if (!ValidateSeekState(seekCt, pipeline, sessionAtStart))
                {
                    cmd.Completion?.TrySetCanceled();
                    _events.RaiseSeekCompleted(TimeSpan.FromMilliseconds(currentTargetMs));
                    return;
                }

                pipeline.StartDecoding(
                    _options,
                    CreateTrackEndedCallback(cmd.SessionId),
                    CreateErrorCallback(cmd.SessionId, pipeline));

                long phaseDMs = phaseSw.ElapsedMilliseconds;
                phaseSw.Restart();

                long drainedBeforeWarmup = DrainPendingSeekMs();
                if (drainedBeforeWarmup >= 0)
                {
                    currentTargetMs = drainedBeforeWarmup;
                    await StopDecoderForReseekAsync(pipeline, seekCt).ConfigureAwait(false);
                    pipeline.PrepareForSeek(currentTargetMs);
                    Log.Debug($"[Seek] Phase D interrupted, re-seeking to {drainedBeforeWarmup}ms");
                    continue;
                }

                long phaseEMs = 0;

                if (wasPlaying && CurrentPlaybackIntent == PlaybackIntent.Play)
                {
                    var warmupPlan = ComputePlaybackWarmupPlan(pipeline, isSeek: true);

                    bool pcmReady = await pipeline.WaitForBufferAsync(
                        warmupPlan.PcmThresholdSamples,
                        warmupPlan.WarmupTimeoutMs,
                        iterCt).ConfigureAwait(false);

                    phaseEMs = phaseSw.ElapsedMilliseconds;

                    long drainedFinal = DrainPendingSeekMs();
                    if (drainedFinal >= 0)
                    {
                        currentTargetMs = drainedFinal;
                        await StopDecoderForReseekAsync(pipeline, seekCt).ConfigureAwait(false);
                        pipeline.PrepareForSeek(currentTargetMs);
                        Log.Debug($"[Seek] Phase E interrupted, re-seeking to {drainedFinal}ms");
                        continue;
                    }

                    if (!ValidateSeekState(seekCt, pipeline, sessionAtStart))
                    {
                        cmd.Completion?.TrySetCanceled();
                        _events.RaiseSeekCompleted(TimeSpan.FromMilliseconds(currentTargetMs));
                        return;
                    }

                    bool sourceReady = IsSourceReadyForResume(pipeline, warmupPlan.SourceAheadMs);
                    RestorePlaybackActiveState(pipeline);

                    ResumeOrDefer(pipeline, pcmReady, sourceReady, warmupPlan, sessionAtStart,
                        startTimers: false, configurePipeline: false, logContext: "Seek warmup");
                }
                else
                {
                    SetState(PlayerState.Paused);
                }

                Log.Info($"[Seek] Seek to {currentTargetMs}ms COMPLETED. " +
                         $"Total: {totalSw.ElapsedMilliseconds}ms | " +
                         $"A: {phaseAMs}ms | D: {phaseDMs}ms | E: {phaseEMs}ms");

                StartPositionTimerDelayed();
                _events.RaiseSeekCompleted(TimeSpan.FromMilliseconds(currentTargetMs));
                cmd.Completion?.TrySetResult(true);
                return;
            }

            Log.Warn($"[Seek] Seek coalescing loop exhausted ({SeekLoopMaxIterations} iterations)");
            cmd.Completion?.TrySetResult(false);
            _events.RaiseSeekCompleted(TimeSpan.FromMilliseconds(initialPosMs));
        }
        catch (OperationCanceledException)
        {
            Log.Debug($"[Seek] Seek to {initialPosMs}ms cancelled. " +
                      $"Elapsed: {totalSw.ElapsedMilliseconds}ms");
            cmd.Completion?.TrySetCanceled();
            _events.RaiseSeekCompleted(TimeSpan.FromMilliseconds(initialPosMs));
        }
        catch (Exception ex)
        {
            Log.Error($"[Seek] Seek to {initialPosMs}ms failed: {ex.Message}", ex);
            cmd.Completion?.TrySetException(ex);
            SetState(wasPlaying && CurrentPlaybackIntent == PlaybackIntent.Play ? PlayerState.Playing : PlayerState.Paused);
            _events.RaiseSeekCompleted(TimeSpan.FromMilliseconds(initialPosMs));
        }
        finally
        {
            if (CurrentPlaybackIntent == PlaybackIntent.Play)
                RestorePlaybackActiveState(pipeline);

            StartPositionTimerDelayed();

            long lateSeekMs = -1;
            lock (_seekStateLock)
            {
                if (ReferenceEquals(Volatile.Read(ref _backgroundSeekPipeline), pipeline))
                {
                    lateSeekMs = Interlocked.Exchange(ref _pendingSeekMs, -1);
                    Volatile.Write(ref _backgroundSeekPipeline, null);
                    _backgroundSeekActive = false;
                }
            }

            if (lateSeekMs >= 0 && !_disposed && _activePipeline == pipeline && !_session.IsStale(sessionAtStart))
            {
                int nextGen = Interlocked.Increment(ref _seekGeneration);
                Volatile.Write(ref _pendingSeekMs, lateSeekMs);
                _commandChannel.Writer.TryWrite(new SeekCommand(TimeSpan.FromMilliseconds(lateSeekMs), sessionAtStart, nextGen));
            }

            Interlocked.CompareExchange(ref _activeSeekCts, null, seekCts);
            Interlocked.Exchange(ref _seekIterationInterruptCts, null);

            try { seekCts.Dispose(); }
            catch (ObjectDisposedException) { }
        }
    }

    #endregion

    #region Deferred Seek Resume

    /// <summary>
    /// Background-waits for PCM and source-ahead readiness after deferred seek/rebuffer.
    /// Escalation: 60s → UI signal + rebuild. 180s → stop.
    /// </summary>
    private async Task AwaitDeferredSeekBufferAndResumeAsync(
        AudioPipeline pipeline,
        int seekThreshold,
        int sourceAheadMs,
        int sessionId,
        int seekGeneration,
        CancellationTokenSource deferredResumeCts)
    {
        var ct = deferredResumeCts.Token;
        var waitLogSw = System.Diagnostics.Stopwatch.StartNew();
        long nextProgressLogMs = DeferredSeekResumeTimeoutMs;
        bool escalationFired = false;

        try
        {
            while (!ct.IsCancellationRequested && !_disposed)
            {
                bool pcmReady = seekThreshold <= 0 || pipeline.BufferedSamples >= seekThreshold;
                bool sourceReady = IsSourceReadyForResume(pipeline, sourceAheadMs);

                if (!pcmReady
                    && seekThreshold > 0
                    && pipeline.BufferedSamples > 0
                    && sourceReady
                    && GetSourceBufferedAheadMs(pipeline) >= sourceAheadMs)
                {
                    pcmReady = true;
                }

                if (!pcmReady && seekThreshold > 0)
                {
                    int waitSliceMs = sourceAheadMs >= 6000 ? 1000 : 400;

                    bool signaled = await pipeline.WaitForBufferAsync(seekThreshold, waitSliceMs, ct)
                        .ConfigureAwait(false);

                    pcmReady = signaled || pipeline.BufferedSamples >= seekThreshold;
                    sourceReady = IsSourceReadyForResume(pipeline, sourceAheadMs);

                    if (!pcmReady
                        && seekThreshold > 0
                        && pipeline.BufferedSamples > 0
                        && sourceReady
                        && GetSourceBufferedAheadMs(pipeline) >= sourceAheadMs)
                    {
                        pcmReady = true;
                    }
                }

                if (pcmReady && sourceReady)
                {
                    int ringBufferSamples = pipeline.BufferedSamples;

                    if (seekThreshold > 0 && ringBufferSamples == 0)
                    {
                        if (!await ct.DelayNoThrowAsync(100, continueOnCapturedContext: false).ConfigureAwait(false))
                            return;
                        continue;
                    }

                    if (!_commandChannel.Writer.TryWrite(new DeferredResumeCommand(
                            sessionId,
                            seekGeneration,
                            pipeline,
                            ThresholdReached: true,
                            BufferedSamples: ringBufferSamples)))
                    {
                        Log.Debug("[Seek] Deferred resume command dropped: channel unavailable");
                    }

                    return;
                }

                long elapsedMs = waitLogSw.ElapsedMilliseconds;

                if (elapsedMs >= nextProgressLogMs)
                {
                    Log.Warn($"[Seek] Deferred warmup still waiting " +
                             $"(ring={pipeline.BufferedSamples}/{seekThreshold}, " +
                             $"ahead={GetSourceBufferedAheadMs(pipeline)}ms/{sourceAheadMs}ms, " +
                             $"elapsed={elapsedMs}ms)");

                    nextProgressLogMs += DeferredSeekResumeTimeoutMs;
                }

                if (!escalationFired && elapsedMs >= DeferredWarmupEscalationMs)
                {
                    escalationFired = true;
                    Log.Warn($"[Seek] Deferred warmup exceeded {DeferredWarmupEscalationMs / 1000}s — " +
                             "network likely unavailable, escalating");

                    _options.OnStarvationDetected?.Invoke();
                }

                if (elapsedMs >= DeferredWarmupHardCapMs)
                {
                    Log.Error($"[Seek] Deferred warmup hard timeout ({DeferredWarmupHardCapMs / 1000}s)");
                    _commandChannel.Writer.TryWrite(new StopCommand(sessionId));
                    return;
                }

                if (!await ct.DelayNoThrowAsync(100, continueOnCapturedContext: false).ConfigureAwait(false))
                    return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"[Seek] Deferred seek waiter error: {ex.Message}");
        }
        finally
        {
            Interlocked.CompareExchange(ref _deferredResumeCts, null, deferredResumeCts);
            try { deferredResumeCts.Dispose(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Resumes playback after deferred buffering inside the actor loop.
    /// </summary>
    private Task HandleDeferredResumeAsync(DeferredResumeCommand cmd)
    {
        int currentSeekGeneration = Volatile.Read(ref _seekGeneration);

        if (_disposed
            || _activePipeline != cmd.Pipeline
            || _session.IsStale(cmd.SessionId)
            || cmd.SeekGeneration != currentSeekGeneration)
        {
            Log.Debug($"[Seek] Deferred resume dropped: " +
                      $"disposed={_disposed}, pipelineMatch={_activePipeline == cmd.Pipeline}, " +
                      $"stale={_session.IsStale(sessionId: cmd.SessionId)}, " +
                      $"generationMatch={cmd.SeekGeneration == currentSeekGeneration}, " +
                      $"state={_state}");
            return Task.CompletedTask;
        }

        if (CurrentPlaybackIntent != PlaybackIntent.Play)
        {
            Log.Debug($"[Seek] Deferred resume ignored due to intent={CurrentPlaybackIntent}");
            return Task.CompletedTask;
        }

        if (_state is PlayerState.Idle or PlayerState.Disposed or PlayerState.Error)
        {
            Log.Debug($"[Seek] Deferred resume ignored in terminal state={_state}");
            return Task.CompletedTask;
        }

        if (!cmd.ThresholdReached)
        {
            Log.Warn("[Seek] Deferred resume ignored because adaptive readiness threshold was not reached.");
            return Task.CompletedTask;
        }

        if (cmd.Pipeline.IsDeviceLost)
        {
            SetState(PlayerState.Buffering);
            _commandChannel.Writer.TryWrite(new DeviceRecoveryCommand(cmd.SessionId));
            return Task.CompletedTask;
        }

        Log.Debug($"[Seek] Deferred warmup complete (ring={cmd.BufferedSamples}). Resuming playback.");

        ResumePlaybackSequence(
            cmd.Pipeline,
            startTimers: true,
            configurePipeline: false);

        Log.Info("[Seek] Deferred seek buffer ready. Playback resumed automatically.");
        return Task.CompletedTask;
    }

    #endregion

    #region Seek Helpers

    /// <summary>Atomically consumes the pending seek position.</summary>
    private long DrainPendingSeekMs() =>
        Interlocked.Exchange(ref _pendingSeekMs, -1);

    /// <summary>
    /// Stops the decoder and clears buffers before re-seek.
    /// </summary>
    private static async Task StopDecoderForReseekAsync(AudioPipeline pipeline, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        await pipeline.StopDecodingAsync(
            TimeSpan.FromMilliseconds(ReSeekDecoderStopTimeoutMs)).ConfigureAwait(false);

        pipeline.Flush();
    }

    /// <summary>Validates the seek context.</summary>
    private bool ValidateSeekState(CancellationToken ct, AudioPipeline pipeline, int sessionId)
    {
        return !ct.IsCancellationRequested
            && !_disposed
            && _activePipeline == pipeline
            && !_session.IsStale(sessionId);
    }

    /// <summary>Updates the decoded samples position after seek.</summary>
    private static void UpdateDecodedPosition(AudioPipeline pipeline, long posMs)
    {
        long targetSamples = (long)(posMs / 1000.0 * pipeline.SampleRate * pipeline.Channels);
        pipeline.SetDecodedSamplesPosition(targetSamples);
    }

    /// <summary>Resets coalescing seek state.</summary>
    private void ResetSeekState()
    {
        lock (_seekStateLock)
        {
            Volatile.Write(ref _pendingSeekMs, -1);
            Volatile.Write(ref _backgroundSeekPipeline, null);
            _backgroundSeekActive = false;
        }
    }

    /// <summary>
    /// Checks whether data for the seek position is available locally (RAM/disk).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSeekDataLocallyAvailable(AudioPipeline pipeline, long positionMs)
    {
        if (pipeline.Source.IsFullyBuffered)
            return true;

        if (pipeline.Source is CachingStreamSource css)
            return css.IsSeekDataReady(positionMs);

        if (pipeline.Source is LocalFileSource)
            return true;

        return false;
    }

    /// <summary>
    /// Restores the playback gate after seek completion/cancellation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RestorePlaybackActiveState(AudioPipeline pipeline)
    {
        pipeline.Source.SetPlaybackActive(true);
    }

    #endregion
}