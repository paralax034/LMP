using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using LMP.UI.Features.Shell;
using AsyncImageLoader;
using LMP.Core.Audio.Cache;
using System.Diagnostics;
using LMP.Core.Audio.Http;

namespace LMP;

public partial class App : Application
{
    private SplashWindow? _splash;
    private readonly CancellationTokenSource _appLifetimeCts = new();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Log.Info("Framework initialization completed.");
        // The flag is raised from within the loop: synchronous theme, localization, and splash prevent false 400ms SPIKE pings
        Dispatcher.UIThread.Post(LMP.Core.Diagnostics.ProcessFlightRecorder.MarkUiDispatcherReady, DispatcherPriority.Background);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Theme
            var themeManager = AppEntry.Services.GetRequiredService<ThemeManagerService>();
            themeManager.LoadAndApplyThemeOnStartup();

            // Localization
            var bootstrap = AppEntry.Services.GetRequiredService<BootstrapSettings>();
            LocalizationService.Instance.Initialize(bootstrap.LanguageCode);
            LocalizationService.Instance.UpdateApplicationResources();
            Log.Info($"Localization: {bootstrap.LanguageCode}");

#if DEBUG
            if (AppEntry.IsTestUiMode)
            {
                Log.Info("[Startup] Launching directly into DebugWindow (--test-ui mode, zero background warmup).");
                var debugWindow = new UI.Features.Debug.DebugWindow(AppEntry.CommandLineArgs);
                desktop.MainWindow = debugWindow;
                debugWindow.Show();
                base.OnFrameworkInitializationCompleted();
                return;
            }
#endif

            if (!AppEntry.IsFastStartup)
            {
                // Splash Screen
                _splash = new SplashWindow();
                desktop.MainWindow = _splash;
                _splash.Show();
            }

            // Allow the UI thread to render the splash (or start initialization immediately in --fast mode)
            Dispatcher.UIThread.Post(() => { _ = InitializeAppAsync(desktop); }, DispatcherPriority.Background);
        }

        if (!OperatingSystem.IsWindows())
        {
            InitializeTrayIcon();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void InitializeTrayIcon()
    {
        var trayIcon = new TrayIcon
        {
            // In Avalonia 11+, icons are loaded via AssetLoader
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://LMP/Assets/app.ico"))),
            ToolTipText = LocalizationService.Instance["Common_AppName"],
            IsVisible = false
        };

        var trayIcons = new TrayIcons { trayIcon };
        TrayIcon.SetIcons(this, trayIcons);
    }

    private async Task InitializeAppAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var stopwatch = Stopwatch.StartNew();
        var L = LocalizationService.Instance;

        try
        {
            bool isFast = AppEntry.IsFastStartup;

            if (!isFast)
            {
                // Prioritize UI so the Splash renders instantly
                await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            }

            _splash?.SetProgress(5);
            _splash?.UpdateStatus(L["Splash_Initializing"]);

            // Auth init first: LibraryService.InitializeAsync depends on CurrentOwnerId
            var auth = AppEntry.Services.GetRequiredService<CookieAuthService>();
            await auth.InitializeAsync().ConfigureAwait(false);
            _splash?.SetProgress(10);

            AudioCacheManager? audioCacheManager = null;

            if (!isFast)
            {
                // Bind static SharedHttpClient to NetworkManager singleton
                var networkManager = AppEntry.Services.GetRequiredService<NetworkManager>();
                SharedHttpClient.Initialize(networkManager);

                // Audio Cache
                _splash?.UpdateStatus(L["Splash_InitAudioCache"]);
                var audioCacheTask = Task.Run(() =>
                    AppEntry.Services.GetRequiredService<AudioCacheManager>());

                var statsLoadTask = Task.Run(() =>
                {
                    CdnHostStatsStore.Load();
                    SessionCacheStore.Load();
                });

                await Task.WhenAll(audioCacheTask, statsLoadTask).ConfigureAwait(false);

                audioCacheManager = audioCacheTask.Result;
                AudioSourceFactory.InitializeGlobalCache(audioCacheManager);

                // Fire-and-forget: warm up top CDN clusters while UI continues initialization
                _ = CdnHostStatsStore.PreWarmTopClustersAsync(
                    networkManager.AudioClient,
                    _appLifetimeCts.Token);
            }
            else
            {
                _ = Task.Run(() =>
                {
                    CdnHostStatsStore.Load();
                    SessionCacheStore.Load();
                });
            }

            _splash?.SetProgress(20);

            _ = Task.Run(async () =>
            {
                try
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
                    while (await timer.WaitForNextTickAsync(_appLifetimeCts.Token).ConfigureAwait(false))
                    {
                        CdnHostStatsStore.Save();
                        SessionCacheStore.Save();
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Log.Warn($"[App] Periodic stats save failed: {ex.Message}");
                }
            });

            // Memory Monitor
            MemoryCleanupHelper.StartAutoCleanup();
            Log.Info("[Startup] Memory auto-cleanup scheduled");
            _splash?.SetProgress(25);

            // Library Service
            _splash?.UpdateStatus(L["Splash_LoadingLibrary"]);
            var library = AppEntry.Services.GetRequiredService<LibraryService>();
            await library.InitializeAsync().ConfigureAwait(false);
            _splash?.SetProgress(45);

            // Apply settings loaded from DB to the audio engine
            var audioEngine = AppEntry.Services.GetRequiredService<AudioEngine>();
            audioEngine.InitializeVolumeFromSettings();

            // LANGUAGE SYNCHRONIZATION
            var savedLang = library.Settings.LanguageCode;
            var currentLang = L.CurrentLanguageCode;

            if (!string.IsNullOrEmpty(savedLang) && savedLang != currentLang)
            {
                if (savedLang == "en" && currentLang != "en")
                {
                    Log.Info($"First run: saving auto-detected '{currentLang}' to DB (was default 'en')");
                    library.Settings.LanguageCode = currentLang;
                }
                else if (savedLang != "en")
                {
                    Log.Info($"Applying saved language from DB: {savedLang}");
                    L.CurrentLanguage = savedLang;
                }
            }

            _splash?.SetProgress(50);

            // CRITICAL: NotificationService after LibraryService
            var notifications = AppEntry.Services.GetRequiredService<NotificationService>();
            await notifications.InitializeAsync();
            Log.Info("[Startup] NotificationService history loaded");
            _splash?.SetProgress(55);

            // PlaybackErrorOrchestrator after NotificationService
            _ = AppEntry.Services.GetRequiredService<PlaybackErrorOrchestrator>();
            Log.Info("[Startup] PlaybackErrorOrchestrator ready");
            _splash?.SetProgress(60);

            // Image Cache
            _splash?.UpdateStatus(L["Splash_PreparingImages"]);
            var imageCache = AppEntry.Services.GetRequiredService<ImageCacheService>();

            var oldLoader = ImageLoader.AsyncImageLoader;
            ImageLoader.AsyncImageLoader = new CachedImageLoader(imageCache);
            if (oldLoader is IDisposable disposableLoader)
                disposableLoader.Dispose();

            _splash?.SetProgress(70);

            if (!isFast)
            {
                // YouTube Provider (in --fast mode initialized lazily only on the first actual request)
                _splash?.UpdateStatus(L["Splash_ConnectingYouTube"]);
                var youtube = AppEntry.Services.GetRequiredService<YoutubeProvider>();

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await youtube.InitializeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[YouTube] Background init failed: {ex.Message}");
                    }
                });

                _splash?.SetProgress(80);

                // Create Main Window
                _splash?.UpdateStatus(L["Splash_BuildingInterface"]);
                await Task.Delay(200); // No overload
                _splash?.SetProgress(85);
            }
            else
            {
                Log.Info("[Startup] Fast mode active: YouTube/PoToken warmup and page prewarm deferred until needed.");
            }

            MainWindow? mainWindow = null;
            MainWindowViewModel? mainWindowVM = null;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                mainWindowVM = AppEntry.Services.GetRequiredService<MainWindowViewModel>();
                if (!isFast)
                {
                    mainWindowVM.InitializeShellPages();
                }
                mainWindow = new MainWindow { DataContext = mainWindowVM };
            });
            _splash?.SetProgress(96);

            // Ready!
            _splash?.UpdateStatus(L["Splash_Ready"]);
            _splash?.SetProgress(100);

            // Switch Windows - Without artificial MinSplashTimeMs delay!
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                desktop.MainWindow = mainWindow;
                mainWindow?.Show();
                mainWindowVM?.NotifyWindowShown();
                _splash?.Close();
                _splash = null;

                // If migrated from an older version (v1/v2) to v3
                if (AppEntry.WasMigratedFromLegacy)
                {
                    try
                    {
                        var dialogService = AppEntry.Services.GetRequiredService<DialogService>();

                        // Send a warning Toast via the already declared notifications instance
                        await notifications.ShowToastAsync(
                            "Dialog_Warning_Title",
                            "Library_LegacyPlaylists_Message",
                            NotificationSeverity.Warning,
                            durationMs: 12000);

                        // Block the UI with a modal overlay
                        await dialogService.ShowLegacyMigrationAlertAsync(30);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[Startup] Failed to show legacy warning: {ex.Message}");
                    }
                }
            });

            Log.Info($"Main window ready. Total splash time: {stopwatch.ElapsedMilliseconds}ms");

            // Update check and notification
            if (BootstrapSettings.Current.AppUpdatedThisRun)
            {
                Log.Info(
                    $"[App] Detected successful version upgrade to v{G.Build.DisplayVersion}. Dispatching success toast.");
                _ = notifications.ShowToastAsync(
                    titleKey: "Dialog_Done_Title",
                    messageKey: "Update_InstalledToast_Message",
                    severity: NotificationSeverity.Success,
                    durationMs: 7000,
                    messageArgs: [G.Build.DisplayVersion]);
            }
            else if (!isFast && library.Settings.Updates.AutoCheckUpdates)
            {
                var updateService = AppEntry.Services.GetRequiredService<UpdateService>();

                // Background startup check without waiting for cooldown (15s timeout to prevent hanging)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(_appLifetimeCts.Token);
                        startupCts.CancelAfter(TimeSpan.FromSeconds(15));

                        var checkResult = await updateService
                            .CheckForUpdatesAsync(manual: false, ignoreCooldown: true, startupCts.Token)
                            .ConfigureAwait(false);
                        if (checkResult.HasUpdate)
                        {
                            Log.Info(
                                $"[App] Startup check detected new update '{checkResult.VersionName}'. Dispatching info toast.");
                            await notifications.ShowToastAsync(
                                titleKey: "Update_Available_Title",
                                messageKey: "Update_Available_Toast",
                                severity: NotificationSeverity.Info,
                                durationMs: 8000,
                                messageArgs: [checkResult.VersionName]).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        Log.Debug($"[App] Startup background update check ignored: {ex.Message}");
                    }
                });

                // Periodic background update check respecting the user interval
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var periodicTimer = new PeriodicTimer(TimeSpan.FromHours(1));
                        while (await periodicTimer.WaitForNextTickAsync(_appLifetimeCts.Token).ConfigureAwait(false))
                        {
                            if (!library.Settings.Updates.AutoCheckUpdates) continue;

                            var periodicResult = await updateService
                                .CheckForUpdatesAsync(manual: false, ignoreCooldown: false, _appLifetimeCts.Token)
                                .ConfigureAwait(false);
                            if (periodicResult.HasUpdate)
                            {
                                Log.Info(
                                    $"[App] Periodic background check detected new update '{periodicResult.VersionName}'. Dispatching info toast.");
                                await notifications.ShowToastAsync(
                                    titleKey: "Update_Available_Title",
                                    messageKey: "Update_Available_Toast",
                                    severity: NotificationSeverity.Info,
                                    durationMs: 8000,
                                    messageArgs: [periodicResult.VersionName]).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        Log.Debug($"[App] Periodic update watchdog loop error: {ex.Message}");
                    }
                });
            }

            // Post-Startup GC Compaction
            _ = Task.Run(async () =>
            {
                await Task.Delay(2500);
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                Log.Info("[Memory] Post-startup GC compaction complete. Cold memory reclaimed.");
            });

            // Shutdown Handler
            bool isCleaningUp = false;
            desktop.ShutdownRequested += (sender, e) =>
            {
                if (isCleaningUp) return;

                // Cancel immediate synchronous OS process termination
                e.Cancel = true;
                isCleaningUp = true;

                // Redirect cleanup execution to the UI thread queue tracking the Task (without async void)
                _ = Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    try
                    {
                        Log.Info("Starting graceful shutdown...");

                        // FIRST save statistics before releasing resources
                        CdnHostStatsStore.Save();
                        SessionCacheStore.Save();

                        // GRACEFULLY release the audio engine and cache.
                        // Do this BEFORE canceling the token so threads terminate normally without throwing IOException
                        await audioEngine.DisposeAsync().ConfigureAwait(false);
                        var cacheToDispose = audioCacheManager ?? AudioSourceFactory.GlobalCache;
                        if (cacheToDispose != null)
                        {
                            await cacheToDispose.DisposeAsync().ConfigureAwait(false);
                        }
                        await library.DisposeAsync().ConfigureAwait(false);
                        LocalAuthServer.DisposeIfCreated();
                        MemoryCleanupHelper.Dispose();

                        // Cancel the global token only when active network operations are gracefully completed
                        _appLifetimeCts.Cancel();
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Shutdown error during async cleanup: {ex.Message}");
                    }
                    finally
                    {
                        _appLifetimeCts.Dispose();
                        Log.Info("Shutdown complete. Exiting with code 0.");

                        // Guaranteed clean exit with code 0.
                        // Using desktop.Shutdown() after canceling e.Cancel often leads to exit code -1 in Avalonia.
                        Environment.Exit(0);
                    }
                }, DispatcherPriority.Normal);
            };

#if DEBUG
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                mainWindow?.KeyDown += (s, e) =>
                {
                    if (e.Key == Avalonia.Input.Key.F9)
                        new UI.Features.Debug.DebugWindow().Show();
                };
            });
#endif
        }
        catch (Exception ex)
        {
            Log.Fatal($"Initialization failed: {ex.Message}\n{ex.StackTrace}");

            await Dispatcher.UIThread.InvokeAsync(() => { _splash?.UpdateStatus(L["Splash_Error_Title"]); });

            OsNotificationHelper.ShowFatalError(
                "LMP Startup Error",
                ex.Message,
                ex.ToString());

            Environment.Exit(1);
        }
    }
}
