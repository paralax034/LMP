using Avalonia;
using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Http;
using LMP.Core.Data;
using LMP.Core.Diagnostics;
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
    public static bool WasMigratedFromLegacy { get; internal set; }

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

        bool? parentProcessExited = null;
        string? parentWaitError = null;
        if (waitPid > 0)
        {
            try
            {
                using var parentProcess = System.Diagnostics.Process.GetProcessById(waitPid);
                parentProcessExited = parentProcess.WaitForExit(7000);
            }
            catch (Exception ex)
            {
                parentWaitError = ex.Message;
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

        // 3. Single instance protection: holds a mutex for the entire process lifetime
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

            if (waitPid > 0)
            {
                if (parentProcessExited == true)
                    Log.Info($"[AppEntry] Launched after in-place update. Parent process {waitPid} successfully exited.");
                else if (parentProcessExited == false)
                    Log.Warn($"[AppEntry] Launched after in-place update. Parent process {waitPid} did not exit within timeout. Proceeding...");
                else
                    Log.Debug($"[AppEntry] Launched after in-place update. Parent process {waitPid} already terminated ({parentWaitError}).");
            }

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
            ServiceRegistration.ConfigureServices(services);
            Services = services.BuildServiceProvider();

            AudioSourceFactory.RegisterLazyFactory(() => Services.GetRequiredService<AudioCacheManager>());
            SharedHttpClient.RegisterLazyFactory(() => Services.GetRequiredService<NetworkManager>());

            LifecycleRegistry.Instance = Services.GetRequiredService<LifecycleRegistry>();
            LifecycleRegistry.ActiveUiPageResolver = () =>
                Services.GetService<MainWindowViewModel>()?.CurrentPage;

#if DEBUG
            if (!IsTestUiMode && Tests.ManualTests.HasTestCliArguments(args))
            {
                WasMigratedFromLegacy = DatabaseExtensions.MigrateDatabaseSync(Services, fastMode: true);
                Environment.ExitCode = Tests.ManualTests.RunCliAsync(args).GetAwaiter().GetResult();
                return;
            }
#endif

            WasMigratedFromLegacy = DatabaseExtensions.MigrateDatabaseSync(Services, fastMode: IsFastStartup);

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
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
                    if (NetworkErrorHelper.IsSslOrTlsHandshakeFailure(ex))
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
}