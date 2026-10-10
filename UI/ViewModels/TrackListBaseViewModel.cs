using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.UI.ViewModels;

/// <summary>
/// Единая архитектурная основа для всех экранов отображения музыкальных треков.
/// Исключает дублирование реактивности плеера, загрузок, SIMD-фильтрации,
/// переходов навигации и перемещения элементов.
/// </summary>
public abstract partial class TrackListBaseViewModel : ViewModelBase, IFilterable
{
    protected readonly LibraryService LibService;
    protected readonly AudioEngine Audio;
    protected readonly DownloadService Downloads;
    protected readonly PlayerControlService PlayerControl;
    protected readonly TrackViewModelFactory VmFactory;

    protected readonly VirtualTrackList _items;
    private CancellationTokenSource? _filterDebounceCts;
    private CancellationTokenSource? _loadCts;
    private const int FilterDebounceMs = 150;
    private int _consecutiveEmptyLoads;

    private bool _isDataLoading = true;

    public IVirtualTrackList Items => _items;

    /// <summary>
    /// Текущий активный трек плеера для прямого доступа представлений без материализации ViewModel.
    /// </summary>
    public TrackInfo? CurrentPlayingTrack => PlayerControl.CurrentTrack;

    public bool IsLoading
    {
        get => _isDataLoading;
        protected set
        {
            if (_isDataLoading == value) return;
            _isDataLoading = value;
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(CanReorderItems));
            LoadMoreCommand.NotifyCanExecuteChanged();
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool IsLoadingMore { get; protected set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool IsFetchingFromNetwork { get; protected set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool HasMoreItems { get; protected set; }

    [ObservableProperty] public partial bool ReachedEnd { get; protected set; }

    [ObservableProperty] public partial string FilterQuery { get; set; } = string.Empty;

    string IFilterable.FilterQuery
    {
        get => FilterQuery;
        set => FilterQuery = value;
    }

    public int TotalCount => _items.TotalCount;
    public int FilteredCount => _items.FilteredCount;
    public bool IsFilterEmpty => _items.FilteredCount == 0 && _items.TotalCount > 0;
    public bool CanReorder => string.IsNullOrWhiteSpace(FilterQuery);
    public virtual bool CanReorderItems => CanReorder && !IsLoading && TotalCount > 1;

    public IAsyncRelayCommand<(int oldIndex, int newIndex)> MoveItemCommand { get; }
    public IAsyncRelayCommand LoadMoreCommand { get; }

    protected override bool HandlesAccountChanges => true;

    protected TrackListBaseViewModel(
        AudioEngine audio,
        DownloadService downloads,
        TrackViewModelFactory vmFactory,
        PlayerControlService? playerControl = null)
    {
        Audio = audio;
        Downloads = downloads;
        VmFactory = vmFactory;
        PlayerControl = playerControl ?? AppEntry.Services.GetRequiredService<PlayerControlService>();
        LibService = AppEntry.Services.GetRequiredService<LibraryService>();

        _items = new VirtualTrackList(vmFactory, OnPlay);

        MoveItemCommand =
            new AsyncRelayCommand<(int oldIndex, int newIndex)>(tuple => MoveItemAsync(tuple.oldIndex, tuple.newIndex));
        LoadMoreCommand = new AsyncRelayCommand(LoadNextBatchAsync,
            () => !IsLoadingMore && !IsLoading && !IsFetchingFromNetwork && HasMoreItems);

        PlayerControl.CurrentTrackChanged += HandleTrackChanged;
        PlayerControl.IsPlayingChanged += HandlePlaybackStateChanged;
        PlayerControl.ForceSyncTriggered += HandleForceSyncTriggered;

        Downloads.OnProgress += HandleDownloadProgress;
        Downloads.OnCompleted += HandleDownloadCompleted;

        AudioSourceFactory.GlobalCache.OnFormatCached += HandleFormatCached;
    }

    public override async Task OnNavigatedToAsync()
    {
        await base.OnNavigatedToAsync().ConfigureAwait(false);
    }

    partial void OnFilterQueryChanged(string value)
    {
        OnPropertyChanged(nameof(CanReorder));
        OnPropertyChanged(nameof(CanReorderItems));

        _filterDebounceCts?.Cancel();
        _filterDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _filterDebounceCts = cts;

        _ = DebounceFilterAsync(value, cts.Token);
    }

    private async Task DebounceFilterAsync(string query, CancellationToken ct)
    {
        if (!await ct.DelayNoThrowAsync(FilterDebounceMs, continueOnCapturedContext: false))
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ct.IsCancellationRequested) return;

            _items.ApplyFilter(query);

            OnPropertyChanged(nameof(FilteredCount));
            OnPropertyChanged(nameof(IsFilterEmpty));
            OnPropertyChanged(nameof(CanReorderItems));
            OnFilterApplied();
        }, DispatcherPriority.Normal, ct);
    }

    protected virtual void OnFilterApplied()
    {
    }

    public async Task MoveItemAsync(int oldVisualIndex, int newVisualIndex)
    {
        if (!CanReorder || oldVisualIndex == newVisualIndex)
            return;

        int count = _items.FilteredCount;
        if (oldVisualIndex < 0 || oldVisualIndex >= count || newVisualIndex < 0 || newVisualIndex >= count)
            return;

        _items.MoveTrack(oldVisualIndex, newVisualIndex);

        try
        {
            await SaveMoveAsync(oldVisualIndex, newVisualIndex, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"[TrackListBase] Save move failed: {ex.Message}");
            _items.MoveTrack(newVisualIndex, oldVisualIndex);
        }
    }

    protected virtual Task SaveMoveAsync(int fromVisualIndex, int toVisualIndex, CancellationToken ct) =>
        Task.CompletedTask;

    protected abstract void OnPlay(TrackInfo track);

    protected virtual Task<List<TrackInfo>> FetchMoreFromNetworkAsync(CancellationToken ct) =>
        Task.FromResult(new List<TrackInfo>());

    private async Task LoadNextBatchAsync()
    {
        if (IsLoadingMore || IsFetchingFromNetwork || !HasMoreItems) return;

        IsLoadingMore = true;
        IsFetchingFromNetwork = true;
        int countBefore = TotalCount;

        try
        {
            var token = _loadCts?.Token ?? CancellationToken.None;
            var newTracks = await FetchMoreFromNetworkAsync(token).ConfigureAwait(false);

            if (token.IsCancellationRequested) return;

            if (newTracks is { Count: > 0 })
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _items.AppendTracks(newTracks);
                    NotifyCountProperties();
                });

                if (TotalCount == countBefore)
                    _consecutiveEmptyLoads++;
                else
                    _consecutiveEmptyLoads = 0;

                if (_consecutiveEmptyLoads >= 5)
                    SetCanFetchMore(false);
            }
            else
            {
                SetCanFetchMore(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error($"[TrackListBase] Batch load failure: {ex.Message}");
            SetCanFetchMore(false);
        }
        finally
        {
            IsLoadingMore = false;
            IsFetchingFromNetwork = false;
            UpdatePaginationState();
        }
    }

    protected void SetCanFetchMore(bool value)
    {
        HasMoreItems = value;
        UpdatePaginationState();
        LoadMoreCommand.NotifyCanExecuteChanged();
    }

    protected void UpdatePaginationState()
    {
        ReachedEnd = !HasMoreItems && TotalCount > 0;
    }

    protected void CancelLoading()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        IsLoadingMore = false;
        IsFetchingFromNetwork = false;
    }

    protected virtual async Task InitializeItemsAsync(IEnumerable<TrackInfo> tracks, bool canFetchMore = true)
    {
        CancelLoading();
        _loadCts = new CancellationTokenSource();
        _consecutiveEmptyLoads = 0;
        SetCanFetchMore(canFetchMore);

        SetTracks(tracks);
        await Task.CompletedTask;
    }

    protected virtual void ClearItems()
    {
        CancelLoading();
        _items.Clear();
        _isDataLoading = false;
        SetCanFetchMore(false);
        NotifyCountProperties();
    }

    public List<TrackInfo> GetItemsSnapshot()
    {
        var list = new List<TrackInfo>(TotalCount);
        for (int i = 0; i < TotalCount; i++)
        {
            var track = _items.GetTrackAt(i);
            if (track != null) list.Add(track);
        }

        return list;
    }

    public List<string> GetLoadedItemsIds()
    {
        var list = new List<string>(TotalCount);
        for (int i = 0; i < TotalCount; i++)
        {
            var track = _items.GetTrackAt(i);
            if (track != null) list.Add(track.Id);
        }

        return list;
    }

    protected void SetTracks(IEnumerable<TrackInfo> tracks, IReadOnlyList<string>? explicitOrder = null,
        bool preserveSelection = false)
    {
        _items.SetTracks(tracks, explicitOrder, preserveSelection);
        _items.UpdatePlaybackState(PlayerControl.CurrentTrack, PlayerControl.IsPlaying);
        _isDataLoading = false;

        NotifyCountProperties();
    }

    protected bool RemoveTrackLocally(string trackId)
    {
        bool ok = _items.RemoveTrack(trackId);
        if (ok)
        {
            NotifyCountProperties();
        }

        return ok;
    }

    protected async Task HydrateCacheStatusAsync(CancellationToken ct = default)
    {
        if (TotalCount == 0) return;

        try
        {
            var tracks = GetItemsSnapshot();
            if (tracks.Count == 0) return;

            await Task.Run(() => AudioSourceFactory.GlobalCache.HydrateCacheStatus(tracks), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"[TrackListBase] Cache hydration error: {ex.Message}");
        }
    }

    private void NotifyCountProperties()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(FilteredCount));
        OnPropertyChanged(nameof(IsFilterEmpty));
        OnPropertyChanged(nameof(CanReorderItems));
        OnPropertyChanged(nameof(IsLoading));
    }

    protected override void OnAccountChanged()
    {
        base.OnAccountChanged();
        _items.Clear();
        _isDataLoading = true;
        NotifyCountProperties();
    }

    private void HandleTrackChanged(TrackInfo? track) =>
        Dispatcher.UIThread.Post(() => _items.UpdatePlaybackState(track, PlayerControl.IsPlaying));

    private void HandlePlaybackStateChanged(bool isPlaying) =>
        Dispatcher.UIThread.Post(() => _items.UpdatePlaybackState(PlayerControl.CurrentTrack, isPlaying));

    private void HandleForceSyncTriggered() =>
        Dispatcher.UIThread.Post(() => _items.UpdatePlaybackState(PlayerControl.CurrentTrack, PlayerControl.IsPlaying));

    private void HandleDownloadProgress(string id, float progress) =>
        Dispatcher.UIThread.Post(() => _items.UpdateDownloadProgress(id, progress));

    private void HandleDownloadCompleted(string id, bool ok, string? path) =>
        Dispatcher.UIThread.Post(() => _items.UpdateDownloadCompleted(id, ok, path));

    private void HandleFormatCached(string trackId, AudioFormat format, int bitrate, bool isExport) =>
        Dispatcher.UIThread.Post(() => _items.UpdateCacheStatus(trackId, format, bitrate));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelLoading();
            _filterDebounceCts?.Cancel();
            _filterDebounceCts?.Dispose();
            _filterDebounceCts = null;

            PlayerControl.CurrentTrackChanged -= HandleTrackChanged;
            PlayerControl.IsPlayingChanged -= HandlePlaybackStateChanged;
            PlayerControl.ForceSyncTriggered -= HandleForceSyncTriggered;

            Downloads.OnProgress -= HandleDownloadProgress;
            Downloads.OnCompleted -= HandleDownloadCompleted;

            AudioSourceFactory.GlobalCache.OnFormatCached -= HandleFormatCached;

            _items.Dispose();
        }

        base.Dispose(disposing);
    }
}
