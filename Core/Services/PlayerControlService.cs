namespace LMP.Core.Services;

/// <summary>
/// Unified playback management coordinator.
/// Provides properties and events synchronized across all UI components.
///
/// <para><b>Architecture:</b></para>
/// <list type="bullet">
///   <item>Acts as the sole subscriber to AudioEngine events for state tracking</item>
///   <item>Provides ObservableObject properties and typed C# events for PlayerBar, TrayIcon, MediaKeys</item>
///   <item>Operates independently of the window suspend/resume state</item>
///   <item>Serves as the single point of volume control — the UI does not access AudioEngine directly</item>
/// </list>
///
/// <para><b>ForceSync:</b></para>
/// <para>Does not re-emit notifications for CurrentTrack if the object is the same (by Id).
/// This prevents false TrackReset signals when restoring from the tray.</para>
///
/// <para><b>Shuffle:</b></para>
/// <para>All ShuffleEnabled changes MUST go through this service (SetShuffleEnabled / ToggleAutoShuffle)
/// to keep the state synchronized with AudioEngine at all times.</para>
///
/// <para><b>N-Token Warning:</b></para>
/// <para>Complex decipher warning is published via <see cref="NTokenWarning"/>
/// and simultaneously shown via <see cref="NotificationService"/> (if available).</para>
/// </summary>
public sealed partial class PlayerControlService : ObservableObject, IDisposable
{
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly NotificationService? _notificationService;
    private readonly YoutubeProvider? _youtube;
    private readonly CookieAuthService? _auth;
    private readonly TrackRegistry? _trackRegistry;
    private readonly PlaylistSyncService? _playlistSync;

    private readonly HashSet<string> _activePlaylistTrackIds = new(StringComparer.Ordinal);
    private int _expectedPlaylistTrackCount;

    [ObservableProperty] public partial bool IsPlaying { get; set; }

    [ObservableProperty] public partial bool IsPaused { get; set; }

    [ObservableProperty] public partial bool IsLoading { get; set; }

    [ObservableProperty] public partial TrackInfo? CurrentTrack { get; set; }

    [ObservableProperty] public partial RepeatMode RepeatMode { get; set; }

    [ObservableProperty] public partial bool ShuffleEnabled { get; set; }

    [ObservableProperty] public partial int QueueCount { get; set; }

    [ObservableProperty] public partial int CurrentVolume { get; set; }

    /// <summary>
    /// ID of the playlist from which the current queue was launched.
    /// Null = source is not a playlist (Home, Search, single track).
    /// </summary>
    [ObservableProperty]
    public partial string? ActivePlaylistId { get; set; }

    /// <summary>
    /// Indicates whether the active playback queue matches the original composition of the launched playlist.
    /// </summary>
    [ObservableProperty]
    public partial bool IsQueuePure { get; private set; }

    /// <summary>
    /// Indicates whether the pristine queue of the launched playlist is currently playing.
    /// </summary>
    [ObservableProperty]
    public partial bool IsPlayingPure { get; private set; }

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
    /// Event raised when the purity of the active playlist queue changes.
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
        PlaylistSyncService? playlistSync = null)
    {
        _audio = audio;
        _library = library;
        _notificationService = notificationService;
        _youtube = youtube;
        _auth = auth;
        _trackRegistry = trackRegistry;
        _playlistSync = playlistSync;

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

        _audio.OnPlaybackStateChanged += HandlePlaybackStateChanged;
        _audio.OnTrackChanged += HandleTrackChanged;
        _audio.OnQueueChanged += HandleQueueChanged;
        _audio.OnLoadingStateChanged += HandleLoadingStateChanged;
        _audio.OnNTokenDecryptionWarning += HandleNTokenDecryptionWarning;

        // If DB is already loaded — sync UI state, if not — wait for Initialized
        if (_library.IsInitialized)
        {
            _audio.InitializeVolumeFromSettings();
            ForceSync();
        }
        else
        {
            _library.OnInitialized += () =>
            {
                _audio.InitializeVolumeFromSettings();
                ForceSync();
            };
        }

        Log.Debug("[PlayerControl] Service initialized");
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

        // When enabling shuffle — immediately shuffle the current queue.
        // The user immediately sees the randomized order in QueueView.
        // When disabling — order remains as is (already shuffled).
        if (newState)
            _audio.ShuffleQueue();

        _library.UpdateSettings(s => s.ShuffleEnabled = newState);
        ShuffleEnabled = newState;
        ShuffleEnabledChanged?.Invoke(newState);

        Log.Debug($"[PlayerControl] AutoShuffle changed to {newState}");
    }

    /// <summary>
    /// Directly sets the auto-shuffle state.
    /// Used from PlaylistViewModel and other places that need
    /// to explicitly set shuffle = false before starting a queue.
    ///
    /// <para><b>IMPORTANT:</b> All ShuffleEnabled changes must go through this method
    /// or ToggleAutoShuffle() to keep the property synchronized.</para>
    /// </summary>
    /// <param name="enabled">New auto-shuffle state.</param>
    public void SetShuffleEnabled(bool enabled)
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
    /// Starts playlist playback while fully registering its track composition to track queue purity in memory.
    /// Guarantees the presence of the start track in the playback queue even if it was deleted externally from the data source.
    /// </summary>
    /// <param name="playlistId">Playlist ID.</param>
    /// <param name="tracks">Full ordered list of playlist tracks.</param>
    /// <param name="startTrack">Track to start playback from (defaults to the first track).</param>
    /// <param name="enableShuffle">Whether to enable shuffle mode.</param>
    public async Task PlayPlaylistAsync(
        string playlistId,
        IReadOnlyList<TrackInfo> tracks,
        TrackInfo? startTrack = null,
        bool? enableShuffle = null)
    {
        if (tracks.Count == 0 && startTrack == null) return;

        if (enableShuffle.HasValue)
        {
            SetShuffleEnabled(enableShuffle.Value);
        }

        bool isShuffled = enableShuffle ?? ShuffleEnabled;

        IReadOnlyList<TrackInfo> effectiveTracks = tracks;
        if (startTrack != null && !tracks.Any(t => string.Equals(t.Id, startTrack.Id, StringComparison.Ordinal)))
        {
            var list = new List<TrackInfo>(tracks.Count + 1) { startTrack };
            list.AddRange(tracks);
            effectiveTracks = list;
        }

        ActivePlaylistId = playlistId;
        _activePlaylistTrackIds.Clear();
        _expectedPlaylistTrackCount = effectiveTracks.Count;

        for (int i = 0; i < effectiveTracks.Count; i++)
        {
            _activePlaylistTrackIds.Add(effectiveTracks[i].Id);
        }

        var targetStartTrack = startTrack ??
                               (isShuffled
                                   ? effectiveTracks[Random.Shared.Next(effectiveTracks.Count)]
                                   : effectiveTracks[0]);
        await _audio.StartQueueAsync(effectiveTracks, targetStartTrack).ConfigureAwait(false);

        UpdatePurityState();
    }

    /// <summary>
    /// Toggles the "Like" mark state for a track locally and in the YouTube cloud.
    /// </summary>
    /// <param name="track">Track instance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Resolves the canonical instance in the registry, ensuring flag consistency
    /// across all UI views and collections.
    /// </remarks>
    public async Task ToggleLikeAsync(TrackInfo track, CancellationToken ct = default)
    {
        var canonical = _trackRegistry?.RegisterOrUpdate(track, hasUserContext: true)
                        ?? _library.GetTrack(track.Id)
                        ?? track;

        bool previousLikedState = canonical.IsLiked;
        bool targetLikedState = !previousLikedState;

        canonical.SetLikedState(targetLikedState);
        _trackRegistry?.UpdatePinStatus(canonical);

        bool syncToCloud = _auth?.IsAuthenticated == true && _library.Settings.LikeSyncMode != LikeSyncMode.LocalOnly;

        // Intent is pushed to Outbox synchronously before the first await: enqueue order matches click order
        if (syncToCloud && _playlistSync != null)
            _playlistSync.EnqueueLikeMutation(canonical.Id, targetLikedState, previousLikedState);

        // Optimistic Local-First: commit state in SQLite and notify UI
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
    /// Used for tray mouse wheel and hotkeys.
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
    /// Acts as the single entry point for volume modification across the application.
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
    /// Called when the user interacts with the UI while in suspend mode.
    /// </summary>
    public void RequestResume()
    {
        ResumeRequested?.Invoke();
    }

    #endregion

    #region AudioEngine Event Handlers

    private void HandlePlaybackStateChanged(bool isPlaying, bool isPaused)
    {
        IsPlaying = isPlaying;
        IsPaused = isPaused;
        IsPlayingChanged?.Invoke(isPlaying);
        IsPausedChanged?.Invoke(isPaused);

        UpdatePurityState();
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

        // Reset source only on actual stop (track → null).
        // During track transitions (prev != null → new != null) source is retained —
        // this is expected: user is listening to the same playlist.
        if (track == null && ActivePlaylistId != null)
        {
            ActivePlaylistId = null;
            _activePlaylistTrackIds.Clear();
            _expectedPlaylistTrackCount = 0;
            UpdatePurityState();
            Log.Debug("[PlayerControl] ActivePlaylistId cleared (track → null)");
        }
    }

    private void HandleQueueChanged()
    {
        QueueCount = _audio.Queue.Count;
        QueueCountChanged?.Invoke(QueueCount);

        UpdatePurityState();
    }

    private void UpdatePurityState()
    {
        bool isPure = false;

        if (!string.IsNullOrEmpty(ActivePlaylistId) && _expectedPlaylistTrackCount > 0)
        {
            var queue = _audio.Queue;
            if (queue.Count == _expectedPlaylistTrackCount)
            {
                isPure = true;
                for (int i = 0; i < queue.Count; i++)
                {
                    if (!_activePlaylistTrackIds.Contains(queue[i].Id))
                    {
                        isPure = false;
                        break;
                    }
                }
            }
        }

        bool purityChanged = IsQueuePure != isPure;
        bool playingPure = isPure && IsPlaying;
        bool playingPureChanged = IsPlayingPure != playingPure;

        IsQueuePure = isPure;
        IsPlayingPure = playingPure;

        if (purityChanged || playingPureChanged)
        {
            PlaybackPurityChanged?.Invoke(ActivePlaylistId, IsQueuePure, IsPlayingPure);
        }
    }

    private void HandleLoadingStateChanged(bool isLoading)
    {
        IsLoading = isLoading;
        IsLoadingChanged?.Invoke(isLoading);
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

    /// <summary>
    /// Publishes a complex n-token deciphering notification
    /// either as a toast or strictly in the notification center.
    /// </summary>
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
    ///
    /// <para><b>IMPORTANT:</b> Does NOT re-emit CurrentTrack if the track is the same (by Id).
    /// This prevents false TrackReset signals when restoring from tray.</para>
    ///
    /// <para>Instead, calls ForceSync, which PlayerBarViewModel
    /// subscribes to for soft updates (position, buffer, stream info).</para>
    ///
    /// <para>If the actual track in AudioEngine differs from cached (by Id),
    /// publishes the new track via CurrentTrack.</para>
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
            // Update reference without re-emitting event
            CurrentTrack = actualTrack;
        }

        UpdatePurityState();
        ForceSyncTriggered?.Invoke();

        Log.Debug("[PlayerControl] Forced sync completed (soft, no track reset)");
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _audio.OnPlaybackStateChanged -= HandlePlaybackStateChanged;
        _audio.OnTrackChanged -= HandleTrackChanged;
        _audio.OnQueueChanged -= HandleQueueChanged;
        _audio.OnLoadingStateChanged -= HandleLoadingStateChanged;
        _audio.OnNTokenDecryptionWarning -= HandleNTokenDecryptionWarning;

        Log.Debug("[PlayerControl] Service disposed");
    }

    #endregion
}

