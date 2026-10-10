using Avalonia.Threading;

namespace LMP.UI.Features.Queue;

/// <summary>
/// ViewModel экрана текущей очереди воспроизведения.
/// Синхронизирует состав очереди с <see cref="AudioEngine"/> и активное состояние треков с <see cref="PlayerControlService"/>.
/// </summary>
public sealed partial class QueueViewModel : TrackListBaseViewModel
{
    private readonly PlaylistService _playlistService;
    private readonly CookieAuthService _auth;
    private readonly DialogService _dialog;
    private int _selfMoveCounter;

    [ObservableProperty] public partial string FormattedTotalDuration { get; private set; } = string.Empty;

    public IVirtualTrackList QueueItems => Items;
    public IVirtualTrackList QueueTracks => Items;

    public bool IsEmpty => TotalCount == 0;
    public override bool CanReorderItems => CanReorder && TotalCount > 1;

    public IRelayCommand ClearQueueCommand { get; }
    public IRelayCommand ShuffleQueueCommand { get; }
    public IAsyncRelayCommand DownloadAllCommand { get; }
    public IAsyncRelayCommand SaveQueueToPlaylistCommand { get; }

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="QueueViewModel"/>.
    /// </summary>
    public QueueViewModel(
        AudioEngine audio,
        PlaylistService playlistService,
        CookieAuthService auth,
        DialogService dialog,
        TrackViewModelFactory vmFactory,
        DownloadService downloads,
        PlayerControlService? playerControl = null)
        : base(audio, downloads, vmFactory, playerControl)
    {
        _playlistService = playlistService;
        _auth = auth;
        _dialog = dialog;

        _items.IsQueueContext = true;

        ClearQueueCommand = new RelayCommand(AudioClearQueue, () => TotalCount > 0);
        ShuffleQueueCommand = new RelayCommand(AudioShuffleQueue, () => TotalCount > 1);
        DownloadAllCommand = new AsyncRelayCommand(DownloadAllAsync, () => TotalCount > 0);
        SaveQueueToPlaylistCommand = new AsyncRelayCommand(SaveQueueToPlaylistAsync, () => TotalCount > 0);

        Audio.OnQueueItemMoved += OnAudioQueueItemMoved;
        Audio.OnQueueChanged += OnAudioQueueChanged;

        SyncWithAudioQueue();
        IsLoading = false;
    }

    private void AudioClearQueue() => Audio.ClearQueue();
    private void AudioShuffleQueue() => Audio.ShuffleQueue();

    protected override void OnPlay(TrackInfo track)
    {
        _ = Audio.PlayTrackAsync(track);
    }

    protected override Task SaveMoveAsync(int fromVisualIndex, int toVisualIndex, CancellationToken ct)
    {
        Interlocked.Increment(ref _selfMoveCounter);
        try
        {
            Audio.MoveQueueItem(fromVisualIndex, toVisualIndex);
        }
        finally
        {
            Dispatcher.UIThread.Post(() => Interlocked.Decrement(ref _selfMoveCounter), DispatcherPriority.Background);
        }

        return Task.CompletedTask;
    }

    private void OnAudioQueueItemMoved(int from, int to)
    {
        if (Volatile.Read(ref _selfMoveCounter) > 0) return;
        Dispatcher.UIThread.Post(SyncWithAudioQueue);
    }

    private void OnAudioQueueChanged()
    {
        if (Volatile.Read(ref _selfMoveCounter) > 0) return;
        Dispatcher.UIThread.Post(SyncWithAudioQueue);
    }

    private void SyncWithAudioQueue()
    {
        var rawQueue = Audio.Queue;

        TimeSpan duration = TimeSpan.Zero;
        for (int i = 0; i < rawQueue.Count; i++)
            duration += rawQueue[i].Duration;

        FormattedTotalDuration = duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss")
            : duration.ToString(@"m\:ss");

        SetTracks(rawQueue, explicitOrder: null, preserveSelection: true);

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanReorderItems));
        NotifyCommandStates();
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
        var rawQueue = Audio.Queue;
        for (int i = 0; i < rawQueue.Count; i++)
        {
            var track = rawQueue[i];
            if (!track.IsDownloaded)
                Downloads.StartDownload(track);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Сохраняет текущую воспроизводимую очередь в новый плейлист с опциональной привязкой к YouTube Music.
    /// </summary>
    private async Task SaveQueueToPlaylistAsync()
    {
        var result = await _dialog.ShowCreatePlaylistDialogAsync();
        if (result == null || string.IsNullOrWhiteSpace(result.Name)) return;

        var trimmedName = result.Name.Trim();
        var playlist = await _playlistService.CreatePlaylistAsync(trimmedName);

        var tracks = Audio.Queue.ToList();
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
            Audio.OnQueueItemMoved -= OnAudioQueueItemMoved;
            Audio.OnQueueChanged -= OnAudioQueueChanged;
        }

        base.Dispose(disposing);
    }
}