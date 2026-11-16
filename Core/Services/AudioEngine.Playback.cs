using LMP.Core.Audio.Helpers;
using LMP.Core.Exceptions;

namespace LMP.Core.Services;

public sealed partial class AudioEngine
{
    #region Command Loop

    /// <summary>
    /// Unified processing loop for typed commands.
    /// </summary>
    private async Task ProcessCommandsAsync()
    {
        try
        {
            await foreach (var cmd in _commandQueue.Reader.ReadAllAsync(_lifetimeCts.Token).ConfigureAwait(false))
            {
                try
                {
                    switch (cmd)
                    {
                        case PlayTrackCommand play:
                            await HandlePlayTrackAsync(play).ConfigureAwait(false);
                            break;

                        case StartQueueCommand start:
                            await HandleStartQueueAsync(start).ConfigureAwait(false);
                            break;

                        case PlayCurrentIndexCommand pci:
                            await PlayCurrentIndexAsync(pci.Session, startPlaying: pci.StartPlaying, pauseEpoch: pci.PauseEpoch).ConfigureAwait(false);
                            break;

                        case NavigateCommand nav:
                            await HandleNavigateAsync(nav).ConfigureAwait(false);
                            break;

                        case SwitchQualityCommand sq:
                            await HandleSwitchQualityAsync(sq).ConfigureAwait(false);
                            break;
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log.Warn($"[AudioEngine] Command error: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Enqueues a typed command.</summary>
    private void EnqueueCommand(IEngineCommand command)
    {
        _commandQueue.Writer.TryWrite(command);
    }

    #endregion

    #region Navigation & Queue Handlers

    private async Task HandlePlayTrackAsync(PlayTrackCommand cmd)
    {
        if (_session.IsStale(cmd.Session)) return;

        if (!cmd.IsRetry)
        {
            Volatile.Write(ref _cacheRetryCount, 0);
        }

        lock (_queueLock)
        {
            int idx = FindTrackIndexById(_queue, cmd.Track.Id);
            if (idx >= 0)
            {
                _currentIndex = idx;
                _queue[idx] = cmd.Track;
            }
            else
            {
                _queue.Clear();
                _queue.Add(cmd.Track);
                _currentIndex = 0;
            }

            _queueSource = TransitionOnPlayTrack(_queueSource, ref _queueSourceGeneration, wasInQueue: idx >= 0);
            RecalculateQueuePurityLocked();
            InvalidateQueueSnapshot();
        }

        RaiseOnUI(_raiseQueueChangedOnUIDelegate);
        await PlayCurrentIndexAsync(cmd.Session, cmd.SeekPosition, startPlaying: true, isRetry: cmd.IsRetry, pauseEpoch: cmd.PauseEpoch).ConfigureAwait(false);
    }

    private async Task HandleStartQueueAsync(StartQueueCommand cmd)
    {
        if (_session.IsStale(cmd.Session)) return;

        Volatile.Write(ref _cacheRetryCount, 0);

        lock (_queueLock)
        {
            _queue.Clear();
            _queue.AddRange(cmd.Tracks);
            _currentIndex = FindTrackIndexById(_queue, cmd.StartTrack.Id);
            if (_currentIndex == -1 && _queue.Count > 0) _currentIndex = 0;

            _queueSource = TransitionOnStartQueue(
                ref _queueSourceGeneration,
                _queue,
                cmd.PlaylistId,
                cmd.PlaylistName);

            if (ShuffleEnabled && _queue.Count > 1) ApplyShuffleInPlace(preserveCurrentAtStart: true);
            RecalculateQueuePurityLocked();
            InvalidateQueueSnapshot();
        }

        RaiseOnUI(_raiseQueueChangedOnUIDelegate);
        await PlayCurrentIndexAsync(cmd.Session, pauseEpoch: cmd.PauseEpoch).ConfigureAwait(false);
    }

    private async Task HandleNavigateAsync(NavigateCommand cmd)
    {
        if (cmd.ReplaceSeq != Volatile.Read(ref _queueReplaceSeq))
            return;

        if (cmd.PauseEpoch >= 0 && cmd.PauseEpoch != Volatile.Read(ref _pauseEpoch))
            return;

        if (!cmd.UserInitiated && ((cmd.Session >= 0 && _session.IsStale(cmd.Session)) || !_playIntent))
            return;

        bool canMove;
        bool queueMutated;

        lock (_queueLock)
        {
            // A deferred auto-transition from the end of a track that the queue has already moved away from is discarded until BeginNewSession:
            // otherwise it will cancel the running next track and skip over it
            if (cmd.EndedTrackId != null &&
                IsStaleTrackEnd(cmd.EndedTrackId, (uint)_currentIndex < (uint)_queue.Count ? _queue[_currentIndex].Id : null))
            {
                return;
            }

            canMove = cmd.Forward ? TryMoveNext(cmd.UserInitiated) : TryMovePrevious();
            queueMutated = _queueMutatedByNavigation;
        }

        if (queueMutated) RaiseOnUI(_raiseQueueChangedOnUIDelegate);

        bool shouldStartTrack = canMove
            || (!cmd.Forward && _player.State == PlaybackState.Stopped && cmd.UserInitiated && CurrentTrack != null && cmd.StartPlaying);

        if (shouldStartTrack)
        {
            int session;
            lock (_playerDispatchLock)
            {
                if (cmd.ReplaceSeq != Volatile.Read(ref _queueReplaceSeq))
                    return;

                if (cmd.PauseEpoch >= 0 && cmd.PauseEpoch != Volatile.Read(ref _pauseEpoch))
                    return;

                if (!cmd.UserInitiated && ((cmd.Session >= 0 && _session.IsStale(cmd.Session)) || !_playIntent))
                    return;

                if (cmd.StartPlaying)
                    _playIntent = true;
                session = BeginNewSession();
            }

            await PlayCurrentIndexAsync(
                session,
                startPlaying: canMove ? cmd.StartPlaying : true,
                pauseEpoch: cmd.PauseEpoch).ConfigureAwait(false);
            return;
        }

        if (!cmd.Forward && _player.State != PlaybackState.Stopped)
        {
            Volatile.Write(ref _lastSwitchPositionTicks, 0);
            await _player.SeekAsync(TimeSpan.Zero).ConfigureAwait(false);
        }
        else if (!cmd.UserInitiated)
        {
            Stop();
        }
    }

    #endregion

    #region Playback Core

    /// <summary>
    /// Starts track playback at the current queue index with an optional position and auto-start flag.
    /// </summary>
    /// <param name="session">Playback session identifier.</param>
    /// <param name="seekPosition">Initial playback position when seeking.</param>
    /// <param name="startPlaying">Automatic playback start flag.</param>
    /// <param name="isRetry">Whether this startup is an automatic cache recovery retry.</param>
    /// <param name="pauseEpoch">Pause generation captured when the command was enqueued.</param>
    private Task PlayCurrentIndexAsync(
        int session,
        TimeSpan? seekPosition = null,
        bool startPlaying = true,
        bool isRetry = false,
        int pauseEpoch = -1)
    {
        TrackInfo? track;
        lock (_queueLock)
        {
            if (_currentIndex < 0 || _currentIndex >= _queue.Count) return Task.CompletedTask;
            track = _queue[_currentIndex];
        }

        if (track == null || IsSealedFailedTrack(track.Id)) return Task.CompletedTask;
        if (_session.IsStale(session)) return Task.CompletedTask;

        // Do not block the actor thread waiting for the previous canceled task:
        // cancellation of the old session was already initiated via CancellationTokenSource in BeginNewSession().
        // The new task starts instantly, while the canceled one completes asynchronously on the ThreadPool.
        var playTask = PlayTrackCoreAsync(track, session, GetSessionToken(), seekPosition, startPlaying, isRetry, pauseEpoch);
        Volatile.Write(ref _activePlayTask, playTask);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Main method for preparing and starting track playback with support for SeekPosition and lazy loading.
    /// </summary>
    private async Task PlayTrackCoreAsync(
       TrackInfo track,
       int session,
       CancellationToken ct,
       TimeSpan? seekPosition = null,
       bool startPlaying = true,
       bool isRetry = false,
       int pauseEpoch = -1)
    {
        if (_session.IsStaleOrCancelled(session, ct) || IsSealedFailedTrack(track.Id))
            return;

        Log.Debug($"[AudioEngine] [PlayTrackCore] Initiating playback for track: {track.Id} | " +
                          $"Session: {session} | StartPlaying: {startPlaying}");

        FlushPendingEqualizerSave();

        bool effectiveStartPlaying;
        lock (_playerDispatchLock)
        {
            if (_session.IsStaleOrCancelled(session, ct) || IsSealedFailedTrack(track.Id))
                return;

            effectiveStartPlaying = startPlaying && (pauseEpoch < 0 || pauseEpoch == Volatile.Read(ref _pauseEpoch) || _playIntent);
            _playIntent = effectiveStartPlaying;
            SetManualLoading(true);

            // Keep existing audio alive only on cache recovery retries; re-clicking the active track resets it.
            if (!isRetry)
            {
                Volatile.Write(ref _lastSwitchPositionTicks, seekPosition?.Ticks ?? 0);
                _player.Stop();
            }
        }

        if (_session.IsStaleOrCancelled(session, ct) || IsSealedFailedTrack(track.Id))
        {
            if (!_session.IsStale(session))
                SetManualLoading(false);
            return;
        }

        try
        {
            var canonical = await _library.GetTrackAsync(track.Id, ct).ConfigureAwait(false);
            if (_session.IsStaleOrCancelled(session, ct) || IsSealedFailedTrack(track.Id))
                return;

            if (canonical != null)
            {
                canonical.UpdateMetadata(track);
                track = canonical;
            }
            else
            {
                track = _trackRegistry.RegisterOrUpdate(track);
            }

            lock (_playerDispatchLock)
            {
                if (_session.IsStaleOrCancelled(session, ct) || IsSealedFailedTrack(track.Id))
                    return;

                if (!startPlaying || !_playIntent)
                    effectiveStartPlaying = false;

                CurrentTrack = track;
                StreamInfo = AudioStreamInfo.Empty;
            }

            RaiseOnUI(() =>
            {
                if (_session.IsStale(session))
                    return;

                OnTrackChanged?.Invoke(track);
                OnPositionChanged?.Invoke(TimeSpan.Zero);
            });

            if (!effectiveStartPlaying)
            {
                SetManualLoading(false);
                return;
            }

            ct.ThrowIfCancellationRequested();
            lock (_playerDispatchLock)
            {
                if (_session.IsStaleOrCancelled(session, ct) || IsSealedFailedTrack(track.Id))
                    return;

                Volatile.Write(ref _nTokenWarnedTrackId, null);
                Volatile.Write(ref _nTokenActiveSession, session);
                Volatile.Write(ref _nTokenActiveTrackId, track.Id);
            }

            AudioSourceFactory.PreWarmCdnConnections(
                Audio.Http.SharedHttpClient.Instance, _lifetimeCts.Token);

            const int maxStartupAttempts = 3;
            const int maxCdnFailoverAttempts = 3;

            for (int attempt = 1; attempt <= maxStartupAttempts; attempt++)
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    var descriptor = await ResolveStreamAsync(track, ct, seekPosition).ConfigureAwait(false);

                    if (descriptor.HasPerceptualLufs)
                    {
                        track.SetIntegratedLufs(
                            descriptor.IntegratedLufs,
                            Audio.Normalization.LoudnessSource.YoutubePerceptual);

                        CommitIntegratedLufs(
                            track.Id,
                            descriptor.IntegratedLufs,
                            Audio.Normalization.LoudnessSource.YoutubePerceptual);
                    }

                    Task? playTask;
                    lock (_playerDispatchLock)
                    {
                        if (_session.IsStaleOrCancelled(session, ct) || IsSealedFailedTrack(track.Id))
                            return;

                        if (!_playIntent)
                        {
                            Log.Debug($"[AudioEngine] PlayTrackCore paused before pipeline start: {track.Id}");
                            return;
                        }

                        Log.Info($"[AudioEngine] PlayTrackCore resolved -> {descriptor}");
                        playTask = _player.PlayAsync(descriptor, ct, seekPosition: seekPosition);
                    }

                    await playTask.ConfigureAwait(false);

                    PreWarmNextTracksInQueue(
                        CurrentQueueIndex,
                        Audio.Http.SharedHttpClient.Instance,
                        _lifetimeCts.Token);
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested || _session.IsStale(session))
                {
                    // User switched track — exit instantly without retries
                    throw;
                }
                catch (Exception ex)
                    when (TryExtractCdnException(ex) is { } cdnEx
                          && attempt < maxCdnFailoverAttempts
                          && !_session.IsStaleOrCancelled(session, ct))
                {
                    Log.Warn($"[AudioEngine] CDN unavailable: host={cdnEx.Host}, " +
                             $"attempt={attempt}/{maxCdnFailoverAttempts}. " +
                             "Blacklisting host and forcing manifest refresh.");

                    AudioSourceFactory.CdnBlacklist.MarkBlocked(cdnEx.Host);
                    _youtube.InvalidateMemoryCache(track.Id);
                    Audio.Http.SessionCacheStore.Invalidate(track.Id);

                    _player.Stop();
                    if (!await ct.DelayNoThrowAsync(300, continueOnCapturedContext: false).ConfigureAwait(false))
                        return;
                }
                catch (OperationCanceledException ex)
                    when (!_session.IsStaleOrCancelled(session, ct) && attempt < maxStartupAttempts)
                {
                    Log.Warn($"[AudioEngine] Transient internal timeout during track startup " +
                             $"(attempt {attempt}/{maxStartupAttempts}): {ex.Message}");
                    _player.Stop();
                    if (!await ct.DelayNoThrowAsync(150, continueOnCapturedContext: false).ConfigureAwait(false))
                        return;
                }
            }

            ApplyGainToPipeline();
            ApplyLifecycleSourceSuspendPolicy();
        }
        catch (OperationCanceledException ex)
        {
            if (!_session.IsStaleOrCancelled(session, ct))
            {
                Log.Warn($"[AudioEngine] Playback startup aborted: {ex.Message}");
                lock (_playerDispatchLock)
                {
                    _playIntent = false;
                    Interlocked.Increment(ref _pauseEpoch);
                    _player.Stop();
                }
                RaiseError(ex);
            }
        }
        catch (Exception) when (_session.IsStaleOrCancelled(session, ct)) { }
        catch (Exception ex)
        {
            AbortCurrentTrackPlaybackAfterFatalError(track.Id);
            RaiseError(ex);
        }
        finally
        {
            if (!_session.IsStale(session))
                SetManualLoading(false);

            lock (_playerDispatchLock)
            {
                if (Volatile.Read(ref _nTokenActiveSession) == session)
                {
                    Volatile.Write(ref _nTokenActiveTrackId, null);
                    Volatile.Write(ref _nTokenActiveSession, 0);
                }
            }
        }
    }

    #endregion

    #region Player Event Handlers

    /// <summary>
    /// Subscribes to low-level player events (<see cref="AudioPlayer"/>).
    /// </summary>
    private void SubscribeToPlayerEvents()
    {
        _player.Events.PositionChanged += _positionChangedHandler;
        _player.Events.StateChanged += HandlePlayerStateChanged;
        _player.Events.TrackEnded += HandlePlayerTrackEnded;
        _player.Events.StreamInfoChanged += HandleStreamInfoChanged;
        _player.Events.BufferStateChanged += _bufferStateChangedHandler;
        _player.Events.SeekCompleted += _seekCompletedHandler;
        _player.Events.DeviceLost += _deviceLostHandler;
        _player.Events.DeviceRestored += _deviceRestoredHandler;

        _player.Events.ErrorOccurred += err =>
        {
            if (CancellationHelper.IsCancellationLike(err.Exception)) return;
            if (err.Exception is AudioSourceException && CancellationHelper.IsCancellationLike(err.Exception?.InnerException)) return;

            var ex = err.Exception;
            if (ex is AudioDeviceException)
            {
                RaiseError(new AudioDeviceException(err.Message, ex?.InnerException));
            }
            else if (ex is CacheInvalidatedException cacheEx)
            {
                HandleCacheInvalidated(cacheEx);
            }
            else
            {
                RaiseError(new AudioException(err.Message, ex));
            }
        };
    }

    private void HandlePlayerStateChanged(PlaybackState state)
    {
        // Intention is checked against state: quality change and recovery trigger PlayAsync bypassing SetPlaybackStateAsync.
        // Preserve _playIntent across device-loss pauses so queue mutations continue auto-advancing on reconnect.
        if (state == PlaybackState.Playing)
            _playIntent = true;
        else if (state == PlaybackState.Paused && _player.GetActivePipeline()?.IsDeviceLost != true)
            _playIntent = false;

        ApplyLifecycleSourceSuspendPolicy();
        RaiseOnUI(_raiseStateChangedOnUIDelegate);
    }

    /// <summary>
    /// Handler for natural track completion.
    /// Routed via typed command to preserve actor invariant.
    /// </summary>
    private void HandlePlayerTrackEnded()
    {
        if (_player.State == PlaybackState.Loading || Volatile.Read(ref _manualLoadingFlag) != 0)
            return;

        EnqueueCommand(new NavigateCommand(
            Forward: true,
            UserInitiated: false,
            EndedTrackId: CurrentTrack?.Id,
            ReplaceSeq: Volatile.Read(ref _queueReplaceSeq),
            Session: _session.Current,
            PauseEpoch: Volatile.Read(ref _pauseEpoch)));
    }

    private void HandleStreamInfoChanged(AudioStreamInfo info)
    {
        RaiseOnUI(() => { StreamInfo = info; OnStreamInfoChanged?.Invoke(info); });
    }

    #endregion
}