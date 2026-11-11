using Avalonia.Threading;

namespace LMP.Core.Services;

/// <summary>
/// Unified playback management coordinator for transport commands, volume, track likes,
/// and live synchronization between the active playlist and the playback queue.
/// </summary>
public sealed class PlayerControlService : IDisposable
{
    private const int LiveSyncDebounceMs = 75;

    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly NotificationService? _notificationService;
    private readonly YoutubeProvider? _youtube;
    private readonly CookieAuthService? _auth;
    private readonly TrackRegistry? _trackRegistry;
    private readonly PlaylistSyncService? _playlistSync;
    private readonly PlaylistService? _playlistService;

    private readonly Lock _liveSyncLock = new();
    private CancellationTokenSource? _liveSyncCts;

    public bool IsPlaying { get; private set; }

    public bool IsPaused { get; private set; }

    public bool IsLoading { get; private set; }

    public TrackInfo? CurrentTrack { get; private set; }

    public RepeatMode RepeatMode { get; private set; }

    public bool ShuffleEnabled { get; private set; }

    public int QueueCount { get; private set; }

    /// <summary>
    /// Snapshot of the current playback queue from <see cref="AudioEngine"/>.
    /// </summary>
    public IReadOnlyList<TrackInfo> Queue => _audio.Queue;

    public int CurrentVolume { get; private set; }

    /// <summary>
    /// ID of the playlist from which the current queue was launched.
    /// Null = source is not a playlist (Home, Search, single track, or cleared queue).
    /// </summary>
    public string? ActivePlaylistId { get; private set; }

    /// <summary>
    /// Display name of the active source playlist, or <see langword="null"/> if the source is manual.
    /// </summary>
    public string? ActivePlaylistName { get; private set; }

    /// <summary>
    /// Indicates whether the active playback queue matches the composition of the launched playlist.
    /// </summary>
    public bool IsQueuePure { get; private set; }

    /// <summary>
    /// Indicates whether the pristine queue of the launched playlist is currently playing.
    /// </summary>
    public bool IsPlayingPure { get; private set; }

    public bool HasTrack => CurrentTrack != null;

    #region Events

    public event Action? ForceSyncTriggered;
    public event Action? ResumeRequested;

    public event Action<bool>? IsPlayingChanged;
    public event Action<bool>? IsPausedChanged;
    public event Action<bool>? IsLoadingChanged;
    public event Action<TrackInfo?>? CurrentTrackChanged;
    public event Action<RepeatMode>? RepeatModeChanged;
    public event Action<bool>? ShuffleEnabledChanged;
    public event Action<int>? QueueCountChanged;
    public event Action<int>? VolumeChanged;

    /// <summary>
    /// Event raised when the active playlist source or queue purity changes.
    /// Arguments: ActivePlaylistId, IsQueuePure, IsPlayingPure.
    /// </summary>
    public event Action<string?, bool, bool>? PlaybackPurityChanged;

    #endregion

    private bool _disposed;

    #region Constructors

    public PlayerControlService(
        AudioEngine audio,
        LibraryService library,
        NotificationService? notificationService = null,
        YoutubeProvider? youtube = null,
        CookieAuthService? auth = null,
        TrackRegistry? trackRegistry = null,
        PlaylistSyncService? playlistSync = null,
        PlaylistService? playlistService = null)
    {
        _audio = audio;
        _library = library;
        _notificationService = notificationService;
        _youtube = youtube;
        _auth = auth;
        _trackRegistry = trackRegistry;
        _playlistSync = playlistSync;
        _playlistService = playlistService;

        if (_playlistSync != null)
        {
            _playlistSync.IsAudioBufferingCheck = () =>
                _audio.IsLoading || (_audio.IsPlaying && !_audio.IsFullyBuffered && _audio.BufferProgress < 0.15);
        }

        CurrentTrack = _audio.CurrentTrack;
        IsPlaying = _audio.IsPlaying;
        IsPaused = _audio.IsPaused;
        IsLoading = _audio.IsLoading;
        RepeatMode = _audio.RepeatMode;
        ShuffleEnabled = _audio.ShuffleEnabled;
        QueueCount = _audio.Queue.Count;
        CurrentVolume = (int)Math.Round(_audio.GetVolume());
        SyncSourceAndPurityFromEngine(raiseEvent: false);

        _audio.OnPlaybackStateChanged += HandlePlaybackStateChanged;
        _audio.OnTrackChanged += HandleTrackChanged;
        _audio.OnQueueChanged += HandleQueueChanged;
        _audio.OnQueueSourceChanged += HandleQueueSourceChanged;
        _audio.OnLoadingStateChanged += HandleLoadingStateChanged;
        _audio.OnMaxVolumeChanged += HandleMaxVolumeChanged;
        _audio.OnNTokenDecryptionWarning += HandleNTokenDecryptionWarning;

        if (_playlistService != null)
        {
            _playlistService.OnPlaylistChanged += HandlePlaylistChanged;
            _playlistService.OnPlaylistRemoved += HandlePlaylistRemoved;
        }

        _library.OnTrackUpdated += HandleTrackUpdated;

        if (!_library.IsInitialized)
        {
            _library.OnInitialized += HandleLibraryInitialized;
        }

        Log.Debug("[PlayerControl] Service initialized");
    }

    private void HandleLibraryInitialized()
    {
        ForceSync();
    }

    #endregion

    #region Commands

    public async Task PlayPauseAsync()
    {
        try
        {
            await _audio.SetPlaybackStateAsync(!_audio.IsPlaying);
        }
        catch (Exception ex)
        {
            Log.Error($"[PlayerControl] PlayPause error: {ex.Message}");
        }
    }

    public async Task NextAsync()
    {
        try
        {
            await _audio.PlayNextAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"[PlayerControl] Next error: {ex.Message}");
        }
    }

    public async Task PreviousAsync()
    {
        try
        {
            await _audio.PlayPreviousAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"[PlayerControl] Previous error: {ex.Message}");
        }
    }

    public void ToggleRepeat()
    {
        var newMode = _audio.RepeatMode switch
        {
            RepeatMode.None => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.None
        };

        _audio.RepeatMode = newMode;
        _library.UpdateSettings(s => s.RepeatMode = newMode);
        RepeatMode = newMode;
        RepeatModeChanged?.Invoke(newMode);

        Log.Debug($"[PlayerControl] RepeatMode changed to {newMode}");
    }

    /// <summary>
    /// Adds a range of unique tracks to the playback queue via <see cref="AudioEngine"/>.
    /// </summary>
    public int EnqueueRangeUnique(IEnumerable<TrackInfo> tracks) =>
        _audio.EnqueueRangeUnique(tracks);

    /// <summary>
    /// Removes a track from the playback queue via <see cref="AudioEngine"/>.
    /// </summary>
    public void RemoveFromQueue(TrackInfo track) =>
        _audio.RemoveFromQueue(track);

    /// <summary>
    /// Starts playback of a specific track via <see cref="AudioEngine"/>.
    /// </summary>
    public Task PlayTrackAsync(TrackInfo track) =>
        _audio.PlayTrackAsync(track);

    /// <summary>
    /// Clears the playback queue (preserving the currently playing track if any).
    /// </summary>
    public void ClearQueue()
    {
        _audio.ClearQueue();
        Log.Debug("[PlayerControl] Queue cleared");
    }

    /// <summary>
    /// Moves a track within the playback queue from one index to another.
    /// </summary>
    public void MoveQueueItem(int from, int to) =>
        _audio.MoveQueueItem(from, to);

    public void ShuffleQueue()
    {
        _audio.ShuffleQueue();
        Log.Debug("[PlayerControl] Queue shuffled");
    }

    /// <summary>
    /// Toggles auto-shuffle.
    /// Synchronizes AudioEngine, persists to settings, and updates state.
    /// </summary>
    public void ToggleAutoShuffle()
    {
        bool newState = !_audio.ShuffleEnabled;
        _audio.ShuffleEnabled = newState;

        if (newState)
            _audio.ShuffleQueue();

        _library.UpdateSettings(s => s.ShuffleEnabled = newState);
        ShuffleEnabled = newState;
        ShuffleEnabledChanged?.Invoke(newState);

        Log.Debug($"[PlayerControl] AutoShuffle changed to {newState}");
    }

    /// <summary>
    /// Directly sets the auto-shuffle state.
    /// </summary>
    /// <param name="enabled">New auto-shuffle state.</param>
    private void SetShuffleEnabled(bool enabled)
    {
        if (_audio.ShuffleEnabled == enabled)
            return;

        _audio.ShuffleEnabled = enabled;
        _library.UpdateSettings(s => s.ShuffleEnabled = enabled);
        ShuffleEnabled = enabled;
        ShuffleEnabledChanged?.Invoke(enabled);

        Log.Debug($"[PlayerControl] ShuffleEnabled set to {enabled}");
    }

    /// <summary>
    /// Starts playlist playback while registering its <see cref="QueueSource"/> atomically inside <see cref="AudioEngine"/>.
    /// Guarantees the presence of the start track in the playback queue even if it was deleted externally from the data source.
    /// </summary>
    /// <param name="playlistId">Playlist ID.</param>
    /// <param name="tracks">Full ordered list of playlist tracks.</param>
    /// <param name="startTrack">Track to start playback from (defaults to the first track).</param>
    /// <param name="enableShuffle">Whether to enable shuffle mode.</param>
    /// <param name="playlistName">Optional playlist display name for the queue source header.</param>
    public async Task PlayPlaylistAsync(
        string playlistId,
        IReadOnlyList<TrackInfo> tracks,
        TrackInfo? startTrack = null,
        bool? enableShuffle = null,
        string? playlistName = null)
    {
        if (tracks.Count == 0 && startTrack == null) return;

        CancelPendingLiveSync();

        if (enableShuffle.HasValue)
        {
            SetShuffleEnabled(enableShuffle.Value);
        }

        bool isShuffled = enableShuffle ?? ShuffleEnabled;

        IReadOnlyList<TrackInfo> effectiveTracks = tracks;
        if (startTrack != null && AudioEngine.FindTrackIndexById(tracks, startTrack.Id) < 0)
        {
            var list = new List<TrackInfo>(tracks.Count + 1) { startTrack };
            list.AddRange(tracks);
            effectiveTracks = list;
        }

        var targetStartTrack = startTrack ??
                               (isShuffled
                                   ? effectiveTracks[Random.Shared.Next(effectiveTracks.Count)]
                                   : effectiveTracks[0]);

        await _audio.StartQueueAsync(effectiveTracks, targetStartTrack, playlistId, playlistName)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Toggles the "Like" mark state for a track locally and in the YouTube cloud.
    /// </summary>
    /// <param name="track">Track instance.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ToggleLikeAsync(TrackInfo track, CancellationToken ct = default)
    {
        var canonical = _trackRegistry?.RegisterOrUpdate(track, hasUserContext: true)
                        ?? _library.GetTrack(track.Id)
                        ?? track;

        bool previousLikedState = canonical.IsLiked;
        bool targetLikedState = !previousLikedState;

        bool syncToCloud = _auth?.IsAuthenticated == true && _library.Settings.LikeSyncMode != LikeSyncMode.LocalOnly;

        // Intent is pushed to Outbox synchronously before the first await: enqueue order matches click order
        if (syncToCloud && _playlistSync != null)
            _playlistSync.EnqueueLikeMutation(canonical.Id, targetLikedState, previousLikedState);

        // Optimistic Local-First: commit state in SQLite and notify UI (SetLikeStateAsync updates L1 and pin status)
        await _library.SetLikeStateAsync(canonical, targetLikedState, ct).ConfigureAwait(false);

        if (syncToCloud && _playlistSync == null && _youtube != null)
        {
            try
            {
                await _youtube.LikeTrackAsync(canonical.Id, targetLikedState).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error($"[PlayerControl] Failed to sync like to YouTube: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Changes volume by the specified step (positive or negative).
    /// </summary>
    /// <param name="delta">Amount of volume change.</param>
    /// <returns>The new volume value.</returns>
    public int AdjustVolume(int delta)
    {
        int currentVolume = (int)Math.Round(_audio.GetVolume());
        int maxVolume = _library.Settings.MaxVolumeLimit;
        if (maxVolume <= 0) maxVolume = 100;

        int newVolume = Math.Clamp(currentVolume + delta, 0, maxVolume);
        SetVolume(newVolume);
        return newVolume;
    }

    /// <summary>
    /// Sets playback volume, applies it to the audio pipeline, and schedules background persistence.
    /// </summary>
    /// <param name="volume">New volume value (0–MaxVolume).</param>
    public void SetVolume(int volume)
    {
        int maxVolume = _library.Settings.MaxVolumeLimit;
        if (maxVolume <= 0) maxVolume = 100;

        int clamped = Math.Clamp(volume, 0, maxVolume);
        int current = (int)Math.Round(_audio.GetVolume());

        if (clamped != current)
        {
            _audio.SetVolumeInstant(clamped);
            CurrentVolume = clamped;
            VolumeChanged?.Invoke(clamped);
        }
    }

    /// <summary>
    /// Returns current volume from AudioEngine (rounded to int).
    /// </summary>
    public int GetCurrentVolume() => (int)Math.Round(_audio.GetVolume());

    /// <summary>
    /// Requests Resume from MainWindow (via OnResumeRequested).
    /// </summary>
    public void RequestResume()
    {
        ResumeRequested?.Invoke();
    }

    #endregion

    #region Live Playlist Synchronization

    private void HandlePlaylistRemoved(string playlistId)
    {
        if (_disposed || string.IsNullOrEmpty(playlistId)) return;

        var source = _audio.QueueSource;
        if (source.Kind == QueueSourceKind.Playlist
            && string.Equals(source.Id, playlistId, StringComparison.Ordinal))
        {
            CancelPendingLiveSync();
            _audio.DetachPlaylistSource(playlistId);
        }
    }

    private void HandlePlaylistChanged(Playlist playlist)
    {
        if (_disposed || playlist == null) return;

        var source = _audio.QueueSource;
        if (source.Kind != QueueSourceKind.Playlist
            || !string.Equals(source.Id, playlist.Id, StringComparison.Ordinal))
        {
            return;
        }

        ScheduleLivePlaylistSync(playlist.Id, source.Generation, playlist.Name);
    }

    private void HandleTrackUpdated(TrackInfo track)
    {
        if (_disposed) return;

        var source = _audio.QueueSource;
        if (!AudioEngine.ShouldSyncLikedOnTrackUpdated(source, track))
            return;

        ScheduleLivePlaylistSync(LibraryService.LikedPlaylistId, source.Generation, source.Name);
    }

    private void ScheduleLivePlaylistSync(string playlistId, int expectedGeneration, string? updatedName)
    {
        if (_playlistService == null) return;

        CancellationTokenSource cts;
        lock (_liveSyncLock)
        {
            if (_disposed) return;
            _liveSyncCts?.Cancel();
            _liveSyncCts?.Dispose();
            cts = _liveSyncCts = new CancellationTokenSource();
        }

        _ = RunLivePlaylistSyncAsync(playlistId, expectedGeneration, updatedName, cts);
    }

    private void CancelPendingLiveSync()
    {
        lock (_liveSyncLock)
        {
            _liveSyncCts?.Cancel();
            _liveSyncCts?.Dispose();
            _liveSyncCts = null;
        }
    }

    private async Task RunLivePlaylistSyncAsync(
        string playlistId,
        int expectedGeneration,
        string? updatedName,
        CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            if (!await ct.DelayNoThrowAsync(LiveSyncDebounceMs, continueOnCapturedContext: false).ConfigureAwait(false))
                return;

            var source = _audio.QueueSource;
            if (source.Kind != QueueSourceKind.Playlist
                || source.Generation != expectedGeneration
                || !string.Equals(source.Id, playlistId, StringComparison.Ordinal))
            {
                return;
            }

            var freshTrackIds = await _playlistService!
                .GetPlaylistTrackIdsAsync(playlistId, ct)
                .ConfigureAwait(false);

            if (ct.IsCancellationRequested) return;

            List<string>? addedIds = null;
            for (int i = 0; i < freshTrackIds.Count; i++)
            {
                string id = freshTrackIds[i];
                if (!string.IsNullOrEmpty(id) && !source.ContainsTrack(id))
                {
                    addedIds ??= [];
                    addedIds.Add(id);
                }
            }

            IReadOnlyList<TrackInfo> addedTracks = [];
            if (addedIds != null && addedIds.Count > 0)
            {
                if (_trackRegistry != null)
                {
                    addedTracks = await _trackRegistry
                        .PreloadAndReturnAsync(addedIds, ct)
                        .ConfigureAwait(false);
                }
                else
                {
                    var loaded = new List<TrackInfo>(addedIds.Count);
                    for (int i = 0; i < addedIds.Count; i++)
                    {
                        var t = await _library.GetTrackAsync(addedIds[i], ct).ConfigureAwait(false);
                        if (t != null)
                            loaded.Add(t);
                    }
                    addedTracks = loaded;
                }
            }

            if (ct.IsCancellationRequested) return;

            _audio.ApplyPlaylistDelta(
                playlistId,
                expectedGeneration,
                freshTrackIds,
                addedTracks,
                updatedName);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"[PlayerControl] Live playlist sync failed for '{playlistId}': {ex.Message}");
        }
        finally
        {
            lock (_liveSyncLock)
            {
                if (ReferenceEquals(_liveSyncCts, cts))
                {
                    cts.Dispose();
                    _liveSyncCts = null;
                }
            }
        }
    }

    #endregion

    #region AudioEngine Event Handlers

    private void HandlePlaybackStateChanged(bool isPlaying, bool isPaused)
    {
        IsPlaying = isPlaying;
        IsPaused = isPaused;
        IsPlayingChanged?.Invoke(isPlaying);
        IsPausedChanged?.Invoke(isPaused);

        SyncSourceAndPurityFromEngine(raiseEvent: true);
    }

    /// <summary>
    /// Handles track change from AudioEngine.
    /// Does not re-emit the event if track Id hasn't changed,
    /// preventing false TrackReset calls when restoring from tray.
    /// </summary>
    private void HandleTrackChanged(TrackInfo? track)
    {
        var previous = CurrentTrack;
        CurrentTrack = track;

        if (previous?.Id == track?.Id)
            return;

        CurrentTrackChanged?.Invoke(track);
        SyncSourceAndPurityFromEngine(raiseEvent: true);
    }

    private void HandleQueueChanged()
    {
        QueueCount = _audio.Queue.Count;
        QueueCountChanged?.Invoke(QueueCount);

        SyncSourceAndPurityFromEngine(raiseEvent: true);
    }

    private void HandleQueueSourceChanged()
    {
        SyncSourceAndPurityFromEngine(raiseEvent: true);
    }

    private void SyncSourceAndPurityFromEngine(bool raiseEvent)
    {
        var source = _audio.QueueSource;
        string? newPlaylistId = source.Kind == QueueSourceKind.Playlist ? source.Id : null;
        string? newPlaylistName = source.Kind == QueueSourceKind.Playlist ? source.Name : null;
        bool isPure = _audio.IsQueuePure;
        bool playingPure = isPure && IsPlaying;

        bool sourceChanged = !string.Equals(ActivePlaylistId, newPlaylistId, StringComparison.Ordinal)
                             || !string.Equals(ActivePlaylistName, newPlaylistName, StringComparison.Ordinal);
        bool purityChanged = IsQueuePure != isPure;
        bool playingPureChanged = IsPlayingPure != playingPure;

        ActivePlaylistId = newPlaylistId;
        ActivePlaylistName = newPlaylistName;
        IsQueuePure = isPure;
        IsPlayingPure = playingPure;

        if (raiseEvent && (sourceChanged || purityChanged || playingPureChanged))
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                PlaybackPurityChanged?.Invoke(ActivePlaylistId, IsQueuePure, IsPlayingPure);
            }
            else
            {
                string? capturedId = ActivePlaylistId;
                bool capturedPure = IsQueuePure;
                bool capturedPlayingPure = IsPlayingPure;
                Dispatcher.UIThread.Post(() =>
                    PlaybackPurityChanged?.Invoke(capturedId, capturedPure, capturedPlayingPure));
            }
        }
    }

    private void HandleLoadingStateChanged(bool isLoading)
    {
        IsLoading = isLoading;
        IsLoadingChanged?.Invoke(isLoading);
    }

    private void HandleMaxVolumeChanged(int newMaxVolume)
    {
        int actualVolume = (int)Math.Round(_audio.GetVolume());
        if (CurrentVolume != actualVolume)
        {
            CurrentVolume = actualVolume;
            VolumeChanged?.Invoke(actualVolume);
        }
    }

    /// <summary>
    /// Publishes a complex n-token deciphering warning
    /// and, if permitted by settings, either adds it to the notification center
    /// or shows a toast.
    /// </summary>
    private void HandleNTokenDecryptionWarning(AudioEngine.NTokenWarningInfo warning)
    {
        if (_notificationService == null)
            return;

        var mode = _library.Settings.Audio.NTokenNotificationMode;
        switch (mode)
        {
            case NTokenNotificationMode.Disabled:
                Log.Debug($"[PlayerControl] N-Token warning suppressed for track '{warning.Track?.Id}' as configured.");
                return;

            case NTokenNotificationMode.PanelOnly:
                _ = PublishNTokenWarningAsync(_notificationService, warning, showToast: false);
                return;

            default:
                _ = PublishNTokenWarningAsync(_notificationService, warning, showToast: true);
                return;
        }
    }

    private static async Task PublishNTokenWarningAsync(
        NotificationService notificationService,
        AudioEngine.NTokenWarningInfo warning,
        bool showToast)
    {
        try
        {
            var track = warning.Track;
            string trackDisplay = track?.Title ?? track?.Id ?? "Unknown";
            string? trackTitle = track?.Title ?? track?.Id;
            string messageKey = warning.WasSkipped
                ? "Notification_NToken_Skipped"
                : "Notification_NToken_Message";

            if (showToast)
            {
                await notificationService.ShowToastAsync(
                    titleKey: "Notification_NToken_Title",
                    messageKey: messageKey,
                    severity: NotificationSeverity.Warning,
                    messageArgs: [trackDisplay],
                    trackId: track?.Id,
                    trackTitle: trackTitle);
            }
            else
            {
                await notificationService.AddToPanelAsync(
                    titleKey: "Notification_NToken_Title",
                    messageKey: messageKey,
                    severity: NotificationSeverity.Warning,
                    messageArgs: [trackDisplay],
                    trackId: track?.Id,
                    trackTitle: trackTitle);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[PlayerControl] Failed to publish n-token warning: {ex.Message}");
        }
    }

    #endregion

    #region Sync

    /// <summary>
    /// Forces synchronization of all states when restoring from tray.
    /// </summary>
    public void ForceSync()
    {
        IsPlaying = _audio.IsPlaying;
        IsPaused = _audio.IsPaused;
        IsLoading = _audio.IsLoading;
        RepeatMode = _audio.RepeatMode;
        ShuffleEnabled = _audio.ShuffleEnabled;
        QueueCount = _audio.Queue.Count;
        CurrentVolume = (int)Math.Round(_audio.GetVolume());

        var actualTrack = _audio.CurrentTrack;
        if (CurrentTrack?.Id != actualTrack?.Id)
        {
            CurrentTrack = actualTrack;
            CurrentTrackChanged?.Invoke(actualTrack);
        }
        else
        {
            CurrentTrack = actualTrack;
        }

        SyncSourceAndPurityFromEngine(raiseEvent: true);
        ForceSyncTriggered?.Invoke();

        Log.Debug("[PlayerControl] Forced sync completed (soft, no track reset)");
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CancelPendingLiveSync();

        _library.OnInitialized -= HandleLibraryInitialized;
        _library.OnTrackUpdated -= HandleTrackUpdated;

        if (_playlistService != null)
        {
            _playlistService.OnPlaylistChanged -= HandlePlaylistChanged;
            _playlistService.OnPlaylistRemoved -= HandlePlaylistRemoved;
        }

        _audio.OnPlaybackStateChanged -= HandlePlaybackStateChanged;
        _audio.OnTrackChanged -= HandleTrackChanged;
        _audio.OnQueueChanged -= HandleQueueChanged;
        _audio.OnQueueSourceChanged -= HandleQueueSourceChanged;
        _audio.OnLoadingStateChanged -= HandleLoadingStateChanged;
        _audio.OnMaxVolumeChanged -= HandleMaxVolumeChanged;
        _audio.OnNTokenDecryptionWarning -= HandleNTokenDecryptionWarning;

        Log.Debug("[PlayerControl] Service disposed");
    }

    #endregion
}