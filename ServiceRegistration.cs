using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Http;
using LMP.Core.Data;
using LMP.Core.Data.Repositories;
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
/// Central compile-time safe Dependency Injection registration for all Core and UI services.
/// </summary>
internal static class ServiceRegistration
{
    /// <summary>
    /// Registers all application singletons, repositories, and ViewModels in the DI container.
    /// </summary>
    /// <param name="services">Target service collection.</param>
    public static void ConfigureServices(IServiceCollection services)
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