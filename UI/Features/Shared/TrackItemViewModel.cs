using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.UI.Features.Shared;

/// <summary>
/// ViewModel для представления отдельного трека в списках и очередях.
/// </summary>
public sealed partial class TrackItemViewModel : ViewModelBase
{
    #region Weak Event Subscription

    private sealed class WeakPropertyChangedSubscription
    {
        private readonly WeakReference<TrackItemViewModel> _weak;
        private readonly INotifyPropertyChanged _source;
        private int _isUnsubscribed;

        internal WeakPropertyChangedSubscription(TrackItemViewModel vm, INotifyPropertyChanged source)
        {
            _weak = new WeakReference<TrackItemViewModel>(vm);
            _source = source;
            source.PropertyChanged += Handle;
        }

        private void Handle(object? sender, PropertyChangedEventArgs e)
        {
            if (_weak.TryGetTarget(out var vm))
            {
                vm.OnTrackPropertyChanged(sender, e);
            }
            else
            {
                Unsubscribe();
            }
        }

        internal void Unsubscribe()
        {
            if (Interlocked.Exchange(ref _isUnsubscribed, 1) == 0)
            {
                _source.PropertyChanged -= Handle;
            }
        }
    }

    private readonly WeakPropertyChangedSubscription _trackSubscription;

    #endregion

    #region Static Geometries Cache

    private static StreamGeometry? _checkCircleGeometry;
    private static StreamGeometry? _cloudCheckGeometry;

    private static StreamGeometry? CheckCircleGeometry =>
        _checkCircleGeometry ??= ResolveStaticGeometry("Icon.CheckCircle");

    private static StreamGeometry? CloudCheckGeometry =>
        _cloudCheckGeometry ??= ResolveStaticGeometry("Icon.CloudCheck");

    private static StreamGeometry? ResolveStaticGeometry(string key) =>
        Avalonia.Application.Current?.Resources.TryGetResource(key, null, out var res) == true
            ? res as StreamGeometry
            : null;

    #endregion

    private readonly AudioEngine _audio;
    private readonly PlayerControlService _playerControl;
    private readonly PlaylistService _playlistService;
    private readonly DownloadService _downloads;
    private readonly DialogService _dialog;
    private readonly LibraryService _library;

    private readonly TrackInfo[] _singleTarget;
    private CancellationTokenSource? _submenuCts;

    private Action<TrackInfo>? _onPlay;

    private ICommand? _addToQueueCommand;
    private ICommand? _startRadioCommand;
    private ICommand? _saveToDownloadsCommand;
    private ICommand? _removeFromPlaylistCommand;
    private ICommand? _removeFromQueueCommand;
    private ICommand? _copyLinkCommand;
    private IAsyncRelayCommand<object?>? _copyTitleCommand;

    public TrackInfo Track { get; }
    public bool IsDisposed { get; private set; }

    public string Id => Track.Id;
    public string Title => Track.Title;
    public string Author => Track.Author;
    public TimeSpan Duration => Track.Duration;
    public string ThumbnailUrl => Track.ThumbnailUrl;

    public bool IsLiked => Track.IsLiked;
    public bool IsDownloaded => Track.IsDownloaded;

    public string FormattedDuration => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");

    [ObservableProperty] public partial bool IsActive { get; private set; }
    [ObservableProperty] public partial bool IsPlaying { get; private set; }
    [ObservableProperty] public partial bool IsDownloading { get; private set; }
    [ObservableProperty] public partial float DownloadProgress { get; private set; }
    [ObservableProperty] public partial bool IsMenuOpen { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial bool IsPlaylistContext { get; set; }
    [ObservableProperty] public partial bool IsQueueContext { get; set; }

    [ObservableProperty] public partial string AddToPlaylistHeader { get; private set; } = string.Empty;
    public ObservableCollection<PlaylistMenuItemViewModel> PlaylistMenuItems { get; } = [];

    public bool ShowAddToQueue => !IsQueueContext;
    public bool HasCacheIcon => !IsDownloading && (Track.IsDownloaded || Track.IsCached);

    public StreamGeometry? CacheIconGeometry => Track.IsDownloaded
        ? CheckCircleGeometry
        : (Track.IsCached ? CloudCheckGeometry : null);

    public string? CacheIconTooltip => Track.IsDownloaded
        ? L["Track_Downloaded"]
        : (Track.IsCached ? L["Track_SaveToFolder"] : null);

    public string DownloadStatusText
    {
        get
        {
            if (Track.IsDownloaded) return L["Track_Downloaded"];
            if (Track.IsCached) return L["Track_SaveToFolder"];
            return L["Track_Download"];
        }
    }

    public Action<TrackInfo>? StartRadioAction { get; set; }
    public Action<IReadOnlyList<TrackInfo>>? RemoveFromPlaylistAction { get; set; }
    public Func<IReadOnlyList<TrackInfo>>? SelectionProvider { get; set; }
    public string? SourceContextId { get; set; }

    public ICommand PlayCommand { get; }
    public ICommand ToggleLikeCommand { get; }

    public ICommand AddToQueueCommand =>
        _addToQueueCommand ??= new TrackSyncCommand(OnAddToQueue);

    public ICommand StartRadioCommand =>
        _startRadioCommand ??= new TrackSyncCommand(OnStartRadio);

    public ICommand SaveToDownloadsCommand =>
        _saveToDownloadsCommand ??= new TrackAsyncCommand(SaveToDownloadsAsync);

    public ICommand CopyLinkCommand =>
        _copyLinkCommand ??= new TrackAsyncCommand(CopyLinkAsync);

    public ICommand RemoveFromPlaylistCommand =>
        _removeFromPlaylistCommand ??= new TrackSyncCommand(OnRemoveFromPlaylist);

    public ICommand RemoveFromQueueCommand =>
        _removeFromQueueCommand ??= new TrackSyncCommand(OnRemoveFromQueue);

    public IAsyncRelayCommand<object?> CopyTitleCommand =>
        _copyTitleCommand ??= new AsyncRelayCommand<object?>(CopyTitleAsync);


    public TrackItemViewModel(
        TrackInfo track,
        AudioEngine audio,
        PlayerControlService playerControl,
        PlaylistService playlistService,
        DownloadService downloads,
        DialogService dialog,
        LibraryService library,
        Action<TrackInfo>? onPlay = null)
    {
        Track = track;
        _audio = audio;
        _playerControl = playerControl;
        _playlistService = playlistService;
        _downloads = downloads;
        _dialog = dialog;
        _library = library;
        _onPlay = onPlay;

        _singleTarget = [track];

        PlayCommand = new TrackAsyncCommand(PlayAsync);
        ToggleLikeCommand = new TrackAsyncCommand(ToggleLikeAsync);

        _trackSubscription = new WeakPropertyChangedSubscription(this, track);
        UpdateAddToPlaylistHeader();
    }

    private void OnTrackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Track.IsLiked):
                OnPropertyChanged(nameof(IsLiked));
                break;

            case nameof(Track.IsDownloaded):
                if (Track.IsDownloaded && IsDownloading)
                {
                    IsDownloading = false;
                    DownloadProgress = 0f;
                }
                OnPropertyChanged(nameof(IsDownloaded));
                OnPropertyChanged(nameof(DownloadStatusText));
                OnPropertyChanged(nameof(HasCacheIcon));
                OnPropertyChanged(nameof(CacheIconGeometry));
                OnPropertyChanged(nameof(CacheIconTooltip));
                break;

            case nameof(Track.IsCached):
                OnPropertyChanged(nameof(DownloadStatusText));
                OnPropertyChanged(nameof(HasCacheIcon));
                OnPropertyChanged(nameof(CacheIconGeometry));
                OnPropertyChanged(nameof(CacheIconTooltip));
                break;
        }
    }

    public IReadOnlyList<TrackInfo> GetActionTargets()
    {
        if (IsSelected && SelectionProvider != null)
        {
            var selected = SelectionProvider();
            if (selected.Count > 1)
                return selected;
        }

        return _singleTarget;
    }

    private void UpdateAddToPlaylistHeader()
    {
        var targets = GetActionTargets();
        AddToPlaylistHeader = targets.Count > 1
            ? string.Format(L["AddToPlaylist_BatchHeader"], targets.Count)
            : L["AddToPlaylist_Title"];
    }

    public async Task PreparePlaylistSubmenuAsync(CancellationToken ct = default)
    {
        _submenuCts?.Cancel();
        _submenuCts?.Dispose();
        _submenuCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _submenuCts.Token;

        var targets = GetActionTargets();
        UpdateAddToPlaylistHeader();

        try
        {
            await _playlistService.EnsureIndexInitializedAsync(token).ConfigureAwait(false);
            var playlists = await _playlistService.GetEditablePlaylistsAsync(token).ConfigureAwait(false);

            if (token.IsCancellationRequested || !IsMenuOpen)
                return;

            var items = new List<PlaylistMenuItemViewModel>(playlists.Count + 2);

            var createItem = new PlaylistMenuItemViewModel(
                playlistId: string.Empty,
                name: L["Playlist_CreateNew"],
                state: PlaylistMembershipState.None,
                countText: string.Empty,
                onToggle: _ => CreateNewPlaylistWithTargetsAsync(targets),
                isCreateAction: true);
            items.Add(createItem);
            items.Add(PlaylistMenuItemViewModel.CreateSeparator());

            bool isSingleTarget = targets.Count == 1;
            var singleTargetTrack = isSingleTarget ? targets[0] : null;

            for (int i = 0; i < playlists.Count; i++)
            {
                var p = playlists[i];
                PlaylistMembershipState state;
                string? countText = null;

                if (isSingleTarget && singleTargetTrack != null)
                {
                    bool inPlaylist = singleTargetTrack.InPlaylists.Contains(p.Id);
                    state = inPlaylist ? PlaylistMembershipState.All : PlaylistMembershipState.None;
                }
                else
                {
                    var status = _playlistService.GetMembershipStatus(p.Id, targets);
                    state = status.State;
                    if (targets.Count > 1 && state == PlaylistMembershipState.Indeterminate)
                    {
                        countText = $"{status.IncludedCount}/{status.TotalCount}";
                    }
                }

                var item = new PlaylistMenuItemViewModel(
                    playlistId: p.Id,
                    name: p.Name,
                    state: state,
                    countText: countText,
                    onToggle: vm => TogglePlaylistMembershipAsync(vm, targets));

                items.Add(item);
            }

            if (token.IsCancellationRequested || !IsMenuOpen)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || !IsMenuOpen)
                    return;

                PlaylistMenuItems.Clear();
                for (int i = 0; i < items.Count; i++)
                {
                    PlaylistMenuItems.Add(items[i]);
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error($"[TrackItemVM] PreparePlaylistSubmenuAsync failed: {ex.Message}");
        }
    }

    public void ClearPlaylistSubmenu()
    {
        _submenuCts?.Cancel();
        _submenuCts?.Dispose();
        _submenuCts = null;
        PlaylistMenuItems.Clear();
    }

    private async Task TogglePlaylistMembershipAsync(PlaylistMenuItemViewModel item, IReadOnlyList<TrackInfo> targets)
    {
        var previousState = item.State;
        var previousCountText = item.CountText;

        if (previousState == PlaylistMembershipState.All)
        {
            item.State = PlaylistMembershipState.None;
            item.CountText = string.Empty;
        }
        else
        {
            item.State = PlaylistMembershipState.All;
            item.CountText = string.Empty;
        }

        try
        {
            var newState = await _playlistService.ToggleTracksMembershipAsync(item.PlaylistId, targets).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                item.State = newState;
                item.CountText = string.Empty;
            });

            var notif = AppEntry.Services.GetService<NotificationService>();
            if (notif != null)
            {
                string msgKey = newState == PlaylistMembershipState.All
                    ? "Playlist_TracksAdded_Toast"
                    : "Playlist_TracksRemoved_Toast";

                await notif.ShowToastAsync(
                    titleKey: "Dialog_Success",
                    messageKey: msgKey,
                    severity: NotificationSeverity.Success,
                    durationMs: 3000,
                    messageArgs: [targets.Count, item.Name]).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[TrackItemVM] Failed to toggle playlist membership: {ex.Message}");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                item.State = previousState;
                item.CountText = previousCountText;
            });
        }
    }

    private async Task CreateNewPlaylistWithTargetsAsync(IReadOnlyList<TrackInfo> targets)
    {
        var result = await _dialog.ShowCreatePlaylistDialogAsync();
        if (result == null || string.IsNullOrWhiteSpace(result.Name)) return;

        try
        {
            var playlist = await _playlistService.CreatePlaylistAsync(
                result.Name,
                result.Description,
                result.ThumbnailUrl,
                result.CustomColor,
                result.ComputedColor).ConfigureAwait(false);

            await _playlistService.AddTracksToPlaylistAsync(playlist.Id, targets).ConfigureAwait(false);

            var notif = AppEntry.Services.GetService<NotificationService>();
            if (notif != null)
            {
                await notif.ShowToastAsync(
                    titleKey: "Dialog_Success",
                    messageKey: "Playlist_TracksAdded_Toast",
                    severity: NotificationSeverity.Success,
                    durationMs: 3000,
                    messageArgs: [targets.Count, playlist.Name]).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[TrackItemVM] Failed to create playlist with tracks: {ex.Message}");
        }
    }

    private async Task ToggleLikeAsync()
    {
        var targets = GetActionTargets();
        if (targets.Count > 1)
        {
            bool anyUnliked = false;
            for (int i = 0; i < targets.Count; i++)
            {
                if (!targets[i].IsLiked)
                {
                    anyUnliked = true;
                    break;
                }
            }

            bool targetState = anyUnliked;
            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i].IsLiked != targetState)
                    await _playerControl.ToggleLikeAsync(targets[i]).ConfigureAwait(false);
            }
        }
        else
        {
            await _playerControl.ToggleLikeAsync(Track).ConfigureAwait(false);
        }
    }

    private void OnAddToQueue()
    {
        var targets = GetActionTargets();
        _audio.EnqueueRangeUnique(targets);
    }

    private void OnStartRadio() => StartRadioAction?.Invoke(Track);

    private void OnRemoveFromPlaylist()
    {
        if (!IsPlaylistContext) return;
        RemoveFromPlaylistAction?.Invoke(GetActionTargets());
    }

    private void OnRemoveFromQueue()
    {
        if (!IsQueueContext) return;

        var targets = GetActionTargets();
        for (int i = 0; i < targets.Count; i++)
            _audio.RemoveFromQueue(targets[i]);
    }

    private async Task PlayAsync()
    {
        if (_audio.CurrentTrack?.Id == Id)
            await _audio.SetPlaybackStateAsync(!_audio.IsPlaying).ConfigureAwait(false);
        else
            _onPlay?.Invoke(Track);
    }

    private async Task CopyTitleAsync(object? parameter)
    {
        if (IsDisposed) return;
        await TrackCopyHelper.CopyTrackTitlesAsync(GetActionTargets(), parameter).ConfigureAwait(false);
    }

    private async Task SaveToDownloadsAsync()
    {
        var targets = GetActionTargets();
        for (int i = 0; i < targets.Count; i++)
        {
            var targetTrack = targets[i];
            if (targetTrack.IsDownloaded) continue;

            if (targetTrack.IsCached)
            {
                var cache = AudioSourceFactory.GlobalCache;
                if (cache == null) continue;

                bool success = await cache.ExportTrackToDownloadsAsync(
                    targetTrack.Id,
                    async id => await _library.GetTrackAsync(id).ConfigureAwait(false),
                    async t => await _library.AddOrUpdateTrackAsync(t).ConfigureAwait(false)).ConfigureAwait(false);

                if (success) targetTrack.IsDownloaded = true;
            }
            else
            {
                _downloads.StartDownload(targetTrack);
            }
        }
    }

    public void SetActive(bool isActive, bool isPlaying)
    {
        IsActive = isActive;
        IsPlaying = isActive && isPlaying;
    }

    public void SetDownloadState(bool isDownloading, float progress)
    {
        if (Track.IsDownloaded)
            isDownloading = false;

        IsDownloading = isDownloading;
        DownloadProgress = isDownloading ? progress : 0f;

        OnPropertyChanged(nameof(HasCacheIcon));
        OnPropertyChanged(nameof(CacheIconGeometry));
        OnPropertyChanged(nameof(CacheIconTooltip));

        if (!isDownloading)
        {
            OnPropertyChanged(nameof(DownloadStatusText));
        }
    }

    public void UpdatePlayAction(Action<TrackInfo>? onPlay) => _onPlay = onPlay;

    private async Task CopyLinkAsync()
    {
        if (IsDisposed) return;

        var targets = GetActionTargets();
        if (targets.Count > 1)
        {
            var builder = new System.Text.StringBuilder(targets.Count * 45);
            for (int i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                var targetUrl = target.Url;
                if (string.IsNullOrEmpty(targetUrl))
                    targetUrl = $"https://www.youtube.com/watch?v={target.GetRawId()}";

                if (!string.IsNullOrEmpty(targetUrl))
                {
                    if (builder.Length > 0) builder.AppendLine();
                    builder.Append(targetUrl);
                }
            }

            if (builder.Length == 0)
            {
                CopyHintService.Instance.Show(
                    L["Track_CopyLink_NoUrl"],
                    CopyHintKind.Warning,
                    null);
                return;
            }

            await Clipboard.SetTextAsync(builder.ToString()).ConfigureAwait(false);
            CopyHintService.Instance.Show(
                L["Track_Copied"],
                CopyHintKind.Success,
                null);
            return;
        }

        var url = Track.Url;
        if (string.IsNullOrEmpty(url))
            url = $"https://www.youtube.com/watch?v={Track.GetRawId()}";

        if (string.IsNullOrEmpty(url))
        {
            CopyHintService.Instance.Show(
                L["Track_CopyLink_NoUrl"],
                CopyHintKind.Warning,
                null);
            return;
        }

        await Clipboard.SetTextAsync(url).ConfigureAwait(false);

        CopyHintService.Instance.Show(
            L["Track_Copied"],
            CopyHintKind.Success,
            null);
    }

    protected override void Dispose(bool disposing)
    {
        if (IsDisposed) return;
        if (disposing)
        {
            _submenuCts?.Cancel();
            _submenuCts?.Dispose();
            _submenuCts = null;

            _trackSubscription.Unsubscribe();
            _onPlay = null;
            StartRadioAction = null;
            RemoveFromPlaylistAction = null;
            SelectionProvider = null;
            PlaylistMenuItems.Clear();
        }
        base.Dispose(disposing);
        IsDisposed = true;
    }
}
