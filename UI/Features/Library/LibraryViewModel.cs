using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Threading;
using LMP.Core.Youtube.Search;
using LMP.UI.Dialogs;
using LMP.UI.Features.Shell;

namespace LMP.UI.Features.Library;

/// <summary>
/// ViewModel страницы «Библиотека» — управление плейлистами пользователя.
/// </summary>
public sealed partial class LibraryViewModel : ViewModelBase, ISmoothTransitionViewModel
{
    #region Зависимости

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

    #region Внутреннее состояние

    private CancellationTokenSource? _syncCts;
    private CancellationTokenSource? _staggerCts;
    private CancellationTokenSource? _statsAnimCts;
    private DispatcherTimer? _dataChangedTimer;
    private bool _isDisposed;
    private int _prevPlaylistCount;
    private int _prevTrackCount;

    private bool _isDataLoaded;
    private string _loadedOwnerId = string.Empty;
    private bool _isDirty;
    private bool _isViewActive = true;

    #endregion

    #region Свойства

    [ObservableProperty] public partial bool IsContentReady { get; private set; }
    [ObservableProperty] public partial bool IsLoading { get; private set; }
    [ObservableProperty] public partial bool IsSyncing { get; private set; }
    [ObservableProperty] public partial double SyncProgress { get; private set; }
    [ObservableProperty] public partial string SyncStatus { get; private set; } = "";
    [ObservableProperty] public partial bool IsAuthenticated { get; private set; }
    [ObservableProperty] public partial bool HasPlaylists { get; private set; }

    partial void OnIsSyncingChanged(bool value)
    {
        if (Dispatcher.UIThread.CheckAccess())
            SyncAccountPlaylistsCommand.NotifyCanExecuteChanged();
        else
            Dispatcher.UIThread.Post(() => SyncAccountPlaylistsCommand.NotifyCanExecuteChanged());
    }

    #endregion

    #region Статистика

    [ObservableProperty] public partial bool IsStatsVisible { get; private set; }
    [ObservableProperty] public partial string PlaylistCountText { get; private set; } = "";
    [ObservableProperty] public partial string TotalTracksText { get; private set; } = "";
    [ObservableProperty] public partial string TotalDurationText { get; private set; } = "";
    [ObservableProperty] public partial string AvgTrackDurationText { get; private set; } = "";
    [ObservableProperty] public partial string AvgPlaylistDurationText { get; private set; } = "";

    #endregion

    #region Коллекция и команды

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
    }

    /// <inheritdoc />
    public void PrepareForTransition()
    {
        _isViewActive = false;
        if (!_isDataLoaded)
            IsContentReady = false;
    }

    /// <inheritdoc />
    public override async Task OnNavigatedToAsync()
    {
        if (_isDisposed) return;

        _isViewActive = true;

        var currentOwnerId = _auth.State.DisplayId;
        if (_isDataLoaded && string.Equals(_loadedOwnerId, currentOwnerId, StringComparison.Ordinal))
        {
            IsContentReady = true;

            await RefreshLikedTrackCountAsync().ConfigureAwait(false);

            int currentPlaylists = Playlists.Count;
            int currentTracks = Playlists.Sum(p => p.TrackCount);

            if (_isDirty || currentPlaylists != _prevPlaylistCount || currentTracks != _prevTrackCount)
            {
                _isDirty = false;
                UpdateStatsInBackground();
            }

            return;
        }

        await LoadPlaylistsAsync().ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_isDisposed) return;
            _isDataLoaded = true;
            _loadedOwnerId = _auth.State.DisplayId;
            _isDirty = false;
            IsContentReady = true;
        });
    }

    /// <inheritdoc />
    protected override void OnSuspend() => _isViewActive = false;

    /// <inheritdoc />
    protected override async void OnResume()
    {
        base.OnResume();
        _isViewActive = true;
        if (_isDirty)
        {
            _isDirty = false;
            await RefreshLikedTrackCountAsync().ConfigureAwait(false);
            UpdateStatsInBackground();
        }
    }

    /// <summary>
    /// Оформляет подписки на события сервисов библиотеки и плейлистов.
    /// </summary>
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
        }

        Dispatcher.UIThread.Post(async () =>
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

                await Task.Delay(50);
                vm.Show();
            }

            if (_isViewActive)
            {
                UpdateStatsInBackground();
            }
            else
            {
                _isDirty = true;
            }
        });
    }

    private void OnPlaylistRemovedIncremental(string playlistId)
    {
        if (_isDisposed || IsSyncing) return;

        if (!_isViewActive)
        {
            _isDirty = true;
        }

        Dispatcher.UIThread.Post(() =>
        {
            var vm = Playlists.FirstOrDefault(x => x.Id == playlistId);
            if (vm != null)
            {
                vm.Dispose();
                Playlists.Remove(vm);

                if (_isViewActive)
                {
                    UpdateStatsInBackground();
                }
                else
                {
                    _isDirty = true;
                }
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
            async (_, _) =>
            {
                _dataChangedTimer?.Stop();
                if (_isDisposed || IsSyncing) return;

                await RefreshLikedTrackCountAsync().ConfigureAwait(false);
                UpdateStatsInBackground();
            });
        _dataChangedTimer.Start();
    }

    private async void UpdateStatsInBackground()
    {
        try { await UpdateStatsAnimatedAsync(); }
        catch (Exception ex) { Log.Warn($"[Library] Update stats error: {ex.Message}"); }
    }

    private int CalculateInsertIndex(Core.Models.Playlist playlist)
    {
        if (playlist.Id == LibraryService.LikedPlaylistId) return 0;

        int index = 0;
        foreach (var vm in Playlists)
        {
            if (vm.IsLikedPlaylist) { index++; continue; }
            if (playlist.IsLocal && !vm.IsLocal) break;
            if (!playlist.IsLocal && vm.IsLocal) { index++; continue; }
            if (string.Compare(playlist.Name, vm.Name, StringComparison.Ordinal) < 0) break;
            index++;
        }

        return index;
    }

    /// <summary>
    /// Открывает диалог создания плейлиста и передает управление в доменный сервис.
    /// </summary>
    private async Task OpenCreateDialogAsync()
    {
        if (_isDisposed) return;

        var result = await _dialog.ShowCreatePlaylistDialogAsync();
        if (result == null || string.IsNullOrWhiteSpace(result.Name)) return;

        var trimmedName = result.Name.Trim();
        var playlist = await _playlistService.CreatePlaylistAsync(trimmedName);

        if (result.SyncToCloud && _auth.IsAuthenticated)
        {
            _mainWindow.LockNavigation(SL["Playlist_CreatingCloud"] ?? "Creating on cloud...");
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
        IsContentReady = false;
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

            List<Core.Models.Playlist> ytPlaylists;
            try
            {
                ytPlaylists = await _youtube.GetUserPlaylistsByAuthAsync();
                ct.ThrowIfCancellationRequested();
                SyncProgress = 0.1;

                var filtered = ytPlaylists
                    .Where(p => !string.IsNullOrEmpty(p.YoutubeId) && p.YoutubeId != "LM" && p.YoutubeId != "VLLM" && !p.YoutubeId.StartsWith("RD"))
                    .ToList();

                playlistsToImport = [.. filtered.Select(p =>
                {
                    var pid = new Core.Youtube.Playlists.PlaylistId(p.YoutubeId!);
                    var thumbs = new List<Thumbnail>();
                    if (!string.IsNullOrEmpty(p.ThumbnailUrl))
                        thumbs.Add(new Thumbnail(p.ThumbnailUrl, new Resolution(0, 0)));
                    return new PlaylistSearchResult(pid, p.Name, null, thumbs);
                })];
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (!_isDisposed)
                    await _dialog.ShowInfoAsync(SL["Dialog_Error_Title"], SL["Sync_Error_API"] + ": " + ex.Message);
                return;
            }

            SyncProgress = 0.15;
            var allPlaylists = await _playlistService.GetAllPlaylistsAsync(ct);

            var cloudPlaylistIds = new HashSet<string>(
                ytPlaylists.Select(p => p.YoutubeId).Where(id => !string.IsNullOrEmpty(id))!,
                StringComparer.Ordinal);

            int orphansCleaned = 0;
            for (int i = 0; i < allPlaylists.Count; i++)
            {
                var localPl = allPlaylists[i];
                if (localPl.SyncMode == PlaylistSyncMode.TwoWaySync
                    && !string.IsNullOrEmpty(localPl.YoutubeId)
                    && localPl.Id != LibraryService.LikedPlaylistId
                    && !cloudPlaylistIds.Contains(localPl.YoutubeId))
                {
                    localPl.SyncMode = PlaylistSyncMode.LocalOnly;
                    localPl.YoutubeId = null;
                    localPl.IsCloudUnavailable = false;
                    await _playlistService.AddOrUpdatePlaylistAsync(localPl, ct);
                    orphansCleaned++;
                }
            }

            if (orphansCleaned > 0)
            {
                await _notifications.ShowToastAsync(
                    "Dialog_Warning_Title",
                    "Playlist_OrphansCleaned",
                    NotificationSeverity.Warning,
                    durationMs: 4000,
                    messageArgs: [orphansCleaned]);
            }

            if (playlistsToImport.Count == 0)
            {
                var confirmSyncLikes = await _dialog.ConfirmAsync(
                    SL["Sync_ConfirmLikedOnly"] ?? "No playlists found",
                    SL["Sync_NoPlaylistsFound_AskLiked"] ?? "Sync liked songs?",
                    SL["Common_Yes"] ?? "Yes",
                    SL["Common_No"] ?? "No");

                if (confirmSyncLikes)
                {
                    SyncStatus = SL["Sync_LikedSongs"];
                    await _playlistService.SyncLikedTracksAsync(ct);
                    await _dialog.ShowInfoAsync(
                        SL["Dialog_Done_Title"],
                        SL["Sync_Success_Msg_LikedOnly"] ?? "Liked songs synchronized.");
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

                    for (int i = 0; i < fullPlaylist.TrackIds.Count; i++)
                    {
                        var trackId = fullPlaylist.TrackIds[i];
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
            await Task.Delay(300);
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                try { _mainWindow.UnlockNavigation(); } catch { }

                if (!_isDisposed)
                {
                    IsSyncing = false;
                    SyncProgress = 0;
                    SyncStatus = string.Empty;
                    try { await LoadPlaylistsAsync(); } catch { }
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

    /// <summary>
    /// Анимированно интерполирует значения счетчиков статистики библиотеки.
    /// Анимация счетчиков плейлистов и треков запускается немедленно, параллельно с запросом длительности из БД.
    /// </summary>
    private async Task UpdateStatsAnimatedAsync()
    {
        if (_isDisposed || !_isViewActive) return;

        _statsAnimCts?.Cancel();
        _statsAnimCts?.Dispose();
        _statsAnimCts = new CancellationTokenSource();
        var ct = _statsAnimCts.Token;

        var targetPlaylists = Playlists.Count;
        var targetTracks = Playlists.Sum(p => p.TrackCount);

        int startPlaylists = _prevPlaylistCount;
        int startTracks = _prevTrackCount;
        int diff = Math.Abs(targetPlaylists - startPlaylists) + Math.Abs(targetTracks - startTracks);

        // Запускаем фоновый подсчет общей длительности параллельно с тиками анимации
        var durationTask = _library.GetTotalLibraryDurationAsync(ct);

        if (diff == 0)
        {
            long ticks = 0;
            try { ticks = await durationTask.ConfigureAwait(false); } catch { }

            if (ct.IsCancellationRequested || _isDisposed || !_isViewActive) return;

            var duration = TimeSpan.FromTicks(ticks);
            var avgTrk = targetTracks > 0 ? TimeSpan.FromTicks(duration.Ticks / targetTracks) : TimeSpan.Zero;
            var avgPl = targetPlaylists > 0 ? TimeSpan.FromTicks(duration.Ticks / targetPlaylists) : TimeSpan.Zero;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested || _isDisposed || !_isViewActive) return;
                PlaylistCountText = SL.GetPlural("Library_PlaylistWord", targetPlaylists);
                TotalTracksText = SL.GetPlural("Library_TrackWord", targetTracks);
                TotalDurationText = FormatDurationLocalized(duration);
                AvgTrackDurationText = $"⌀ {SL["Library_AvgTrack"]}: {FormatDurationShort(avgTrk)}";
                AvgPlaylistDurationText = $"⌀ {SL["Library_AvgPlaylist"]}: {FormatDurationLocalized(avgPl)}";
                _prevPlaylistCount = targetPlaylists;
                _prevTrackCount = targetTracks;
                IsStatsVisible = true;
            }, DispatcherPriority.Normal, ct);
            return;
        }

        int steps = diff <= 3 ? 15 : 25;

        try
        {
            for (int i = 1; i <= steps; i++)
            {
                if (ct.IsCancellationRequested || _isDisposed || !_isViewActive) return;

                double t = (double)i / steps;
                double ease = 1 - Math.Pow(1 - t, 3);

                int currentPlaylists = startPlaylists + (int)Math.Round((targetPlaylists - startPlaylists) * ease);
                int currentTracks = startTracks + (int)Math.Round((targetTracks - startTracks) * ease);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested || _isDisposed || !_isViewActive) return;

                    PlaylistCountText = SL.GetPlural("Library_PlaylistWord", currentPlaylists);
                    TotalTracksText = SL.GetPlural("Library_TrackWord", currentTracks);

                    if (!IsStatsVisible)
                        IsStatsVisible = true;
                }, DispatcherPriority.Normal, ct);

                try { await Task.Delay(16, ct); }
                catch (OperationCanceledException) { break; }
            }

            long totalTicks = 0;
            try { totalTicks = await durationTask.ConfigureAwait(false); } catch { }

            if (ct.IsCancellationRequested || _isDisposed || !_isViewActive) return;

            var finalDuration = TimeSpan.FromTicks(totalTicks);
            var finalAvgTrack = targetTracks > 0 ? TimeSpan.FromTicks(finalDuration.Ticks / targetTracks) : TimeSpan.Zero;
            var finalAvgPlaylist = targetPlaylists > 0 ? TimeSpan.FromTicks(finalDuration.Ticks / targetPlaylists) : TimeSpan.Zero;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested || _isDisposed || !_isViewActive) return;

                PlaylistCountText = SL.GetPlural("Library_PlaylistWord", targetPlaylists);
                TotalTracksText = SL.GetPlural("Library_TrackWord", targetTracks);
                TotalDurationText = FormatDurationLocalized(finalDuration);
                AvgTrackDurationText = $"⌀ {SL["Library_AvgTrack"]}: {FormatDurationShort(finalAvgTrack)}";
                AvgPlaylistDurationText = $"⌀ {SL["Library_AvgPlaylist"]}: {FormatDurationLocalized(finalAvgPlaylist)}";
                _prevPlaylistCount = targetPlaylists;
                _prevTrackCount = targetTracks;
                IsStatsVisible = true;
            }, DispatcherPriority.Normal, ct);
        }
        catch (OperationCanceledException) { }
    }

    private async Task RefreshLikedTrackCountAsync()
    {
        if (_isDisposed) return;

        var countResult = await _playlistService.GetPlaylistWithCountAsync(LibraryService.LikedPlaylistId).ConfigureAwait(false);
        if (countResult == null) return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_isDisposed) return;
            var likedCard = Playlists.FirstOrDefault(p => p.IsLikedPlaylist);
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

    private async Task LoadPlaylistsAsync()
    {
        if (_isDisposed) return;

        _staggerCts?.Cancel();
        _staggerCts?.Dispose();
        _staggerCts = new CancellationTokenSource();
        var ct = _staggerCts.Token;

        // Скрываем плашку только если данные еще ни разу не были загружены
        if (!_isDataLoaded)
        {
            IsStatsVisible = false;
        }

        var allPlaylistsWithCounts = await Task.Run(
            () => _playlistService.GetAllPlaylistsWithCountsAsync(ct), ct).ConfigureAwait(false);

        if (_isDisposed || ct.IsCancellationRequested) return;

        var sorted = allPlaylistsWithCounts
            .OrderByDescending(x => x.Playlist.Id == LibraryService.LikedPlaylistId)
            .ThenByDescending(x => x.Playlist.IsLocal)
            .ThenBy(x => x.Playlist.Name)
            .ToList();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ct.IsCancellationRequested || _isDisposed) return;

            var existingDict = Playlists.ToDictionary(vm => vm.Id);
            var newIdSet = new HashSet<string>(sorted.Select(x => x.Playlist.Id));

            for (int i = Playlists.Count - 1; i >= 0; i--)
            {
                if (!newIdSet.Contains(Playlists[i].Id))
                {
                    Playlists[i].Dispose();
                    Playlists.RemoveAt(i);
                }
            }

            for (int i = 0; i < sorted.Count; i++)
            {
                var (playlist, trackCount) = sorted[i];

                if (existingDict.TryGetValue(playlist.Id, out var existingVm))
                {
                    existingVm.UpdateFrom(playlist, trackCount);
                }
                else
                {
                    var vm = CreatePlaylistCardVm(playlist, trackCount);
                    vm.Show();

                    if (i >= Playlists.Count)
                        Playlists.Add(vm);
                    else
                        Playlists.Insert(i, vm);
                }
            }

            _loadedOwnerId = _auth.State.DisplayId;
            UpdateStatsInBackground();
        }, DispatcherPriority.Normal, ct);
    }

    private PlaylistCardViewModel CreatePlaylistCardVm(Core.Models.Playlist playlist, int trackCount)
    {
        return new PlaylistCardViewModel(
            _auth,
            _playerControl,
            playlist,
            trackCount,
            onOpen: _mainWindow.NavigateToPlaylist,
            addToQueueAction: async (p) =>
            {
                var tracks = await _playlistService.GetPlaylistTracksAsync(p.Id);
                _audio.EnqueuePlaylistWithNotification(tracks, p.Name);
            },
            playAction: async (p) =>
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
            },
            onDelete: DeletePlaylistAsync,
            onEdit: EditPlaylistFromCardAsync);
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

    /// <summary>
    /// Освобождает управляемые ресурсы и отписывается от событий сервисов.
    /// </summary>
    /// <param name="disposing">Флаг явного вызова Dispose.</param>
    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;
        if (disposing)
        {
            _isDisposed = true;

            Playlists.CollectionChanged -= OnPlaylistsCollectionChanged;
            _playlistService.OnPlaylistChanged -= OnPlaylistChangedIncremental;
            _playlistService.OnPlaylistRemoved -= OnPlaylistRemovedIncremental;
            _library.OnDataChanged -= OnLibraryDataChanged;

            _dataChangedTimer?.Stop();
            _dataChangedTimer = null;

            _staggerCts?.Cancel();
            _staggerCts?.Dispose();

            _statsAnimCts?.Cancel();
            _statsAnimCts?.Dispose();

            for (int i = 0; i < Playlists.Count; i++)
                Playlists[i].Dispose();

            Playlists.Clear();

            _auth.OnAuthStateChanged -= OnAuthChanged;

            _syncCts?.Cancel();
            _syncCts?.Dispose();
        }
        base.Dispose(disposing);
    }
}