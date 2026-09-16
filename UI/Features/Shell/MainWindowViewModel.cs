using System.Diagnostics;
using Avalonia.Threading;
using LMP.UI.Features.Home;
using LMP.UI.Features.Library;
using LMP.UI.Features.Notifications;
using LMP.UI.Features.Player;
using LMP.UI.Features.Playlist;
using LMP.UI.Features.Queue;
using LMP.UI.Features.Search;
using LMP.UI.Features.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.UI.Features.Shell;

/// <summary>
/// Главная модель представления окна оболочки (Shell).
/// Управляет вкладками навигации, глобальными блокировками, заголовком окна и интеграцией дочерних сервисов.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IServiceProvider _services;
    private readonly Dictionary<string, ViewModelBase> _pageCache = new(StringComparer.Ordinal);

    // VERSION INFO
    [ObservableProperty] public partial bool IsVersionInfoVisible { get; set; } = true;
    public static string VersionDisplay => G.Build.DisplayVersion;
    public static string GitHashDisplay => G.Build.GitHash;

    private string _commitsDisplay = "";
    public string CommitsDisplay
    {
        get => _commitsDisplay;
        private set => SetProperty(ref _commitsDisplay, value);
    }

    // NAVIGATION
    [ObservableProperty] public partial ViewModelBase? CurrentPage { get; private set; }
    [ObservableProperty] public partial PlayerBarViewModel PlayerBar { get; private set; }
    [ObservableProperty] public partial string CurrentPageName { get; private set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NavigateCommand))]
    public partial bool IsNavigationLocked { get; private set; }

    [ObservableProperty] public partial string NavigationLockReason { get; private set; } = "";

    // NOTIFICATIONS
    [ObservableProperty] public partial NotificationButtonViewModel NotificationButton { get; private set; }
    [ObservableProperty] public partial NotificationPanelViewModel NotificationPanel { get; private set; }
    [ObservableProperty] public partial ToastOverlayViewModel ToastOverlay { get; private set; }

    // DIALOG HOST
    [ObservableProperty] public partial DialogHostViewModel DialogHost { get; private set; }

    private const int DeferredLoadDelayMs = 140;
    private static readonly TimeSpan StartupAuthValidationTtl = TimeSpan.FromHours(4);

    private readonly CancellationTokenSource _lifetimeCts = new();
    private ViewModelBase? _pendingInitialPage;
    private string _pendingInitialPageName = string.Empty;
    private bool _isShellShown;
    private int _startupAuthValidationStarted;

    public MainWindowViewModel(
          IServiceProvider services,
          PlayerBarViewModel playerBar,
          NotificationButtonViewModel notificationButton,
          NotificationPanelViewModel notificationPanel,
          ToastOverlayViewModel toastOverlay,
          DialogHostViewModel dialogHost,
          LibraryService library)
    {
        Log.Info("MainWindowViewModel constructor started.");

        _services = services;
        PlayerBar = playerBar;
        NotificationButton = notificationButton;
        NotificationPanel = notificationPanel;
        ToastOverlay = toastOverlay;
        DialogHost = dialogHost;

        library.OnAccountHydrated += HandleGlobalAccountHydrated;

        UpdateCommitsDisplay();

        LocalizationService.Instance.LanguageChanged += (_, _) =>
        {
            UpdateCommitsDisplay();
            OnPropertyChanged(nameof(L));
        };

        DialogHost.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(DialogHostViewModel.HasActiveDialog))
            {
                NavigateCommand.NotifyCanExecuteChanged();
            }
        };

        Navigate("Home");

        Log.Info("MainWindowViewModel initialized.");
    }

    /// <summary>
    /// Обрабатывает изменения авторизации на глобальном уровне после полной гидрации кэшей.
    /// </summary>
    private void HandleGlobalAccountHydrated()
    {
        Dispatcher.UIThread.Post(() =>
        {
            ViewModelBase.BroadcastAccountChanged();
        }, DispatcherPriority.Normal);
    }

    private void UpdateCommitsDisplay()
    {
        var L = LocalizationService.Instance;
        CommitsDisplay = $"({string.Format(L["Build_CommitsCount"], G.Build.CommitCount)})";
    }

    public void LockNavigation(string reason)
    {
        IsNavigationLocked = true;
        NavigationLockReason = reason;
        Log.Info($"[Navigation] Locked: {reason}");
    }

    public void UnlockNavigation()
    {
        IsNavigationLocked = false;
        NavigationLockReason = "";
        Log.Info("[Navigation] Unlocked");
    }

    public async Task WithNavigationLockAsync(string reason, Func<Task> operation)
    {
        LockNavigation(reason);
        try { await operation(); }
        finally { UnlockNavigation(); }
    }

    public async Task<T> WithNavigationLockAsync<T>(string reason, Func<Task<T>> operation)
    {
        LockNavigation(reason);
        try { return await operation(); }
        finally { UnlockNavigation(); }
    }

    /// <summary>
    /// Уведомляет shell о фактическом показе главного окна.
    /// Первая тяжёлая инициализация страницы запускается только после отображения окна.
    /// </summary>
    public void NotifyWindowShown()
    {
        if (_isShellShown)
            return;

        _isShellShown = true;

        var page = _pendingInitialPage ?? CurrentPage;
        var pageName = _pendingInitialPageName.Length != 0
            ? _pendingInitialPageName
            : CurrentPageName;

        _pendingInitialPage = null;
        _pendingInitialPageName = string.Empty;

        if (page != null && !string.IsNullOrEmpty(pageName))
        {
            _ = DeferredInitAsync(page, pageName, isInitialNavigation: true);
        }
    }

    [RelayCommand]
    private void ToggleVersionInfo()
    {
        IsVersionInfoVisible = !IsVersionInfoVisible;
    }

    [RelayCommand]
    private void OpenGitHub()
    {
        OpenGitHubExternal();
    }

    private bool CanNavigate(string pageName) => !IsNavigationLocked && !DialogHost.HasActiveDialog;

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void Navigate(string pageName)
    {
        if (IsNavigationLocked || DialogHost.HasActiveDialog) return;

        if (CurrentPageName == pageName)
        {
            Log.Debug($"[Navigation] Already on '{pageName}', skipping");
            return;
        }

        var sw = Stopwatch.StartNew();
        Log.Info($"Switching to page: {pageName}");

        var oldPage = CurrentPage;
        var oldPageName = CurrentPageName;

        if (oldPage is ISmoothTransitionViewModel smoothOldPage)
        {
            smoothOldPage.PrepareForTransition();
        }

        if (!_pageCache.TryGetValue(pageName, out var newPage))
        {
            newPage = pageName switch
            {
                "Home" => _services.GetRequiredService<HomeViewModel>(),
                "Search" => _services.GetRequiredService<SearchViewModel>(),
                "Library" => _services.GetRequiredService<LibraryViewModel>(),
                "Settings" => _services.GetRequiredService<SettingsViewModel>(),
                "Queue" => _services.GetRequiredService<QueueViewModel>(),
                _ => null
            };

            if (newPage == null)
            {
                Log.Warn($"[Navigation] Unknown page: {pageName}");
                return;
            }

            _pageCache[pageName] = newPage;
        }

        if (newPage is ISmoothTransitionViewModel smoothNewPage)
        {
            smoothNewPage.PrepareForTransition();
        }

        CurrentPage = newPage;
        CurrentPageName = pageName;

        sw.Stop();
        Log.Info($"Page '{pageName}' ready in {sw.ElapsedMilliseconds}ms, scheduling deferred init...");

        bool isInitialNavigation = !_isShellShown && oldPage == null && string.IsNullOrEmpty(oldPageName);
        if (isInitialNavigation)
        {
            _pendingInitialPage = newPage;
            _pendingInitialPageName = pageName;
        }
        else
        {
            _ = DeferredInitAsync(newPage, pageName, isInitialNavigation: false);
        }

        if (oldPage is IDisposable disposable && !string.IsNullOrEmpty(oldPageName))
        {
            if (!_pageCache.ContainsKey(oldPageName))
            {
                _ = DisposePageDelayedAsync(disposable, oldPageName);
            }
        }
    }

    public void NavigateToPlaylist(string playlistId)
    {
        if (IsNavigationLocked || DialogHost.HasActiveDialog) return;

        Log.Info($"Navigating to Playlist: {playlistId}");

        var oldPage = CurrentPage;
        var oldPageName = CurrentPageName;

        if (oldPage is ISmoothTransitionViewModel smoothOldPage)
        {
            smoothOldPage.PrepareForTransition();
        }

        if (!_pageCache.TryGetValue("Playlist", out var playlistPage) || playlistPage is not PlaylistViewModel playlistVM)
        {
            playlistVM = _services.GetRequiredService<PlaylistViewModel>();
            _pageCache["Playlist"] = playlistVM;
        }

        if (playlistVM is ISmoothTransitionViewModel smoothPlaylistPage)
        {
            smoothPlaylistPage.PrepareForTransition();
        }

        CurrentPage = playlistVM;
        CurrentPageName = "Playlist";

        if (oldPage is IDisposable disposable && !string.IsNullOrEmpty(oldPageName))
        {
            if (!_pageCache.ContainsKey(oldPageName))
            {
                _ = DisposePageDelayedAsync(disposable, oldPageName);
            }
        }

        _ = DeferredInitAsync(playlistVM, "Playlist", isInitialNavigation: false);
        _ = LoadPlaylistSafeAsync(playlistVM, playlistId);
    }

    private static async Task LoadPlaylistSafeAsync(PlaylistViewModel vm, string playlistId)
    {
        try
        {
            await vm.LoadPlaylistAsync(playlistId);
        }
        catch (Exception ex)
        {
            Log.Error($"[Navigation] Playlist load failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Выполняет отложенную инициализацию страницы.
    /// Для первой страницы приложения стартует после фактического показа окна без искусственной паузы.
    /// </summary>
    private async Task DeferredInitAsync(ViewModelBase page, string pageName, bool isInitialNavigation)
    {
        try
        {
            if (isInitialNavigation)
            {
                await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            }
            else
            {
                await Task.Delay(DeferredLoadDelayMs, _lifetimeCts.Token);
            }

            if (_lifetimeCts.IsCancellationRequested)
                return;

            if (CurrentPage != page)
            {
                Log.Debug($"[Navigation] Page '{pageName}' is no longer current, skipping deferred init");
                return;
            }

            var sw = Stopwatch.StartNew();
            await page.OnNavigatedToAsync();
            sw.Stop();

            Log.Info($"[Navigation] Deferred init for '{pageName}' completed in {sw.ElapsedMilliseconds}ms");

            if (isInitialNavigation && Interlocked.Exchange(ref _startupAuthValidationStarted, 1) == 0)
            {
                _ = ValidateAuthOnStartupAsync(_lifetimeCts.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error($"[Navigation] Deferred init failed for '{pageName}': {ex.Message}");
        }
    }

    private async Task DisposePageDelayedAsync(IDisposable page, string pageName)
    {
        await Task.Delay(256, _lifetimeCts.Token);

        try
        {
            page.Dispose();
            Log.Debug($"[Navigation] Disposed old page: {pageName}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Navigation] Error disposing {pageName}: {ex.Message}");
        }

        _services.GetRequiredService<TrackRegistry>().CleanupDeadReferences();
    }

    private static void OpenGitHubExternal()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = G.GitHubUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to open GitHub: {ex.Message}");
        }
    }

    /// <summary>
    /// Выполняет фоновую валидацию сессии после готовности первой страницы приложения.
    /// Сетевой запрос пропускается, если кэш профиля ещё свежий.
    /// При сетевых ошибках показывает информативный toast вместо молчания.
    /// </summary>
    private async Task ValidateAuthOnStartupAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var auth = _services.GetRequiredService<CookieAuthService>();

            if (!auth.IsAuthenticated)
                return;

            if (auth.HasProfileLoadError)
            {
                Log.Warn("[Auth] Profile load error detected on startup");

                var notifications = _services.GetRequiredService<NotificationService>();
                await notifications.ShowToastAsync(
                    "Auth_ProfileLoadError_Title",
                    "Auth_ProfileLoadError_Message",
                    NotificationSeverity.Warning,
                    durationMs: 6000);

                return;
            }

            var lastUpdated = auth.State.LastUpdated;
            if (lastUpdated != DateTime.MinValue &&
                DateTime.UtcNow - lastUpdated < StartupAuthValidationTtl)
            {
                Log.Info("[Auth] Startup validation skipped. Cached profile is still fresh.");
                return;
            }

            var (isValid, error, isNetworkError) = await auth.ValidateSessionAsync(ct);

            if (ct.IsCancellationRequested)
                return;

            if (!isValid)
            {
                var notifications = _services.GetRequiredService<NotificationService>();

                if (isNetworkError)
                {
                    Log.Warn($"[Auth] Startup validation failed due to network: {error}");

                    await notifications.ShowToastAsync(
                        "Auth_ProfileLoadError_Title",
                        "Error_Network_ConnectionFailed",
                        NotificationSeverity.Warning,
                        durationMs: 6000,
                        recommendationKey: "Recommendation_CheckNetwork");
                }
                else
                {
                    Log.Warn($"[Auth] Session expired on startup: {error}");

                    await notifications.ShowToastAsync(
                        "Auth_SessionExpired_Title",
                        "Auth_SessionExpired_Message",
                        NotificationSeverity.Warning,
                        durationMs: 8000);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error($"[Auth] Startup validation error: {ex.Message}");
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lifetimeCts.Cancel();
            _lifetimeCts.Dispose();
        }

        base.Dispose(disposing);
    }
}