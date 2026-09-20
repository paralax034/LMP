using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using LMP.UI.Dialogs;
using LMP.UI.Features.Shared;
using LMP.UI.Features.Shell;
using LMP.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.UI.Features.Playlist;

/// <summary>
/// ViewModel экрана плейлиста.
/// Управляет метаданными, отображением ownership/visibility,
/// воспроизведением и двусторонней синхронизацией с YouTube.
/// </summary>
public sealed partial class PlaylistViewModel : TrackListReorderableViewModel, ISmoothTransitionViewModel
{
    #region Fields

    private readonly DialogService _dialog;
    private readonly DominantColorService _dominantColor;
    private readonly MainWindowViewModel _mainWindow;
    private readonly PlaylistEditService _editService;
    private readonly PlaylistSyncService _syncService;
    private readonly PlayerControlService _playerControl;
    private readonly CookieAuthService _auth;

    private readonly EventHandler<string> _languageChangedHandler;
    private DispatcherTimer? _dataChangedDebounceTimer;
    private DispatcherTimer? _shuffleAnimationTimer;
    private DispatcherTimer? _downloadAnimationTimer;

    private CancellationTokenSource? _playlistLoadCts;
    private string _currentPlaylistId = "";
    private Core.Models.Playlist? _currentPlaylist;

    private DateTime _lastLocalMutationTime = DateTime.MinValue;
    private const int LocalMutationDebounceMs = 1500;

    private List<TrackInfo>? _allTracksCache;
    private bool _allTracksCacheValid;

    private volatile bool _isSuspended;
    private int _syncInProgressGate;

    #endregion

    #region Properties — Metadata

    [ObservableProperty] public partial string PlaylistName { get; private set; } = string.Empty;
    [ObservableProperty] public partial string? ThumbnailUrl { get; private set; }
    [ObservableProperty] public partial string? Description { get; private set; }
    [ObservableProperty] public partial int TrackCount { get; private set; }
    [ObservableProperty] public partial TimeSpan TotalDuration { get; private set; }
    [ObservableProperty] public partial string FormattedDuration { get; set; } = "";
    [ObservableProperty] public partial IBrush? HeaderBackground { get; private set; }
    [ObservableProperty] public partial bool IsLikedPlaylist { get; private set; }

    /// <summary>Отформатированное количество просмотров (компактный вид: 1.2K, 3.5M).</summary>
    [ObservableProperty] public partial string? FormattedViewCount { get; private set; }

    /// <summary>Отформатированная дата обновления плейлиста.</summary>
    [ObservableProperty] public partial string? FormattedReleaseDate { get; private set; }

    public string FormattedTrackCount =>
        LocalizationService.Instance.GetPlural("Playlist_TracksCount", TrackCount);

    public string? PlaylistYoutubeUrl =>
        IsLikedPlaylist && _auth.IsAuthenticated
            ? "https://www.youtube.com/playlist?list=LL"
            : _currentPlaylist?.YoutubeId is { Length: > 0 } id
                ? $"https://www.youtube.com/playlist?list={id}"
                : null;

    [ObservableProperty] public partial bool HasYoutubeLink { get; private set; }

    partial void OnTrackCountChanged(int value)
    {
        PlayAllCommand.NotifyCanExecuteChanged();
        ShufflePlayCommand.NotifyCanExecuteChanged();
        DownloadAllCommand.NotifyCanExecuteChanged();
        AddToQueueCommand.NotifyCanExecuteChanged();
    }

    #endregion

    #region Properties — Author & Ownership

    /// <summary>Имя автора/владельца плейлиста.</summary>
    [ObservableProperty] public partial string? AuthorName { get; private set; }

    /// <summary>Показывать строку автора (любой плейлист с известным автором).</summary>
    [ObservableProperty] public partial bool ShowAuthor { get; private set; }

    /// <summary>Плейлист доступен только для прослушивания (Foreign / CloudPublic).</summary>
    [ObservableProperty] public partial bool IsReadOnly { get; private set; }

    /// <summary>Плейлист можно редактировать (не read-only, не system).</summary>
    [ObservableProperty] public partial bool CanEdit { get; private set; }

    /// <summary>Плейлист приватный (🔒).</summary>
    [ObservableProperty] public partial bool IsPrivate { get; private set; }

    /// <summary>Плейлист доступен по ссылке (🔗).</summary>
    [ObservableProperty] public partial bool IsUnlisted { get; private set; }

    partial void OnCanEditChanged(bool value)
    {
        CanReorderItems = value && CanReorder;
        MergePlaylistCommand.NotifyCanExecuteChanged();
        EditPlaylistCommand.NotifyCanExecuteChanged();
    }

    #endregion

    #region Properties — Cloud & Sync

    /// <summary>Плейлист связан с YouTube (есть YoutubeId и он доступен).</summary>
    [ObservableProperty] public partial bool HasCloudSource { get; private set; }

    /// <summary>Можно запустить refresh/sync из облака (TwoWaySync или Liked).</summary>
    [ObservableProperty] public partial bool CanRefreshFromCloud { get; private set; }

    /// <summary>Двусторонняя синхронизация активна.</summary>
    [ObservableProperty] public partial bool IsTwoWaySynced { get; private set; }

    /// <summary>Синхронизация в процессе прямо сейчас.</summary>
    [ObservableProperty] public partial bool IsSyncing { get; private set; }

    /// <summary>Есть хотя бы один статусный чип для отображения рядом с action-кнопками.</summary>
    [ObservableProperty] public partial bool HasStatusChips { get; private set; }

    /// <summary>Локализованная строка последней синхронизации (null если не синхронизировался).</summary>
    [ObservableProperty] public partial string? LastSyncedText { get; private set; }

    partial void OnCanRefreshFromCloudChanged(bool value)
    {
        RefreshPlaylistCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsSyncingChanged(bool value)
    {
        RefreshPlaylistCommand.NotifyCanExecuteChanged();
    }

    #endregion

    #region Properties — Playback State

    [ObservableProperty] public partial bool IsPlayingThisPlaylist { get; private set; }
    [ObservableProperty] public partial bool IsShuffleActive { get; private set; }
    [ObservableProperty] public partial bool IsDownloadingActive { get; private set; }
    [ObservableProperty] public partial bool CanReorderItems { get; private set; }

    /// <summary>Очередь «чистая» — запущена из этого плейлиста без сторонних треков.</summary>
    [ObservableProperty] public partial bool IsQueuePure { get; private set; }

    /// <summary>Очередь чистая и сейчас активно играет (для анимации эквалайзера).</summary>
    [ObservableProperty] public partial bool IsPlayingPure { get; private set; }

    /// <summary>Динамическая подсказка для кнопки-трансформера Play/Pause/Replace.</summary>
    public string PlayButtonTooltip
    {
        get
        {
            if (IsQueuePure)
                return IsPlayingPure ? (SL["Player_Pause"] ?? "Pause") : (SL["Player_Play"] ?? "Play");
            return SL["Playlist_PlayAll"] ?? "Play (replace queue)";
        }
    }

    #endregion

    #region Properties — Header UI

    public GridLength HeaderHeight
    {
        get => _headerHeight;
        set
        {
            if (!value.IsAbsolute) return;
            var clamped = new GridLength(Math.Clamp(value.Value, HeaderHeightMin, HeaderHeightMax));
            if (SetProperty(ref _headerHeight, clamped))
            {
                if (Math.Abs(LibService.Settings.PlaylistHeaderHeight - clamped.Value) > 1)
                    LibService.UpdateSettings(s => s.PlaylistHeaderHeight = clamped.Value);
            }
        }
    }

    private GridLength _headerHeight;
    private const double HeaderHeightMin = 215;
    private const double HeaderHeightMax = 270;

    #endregion

    #region Commands

    public IAsyncRelayCommand PlayAllCommand { get; }
    public IAsyncRelayCommand DeletePlaylistCommand { get; }
    public IAsyncRelayCommand UploadToCloudCommand { get; }
    public IAsyncRelayCommand UnlinkFromCloudCommand { get; }
    public IAsyncRelayCommand ShufflePlayCommand { get; }
    public IAsyncRelayCommand DownloadAllCommand { get; }
    public IAsyncRelayCommand MergePlaylistCommand { get; }
    public IAsyncRelayCommand RefreshPlaylistCommand { get; }
    public IRelayCommand AddToQueueCommand { get; }
    public IAsyncRelayCommand<(int oldIndex, int newIndex)> MoveItemCommand { get; }
    public IAsyncRelayCommand EditPlaylistCommand { get; }
    public IAsyncRelayCommand CopyPlaylistLinkCommand { get; }
    public IRelayCommand OpenAuthorCommand { get; }

    #endregion

    #region Constructor

    public PlaylistViewModel(
        AudioEngine audio,
        DownloadService downloads,
        DialogService dialog,
        TrackViewModelFactory vmFactory,
        DominantColorService dominantColor,
        MainWindowViewModel mainWindow,
        PlaylistSyncService syncService,
        PlaylistEditService editService,
        PlayerControlService playerControl,
        CookieAuthService auth)
        : base(audio, downloads, vmFactory)
    {
        _dialog = dialog;
        _dominantColor = dominantColor;
        _mainWindow = mainWindow;
        _syncService = syncService;
        _editService = editService;
        _playerControl = playerControl;
        _auth = auth;

        _languageChangedHandler = (_, _) =>
            OnPropertyChanged(nameof(FormattedTrackCount));
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;

        _headerHeight = new GridLength(Math.Clamp(
            LibService.Settings.PlaylistHeaderHeight,
            HeaderHeightMin, HeaderHeightMax));

        PlayAllCommand = new AsyncRelayCommand(PlayAllAsync, () => TrackCount > 0);

        DeletePlaylistCommand = new AsyncRelayCommand(async () =>
        {
            if (await _dialog.ConfirmAsync(
                SL["Dialog_Confirm_Title"],
                string.Format(SL["Playlist_DeleteConfirm"], PlaylistName)))
            {
                await _syncService.DeletePlaylistAsync(_currentPlaylistId, deleteFromCloud: true);
            }
        });

        UploadToCloudCommand = new AsyncRelayCommand(async () =>
        {
            await _syncService.UploadPlaylistToAccountAsync(_currentPlaylistId);
            await LoadPlaylistAsync(_currentPlaylistId);
        });

        UnlinkFromCloudCommand = new AsyncRelayCommand(async () =>
        {
            await _syncService.ConvertToLocalAsync(_currentPlaylistId);
            await LoadPlaylistAsync(_currentPlaylistId);
        });

        RefreshPlaylistCommand = new AsyncRelayCommand(
            RefreshPlaylistAsync,
            () => CanRefreshFromCloud && !IsSyncing);

        ShufflePlayCommand = new AsyncRelayCommand(ShufflePlayAsync, () => TrackCount > 0);
        DownloadAllCommand = new AsyncRelayCommand(DownloadAllAsync, () => TrackCount > 0);
        MergePlaylistCommand = new AsyncRelayCommand(MergePlaylistAsync, () => CanEdit);
        AddToQueueCommand = new RelayCommand(EnqueueUniquePlaylistTracks, () => TrackCount > 0);

        MoveItemCommand = new AsyncRelayCommand<(int oldIndex, int newIndex)>(async tuple =>
        {
            if (!CanReorderItems) return;
            _lastLocalMutationTime = DateTime.Now;
            InvalidateAllTracksCache();
            await MoveItemAsync(tuple.oldIndex, tuple.newIndex);
        });

        EditPlaylistCommand = new AsyncRelayCommand(EditPlaylistAsync, () => CanEdit);

        CopyPlaylistLinkCommand = new AsyncRelayCommand(
            CopyPlaylistLinkAsync,
            () => HasYoutubeLink);

        OpenAuthorCommand = new RelayCommand(() =>
        {
            var url = _currentPlaylist?.AuthorUrl;
            if (string.IsNullOrEmpty(url)) return;

            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
            }
            catch (Exception ex)
            {
                Log.Warn($"[Playlist] Failed to open author URL: {ex.Message}");
            }
        });

        LibService.OnDataChanged += OnLibraryDataChanged;

        _playerControl.PlaybackPurityChanged += OnPlaybackPurityChanged;
    }

    private void OnLibraryDataChanged()
    {
        if (_isSuspended) return;
        if ((DateTime.Now - _lastLocalMutationTime).TotalMilliseconds < LocalMutationDebounceMs)
        {
            Log.Debug("[Playlist] Ignoring OnDataChanged (recent local mutation)");
            return;
        }

        _dataChangedDebounceTimer?.Stop();
        _dataChangedDebounceTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(600),
            DispatcherPriority.Background,
            (_, _) =>
            {
                _dataChangedDebounceTimer?.Stop();
                if (_isSuspended) return;

                InvalidateAllTracksCache();
                if (string.IsNullOrEmpty(_currentPlaylistId)) return;

                _ = LoadPlaylistAsync(_currentPlaylistId, showLoader: false, CancellationToken.None);
            });
        _dataChangedDebounceTimer.Start();
    }

    #endregion

    #region ISmoothTransitionViewModel

    public override void PrepareForTransition()
    {
        base.PrepareForTransition();
        IsLoading = true;
    }

    #endregion

    #region Lifecycle

    protected override void OnSuspend() => _isSuspended = true;

    protected override void OnResume()
    {
        _isSuspended = false;
        InvalidateAllTracksCache();
        UpdatePlaybackState();
        OnPropertyChanged(nameof(FormattedTrackCount));
    }

    #endregion

    #region TrackListReorderableViewModel

    protected override TrackItemViewModel CreateViewModel(TrackInfo item)
    {
        var vm = base.CreateViewModel(item);

        vm.SourceContextId = _currentPlaylistId;
        vm.IsPlaylistContext = CanEdit;

        vm.RemoveFromPlaylistAction = async t =>
        {
            if (!CanEdit) return;

            _lastLocalMutationTime = DateTime.Now;
            InvalidateAllTracksCache();
            RemoveItemLocally(t.Id);

            TrackCount = Math.Max(0, TrackCount - 1);
            OnPropertyChanged(nameof(FormattedTrackCount));

            if (t.Duration > TimeSpan.Zero)
            {
                TotalDuration = TotalDuration > t.Duration
                    ? TotalDuration - t.Duration
                    : TimeSpan.Zero;
                FormatDuration();
            }

            await _syncService.RemoveTrackFromPlaylistAsync(_currentPlaylistId, t.Id);
        };

        vm.StartRadioAction = t => Log.Info($"[Playlist] Start radio requested for {t.Title}");
        return vm;
    }

    protected override async Task<List<TrackInfo>> LoadTracksAsync(
        IEnumerable<string> ids, CancellationToken ct) =>
        await LibService.GetPlaylistTracksAsync(_currentPlaylistId, ct);

    protected override async Task SaveMoveAsync(
        int fromMasterIndex, int toMasterIndex, CancellationToken ct)
    {
        Log.Info($"[Playlist] Saving move {fromMasterIndex}→{toMasterIndex}");
        await _syncService.MovePlaylistTrackAsync(_currentPlaylistId, fromMasterIndex, toMasterIndex, ct);
        Log.Info("[Playlist] Move saved");
    }

    protected override void OnPlay(TrackInfo track) =>
        _ = PlayFromPlaylistAsync(track);

    protected override void RebuildVisibleItems()
    {
        base.RebuildVisibleItems();
        CanReorderItems = CanEdit && CanReorder;
    }

    #endregion

    #region Public API

    /// <summary>
    /// Загружает плейлист с отображением loader/skeleton.
    /// </summary>
    public Task LoadPlaylistAsync(string playlistId) =>
        LoadPlaylistAsync(playlistId, showLoader: true, CancellationToken.None);

    #endregion

    #region Loading

    private CancellationTokenSource ReplacePlaylistLoadCts(CancellationToken externalCt)
    {
        _playlistLoadCts?.Cancel();
        _playlistLoadCts?.Dispose();
        _playlistLoadCts = externalCt.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(externalCt)
            : new CancellationTokenSource();
        return _playlistLoadCts;
    }

    private async Task LoadPlaylistAsync(string playlistId, bool showLoader, CancellationToken ct)
    {
        var loadCts = ReplacePlaylistLoadCts(ct);
        var loadCt = loadCts.Token;

        _currentPlaylistId = playlistId;
        InvalidateAllTracksCache();
        IsLoading = showLoader;

        try
        {
            loadCt.ThrowIfCancellationRequested();

            var payload = await Task.Run(async () =>
            {
                var playlist = await LibService.GetPlaylistAsync(playlistId).ConfigureAwait(false);
                if (playlist is null)
                    return null;

                loadCt.ThrowIfCancellationRequested();
                var trackIds = await LibService.GetPlaylistTrackIdsAsync(playlistId, loadCt).ConfigureAwait(false);

                loadCt.ThrowIfCancellationRequested();
                var totalDuration = await LibService.GetPlaylistTotalDurationAsync(playlistId, loadCt).ConfigureAwait(false);

                loadCt.ThrowIfCancellationRequested();
                var tracks = await LibService.GetPlaylistTracksAsync(playlistId, loadCt).ConfigureAwait(false);

                return new PlaylistLoadPayload(playlist, trackIds, totalDuration, tracks);
            }, loadCt).ConfigureAwait(false);

            if (payload is null || loadCt.IsCancellationRequested)
                return;

            await WaitForTransitionAsync(loadCt).ConfigureAwait(false);
            loadCt.ThrowIfCancellationRequested();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (loadCt.IsCancellationRequested)
                    return;

                ApplyLoadedPayload(payload);
                InitializeWithPreloadedData(payload.TrackIds, payload.Tracks);

                _ = LoadHeaderGradientAsync();
                _ = HydrateCacheStatusAsync(loadCt);

                UpdatePlaybackState();
            }, DispatcherPriority.Normal, loadCt);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error($"[Playlist] Error loading playlist '{playlistId}': {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_playlistLoadCts, loadCts))
                IsLoading = false;
        }
    }

    private void ApplyLoadedPayload(PlaylistLoadPayload payload)
    {
        var playlist = payload.Playlist;
        _currentPlaylist = playlist;

        PlaylistName = playlist.Name;
        ThumbnailUrl = playlist.ThumbnailUrl;
        Description = playlist.Description;
        IsLikedPlaylist = _currentPlaylistId == LibraryService.LikedPlaylistId;

        AuthorName = playlist.Author;
        ShowAuthor = !string.IsNullOrEmpty(playlist.Author);
        IsReadOnly = playlist.IsReadOnly;
        CanEdit = playlist.IsEditable;
        IsPrivate = playlist.Visibility == PlaylistVisibility.Private;
        IsUnlisted = playlist.Visibility == PlaylistVisibility.Unlisted;

        IsTwoWaySynced = playlist.SyncMode == PlaylistSyncMode.TwoWaySync;
        HasCloudSource = playlist.HasCloudLink || (IsLikedPlaylist && _auth.IsAuthenticated);
        CanRefreshFromCloud = IsTwoWaySynced || (IsLikedPlaylist && _auth.IsAuthenticated);
        HasStatusChips = IsReadOnly || IsPrivate || IsUnlisted || HasCloudSource;
        LastSyncedText = FormatRelativeTime(playlist.LastSyncedAtUtc);

        FormattedViewCount = FormatViewCount(playlist.ViewCount);
        FormattedReleaseDate = FormatReleaseDate(playlist.ReleaseDate);

        OnPropertyChanged(nameof(PlaylistYoutubeUrl));
        HasYoutubeLink = PlaylistYoutubeUrl is not null;
        CopyPlaylistLinkCommand.NotifyCanExecuteChanged();

        TrackCount = payload.TrackIds.Count;
        OnPropertyChanged(nameof(FormattedTrackCount));

        TotalDuration = payload.TotalDuration;
        FormatDuration();
    }

    private async Task LoadHeaderGradientAsync()
    {
        try
        {
            if (_currentPlaylist?.EffectiveColor is { } colorStr)
            {
                try
                {
                    var color = Color.Parse(colorStr);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        HeaderBackground = DominantColorService.CreateHeaderGradient(color));
                    return;
                }
                catch (FormatException)
                {
                    Log.Warn($"[Playlist] Invalid EffectiveColor: {colorStr}");
                }
            }

            if (string.IsNullOrEmpty(ThumbnailUrl)
                || !PlaylistEditorViewModel.IsValidUri(ThumbnailUrl))
            {
                await SetHeaderBackgroundAsync(null);
                return;
            }

            var dominantColor = await _dominantColor.GetDominantColorAsync(ThumbnailUrl);
            if (dominantColor is not null)
            {
                _ = SaveComputedColorAsync(dominantColor.Value);
                await Dispatcher.UIThread.InvokeAsync(() =>
                    HeaderBackground = DominantColorService.CreateHeaderGradient(dominantColor.Value));
            }
            else
            {
                await SetHeaderBackgroundAsync(null);
            }
        }
        catch (HttpRequestException ex)
        {
            Log.Warn($"[Playlist] Thumbnail load failed (network): {ex.Message}");
            await SetHeaderBackgroundAsync(null);
            await _dialog.ShowInfoAsync(
                SL["Dialog_Warning_Title"] ?? "Warning",
                string.Format(SL["Playlist_ThumbnailLoadFailed"] ?? "Could not load cover: {0}", ex.Message));
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            Log.Warn($"[Playlist] Invalid image: {ex.Message}");
            await SetHeaderBackgroundAsync(null);
            await _dialog.ShowInfoAsync(
                SL["Dialog_Warning_Title"] ?? "Warning",
                SL["Playlist_ThumbnailInvalidFormat"] ?? "Invalid cover format.");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Playlist] Gradient error: {ex.Message}");
            await SetHeaderBackgroundAsync(null);
        }
    }

    private async Task SaveComputedColorAsync(Color color)
    {
        if (_currentPlaylist is null) return;

        var colorHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        if (string.Equals(_currentPlaylist.ComputedColor, colorHex, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            _currentPlaylist.ComputedColor = colorHex;
            await LibService.AddOrUpdatePlaylistAsync(_currentPlaylist);
            Log.Debug($"[Playlist] ComputedColor saved: {colorHex}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Playlist] Failed to save ComputedColor: {ex.Message}");
        }
    }

    private async Task SetHeaderBackgroundAsync(IBrush? brush) =>
        await Dispatcher.UIThread.InvokeAsync(() => HeaderBackground = brush);

    #endregion

    #region Playback

    private async Task PlayAllAsync()
    {
        if (TrackCount == 0) return;

        if (IsQueuePure)
        {
            await _playerControl.PlayPauseAsync();
            return;
        }

        var allTracks = await GetAllTracksAsync();
        if (allTracks.Count == 0) return;

        IsShuffleActive = false;
        await _playerControl.PlayPlaylistAsync(_currentPlaylistId, allTracks, allTracks[0], enableShuffle: false);
    }

    private async Task ShufflePlayAsync()
    {
        if (TrackCount == 0) return;
        var allTracks = await GetAllTracksAsync();
        if (allTracks.Count == 0) return;

        await _playerControl.PlayPlaylistAsync(_currentPlaylistId, allTracks, enableShuffle: true);

        IsShuffleActive = true;
        _shuffleAnimationTimer?.Stop();
        _shuffleAnimationTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(800),
            DispatcherPriority.Normal,
            (_, _) =>
            {
                IsShuffleActive = false;
                _shuffleAnimationTimer?.Stop();
            });
        _shuffleAnimationTimer.Start();
    }

    private async Task PlayFromPlaylistAsync(TrackInfo track)
    {
        try
        {
            IsShuffleActive = false;
            var allTracks = await GetAllTracksAsync();
            await _playerControl.PlayPlaylistAsync(_currentPlaylistId, allTracks, track, enableShuffle: false);
            _ = LibService.AddToRecentlyPlayedAsync(track);
        }
        catch (Exception ex)
        {
            Log.Error($"[Playlist] PlayFromPlaylist error: {ex.Message}");
        }
    }

    private async Task DownloadAllAsync()
    {
        IsDownloadingActive = true;
        var allTracks = await GetAllTracksAsync();
        foreach (var track in allTracks.Where(static t => !t.IsDownloaded))
            Downloads.StartDownload(track);

        _downloadAnimationTimer?.Stop();
        _downloadAnimationTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(2),
            DispatcherPriority.Normal,
            (_, _) =>
            {
                IsDownloadingActive = false;
                _downloadAnimationTimer?.Stop();
            });
        _downloadAnimationTimer.Start();
    }

    private void EnqueueUniquePlaylistTracks()
    {
        var tracks = GetLoadedItemsSnapshot();
        if (tracks.Count == 0) return;
        Audio.EnqueuePlaylistWithNotification(tracks, PlaylistName);
    }

    private void OnPlaybackPurityChanged(string? activeId, bool isPure, bool isPlayingPure)
    {
        UpdatePlaybackState();
    }

    private void UpdatePlaybackState()
    {
        bool isThis = string.Equals(_playerControl.ActivePlaylistId, _currentPlaylistId, StringComparison.Ordinal);
        IsPlayingThisPlaylist = isThis;
        IsQueuePure = isThis && _playerControl.IsQueuePure;
        IsPlayingPure = isThis && _playerControl.IsPlayingPure;
        OnPropertyChanged(nameof(PlayButtonTooltip));
    }

    #endregion

    #region Sync & Edit

    private async Task RefreshPlaylistAsync()
    {
        if (!CanRefreshFromCloud) return;
        if (Interlocked.Exchange(ref _syncInProgressGate, 1) != 0)
        {
            Log.Debug("[Playlist] Sync ignored: already in progress");
            return;
        }

        IsSyncing = true;
        _mainWindow.LockNavigation(SL["Playlist_SyncInProgress"] ?? "Syncing...");

        try
        {
            var notifications = AppEntry.Services.GetRequiredService<NotificationService>();

            if (IsLikedPlaylist)
            {
                await _syncService.SyncLikedTracksAsync();
                InvalidateAllTracksCache();
                await LoadPlaylistAsync(_currentPlaylistId);

                await notifications.ShowToastAsync(
                    titleKey: "Playlist_SyncComplete_Toast_Title",
                    messageKey: "Sync_Success_Msg_LikedOnly",
                    severity: NotificationSeverity.Success,
                    durationMs: 4000);
                NotificationService.PlaySuccessSound();
            }
            else
            {
                var result = await _syncService.SyncWithDialogAsync(_currentPlaylistId);
                if (result is null) return;

                if (result.Success)
                {
                    InvalidateAllTracksCache();
                    await LoadPlaylistAsync(_currentPlaylistId);

                    if (result.TracksAddedLocally > 0 || result.TracksAddedToCloud > 0 ||
                        result.TracksRemovedLocally > 0 || result.TracksRemovedFromCloud > 0 ||
                        result.MetadataChanged)
                    {
                        await notifications.ShowToastAsync(
                            titleKey: "Playlist_SyncComplete_Toast_Title",
                            messageKey: "Playlist_SyncSuccess_Details",
                            messageArgs:
                            [
                                result.TracksAddedLocally, result.TracksAddedToCloud,
                                result.TracksRemovedLocally, result.TracksRemovedFromCloud
                            ],
                            severity: NotificationSeverity.Success,
                            durationMs: 4000);
                        NotificationService.PlaySuccessSound();
                    }
                }
                else
                {
                    await _dialog.ShowInfoAsync(
                        SL["Dialog_Error_Title"] ?? "Error",
                        result.ErrorMessage ?? SL["Playlist_SyncFailed"] ?? "Sync failed");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Playlist] Sync error: {ex.Message}");
            await _dialog.ShowInfoAsync(SL["Dialog_Error_Title"] ?? "Error", ex.Message);
        }
        finally
        {
            IsSyncing = false;
            _mainWindow.UnlockNavigation();
            Volatile.Write(ref _syncInProgressGate, 0);
        }
    }

    private async Task EditPlaylistAsync()
    {
        var result = await _editService.EditPlaylistAsync(
            _currentPlaylistId,
            _mainWindow.LockNavigation,
            _mainWindow.UnlockNavigation);

        if (result is { Changed: true })
            await LoadPlaylistAsync(_currentPlaylistId);
    }

    private async Task CopyPlaylistLinkAsync()
    {
        if (_currentPlaylist?.YoutubeId is not { Length: > 0 } ytId)
        {
            CopyHintService.Instance.Show(
                SL["Playlist_CopyLink_NotLinked"] ?? "Not linked to YouTube",
                CopyHintKind.Warning);
            return;
        }

        await Clipboard.SetTextAsync($"https://www.youtube.com/playlist?list={ytId}");
        CopyHintService.Instance.Show(SL["Playlist_LinkCopied"] ?? "Copied!", CopyHintKind.Success);
    }

    private async Task MergePlaylistAsync()
    {
        var otherPlaylists = (await LibService.GetAllPlaylistsAsync())
            .Where(p => p.Id != _currentPlaylistId && p.IsLocal)
            .ToList();

        if (otherPlaylists.Count == 0)
        {
            await _dialog.ShowInfoAsync(
                SL["Dialog_Merge_NoTarget_Title"],
                SL["Dialog_Merge_NoTarget_Msg"]);
            return;
        }

        var targetId = otherPlaylists.First().Id;
        if (!string.IsNullOrEmpty(targetId))
        {
            bool ok = await _syncService.MergePlaylistsAsync(_currentPlaylistId, targetId);
            await _dialog.ShowInfoAsync(
                ok ? SL["Dialog_Success"] : SL["Dialog_Error_Title"],
                ok ? SL["Merge_Success_Msg"] : SL["Merge_Error_Msg"]);
        }
    }

    #endregion

    #region Cache & Helpers

    private async Task<List<TrackInfo>> GetAllTracksAsync()
    {
        if (_allTracksCacheValid && _allTracksCache is not null)
            return _allTracksCache;

        _allTracksCache = await LibService.GetPlaylistTracksAsync(_currentPlaylistId);
        _allTracksCacheValid = true;
        return _allTracksCache;
    }

    private void InvalidateAllTracksCache()
    {
        _allTracksCacheValid = false;
        _allTracksCache = null;
    }

    #endregion

    #region Formatting

    /// <summary>
    /// Форматирует количество просмотров в компактный вид с правильным локализованным склонением.
    /// </summary>
    private static string? FormatViewCount(long? views)
    {
        if (views is null or <= 0) return null;

        long v = views.Value;

        if (v < 10_000)
        {
            return SL.GetPlural("Playlist_Views", (int)v);
        }

        string number = v switch
        {
            >= 1_000_000_000 => string.Create(CultureInfo.CurrentCulture, $"{v / 1_000_000_000.0:0.#}B"),
            >= 1_000_000 => string.Create(CultureInfo.CurrentCulture, $"{v / 1_000_000.0:0.#}M"),
            _ => string.Create(CultureInfo.CurrentCulture, $"{v / 1_000.0:0.#}K")
        };

        var pattern = SL.Get("Playlist_Views_other", "{0} views");
        return string.Format(CultureInfo.CurrentCulture, pattern, number);
    }

    /// <summary>
    /// Форматирует дату обновления плейлиста с учётом текущей локали.
    /// </summary>
    private static string? FormatReleaseDate(DateOnly? date)
    {
        if (date is null) return null;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        int daysDiff = today.DayNumber - date.Value.DayNumber;

        if (daysDiff == 0) return SL["Playlist_Updated_JustNow"] ?? "обновлено сегодня";
        if (daysDiff == 1) return SL["Playlist_Updated_Yesterday"] ?? "обновлено вчера";
        if (daysDiff is > 1 and < 7)
            return string.Format(SL["Playlist_Updated_DaysAgo"] ?? "обновлено {0} дн. назад", daysDiff);

        return date.Value.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }

    private void FormatDuration()
    {
        int totalHours = (int)TotalDuration.TotalHours;
        int minutes = TotalDuration.Minutes;
        int seconds = TotalDuration.Seconds;

        FormattedDuration = totalHours > 0
            ? $"{totalHours}{SL["Duration_Hours"]} {minutes}{SL["Duration_Minutes"]}"
            : minutes > 0
                ? $"{minutes}{SL["Duration_Minutes"]} {seconds}{SL["Duration_Seconds"]}"
                : $"{seconds}{SL["Duration_Seconds"]}";
    }

    /// <summary>
    /// Форматирует UTC-время в локализованную относительную строку.
    /// </summary>
    private static string? FormatRelativeTime(DateTime? utcTime)
    {
        if (utcTime is null) return null;

        var diff = DateTime.UtcNow - utcTime.Value;

        if (diff.TotalMinutes < 1) return SL["Playlist_Synced_JustNow"];
        if (diff.TotalHours < 1) return string.Format(SL["Playlist_Synced_MinutesAgo"], (int)diff.TotalMinutes);
        if (diff.TotalDays < 1) return string.Format(SL["Playlist_Synced_HoursAgo"], (int)diff.TotalHours);
        if (diff.TotalDays < 7) return string.Format(SL["Playlist_Synced_DaysAgo"], (int)diff.TotalDays);
        if (diff.TotalDays < 30) return string.Format(SL["Playlist_Synced_WeeksAgo"], (int)(diff.TotalDays / 7));
        return string.Format(SL["Playlist_Synced_MonthsAgo"], (int)(diff.TotalDays / 30));
    }

    #endregion

    #region Dispose

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Log.Debug($"[PlaylistVM] Disposing {_currentPlaylistId}");
            LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;

            LibService.OnDataChanged -= OnLibraryDataChanged;
            _dataChangedDebounceTimer?.Stop();
            _dataChangedDebounceTimer = null;

            _shuffleAnimationTimer?.Stop();
            _shuffleAnimationTimer = null;

            _downloadAnimationTimer?.Stop();
            _downloadAnimationTimer = null;

            _playerControl.PlaybackPurityChanged -= OnPlaybackPurityChanged;

            _playlistLoadCts?.Cancel();
            _playlistLoadCts?.Dispose();
            _playlistLoadCts = null;

            InvalidateAllTracksCache();
            _currentPlaylist = null;
        }
        base.Dispose(disposing);
    }

    #endregion

    private sealed record PlaylistLoadPayload(
        Core.Models.Playlist Playlist,
        List<string> TrackIds,
        TimeSpan TotalDuration,
        List<TrackInfo> Tracks);
}