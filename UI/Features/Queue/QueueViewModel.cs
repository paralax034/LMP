using Avalonia.Collections;
using Avalonia.Threading;
using LMP.UI.Features.Shared;

namespace LMP.UI.Features.Queue;

/// <summary>
/// ViewModel панели очереди воспроизведения.
/// </summary>
public sealed partial class QueueViewModel : TrackListReorderableViewModel
{
    #region Fields

    private readonly DownloadService _downloads;
    private readonly DialogService _dialog;
    private readonly MusicLibraryManager _manager;
    private readonly LibraryService _library;

    private DispatcherTimer? _queueChangedDebounceTimer;
    private bool _isMovingInternally;
    private volatile bool _isSuspended;
    private bool _isDisposed;

    #endregion

    #region Properties

    /// <summary>True когда очередь пуста (нет треков вообще).</summary>
    [ObservableProperty] public partial bool IsEmpty { get; private set; } = true;

    /// <summary>True когда очередь не пуста, но фильтр не нашёл совпадений.</summary>
    [ObservableProperty] public partial bool IsFilterEmpty { get; private set; }

    [ObservableProperty] public partial bool CanReorderItems { get; private set; } = true;

    /// <summary>
    /// Псевдоним для Items, сохраняющий совместимость с биндингом в QueueView.axaml.
    /// </summary>
    public AvaloniaList<TrackItemViewModel> QueueItems => Items;

    partial void OnIsEmptyChanged(bool value)
    {
        SaveQueueToPlaylistCommand.NotifyCanExecuteChanged();
    }

    #endregion

    #region Commands

    public IRelayCommand ClearQueueCommand { get; }
    public IRelayCommand ShuffleQueueCommand { get; }
    public IRelayCommand DownloadAllCommand { get; }
    public IRelayCommand<TrackItemViewModel> RemoveTrackCommand { get; }
    public IAsyncRelayCommand<(int oldIndex, int newIndex)> MoveItemCommand { get; }
    public IAsyncRelayCommand SaveQueueToPlaylistCommand { get; }

    #endregion

    #region Constructor

    public QueueViewModel(
        AudioEngine audio,
        DownloadService downloads,
        DialogService dialog,
        MusicLibraryManager manager,
        LibraryService library,
        TrackViewModelFactory vmFactory)
        : base(audio, downloads, vmFactory)
    {
        _downloads = downloads;
        _dialog = dialog;
        _manager = manager;
        _library = library;

        ClearQueueCommand = new RelayCommand(() => Audio.ClearQueue());
        ShuffleQueueCommand = new RelayCommand(() => Audio.ShuffleQueue());
        DownloadAllCommand = new RelayCommand(OnDownloadAll);

        RemoveTrackCommand = new RelayCommand<TrackItemViewModel>(item =>
        {
            if (item?.Track != null) Audio.RemoveFromQueue(item.Track);
        });

        MoveItemCommand = new AsyncRelayCommand<(int oldIndex, int newIndex)>(
            t => CanReorderItems ? MoveItemAsync(t.oldIndex, t.newIndex) : Task.CompletedTask);

        SaveQueueToPlaylistCommand = new AsyncRelayCommand(
            SaveQueueToPlaylistAsync,
            () => !IsEmpty);

        Audio.OnQueueChanged += OnAudioQueueChanged;

        RefreshFromAudioEngine();
    }

    private void OnAudioQueueChanged()
    {
        if (_isMovingInternally || _isSuspended || _isDisposed) return;

        _queueChangedDebounceTimer?.Stop();
        _queueChangedDebounceTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(80),
            DispatcherPriority.Normal,
            (_, _) =>
            {
                _queueChangedDebounceTimer?.Stop();
                if (!_isMovingInternally && !_isSuspended && !_isDisposed)
                    RefreshFromAudioEngine();
            });
        _queueChangedDebounceTimer.Start();
    }

    #endregion

    #region Overrides

    protected override void RebuildVisibleItems()
    {
        CanReorderItems = string.IsNullOrWhiteSpace(FilterQuery);
        base.RebuildVisibleItems();

        IsEmpty = TotalCount == 0;
        IsFilterEmpty = !IsEmpty && !string.IsNullOrWhiteSpace(FilterQuery) && Items.Count == 0;
    }

    protected override TrackItemViewModel CreateViewModel(TrackInfo track)
    {
        var vm = VmFactory.CreateForQueue(track, PlayFromQueue);

        if (Audio.CurrentTrack?.Id == track.Id)
        {
            vm.SetActive(true, Audio.IsPlaying);
            CurrentActiveVm = vm;
        }

        return vm;
    }

    protected override Task SaveMoveAsync(int fromIndex, int toIndex, CancellationToken ct)
    {
        try
        {
            _isMovingInternally = true;
            Audio.MoveQueueItem(fromIndex, toIndex);
        }
        finally
        {
            _isMovingInternally = false;
        }
        return Task.CompletedTask;
    }

    protected override void OnPlay(TrackInfo track) => PlayFromQueue(track);

    protected override Task<List<TrackInfo>> LoadTracksAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        return Task.FromResult(Audio.Queue.ToList());
    }

    #endregion

    #region Queue Management

    private void RefreshFromAudioEngine()
    {
        var queue = Audio.Queue;
        if (queue.Count == 0)
        {
            UpdateMasterData([]);
            return;
        }

        var seen = new HashSet<string>(queue.Count, StringComparer.Ordinal);
        var uniqueTracks = new List<TrackInfo>(queue.Count);

        for (int i = 0; i < queue.Count; i++)
        {
            var track = queue[i];
            if (track != null && seen.Add(track.Id))
            {
                uniqueTracks.Add(track);
            }
        }

        UpdateMasterData(uniqueTracks);
    }

    private void OnDownloadAll()
    {
        var items = Items;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!item.IsDownloading && !item.Track.IsDownloaded)
            {
                _downloads.StartDownload(item.Track);
            }
        }
    }

    private void PlayFromQueue(TrackInfo track) => _ = Audio.PlayTrackAsync(track);

    #endregion

    #region Lifecycle

    protected override void OnSuspend()
    {
        _isSuspended = true;
        Log.Debug("[QueueVM] Suspended");
    }

    protected override void OnResume()
    {
        _isSuspended = false;
        Log.Debug("[QueueVM] Resumed");
        RefreshFromAudioEngine();
    }

    public override async Task OnNavigatedToAsync()
    {
        await base.OnNavigatedToAsync().ConfigureAwait(false);
        RefreshFromAudioEngine();
    }

    protected override void OnAccountChanged()
    {
        base.OnAccountChanged();
        RefreshFromAudioEngine();
        Log.Info("[Queue] Playback queue view synchronized with new account state.");
    }

    #endregion

    #region Commands Implementation

    private async Task SaveQueueToPlaylistAsync()
    {
        var tracks = GetLoadedItemsSnapshot();
        if (tracks.Count == 0) return;

        var result = await _dialog.ShowCreatePlaylistDialogAsync();
        if (result is null || string.IsNullOrWhiteSpace(result.Name)) return;

        var playlist = await _library.CreatePlaylistAsync(result.Name.Trim());

        foreach (var track in tracks)
            await _manager.AddTrackToPlaylistAsync(playlist.Id, track);

        Log.Info($"[Queue] Saved {tracks.Count} tracks to playlist '{result.Name}'");
    }

    #endregion

    #region IDisposable

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            Log.Debug("[QueueVM] Disposing");
            Audio.OnQueueChanged -= OnAudioQueueChanged;
            _queueChangedDebounceTimer?.Stop();
            _queueChangedDebounceTimer = null;
        }

        base.Dispose(disposing);
        _isDisposed = true;
    }

    #endregion
}