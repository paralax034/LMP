using Avalonia.Threading;

namespace LMP.UI.Features.Queue;

/// <summary>
/// ViewModel for the current playback queue screen.
/// Synchronizes the queue contents with <see cref="AudioEngine"/> and active track state with <see cref="PlayerControlService"/>.
/// </summary>
public sealed partial class QueueViewModel : TrackListBaseViewModel
{
    private readonly PlaylistService _playlistService;
    private readonly CookieAuthService _auth;
    private readonly DialogService _dialog;
    private int _selfMoveCounter;

    [ObservableProperty] public partial string FormattedTotalDuration { get; private set; } = string.Empty;

    public IVirtualTrackList QueueItems => Items;

    public bool IsEmpty => TotalCount == 0;
    public override bool CanReorderItems => CanReorder && TotalCount > 1;

    public IRelayCommand ClearQueueCommand { get; }
    public IRelayCommand ShuffleQueueCommand { get; }
    public IAsyncRelayCommand DownloadAllCommand { get; }
    public IAsyncRelayCommand SaveQueueToPlaylistCommand { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="QueueViewModel"/> class.
    /// </summary>
    public QueueViewModel(
        PlaylistService playlistService,
        CookieAuthService auth,
        DialogService dialog,
        TrackViewModelFactory vmFactory,
        DownloadService downloads,
        PlayerControlService? playerControl = null)
        : base(downloads, vmFactory, playerControl)
    {
        _playlistService = playlistService;
        _auth = auth;
        _dialog = dialog;

        _items.IsQueueContext = true;

        ClearQueueCommand = new RelayCommand(AudioClearQueue, () => TotalCount > 0);
        ShuffleQueueCommand = new RelayCommand(AudioShuffleQueue, () => TotalCount > 1);
        DownloadAllCommand = new AsyncRelayCommand(DownloadAllAsync, () => TotalCount > 0);
        SaveQueueToPlaylistCommand = new AsyncRelayCommand(SaveQueueToPlaylistAsync, () => TotalCount > 0);

        PlayerControl.QueueChanged += OnAudioQueueChanged;

        SyncWithAudioQueue();
        IsLoading = false;
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
        }
        finally
        {
            Dispatcher.UIThread.Post(() => Interlocked.Decrement(ref _selfMoveCounter), DispatcherPriority.Background);
        }

        return Task.CompletedTask;
    }

    private void OnAudioQueueChanged()
    {
        if (Volatile.Read(ref _selfMoveCounter) > 0) return;
        Dispatcher.UIThread.Post(SyncWithAudioQueue);
    }

    private void SyncWithAudioQueue()
    {
        var rawQueue = PlayerControl.Queue;

        TimeSpan duration = TimeSpan.Zero;
        for (int i = 0; i < rawQueue.Count; i++)
            duration += rawQueue[i].Duration;

        FormattedTotalDuration = duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss")
            : duration.ToString(@"m\:ss");

        if (!IsQueueContentIdentical(rawQueue))
        {
            SetTracks(rawQueue, explicitOrder: null, preserveSelection: true);
        }
        else
        {
            _items.UpdatePlaybackState(PlayerControl.CurrentTrack, PlayerControl.IsPlaying);
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanReorderItems));
        NotifyCommandStates();
    }

    private bool IsQueueContentIdentical(IReadOnlyList<TrackInfo> rawQueue)
    {
        if (_items.TotalCount != rawQueue.Count) return false;
        for (int i = 0; i < rawQueue.Count; i++)
        {
            var existing = _items.GetTrackAt(i);
            if (existing == null || !string.Equals(existing.Id, rawQueue[i].Id, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private void NotifyCommandStates()
    {
        ClearQueueCommand.NotifyCanExecuteChanged();
        ShuffleQueueCommand.NotifyCanExecuteChanged();
        DownloadAllCommand.NotifyCanExecuteChanged();
        SaveQueueToPlaylistCommand.NotifyCanExecuteChanged();
    }

    private Task DownloadAllAsync()
    {
        var rawQueue = PlayerControl.Queue;
        for (int i = 0; i < rawQueue.Count; i++)
        {
            var track = rawQueue[i];
            if (!track.IsDownloaded)
                Downloads.StartDownload(track);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Saves the current playback queue to a new playlist with optional binding to YouTube Music.
    /// </summary>
    private async Task SaveQueueToPlaylistAsync()
    {
        var result = await _dialog.ShowCreatePlaylistDialogAsync();
        if (result == null || string.IsNullOrWhiteSpace(result.Name)) return;

        var trimmedName = result.Name.Trim();
        var playlist = await _playlistService.CreatePlaylistAsync(
            name: trimmedName,
            description: result.Description,
            thumbnailUrl: result.ThumbnailUrl,
            customColor: result.CustomColor,
            computedColor: result.ComputedColor);

        var tracks = PlayerControl.Queue;
        if (tracks.Count > 0)
        {
            await _playlistService.AddTracksToPlaylistAsync(playlist.Id, tracks);
        }

        if (result.SyncToCloud && _auth.IsAuthenticated)
        {
            await _playlistService.LinkToCloudAsync(playlist.Id);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            PlayerControl.QueueChanged -= OnAudioQueueChanged;
        }

        base.Dispose(disposing);
    }
}