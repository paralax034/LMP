using Avalonia.Threading;

namespace LMP.Core.Services;

/// <summary>
/// Unified playback management coordinator for transport commands, volume, track likes,
/// and live synchronization between the active playlist and the playback queue.
/// </summary>
public sealed class PlayerControlService : IDisposable
{
    private const int LiveSyncDebounceMs = 75;
    private const double LowBufferYieldThresholdPercent = 15.0;

    internal static bool ShouldYieldToAudioBuffering(
        bool isLoading,
        bool isPlaying,
        bool isFullyBuffered,
        double bufferProgressPercent) =>
        isLoading || (isPlaying && !isFullyBuffered && bufferProgressPercent < LowBufferYieldThresholdPercent);

    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly YoutubeProvider? _youtube;
    private readonly CookieAuthService? _auth;
    private readonly TrackRegistry? _trackRegistry;
    private readonly PlaylistSyncService? _playlistSync;
    private readonly PlaylistService? _playlistService;

    private readonly Lock _liveSyncLock = new();
    private CancellationTokenSource? _liveSyncCts;
    private readonly Action<RepeatMode, bool> _playbackModesChangedHandler;

    private string? _lastTrackId;
    private int _lastNotifiedRepeatMode;
    private volatile bool _lastNotifiedShuffleEnabled;
    private volatile bool _lastCanGoNext;
    private volatile bool _lastCanGoPrevious;
    private int _currentVolume;
    private double _volumeOverdriveFactor = 1.0;
    private string? _activePlaylistId;
    private string? _activePlaylistName;
    private volatile bool _isQueuePure;
    private volatile bool _isPlayingPure;

    public bool IsPlaying => _audio.IsPlaying;

    public bool IsPaused => _audio.IsPaused;

    public bool IsLoading => _audio.IsLoading;

    public PlaybackState PlaybackState => _audio.EffectivePlaybackState;

    public bool CanGoNext => _audio.CanGoNext;

    public bool CanGoPrevious => _audio.CanGoPrevious;

    public TimeSpan CurrentPosition => _audio.CurrentPosition;

    public bool HasSeekableDuration => _audio.TotalDuration > TimeSpan.Zero;

    public TimeSpan TotalDuration
    {
        get
        {
            var audioDuration = _audio.TotalDuration;
            return audioDuration > TimeSpan.Zero
                ? audioDuration
                : (CurrentTrack?.Duration ?? TimeSpan.Zero);
        }
    }

    public TrackInfo? CurrentTrack => _audio.CurrentTrack;

    public RepeatMode RepeatMode => _audio.RepeatMode;

    public bool ShuffleEnabled => _audio.ShuffleEnabled;

    public int QueueCount => _audio.Queue.Count;

    /// <summary>
    /// Zero-based index of the currently active track inside <see cref="Queue"/>, or -1 if none.
    /// </summary>
    public int CurrentQueueIndex => _audio.CurrentQueueIndex;

    /// <summary>
    /// Snapshot of the current playback queue from <see cref="AudioEngine"/>.
    /// </summary>
    public IReadOnlyList<TrackInfo> Queue => _audio.Queue;

    public int CurrentVolume
    {
        get => Volatile.Read(ref _currentVolume);
        private set => Volatile.Write(ref _currentVolume, value);
    }

    /// <summary>
    /// Normalized animation intensity multiplier derived from current volume and audio boost settings.
    /// </summary>
    public double VolumeOverdriveFactor
    {
        get => Volatile.Read(ref _volumeOverdriveFactor);
        private set => Volatile.Write(ref _volumeOverdriveFactor, value);
    }

    /// <summary>
    /// ID of the playlist from which the current queue was launched.
    /// Null = source is not a playlist (Home, Search, single track, or cleared queue).
    /// </summary>
    public string? ActivePlaylistId
    {
        get => Volatile.Read(ref _activePlaylistId);
        private set => Volatile.Write(ref _activePlaylistId, value);
    }

    /// <summary>
    /// Display name of the active source playlist, or <see langword="null"/> if the source is manual.
    /// Dynamically reflects localization changes for system playlists.
    /// </summary>
    public string? ActivePlaylistName
    {
        get => Volatile.Read(ref _activePlaylistName);
        private set => Volatile.Write(ref _activePlaylistName, value);
    }

    /// <summary>
    /// Indicates whether the active playback queue matches the composition of the launched playlist.
    /// </summary>
    public bool IsQueuePure { get => _isQueuePure; private set => _isQueuePure = value; }

    /// <summary>
    /// Indicates whether the pristine queue of the launched playlist is currently playing.
    /// </summary>
    public bool IsPlayingPure { get => _isPlayingPure; private set => _isPlayingPure = value; }

    public bool HasTrack => CurrentTrack != null;

    #region Events

    public event Action? ForceSyncTriggered;
    public event Action? ResumeRequested;

    public event Action<bool>? IsPlayingChanged;
    public event Action<bool>? IsPausedChanged;
    public event Action<bool>? IsLoadingChanged;
    public event Action<PlaybackState>? PlaybackStateChanged;
    public event Action? CanNavigateChanged;
    public event Action<TimeSpan>? SeekCompleted;
    public event Action<TimeSpan>? PositionChanged;
    public event Action<TrackInfo?>? CurrentTrackChanged;
    public event Action<RepeatMode>? RepeatModeChanged;
    public event Action<bool>? ShuffleEnabledChanged;
    public event Action? QueueChanged;
    public event Action<int>? QueueCountChanged;
    public event Action<int>? VolumeChanged;
    public event Action<double>? VolumeOverdriveChanged;

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
        YoutubeProvider? youtube = null,
        CookieAuthService? auth = null,
        TrackRegistry? trackRegistry = null,
        PlaylistSyncService? playlistSync = null,
        PlaylistService? playlistService = null)
    {
        _audio = audio;
        _library = library;
        _youtube = youtube;
        _auth = auth;
        _trackRegistry = trackRegistry;
        _playlistSync = playlistSync;
        _playlistService = playlistService;

        if (_playlistSync != null)
        {
            _playlistSync.IsAudioBufferingCheck = () => ShouldYieldToAudioBuffering(
                _audio.IsLoading,
                _audio.IsPlaying,
                _audio.IsFullyBuffered,
                _audio.BufferProgress);
        }

        _lastTrackId = _audio.CurrentTrack?.Id;
        _lastCanGoNext = _audio.CanGoNext;
        _lastCanGoPrevious = _audio.CanGoPrevious;
        Volatile.Write(ref _lastNotifiedRepeatMode, (int)_audio.RepeatMode);
        _lastNotifiedShuffleEnabled = _audio.ShuffleEnabled;
        CurrentVolume = (int)Math.Round(_audio.GetVolume());
        RefreshVolumeOverdrive(raiseEvent: false);
        SyncSourceAndPurityFromEngine(raiseEvent: false);

        _audio.OnPlaybackStateChanged += HandlePlaybackStateChanged;
        _audio.OnDetailedPlaybackStateChanged += HandleDetailedPlaybackStateChanged;
        _playbackModesChangedHandler = (_, _) => HandlePlaybackModesChanged();
        _audio.OnPlaybackModesChanged += _playbackModesChangedHandler;
        _audio.OnSeekCompleted += HandleSeekCompleted;
        _audio.OnPositionChanged += HandlePositionChanged;
        _audio.OnTrackChanged += HandleTrackChanged;
        _audio.OnTrackPlaybackStarted += HandleTrackPlaybackStarted;
        _audio.OnQueueChanged += HandleQueueChanged;
        _audio.OnQueueSourceChanged += HandleQueueSourceChanged;
        _audio.OnLoadingStateChanged += HandleLoadingStateChanged;
        _audio.OnMaxVolumeChanged += HandleMaxVolumeChanged;
        _audio.OnAudioSettingsChanged += HandleAudioSettingsChanged;
        LocalizationService.Instance.LanguageChanged += HandleLanguageChanged;

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
        RefreshVolumeOverdrive(raiseEvent: true);
        ForceSync();
    }

    #endregion

    #region Commands

    public async Task PlayAsync()
    {
        try
        {
            await _audio.PlayAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"[PlayerControl] Play error: {ex.Message}");
        }
    }

    public async Task PauseAsync()
    {
        try
        {
            await _audio.PauseAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"[PlayerControl] Pause error: {ex.Message}");
        }
    }

    public async Task PlayPauseAsync()
    {
        try
        {
            await _audio.TogglePlaybackStateAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"[PlayerControl] PlayPause error: {ex.Message}");
        }
    }

    public void StopPlaybackKeepTrack()
    {
        try
        {
            _audio.StopPlaybackKeepTrack();
        }
        catch (Exception ex)
        {
            Log.Error($"[PlayerControl] Stop error: {ex.Message}");
        }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        try
        {
            var duration = _audio.TotalDuration;
            if (duration <= TimeSpan.Zero)
                return;

            var clamped = position < TimeSpan.Zero
                ? TimeSpan.Zero
                : position > duration ? duration : position;

            await _audio.SeekAsync(clamped).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"[PlayerControl] Seek error: {ex.Message}");
        }
    }

    public Task SeekRelativeAsync(TimeSpan delta)
    {
        return SeekAsync(_audio.CurrentPosition + delta);
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
        RefreshNavigationCapabilities();
        NotifyRepeatModeIfChanged(newMode);

        Log.Debug($"[PlayerControl] RepeatMode changed to {newMode}");
    }

    /// <summary>
    /// Validates, registers, and adds a range of unique tracks to the playback queue.
    /// </summary>
    public int EnqueueRangeUnique(IEnumerable<TrackInfo> tracks)
    {
        if (_disposed || tracks is null) return 0;

        IReadOnlyList<TrackInfo> materialized = tracks as IReadOnlyList<TrackInfo> ?? [.. tracks];
        if (materialized.Count == 0) return 0;

        if (_trackRegistry != null)
        {
            for (int i = 0; i < materialized.Count; i++)
            {
                var track = materialized[i];
                if (track != null)
                    _trackRegistry.RegisterOrUpdate(track);
            }
        }

        return _audio.EnqueueRangeUnique(materialized);
    }

    /// <summary>
    /// Safely removes a track from the playback queue.
    /// </summary>
    public void RemoveFromQueue(TrackInfo track)
    {
        if (_disposed || track is null) return;
        _audio.RemoveFromQueue(track);
    }

    /// <summary>
    /// Coordinates single-track playback: cancels pending playlist live-sync, registers the track,
    /// records playback history, and starts playback with an error boundary.
    /// </summary>
    public async Task PlayTrackAsync(TrackInfo track)
    {
        if (_disposed || track is null) return;

        if (AudioEngine.FindTrackIndexById(_audio.Queue, track.Id) < 0)
        {
            CancelPendingLiveSync();
        }

        var canonical = _trackRegistry?.RegisterOrUpdate(track) ?? track;

        try
        {
            await _audio.PlayTrackAsync(canonical).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"[PlayerControl] PlayTrack error for '{canonical.Id}': {ex.Message}");
        }
    }

    /// <summary>
    /// Cancels active playlist synchronization and clears the playback queue.
    /// </summary>
    public void ClearQueue()
    {
        if (_disposed) return;

        CancelPendingLiveSync();
        _audio.ClearQueue();
        Log.Debug("[PlayerControl] Queue cleared");
    }

    /// <summary>
    /// Moves a track within the playback queue after validating indices.
    /// </summary>
    public void MoveQueueItem(int from, int to)
    {
        if (_disposed || from == to || from < 0 || to < 0) return;
        _audio.MoveQueueItem(from, to);
    }

    public void ShuffleQueue()
    {
        if (_disposed) return;
        _audio.ShuffleQueue();
        Log.Debug("[PlayerControl] Queue shuffled");
    }

    /// <summary>
    /// Toggles auto-shuffle.
    /// Synchronizes AudioEngine, persists to settings, and updates state.
    /// </summary>
    public void ToggleAutoShuffle()
    {
        if (_disposed) return;

        bool newState = !_audio.ShuffleEnabled;
        _audio.ShuffleEnabled = newState;

        if (newState)
            _audio.ShuffleQueue();

        _library.UpdateSettings(s => s.ShuffleEnabled = newState);
        NotifyShuffleEnabledIfChanged(newState);

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
        NotifyShuffleEnabledIfChanged(enabled);

        Log.Debug($"[PlayerControl] ShuffleEnabled set to {enabled}");
    }

    /// <summary>
    /// Starts playlist or multi-track playback while registering its <see cref="QueueSource"/> atomically inside <see cref="AudioEngine"/>.
    /// Guarantees the presence of the start track in the playback queue and records history.
    /// </summary>
    /// <param name="playlistId">Playlist ID (or empty string for an ad-hoc track list).</param>
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
        if (_disposed || (tracks.Count == 0 && startTrack == null)) return;

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

        string? normalizedPlaylistId = string.IsNullOrWhiteSpace(playlistId) ? null : playlistId;

        try
        {
            await _audio.StartQueueAsync(effectiveTracks, targetStartTrack, normalizedPlaylistId, playlistName)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"[PlayerControl] PlayPlaylist error for '{playlistId}': {ex.Message}");
        }
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
    private int GetEffectiveMaxVolume()
    {
        int maxVolume = _library.Settings.MaxVolumeLimit;
        return maxVolume > 0 ? maxVolume : 100;
    }

    public int AdjustVolume(int delta)
    {
        int currentVolume = (int)Math.Round(_audio.GetVolume());
        int newVolume = Math.Clamp(currentVolume + delta, 0, GetEffectiveMaxVolume());
        SetVolume(newVolume);
        return newVolume;
    }

    /// <summary>
    /// Sets playback volume, applies it to the audio pipeline, and schedules background persistence.
    /// </summary>
    /// <param name="volume">New volume value (0–MaxVolume).</param>
    public void SetVolume(int volume)
    {
        int clamped = Math.Clamp(volume, 0, GetEffectiveMaxVolume());
        int current = (int)Math.Round(_audio.GetVolume());

        if (clamped != current)
        {
            _audio.SetVolumeInstant(clamped);
            CurrentVolume = clamped;
            VolumeChanged?.Invoke(clamped);
            RefreshVolumeOverdrive(raiseEvent: true);
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

        ScheduleLivePlaylistSync(LibraryService.LikedPlaylistId, source.Generation, ResolveActivePlaylistName(source));
    }

    private void ScheduleLivePlaylistSync(string playlistId, int expectedGeneration, string? updatedName)
    {
        if (_playlistService == null) return;

        CancellationTokenSource cts;
        CancellationTokenSource? previousCts;
        lock (_liveSyncLock)
        {
            if (_disposed) return;
            previousCts = _liveSyncCts;
            cts = _liveSyncCts = new CancellationTokenSource();
        }

        if (previousCts != null)
        {
            try { previousCts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        _ = RunLivePlaylistSyncAsync(playlistId, expectedGeneration, updatedName, cts);
    }

    private void CancelPendingLiveSync()
    {
        CancellationTokenSource? previousCts;
        lock (_liveSyncLock)
        {
            previousCts = _liveSyncCts;
            _liveSyncCts = null;
        }

        if (previousCts != null)
        {
            try { previousCts.Cancel(); }
            catch (ObjectDisposedException) { }
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
                    _liveSyncCts = null;
            }

            cts.Dispose();
        }
    }

    #endregion

    #region AudioEngine Event Handlers

    private void HandlePlaybackStateChanged(bool isPlaying, bool isPaused)
    {
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
        var previousId = Interlocked.Exchange(ref _lastTrackId, track?.Id);

        RefreshNavigationCapabilities();

        if (string.Equals(previousId, track?.Id, StringComparison.Ordinal))
            return;

        CurrentTrackChanged?.Invoke(track);
        SyncSourceAndPurityFromEngine(raiseEvent: true);
    }

    private void HandleTrackPlaybackStarted(TrackInfo track)
    {
        if (_disposed)
            return;

        _ = RecordRecentlyPlayedSafeAsync(track);
    }

    private async Task RecordRecentlyPlayedSafeAsync(TrackInfo track)
    {
        try
        {
            await _library.AddToRecentlyPlayedAsync(track).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"[PlayerControl] Failed to record playback history for '{track.Id}': {ex.Message}");
        }
    }

    private void HandleQueueChanged()
    {
        QueueCountChanged?.Invoke(_audio.Queue.Count);
        QueueChanged?.Invoke();
        RefreshNavigationCapabilities();
    }

    private void HandleQueueSourceChanged()
    {
        SyncSourceAndPurityFromEngine(raiseEvent: true);
    }

    private static string? ResolveActivePlaylistName(QueueSource source)
    {
        if (source.Kind != QueueSourceKind.Playlist)
            return null;

        if (string.Equals(source.Id, LibraryService.LikedPlaylistId, StringComparison.Ordinal))
            return LocalizationService.Instance["Playlist_Liked"];

        return source.Name;
    }

    private void HandleLanguageChanged(object? sender, string _)
    {
        if (_disposed) return;

        if (string.Equals(ActivePlaylistId, LibraryService.LikedPlaylistId, StringComparison.Ordinal))
            SyncSourceAndPurityFromEngine(raiseEvent: true);
    }

    private void HandleAudioSettingsChanged()
    {
        if (_disposed) return;
        RefreshVolumeOverdrive(raiseEvent: true);
    }

    private void SyncSourceAndPurityFromEngine(bool raiseEvent)
    {
        var source = _audio.QueueSource;
        string? newPlaylistId = source.Kind == QueueSourceKind.Playlist ? source.Id : null;
        string? newPlaylistName = ResolveActivePlaylistName(source);
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
        IsLoadingChanged?.Invoke(isLoading);
    }

    private void HandleDetailedPlaybackStateChanged(PlaybackState state)
    {
        PlaybackStateChanged?.Invoke(state);
    }

    private void HandlePlaybackModesChanged()
    {
        NotifyRepeatModeIfChanged(_audio.RepeatMode);
        NotifyShuffleEnabledIfChanged(_audio.ShuffleEnabled);
        RefreshNavigationCapabilities();
    }

    private void NotifyRepeatModeIfChanged(RepeatMode mode)
    {
        int next = (int)mode;
        if (Interlocked.Exchange(ref _lastNotifiedRepeatMode, next) != next)
            RepeatModeChanged?.Invoke(mode);
    }

    private void NotifyShuffleEnabledIfChanged(bool enabled)
    {
        if (_lastNotifiedShuffleEnabled == enabled)
            return;

        _lastNotifiedShuffleEnabled = enabled;
        ShuffleEnabledChanged?.Invoke(enabled);
    }

    private void HandleSeekCompleted(TimeSpan position)
    {
        SeekCompleted?.Invoke(position);
    }

    private void HandlePositionChanged(TimeSpan position)
    {
        PositionChanged?.Invoke(position);
    }

    private void RefreshNavigationCapabilities()
    {
        bool next = _audio.CanGoNext;
        bool prev = _audio.CanGoPrevious;
        if (_lastCanGoNext == next && _lastCanGoPrevious == prev)
            return;

        _lastCanGoNext = next;
        _lastCanGoPrevious = prev;
        CanNavigateChanged?.Invoke();
    }

    private void HandleMaxVolumeChanged(int newMaxVolume)
    {
        int actualVolume = (int)Math.Round(_audio.GetVolume());
        if (CurrentVolume != actualVolume)
        {
            CurrentVolume = actualVolume;
            VolumeChanged?.Invoke(actualVolume);
        }

        RefreshVolumeOverdrive(raiseEvent: true);
    }

    private void RefreshVolumeOverdrive(bool raiseEvent)
    {
        var settings = _library.Settings;
        double factor = AudioEngine.ComputeVolumeOverdrive(
            CurrentVolume,
            settings.MaxVolumeLimit,
            settings.Audio.VolumeBoostEnabled);

        if (Math.Abs(VolumeOverdriveFactor - factor) <= 0.0001)
            return;

        VolumeOverdriveFactor = factor;
        if (!raiseEvent)
            return;

        if (Dispatcher.UIThread.CheckAccess())
            VolumeOverdriveChanged?.Invoke(factor);
        else
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed)
                    VolumeOverdriveChanged?.Invoke(factor);
            });
    }

    #endregion

    #region Sync

    /// <summary>
    /// Forces synchronization of all states when restoring from tray.
    /// </summary>
    public void ForceSync()
    {
        Volatile.Write(ref _lastNotifiedRepeatMode, (int)_audio.RepeatMode);
        _lastNotifiedShuffleEnabled = _audio.ShuffleEnabled;
        _lastCanGoNext = _audio.CanGoNext;
        _lastCanGoPrevious = _audio.CanGoPrevious;
        CurrentVolume = (int)Math.Round(_audio.GetVolume());
        RefreshVolumeOverdrive(raiseEvent: true);

        var actualTrack = _audio.CurrentTrack;
        var previousId = Interlocked.Exchange(ref _lastTrackId, actualTrack?.Id);
        if (!string.Equals(previousId, actualTrack?.Id, StringComparison.Ordinal))
        {
            CurrentTrackChanged?.Invoke(actualTrack);
        }

        PlaybackStateChanged?.Invoke(PlaybackState);
        CanNavigateChanged?.Invoke();
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

        if (_playlistSync != null)
            _playlistSync.IsAudioBufferingCheck = null;

        _library.OnInitialized -= HandleLibraryInitialized;
        _library.OnTrackUpdated -= HandleTrackUpdated;

        if (_playlistService != null)
        {
            _playlistService.OnPlaylistChanged -= HandlePlaylistChanged;
            _playlistService.OnPlaylistRemoved -= HandlePlaylistRemoved;
        }

        _audio.OnPlaybackStateChanged -= HandlePlaybackStateChanged;
        _audio.OnDetailedPlaybackStateChanged -= HandleDetailedPlaybackStateChanged;
        _audio.OnPlaybackModesChanged -= _playbackModesChangedHandler;
        _audio.OnSeekCompleted -= HandleSeekCompleted;
        _audio.OnPositionChanged -= HandlePositionChanged;
        _audio.OnTrackChanged -= HandleTrackChanged;
        _audio.OnTrackPlaybackStarted -= HandleTrackPlaybackStarted;
        _audio.OnQueueChanged -= HandleQueueChanged;
        _audio.OnQueueSourceChanged -= HandleQueueSourceChanged;
        _audio.OnLoadingStateChanged -= HandleLoadingStateChanged;
        _audio.OnMaxVolumeChanged -= HandleMaxVolumeChanged;
        _audio.OnAudioSettingsChanged -= HandleAudioSettingsChanged;
        LocalizationService.Instance.LanguageChanged -= HandleLanguageChanged;

        Log.Debug("[PlayerControl] Service disposed");
    }

    #endregion
}