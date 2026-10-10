using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia.Threading;
using LMP.Core.Youtube.Search;
using LMP.UI.Dialogs;
using LMP.UI.Features.Shell;

namespace LMP.UI.Features.Library;

/// <summary>
/// ViewModel страницы «Библиотека» — высокопроизводительное управление плейлистами пользователя.
/// Поддерживает кинематический параллельный счетчик статистики со сглаживанием CubicEaseOut и потокобезопасную предварительную загрузку.
/// </summary>
public sealed partial class LibraryViewModel : ViewModelBase
{
    #region Constants

    private const double StatsAnimationDurationMs = 380.0;

    #endregion

    #region Dependencies

    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly PlaylistService _playlistService;
    private readonly YoutubeProvider _youtube;
    private readonly CookieAuthService _auth;
    private readonly DialogService _dialog;
    private readonly MainWindowViewModel _mainWindow;
    private readonly PlaylistEditService _editService;
    private readonly NotificationService _notifications;
    private readonly PlayerControlService _playerControl;

    #endregion

    #region Internal State

    private CancellationTokenSource? _syncCts;
    private CancellationTokenSource? _loadCts;
    private DispatcherTimer? _dataChangedTimer;
    private DispatcherTimer? _statsRollTimer;
    private Stopwatch? _statsRollStopwatch;

    private Task? _initialLoadTask;
    private bool _isDisposed;
    private bool _isDataLoaded;
    private bool _isDirty;
    private bool _isViewActive = true;
    private bool _hasAnimatedStatsOnce;
    private string _loadedOwnerId = string.Empty;

    // Снапшоты анимации
    private int _startPlaylists;
    private int _targetPlaylists;
    private int _startTracks;
    private int _targetTracks;
    private long _startDurationTicks;
    private long _targetDurationTicks;

    // Последние зафиксированные значения счетчиков
    private int _lastRenderedPlaylists;
    private int _lastRenderedTracks;
    private long _lastRenderedDurationTicks;

    #endregion

    #region Properties — State & Progress

    [ObservableProperty] public partial bool IsContentReady { get; private set; }
    public bool IsLoading { get; private set; }
    [ObservableProperty] public partial bool IsSyncing { get; private set; }
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
        YoutubeProvider youtube,
        CookieAuthService auth,
        MainWindowViewModel mainWindow,
        DialogService dialog,
        AudioEngine audio,
        NotificationService notifications,
        PlaylistEditService editService,
        PlayerControlService playerControl)
    {
        _audio = audio;
        _library = library;
        _playlistService = playlistService;
        _youtube = youtube;
        _auth = auth;
        _dialog = dialog;
        _mainWindow = mainWindow;
        _notifications = notifications;
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

        // Потокобезопасная предварительная загрузка данных на этапе Splash Screen
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

        // Если предварительная загрузка со сплэша еще завершается, ожидаем её без перезапуска
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
    }

    private void OnPlaylistChangedIncremental(Core.Models.Playlist playlist)
    {
        if (_isDisposed || IsSyncing) return;

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
                if (result == null) return;

                var (freshPlaylist, trackCount) = result.Value;
                var existingVm = Playlists.FirstOrDefault(vm => vm.Id == playlist.Id);

                if (existingVm != null)
                {
                    existingVm.UpdateFrom(freshPlaylist, trackCount);
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

                UpdateStatsInBackground(animateFromZero: false);
            }
            catch (Exception ex)
            {
                Log.Warn($"[Library] Incremental playlist update error: {ex.Message}");
            }
        });
    }

    private void OnPlaylistRemovedIncremental(string playlistId)
    {
        if (_isDisposed || IsSyncing) return;

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
        if (_isDisposed || IsSyncing) return;

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

        _dataChangedTimer?.Stop();
        _dataChangedTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(300),
            DispatcherPriority.Background,
            (_, _) =>
            {
                _dataChangedTimer?.Stop();
                if (_isDisposed || IsSyncing || !_isViewActive) return;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (_isDisposed || IsSyncing || !_isViewActive) return;
                        await RefreshLikedTrackCountAsync().ConfigureAwait(false);
                        UpdateStatsInBackground(animateFromZero: false);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[Library] Debounced data change handler error: {ex.Message}");
                    }
                });
            });
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

            // Если изменений нет, мгновенно фиксируем финальные значения
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

        // Кинематическая кривая замедления Cubic Ease-Out: 1 - (1 - t)^3
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

        var sorted = allPlaylistsWithCounts
            .OrderByDescending(static x => x.Playlist.Id == LibraryService.LikedPlaylistId)
            .ThenByDescending(static x => x.Playlist.IsLocal)
            .ThenBy(static x => x.Playlist.Name)
            .ToList();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ct.IsCancellationRequested || _isDisposed) return;

            var existingDict = Playlists.ToDictionary(static vm => vm.Id);
            var newIdSet = new HashSet<string>(sorted.Select(static x => x.Playlist.Id));

            for (int i = Playlists.Count - 1; i >= 0; i--)
            {
                if (!newIdSet.Contains(Playlists[i].Id))
                {
                    Playlists[i].Dispose();
                    Playlists.RemoveAt(i);
                }
            }

            int index = 0;
            foreach (var item in sorted)
            {
                var playlist = item.Playlist;
                int trackCount = item.TrackCount;

                if (existingDict.TryGetValue(playlist.Id, out var existingVm))
                {
                    existingVm.UpdateFrom(playlist, trackCount);
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

            UpdateStatsInBackground(animateFromZero: !_hasAnimatedStatsOnce);
        }, DispatcherPriority.Normal, ct);
    }

    private int CalculateInsertIndex(Core.Models.Playlist playlist)
    {
        if (playlist.Id == LibraryService.LikedPlaylistId) return 0;

        int index = 0;
        foreach (var vm in Playlists)
        {
            if (vm.IsLikedPlaylist)
            {
                index++;
                continue;
            }

            if (playlist.IsLocal && !vm.IsLocal) break;
            if (!playlist.IsLocal && vm.IsLocal)
            {
                index++;
                continue;
            }

            if (string.Compare(playlist.Name, vm.Name, StringComparison.Ordinal) < 0) break;
            index++;
        }

        return index;
    }

    private async Task OpenCreateDialogAsync()
    {
        if (_isDisposed) return;

        var result = await _dialog.ShowCreatePlaylistDialogAsync();
        if (result == null || string.IsNullOrWhiteSpace(result.Name)) return;

        var trimmedName = result.Name.Trim();
        var playlist = await _playlistService.CreatePlaylistAsync(
            name: trimmedName,
            description: result.Description,
            thumbnailUrl: result.ThumbnailUrl,
            customColor: result.CustomColor,
            computedColor: result.ComputedColor);

        if (result.SyncToCloud && _auth.IsAuthenticated)
        {
            _mainWindow.LockNavigation(SL["Playlist_CreatingCloud"]);
            try
            {
                bool success = await _playlistService.LinkToCloudAsync(playlist.Id);
                if (!success)
                {
                    await _notifications.ShowToastAsync(
                        titleKey: "Dialog_Warning_Title",
                        messageKey: "Playlist_CloudCreateFailed",
                        severity: NotificationSeverity.Warning);
                }
            }
            finally
            {
                _mainWindow.UnlockNavigation();
            }
        }
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

    private async Task SyncAccountPlaylistsAsync()
    {
        if (_isDisposed) return;

        _syncCts?.Cancel();
        _syncCts?.Dispose();
        _syncCts = new CancellationTokenSource();
        var ct = _syncCts.Token;

        IsSyncing = true;
        IsStatsVisible = false;
        SyncProgress = 0;
        SyncStatus = SL["Sync_FetchingPlaylists"];

        _mainWindow.LockNavigation(SL["Sync_InProgress"]);

        try
        {
            List<PlaylistSearchResult> playlistsToImport = [];
            if (!_auth.IsAuthenticated)
            {
                await _dialog.ShowInfoAsync(SL["Library_SyncYoutube"], SL["Auth_NotSignedIn"]);
                return;
            }

            try
            {
                var ytPlaylists = await _youtube.GetUserPlaylistsByAuthAsync();
                ct.ThrowIfCancellationRequested();
                SyncProgress = 0.1;

                var filtered = ytPlaylists
                    .Where(p => !string.IsNullOrEmpty(p.YoutubeId) && p.YoutubeId != "LM" && p.YoutubeId != "VLLM" &&
                                !p.YoutubeId.StartsWith("RD"))
                    .ToList();

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

            SyncProgress = 0.15;
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
                    SyncStatus = SL["Sync_LikedSongs"];
                    await _playlistService.SyncLikedTracksAsync(ct);
                    await _dialog.ShowInfoAsync(
                        SL["Dialog_Done_Title"],
                        SL["Sync_Success_Msg_LikedOnly"]);
                }

                return;
            }

            ct.ThrowIfCancellationRequested();
            SyncStatus = SL["Sync_SelectPlaylists"];

            var existingLocal = allPlaylists
                .Where(p => p.IsLocal)
                .GroupBy(p => p.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var localNames = new HashSet<string>(existingLocal.Keys, StringComparer.Ordinal);

            var decisions = await _dialog.ShowSyncSelectionAsync(playlistsToImport, localNames);
            if (decisions.Count == 0 || _isDisposed) return;

            ct.ThrowIfCancellationRequested();
            SyncProgress = 0.2;
            SyncStatus = SL["Sync_ImportingPlaylists"];

            int importedCount = 0;
            int mergedCount = 0;
            int processed = 0;
            int totalToProcess = decisions.Count;

            foreach (var decision in decisions)
            {
                ct.ThrowIfCancellationRequested();
                if (_isDisposed) return;

                SyncStatus = string.Format(SL["Sync_ImportingPlaylist"], decision.Playlist.Title);

                var fullPlaylist = await _youtube.ImportPlaylistAsync(
                    decision.Playlist.Id.Value, _auth.IsAuthenticated, ct);

                if (fullPlaylist == null)
                {
                    processed++;
                    SyncProgress = 0.2 + (0.65 * processed / totalToProcess);
                    continue;
                }

                existingLocal.TryGetValue(decision.Playlist.Title, out var existing);

                if (decision.Action == MergeAction.Merge && existing != null)
                {
                    var existingTrackIds = await _playlistService.GetPlaylistTrackIdsAsync(existing.Id, ct);
                    var existingTrackSet = new HashSet<string>(existingTrackIds, StringComparer.Ordinal);

                    bool tracksChanged = false;
                    bool metadataChanged = ApplyMergedCloudMetadata(existing, fullPlaylist);

                    foreach (var trackId in fullPlaylist.TrackIds)
                    {
                        if (existingTrackSet.Add(trackId))
                        {
                            existing.TrackIds.Add(trackId);
                            tracksChanged = true;
                        }

                        var t = await _library.GetTrackAsync(trackId, ct);
                        if (t != null && !t.InPlaylists.Contains(existing.Id))
                        {
                            t.InPlaylists.Add(existing.Id);
                            await _library.AddOrUpdateTrackAsync(t, ct);
                        }
                    }

                    if (tracksChanged || metadataChanged)
                        await _playlistService.AddOrUpdatePlaylistAsync(existing, ct);

                    mergedCount++;
                }
                else
                {
                    if (decision.Action == MergeAction.Duplicate && existing != null)
                        fullPlaylist.Name = $"{decision.Playlist.Title} ({SL["Sync_DuplicateName"]})";

                    if (_auth.IsAuthenticated)
                    {
                        fullPlaylist.SyncMode = PlaylistSyncMode.TwoWaySync;
                        fullPlaylist.YoutubeId = decision.Playlist.Id.Value;
                    }

                    await _playlistService.AddOrUpdatePlaylistAsync(fullPlaylist, ct);
                    importedCount++;
                }

                processed++;
                SyncProgress = 0.2 + (0.65 * processed / totalToProcess);
            }

            ct.ThrowIfCancellationRequested();
            SyncStatus = SL["Sync_LikedSongs"];
            SyncProgress = 0.9;
            await _playlistService.SyncLikedTracksAsync(ct);

            SyncProgress = 1.0;
            SyncStatus = SL["Sync_Complete"];

            if (!_isDisposed)
            {
                await _notifications.ShowToastAsync(
                    "Sync_Complete_Title",
                    "Sync_Success_Msg",
                    NotificationSeverity.Success,
                    durationMs: 5000,
                    messageArgs: [importedCount, mergedCount]);
            }
        }
        catch (OperationCanceledException)
        {
            SyncStatus = SL["Sync_Cancelled"];
        }
        catch (Exception ex)
        {
            Log.Error($"[Library] Sync error: {ex.Message}");
            if (!_isDisposed)
            {
                await _notifications.ShowToastAsync(
                    "Dialog_Error_Title",
                    "Sync_Error_API",
                    NotificationSeverity.Error,
                    durationMs: 6000,
                    messageArgs: [ex.Message]);
            }
        }
        finally
        {
            await Task.Delay(300, CancellationToken.None);
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                try
                {
                    _mainWindow.UnlockNavigation();
                }
                catch (Exception ex)
                {
                    Log.Debug($"[Library] Navigation unlock notice: {ex.Message}");
                }

                if (!_isDisposed)
                {
                    IsSyncing = false;
                    SyncProgress = 0;
                    SyncStatus = string.Empty;
                    try
                    {
                        await LoadPlaylistsAsync();
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[Library] Post-sync reload playlists failed: {ex.Message}");
                    }
                }
            });
        }
    }

    private static bool ApplyMergedCloudMetadata(Core.Models.Playlist existing, Core.Models.Playlist incoming)
    {
        bool changed = false;

        if (!string.Equals(existing.Author, incoming.Author, StringComparison.Ordinal))
        {
            existing.Author = incoming.Author;
            changed = true;
        }

        if (!string.Equals(existing.OwnerChannelId, incoming.OwnerChannelId, StringComparison.Ordinal))
        {
            existing.OwnerChannelId = incoming.OwnerChannelId;
            changed = true;
        }

        if (existing.Ownership != incoming.Ownership)
        {
            existing.Ownership = incoming.Ownership;
            changed = true;
        }

        if (existing.Visibility != incoming.Visibility)
        {
            existing.Visibility = incoming.Visibility;
            changed = true;
        }

        if (existing.CloudTrackCount != incoming.CloudTrackCount)
        {
            existing.CloudTrackCount = incoming.CloudTrackCount;
            changed = true;
        }

        if (existing.ViewCount != incoming.ViewCount)
        {
            existing.ViewCount = incoming.ViewCount;
            changed = true;
        }

        if (existing.ReleaseDate != incoming.ReleaseDate)
        {
            existing.ReleaseDate = incoming.ReleaseDate;
            changed = true;
        }

        if (!string.Equals(existing.ThumbnailUrl, incoming.ThumbnailUrl, StringComparison.Ordinal))
        {
            existing.ThumbnailUrl = incoming.ThumbnailUrl;
            changed = true;
        }

        if (!string.Equals(existing.Description, incoming.Description, StringComparison.Ordinal))
        {
            existing.Description = incoming.Description;
            changed = true;
        }

        if (existing.IsCloudUnavailable)
        {
            existing.IsCloudUnavailable = false;
            changed = true;
        }

        existing.LastSyncedAtUtc = DateTime.UtcNow;
        existing.UpdatedAt = DateTime.Now;

        return changed;
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
        ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");

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
        _audio.EnqueuePlaylistWithNotification(tracks, p.Name);
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
            await _playerControl.PlayPlaylistAsync(p.Id, tracks, tracks[0], enableShuffle: false);
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

        _mainWindow.LockNavigation(SL["Playlist_Deleting"]);
        try
        {
            await _playlistService.DeletePlaylistAsync(playlistId, deleteFromCloud: true);
        }
        finally
        {
            _mainWindow.UnlockNavigation();
        }
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
