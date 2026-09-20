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
using LMP.UI.Features.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace LMP;

/// <summary>
/// Точка входа в приложение Lite Music Player.
/// </summary>
public sealed class AppEntry
{
#if DEBUG
    private static bool _disableAvaloniaLogging;
    private static Avalonia.Logging.LogEventLevel _avaloniaLogLevel = Avalonia.Logging.LogEventLevel.Debug;
#endif

    /// <summary>
    /// Глобальный провайдер служб внедрения зависимостей.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>
    /// Флаг, указывающий, была ли выполнена инкрементальная миграция схемы с версии ниже v3 на старте этого запуска.
    /// </summary>
    public static bool WasMigratedFromLegacy { get; private set; }

    /// <summary>
    /// Главный метод запуска приложения.
    /// </summary>
    /// <param name="args">Аргументы командной строки.</param>
    [STAThread]
    public static void Main(string[] args)
    {
        // 1. Инициализация нативного моста SQLite для Native AOT (критично перед любым вызовом к БД)
        try
        {
            SQLitePCL.Batteries_V2.Init();
        }
        catch { }

        // 2. Защита от параллельного запуска: удерживает мьютекс на всё время жизни процесса
        using var instanceGuard = SingleInstanceGuard.TryAcquire();
        if (instanceGuard is null)
            return;

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

#if DEBUG
        // Низкоаллокационный, быстрый парсинг аргументов командной строки без LINQ
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.IsNullOrEmpty(arg)) continue;

            if (string.Equals(arg, "--no-avalonia-logs", StringComparison.OrdinalIgnoreCase))
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
        }
#endif

        SetupGlobalExceptionHandlers();

        try
        {
            G.Folder.Create();

            Log.Initialize();
            Log.Info($"{G.AppId} starting...");

            BootstrapSettings.Initialize();

            var services = new ServiceCollection();
            ConfigureServices(services);
            Services = services.BuildServiceProvider();

            LifecycleRegistry.Instance = Services.GetRequiredService<LifecycleRegistry>();
            LifecycleRegistry.ActiveUiPageResolver = () =>
                Services.GetService<MainWindowViewModel>()?.CurrentPage;

            // Безопасная инициализация и автоматическая миграция БД
            MigrateDatabaseSync();

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal($"Global crash: {ex.Message}\n{ex.StackTrace}");
            OsNotificationHelper.ShowFatalError("LMP Fatal Startup Error", ex.Message, ex.ToString());
        }
        finally
        {
            Log.Shutdown();
        }
    }

    /// <summary>
    /// Настраивает конфигурацию сборщика приложения Avalonia.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
    {
        BootstrapSettings.Initialize();
        var gpuCacheBytes = BootstrapSettings.Current.GpuTextureCacheMb * 1024L * 1024L;

        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new SkiaOptions
            {
                MaxGpuResourceSizeBytes = gpuCacheBytes
            });

        if (OperatingSystem.IsWindows() && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            Log.Info("[AppEntry] Windows 10 detected. Using RedirectionSurface to prevent dcomp.dll compositor crashes.");

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

    private static void MigrateDatabaseSync()
    {
        var dbPath = G.FilePath.Database;

        if (!File.Exists(dbPath))
        {
            CreateFreshDatabase();
            return;
        }

        // Страховочный бэкап базы данных перед проверкой и обновлением схемы
        try
        {
            var bakPath = dbPath + ".bak";
            File.Copy(dbPath, bakPath, overwrite: true);
            Log.Info($"[DB] Safety backup created: {bakPath}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[DB] Failed to create safety database backup: {ex.Message}");
        }

        int dbVersion = 0;

        try
        {
            var connectionFactory = Services.GetRequiredService<ISqliteConnectionFactory>();
            using var connection = connectionFactory.CreateConnection();
            connection.Open();

            dbVersion = connection.GetDatabaseVersionAsync(CancellationToken.None).GetAwaiter().GetResult();

            if (dbVersion < DatabaseExtensions.CurrentDbVersion)
            {
                if (dbVersion < 3)
                {
                    WasMigratedFromLegacy = true;
                }

                Log.Info($"[DB] Upgrading schema: v{dbVersion} -> v{DatabaseExtensions.CurrentDbVersion}");

                connection.EnsureTablesCreatedAsync(CancellationToken.None).GetAwaiter().GetResult();
                connection.MigrateSchemaAsync(CancellationToken.None).GetAwaiter().GetResult();
                connection.OptimizeAsync(CancellationToken.None).GetAwaiter().GetResult();
                connection.EnsureFtsTablesAsync(CancellationToken.None).GetAwaiter().GetResult();
                connection.SetDatabaseVersionAsync(DatabaseExtensions.CurrentDbVersion, CancellationToken.None).GetAwaiter().GetResult();

                Log.Info($"[DB] Schema upgrade complete (Version: {DatabaseExtensions.CurrentDbVersion})");
            }
            else
            {
                connection.EnsureTablesCreatedAsync(CancellationToken.None).GetAwaiter().GetResult();
                connection.OptimizeAsync(CancellationToken.None).GetAwaiter().GetResult();
                connection.EnsureFtsTablesAsync(CancellationToken.None).GetAwaiter().GetResult();

                Log.Info($"[DB] Database schema is current (Version: {dbVersion})");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[DB] Incremental migration from v{dbVersion} failed: {ex.Message}");
            BackupAndRecreateDatabase(dbPath);
        }
    }

    private static void CreateFreshDatabase()
    {
        try
        {
            var connectionFactory = Services.GetRequiredService<ISqliteConnectionFactory>();
            using var connection = connectionFactory.CreateConnection();
            connection.Open();

            connection.EnsureTablesCreatedAsync(CancellationToken.None).GetAwaiter().GetResult();
            connection.MigrateSchemaAsync(CancellationToken.None).GetAwaiter().GetResult();
            connection.OptimizeAsync(CancellationToken.None).GetAwaiter().GetResult();
            connection.EnsureFtsTablesAsync(CancellationToken.None).GetAwaiter().GetResult();
            connection.SetDatabaseVersionAsync(DatabaseExtensions.CurrentDbVersion, CancellationToken.None).GetAwaiter().GetResult();

            Log.Info($"[DB] Fresh database created (Version: {DatabaseExtensions.CurrentDbVersion})");
        }
        catch (Exception ex)
        {
            Log.Fatal($"[DB] Failed to create fresh database: {ex.Message}");
            throw;
        }
    }

    private static void BackupAndRecreateDatabase(string dbPath)
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            if (File.Exists(dbPath))
            {
                var backupPath = dbPath + $".backup.{DateTime.Now:yyyyMMddHHmmss}";
                File.Move(dbPath, backupPath, overwrite: true);
                Log.Info($"[DB] Incompatible database backed up to: {backupPath}");
            }

            var auth = Services.GetRequiredService<CookieAuthService>();
            auth.Logout();
            Log.Info("[DB] Authorization cleared after database recreation.");
        }
        catch (Exception backupEx)
        {
            Log.Error($"[DB] Failed to backup database before recreation: {backupEx.Message}");
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
            catch { }
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
            catch { }
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

        // 1. Централизованный сетевой менеджер (Singleton)
        services.AddSingleton<INetworkManager, NetworkManager>();

        services.AddSingleton(sp =>
        {
            var trackRepo = sp.GetRequiredService<ITrackRepository>();
            var playlistRepo = sp.GetRequiredService<IPlaylistRepository>();
            var auth = sp.GetRequiredService<CookieAuthService>();
            return new TrackRegistry(trackRepo, playlistRepo, auth);
        });

        services.AddSingleton<IAsyncImageLoader>(sp =>
            new CachedImageLoader(sp.GetRequiredService<ImageCacheService>(), ImageQuality.Low));

        services.AddSingleton<LibraryService>();
        services.AddSingleton<ThemeManagerService>();
        services.AddSingleton<CookieAuthService>();
        services.AddSingleton<LocalAuthServer>();
        services.AddSingleton<YoutubeProvider>();
        services.AddTransient(sp => new Lazy<YoutubeProvider>(sp.GetRequiredService<YoutubeProvider>));
        services.AddSingleton<YoutubeUserDataService>();

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

        // 2. PlayerContextManager с динамическим разрешением HttpClient через NetworkManager
        services.AddSingleton(sp =>
        {
            var net = sp.GetRequiredService<INetworkManager>();
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

        services.AddSingleton<PlaylistSyncService>();
        services.AddSingleton<PlaylistEditService>();

        services.AddSingleton<SearchCacheService>();
        services.AddSingleton<ImageCacheService>();

        services.AddSingleton<AudioCacheManager>();
        services.AddSingleton<AudioEngine>();
        services.AddSingleton<DownloadService>();

        services.AddSingleton<NotificationService>();
        services.AddTransient<NotificationButtonViewModel>();
        services.AddTransient<NotificationPanelViewModel>();
        services.AddTransient<ToastOverlayViewModel>();

        services.AddSingleton<PlaybackErrorOrchestrator>();

        services.AddSingleton<DominantColorService>();
        services.AddSingleton(sp => new PlayerControlService(
            sp.GetRequiredService<AudioEngine>(),
            sp.GetRequiredService<LibraryService>(),
            sp.GetService<NotificationService>(),
            sp.GetService<YoutubeProvider>(),
            sp.GetService<CookieAuthService>()));

        services.AddTransient<HomeViewModel>();
        services.AddTransient<SearchViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<QueueViewModel>();
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