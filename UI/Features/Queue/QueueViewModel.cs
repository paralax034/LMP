using Avalonia.Media;
using Avalonia.Threading;

namespace LMP.UI.Features.Queue;

/// <summary>
/// ViewModel for the current playback queue screen.
/// Synchronizes queue items, live playback telemetry, and source playlist navigation with <see cref="PlayerControlService"/>.
/// </summary>
public sealed partial class QueueViewModel : TrackListBaseViewModel
{
    private readonly PlaylistService _playlistService;
    private readonly PlaylistEditService _editService;

    private int _selfMoveCounter;
    private string? _activePlaylistId;
    private bool _isLikedSource;
    private CancellationTokenSource? _colorLoadCts;
    private TimeSpan _elapsedBeforeCurrent;
    private TimeSpan _totalQueueDuration;
    private long _lastRenderedPositionSecond = long.MinValue;
    private bool _showRemainingTime;
    private bool _isPageActive;
    private bool _isLivePositionSubscribed;
    private bool _disposed;

    [ObservableProperty] public partial int CurrentTrackIndex { get; private set; } = -1;
    [ObservableProperty] public partial string CurrentTrackNumberDisplay { get; private set; } = "–";
    [ObservableProperty] public partial string FormattedElapsedDuration { get; private set; } = "0:00";
    [ObservableProperty] public partial string FormattedSecondaryDuration { get; private set; } = "0:00";
    [ObservableProperty] public partial string QueueTimeTooltip { get; private set; } = string.Empty;
    [ObservableProperty] public partial double QueueProgressRatio { get; private set; }
    [ObservableProperty] public partial bool HasSourcePlaylist { get; private set; }
    [ObservableProperty] public partial bool IsQueueMixed { get; private set; }
    [ObservableProperty] public partial Color? SourcePlaylistColor { get; private set; }
    [ObservableProperty] public partial bool IsPlaybackActive { get; private set; }
    [ObservableProperty] public partial double VolumeOverdriveFactor { get; private set; } = 1.0;
    [ObservableProperty] public partial double TopGlowAttenuation { get; set; } = 1.0;

    public IVirtualTrackList QueueItems => Items;

    public bool IsEmpty => TotalCount == 0;
    public override bool CanReorderItems => CanReorder && TotalCount > 1;

    public bool HasSourcePlaylistName => !string.IsNullOrWhiteSpace(PlayerControl.ActivePlaylistName);

    public string SourcePlaylistDisplayName =>
        !string.IsNullOrWhiteSpace(PlayerControl.ActivePlaylistName)
            ? PlayerControl.ActivePlaylistName
            : L["Nav_Playlist"];

    public string GoToPlaylistTooltip => HasSourcePlaylistName
        ? string.Format(L["Queue_GoToPlaylistNamed"], SourcePlaylistDisplayName)
        : L["Queue_GoToPlaylist"];

    public IRelayCommand ScrollToCurrentTrackCommand { get; }
    public IRelayCommand ToggleTimeDisplayModeCommand { get; }
    public IRelayCommand GoToSourcePlaylistCommand { get; }
    public IRelayCommand ClearQueueCommand { get; }
    public IRelayCommand ShuffleQueueCommand { get; }
    public IAsyncRelayCommand DownloadAllCommand { get; }
    public IAsyncRelayCommand SaveQueueToPlaylistCommand { get; }

    /// <summary>
    /// Raised when the user requests navigation to the queue's source playlist.
    /// </summary>
    public event Action<string>? NavigateToPlaylistRequested;

    /// <summary>
    /// Raised when the user requests scrolling the queue list to the currently playing track.
    /// </summary>
    public event Action? ScrollToCurrentRequested;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueueViewModel"/> class.
    /// </summary>
    public QueueViewModel(
        PlaylistService playlistService,
        PlaylistEditService editService,
        TrackViewModelFactory vmFactory,
        DownloadService downloads,
        PlayerControlService? playerControl = null)
        : base(downloads, vmFactory, playerControl)
    {
        _playlistService = playlistService;
        _editService = editService;

        _items.IsQueueContext = true;

        ScrollToCurrentTrackCommand = new RelayCommand(RequestScrollToCurrentTrack, () => CurrentTrackIndex >= 0);
        ToggleTimeDisplayModeCommand = new RelayCommand(ToggleTimeDisplayMode, () => TotalCount > 0);
        GoToSourcePlaylistCommand = new RelayCommand(GoToSourcePlaylist, () => HasSourcePlaylist);
        ClearQueueCommand = new RelayCommand(AudioClearQueue, () => TotalCount > 0);
        ShuffleQueueCommand = new RelayCommand(AudioShuffleQueue, () => TotalCount > 1);
        DownloadAllCommand = new AsyncRelayCommand(DownloadAllAsync, () => TotalCount > 0);
        SaveQueueToPlaylistCommand = new AsyncRelayCommand(SaveQueueToPlaylistAsync, () => TotalCount > 0);

        PlayerControl.QueueChanged += OnAudioQueueChanged;
        PlayerControl.CurrentTrackChanged += OnCurrentTrackChanged;
        PlayerControl.IsPlayingChanged += OnIsPlayingChanged;
        PlayerControl.PlaybackPurityChanged += OnPlaybackPurityChanged;
        PlayerControl.VolumeOverdriveChanged += OnPlayerVolumeOverdriveChanged;
        IsPlaybackActive = PlayerControl.IsPlaying;
        VolumeOverdriveFactor = PlayerControl.VolumeOverdriveFactor;
        _playlistService.OnPlaylistChanged += OnSourcePlaylistChanged;
        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;

        SyncWithAudioQueue();
        SyncSourcePlaylistState();
        IsLoading = false;
    }

    public override Task OnNavigatedToAsync()
    {
        _isPageActive = true;
        UpdateLivePositionSubscription(IsSuspended);
        IsPlaybackActive = PlayerControl.IsPlaying;
        VolumeOverdriveFactor = PlayerControl.VolumeOverdriveFactor;
        RefreshPlaybackTiming(force: true);
        if (_isLikedSource)
            ApplyLikedAccentColor();

        return base.OnNavigatedToAsync();
    }

    public override void OnNavigatedFrom()
    {
        _isPageActive = false;
        UpdateLivePositionSubscription(IsSuspended);
        base.OnNavigatedFrom();
    }

    protected override void OnSuspend(SuspendLevel level)
    {
        UpdateLivePositionSubscription(isSuspended: true);
        base.OnSuspend(level);
    }

    protected override void OnResume(SuspendLevel previousLevel)
    {
        base.OnResume(previousLevel);
        UpdateLivePositionSubscription(isSuspended: false);
        IsPlaybackActive = PlayerControl.IsPlaying;
        VolumeOverdriveFactor = PlayerControl.VolumeOverdriveFactor;
        if (_isPageActive)
            RefreshPlaybackTiming(force: true);
    }

    private void RequestScrollToCurrentTrack()
    {
        if (CurrentTrackIndex >= 0)
            ScrollToCurrentRequested?.Invoke();
    }

    private void ToggleTimeDisplayMode()
    {
        _showRemainingTime = !_showRemainingTime;
        RefreshPlaybackTiming(force: true);
    }

    private void GoToSourcePlaylist()
    {
        var playlistId = _activePlaylistId;
        if (string.IsNullOrEmpty(playlistId))
            return;

        NavigateToPlaylistRequested?.Invoke(playlistId);
    }

    private void AudioClearQueue() => PlayerControl.ClearQueue();
    private void AudioShuffleQueue() => PlayerControl.ShuffleQueue();

    protected override void OnPlay(TrackInfo track)
    {
        _ = PlayerControl.PlayTrackAsync(track);
    }

    protected override Task SaveMoveAsync(int fromVisualIndex, int toVisualIndex, CancellationToken ct)
    {
        Interlocked.Increment(ref _selfMoveCounter);
        try
        {
            PlayerControl.MoveQueueItem(fromVisualIndex, toVisualIndex);
            RecalculateQueueTimingState();
        }
        finally
        {
            Dispatcher.UIThread.Post(() => Interlocked.Decrement(ref _selfMoveCounter), DispatcherPriority.Background);
        }

        return Task.CompletedTask;
    }

    private void UpdateLivePositionSubscription(bool isSuspended)
    {
        bool shouldSubscribe = !_disposed && _isPageActive && !isSuspended;
        if (shouldSubscribe == _isLivePositionSubscribed)
            return;

        _isLivePositionSubscribed = shouldSubscribe;
        if (shouldSubscribe)
        {
            PlayerControl.PositionChanged += OnPlayerPositionChanged;
            PlayerControl.SeekCompleted += OnPlayerSeekCompleted;
            PlayerControl.ForceSyncTriggered += OnPlayerForceSyncTriggered;
        }
        else
        {
            PlayerControl.PositionChanged -= OnPlayerPositionChanged;
            PlayerControl.SeekCompleted -= OnPlayerSeekCompleted;
            PlayerControl.ForceSyncTriggered -= OnPlayerForceSyncTriggered;
        }
    }

    private void OnPlayerPositionChanged(TimeSpan position)
    {
        if (_disposed || !_isPageActive)
            return;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => RefreshPlaybackTiming(force: false, explicitPosition: position));
            return;
        }

        RefreshPlaybackTiming(force: false, explicitPosition: position);
    }

    private void OnPlayerSeekCompleted(TimeSpan position)
    {
        if (_disposed || !_isPageActive)
            return;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => RefreshPlaybackTiming(force: true, explicitPosition: position));
            return;
        }

        RefreshPlaybackTiming(force: true, explicitPosition: position);
    }

    private void OnPlayerForceSyncTriggered()
    {
        if (_disposed) return;

        void Sync()
        {
            if (_disposed) return;
            IsPlaybackActive = PlayerControl.IsPlaying;
            VolumeOverdriveFactor = PlayerControl.VolumeOverdriveFactor;
            if (_isPageActive)
                RefreshPlaybackTiming(force: true);
        }

        if (Dispatcher.UIThread.CheckAccess())
            Sync();
        else
            Dispatcher.UIThread.Post(Sync);
    }

    private void OnCurrentTrackChanged(TrackInfo? _)
    {
        if (_disposed) return;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RecalculateQueueTimingState);
            return;
        }

        RecalculateQueueTimingState();
    }

    private void OnAudioQueueChanged()
    {
        if (Volatile.Read(ref _selfMoveCounter) > 0) return;
        Dispatcher.UIThread.Post(SyncWithAudioQueue);
    }

    private void OnPlaybackPurityChanged(string? playlistId, bool isQueuePure, bool isPlayingPure)
    {
        if (_disposed) return;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(SyncSourcePlaylistState);
            return;
        }

        SyncSourcePlaylistState();
    }

    private void OnLanguageChanged(object? sender, string _)
    {
        if (_disposed) return;

        OnPropertyChanged(nameof(SourcePlaylistDisplayName));
        OnPropertyChanged(nameof(HasSourcePlaylistName));
        OnPropertyChanged(nameof(GoToPlaylistTooltip));
        RefreshPlaybackTiming(force: true);
    }

    private void SyncSourcePlaylistState()
    {
        if (_disposed) return;

        var playlistId = PlayerControl.ActivePlaylistId;
        bool hasSource = !string.IsNullOrEmpty(playlistId);
        bool isMixed = hasSource && !PlayerControl.IsQueuePure;

        bool idChanged = !string.Equals(_activePlaylistId, playlistId, StringComparison.Ordinal);
        bool visibilityChanged = HasSourcePlaylist != hasSource;
        bool mixedChanged = IsQueueMixed != isMixed;

        _activePlaylistId = playlistId;

        if (visibilityChanged)
        {
            HasSourcePlaylist = hasSource;
            GoToSourcePlaylistCommand.NotifyCanExecuteChanged();
        }

        if (mixedChanged)
        {
            IsQueueMixed = isMixed;
        }

        OnPropertyChanged(nameof(SourcePlaylistDisplayName));
        OnPropertyChanged(nameof(HasSourcePlaylistName));
        OnPropertyChanged(nameof(GoToPlaylistTooltip));

        if (idChanged)
        {
            UpdateSourcePlaylistAppearance(playlistId);
        }
    }

    private void OnSourcePlaylistChanged(Core.Models.Playlist playlist)
    {
        if (_disposed || !string.Equals(playlist.Id, _activePlaylistId, StringComparison.Ordinal))
            return;

        string? colorHex = playlist.EffectiveColor;
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyPlaylistColor(colorHex);
        }
        else
        {
            string targetId = playlist.Id;
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed && string.Equals(_activePlaylistId, targetId, StringComparison.Ordinal))
                    ApplyPlaylistColor(colorHex);
            });
        }
    }

    private void UpdateSourcePlaylistAppearance(string? playlistId)
    {
        CancelColorLoad();

        if (string.IsNullOrEmpty(playlistId))
        {
            _isLikedSource = false;
            SourcePlaylistColor = null;
            return;
        }

        if (string.Equals(playlistId, LibraryService.LikedPlaylistId, StringComparison.Ordinal))
        {
            _isLikedSource = true;
            ApplyLikedAccentColor();
            return;
        }

        _isLikedSource = false;
        var cts = _colorLoadCts = new CancellationTokenSource();
        _ = LoadSourcePlaylistMetadataAsync(playlistId, cts.Token);
    }

    private void ApplyLikedAccentColor() =>
        SourcePlaylistColor = LMP.UI.Services.ThemeManagerService.GetThemeColor("Accent");

    private async Task LoadSourcePlaylistMetadataAsync(string playlistId, CancellationToken ct)
    {
        try
        {
            var playlist = await _playlistService.GetPlaylistAsync(playlistId, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested || _disposed)
                return;

            string? colorHex = playlist?.EffectiveColor;
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed || ct.IsCancellationRequested || !string.Equals(_activePlaylistId, playlistId, StringComparison.Ordinal))
                    return;

                ApplyPlaylistColor(colorHex);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Debug($"[Queue] Failed to resolve playlist metadata for '{playlistId}': {ex.Message}");
        }
    }

    private void ApplyPlaylistColor(string? colorHex) =>
        SourcePlaylistColor = !string.IsNullOrWhiteSpace(colorHex) && Color.TryParse(colorHex, out var parsed)
            ? parsed
            : null;

    private void OnIsPlayingChanged(bool isPlaying)
    {
        if (_disposed) return;

        if (Dispatcher.UIThread.CheckAccess())
            IsPlaybackActive = isPlaying;
        else
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed)
                    IsPlaybackActive = isPlaying;
            });
    }

    private void OnPlayerVolumeOverdriveChanged(double factor)
    {
        if (_disposed) return;

        if (Dispatcher.UIThread.CheckAccess())
            VolumeOverdriveFactor = factor;
        else
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed)
                    VolumeOverdriveFactor = factor;
            });
    }

    private void CancelColorLoad()
    {
        _colorLoadCts?.Cancel();
        _colorLoadCts?.Dispose();
        _colorLoadCts = null;
    }

    private void SyncWithAudioQueue()
    {
        if (_disposed) return;

        var rawQueue = PlayerControl.Queue;

        if (!IsQueueContentIdentical(rawQueue))
        {
            SetTracks(rawQueue, explicitOrder: null, preserveSelection: true);
        }
        else
        {
            _items.UpdatePlaybackState(PlayerControl.CurrentTrack, PlayerControl.IsPlaying);
        }

        RecalculateQueueTimingState(rawQueue);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanReorderItems));
        NotifyCommandStates();
        SyncSourcePlaylistState();
    }

    private void RecalculateQueueTimingState() => RecalculateQueueTimingState(PlayerControl.Queue);

    private void RecalculateQueueTimingState(IReadOnlyList<TrackInfo> rawQueue)
    {
        var currentTrack = PlayerControl.CurrentTrack;
        string? currentId = currentTrack?.Id;

        TimeSpan total = TimeSpan.Zero;
        TimeSpan beforeCurrent = TimeSpan.Zero;
        int currentIndex = -1;

        for (int i = 0; i < rawQueue.Count; i++)
        {
            var track = rawQueue[i];
            if (currentIndex < 0 && currentId != null && string.Equals(track.Id, currentId, StringComparison.Ordinal))
            {
                currentIndex = i;
                beforeCurrent = total;
            }

            total += track.Duration;
        }

        _totalQueueDuration = total;
        _elapsedBeforeCurrent = beforeCurrent;

        if (CurrentTrackIndex != currentIndex)
        {
            CurrentTrackIndex = currentIndex;
            ScrollToCurrentTrackCommand.NotifyCanExecuteChanged();
        }

        CurrentTrackNumberDisplay = currentIndex >= 0 ? (currentIndex + 1).ToString() : "–";
        RefreshPlaybackTiming(force: true);
    }

    private void RefreshPlaybackTiming(bool force, TimeSpan? explicitPosition = null)
    {
        var rawPosition = explicitPosition ?? PlayerControl.CurrentPosition;
        var trackDuration = PlayerControl.TotalDuration;

        if (rawPosition < TimeSpan.Zero)
            rawPosition = TimeSpan.Zero;
        else if (trackDuration > TimeSpan.Zero && rawPosition > trackDuration)
            rawPosition = trackDuration;

        long positionSecond = (long)rawPosition.TotalSeconds;
        if (!force && positionSecond == _lastRenderedPositionSecond)
            return;

        _lastRenderedPositionSecond = positionSecond;

        TimeSpan elapsed = CurrentTrackIndex >= 0
            ? _elapsedBeforeCurrent + rawPosition
            : TimeSpan.Zero;

        if (elapsed > _totalQueueDuration)
            elapsed = _totalQueueDuration;

        TimeSpan remaining = _totalQueueDuration - elapsed;
        if (remaining < TimeSpan.Zero)
            remaining = TimeSpan.Zero;

        string elapsedText = FormatDuration(elapsed);
        string remainingText = FormatDuration(remaining);
        string totalText = FormatDuration(_totalQueueDuration);

        FormattedElapsedDuration = elapsedText;
        FormattedSecondaryDuration = _showRemainingTime ? $"-{remainingText}" : totalText;
        QueueProgressRatio = _totalQueueDuration.TotalSeconds > 0
            ? Math.Clamp(elapsed.TotalSeconds / _totalQueueDuration.TotalSeconds, 0.0, 1.0)
            : 0.0;
        QueueTimeTooltip = string.Format(L["Queue_TimeTooltip"], elapsedText, remainingText, totalText);
    }

    private static string FormatDuration(TimeSpan duration) =>
        LMP.Core.Helpers.DurationFormatter.FormatShort(duration);

    private bool IsQueueContentIdentical(IReadOnlyList<TrackInfo> rawQueue)
    {
        if (_items.TotalCount != rawQueue.Count) return false;
        for (int i = 0; i < rawQueue.Count; i++)
        {
            var existing = _items.GetMasterTrackAt(i);
            if (existing == null || !string.Equals(existing.Id, rawQueue[i].Id, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private void NotifyCommandStates()
    {
        ScrollToCurrentTrackCommand.NotifyCanExecuteChanged();
        ToggleTimeDisplayModeCommand.NotifyCanExecuteChanged();
        ClearQueueCommand.NotifyCanExecuteChanged();
        ShuffleQueueCommand.NotifyCanExecuteChanged();
        DownloadAllCommand.NotifyCanExecuteChanged();
        SaveQueueToPlaylistCommand.NotifyCanExecuteChanged();
    }

    private Task DownloadAllAsync() => Downloads.StartDownloadsBatchAsync(PlayerControl.Queue);

    /// <summary>
    /// Saves the current playback queue to a new playlist with optional binding to YouTube Music.
    /// </summary>
    private Task<Core.Models.Playlist?> SaveQueueToPlaylistAsync() =>
        _editService.CreatePlaylistAsync(PlayerControl.Queue);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _isPageActive = false;
            UpdateLivePositionSubscription(isSuspended: true);

            PlayerControl.QueueChanged -= OnAudioQueueChanged;
            PlayerControl.CurrentTrackChanged -= OnCurrentTrackChanged;
            PlayerControl.PlaybackPurityChanged -= OnPlaybackPurityChanged;
            _playlistService.OnPlaylistChanged -= OnSourcePlaylistChanged;
            LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;

            CancelColorLoad();
            PlayerControl.IsPlayingChanged -= OnIsPlayingChanged;
            PlayerControl.VolumeOverdriveChanged -= OnPlayerVolumeOverdriveChanged;
            SourcePlaylistColor = null;

            NavigateToPlaylistRequested = null;
            ScrollToCurrentRequested = null;
        }

        base.Dispose(disposing);
    }
}