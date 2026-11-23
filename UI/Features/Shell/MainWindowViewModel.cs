using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Threading;
using LMP.UI.Controls;
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
/// Main view model for the shell window.
/// Manages navigation tabs, global locks, window title, and child service integration.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IServiceProvider _services;
    private readonly LibraryService _library;
    private readonly Dictionary<string, ViewModelBase> _pageCache = new(StringComparer.Ordinal);

    // NAVIGATION
    [ObservableProperty] public partial ViewModelBase? CurrentPage { get; private set; }
    [ObservableProperty] public partial PlayerBarViewModel PlayerBar { get; private set; }
    [ObservableProperty] public partial string CurrentPageName { get; private set; } = "";
    [ObservableProperty] public partial Color? TitleBarAmbientColor { get; private set; }
    [ObservableProperty] public partial double TitleBarAmbientOpacity { get; private set; }

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

    /// <summary>
    /// Collection of main shell pages for full upfront visual tree materialization.
    /// </summary>
    public IReadOnlyList<ViewModelBase> PrewarmedPages { get; private set; } = [];

    private static readonly TimeSpan StartupAuthValidationTtl = TimeSpan.FromHours(4);

    private readonly CancellationTokenSource _lifetimeCts = new();
    private ViewModelBase? _pendingInitialPage;
    private string _pendingInitialPageName = string.Empty;
    private bool _isShellShown;
    private bool _disposed;
    private int _startupAuthValidationStarted;
    private readonly EventHandler<string> _languageChangedHandler;
    private readonly PropertyChangedEventHandler _dialogHostPropertyChangedHandler;

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
        _library = library;
        PlayerBar = playerBar;
        NotificationButton = notificationButton;
        NotificationPanel = notificationPanel;
        ToastOverlay = toastOverlay;
        DialogHost = dialogHost;

        _library.OnAccountHydrated += HandleGlobalAccountHydrated;
        AmbientBackdrop.GlobalConfigChanged += RefreshTitleBarAmbient;

        _languageChangedHandler = (_, _) => OnPropertyChanged(nameof(L));
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;

        _dialogHostPropertyChangedHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(DialogHostViewModel.HasActiveDialog))
            {
                NavigateCommand.NotifyCanExecuteChanged();
            }
        };
        DialogHost.PropertyChanged += _dialogHostPropertyChangedHandler;

        Navigate("Home");

        Log.Info("MainWindowViewModel initialized.");
    }

    /// <summary>
    /// Initializes all shell pages after the root singleton is fully ready in the DI container.
    /// Prevents constructor deadlock.
    /// </summary>
    public void InitializeShellPages()
    {
        var home = _pageCache.TryGetValue("Home", out var cachedHome)
            ? cachedHome
            : _services.GetRequiredService<HomeViewModel>();

        var search = _services.GetRequiredService<SearchViewModel>();
        var library = _services.GetRequiredService<LibraryViewModel>();
        var queue = _services.GetRequiredService<QueueViewModel>();
        AttachQueueViewModel(queue);
        var settings = _services.GetRequiredService<SettingsViewModel>();

        _pageCache["Home"] = home;
        _pageCache["Search"] = search;
        _pageCache["Library"] = library;
        _pageCache["Queue"] = queue;
        _pageCache["Settings"] = settings;

        PrewarmedPages = [home, search, library, queue, settings];
        OnPropertyChanged(nameof(PrewarmedPages));
    }

    /// <summary>
    /// Handles authorization changes at the global level after full cache hydration.
    /// </summary>
    private void HandleGlobalAccountHydrated()
    {
        Dispatcher.UIThread.Post(() => { BroadcastAccountChanged(); }, DispatcherPriority.Normal);
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
        try
        {
            await operation();
        }
        finally
        {
            UnlockNavigation();
        }
    }

    public async Task<T> WithNavigationLockAsync<T>(string reason, Func<Task<T>> operation)
    {
        LockNavigation(reason);
        try
        {
            return await operation();
        }
        finally
        {
            UnlockNavigation();
        }
    }

    /// <summary>
    /// Notifies the shell that the main window is actually shown.
    /// The first heavy page initialization starts only after the window is displayed.
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

        if (CurrentPage is IUnsavedChangesGuard guard && guard.HasUnsavedChanges)
        {
            _ = ConfirmAndNavigateAsync(guard, () => ExecuteNavigateCore(pageName));
            return;
        }

        ExecuteNavigateCore(pageName);
    }

    private async Task ConfirmAndNavigateAsync(IUnsavedChangesGuard guard, Action onConfirmed)
    {
        var dialogService = _services.GetRequiredService<DialogService>();
        if (await dialogService.ConfirmUnsavedChangesAsync(guard))
        {
            onConfirmed();
        }
    }

    private void SwitchToPage(ViewModelBase newPage, string pageName, bool startDeferredInit = true)
    {
        var oldPage = CurrentPage;
        var oldPageName = CurrentPageName;

        oldPage?.OnNavigatedFrom();

        CurrentPage = newPage;
        CurrentPageName = pageName;

        if (!startDeferredInit)
            return;

        bool isInitialNavigation = !_isShellShown && oldPage is null && string.IsNullOrEmpty(oldPageName);
        if (isInitialNavigation)
        {
            _pendingInitialPage = newPage;
            _pendingInitialPageName = pageName;
        }
        else
        {
            _ = DeferredInitAsync(newPage, pageName, isInitialNavigation: false);
        }
    }

    private void ExecuteNavigateCore(string pageName)
    {
        if (IsNavigationLocked || DialogHost.HasActiveDialog) return;

        var sw = Stopwatch.StartNew();
        Log.Info($"Switching to page: {pageName}");

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

            if (newPage is QueueViewModel queueVm)
            {
                AttachQueueViewModel(queueVm);
            }

            _pageCache[pageName] = newPage;
        }

        SwitchToPage(newPage, pageName);

        sw.Stop();
        Log.Info($"Page '{pageName}' ready in {sw.ElapsedMilliseconds}ms, executing init...");
    }

    private ViewModelBase? _observedAmbientPage;

    partial void OnCurrentPageNameChanged(string value)
    {
        var page = value switch
        {
            "Home" => DiscordNavigationPage.Home,
            "Search" => DiscordNavigationPage.Search,
            "Library" => DiscordNavigationPage.Library,
            "Queue" => DiscordNavigationPage.Queue,
            "Settings" => DiscordNavigationPage.Settings,
            "Playlist" => DiscordNavigationPage.Playlist,
            _ => DiscordNavigationPage.Unknown
        };

        _services.GetService<DiscordRpcService>()?.SetActivePage(page);
    }

    partial void OnCurrentPageChanged(ViewModelBase? value)
    {
        if (_observedAmbientPage != null)
        {
            _observedAmbientPage.PropertyChanged -= OnObservedAmbientPagePropertyChanged;
            _observedAmbientPage = null;
        }

        if (value is QueueViewModel or PlaylistViewModel)
        {
            _observedAmbientPage = value;
            _observedAmbientPage.PropertyChanged += OnObservedAmbientPagePropertyChanged;
        }

        RefreshTitleBarAmbient();
    }

    private void OnObservedAmbientPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(QueueViewModel.SourcePlaylistColor)
            or nameof(PlaylistViewModel.AmbientColor)
            or nameof(QueueViewModel.TopGlowAttenuation)
            or nameof(PlaylistViewModel.TopGlowAttenuation))
        {
            RefreshTitleBarAmbient();
        }
    }

    private void RefreshTitleBarAmbient()
    {
        Color? color = null;
        double topFactor = 1.0;

        if (AmbientBackdrop.CurrentMode != AmbientBackdropMode.Off)
        {
            switch (CurrentPage)
            {
                case QueueViewModel queue:
                    color = queue.SourcePlaylistColor;
                    topFactor = queue.TopGlowAttenuation;
                    break;
                case PlaylistViewModel playlist:
                    color = playlist.AmbientColor;
                    topFactor = playlist.TopGlowAttenuation;
                    break;
            }
        }

        TitleBarAmbientColor = color;
        TitleBarAmbientOpacity = color.HasValue ? Math.Clamp(topFactor, 0.0, 1.0) : 0.0;
    }

    private void AttachQueueViewModel(QueueViewModel queue)
    {
        queue.NavigateToPlaylistRequested -= NavigateToPlaylist;
        queue.NavigateToPlaylistRequested += NavigateToPlaylist;
    }

    public void NavigateToPlaylist(string playlistId)
    {
        if (IsNavigationLocked || DialogHost.HasActiveDialog) return;

        if (CurrentPage is IUnsavedChangesGuard guard && guard.HasUnsavedChanges)
        {
            _ = ConfirmAndNavigateAsync(guard, () => ExecuteNavigateToPlaylistCore(playlistId));
            return;
        }

        ExecuteNavigateToPlaylistCore(playlistId);
    }

    private void ExecuteNavigateToPlaylistCore(string playlistId)
    {
        if (IsNavigationLocked || DialogHost.HasActiveDialog) return;

        Log.Info($"Navigating to Playlist: {playlistId}");

        if (!_pageCache.TryGetValue("Playlist", out var playlistPage) ||
            playlistPage is not PlaylistViewModel playlistVM)
        {
            playlistVM = _services.GetRequiredService<PlaylistViewModel>();
            _pageCache["Playlist"] = playlistVM;
        }

        SwitchToPage(playlistVM, "Playlist", startDeferredInit: false);
        _ = LoadPlaylistSafeAsync(playlistVM, playlistId);
        _ = DeferredInitAsync(playlistVM, "Playlist", isInitialNavigation: false);
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
    /// Initializes the page without artificial timer delays.
    /// </summary>
    private async Task DeferredInitAsync(ViewModelBase page, string pageName, bool isInitialNavigation)
    {
        if (_disposed)
            return;

        CancellationToken ct;
        try
        {
            ct = _lifetimeCts.Token;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (isInitialNavigation)
            {
                await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            }

            if (ct.IsCancellationRequested || _disposed)
                return;

            if (CurrentPage != page)
            {
                Log.Debug($"[Navigation] Page '{pageName}' is no longer current, skipping deferred init");
                return;
            }

            var sw = Stopwatch.StartNew();
            await page.OnNavigatedToAsync();
            sw.Stop();

            Log.Info($"[Navigation] Init for '{pageName}' completed in {sw.ElapsedMilliseconds}ms");

            if (ct.IsCancellationRequested || _disposed)
                return;

            if (isInitialNavigation && Interlocked.Exchange(ref _startupAuthValidationStarted, 1) == 0)
            {
                _ = ValidateAuthOnStartupAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || _disposed)
        {
        }
        catch (Exception ex)
        {
            Log.Error($"[Navigation] Init failed for '{pageName}': {ex.Message}");
        }
    }

    private static void OpenGitHubExternal()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = G.GitHubUrl, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to open GitHub: {ex.Message}");
        }
    }

    /// <summary>
    /// Performs background session validation after the first application page is ready.
    /// The network request is skipped if the profile cache is still fresh.
    /// Displays an informative toast instead of failing silently on network errors.
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
        if (disposing && !_disposed)
        {
            _disposed = true;
            _library.OnAccountHydrated -= HandleGlobalAccountHydrated;
            AmbientBackdrop.GlobalConfigChanged -= RefreshTitleBarAmbient;
            LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;
            DialogHost.PropertyChanged -= _dialogHostPropertyChangedHandler;

            _observedAmbientPage?.PropertyChanged -= OnObservedAmbientPagePropertyChanged;
            _observedAmbientPage = null;

            if (_pageCache.TryGetValue("Queue", out var page) && page is QueueViewModel queueVm)
            {
                queueVm.NavigateToPlaylistRequested -= NavigateToPlaylist;
            }

            foreach (var cachedPage in _pageCache.Values)
            {
                cachedPage.Dispose();
            }
            _pageCache.Clear();

            PlayerBar.Dispose();

            _lifetimeCts.Cancel();
            _lifetimeCts.Dispose();
        }

        base.Dispose(disposing);
    }
}
