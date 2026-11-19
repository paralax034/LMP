using Avalonia.Media;
using Avalonia.Threading;

namespace LMP.UI.Features.Settings.ViewModels;

public sealed partial class GeneralSettingsViewModel : ViewModelBase
{
    private readonly LibraryService _library;
    private readonly SearchCacheService _searchCache;
    private readonly DialogService _dialog;
    private readonly UpdateService _updateService;
    private readonly DiscordRpcService _discordRpc;

    [Flags]
    private enum PendingGeneralChanges : uint
    {
        None = 0,
        UpdateInterval = 1 << 0,
        SearchCacheTtl = 1 << 1,
        MaxSuggestions = 1 << 2,
        DiscordClientId = 1 << 3,
        DiscordCustomBadge = 1 << 4
    }

    private DispatcherTimer? _debounceTimer;
    private DispatcherTimer? _previewTickTimer;
    private PendingGeneralChanges _pendingChanges;
    private bool _isLoading;
    private bool _isPreviewActive;
    private bool _isSuspended;

    private readonly record struct DiscordPresetSpec(
        DiscordRpcPausedBehavior PausedBehavior,
        DiscordRpcBadgeStyle BadgeStyle,
        bool ShowPlaylistName,
        bool ShowPublicPlaylistLink,
        bool ShowLikeStatus,
        bool ShowRepeatMode,
        bool ShowShuffleMode,
        bool ShowQueuePosition,
        bool ShowTrackButton,
        bool ShowGitHubButton,
        bool ShowIdleStatus)
    {
        public void ApplyTo(DiscordRpcSettings rpc)
        {
            rpc.ActivityType = DiscordRpcActivityType.Listening;
            rpc.StatusDisplay = DiscordRpcStatusDisplay.TrackTitle;
            rpc.DetailsFormat = DiscordRpcDetailsFormat.TrackTitle;
            rpc.TimestampMode = DiscordRpcTimestampMode.ProgressBar;
            rpc.StripAuthorFromTitle = true;
            rpc.ShowCoverArt = true;
            rpc.PausedBehavior = PausedBehavior;
            rpc.BadgeStyle = BadgeStyle;
            rpc.ShowPlaylistName = ShowPlaylistName;
            rpc.ShowPublicPlaylistLink = ShowPublicPlaylistLink;
            rpc.ShowLikeStatus = ShowLikeStatus;
            rpc.ShowRepeatMode = ShowRepeatMode;
            rpc.ShowShuffleMode = ShowShuffleMode;
            rpc.ShowQueuePosition = ShowQueuePosition;
            rpc.ShowTrackButton = ShowTrackButton;
            rpc.ShowGitHubButton = ShowGitHubButton;
            rpc.ShowIdleStatus = ShowIdleStatus;
        }

        public bool Matches(DiscordRpcSettings rpc) =>
            rpc.ActivityType == DiscordRpcActivityType.Listening
            && rpc.StatusDisplay == DiscordRpcStatusDisplay.TrackTitle
            && rpc.DetailsFormat == DiscordRpcDetailsFormat.TrackTitle
            && rpc.TimestampMode == DiscordRpcTimestampMode.ProgressBar
            && rpc.ShowCoverArt
            && rpc.StripAuthorFromTitle
            && rpc.PausedBehavior == PausedBehavior
            && rpc.BadgeStyle == BadgeStyle
            && rpc.ShowPlaylistName == ShowPlaylistName
            && rpc.ShowPublicPlaylistLink == ShowPublicPlaylistLink
            && rpc.ShowLikeStatus == ShowLikeStatus
            && rpc.ShowRepeatMode == ShowRepeatMode
            && rpc.ShowShuffleMode == ShowShuffleMode
            && rpc.ShowQueuePosition == ShowQueuePosition
            && rpc.ShowTrackButton == ShowTrackButton
            && rpc.ShowGitHubButton == ShowGitHubButton
            && rpc.ShowIdleStatus == ShowIdleStatus;
    }

    private static readonly DiscordPresetSpec MinimalPresetSpec = new(
        PausedBehavior: DiscordRpcPausedBehavior.ClearPresence,
        BadgeStyle: DiscordRpcBadgeStyle.None,
        ShowPlaylistName: false,
        ShowPublicPlaylistLink: false,
        ShowLikeStatus: false,
        ShowRepeatMode: false,
        ShowShuffleMode: false,
        ShowQueuePosition: false,
        ShowTrackButton: false,
        ShowGitHubButton: false,
        ShowIdleStatus: false);

    private static readonly DiscordPresetSpec BalancedPresetSpec = new(
        PausedBehavior: DiscordRpcPausedBehavior.ShowPaused,
        BadgeStyle: DiscordRpcBadgeStyle.PublicPlaylistCover,
        ShowPlaylistName: true,
        ShowPublicPlaylistLink: true,
        ShowLikeStatus: true,
        ShowRepeatMode: true,
        ShowShuffleMode: true,
        ShowQueuePosition: false,
        ShowTrackButton: true,
        ShowGitHubButton: false,
        ShowIdleStatus: false);

    private static readonly DiscordPresetSpec RichPresetSpec = new(
        PausedBehavior: DiscordRpcPausedBehavior.ShowPaused,
        BadgeStyle: DiscordRpcBadgeStyle.PublicPlaylistCover,
        ShowPlaylistName: true,
        ShowPublicPlaylistLink: true,
        ShowLikeStatus: true,
        ShowRepeatMode: true,
        ShowShuffleMode: true,
        ShowQueuePosition: true,
        ShowTrackButton: true,
        ShowGitHubButton: true,
        ShowIdleStatus: true);

    [ObservableProperty] public partial bool DiscordRpcEnabled { get; set; }

    // Discord RPC Style Presets
    [ObservableProperty] public partial bool IsDiscordPresetMinimal { get; private set; }
    [ObservableProperty] public partial bool IsDiscordPresetBalanced { get; private set; }
    [ObservableProperty] public partial bool IsDiscordPresetRich { get; private set; }

    // Discord RPC Live Preview Snapshot
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiscordPreviewHeaderText))]
    [NotifyPropertyChangedFor(nameof(DiscordPreviewElapsedLabel))]
    public partial DiscordRpcPreviewSnapshot DiscordPreview { get; private set; } = DiscordRpcPreviewSnapshot.Empty;

    public string DiscordPreviewHeaderText => DiscordPreview.ActivityType switch
    {
        DiscordRpcActivityType.Playing => SL["DiscordRpc_Preview_Playing"],
        DiscordRpcActivityType.Watching => SL["DiscordRpc_Preview_Watching"],
        _ => SL["DiscordRpc_Preview_ListeningTo"]
    };

    public string DiscordPreviewElapsedLabel => DiscordPreview.ShowElapsedOnly
        ? string.Format(SL["DiscordRpc_Preview_Elapsed"], DiscordPreview.ElapsedText)
        : string.Empty;

    // Discord RPC Detailed Settings
    [ObservableProperty]
    public partial IReadOnlyList<LocalizedItem<DiscordRpcActivityType>> DiscordActivityTypeOptions { get; private set; } = [];
    [ObservableProperty] public partial LocalizedItem<DiscordRpcActivityType>? SelectedDiscordActivityType { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<LocalizedItem<DiscordRpcStatusDisplay>> DiscordStatusDisplayOptions { get; private set; } = [];
    [ObservableProperty] public partial LocalizedItem<DiscordRpcStatusDisplay>? SelectedDiscordStatusDisplay { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<LocalizedItem<DiscordRpcTimestampMode>> DiscordTimestampModeOptions { get; private set; } = [];
    [ObservableProperty] public partial LocalizedItem<DiscordRpcTimestampMode>? SelectedDiscordTimestampMode { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<LocalizedItem<DiscordRpcDetailsFormat>> DiscordDetailsFormatOptions { get; private set; } = [];
    [ObservableProperty] public partial LocalizedItem<DiscordRpcDetailsFormat>? SelectedDiscordDetailsFormat { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<LocalizedItem<DiscordRpcPausedBehavior>> DiscordPausedBehaviorOptions { get; private set; } = [];
    [ObservableProperty] public partial LocalizedItem<DiscordRpcPausedBehavior>? SelectedDiscordPausedBehavior { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<LocalizedItem<DiscordRpcBadgeStyle>> DiscordBadgeStyleOptions { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDiscordCustomBadgeSelected))]
    public partial LocalizedItem<DiscordRpcBadgeStyle>? SelectedDiscordBadgeStyle { get; set; }

    public bool IsDiscordCustomBadgeSelected =>
        SelectedDiscordBadgeStyle?.Value == DiscordRpcBadgeStyle.Custom;

    [ObservableProperty] public partial bool DiscordShowCoverArt { get; set; }
    [ObservableProperty] public partial bool DiscordShowTrackButton { get; set; }
    [ObservableProperty] public partial bool DiscordShowGitHubButton { get; set; }
    [ObservableProperty] public partial bool DiscordShowPlaylistName { get; set; }
    [ObservableProperty] public partial bool DiscordShowPublicPlaylistLink { get; set; }
    [ObservableProperty] public partial bool DiscordShowLikeStatus { get; set; }
    [ObservableProperty] public partial bool DiscordShowRepeatMode { get; set; }
    [ObservableProperty] public partial bool DiscordShowShuffleMode { get; set; }
    [ObservableProperty] public partial bool DiscordShowQueuePosition { get; set; }
    [ObservableProperty] public partial bool DiscordShowListenerChannel { get; set; }
    [ObservableProperty] public partial bool DiscordShowIdleStatus { get; set; }
    [ObservableProperty] public partial bool DiscordStripAuthorFromTitle { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDiscordCustomBadgeUrlInvalid))]
    public partial string DiscordCustomBadgeUrl { get; set; } = string.Empty;

    public bool IsDiscordCustomBadgeUrlInvalid =>
        IsDiscordCustomBadgeSelected
        && !string.IsNullOrWhiteSpace(DiscordCustomBadgeUrl)
        && !DiscordRpcService.IsValidExternalImageUrl(DiscordCustomBadgeUrl);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDiscordCustomClientIdInvalid))]
    public partial string DiscordCustomClientId { get; set; } = string.Empty;

    [ObservableProperty] public partial string DiscordStatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiscordStatusColor))]
    [NotifyPropertyChangedFor(nameof(ShowDiscordStatusText))]
    public partial DiscordRpcConnectionState DiscordConnectionState { get; private set; } = DiscordRpcConnectionState.Disabled;

    /// <summary>
    /// Shows the inline status caption only when Discord is not connected (waiting, connecting, or error),
    /// keeping the header clean and minimal once live preview is active.
    /// </summary>
    public bool ShowDiscordStatusText =>
        DiscordRpcEnabled && DiscordConnectionState != DiscordRpcConnectionState.Connected;

    public bool IsDiscordCustomClientIdInvalid =>
        !string.IsNullOrWhiteSpace(DiscordCustomClientId)
        && !DiscordRpcService.IsValidClientId(DiscordCustomClientId);

    public Color DiscordStatusColor => DiscordConnectionState switch
    {
        DiscordRpcConnectionState.Connected => ThemeManagerService.GetThemeColor("Accent"),
        DiscordRpcConnectionState.Connecting => ThemeManagerService.GetThemeColor("SystemWarnOrange"),
        DiscordRpcConnectionState.WaitingForDiscord => ThemeManagerService.GetThemeColor("SystemWarnOrange"),
        DiscordRpcConnectionState.MissingClientId => ThemeManagerService.GetThemeColor("SystemWarnOrange"),
        DiscordRpcConnectionState.Error => ThemeManagerService.GetThemeColor("SystemError"),
        _ => ThemeManagerService.GetThemeColor("TextSecondary")
    };

    [ObservableProperty] public partial bool EnableSearchCache { get; set; }
    [ObservableProperty] public partial int SearchCacheTtlMinutes { get; set; }
    [ObservableProperty] public partial int MaxSuggestionsCount { get; set; }

    // Update State
    [ObservableProperty] public partial bool AutoCheckUpdates { get; set; }
    [ObservableProperty] public partial int UpdateCheckIntervalHours { get; set; }

    public string UpdateIntervalDisplayString => $"{UpdateCheckIntervalHours} {SL["Duration_Hours"]}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckForUpdates))]
    [NotifyPropertyChangedFor(nameof(IsUpdateActionIdle))]
    [NotifyPropertyChangedFor(nameof(CanDownloadUpdate))]
    [NotifyCanExecuteChangedFor(nameof(CheckUpdatesCommand))]
    public partial bool IsCheckingUpdates { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckForUpdates))]
    [NotifyPropertyChangedFor(nameof(IsUpdateActionIdle))]
    [NotifyPropertyChangedFor(nameof(CanDownloadUpdate))]
    [NotifyCanExecuteChangedFor(nameof(CheckUpdatesCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadUpdateCommand))]
    public partial bool IsDownloadingUpdate { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadUpdate))]
    [NotifyCanExecuteChangedFor(nameof(DownloadUpdateCommand))]
    public partial bool IsUpdateAvailable { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadUpdate))]
    [NotifyCanExecuteChangedFor(nameof(DownloadUpdateCommand))]
    public partial bool IsUpdateReadyToInstall { get; private set; }

    [ObservableProperty] public partial double DownloadProgress { get; private set; }
    [ObservableProperty] public partial string DownloadProgressText { get; private set; } = string.Empty;
    [ObservableProperty] public partial string UpdateStatusText { get; private set; } = string.Empty;

    public bool CanCheckForUpdates => !IsCheckingUpdates && !IsDownloadingUpdate;
    public bool IsUpdateActionIdle => !IsCheckingUpdates && !IsDownloadingUpdate;
    public bool CanDownloadUpdate => IsUpdateAvailable && !IsDownloadingUpdate && !IsUpdateReadyToInstall;

    // Telemetry & Build Info
    public bool IsDebug => G.Build.IsDebug;
    public string GitCommitCount => $"#{G.Build.CommitCount}";
    public string GitCommitHash => G.Build.GitHash;
    public string BuildDateString => G.Build.BuildDate.ToString("yyyy-MM-dd HH:mm");
    public string BuildProfileShort => G.Build.IsDebug ? "JIT" : "AOT";

    [ObservableProperty] public partial int RepoStars { get; private set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepoIssuesDisplayString))]
    public partial int RepoIssues { get; private set; }
    [ObservableProperty] public partial int RepoForks { get; private set; }
    [ObservableProperty] public partial string RepoBranch { get; private set; } = "main";
    [ObservableProperty] public partial bool HasRepoInfo { get; private set; }

    public string RepoIssuesDisplayString => string.Format(SL["Metadata_Issues_Format"], RepoIssues);
    public string CurrentVersionString => G.Build.FullVersionString;

    public IAsyncRelayCommand ResetLibraryCommand { get; }
    public IAsyncRelayCommand CheckUpdatesCommand { get; }
    public IAsyncRelayCommand DownloadUpdateCommand { get; }
    public IRelayCommand ApplyUpdateCommand { get; }
    public IRelayCommand OpenGitHubRepoCommand { get; }
    public IAsyncRelayCommand SimulateUpdateFlowCommand { get; }
    public IRelayCommand ReconnectDiscordRpcCommand { get; }
    public IRelayCommand ResetDiscordClientIdCommand { get; }
    public IRelayCommand ApplyDiscordMinimalPresetCommand { get; }
    public IRelayCommand ApplyDiscordBalancedPresetCommand { get; }
    public IRelayCommand ApplyDiscordRichPresetCommand { get; }

    public Action? OnLibraryReset { get; set; }

    public GeneralSettingsViewModel(
        LibraryService library,
        SearchCacheService searchCache,
        DialogService dialog,
        UpdateService updateService,
        DiscordRpcService discordRpc)
    {
        _library = library;
        _searchCache = searchCache;
        _dialog = dialog;
        _updateService = updateService;
        _discordRpc = discordRpc;

        ResetLibraryCommand = new AsyncRelayCommand(ResetLibraryAsync);
        CheckUpdatesCommand = new AsyncRelayCommand(CheckUpdatesAsync, () => CanCheckForUpdates);
        DownloadUpdateCommand = new AsyncRelayCommand(DownloadUpdateAsync, () => CanDownloadUpdate);
        ApplyUpdateCommand = new RelayCommand(ApplyUpdate);
        SimulateUpdateFlowCommand = new AsyncRelayCommand(SimulateUpdateFlowAsync);
        ReconnectDiscordRpcCommand = new RelayCommand(() => _discordRpc.Reconnect());
        ResetDiscordClientIdCommand = new RelayCommand(() => DiscordCustomClientId = string.Empty);
        ApplyDiscordMinimalPresetCommand = new RelayCommand(() => ApplyDiscordPreset(DiscordRpcPreset.Minimal));
        ApplyDiscordBalancedPresetCommand = new RelayCommand(() => ApplyDiscordPreset(DiscordRpcPreset.Balanced));
        ApplyDiscordRichPresetCommand = new RelayCommand(() => ApplyDiscordPreset(DiscordRpcPreset.Rich));
        OpenGitHubRepoCommand = new RelayCommand(() =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = G.GitHubUrl,
                    UseShellExecute = true
                });
            }
            catch { }
        });

        _updateService.StateChanged += OnUpdateServiceStateChanged;
        _discordRpc.StatusChanged += OnDiscordRpcStatusChanged;
        _discordRpc.PreviewChanged += OnDiscordRpcPreviewChanged;

        RefreshLists();
        LoadSettings();
        _ = LoadRepoInfoAsync();
    }

    /// <summary>
    /// Controls whether the live Discord Rich Presence preview and 1-second progress timer are active.
    /// Called by <see cref="SettingsViewModel"/> when entering or leaving the General settings tab.
    /// </summary>
    public void SetPreviewActive(bool active)
    {
        if (_isPreviewActive == active)
            return;

        _isPreviewActive = active;
        if (active)
        {
            SyncDiscordRpcStateFromService();
            RefreshDiscordPreview();
        }
        else
        {
            UpdatePreviewTimerState();
        }
    }

    protected override void OnSuspend()
    {
        _isSuspended = true;
        UpdatePreviewTimerState();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _isSuspended = false;
        if (_isPreviewActive)
            RefreshDiscordPreview();
        else
            UpdatePreviewTimerState();
    }

    private void OnUpdateServiceStateChanged()
    {
        Dispatcher.UIThread.Post(SyncUpdateStateFromService);
    }

    private void OnDiscordRpcStatusChanged()
    {
        if (!_isPreviewActive)
            return;

        Dispatcher.UIThread.Post(SyncDiscordRpcStateFromService);
    }

    private void OnDiscordRpcPreviewChanged()
    {
        if (!_isPreviewActive)
            return;

        if (Dispatcher.UIThread.CheckAccess())
            RefreshDiscordPreview();
        else
            Dispatcher.UIThread.Post(() =>
            {
                if (_isPreviewActive)
                    RefreshDiscordPreview();
            });
    }

    public void RefreshLists()
    {
        _isLoading = true;
        try
        {
            var rpc = _library.Settings.DiscordRpc ?? new DiscordRpcSettings();

            var activityType = SelectedDiscordActivityType?.Value ?? rpc.ActivityType;
            DiscordActivityTypeOptions = LocalizedItem.CreateList<DiscordRpcActivityType>("DiscordRpc_ActivityType_");
            SelectedDiscordActivityType = DiscordActivityTypeOptions.FindByValue(activityType, 0);

            var statusDisplay = SelectedDiscordStatusDisplay?.Value ?? rpc.StatusDisplay;
            DiscordStatusDisplayOptions = LocalizedItem.CreateList<DiscordRpcStatusDisplay>("DiscordRpc_StatusDisplay_");
            SelectedDiscordStatusDisplay = DiscordStatusDisplayOptions.FindByValue(statusDisplay, 2);

            var timestampMode = SelectedDiscordTimestampMode?.Value ?? rpc.TimestampMode;
            DiscordTimestampModeOptions = LocalizedItem.CreateList<DiscordRpcTimestampMode>("DiscordRpc_TimestampMode_");
            SelectedDiscordTimestampMode = DiscordTimestampModeOptions.FindByValue(timestampMode, 0);

            var detailsFormat = SelectedDiscordDetailsFormat?.Value ?? rpc.DetailsFormat;
            DiscordDetailsFormatOptions = LocalizedItem.CreateList<DiscordRpcDetailsFormat>("DiscordRpc_DetailsFormat_");
            SelectedDiscordDetailsFormat = DiscordDetailsFormatOptions.FindByValue(detailsFormat, 0);

            var pausedBehavior = SelectedDiscordPausedBehavior?.Value ?? rpc.PausedBehavior;
            DiscordPausedBehaviorOptions = LocalizedItem.CreateList<DiscordRpcPausedBehavior>("DiscordRpc_PausedBehavior_");
            SelectedDiscordPausedBehavior = DiscordPausedBehaviorOptions.FindByValue(pausedBehavior, 0);

            var badgeStyle = SelectedDiscordBadgeStyle?.Value ?? rpc.BadgeStyle;
            DiscordBadgeStyleOptions = LocalizedItem.CreateList<DiscordRpcBadgeStyle>("DiscordRpc_BadgeStyle_");
            SelectedDiscordBadgeStyle = DiscordBadgeStyleOptions.FindByValue(badgeStyle, 1);
        }
        finally
        {
            _isLoading = false;
        }

        OnPropertyChanged(nameof(UpdateIntervalDisplayString));
        OnPropertyChanged(nameof(RepoIssuesDisplayString));
        OnPropertyChanged(nameof(DiscordPreviewHeaderText));
        OnPropertyChanged(nameof(DiscordPreviewElapsedLabel));
        SyncUpdateStateFromService();
        SyncDiscordRpcStateFromService();
        if (_isPreviewActive)
            RefreshDiscordPreview();
    }

    public void LoadSettings()
    {
        _isLoading = true;
        try
        {
            var s = _library.Settings;
            LoadDiscordSettingsCore(s);

            EnableSearchCache = s.EnableSearchCache;
            SearchCacheTtlMinutes = s.SearchCacheTtlMinutes;
            MaxSuggestionsCount = s.MaxSuggestionsCount > 0 ? s.MaxSuggestionsCount : 8;

            AutoCheckUpdates = s.Updates.AutoCheckUpdates;
            UpdateCheckIntervalHours = s.Updates.UpdateCheckIntervalHours > 0 ? s.Updates.UpdateCheckIntervalHours : 2;

            OnPropertyChanged(nameof(UpdateIntervalDisplayString));
            SyncUpdateStateFromService();
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void LoadDiscordSettings(bool refreshPreview = true)
    {
        _isLoading = true;
        try
        {
            LoadDiscordSettingsCore(_library.Settings, refreshPreview);
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void LoadDiscordSettingsCore(AppSettings s, bool refreshPreview = true)
    {
        var rpc = s.DiscordRpc ?? new DiscordRpcSettings();

        DiscordRpcEnabled = s.DiscordRpcEnabled;
        SelectedDiscordActivityType = DiscordActivityTypeOptions.FindByValue(rpc.ActivityType, 0);
        SelectedDiscordStatusDisplay = DiscordStatusDisplayOptions.FindByValue(rpc.StatusDisplay, 2);
        SelectedDiscordTimestampMode = DiscordTimestampModeOptions.FindByValue(rpc.TimestampMode, 0);
        SelectedDiscordDetailsFormat = DiscordDetailsFormatOptions.FindByValue(rpc.DetailsFormat, 0);
        SelectedDiscordPausedBehavior = DiscordPausedBehaviorOptions.FindByValue(rpc.PausedBehavior, 0);
        SelectedDiscordBadgeStyle = DiscordBadgeStyleOptions.FindByValue(rpc.BadgeStyle, 1);
        DiscordShowCoverArt = rpc.ShowCoverArt;
        DiscordShowTrackButton = rpc.ShowTrackButton;
        DiscordShowGitHubButton = rpc.ShowGitHubButton;
        DiscordShowPlaylistName = rpc.ShowPlaylistName;
        DiscordShowPublicPlaylistLink = rpc.ShowPublicPlaylistLink;
        DiscordShowLikeStatus = rpc.ShowLikeStatus;
        DiscordShowRepeatMode = rpc.ShowRepeatMode;
        DiscordShowShuffleMode = rpc.ShowShuffleMode;
        DiscordShowQueuePosition = rpc.ShowQueuePosition;
        DiscordShowListenerChannel = rpc.ShowListenerChannel;
        DiscordShowIdleStatus = rpc.ShowIdleStatus;
        DiscordStripAuthorFromTitle = rpc.StripAuthorFromTitle;
        DiscordCustomBadgeUrl = rpc.CustomBadgeUrl;
        DiscordCustomClientId = rpc.CustomClientId;

        SyncDiscordRpcStateFromService();
        UpdateDiscordPresetFlags();
        if (refreshPreview && _isPreviewActive)
            RefreshDiscordPreview();
    }

    private static DiscordPresetSpec GetPresetSpec(DiscordRpcPreset preset) => preset switch
    {
        DiscordRpcPreset.Minimal => MinimalPresetSpec,
        DiscordRpcPreset.Rich => RichPresetSpec,
        _ => BalancedPresetSpec
    };

    private void ApplyDiscordPreset(DiscordRpcPreset preset)
    {
        var spec = GetPresetSpec(preset);
        _library.UpdateSettings(s => spec.ApplyTo(s.DiscordRpc));
        LoadDiscordSettings(refreshPreview: false);
        _discordRpc.ApplySettings();
        Log.Info($"[Settings] DiscordRpc preset applied -> {preset}");
    }

    private void UpdateDiscordPresetFlags()
    {
        var rpc = _library.Settings.DiscordRpc ?? new DiscordRpcSettings();
        IsDiscordPresetMinimal = MinimalPresetSpec.Matches(rpc);
        IsDiscordPresetBalanced = BalancedPresetSpec.Matches(rpc);
        IsDiscordPresetRich = RichPresetSpec.Matches(rpc);
    }

    private void RefreshDiscordPreview()
    {
        DiscordPreview = _discordRpc.GetLivePreview();
        UpdatePreviewTimerState();
    }

    private bool ShouldTickPreview =>
        _isPreviewActive
        && !_isSuspended
        && DiscordRpcEnabled
        && !DiscordPreview.IsDemoTrack
        && !DiscordPreview.IsPaused
        && (DiscordPreview.ShowProgressBar || DiscordPreview.ShowElapsedOnly);

    private void UpdatePreviewTimerState()
    {
        if (ShouldTickPreview)
        {
            _previewTickTimer ??= new DispatcherTimer(
                TimeSpan.FromSeconds(1),
                DispatcherPriority.Background,
                (_, _) =>
                {
                    if (ShouldTickPreview)
                        RefreshDiscordPreview();
                    else
                        _previewTickTimer?.Stop();
                });

            if (!_previewTickTimer.IsEnabled)
                _previewTickTimer.Start();
        }
        else
        {
            _previewTickTimer?.Stop();
        }
    }

    private void SyncUpdateStateFromService()
    {
        IsCheckingUpdates = _updateService.IsChecking;
        IsDownloadingUpdate = _updateService.IsDownloading;
        DownloadProgress = _updateService.DownloadProgress;
        IsUpdateReadyToInstall = _updateService.IsUpdateReadyToInstall;
        IsUpdateAvailable = _updateService.IsUpdateAvailable;

        if (IsDownloadingUpdate)
        {
            DownloadProgressText = string.Format(SL["Settings_DownloadingUpdate"], DownloadProgress);
            UpdateStatusText = DownloadProgressText;
        }
        else if (IsCheckingUpdates)
        {
            UpdateStatusText = SL["Settings_CheckingUpdates"];
        }
        else if (IsUpdateReadyToInstall)
        {
            UpdateStatusText = SL["Settings_UpdateReadyToInstall"];
        }
        else if (_updateService.LastCheckResult is { } res)
        {
            if (!res.IsSuccess)
            {
                UpdateStatusText = string.Format(SL["Settings_UpdateFailed"], res.ErrorMessage);
            }
            else if (res.HasUpdate)
            {
                UpdateStatusText = string.Format(SL["Settings_UpdateAvailable"], res.VersionName);
            }
            else
            {
                UpdateStatusText = SL["Settings_UpToDate"];
            }
        }
        else if (_library.Settings.Updates.LastUpdateCheckUtc.HasValue)
        {
            UpdateStatusText = SL["Settings_UpToDate"];
        }
        else
        {
            UpdateStatusText = string.Empty;
        }

        CheckUpdatesCommand.NotifyCanExecuteChanged();
        DownloadUpdateCommand.NotifyCanExecuteChanged();
    }

    private void SyncDiscordRpcStateFromService()
    {
        var state = _discordRpc.ConnectionState;
        DiscordConnectionState = state;
        DiscordStatusText = state switch
        {
            DiscordRpcConnectionState.Connected when !string.IsNullOrWhiteSpace(_discordRpc.ConnectedUserName) =>
                string.Format(SL["DiscordRpc_Status_ConnectedUser"], _discordRpc.ConnectedUserName),
            DiscordRpcConnectionState.Connected => SL["DiscordRpc_Status_Connected"],
            DiscordRpcConnectionState.Connecting => SL["DiscordRpc_Status_Connecting"],
            DiscordRpcConnectionState.WaitingForDiscord => SL["DiscordRpc_Status_Waiting"],
            DiscordRpcConnectionState.MissingClientId => SL["DiscordRpc_Status_MissingClientId"],
            DiscordRpcConnectionState.Error =>
                string.Format(SL["DiscordRpc_Status_Error"], _discordRpc.LastError ?? "unknown"),
            _ => SL["DiscordRpc_Status_Disabled"]
        };
        OnPropertyChanged(nameof(DiscordStatusColor));
    }

    private void ScheduleDebounce(PendingGeneralChanges flag)
    {
        _pendingChanges |= flag;
        _debounceTimer?.Stop();
        _debounceTimer ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(400),
            DispatcherPriority.Normal,
            (_, _) => FlushPendingChanges());
        _debounceTimer.Start();
    }

    private void FlushPendingChanges()
    {
        _debounceTimer?.Stop();
        var pending = _pendingChanges;
        if (pending == PendingGeneralChanges.None) return;
        _pendingChanges = PendingGeneralChanges.None;

        if ((pending & PendingGeneralChanges.UpdateInterval) != 0)
            Log.Info($"[Settings] UpdateCheckIntervalHours -> {UpdateCheckIntervalHours}h");

        if ((pending & PendingGeneralChanges.SearchCacheTtl) != 0)
        {
            Log.Info($"[Settings] SearchCacheTtlMinutes -> {SearchCacheTtlMinutes}m");
            _ = _searchCache.CleanupExpiredAsync();
        }

        if ((pending & PendingGeneralChanges.MaxSuggestions) != 0)
            Log.Info($"[Settings] MaxSuggestionsCount -> {MaxSuggestionsCount}");

        if ((pending & PendingGeneralChanges.DiscordClientId) != 0)
        {
            string trimmed = DiscordCustomClientId.Trim();
            if (string.IsNullOrEmpty(trimmed) || DiscordRpcService.IsValidClientId(trimmed))
            {
                _library.UpdateSettings(s => s.DiscordRpc.CustomClientId = trimmed);
                _discordRpc.ApplySettings();
                Log.Info($"[Settings] DiscordRpc.CustomClientId updated (custom={!string.IsNullOrEmpty(trimmed)})");
            }
        }

        if ((pending & PendingGeneralChanges.DiscordCustomBadge) != 0 && !IsDiscordCustomBadgeUrlInvalid)
        {
            string trimmed = DiscordCustomBadgeUrl.Trim();
            _library.UpdateSettings(s => s.DiscordRpc.CustomBadgeUrl = trimmed);
            _discordRpc.ApplySettings();
        }
    }

    private void ApplyDiscordSettingChange(Action<DiscordRpcSettings> mutate)
    {
        _library.UpdateSettings(s => mutate(s.DiscordRpc));
        UpdateDiscordPresetFlags();
        _discordRpc.ApplySettings();
    }

    partial void OnAutoCheckUpdatesChanged(bool value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.Updates.AutoCheckUpdates = value);
        Log.Info($"[Settings] AutoCheckUpdates -> {value}");
    }

    partial void OnUpdateCheckIntervalHoursChanged(int value)
    {
        if (_isLoading) return;
        OnPropertyChanged(nameof(UpdateIntervalDisplayString));
        _library.UpdateSettings(s => s.Updates.UpdateCheckIntervalHours = value);
        ScheduleDebounce(PendingGeneralChanges.UpdateInterval);
    }

    partial void OnDiscordRpcEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowDiscordStatusText));
        UpdatePreviewTimerState();
        if (_isLoading) return;
        _library.UpdateSettings(s => s.DiscordRpcEnabled = value);
        _discordRpc.ApplySettings();
        Log.Info($"[Settings] DiscordRpcEnabled -> {value}");
    }

    partial void OnSelectedDiscordActivityTypeChanged(LocalizedItem<DiscordRpcActivityType>? value)
    {
        if (_isLoading || value is null) return;
        ApplyDiscordSettingChange(r => r.ActivityType = value.Value);
    }

    partial void OnSelectedDiscordStatusDisplayChanged(LocalizedItem<DiscordRpcStatusDisplay>? value)
    {
        if (_isLoading || value is null) return;
        ApplyDiscordSettingChange(r => r.StatusDisplay = value.Value);
    }

    partial void OnSelectedDiscordTimestampModeChanged(LocalizedItem<DiscordRpcTimestampMode>? value)
    {
        if (_isLoading || value is null) return;
        ApplyDiscordSettingChange(r => r.TimestampMode = value.Value);
    }

    partial void OnSelectedDiscordDetailsFormatChanged(LocalizedItem<DiscordRpcDetailsFormat>? value)
    {
        if (_isLoading || value is null) return;
        ApplyDiscordSettingChange(r => r.DetailsFormat = value.Value);
    }

    partial void OnSelectedDiscordPausedBehaviorChanged(LocalizedItem<DiscordRpcPausedBehavior>? value)
    {
        if (_isLoading || value is null) return;
        ApplyDiscordSettingChange(r => r.PausedBehavior = value.Value);
    }

    partial void OnSelectedDiscordBadgeStyleChanged(LocalizedItem<DiscordRpcBadgeStyle>? value)
    {
        OnPropertyChanged(nameof(IsDiscordCustomBadgeUrlInvalid));
        if (_isLoading || value is null) return;
        ApplyDiscordSettingChange(r => r.BadgeStyle = value.Value);
    }

    partial void OnDiscordShowCoverArtChanged(bool value)
    {
        if (_isLoading) return;
        ApplyDiscordSettingChange(r => r.ShowCoverArt = value);
    }

    partial void OnDiscordShowTrackButtonChanged(bool value)
    {
        if (_isLoading) return;
        ApplyDiscordSettingChange(r => r.ShowTrackButton = value);
    }

    partial void OnDiscordShowGitHubButtonChanged(bool value)
    {
        if (_isLoading) return;
        ApplyDiscordSettingChange(r => r.ShowGitHubButton = value);
    }

    partial void OnDiscordShowPlaylistNameChanged(bool value)
    {
        if (_isLoading) return;
        ApplyDiscordSettingChange(r => r.ShowPlaylistName = value);
    }

    partial void OnDiscordShowPublicPlaylistLinkChanged(bool value)
    {
        if (_isLoading) return;
        ApplyDiscordSettingChange(r => r.ShowPublicPlaylistLink = value);
    }

    partial void OnDiscordShowLikeStatusChanged(bool value)
    {
        if (_isLoading) return;
        ApplyDiscordSettingChange(r => r.ShowLikeStatus = value);
    }

    partial void OnDiscordShowRepeatModeChanged(bool value)
    {
        if (_isLoading) return;
        ApplyDiscordSettingChange(r => r.ShowRepeatMode = value);
    }

    partial void OnDiscordShowShuffleModeChanged(bool value)
    {
        if (_isLoading) return;
        ApplyDiscordSettingChange(r => r.ShowShuffleMode = value);
    }

    partial void OnDiscordShowQueuePositionChanged(bool value)
    {
        if (_isLoading) return;
        ApplyDiscordSettingChange(r => r.ShowQueuePosition = value);
    }

    partial void OnDiscordShowListenerChannelChanged(bool value)
    {
        if (_isLoading) return;
        ApplyDiscordSettingChange(r => r.ShowListenerChannel = value);
    }

    partial void OnDiscordShowIdleStatusChanged(bool value)
    {
        if (_isLoading) return;
        ApplyDiscordSettingChange(r => r.ShowIdleStatus = value);
    }

    partial void OnDiscordStripAuthorFromTitleChanged(bool value)
    {
        if (_isLoading) return;
        ApplyDiscordSettingChange(r => r.StripAuthorFromTitle = value);
    }

    partial void OnDiscordCustomBadgeUrlChanged(string value)
    {
        if (_isLoading) return;
        ScheduleDebounce(PendingGeneralChanges.DiscordCustomBadge);
    }

    partial void OnDiscordCustomClientIdChanged(string value)
    {
        if (_isLoading) return;
        ScheduleDebounce(PendingGeneralChanges.DiscordClientId);
    }

    partial void OnEnableSearchCacheChanged(bool value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.EnableSearchCache = value);
        Log.Info($"[Settings] EnableSearchCache -> {value}");
    }

    partial void OnSearchCacheTtlMinutesChanged(int value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.SearchCacheTtlMinutes = value);
        ScheduleDebounce(PendingGeneralChanges.SearchCacheTtl);
    }

    partial void OnMaxSuggestionsCountChanged(int value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.MaxSuggestionsCount = value);
        ScheduleDebounce(PendingGeneralChanges.MaxSuggestions);
    }

    partial void OnDownloadProgressChanged(double value)
    {
        DownloadProgressText = string.Format(SL["Settings_DownloadingUpdate"], value);
    }

    private async Task LoadRepoInfoAsync()
    {
        if (HasRepoInfo) return;

        var info = await _updateService.GetRepositoryInfoAsync();
        if (info != null)
        {
            RepoStars = info.StargazersCount;
            RepoIssues = info.OpenIssuesCount;
            RepoForks = info.ForksCount;
            RepoBranch = info.DefaultBranch;
            HasRepoInfo = true;
        }
    }

    private async Task CheckUpdatesAsync()
    {
        if (!CanCheckForUpdates) return;

        IsCheckingUpdates = true;
        UpdateStatusText = SL["Settings_CheckingUpdates"];
        Log.Info("[GeneralSettings] User initiated manual update check.");

        try
        {
            var result = await _updateService.CheckForUpdatesAsync(manual: true);

            if (!result.IsSuccess)
            {
                UpdateStatusText = string.Format(SL["Settings_UpdateFailed"], result.ErrorMessage);
                IsUpdateAvailable = false;
                Log.Warn($"[GeneralSettings] Manual update check reported error: {result.ErrorMessage}");
            }
            else if (result.HasUpdate && result.Asset != null)
            {
                IsUpdateAvailable = !IsUpdateReadyToInstall;
                UpdateStatusText = string.Format(SL["Settings_UpdateAvailable"], result.VersionName);
                Log.Info($"[GeneralSettings] Update available: '{result.VersionName}'");
            }
            else
            {
                IsUpdateAvailable = false;
                if (!IsUpdateReadyToInstall)
                {
                    UpdateStatusText = SL["Settings_UpToDate"];
                }
                Log.Info("[GeneralSettings] Player is up-to-date.");
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText = string.Format(SL["Settings_UpdateFailed"], ex.Message);
            IsUpdateAvailable = false;
            Log.Error($"[GeneralSettings] CheckUpdatesAsync exception: {ex.Message}");
        }
        finally
        {
            IsCheckingUpdates = false;
            SyncUpdateStateFromService();
        }
    }

    private async Task DownloadUpdateAsync()
    {
        var asset = _updateService.PendingAsset;
        if (asset == null || !CanDownloadUpdate) return;

        IsDownloadingUpdate = true;
        DownloadProgress = 0;
        DownloadProgressText = string.Format(SL["Settings_DownloadingUpdate"], 0.0);
        Log.Info($"[GeneralSettings] Starting download of update asset: '{asset.Name}'");

        try
        {
            var progressReporter = new Progress<double>(value =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    DownloadProgress = value;
                    UpdateStatusText = string.Format(SL["Settings_DownloadingUpdate"], value);
                });
            });

            await _updateService.DownloadUpdateAsync(asset, progressReporter);

            IsUpdateAvailable = false;
            IsUpdateReadyToInstall = true;
            UpdateStatusText = SL["Settings_UpdateReadyToInstall"];
            Log.Info("[GeneralSettings] Update downloaded successfully. Prompting user to restart.");
        }
        catch (Exception ex)
        {
            UpdateStatusText = string.Format(SL["Settings_UpdateFailed"], ex.Message);
            Log.Error($"[GeneralSettings] DownloadUpdateAsync error: {ex.Message}");
        }
        finally
        {
            IsDownloadingUpdate = false;
            SyncUpdateStateFromService();
        }
    }

    private async Task SimulateUpdateFlowAsync()
    {
        if (IsDownloadingUpdate) return;

        Log.Info("[GeneralSettings] [DEBUG] Triggered update simulation flow.");
        IsCheckingUpdates = true;
        UpdateStatusText = SL["Settings_CheckingUpdates"];
        await Task.Delay(400);

        IsCheckingUpdates = false;
        IsUpdateAvailable = true;
        IsUpdateReadyToInstall = false;
        UpdateStatusText = SL["Settings_Update_SimulatedAvailable"];

        await Task.Delay(400);
        IsDownloadingUpdate = true;
        DownloadProgress = 0;
        DownloadProgressText = string.Format(SL["Settings_DownloadingUpdate"], 0.0);

        for (int i = 1; i <= 100; i += 4)
        {
            await Task.Delay(35);
            DownloadProgress = i;
            UpdateStatusText = string.Format(SL["Settings_DownloadingUpdate"], (double)i);
        }

        DownloadProgress = 100;
        IsDownloadingUpdate = false;
        IsUpdateAvailable = false;
        IsUpdateReadyToInstall = true;
        UpdateStatusText = SL["Settings_UpdateReadyToInstall"];
        Log.Info("[GeneralSettings] [DEBUG] Update simulation reached ReadyToInstall state.");
    }

    private void ApplyUpdate()
    {
        try
        {
            Log.Info("[GeneralSettings] User triggered ApplyUpdate command.");
            _updateService.ApplyUpdateAndRestart();
        }
        catch (Exception ex)
        {
            UpdateStatusText = string.Format(SL["Settings_UpdateFailed"], ex.Message);
            Log.Error($"[GeneralSettings] ApplyUpdate exception: {ex.Message}");
        }
    }

    /// <summary>
    /// Requests confirmation and fully resets the library and settings.
    /// A reset failure does not crash the UI: the error is logged and displayed to the user with a request to restart the application,
    /// as the in-memory state has already been cleared by that point.
    /// </summary>
    private async Task ResetLibraryAsync()
    {
        if (!await _dialog.ConfirmAsync(SL["Dialog_Warning_Title"], SL["Dialog_ResetMessage"])) return;

        try
        {
            await _library.ResetAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"[GeneralSettings] Library reset failed: {ex}");
            await _dialog.ShowInfoAsync(
                SL["Dialog_Error_Title"],
                string.Format(SL["Dialog_ResetFailed"], ex.Message));
            return;
        }

        OnLibraryReset?.Invoke();
        await _dialog.ShowInfoAsync(SL["Dialog_Done_Title"], SL["Dialog_ResetComplete"]);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _previewTickTimer?.Stop();
            _previewTickTimer = null;
            _updateService.StateChanged -= OnUpdateServiceStateChanged;
            _discordRpc.StatusChanged -= OnDiscordRpcStatusChanged;
            _discordRpc.PreviewChanged -= OnDiscordRpcPreviewChanged;
            FlushPendingChanges();
        }
        base.Dispose(disposing);
    }
}