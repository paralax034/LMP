using System.Globalization;
using AsyncImageLoader;
using Avalonia;
using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Http;
using LMP.Core.Data;
using LMP.Core.Data.Repositories;
using LMP.Core.Diagnostics;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Core.Youtube.Bridge.NToken;
using LMP.Core.Youtube.Bridge.SigCipher;
using LMP.UI.Dialogs;
using LMP.UI.Features.Home;
using LMP.UI.Features.Library;
using LMP.UI.Features.Notifications;
using LMP.UI.Features.Player;
using LMP.UI.Features.Playlist;
using LMP.UI.Features.Queue;
using LMP.UI.Features.Search;
using LMP.UI.Features.Settings;
using LMP.UI.Features.Settings.ViewModels;
using LMP.UI.Features.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace LMP;

/// <summary>
/// Entry point for the Lite Music Player application.
/// </summary>
public sealed class AppEntry
{
#if DEBUG
    private static bool _disableAvaloniaLogging;
    private static Avalonia.Logging.LogEventLevel _avaloniaLogLevel = Avalonia.Logging.LogEventLevel.Debug;
#endif

    /// <summary>
    /// Global dependency injection service provider.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>
    /// Flag indicating whether an incremental schema migration from a version prior to v3 was performed at startup during this run.
    /// </summary>
    public static bool WasMigratedFromLegacy { get; private set; }

    /// <summary>
    /// Fast startup flag (<c>--fast</c>): skips Splash, YouTube/PoToken/CDN warmup, and inactive page preloading.
    /// All heavy services are initialized strictly on demand.
    /// </summary>
    public static bool IsFastStartup { get; private set; }

    /// <summary>
    /// Command-line arguments for the current process.
    /// </summary>
    public static string[] CommandLineArgs { get; private set; } = [];

#if DEBUG
    /// <summary>
    /// Flag to launch directly into the <see cref="UI.Features.Debug.DebugWindow"/> (<c>--test-ui</c> / <c>--debug-ui</c>)
    /// without initializing the main player window.
    /// </summary>
    public static bool IsTestUiMode { get; private set; }
#endif

    /// <summary>
    /// Main application entry point method.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    [STAThread]
    public static void Main(string[] args)
    {
        // 1. Initialize SQLite native bridge for Native AOT (critical before any database calls).
        // Logger is not initialized yet: the error is saved and rethrown as fatal immediately after Log.Initialize().
        Exception? sqliteInitError = null;
        try
        {
            SQLitePCL.Batteries_V2.Init();
        }
        catch (Exception ex)
        {
            sqliteInitError = ex;
        }

        // 2. Wait for parent process completion during updates and safely clean up .old files
        int waitPid = 0;
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--wait-pid=", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(arg.AsSpan(11), out var parsedPid) && parsedPid > 0)
                    waitPid = parsedPid;
            }
        }

        if (waitPid > 0)
        {
            Log.Info(
                $"[AppEntry] Launched after in-place update. Waiting for parent process {waitPid} to terminate...");
            try
            {
                using var parentProcess = System.Diagnostics.Process.GetProcessById(waitPid);
                if (parentProcess.WaitForExit(7000))
                    Log.Info($"[AppEntry] Parent process {waitPid} successfully exited.");
                else
                    Log.Warn($"[AppEntry] Parent process {waitPid} did not exit within timeout. Proceeding...");
            }
            catch (Exception ex)
            {
                Log.Debug($"[AppEntry] Parent process {waitPid} inspection exception: {ex.Message}");
            }
        }

        UpdateService.CleanupPendingOldFiles();

#if DEBUG
        UpdateService.DebugSimulatedCommitCount = Tests.Framework.TestConfig.Get().General.SimulateOldCommitCount;
#endif

        CommandLineArgs = args;

        bool isHeadlessTest = false;
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--fast", StringComparison.OrdinalIgnoreCase))
            {
                IsFastStartup = true;
            }
#if DEBUG
            else if (string.Equals(arg, "--test-ui", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "--debug-ui", StringComparison.OrdinalIgnoreCase))
            {
                IsTestUiMode = true;
                IsFastStartup = true;
            }
#endif
        }

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--headless", StringComparison.OrdinalIgnoreCase)
#if DEBUG
                || (!IsTestUiMode && Tests.ManualTests.IsTestCliArgument(arg))
#endif
               )
            {
                isHeadlessTest = true;
                break;
            }
        }

        // 2. Single instance protection: holds a mutex for the entire process lifetime (does not block the main instance in headless tests and --test-ui)
        bool bypassSingleInstance = isHeadlessTest
#if DEBUG
            || IsTestUiMode
#endif
            ;
        using var instanceGuard = bypassSingleInstance ? null : SingleInstanceGuard.TryAcquire();
        if (!bypassSingleInstance && instanceGuard is null)
        {
            Console.WriteLine("[AppEntry] Another instance of LMP is already running. Exiting headless probe.");
            return;
        }

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

#if DEBUG
        bool enableTelemetry = !isHeadlessTest && !IsFastStartup;
#else
        bool enableTelemetry = false;
#endif

        // Low-allocation, fast command-line argument parsing without LINQ
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.IsNullOrEmpty(arg)) continue;

            if (string.Equals(arg, "--telemetry", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "--flight-recorder", StringComparison.OrdinalIgnoreCase))
            {
                enableTelemetry = true;
            }
            else if (string.Equals(arg, "--no-telemetry", StringComparison.OrdinalIgnoreCase))
            {
                enableTelemetry = false;
            }
#if DEBUG
            else if (string.Equals(arg, "--no-avalonia-logs", StringComparison.OrdinalIgnoreCase))
            {
                _disableAvaloniaLogging = true;
            }
            else if (arg.StartsWith("--avalonia-log-level=", StringComparison.OrdinalIgnoreCase))
            {
                var levelStr = arg.AsSpan(21);
                if (levelStr.Equals("Verbose".AsSpan(), StringComparison.OrdinalIgnoreCase))
                    _avaloniaLogLevel = Avalonia.Logging.LogEventLevel.Verbose;
                else if (levelStr.Equals("Debug".AsSpan(), StringComparison.OrdinalIgnoreCase))
                    _avaloniaLogLevel = Avalonia.Logging.LogEventLevel.Debug;
                else if (levelStr.Equals("Information".AsSpan(), StringComparison.OrdinalIgnoreCase))
                    _avaloniaLogLevel = Avalonia.Logging.LogEventLevel.Information;
                else if (levelStr.Equals("Warning".AsSpan(), StringComparison.OrdinalIgnoreCase))
                    _avaloniaLogLevel = Avalonia.Logging.LogEventLevel.Warning;
                else if (levelStr.Equals("Error".AsSpan(), StringComparison.OrdinalIgnoreCase))
                    _avaloniaLogLevel = Avalonia.Logging.LogEventLevel.Error;
                else if (levelStr.Equals("Fatal".AsSpan(), StringComparison.OrdinalIgnoreCase))
                    _avaloniaLogLevel = Avalonia.Logging.LogEventLevel.Fatal;
            }
#endif
        }

        SetupGlobalExceptionHandlers();

        try
        {
            G.Folder.Create();

            Log.Initialize();
            Log.Info($"{G.AppId} starting...");

            // Without the native bridge, any database operation is impossible: startup is aborted via the common fatal handler below.
            if (sqliteInitError is not null)
            {
                throw new InvalidOperationException(
                    "SQLite native bridge (SQLitePCL.Batteries_V2) failed to initialize.", sqliteInitError);
            }

            if (enableTelemetry)
            {
                ProcessFlightRecorder.SetEnabled(true, G.Folder.Logs);
            }

            BootstrapSettings.Initialize();

            var services = new ServiceCollection();
#if DEBUG
            if (!IsTestUiMode && Tests.ManualTests.HasTestCliArguments(args))
            {
                var testDbFolder = G.FilePath.UseIsolatedTestDatabase();
                Log.Info($"[Tests] Isolated test database: {testDbFolder}");
            }
#endif
            ConfigureServices(services);
            Services = services.BuildServiceProvider();

            AudioSourceFactory.RegisterLazyFactory(() => Services.GetRequiredService<AudioCacheManager>());
            SharedHttpClient.RegisterLazyFactory(() => Services.GetRequiredService<NetworkManager>());

            LifecycleRegistry.Instance = Services.GetRequiredService<LifecycleRegistry>();
            LifecycleRegistry.ActiveUiPageResolver = () =>
                Services.GetService<MainWindowViewModel>()?.CurrentPage;

#if DEBUG
            if (!IsTestUiMode && Tests.ManualTests.HasTestCliArguments(args))
            {
                MigrateDatabaseSync(fastMode: true);
                Environment.ExitCode = Tests.ManualTests.RunCliAsync(args).GetAwaiter().GetResult();
                return;
            }
#endif

            // Safe initialization and automatic database migration
            MigrateDatabaseSync(fastMode: IsFastStartup);

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // Non-zero return code is mandatory: otherwise headless execution (verify, CI) treats a fatal crash as success.
            Environment.ExitCode = 1;
            Log.Fatal($"Global crash: {ex}");
            if (!isHeadlessTest)
            {
                OsNotificationHelper.ShowFatalError("LMP Fatal Startup Error", ex.Message, ex.ToString());
            }
            else
            {
                Console.Error.WriteLine($"[LMP Fatal Error] {ex}");
            }
        }
        finally
        {
            ProcessFlightRecorder.ShutdownGlobal();
#if DEBUG
            try
            {
                G.FilePath.CleanupIsolatedTestDatabase();
            }
            catch (Exception ex)
            {
                Log.Warn($"[Tests] Failed to remove isolated test database: {ex.Message}");
            }
#endif
            Log.Shutdown();
        }
    }

    /// <summary>
    /// Configures the Avalonia application builder configuration.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
    {
        BootstrapSettings.Initialize();
        var gpuCacheBytes = BootstrapSettings.Current.GpuTextureCacheMb * 1024L * 1024L;

        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new SkiaOptions { MaxGpuResourceSizeBytes = gpuCacheBytes });

        if (OperatingSystem.IsWindows() && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            Log.Info(
                "[AppEntry] Windows 10 detected. Using RedirectionSurface to prevent dcomp.dll compositor crashes.");

            builder.With(new Win32PlatformOptions
            {
                CompositionMode =
                [
                    Win32CompositionMode.RedirectionSurface
                ]
            });
        }

#if DEBUG
        builder.AfterSetup(_ =>
        {
            if (!_disableAvaloniaLogging)
            {
                Avalonia.Logging.Logger.Sink = new AvaloniaCustomLogSink(_avaloniaLogLevel);
            }
            else
            {
                Log.Info("[AppEntry] Avalonia diagnostic logs are completely disabled.");
                Avalonia.Logging.Logger.Sink = null;
            }
        });
#endif

        return builder;
    }

    private const int MigrationMaxAttempts = 3;
    private const int MigrationRetryDelayMs = 250;

    /// <summary>
    /// Validates and updates the database schema. Recreates the database on corruption (SQLITE_CORRUPT, SQLITE_NOTADB),
    /// retries on lock (SQLITE_BUSY, SQLITE_LOCKED), and treats any other error as fatal,
    /// leaving the file untouched.
    /// </summary>
    private static void MigrateDatabaseSync(bool fastMode = false)
    {
        var dbPath = G.FilePath.Database;
        if (!File.Exists(dbPath))
        {
            CreateFreshDatabase();
            return;
        }

        if (!fastMode)
        {
            // Safety backup of the database before validating and updating the schema
            try
            {
                var bakPath = dbPath + ".bak";
                DatabaseExtensions.CreateConsistentBackup(dbPath, bakPath);
                Log.Info($"[DB] Safety backup created: {bakPath}");
            }
            catch (Exception ex)
            {
                Log.Warn($"[DB] Failed to create safety database backup: {ex.Message}");
            }
        }

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                MigrateExistingDatabase(fastMode);
                return;
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.IsTransientLockError() && attempt < MigrationMaxAttempts)
            {
                Log.Warn($"[DB] Database is locked (SQLite {ex.SqliteErrorCode}), retrying migration {attempt}/{MigrationMaxAttempts}...");
                Thread.Sleep(MigrationRetryDelayMs * attempt);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.IsCorruptionError())
            {
                Log.Error($"[DB] Database file is corrupted (SQLite {ex.SqliteErrorCode}): {ex.Message}");
                BackupAndRecreateDatabase(dbPath);
                return;
            }
            catch (Exception ex)
            {
                Log.Fatal($"[DB] Schema migration failed, database left untouched: {ex.Message}");
                throw;
            }
        }
    }

    /// <summary>
    /// Opens an existing database and performs a transactional schema migration if the version is outdated.
    /// </summary>
    private static void MigrateExistingDatabase(bool fastMode)
    {
        var connectionFactory = Services.GetRequiredService<ISqliteConnectionFactory>();
        using var connection = connectionFactory.CreateConnection();
        connection.Open();

        int dbVersion = connection.GetDatabaseVersionAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (dbVersion < DatabaseExtensions.CurrentDbVersion)
        {
            Log.Info($"[DB] Upgrading schema: v{dbVersion} -> v{DatabaseExtensions.CurrentDbVersion}");
            connection.ApplySchemaMigrationAsync(DatabaseExtensions.CurrentDbVersion, CancellationToken.None)
                .GetAwaiter().GetResult();

            // Flag is set only after COMMIT: on migration rollback, legacy v1–v2 data remains untouched.
            if (dbVersion < 3)
                WasMigratedFromLegacy = true;

            connection.OptimizeAsync(CancellationToken.None).GetAwaiter().GetResult();
            Log.Info($"[DB] Schema upgrade complete (Version: {DatabaseExtensions.CurrentDbVersion})");
        }
        else if (!fastMode)
        {
            connection.EnsureTablesCreatedAsync(CancellationToken.None).GetAwaiter().GetResult();
            connection.OptimizeAsync(CancellationToken.None).GetAwaiter().GetResult();
            Log.Info($"[DB] Database schema is current (Version: {dbVersion})");
        }
    }

    private static void CreateFreshDatabase()
    {
        try
        {
            var connectionFactory = Services.GetRequiredService<ISqliteConnectionFactory>();
            using var connection = connectionFactory.CreateConnection();
            connection.Open();
            connection.ApplySchemaMigrationAsync(DatabaseExtensions.CurrentDbVersion, CancellationToken.None)
                .GetAwaiter().GetResult();
            connection.OptimizeAsync(CancellationToken.None).GetAwaiter().GetResult();
            Log.Info($"[DB] Fresh database created (Version: {DatabaseExtensions.CurrentDbVersion})");
        }
        catch (Exception ex)
        {
            Log.Fatal($"[DB] Failed to create fresh database: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Moves a file if it exists, replacing the destination file.
    /// </summary>
    private static void MoveIfExists(string source, string destination)
    {
        if (File.Exists(source))
            File.Move(source, destination, overwrite: true);
    }

    /// <summary>
    /// Restores functionality on database corruption (SQLITE_CORRUPT, SQLITE_NOTADB): attempts to create
    /// a consistent copy, moves source files along with WAL/journals to quarantine, and creates a clean database.
    /// </summary>
    private static void BackupAndRecreateDatabase(string dbPath)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

        // Corrupted files often cannot be read entirely, so making a consistent copy is a best-effort attempt, not a recovery requirement.
        try
        {
            var backupPath = dbPath + ".backup." + stamp;
            DatabaseExtensions.CreateConsistentBackup(dbPath, backupPath);
            Log.Info($"[DB] Consistent backup of damaged database created: {backupPath}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[DB] Consistent backup of damaged database failed, keeping raw files only: {ex.Message}");
        }

        // Journal files are moved first: a leftover -wal file would be applied to the new database.
        // Move failure is fatal; recreating the database on top of un-moved files is not allowed.
        var quarantinePath = dbPath + ".corrupt." + stamp;
        MoveIfExists(dbPath + "-wal", quarantinePath + "-wal");
        MoveIfExists(dbPath + "-shm", quarantinePath + "-shm");
        MoveIfExists(dbPath, quarantinePath);
        Log.Info($"[DB] Damaged database moved to quarantine: {quarantinePath}");

        try
        {
            var auth = Services.GetRequiredService<CookieAuthService>();
            auth.Logout();
            Log.Info("[DB] Authorization cleared after database recreation.");
        }
        catch (Exception ex)
        {
            Log.Warn($"[DB] Failed to clear authorization after database recreation: {ex.Message}");
        }

        try
        {
            CreateFreshDatabase();
            SaveEmergencyNotification();
        }
        catch (Exception ex)
        {
            Log.Fatal($"[DB] Failed to recover database with a clean slate: {ex.Message}");
            throw;
        }
    }

    private static void SaveEmergencyNotification()
    {
        try
        {
            var connectionFactory = Services.GetRequiredService<ISqliteConnectionFactory>();
            using var connection = connectionFactory.CreateConnection();
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                              INSERT INTO Notifications (
                                  Id, TitleKey, TitleRaw, MessageKey, MessageRaw,
                                  MessageArgsJson, RecommendationKey, Severity, IsRead,
                                  TrackId, TrackTitle, ExceptionDetails, AttemptsJson, CreatedAt
                              )
                              VALUES (
                                  @id, @titleKey, NULL, @messageKey, NULL,
                                  NULL, @recommendationKey, @severity, 0,
                                  NULL, NULL, NULL, NULL, @createdAt
                              );
                              """;

            var pId = cmd.CreateParameter();
            pId.ParameterName = "@id";
            pId.Value = Guid.NewGuid().ToString();
            cmd.Parameters.Add(pId);

            var pTitle = cmd.CreateParameter();
            pTitle.ParameterName = "@titleKey";
            pTitle.Value = "Dialog_Warning_Title";
            cmd.Parameters.Add(pTitle);

            var pMsg = cmd.CreateParameter();
            pMsg.ParameterName = "@messageKey";
            pMsg.Value = "Auth_ProfileLoadError_Message";
            cmd.Parameters.Add(pMsg);

            var pRec = cmd.CreateParameter();
            pRec.ParameterName = "@recommendationKey";
            pRec.Value = "Recommendation_ContactDev";
            cmd.Parameters.Add(pRec);

            var pSev = cmd.CreateParameter();
            pSev.ParameterName = "@severity";
            pSev.Value = (int)NotificationSeverity.Warning;
            cmd.Parameters.Add(pSev);

            var pCreated = cmd.CreateParameter();
            pCreated.ParameterName = "@createdAt";
            pCreated.Value = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            cmd.Parameters.Add(pCreated);

            cmd.ExecuteNonQuery();
            Log.Info("[DB] Emergency recovery notification saved to database using localization keys");
        }
        catch (Exception ex)
        {
            Log.Warn($"[DB] Failed to save emergency notification: {ex.Message}");
        }
    }

    private static void SetupGlobalExceptionHandlers()
    {
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            try
            {
                var msg = e.Exception?.InnerException?.Message
                          ?? e.Exception?.Message
                          ?? "unknown";
                Log.Debug($"[UnobservedTask] Suppressed: {msg}");
            }
            catch
            {
            }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                if (e.ExceptionObject is Exception ex)
                {
                    if (IsSslRelatedException(ex))
                    {
                        Log.Warn($"[AppDomain] SSL/TLS exception suppressed: {ex.Message}");
                        return;
                    }

                    Log.Error($"[AppDomain] Unhandled: {ex.Message}", ex);
                    OsNotificationHelper.ShowFatalError("Unhandled Exception", ex.Message, ex.ToString());
                }
                else
                {
                    Log.Error($"[AppDomain] Unhandled non-exception: {e.ExceptionObject}");
                }
            }
            catch
            {
            }
        };
    }

    private static bool IsSslRelatedException(Exception ex)
    {
        var current = ex;
        while (current is not null)
        {
            if (current is System.Security.Authentication.AuthenticationException)
            {
                return true;
            }

            var msg = current.Message;
            if (!string.IsNullOrEmpty(msg))
            {
                var span = msg.AsSpan();
                if (span.Contains("SSL".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                    span.Contains("TLS".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                    span.Contains("secure channel".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                    span.Contains("authentication".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                    span.Contains("EnsureFullTlsFrame".AsSpan(), StringComparison.Ordinal))
                {
                    return true;
                }
            }

            current = current.InnerException;
        }

        if (ex is AggregateException agg)
        {
            var inners = agg.InnerExceptions;
            for (int i = 0; i < inners.Count; i++)
            {
                if (IsSslRelatedException(inners[i]))
                    return true;
            }
        }

        return false;
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        Log.Info("Configuring services...");

        services.AddSingleton(_ => BootstrapSettings.Current);

        var dbPath = G.FilePath.Database;
        services.AddSingleton<ISqliteConnectionFactory>(_ => new LowMemorySqliteConnectionFactory(dbPath));

        services.AddSingleton<ITrackRepository, TrackRepository>();
        services.AddSingleton<IPlaylistRepository, PlaylistRepository>();
        services.AddSingleton<ISettingsRepository, SettingsRepository>();
        services.AddSingleton<INotificationRepository, NotificationRepository>();

        // 1. Centralized network manager (Singleton, automatically initializes SharedHttpClient on first resolution)
        services.AddSingleton(_ =>
        {
            var net = new NetworkManager();
            SharedHttpClient.Initialize(net);
            return net;
        });

        services.AddSingleton(sp =>
        {
            var trackRepo = sp.GetRequiredService<ITrackRepository>();
            var playlistRepo = sp.GetRequiredService<IPlaylistRepository>();
            var auth = sp.GetRequiredService<CookieAuthService>();
            return new TrackRegistry(trackRepo, playlistRepo, auth);
        });

        services.AddSingleton<IAsyncImageLoader>(sp =>
            new CachedImageLoader(sp.GetRequiredService<ImageCacheService>(), ImageQuality.Low));

        services.AddSingleton<LegacyMigrationService>();
        services.AddSingleton<LibraryService>();
        services.AddSingleton<PlaylistSyncService>();
        services.AddSingleton<PlaylistService>();
        services.AddSingleton<ThemeManagerService>();
        services.AddSingleton<CookieAuthService>();
        services.AddSingleton<LocalAuthServer>();
        services.AddSingleton<YoutubeUserDataService>();
        services.AddSingleton<YoutubeProvider>();
        services.AddTransient(sp => new Lazy<YoutubeProvider>(sp.GetRequiredService<YoutubeProvider>));

        services.AddSingleton<DialogHostViewModel>();

        services.AddSingleton(sp =>
        {
            var auth = sp.GetRequiredService<CookieAuthService>();
            var userData = sp.GetRequiredService<YoutubeUserDataService>();
            var localServer = sp.GetRequiredService<LocalAuthServer>();

            DialogHostViewModel GetDialogHost()
            {
                var mainWindow = sp.GetRequiredService<MainWindowViewModel>();
                return mainWindow.DialogHost;
            }

            return new DialogService(auth, userData, localServer, GetDialogHost);
        });

        // 2. PlayerContextManager with dynamic HttpClient resolution via NetworkManager
        services.AddSingleton(sp =>
        {
            var net = sp.GetRequiredService<NetworkManager>();
            return new PlayerContextManager(() => net.AudioClient);
        });

        services.AddSingleton<JsDecryptionService>();

        services.AddSingleton(sp =>
        {
            var jsService = sp.GetRequiredService<JsDecryptionService>();
            return new NTokenDecryptor(jsService, G.FilePath.NTokenCache);
        });

        services.AddSingleton(sp =>
        {
            var jsService = sp.GetRequiredService<JsDecryptionService>();
            return new SigCipherDecryptor(jsService, G.FilePath.SigCipherCache);
        });

        services.AddSingleton<PlaylistEditService>();

        services.AddSingleton<SearchCacheService>();
        services.AddSingleton<ImageCacheService>();

        services.AddSingleton(sp =>
        {
            var lib = sp.GetService<LibraryService>();
            long limitMb = lib is { IsInitialized: true } && lib.Settings.Storage.AudioCacheLimitMb > 0
                ? lib.Settings.Storage.AudioCacheLimitMb
                : 2048;

            var cache = new AudioCacheManager(maxCacheSizeMb: limitMb);
            AudioSourceFactory.InitializeGlobalCache(cache);
            _ = cache.EnforceLimitsAsync();
            return cache;
        });
        services.AddSingleton<AudioEngine>();
        services.AddSingleton<DownloadService>();

        services.AddSingleton<NotificationService>();
        services.AddTransient<NotificationButtonViewModel>();
        services.AddTransient<NotificationPanelViewModel>();
        services.AddTransient<ToastOverlayViewModel>();

        services.AddSingleton<PlaybackErrorOrchestrator>();

        services.AddSingleton<UpdateService>();
        services.AddSingleton<DominantColorService>();
        services.AddSingleton(sp => new PlayerControlService(
            sp.GetRequiredService<AudioEngine>(),
            sp.GetRequiredService<LibraryService>(),
            sp.GetService<NotificationService>(),
            sp.GetService<YoutubeProvider>(),
            sp.GetService<CookieAuthService>(),
            sp.GetService<TrackRegistry>(),
            sp.GetService<PlaylistSyncService>()));

        services.AddTransient<HomeViewModel>();
        services.AddTransient<SearchViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<QueueViewModel>();
        services.AddTransient<AccountLanguageSettingsViewModel>();
        services.AddTransient<NetworkSettingsViewModel>();
        services.AddTransient<StorageCacheSettingsViewModel>();
        services.AddTransient<MemorySettingsViewModel>();
        services.AddTransient<AppearanceSettingsViewModel>();
        services.AddTransient<AudioSettingsViewModel>();
        services.AddTransient<PlaybackSettingsViewModel>();
        services.AddTransient<WindowBehaviorSettingsViewModel>();
        services.AddTransient<GeneralSettingsViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<PlaylistViewModel>();
        services.AddTransient<SyncSelectionViewModel>();

        services.AddSingleton<LifecycleRegistry>();
        services.AddSingleton<TrackViewModelFactory>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<PlayerBarViewModel>();

        Log.Info("Services registered.");
    }
}
