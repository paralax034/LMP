using System.Collections.ObjectModel;
using Avalonia.Threading;
using LMP.UI.Features.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.UI.Features.Queue;

/// <summary>
/// ViewModel экрана текущей очереди воспроизведения.
/// Синхронизирует состав очереди с <see cref="AudioEngine"/> и активное состояние треков с <see cref="PlayerControlService"/>.
/// </summary>
public sealed partial class QueueViewModel : ViewModelBase
{
    private readonly AudioEngine _audio;
    private readonly PlayerControlService _playerControl;
    private readonly PlaylistService _playlistService;
    private readonly CookieAuthService _auth;
    private readonly DialogService _dialog;
    private readonly TrackViewModelFactory _vmFactory;
    private readonly DownloadService _downloads;

    private TrackItemViewModel? _currentActiveVm;

    public ObservableCollection<TrackItemViewModel> QueueItems { get; } = [];
    public ObservableCollection<TrackItemViewModel> QueueTracks => QueueItems;

    [ObservableProperty] public partial int TotalCount { get; private set; }
    [ObservableProperty] public partial string FormattedTotalDuration { get; private set; } = "";
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string FilterQuery { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsFilterEmpty { get; private set; }

    public bool IsEmpty => TotalCount == 0;
    public bool CanReorderItems => string.IsNullOrWhiteSpace(FilterQuery) && TotalCount > 1;

    public IRelayCommand ClearQueueCommand { get; }
    public IRelayCommand ShuffleQueueCommand { get; }
    public IAsyncRelayCommand DownloadAllCommand { get; }
    public IAsyncRelayCommand SaveQueueToPlaylistCommand { get; }
    public IRelayCommand<(int oldIndex, int newIndex)> MoveItemCommand { get; }

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="QueueViewModel"/>.
    /// </summary>
    /// <param name="audio">Низкоуровневый звуковой движок.</param>
    /// <param name="playlistService">Служба управления плейлистами.</param>
    /// <param name="auth">Служба авторизации YouTube.</param>
    /// <param name="dialog">Служба модальных диалогов.</param>
    /// <param name="vmFactory">Фабрика создания моделей представления треков.</param>
    /// <param name="downloads">Служба загрузки треков.</param>
    /// <param name="playerControl">Единый координатор состояния воспроизведения.</param>
    public QueueViewModel(
        AudioEngine audio,
        PlaylistService playlistService,
        CookieAuthService auth,
        DialogService dialog,
        TrackViewModelFactory vmFactory,
        DownloadService downloads,
        PlayerControlService? playerControl = null)
    {
        _audio = audio;
        _playerControl = playerControl ?? AppEntry.Services.GetRequiredService<PlayerControlService>();
        _playlistService = playlistService;
        _auth = auth;
        _dialog = dialog;
        _vmFactory = vmFactory;
        _downloads = downloads;

        ClearQueueCommand = new RelayCommand(_audio.ClearQueue, () => TotalCount > 0);
        ShuffleQueueCommand = new RelayCommand(_audio.ShuffleQueue, () => TotalCount > 1);
        DownloadAllCommand = new AsyncRelayCommand(DownloadAllAsync, () => TotalCount > 0);
        SaveQueueToPlaylistCommand = new AsyncRelayCommand(SaveQueueToPlaylistAsync, () => TotalCount > 0);

        MoveItemCommand = new RelayCommand<(int oldIndex, int newIndex)>(tuple =>
        {
            if (!CanReorderItems) return;
            _audio.MoveQueueItem(tuple.oldIndex, tuple.newIndex);
        });

        _audio.OnQueueChanged += OnAudioQueueChanged;
        _playerControl.CurrentTrackChanged += OnPlayerControlTrackChanged;
        _playerControl.IsPlayingChanged += OnPlayerControlIsPlayingChanged;
        _playerControl.ForceSyncTriggered += OnPlayerControlForceSyncTriggered;

        SyncWithAudioQueue();
    }

    partial void OnFilterQueryChanged(string value)
    {
        UpdateFilterState();
        OnPropertyChanged(nameof(CanReorderItems));
    }

    private void OnAudioQueueChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
            SyncWithAudioQueue();
        else
            Dispatcher.UIThread.Post(SyncWithAudioQueue);
    }

    private void OnPlayerControlTrackChanged(TrackInfo? track)
    {
        if (Dispatcher.UIThread.CheckAccess())
            UpdatePlaybackState(track, _playerControl.IsPlaying);
        else
            Dispatcher.UIThread.Post(() => UpdatePlaybackState(track, _playerControl.IsPlaying));
    }

    private void OnPlayerControlIsPlayingChanged(bool isPlaying)
    {
        if (Dispatcher.UIThread.CheckAccess())
            UpdatePlaybackState(_playerControl.CurrentTrack, isPlaying);
        else
            Dispatcher.UIThread.Post(() => UpdatePlaybackState(_playerControl.CurrentTrack, isPlaying));
    }

    private void OnPlayerControlForceSyncTriggered()
    {
        if (Dispatcher.UIThread.CheckAccess())
            UpdatePlaybackState(_playerControl.CurrentTrack, _playerControl.IsPlaying);
        else
            Dispatcher.UIThread.Post(() => UpdatePlaybackState(_playerControl.CurrentTrack, _playerControl.IsPlaying));
    }

    /// <summary>
    /// Централизованно обновляет флаги активности и воспроизведения для моделей представления элементов очереди.
    /// </summary>
    /// <param name="currentTrack">Текущий воспроизводимый трек.</param>
    /// <param name="isPlaying">Флаг активного физического воспроизведения.</param>
    private void UpdatePlaybackState(TrackInfo? currentTrack, bool isPlaying)
    {
        if (_currentActiveVm != null && _currentActiveVm.Id != currentTrack?.Id)
        {
            _currentActiveVm.SetActive(false, false);
            _currentActiveVm = null;
        }

        if (currentTrack is null) return;

        if (_currentActiveVm == null)
        {
            for (int i = 0; i < QueueItems.Count; i++)
            {
                if (string.Equals(QueueItems[i].Id, currentTrack.Id, StringComparison.Ordinal))
                {
                    _currentActiveVm = QueueItems[i];
                    break;
                }
            }
        }

        _currentActiveVm?.SetActive(true, isPlaying);
    }

    private void SyncWithAudioQueue()
    {
        var rawQueue = _audio.Queue;
        TotalCount = rawQueue.Count;

        TimeSpan duration = TimeSpan.Zero;
        for (int i = 0; i < rawQueue.Count; i++)
            duration += rawQueue[i].Duration;

        FormattedTotalDuration = duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss")
            : duration.ToString(@"m\:ss");

        for (int i = 0; i < QueueItems.Count; i++)
            QueueItems[i].Dispose();

        QueueItems.Clear();
        _currentActiveVm = null;

        var currentTrack = _playerControl.CurrentTrack;
        bool isPlaying = _playerControl.IsPlaying;

        for (int i = 0; i < rawQueue.Count; i++)
        {
            var item = rawQueue[i];
            var vm = _vmFactory.CreateForQueue(item, t => _ = _audio.PlayTrackAsync(t));
            if (_currentActiveVm == null && currentTrack != null && string.Equals(item.Id, currentTrack.Id, StringComparison.Ordinal))
            {
                vm.SetActive(true, isPlaying);
                _currentActiveVm = vm;
            }
            QueueItems.Add(vm);
        }

        UpdateFilterState();

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanReorderItems));

        ClearQueueCommand.NotifyCanExecuteChanged();
        ShuffleQueueCommand.NotifyCanExecuteChanged();
        DownloadAllCommand.NotifyCanExecuteChanged();
        SaveQueueToPlaylistCommand.NotifyCanExecuteChanged();
    }

    private void UpdateFilterState()
    {
        if (string.IsNullOrWhiteSpace(FilterQuery))
        {
            IsFilterEmpty = false;
            return;
        }

        var query = FilterQuery.Trim();
        bool hasMatch = false;

        for (int i = 0; i < QueueItems.Count; i++)
        {
            var item = QueueItems[i];
            if (item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Author.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                hasMatch = true;
                break;
            }
        }

        IsFilterEmpty = !hasMatch;
    }

    private Task DownloadAllAsync()
    {
        var rawQueue = _audio.Queue;
        for (int i = 0; i < rawQueue.Count; i++)
        {
            var track = rawQueue[i];
            if (!track.IsDownloaded)
                _downloads.StartDownload(track);
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

        var tracks = _audio.Queue.ToList();
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
            _audio.OnQueueChanged -= OnAudioQueueChanged;
            _playerControl.CurrentTrackChanged -= OnPlayerControlTrackChanged;
            _playerControl.IsPlayingChanged -= OnPlayerControlIsPlayingChanged;
            _playerControl.ForceSyncTriggered -= OnPlayerControlForceSyncTriggered;

            for (int i = 0; i < QueueItems.Count; i++)
                QueueItems[i].Dispose();

            QueueItems.Clear();
            _currentActiveVm = null;
        }
        base.Dispose(disposing);
    }
}