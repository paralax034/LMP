using Avalonia.Threading;

namespace LMP.UI.Features.Settings.ViewModels;

public sealed partial class GeneralSettingsViewModel : ViewModelBase, IDisposable
{
    private readonly LibraryService _library;
    private readonly SearchCacheService _searchCache;
    private readonly DialogService _dialog;
    private readonly UpdateService _updateService;

    private DispatcherTimer? _suggestionsDebounceTimer;
    private bool _isLoading;
    private GitHubAssetDto? _pendingAsset;

    [ObservableProperty] public partial bool DiscordRpcEnabled { get; set; }
    [ObservableProperty] public partial bool EnableSearchCache { get; set; }
    [ObservableProperty] public partial int SearchCacheTtlMinutes { get; set; }
    [ObservableProperty] public partial int MaxSuggestionsCount { get; set; }

    // === Update State ===
    [ObservableProperty] public partial bool AutoCheckUpdates { get; set; }
    [ObservableProperty] public partial bool IncludePreReleases { get; set; }

    [ObservableProperty] public partial bool IsCheckingUpdates { get; private set; }
    [ObservableProperty] public partial bool IsDownloadingUpdate { get; private set; }
    [ObservableProperty] public partial bool IsUpdateAvailable { get; private set; }
    [ObservableProperty] public partial bool IsUpdateReadyToInstall { get; private set; }
    [ObservableProperty] public partial double DownloadProgress { get; private set; }
    [ObservableProperty] public partial string UpdateStatusText { get; private set; } = string.Empty;

    // === Telemetry & Build Info ===
    public bool IsDebug => G.Build.IsDebug;
    public string GitCommitCount => $"#{G.Build.CommitCount}";
    public string GitCommitHash => G.Build.GitHash;
    public string BuildDateString => G.Build.BuildDate.ToString("yyyy-MM-dd HH:mm");
    public string BuildProfileShort => G.Build.IsDebug ? "JIT" : "AOT";

    [ObservableProperty] public partial int RepoStars { get; private set; }
    [ObservableProperty] public partial int RepoIssues { get; private set; }
    [ObservableProperty] public partial int RepoForks { get; private set; }
    [ObservableProperty] public partial string RepoBranch { get; private set; } = "main";
    [ObservableProperty] public partial bool HasRepoInfo { get; private set; }

    public string CurrentVersionString => G.Build.FullVersionString;
    public bool CanDownloadUpdate => IsUpdateAvailable && !IsDownloadingUpdate && !IsUpdateReadyToInstall;

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
        CheckUpdatesCommand = new AsyncRelayCommand(CheckUpdatesAsync);
        DownloadUpdateCommand = new AsyncRelayCommand(DownloadUpdateAsync);
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

        LoadSettings();
        _ = LoadRepoInfoAsync();
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
            IncludePreReleases = s.Updates.IncludePreReleases;
        }
        finally
        {
            _isLoading = false;
        }
    }

    partial void OnAutoCheckUpdatesChanged(bool value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.Updates.AutoCheckUpdates = value);
    }

    partial void OnIncludePreReleasesChanged(bool value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.Updates.IncludePreReleases = value);
    }

    partial void OnDiscordRpcEnabledChanged(bool value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.DiscordRpcEnabled = value);
    }

    partial void OnEnableSearchCacheChanged(bool value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.EnableSearchCache = value);
    }

    partial void OnSearchCacheTtlMinutesChanged(int value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.SearchCacheTtlMinutes = value);
        _ = _searchCache.CleanupExpiredAsync();
    }

    partial void OnMaxSuggestionsCountChanged(int value)
    {
        if (_isLoading) return;
        _suggestionsDebounceTimer?.Stop();
        _suggestionsDebounceTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(400),
            DispatcherPriority.Normal,
            (_, _) =>
            {
                _suggestionsDebounceTimer?.Stop();
                _library.UpdateSettings(s => s.MaxSuggestionsCount = value);
            });
        _suggestionsDebounceTimer.Start();
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
        if (IsCheckingUpdates) return;

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
                _pendingAsset = result.Asset;
                IsUpdateAvailable = true;
                UpdateStatusText = string.Format(SL["Settings_UpdateAvailable"], result.VersionName);
                Log.Info($"[GeneralSettings] Update available: '{result.VersionName}' (Asset: '{result.Asset.Name}', Size: {result.Asset.Size} bytes)");
            }
            else
            {
                IsUpdateAvailable = false;
                UpdateStatusText = SL["Settings_UpToDate"];
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
            OnPropertyChanged(nameof(CanDownloadUpdate));
        }
    }

    private async Task DownloadUpdateAsync()
    {
        if (_pendingAsset == null || IsDownloadingUpdate) return;

        IsDownloadingUpdate = true;
        DownloadProgress = 0;
        OnPropertyChanged(nameof(CanDownloadUpdate));
        Log.Info($"[GeneralSettings] Starting download of update asset: '{_pendingAsset.Name}'");

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

            await _updateService.DownloadUpdateAsync(_pendingAsset, progressReporter);

            IsUpdateReadyToInstall = true;
            UpdateStatusText = SL["Settings_RestartToApply"];
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
            OnPropertyChanged(nameof(CanDownloadUpdate));
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
        UpdateStatusText = "Доступно тестовое обновление #999";
        OnPropertyChanged(nameof(CanDownloadUpdate));

        await Task.Delay(400);
        IsDownloadingUpdate = true;
        DownloadProgress = 0;
        OnPropertyChanged(nameof(CanDownloadUpdate));

        for (int i = 1; i <= 100; i += 4)
        {
            await Task.Delay(35);
            DownloadProgress = i;
            UpdateStatusText = string.Format(SL["Settings_DownloadingUpdate"], (double)i);
        }

        DownloadProgress = 100;
        IsDownloadingUpdate = false;
        IsUpdateReadyToInstall = true;
        UpdateStatusText = SL["Settings_RestartToApply"];
        OnPropertyChanged(nameof(CanDownloadUpdate));
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

    private async Task ResetLibraryAsync()
    {
        if (!await _dialog.ConfirmAsync(SL["Dialog_Warning_Title"], SL["Dialog_ResetMessage"])) return;
        await _library.ResetAsync();
        OnLibraryReset?.Invoke();
        await _dialog.ShowInfoAsync(SL["Dialog_Done_Title"], SL["Dialog_ResetComplete"]);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _suggestionsDebounceTimer?.Stop();
        }
        base.Dispose(disposing);
    }
}