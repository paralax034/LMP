using Avalonia.Threading;

namespace LMP.UI.Features.Settings.ViewModels;

public sealed partial class GeneralSettingsViewModel : ViewModelBase
{
    private readonly LibraryService _library;
    private readonly SearchCacheService _searchCache;
    private readonly DialogService _dialog;
    private readonly UpdateService _updateService;

    [Flags]
    private enum PendingGeneralChanges : uint
    {
        None = 0,
        UpdateInterval = 1 << 0,
        SearchCacheTtl = 1 << 1,
        MaxSuggestions = 1 << 2
    }

    private DispatcherTimer? _debounceTimer;
    private PendingGeneralChanges _pendingChanges;
    private bool _isLoading;

    [ObservableProperty] public partial bool DiscordRpcEnabled { get; set; }
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

    public Action? OnLibraryReset { get; set; }

    public GeneralSettingsViewModel(
        LibraryService library,
        SearchCacheService searchCache,
        DialogService dialog,
        UpdateService updateService)
    {
        _library = library;
        _searchCache = searchCache;
        _dialog = dialog;
        _updateService = updateService;

        ResetLibraryCommand = new AsyncRelayCommand(ResetLibraryAsync);
        CheckUpdatesCommand = new AsyncRelayCommand(CheckUpdatesAsync, () => CanCheckForUpdates);
        DownloadUpdateCommand = new AsyncRelayCommand(DownloadUpdateAsync, () => CanDownloadUpdate);
        ApplyUpdateCommand = new RelayCommand(ApplyUpdate);
        SimulateUpdateFlowCommand = new AsyncRelayCommand(SimulateUpdateFlowAsync);
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

        LoadSettings();
        _ = LoadRepoInfoAsync();
    }

    private void OnUpdateServiceStateChanged()
    {
        Dispatcher.UIThread.Post(SyncUpdateStateFromService);
    }

    public void RefreshLists()
    {
        OnPropertyChanged(nameof(UpdateIntervalDisplayString));
        OnPropertyChanged(nameof(RepoIssuesDisplayString));
        SyncUpdateStateFromService();
    }

    public void LoadSettings()
    {
        _isLoading = true;
        try
        {
            var s = _library.Settings;
            DiscordRpcEnabled = s.DiscordRpcEnabled;
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
        if (_isLoading) return;
        _library.UpdateSettings(s => s.DiscordRpcEnabled = value);
        Log.Info($"[Settings] DiscordRpcEnabled -> {value}");
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
            _updateService.StateChanged -= OnUpdateServiceStateChanged;
            FlushPendingChanges();
        }
        base.Dispose(disposing);
    }
}