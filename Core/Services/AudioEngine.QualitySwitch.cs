using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Normalization;

namespace LMP.Core.Services;

public sealed partial class AudioEngine
{
    #region Quality Switching

    public Task SwitchQualityAsync(AudioFormat format, int bitrate)
    {
        if (CurrentTrack == null) return Task.CompletedTask;

        ResetSealedFailedTrack();
        int session = BeginNewSession();

        var track = CurrentTrack;
        track.TransientFormat = format;
        track.TransientBitrate = bitrate;

        if (_library.Settings.RememberTrackFormat)
        {
            track.PreferredFormat = format;
            track.PreferredBitrate = bitrate;
        }

        EnqueueCommand(new SwitchQualityCommand(track, TimeSpan.Zero, format, bitrate, session));
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

            Volatile.Write(ref _nTokenActiveTrackId, cmd.Track.Id);

            var descriptor = await _youtube.RefreshStreamAsync(cmd.Track, false, ct).ConfigureAwait(false)
                             ?? await _youtube.RefreshStreamAsync(cmd.Track, true, ct).ConfigureAwait(false);

            if (descriptor == null)
            {
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
                return;
            }

            if (d.HasPerceptualLufs)
            {
                cmd.Track.SetIntegratedLufs(d.IntegratedLufs, LoudnessSource.YoutubePerceptual);
                CommitIntegratedLufs(cmd.Track.Id, d.IntegratedLufs, LoudnessSource.YoutubePerceptual);
            }

            if (previousTask is { IsCompleted: false })
                await previousTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            if (_session.IsStaleOrCancelled(cmd.Session, ct) || IsSealedFailedTrack(cmd.Track.Id)) return;

            // Sample live position after cooldown/resolution so playback does not rewind by 2 seconds.
            TimeSpan resumePosition = CurrentPosition;
            await _player.PlayAsync(
                d,
                ct,
                seekPosition: resumePosition.TotalSeconds > 1 ? resumePosition : null)
                .ConfigureAwait(false);

            ApplyGainToPipeline();
        }
        catch (Exception ex)
        {
            if (!_session.IsStaleOrCancelled(cmd.Session, ct) && !CancellationHelper.IsCancellationLike(ex))
            {
                AbortCurrentTrackPlaybackAfterFatalError(cmd.Track.Id);
                RaiseError(ex);
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _nTokenActiveTrackId, null, cmd.Track.Id);
        }
    }

    #endregion
}