using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia.Threading;
using LMP.Core.Youtube.Search;
using LMP.UI.Dialogs;
using LMP.UI.Features.Shell;

namespace LMP.UI.Features.Library;

/// <summary>
/// ViewModel for the "Library" page — high-performance user playlist management.
/// Supports a kinematic parallel statistics counter with CubicEaseOut smoothing,
/// non-blocking multithreaded YouTube import with 403/BotDetection protection, and thread-safe preloading.
/// </summary>
public sealed partial class LibraryViewModel : ViewModelBase
{
    #region Constants

    private const double StatsAnimationDurationMs = 380.0;

    #endregion

    #region Dependencies

    private readonly LibraryService _library;
    private readonly PlaylistService _playlistService;
    private readonly PlaylistSyncService _playlistSync;
    private readonly CookieAuthService _auth;
    private readonly DialogService _dialog;
    private readonly MainWindowViewModel _mainWindow;
    private readonly PlaylistEditService _editService;
    private readonly PlayerControlService _playerControl;

    #endregion

    #region Internal State

    private CancellationTokenSource? _syncCts;
    private CancellationTokenSource? _loadCts;
    private DispatcherTimer? _dataChangedTimer;
    private DispatcherTimer? _statsRollTimer;
    private Stopwatch? _statsRollStopwatch;

    private readonly Task? _initialLoadTask;
    private bool _isDisposed;
    private bool _isDataLoaded;
    private bool _isDirty;
    private bool _isViewActive;
    private bool _hasAnimatedStatsOnce;
    private string _loadedOwnerId = string.Empty;

    // Animation snapshots
    private int _startPlaylists;
    private int _targetPlaylists;
    private int _startTracks;
    private int _targetTracks;
    private long _startDurationTicks;
    private long _targetDurationTicks;

    // Last committed counter values
    private int _lastRenderedPlaylists;
    private int _lastRenderedTracks;
    private long _lastRenderedDurationTicks;

    #endregion

    #region Properties — State & Progress

    [ObservableProperty] public partial bool IsContentReady { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncAccountPlaylistsCommand))]
    public partial bool IsSyncing { get; private set; }

    [ObservableProperty] public partial double SyncProgress { get; private set; }
    [ObservableProperty] public partial string SyncStatus { get; private set; } = string.Empty;
    [ObservableProperty] public partial bool IsAuthenticated { get; private set; }
    [ObservableProperty] public partial bool HasPlaylists { get; private set; }

    #endregion

    #region Properties — Statistics (Rolling Counter)

    [ObservableProperty] public partial bool IsStatsVisible { get; private set; }
    [ObservableProperty] public partial string PlaylistCountText { get; private set; } = "0";
    [ObservableProperty] public partial string TotalTracksText { get; private set; } = "0";
    [ObservableProperty] public partial string TotalDurationText { get; private set; } = string.Empty;
    [ObservableProperty] public partial string AvgTrackDurationText { get; private set; } = string.Empty;
    [ObservableProperty] public partial string AvgPlaylistDurationText { get; private set; } = string.Empty;

    #endregion

    #region Collections & Commands

    public ObservableCollection<PlaylistCardViewModel> Playlists { get; } = [];

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand OpenCreateCommand { get; }
    public IAsyncRelayCommand SyncAccountPlaylistsCommand { get; }
    public IRelayCommand CancelSyncCommand { get; }

    #endregion

    protected override bool HandlesAccountChanges => true;

    public LibraryViewModel(
        LibraryService library,
        PlaylistService playlistService,
        PlaylistSyncService playlistSync,
        CookieAuthService auth,
        MainWindowViewModel mainWindow,
        DialogService dialog,
        PlaylistEditService editService,
        PlayerControlService playerControl)
    {
        _library = library;
        _playlistService = playlistService;
        _playlistSync = playlistSync;
        _auth = auth;
        _dialog = dialog;
        _mainWindow = mainWindow;
        _editService = editService;
        _playerControl = playerControl;

        IsAuthenticated = _auth.IsAuthenticated;
        _auth.OnAuthStateChanged += OnAuthChanged;

        OpenCreateCommand = new AsyncRelayCommand(OpenCreateDialogAsync);
        SyncAccountPlaylistsCommand = new AsyncRelayCommand(SyncAccountPlaylistsAsync, () => !IsSyncing);

        CancelSyncCommand = new RelayCommand(() =>
        {
            _syncCts?.Cancel();
            SyncStatus = SL["Sync_Cancelling"];
        });

        RefreshCommand = new AsyncRelayCommand(LoadPlaylistsAsync);

        SubscribeToEvents();
        Playlists.CollectionChanged += OnPlaylistsCollectionChanged;
        HasPlaylists = Playlists.Count > 0;

        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;

        // Thread-safe data preloading during the Splash Screen phase
        _initialLoadTask = PreloadDataInBackgroundAsync();
    }

    private async Task PreloadDataInBackgroundAsync()
    {
        try
        {
            await LoadPlaylistsCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"[Library] Preload failed: {ex.Message}");
        }
    }

    private void OnLanguageChanged(object? sender, string lang)
    {
        if (_isDisposed) return;
        UpdateStatsInBackground(animateFromZero: false);
    }

    #region Navigation Lifecycle

    /// <inheritdoc />
    public override void OnNavigatedFrom()
    {
        _isViewActive = false;
        StopStatsRollAnimation();
    }

    /// <inheritdoc />
    public override async Task OnNavigatedToAsync()
    {
        if (_isDisposed) return;

        _isViewActive = true;

        if (_initialLoadTask is { IsCompleted: false } task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Debug($"[Library] Initial task join notice: {ex.Message}");
            }
        }

        var currentOwnerId = _auth.State.DisplayId;
        if (_isDataLoaded && string.Equals(_loadedOwnerId, currentOwnerId, StringComparison.Ordinal))
        {
            await RefreshLikedTrackCountAsync().ConfigureAwait(false);

            if (_isDirty)
            {
                _isDirty = false;
                await LoadPlaylistsAsync().ConfigureAwait(false);
            }
            else
            {
                bool isFirstVisit = !_hasAnimatedStatsOnce;
                _hasAnimatedStatsOnce = true;
                UpdateStatsInBackground(animateFromZero: isFirstVisit);
            }

            IsContentReady = true;
            return;
        }

        await LoadPlaylistsAsync().ConfigureAwait(false);
        _hasAnimatedStatsOnce = true;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_isDisposed) return;
            IsContentReady = true;
        });
    }

    /// <inheritdoc />
    protected override void OnSuspend()
    {
        _isViewActive = false;
        StopStatsRollAnimation();
    }

    /// <inheritdoc />
    protected override async void OnResume()
    {
        base.OnResume();
        _isViewActive = true;
        if (_isDirty)
        {
            _isDirty = false;
            try
            {
                await RefreshLikedTrackCountAsync().ConfigureAwait(false);
                UpdateStatsInBackground(animateFromZero: false);
            }
            catch (Exception ex)
            {
                Log.Warn($"[Library] OnResume refresh error: {ex.Message}");
            }
        }
    }

    #endregion

    #region Event Subscriptions & Incremental Updates

    private void SubscribeToEvents()
    {
        _playlistService.OnPlaylistChanged += OnPlaylistChangedIncremental;
        _playlistService.OnPlaylistRemoved += OnPlaylistRemovedIncremental;
        _library.OnDataChanged += OnLibraryDataChanged;
        _library.OnAccountHydrated += OnLibraryAccountHydrated;
    }

    private void OnLibraryAccountHydrated()
    {
        if (_isDisposed) return;
        if (!_isViewActive)
        {
            _isDirty = true;
            return;
        }

        _ = LoadPlaylistsAsync();
    }

    private void OnPlaylistChangedIncremental(Core.Models.Playlist playlist)
    {
        if (_isDisposed) return;

        if (!_isViewActive)
        {
            _isDirty = true;
            return;
        }

        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                var result = await _playlistService.GetPlaylistWithCountAsync(playlist.Id);
                if (result == null || _isDisposed) return;

                var (freshPlaylist, trackCount) = result.Value;
                var existingVm = Playlists.FirstOrDefault(vm => vm.Id == playlist.Id);

                if (existingVm != null)
                {
                    int oldIndex = Playlists.IndexOf(existingVm);
                    existingVm.UpdateFrom(freshPlaylist, trackCount);

                    int targetIndex = CalculateInsertIndex(freshPlaylist, ignoreItem: existingVm);
                    if (oldIndex >= 0 && targetIndex != oldIndex)
                        Playlists.Move(oldIndex, targetIndex);
                }
                else
                {
                    var vm = CreatePlaylistCardVm(freshPlaylist, trackCount);
                    int insertIndex = CalculateInsertIndex(freshPlaylist);

                    if (insertIndex >= Playlists.Count)
                        Playlists.Add(vm);
                    else
                        Playlists.Insert(insertIndex, vm);

                    vm.Show();
                }

                if (IsSyncing)
                {
                    OnLibraryDataChanged();
                }
                else
                {
                    UpdateStatsInBackground(animateFromZero: false);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[Library] Incremental playlist update error: {ex.Message}");
            }
        });
    }

    private void OnPlaylistRemovedIncremental(string playlistId)
    {
        if (_isDisposed) return;

        if (!_isViewActive)
        {
            _isDirty = true;
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            var vm = Playlists.FirstOrDefault(x => x.Id == playlistId);
            if (vm != null)
            {
                vm.Dispose();
                Playlists.Remove(vm);
                UpdateStatsInBackground(animateFromZero: false);
            }
        });
    }

    private void OnLibraryDataChanged()
    {
        if (_isDisposed) return;

        if (!_isViewActive)
        {
            _isDirty = true;
            return;
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(OnLibraryDataChanged);
            return;
        }

        if (_dataChangedTimer == null)
        {
            _dataChangedTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(350),
                DispatcherPriority.Background,
                (_, _) =>
                {
                    _dataChangedTimer?.Stop();
                    if (_isDisposed || !_isViewActive) return;

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (_isDisposed || !_isViewActive) return;
                            await RefreshLikedTrackCountAsync().ConfigureAwait(false);
                            UpdateStatsInBackground(animateFromZero: false);
                        }
                        catch (Exception ex)
                        {
                            Log.Warn($"[Library] Debounced data change handler error: {ex.Message}");
                        }
                    });
                });
        }
        else
        {
            _dataChangedTimer.Stop();
        }

        _dataChangedTimer.Start();
    }

    #endregion

    #region Rolling Statistics Engine (Juicy Delta & Rolling Animation)

    private void UpdateStatsInBackground(bool animateFromZero)
    {
        if (_isDisposed || !_isViewActive) return;

        int targetPlaylists = Playlists.Count;
        int targetTracks = Playlists.Sum(static p => p.TrackCount);

        _ = Task.Run(async () =>
        {
            long targetTicks = 0;
            try
            {
                targetTicks = await _library.GetTotalLibraryDurationAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Debug($"[Library] Duration query notice: {ex.Message}");
            }

            if (_isDisposed || !_isViewActive) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_isDisposed || !_isViewActive) return;

                StartHarmoniousStatsAnimation(targetPlaylists, targetTracks, targetTicks, animateFromZero);
            }, DispatcherPriority.Render);
        });
    }

    private void StartHarmoniousStatsAnimation(
        int targetPlaylists,
        int targetTracks,
        long targetDurationTicks,
        bool animateFromZero)
    {
        StopStatsRollAnimation();

        _targetPlaylists = targetPlaylists;
        _targetTracks = targetTracks;
        _targetDurationTicks = targetDurationTicks;

        if (animateFromZero)
        {
            _startPlaylists = 0;
            _startTracks = 0;
            _startDurationTicks = 0;
        }
        else
        {
            _startPlaylists = _lastRenderedPlaylists;
            _startTracks = _lastRenderedTracks;
            _startDurationTicks = _lastRenderedDurationTicks;

            if (_startPlaylists == _targetPlaylists &&
                _startTracks == _targetTracks &&
                _startDurationTicks == _targetDurationTicks)
            {
                RenderStatsFrame(1.0);
                IsStatsVisible = true;
                return;
            }
        }

        RenderStatsFrame(0.0);
        IsStatsVisible = true;

        _statsRollStopwatch = Stopwatch.StartNew();
        _statsRollTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16),
            DispatcherPriority.Render,
            OnStatsAnimationTick);
        _statsRollTimer.Start();
    }

    private void OnStatsAnimationTick(object? sender, EventArgs e)
    {
        if (_statsRollStopwatch is null || !_isViewActive || _isDisposed)
        {
            StopStatsRollAnimation();
            return;
        }

        double elapsed = _statsRollStopwatch.Elapsed.TotalMilliseconds;
        double progress = Math.Clamp(elapsed / StatsAnimationDurationMs, 0.0, 1.0);

        double ease = 1.0 - Math.Pow(1.0 - progress, 3);

        RenderStatsFrame(ease);

        if (progress >= 1.0)
        {
            StopStatsRollAnimation();
            RenderStatsFrame(1.0);
        }
    }

    private void RenderStatsFrame(double ease)
    {
        int curPlaylists = (int)Math.Round(_startPlaylists + ((_targetPlaylists - _startPlaylists) * ease));
        int curTracks = (int)Math.Round(_startTracks + ((_targetTracks - _startTracks) * ease));
        long curTicks = (long)Math.Round(_startDurationTicks + ((_targetDurationTicks - _startDurationTicks) * ease));

        _lastRenderedPlaylists = curPlaylists;
        _lastRenderedTracks = curTracks;
        _lastRenderedDurationTicks = curTicks;

        var curDuration = TimeSpan.FromTicks(Math.Max(0, curTicks));
        var curAvgTrack = curTracks > 0 ? TimeSpan.FromTicks(curDuration.Ticks / curTracks) : TimeSpan.Zero;
        var curAvgPlaylist = curPlaylists > 0 ? TimeSpan.FromTicks(curDuration.Ticks / curPlaylists) : TimeSpan.Zero;

        PlaylistCountText = curPlaylists.ToString();
        TotalTracksText = curTracks.ToString();
        TotalDurationText = FormatDurationLocalized(curDuration);

        AvgTrackDurationText = $"⌀ {SL["Library_AvgTrack"]}: {FormatDurationShort(curAvgTrack)}";
        AvgPlaylistDurationText = $"⌀ {SL["Library_AvgPlaylist"]}: {FormatDurationLocalized(curAvgPlaylist)}";
    }

    private void StopStatsRollAnimation()
    {
        _statsRollTimer?.Stop();
        _statsRollTimer = null;
        _statsRollStopwatch?.Stop();
        _statsRollStopwatch = null;
    }

    #endregion

    #region Loading & Data Operations

    private async Task LoadPlaylistsAsync()
    {
        if (_isDisposed) return;

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        await LoadPlaylistsCoreAsync(ct).ConfigureAwait(false);
    }

    private async Task LoadPlaylistsCoreAsync(CancellationToken ct)
    {
        var allPlaylistsWithCounts = await _playlistService.GetAllPlaylistsWithCountsAsync(ct).ConfigureAwait(false);

        if (_isDisposed || ct.IsCancellationRequested) return;

        allPlaylistsWithCounts.Sort((a, b) => ComparePlaylists(a.Playlist, b.Playlist));

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ct.IsCancellationRequested || _isDisposed) return;

            var existingDict = Playlists.ToDictionary(static vm => vm.Id);
            var newIdSet = new HashSet<string>(allPlaylistsWithCounts.Select(static x => x.Playlist.Id));

            for (int i = Playlists.Count - 1; i >= 0; i--)
            {
                if (!newIdSet.Contains(Playlists[i].Id))
                {
                    Playlists[i].Dispose();
                    Playlists.RemoveAt(i);
                }
            }

            int index = 0;
            foreach (var item in allPlaylistsWithCounts)
            {
                var playlist = item.Playlist;
                int trackCount = item.TrackCount;

                if (existingDict.TryGetValue(playlist.Id, out var existingVm))
                {
                    existingVm.UpdateFrom(playlist, trackCount);
                    int currentIdx = Playlists.IndexOf(existingVm);
                    if (currentIdx >= 0 && currentIdx != index && index < Playlists.Count)
                        Playlists.Move(currentIdx, index);
                }
                else
                {
                    var vm = CreatePlaylistCardVm(playlist, trackCount);
                    vm.Show();

                    if (index >= Playlists.Count)
                        Playlists.Add(vm);
                    else
                        Playlists.Insert(index, vm);
                }

                index++;
            }

            _isDataLoaded = true;
            _loadedOwnerId = _auth.State.DisplayId;
            _isDirty = false;

            if (_isViewActive)
                UpdateStatsInBackground(animateFromZero: !_hasAnimatedStatsOnce);
        }, DispatcherPriority.Normal, ct);
    }

    private static int ComparePlaylists(Core.Models.Playlist a, Core.Models.Playlist b)
    {
        bool aLiked = a.Id == LibraryService.LikedPlaylistId;
        bool bLiked = b.Id == LibraryService.LikedPlaylistId;
        if (aLiked != bLiked) return aLiked ? -1 : 1;

        if (a.IsLocal != b.IsLocal) return a.IsLocal ? -1 : 1;

        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    private int CalculateInsertIndex(Core.Models.Playlist playlist, PlaylistCardViewModel? ignoreItem = null)
    {
        if (playlist.Id == LibraryService.LikedPlaylistId) return 0;

        int index = 0;
        foreach (var vm in Playlists)
        {
            if (ReferenceEquals(vm, ignoreItem))
                continue;

            if (ComparePlaylists(playlist, vm.Playlist) < 0)
                break;

            index++;
        }

        return index;
    }

    private async Task OpenCreateDialogAsync()
    {
        if (_isDisposed) return;
        await _editService.CreatePlaylistAsync();
    }

    private void OnAuthChanged()
    {
        if (_isDisposed) return;
        Dispatcher.UIThread.Post(() => IsAuthenticated = _auth.IsAuthenticated, DispatcherPriority.Background);
    }

    protected override void OnAccountChanged()
    {
        base.OnAccountChanged();
        bool wasActive = IsContentReady;
        _isDataLoaded = false;
        _loadedOwnerId = string.Empty;
        _hasAnimatedStatsOnce = false;
        IsContentReady = false;

        foreach (var playlist in Playlists)
            playlist.Dispose();

        Playlists.Clear();

        if (wasActive)
            _ = ReloadAfterAccountChangeAsync();
    }

    private async Task ReloadAfterAccountChangeAsync()
    {
        try
        {
            await LoadPlaylistsAsync();
            if (!_isDisposed)
            {
                _isDataLoaded = true;
                _loadedOwnerId = _auth.State.DisplayId;
                IsContentReady = true;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Library] Failed to reload: {ex.Message}");
        }
    }

    private void SetIsSyncingOnUi(bool syncing)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            IsSyncing = syncing;
            SyncAccountPlaylistsCommand.NotifyCanExecuteChanged();
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsSyncing = syncing;
                SyncAccountPlaylistsCommand.NotifyCanExecuteChanged();
            });
        }
    }

    private async Task SyncAccountPlaylistsAsync()
    {
        if (_isDisposed || IsSyncing) return;

        if (!_auth.IsAuthenticated)
        {
            await _dialog.ShowInfoAsync(SL["Library_SyncYoutube"], SL["Auth_NotSignedIn"]);
            return;
        }

        _syncCts?.Cancel();
        _syncCts?.Dispose();
        _syncCts = new CancellationTokenSource();
        var ct = _syncCts.Token;

        SetIsSyncingOnUi(true);
        SyncProgress = 0.05;
        SyncStatus = SL["Sync_FetchingPlaylists"];
        bool backgroundStarted = false;

        try
        {
            List<PlaylistSearchResult> playlistsToImport;
            try
            {
                var filtered = await _playlistSync.FetchImportableAccountPlaylistsAsync(ct);

                playlistsToImport =
                [
                    .. filtered.Select(p =>
                    {
                        var pid = new Core.Youtube.Playlists.PlaylistId(p.YoutubeId!);
                        var thumbs = new List<Thumbnail>();
                        if (!string.IsNullOrEmpty(p.ThumbnailUrl))
                            thumbs.Add(new Thumbnail(p.ThumbnailUrl, new Resolution(0, 0)));
                        return new PlaylistSearchResult(pid, p.Name, null, thumbs);
                    })
                ];
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error($"[Library] Failed to fetch account playlists: {ex.Message}");
                if (!_isDisposed)
                    await _dialog.ShowInfoAsync(SL["Dialog_Error_Title"], SL["Sync_Error_API"] + ": " + ex.Message);
                return;
            }

            var allPlaylists = await _playlistService.GetAllPlaylistsAsync(ct);

            if (playlistsToImport.Count == 0)
            {
                var confirmSyncLikes = await _dialog.ConfirmAsync(
                    SL["Sync_ConfirmLikedOnly"],
                    SL["Sync_NoPlaylistsFound_AskLiked"],
                    SL["Common_Yes"],
                    SL["Common_No"]);

                if (confirmSyncLikes)
                {
                    backgroundStarted = true;
                    _ = RunBackgroundPlaylistsImportAsync([], [], [], ct);
                }

                return;
            }

            ct.ThrowIfCancellationRequested();

            var existingByName = allPlaylists
                .Where(p => p.IsLocal)
                .GroupBy(p => p.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var existingByYtId = allPlaylists
                .Where(p => p.IsLocal && !string.IsNullOrEmpty(p.YoutubeId))
                .GroupBy(p => p.YoutubeId!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var localNames = new HashSet<string>(existingByName.Keys, StringComparer.Ordinal);
            for (int i = 0; i < playlistsToImport.Count; i++)
            {
                if (existingByYtId.ContainsKey(playlistsToImport[i].Id.Value))
                    localNames.Add(playlistsToImport[i].Title);
            }

            var decisions = await _dialog.ShowSyncSelectionAsync(playlistsToImport, localNames);
            if (decisions.Count == 0 || _isDisposed)
            {
                return;
            }

            var importItems = new List<AccountPlaylistImportItem>(decisions.Count);
            for (int i = 0; i < decisions.Count; i++)
            {
                var d = decisions[i];
                var action = d.Action switch
                {
                    MergeAction.Merge => AccountPlaylistImportAction.Merge,
                    MergeAction.Duplicate => AccountPlaylistImportAction.Duplicate,
                    _ => AccountPlaylistImportAction.Import
                };
                importItems.Add(new AccountPlaylistImportItem(d.Playlist.Id.Value, d.Playlist.Title, action));
            }

            backgroundStarted = true;
            _ = RunBackgroundPlaylistsImportAsync(importItems, existingByName, existingByYtId, ct);
        }
        catch (Exception ex)
        {
            Log.Error($"[Library] Sync initialization error: {ex.Message}");
        }
        finally
        {
            if (!backgroundStarted)
            {
                SetIsSyncingOnUi(false);
                SyncProgress = 0;
                SyncStatus = string.Empty;
            }
        }
    }

    private async Task RunBackgroundPlaylistsImportAsync(
        IReadOnlyList<AccountPlaylistImportItem> items,
        Dictionary<string, Core.Models.Playlist> existingByName,
        Dictionary<string, Core.Models.Playlist> existingByYtId,
        CancellationToken ct)
    {
        try
        {
            await _playlistSync.ImportAccountPlaylistsAsync(
                items,
                existingByName,
                existingByYtId,
                getPlaylistTrackIdsAsync: _playlistService.GetPlaylistTrackIdsAsync,
                savePlaylistAsync: _playlistService.AddOrUpdatePlaylistAsync,
                syncLikedTracksAsync: _playlistService.SyncLikedTracksAsync,
                onProgress: prog => Dispatcher.UIThread.Post(() =>
                {
                    if (!_isDisposed)
                        SyncProgress = prog;
                }),
                cancelCommand: CancelSyncCommand,
                ct: ct).ConfigureAwait(false);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (!_isDisposed)
                {
                    IsSyncing = false;
                    SyncAccountPlaylistsCommand.NotifyCanExecuteChanged();
                    SyncProgress = 0;
                    SyncStatus = string.Empty;
                    try
                    {
                        await RefreshLikedTrackCountAsync().ConfigureAwait(false);
                        UpdateStatsInBackground(animateFromZero: false);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[Library] Post-sync stats refresh failed: {ex.Message}");
                    }
                }
            });
        }
    }

    private async Task RefreshLikedTrackCountAsync()
    {
        if (_isDisposed) return;

        var countResult = await _playlistService.GetPlaylistWithCountAsync(LibraryService.LikedPlaylistId)
            .ConfigureAwait(false);
        if (countResult == null) return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_isDisposed) return;
            var likedCard = Playlists.FirstOrDefault(static p => p.IsLikedPlaylist);
            if (likedCard != null && likedCard.TrackCount != countResult.Value.TrackCount)
            {
                likedCard.TrackCount = countResult.Value.TrackCount;
            }
        });
    }

    private static string FormatDurationLocalized(TimeSpan ts)
    {
        var h = (int)ts.TotalHours;
        var m = ts.Minutes;
        var s = ts.Seconds;

        if (h > 0) return $"{h} {SL["Time_Hours_Short"]} {m} {SL["Time_Minutes_Short"]}";
        if (m > 0) return $"{m} {SL["Time_Minutes_Short"]}";
        return $"{s} {SL["Time_Seconds_Short"]}";
    }

    private static string FormatDurationShort(TimeSpan ts) =>
        LMP.Core.Helpers.DurationFormatter.FormatShort(ts);

    private PlaylistCardViewModel CreatePlaylistCardVm(Core.Models.Playlist playlist, int trackCount)
    {
        return new PlaylistCardViewModel(
            _auth,
            _playerControl,
            playlist,
            trackCount,
            onOpen: _mainWindow.NavigateToPlaylist,
            addToQueueAction: AddPlaylistToQueueAsync,
            playAction: PlayPlaylistFromCardAsync,
            onDelete: DeletePlaylistAsync,
            onEdit: EditPlaylistFromCardAsync);
    }

    private async Task AddPlaylistToQueueAsync(Core.Models.Playlist p)
    {
        var tracks = await _playlistService.GetPlaylistTracksAsync(p.Id);
        _playerControl.EnqueuePlaylistWithNotification(tracks, p.Name);
    }

    private async Task PlayPlaylistFromCardAsync(Core.Models.Playlist p)
    {
        if (_playerControl.ActivePlaylistId == p.Id && _playerControl.IsQueuePure)
        {
            await _playerControl.PlayPauseAsync();
            return;
        }

        var tracks = await _playlistService.GetPlaylistTracksAsync(p.Id);
        if (tracks.Count > 0)
        {
            await _playerControl.PlayPlaylistAsync(
                p.Id,
                tracks,
                tracks[0],
                enableShuffle: false,
                playlistName: p.Name);
        }
    }

    private async Task EditPlaylistFromCardAsync(Core.Models.Playlist playlist)
    {
        if (_isDisposed) return;

        await _editService.EditPlaylistAsync(
            playlist.Id,
            _mainWindow.LockNavigation,
            _mainWindow.UnlockNavigation);
    }

    private async Task DeletePlaylistAsync(string playlistId)
    {
        if (_isDisposed) return;
        var playlist = await _playlistService.GetPlaylistAsync(playlistId);
        if (playlist == null) return;

        if (playlistId == LibraryService.LikedPlaylistId)
        {
            await _dialog.ShowInfoAsync(SL["Dialog_Error_Title"], SL["Playlist_CannotDeleteLiked"]);
            return;
        }

        var confirmed = await _dialog.ConfirmAsync(
            SL["Dialog_Confirm_Title"],
            string.Format(SL["Playlist_DeleteConfirm"], playlist.Name),
            SL["Button_Delete"], SL["Button_Cancel"]);

        if (!confirmed) return;

        await _playlistService.DeletePlaylistAsync(playlistId, deleteFromCloud: true);
    }

    private void OnPlaylistsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isDisposed) return;
        HasPlaylists = Playlists.Count > 0;
    }

    #endregion

    #region IDisposable

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;
        if (disposing)
        {
            _isDisposed = true;

            StopStatsRollAnimation();

            LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;

            Playlists.CollectionChanged -= OnPlaylistsCollectionChanged;
            _playlistService.OnPlaylistChanged -= OnPlaylistChangedIncremental;
            _playlistService.OnPlaylistRemoved -= OnPlaylistRemovedIncremental;
            _library.OnDataChanged -= OnLibraryDataChanged;
            _library.OnAccountHydrated -= OnLibraryAccountHydrated;

            _dataChangedTimer?.Stop();
            _dataChangedTimer = null;

            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = null;

            _syncCts?.Cancel();
            _syncCts?.Dispose();
            _syncCts = null;

            foreach (var playlist in Playlists)
                playlist.Dispose();

            Playlists.Clear();

            _auth.OnAuthStateChanged -= OnAuthChanged;
        }

        base.Dispose(disposing);
    }

    #endregion
}