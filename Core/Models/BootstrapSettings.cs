using System.Text.Json;
using System.Text.Json.Serialization;

namespace LMP.Core.Models;

public sealed class BootstrapSettings
{
    public string LanguageCode { get; set; } = "en";
    public bool IsFirstRun { get; set; } = true;
    public string? ThemeJson { get; set; }

    [JsonIgnore]
    private static readonly string FilePath = G.FilePath.Bootstrap;

    /// <summary>
    /// Skia GPU texture cache limit in bytes.
    /// Applied at startup via SkiaOptions.MaxGpuResourceSizeBytes.
    /// Changes take effect after a restart.
    /// Default: 64MB — covers ~762 120px covers without excessive RAM consumption.
    /// </summary>
    public long GpuTextureCacheMb { get; set; } = 64;

    public string? LastRunVersion { get; set; }

    /// <summary>
    /// Flag indicating that an application version update occurred during the current launch.
    /// Used for conditional reset of critical configurations.
    /// </summary>
    [JsonIgnore]
    public bool AppUpdatedThisRun { get; private set; }

    public static BootstrapSettings Load()
    {
        try
        {
            var text = AtomicFile.ReadTextWithFallback(FilePath, out bool recovered);
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (recovered)
                    Log.Warn("[Bootstrap] Recovered settings from backup (.bak)");

                var settings = JsonSerializer.Deserialize(text, AppJsonContext.Default.BootstrapSettings);
                if (settings != null)
                {
                    // CACHE CLEARING ON PLAYER VERSION UPDATE
                    if (settings.LastRunVersion != G.Build.Version)
                    {
                        settings.AppUpdatedThisRun = true;
                        Log.Info($"[Bootstrap] App updated: {settings.LastRunVersion} -> {G.Build.Version}. Purging obsolete bypass caches...");
                        PurgeBypassCaches();
                        settings.LastRunVersion = G.Build.Version;
                        settings.Save();
                    }

                    // FIRST LAUNCH CHECK
                    if (settings.IsFirstRun)
                    {
                        settings.LanguageCode = G.SystemInfo.DetectSystemLanguage();
                        settings.IsFirstRun = false;
                        settings.Save();
                        Log.Info($"[Bootstrap] First run, detected language: {settings.LanguageCode}");
                    }
                    else
                    {
                        Log.Info($"[Bootstrap] Loaded: lang={settings.LanguageCode}");
                    }
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[Bootstrap] Failed to load: {ex.Message}");
        }

        var defaults = new BootstrapSettings
        {
            IsFirstRun = false,
            LanguageCode = G.SystemInfo.DetectSystemLanguage(),
            LastRunVersion = G.Build.Version
        };

        Log.Info($"[Bootstrap] First run (no file), detected language: {defaults.LanguageCode}");
        defaults.Save();

        return defaults;
    }

    /// <summary>
    /// Forcefully deletes cached bypass files from disk.
    /// </summary>
    private static void PurgeBypassCaches()
    {
        try
        {
            if (Directory.Exists(G.Folder.NTokenCache))
                Directory.Delete(G.Folder.NTokenCache, true);
            if (Directory.Exists(G.Folder.SigCipherCache))
                Directory.Delete(G.Folder.SigCipherCache, true);

            G.Folder.Create(); // Recreate empty directories of the folder structure
        }
        catch (Exception ex)
        {
            Log.Warn($"[Bootstrap] Failed to purge bypass caches: {ex.Message}");
        }
    }

    public void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(this, AppJsonContext.DefaultPretty.BootstrapSettings);
            AtomicFile.WriteText(FilePath, json, createBackup: false);
        }
        catch (Exception ex)
        {
            Log.Error($"[Bootstrap] Failed to save: {ex.Message}");
        }
    }

    private static bool _isInitialized;

    public static BootstrapSettings Current { get; private set; } = new();

    public static void Initialize(bool forceReload = false)
    {
        if (_isInitialized && !forceReload)
            return;

        _isInitialized = true;
        Current = Load();
    }
}