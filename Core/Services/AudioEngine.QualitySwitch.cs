using LMP.Core.Audio.Normalization;

namespace LMP.Core.Services;

public sealed partial class AudioEngine
{
    #region Quality Switching

    public Task SwitchQualityAsync(AudioFormat format, int bitrate)
    {
        if (CurrentTrack is not { } track) return Task.CompletedTask;

        TimeSpan livePos = CurrentPosition;
        if (livePos.TotalSeconds > 1)
            Volatile.Write(ref _lastSwitchPositionTicks, livePos.Ticks);
        else
            livePos = TimeSpan.FromTicks(Volatile.Read(ref _lastSwitchPositionTicks));

        ResetSealedFailedTrack();
        int session = BeginNewSession();
        track.TransientFormat = format;
        track.TransientBitrate = bitrate;

        if (_library.Settings.RememberTrackFormat)
        {
            track.PreferredFormat = format;
            track.PreferredBitrate = bitrate;
        }

        EnqueueCommand(new SwitchQualityCommand(track, livePos, format, bitrate, session));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Schedules quality switching outside the command actor so cooldown and resolution never freeze queue commands.
    /// </summary>
    private Task HandleSwitchQualityAsync(SwitchQualityCommand cmd)
    {
        var ct = GetSessionToken();
        if (_session.IsStaleOrCancelled(cmd.Session, ct))
            return Task.CompletedTask;

        var previousTask = Interlocked.Exchange(ref _activePlayTask, null);
        var switchTask = SwitchQualityCoreAsync(cmd, ct, previousTask);
        Volatile.Write(ref _activePlayTask, switchTask);
        return Task.CompletedTask;
    }

    private async Task SwitchQualityCoreAsync(SwitchQualityCommand cmd, CancellationToken ct, Task? previousTask)
    {
        if (_session.IsStaleOrCancelled(cmd.Session, ct)) return;

        SetManualLoading(true);

        try
        {
            var elapsed = (DateTime.UtcNow - _lastQualitySwitchTime).TotalMilliseconds;
            if (elapsed < QualitySwitchCooldownMs &&
                !await ct.DelayNoThrowAsync(QualitySwitchCooldownMs - (int)elapsed, continueOnCapturedContext: false).ConfigureAwait(false))
                return;

            if (_session.IsStaleOrCancelled(cmd.Session, ct) || IsSealedFailedTrack(cmd.Track.Id))
                return;

            _lastQualitySwitchTime = DateTime.UtcNow;

            Log.Info($"[AudioEngine] SwitchQuality start: track={cmd.Track.Id}, requestedFormat={cmd.Track.TransientFormat?.ToContainerName() ?? "-"}, requestedBitrate={cmd.Track.TransientBitrate}");

            lock (_playerDispatchLock)
            {
                if (_session.IsStaleOrCancelled(cmd.Session, ct) || IsSealedFailedTrack(cmd.Track.Id))
                    return;

                Volatile.Write(ref _nTokenWarnedTrackId, null);
                Volatile.Write(ref _nTokenActiveSession, cmd.Session);
                Volatile.Write(ref _nTokenActiveTrackId, cmd.Track.Id);
            }

            if (previousTask is { IsCompleted: false })
                await previousTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            const int maxCdnFailoverAttempts = 3;
            for (int attempt = 1; attempt <= maxCdnFailoverAttempts; attempt++)
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    var descriptor = await _youtube.RefreshStreamAsync(cmd.Track, false, ct).ConfigureAwait(false)
                                     ?? await _youtube.RefreshStreamAsync(cmd.Track, true, ct).ConfigureAwait(false);

                    if (descriptor == null)
                    {
                        lock (_playerDispatchLock)
                        {
                            if (!_session.IsStale(cmd.Session))
                                TrySyncActivePlaybackSessionLocked(cmd.Track, cmd.Session);
                        }

                        if (!_session.IsStale(cmd.Session))
                            RaiseError(new InvalidOperationException("No stream available"));
                        return;
                    }

                    if (_session.IsStaleOrCancelled(cmd.Session, ct) || IsSealedFailedTrack(cmd.Track.Id)) return;

                    var d = descriptor.Value;

                    Log.Info($"[AudioEngine] SwitchQuality resolved -> {d}");

                    var currentInfo = _player.StreamInfo;
                    if (currentInfo.IsValid
                        && string.Equals(currentInfo.TrackId, d.TrackId, StringComparison.Ordinal)
                        && currentInfo.Format == d.Format
                        && currentInfo.CodecType == d.Codec
                        && currentInfo.Bitrate == d.BitrateKbps)
                    {
                        Log.Info($"[AudioEngine] SwitchQuality skipped: active pipeline already matches");
                        lock (_playerDispatchLock)
                        {
                            if (!_session.IsStale(cmd.Session))
                                TrySyncActivePlaybackSessionLocked(cmd.Track, cmd.Session, resumeClockIfPlaying: true);
                        }
                        RaiseOnUI(() =>
                        {
                            if (!_session.IsStale(cmd.Session))
                                OnStreamInfoChanged?.Invoke(_player.StreamInfo);
                        });
                        return;
                    }

                    if (d.HasPerceptualLufs)
                    {
                        cmd.Track.SetIntegratedLufs(d.IntegratedLufs, LoudnessSource.YoutubePerceptual);
                        CommitIntegratedLufs(cmd.Track.Id, d.IntegratedLufs, LoudnessSource.YoutubePerceptual);
                    }

                    Task? switchPlayTask;
                    bool shouldStartPaused;
                    lock (_playerDispatchLock)
                    {
                        if (_session.IsStaleOrCancelled(cmd.Session, ct) || IsSealedFailedTrack(cmd.Track.Id))
                            return;

                        shouldStartPaused = !_playIntent;

                        TimeSpan livePos = CurrentPosition;
                        TimeSpan resumePosition = livePos.TotalSeconds > 1
                            ? livePos
                            : (cmd.Position.TotalSeconds > 1
                                ? cmd.Position
                                : TimeSpan.FromTicks(Volatile.Read(ref _lastSwitchPositionTicks)));

                        if (resumePosition.TotalSeconds > 1)
                            Volatile.Write(ref _lastSwitchPositionTicks, resumePosition.Ticks);

                        switchPlayTask = _player.PlayAsync(
                            d,
                            ct,
                            seekPosition: resumePosition.TotalSeconds > 1 ? resumePosition : null);

                        if (shouldStartPaused)
                            _player.Pause();
                    }

                    await switchPlayTask.ConfigureAwait(false);

                    lock (_playerDispatchLock)
                    {
                        if (!_session.IsStaleOrCancelled(cmd.Session, ct)
                            && !TrySyncActivePlaybackSessionLocked(cmd.Track, cmd.Session, resumeClockIfPlaying: true)
                            && _playIntent
                            && _player.State == PlaybackState.Playing)
                        {
                            BeginPlaybackSessionLocked(cmd.Track, cmd.Session, isPlayingNow: true);
                        }
                    }

                    ApplyGainToPipeline();
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested || _session.IsStale(cmd.Session))
                {
                    throw;
                }
                catch (Exception ex)
                    when (TryExtractCdnException(ex) is { } cdnEx
                          && attempt < maxCdnFailoverAttempts
                          && !_session.IsStaleOrCancelled(cmd.Session, ct))
                {
                    Log.Warn($"[AudioEngine] SwitchQuality CDN unavailable: host={cdnEx.Host}, attempt={attempt}/{maxCdnFailoverAttempts}");
                    AudioSourceFactory.CdnBlacklist.MarkBlocked(cdnEx.Host);
                    _youtube.InvalidateMemoryCache(cmd.Track.Id);
                    Audio.Http.SessionCacheStore.Invalidate(cmd.Track.Id);

                    _player.Stop();
                    if (!await ct.DelayNoThrowAsync(300, continueOnCapturedContext: false).ConfigureAwait(false))
                        return;
                }
            }
        }
        catch (Exception ex)
        {
            if (!_session.IsStaleOrCancelled(cmd.Session, ct))
            {
                Log.Warn($"[AudioEngine] SwitchQuality aborted: {ex.Message}");
                AbortCurrentTrackPlaybackAfterFatalError(cmd.Track.Id);
                RaiseError(ex);
            }
        }
        finally
        {
            if (!_session.IsStale(cmd.Session))
                SetManualLoading(false);

            lock (_playerDispatchLock)
            {
                if (Volatile.Read(ref _nTokenActiveSession) == cmd.Session)
                {
                    Volatile.Write(ref _nTokenActiveTrackId, null);
                    Volatile.Write(ref _nTokenActiveSession, 0);
                }
            }
        }
    }

    #endregion
}