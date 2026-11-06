namespace LMP.Core.Services;

public sealed partial class AudioEngine
{
    /// <summary>
    /// Skips the current track requiring complex n-token decryption.
    /// </summary>
    private void SkipCurrentTrackRequiringNToken(string? skippedTrackId)
    {
        Interlocked.CompareExchange(ref _nTokenActiveTrackId, null, skippedTrackId);

        int session = BeginNewSession();
        _player.Stop();

        bool canAdvance;
        lock (_queueLock) { canAdvance = TryMoveNextSkippingTrack(skippedTrackId); }

        if (canAdvance)
        {
            EnqueueCommand(new PlayCurrentIndexCommand(session));
            return;
        }

        StopAfterFatalPlaybackError();
    }

    /// <summary>
    /// Handler for the start of n-token decryption.
    /// </summary>
    private void HandleNTokenDecryptionStarted(string rawVideoId)
    {
        var activeTrackId = Interlocked.CompareExchange(ref _nTokenActiveTrackId, null, null);
        if (activeTrackId == null || IsSealedFailedTrack(activeTrackId))
            return;

        var currentTrack = CurrentTrack;
        if (currentTrack?.Id != activeTrackId
            || !currentTrack.GetRawIdSpan().SequenceEqual(rawVideoId.AsSpan()))
            return;

        var previous = Interlocked.CompareExchange(ref _nTokenWarnedTrackId, activeTrackId, null);
        if (previous != null) return;

        bool wasSkipped = _library.Settings.Audio.SkipNTokenTracks;
        RaiseOnUI(() => OnNTokenDecryptionWarning?.Invoke(new NTokenWarningInfo(currentTrack, wasSkipped)));

        if (wasSkipped)
            SkipCurrentTrackRequiringNToken(currentTrack.Id);
    }
}