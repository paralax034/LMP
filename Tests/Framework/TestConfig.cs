using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LMP.Tests.Framework;

/// <summary>
/// Test parameters configuration loaded from a JSON file.
/// <para>
/// <b>Purpose:</b> allows changing test data (videoId, itag, tokens, iterations)
/// WITHOUT recompilation — just edit the JSON and rerun the tests.
/// </para>
/// <para>
/// <b>Loading order:</b>
/// <list type="number">
///   <item>Path from <see cref="G.FilePath.TestConfig"/> (%LOCALAPPDATA%/LMP/test-config.json)</item>
///   <item>Override via the LMP_TEST_CONFIG env variable</item>
///   <item>If the file is not found, it is created with default values</item>
/// </list>
/// </para>
/// <para>
/// <b>Usage in tests:</b>
/// <code>
/// var config = TestConfig.Get();
/// var videoId = config.Pipeline.DebugVideoId;
/// var iterations = config.NToken.BenchmarkIterations;
/// </code>
/// </para>
/// </summary>
/// <remarks>
/// Singleton with lazy initialization. Thread-safe.
/// Use <see cref="Reload"/> for hot reloading.
/// </remarks>
public sealed class TestConfig
{
    //
    // SINGLETON
    //

    private static TestConfig? _instance;
    private static readonly Lock _lock = new();

    /// <summary>
    /// Returns the current configuration instance.
    /// On first call, loads from file or creates a default instance.
    /// </summary>
    public static TestConfig Get()
    {
        if (_instance is not null) return _instance;

        lock (_lock)
        {
            _instance ??= Load();
        }

        return _instance;
    }

    //
    // CONFIGURATION SECTIONS
    //

    /// <summary>
    /// Config metadata (not editable by the user).
    /// </summary>
    [JsonPropertyName("_meta")]
    public MetaConfig Meta { get; init; } = new();

    /// <summary>
    /// Parameters for StreamPipelineTests.
    /// Manages the list of test videos, debug parameters, and itags.
    /// </summary>
    [JsonPropertyName("pipeline")]
    public PipelineConfig Pipeline { get; init; } = new();

    /// <summary>
    /// Parameters for NTokenTests.
    /// Manages test tokens and the number of benchmark iterations.
    /// </summary>
    [JsonPropertyName("ntoken")]
    public NTokenConfig NToken { get; init; } = new();

    /// <summary>
    /// Parameters for SigCipherTests.
    /// Manages test signature for decryption verification.
    /// </summary>
    [JsonPropertyName("sigCipher")]
    public SigCipherConfig SigCipher { get; init; } = new();

    /// <summary>
    /// Parameters for SigCipherSolverTests.
    /// Manages the number of random combinations and benchmark iterations.
    /// </summary>
    [JsonPropertyName("solver")]
    public SolverConfig Solver { get; init; } = new();

    /// <summary>
    /// Parameters for player cross-version compatibility tests.
    /// </summary>
    [JsonPropertyName("playerVersions")]
    public PlayerVersionConfig PlayerVersions { get; init; } = new();

    /// <summary>
    /// General parameters: timeouts, behavior flags.
    /// </summary>
    [JsonPropertyName("general")]
    public GeneralConfig General { get; init; } = new();

    //
    // CONFIG CLASSES
    //

    /// <summary>
    /// Config metadata (internal service information).
    /// </summary>
    public sealed class MetaConfig
    {
        /// <summary>File description.</summary>
        [JsonPropertyName("description")]
        public string Description { get; init; } = "LMP Test Configuration. Edit values and reload (no recompilation needed).";

        /// <summary>Path to documentation.</summary>
        [JsonPropertyName("docs")]
        public string Docs { get; init; } = "https://github.com/Scream034/LMP/tree/main/Tests/Framework/TestConfig.cs";

        /// <summary>Config schema version.</summary>
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = 1;
    }

    /// <summary>
    /// Configuration for StreamPipeline tests.
    /// </summary>
    public sealed class PipelineConfig
    {
        /// <summary>
        /// List of videoIds for multi-video tests.
        /// Must be available, non-deleted videos.
        /// </summary>
        [JsonPropertyName("testVideoIds")]
        public string[] TestVideoIds { get; init; } =
        [
            "dQw4w9WgXcQ",
            "jNQXAC9IVRw",
            "kJQP7kiw5Fk",
        ];

        /// <summary>
        /// VideoId for the detailed DebugSpecificStreamAsync debug test.
        /// </summary>
        [JsonPropertyName("debugVideoId")]
        public string DebugVideoId { get; init; } = "dQw4w9WgXcQ";

        /// <summary>
        /// Target itag for the debug test.
        /// null = any first audio stream.
        /// </summary>
        [JsonPropertyName("debugTargetItag")]
        public int? DebugTargetItag { get; init; } = 251;

        /// <summary>
        /// Whether to download the test chunk (first 1KB) in the debug test.
        /// </summary>
        [JsonPropertyName("debugTestDownload")]
        public bool DebugTestDownload { get; init; } = true;
    }

    /// <summary>
    /// Configuration for NToken tests.
    /// </summary>
    public sealed class NTokenConfig
    {
        /// <summary>
        /// Test N-token for the LiveDecryption test.
        /// </summary>
        [JsonPropertyName("testToken")]
        public string TestToken { get; init; } = "Siib9I-K-KF0GqS-";

        /// <summary>
        /// Number of iterations in the NToken benchmark.
        /// </summary>
        [JsonPropertyName("benchmarkIterations")]
        public int BenchmarkIterations { get; init; } = 100;
    }

    /// <summary>
    /// Configuration for SigCipher tests.
    /// </summary>
    public sealed class SigCipherConfig
    {
        /// <summary>
        /// Test signature for the LiveDecryption test.
        /// NOTE: YouTube signatures are unique for each request.
        /// The test verifies that the result does not equal the input.
        /// </summary>
        [JsonPropertyName("testSignature")]
        public string TestSignature { get; init; } =
            "AHEqNM4wRQIgUw3FiHA8Pht_xgtH0N_C7fQwvOMGHPW9KCHzFbzj_uECIQDPrmvV4I7V_V-uKiksYsVh1xBFwp_vFpXjjLL7T4pBxg==";
    }

    /// <summary>
    /// Configuration for SigCipherSolver tests.
    /// </summary>
    public sealed class SolverConfig
    {
        /// <summary>
        /// Number of random combinations for the stress test.
        /// </summary>
        [JsonPropertyName("randomCombinationsCount")]
        public int RandomCombinationsCount { get; init; } = 100;

        /// <summary>
        /// Number of iterations in the Solver benchmark.
        /// </summary>
        [JsonPropertyName("benchmarkIterations")]
        public int BenchmarkIterations { get; init; } = 50;
    }

    /// <summary>
    /// Configuration for player version compatibility tests.
    /// </summary>
    public sealed class PlayerVersionConfig
    {
        /// <summary>
        /// List of player versions for testing.
        /// Versions are hashes from URL: /player/XXXXXXXX/
        /// </summary>
        [JsonPropertyName("versions")]
        public string[] Versions { get; init; } = [];

        /// <summary>
        /// token→expected pairs for each version.
        /// </summary>
        [JsonPropertyName("nTokenTestCases")]
        public Dictionary<string, NTokenTestCase[]> NTokenTestCases { get; init; } = [];

        /// <summary>
        /// Whether to load base.js for older versions from cache.
        /// </summary>
        [JsonPropertyName("loadCachedPlayerVersions")]
        public bool LoadCachedPlayerVersions { get; init; } = true;
    }

    /// <summary>
    /// encrypted→expected pair for NToken testing.
    /// </summary>
    public sealed class NTokenTestCase
    {
        [JsonPropertyName("encrypted")]
        public string Encrypted { get; init; } = "";

        [JsonPropertyName("expected")]
        public string Expected { get; init; } = "";

        [JsonPropertyName("description")]
        public string? Description { get; init; }
    }

    public sealed class GeneralConfig
    {
        /// <summary>
        /// Simulation of an outdated commit count for debugging auto-update in Debug (0 = disabled).
        /// </summary>
        [JsonPropertyName("simulateOldCommitCount")]
        public int SimulateOldCommitCount { get; init; } = 0;

        /// <summary>
        /// Default timeout for network tests, in seconds.
        /// </summary>
        [JsonPropertyName("defaultNetworkTimeoutSeconds")]
        public int DefaultNetworkTimeoutSeconds { get; init; } = 60;

        /// <summary>
        /// Verbose logging in tests.
        /// </summary>
        [JsonPropertyName("verboseLogging")]
        public bool VerboseLogging { get; init; } = false;
    }

    //
    // LOADING AND SAVING
    //

    /// <summary>
    /// Loads configuration from a file.
    /// </summary>
    private static TestConfig Load()
    {
        var configPath = GetConfigPath();

        try
        {
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                var config = JsonSerializer.Deserialize<TestConfig>(json, G.Json.Beautiful);

                if (config is not null)
                {
                    Log.Debug($"[TestConfig] Loaded from {configPath}");
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[TestConfig] Failed to load {configPath}: {ex.Message}");
        }

        // Create default config
        var defaultConfig = new TestConfig();
        SaveConfig(configPath, defaultConfig);
        return defaultConfig;
    }

    /// <summary>
    /// Returns the path to the configuration file.
    /// </summary>
    public static string GetConfigPath()
    {
        var envPath = Environment.GetEnvironmentVariable("LMP_TEST_CONFIG");
        if (!string.IsNullOrWhiteSpace(envPath) && Path.IsPathRooted(envPath))
            return envPath;

        return G.FilePath.TestConfig;
    }

    /// <summary>
    /// Saves configuration to valid JSON (without comments).
    /// </summary>
    private static void SaveConfig(string path, TestConfig config)
    {
        try
        {
            var json = JsonSerializer.Serialize(config, G.Json.Beautiful);
            AtomicFile.WriteText(path, json, createBackup: false);
            Log.Info($"[TestConfig] Saved config to {path}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[TestConfig] Failed to save config: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens test-config.json in a text editor.
    /// </summary>
    /// <remarks>
    /// Opening strategies:
    /// 1. notepad.exe (always present on Windows)
    /// 2. code (VS Code, if installed)
    /// 3. Fallback to Process.Start with UseShellExecute
    /// </remarks>
    public static void OpenInEditor()
    {
        var path = GetConfigPath();

        // Create file if it does not exist
        if (!File.Exists(path))
        {
            Log.Info($"[TestConfig] Creating default config at {path}");
            SaveConfig(path, new TestConfig());
        }

        try
        {
            // Strategy 1: Notepad (guaranteed to work on Windows)
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = false
                });
                Log.Info($"[TestConfig] Opened in Notepad: {path}");
                return;
            }

            // Strategy 2: xdg-open on Linux
            if (OperatingSystem.IsLinux())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = false
                });
                Log.Info($"[TestConfig] Opened with xdg-open: {path}");
                return;
            }

            // Strategy 3: open on macOS
            if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = $"-t \"{path}\"",
                    UseShellExecute = false
                });
                Log.Info($"[TestConfig] Opened with open: {path}");
                return;
            }

            // Fallback: ShellExecute
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
            Log.Info($"[TestConfig] Opened via ShellExecute: {path}");
        }
        catch (Exception ex)
        {
            Log.Error($"[TestConfig] Failed to open editor: {ex.Message}");

            // Final fallback — log the path
            Log.Info($"[TestConfig] Please open manually: {path}");
        }
    }

    /// <summary>
    /// Opens the configuration folder in file explorer.
    /// </summary>
    public static void OpenConfigFolder()
    {
        var path = GetConfigPath();
        var folder = Path.GetDirectoryName(path);

        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            Log.Warn($"[TestConfig] Folder not found: {folder}");
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = false
                });
            }
            else if (OperatingSystem.IsLinux())
            {
                Process.Start("xdg-open", folder);
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", folder);
            }

            Log.Info($"[TestConfig] Opened folder: {folder}");
        }
        catch (Exception ex)
        {
            Log.Error($"[TestConfig] Failed to open folder: {ex.Message}");
        }
    }

    /// <summary>
    /// Reloads configuration from file.
    /// </summary>
    public static TestConfig Reload()
    {
        lock (_lock)
        {
            _instance = Load();
            Log.Info("[TestConfig] Reloaded from disk");
            return _instance;
        }
    }
}