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
                            await PlayCurrentIndexAsync(pci.Session, startPlaying: pci.StartPlaying).ConfigureAwait(false);
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
        await PlayCurrentIndexAsync(cmd.Session, cmd.SeekPosition).ConfigureAwait(false);
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
        await PlayCurrentIndexAsync(cmd.Session).ConfigureAwait(false);
    }

    private async Task HandleNavigateAsync(NavigateCommand cmd)
    {
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

        int session = BeginNewSession();

        if (queueMutated) RaiseOnUI(_raiseQueueChangedOnUIDelegate);

        if (canMove)
            await PlayCurrentIndexAsync(session, startPlaying: cmd.StartPlaying).ConfigureAwait(false);
        else if (!cmd.Forward && _player.State != PlaybackState.Stopped)
            await _player.SeekAsync(TimeSpan.Zero).ConfigureAwait(false);
        else
            Stop();
    }

    #endregion

    #region Playback Core

    /// <summary>
    /// Starts track playback at the current queue index with an optional position and auto-start flag.
    /// </summary>
    /// <param name="session">Playback session identifier.</param>
    /// <param name="seekPosition">Initial playback position when seeking.</param>
    /// <param name="startPlaying">Automatic playback start flag.</param>
    private Task PlayCurrentIndexAsync(int session, TimeSpan? seekPosition = null, bool startPlaying = true)
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
        var playTask = PlayTrackCoreAsync(track, session, GetSessionToken(), seekPosition, startPlaying);
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
       bool startPlaying = true)
    {
        if (_session.IsStaleOrCancelled(session, ct) || IsSealedFailedTrack(track.Id))
            return;

        Log.Debug($"[AudioEngine] [PlayTrackCore] Initiating playback for track: {track.Id} | " +
                          $"Session: {session} | StartPlaying: {startPlaying}");

        FlushPendingEqualizerSave();

        // Mute/stop the player only when switching to a DIFFERENT track.
        // On retry/resync of the same track, PlayAsync will atomically replace the pipeline without a 30-second silence gap.
        if (!string.Equals(CurrentTrack?.Id, track.Id, StringComparison.Ordinal))
        {
            _player.Stop();
        }

        if (_session.IsStaleOrCancelled(session, ct) || IsSealedFailedTrack(track.Id))
            return;

        _playIntent = startPlaying;
        SetManualLoading(true);

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

            CurrentTrack = track;
            StreamInfo = AudioStreamInfo.Empty;

            RaiseOnUI(() =>
            {
                OnTrackChanged?.Invoke(track);
                OnPositionChanged?.Invoke(TimeSpan.Zero);
            });

            if (!startPlaying)
            {
                SetManualLoading(false);
                return;
            }

            ct.ThrowIfCancellationRequested();
            Volatile.Write(ref _nTokenActiveTrackId, track.Id);
            Volatile.Write(ref _nTokenWarnedTrackId, null);

            AudioSourceFactory.PreWarmCdnConnections(
                Audio.Http.SharedHttpClient.Instance, _lifetimeCts.Token);

            const int maxStartupAttempts = 3;
            const int maxCdnFailoverAttempts = 3;

            for (int attempt = 1; attempt <= maxStartupAttempts; attempt++)
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    // Call asynchronous ResolveStreamAsync directly without an extra Task.Run wrapper,
                    // which caused a cascade of duplicate TaskCanceledExceptions in the thread pool.
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

                    if (_session.IsStaleOrCancelled(session, ct) || IsSealedFailedTrack(track.Id))
                        return;

                    Log.Info($"[AudioEngine] PlayTrackCore resolved -> {descriptor}");

                    await _player.PlayAsync(descriptor, ct, seekPosition: seekPosition)
                        .ConfigureAwait(false);

                    PreWarmNextTracksInQueue(
                        CurrentQueueIndex,
                        Audio.Http.SharedHttpClient.Instance,
                        _lifetimeCts.Token);
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // User switched track — exit instantly without retries
                    throw;
                }
                catch (Exception ex)
                    when (TryExtractCdnException(ex) is { } cdnEx
                          && attempt < maxCdnFailoverAttempts)
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
                    when (!ct.IsCancellationRequested && attempt < maxStartupAttempts)
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
            if (!ct.IsCancellationRequested)
            {
                Log.Warn($"[AudioEngine] Playback startup aborted: {ex.Message}");
                _player.Stop();
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

            Interlocked.CompareExchange(ref _nTokenActiveTrackId, null, track.Id);
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

        RaiseOnUI(() =>
        {
            OnPropertyChanged(nameof(IsPlaying));
            OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(TotalDuration));
            OnPlaybackStateChanged?.Invoke(state == PlaybackState.Playing, state == PlaybackState.Paused);
            OnLoadingStateChanged?.Invoke(IsLoading);
        });
    }

    /// <summary>
    /// Handler for natural track completion.
    /// Routed via typed command to preserve actor invariant.
    /// </summary>
    private void HandlePlayerTrackEnded()
    {
        if (_player.State is PlaybackState.Loading or PlaybackState.Buffering) return;
        EnqueueCommand(new NavigateCommand(Forward: true, UserInitiated: false, EndedTrackId: CurrentTrack?.Id));
    }

    private void HandleStreamInfoChanged(AudioStreamInfo info)
    {
        RaiseOnUI(() => { StreamInfo = info; OnStreamInfoChanged?.Invoke(info); });
    }

    #endregion
}