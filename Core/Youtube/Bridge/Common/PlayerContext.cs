using System.Text.RegularExpressions;

namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Represents an immutable and thread-safe context for a specific YouTube JS player version.
/// Coordinates the storage of original code, AST optimization results, and compiled native bytecode.
/// </summary>
/// <remarks>
/// <para>
/// <b>Memory Architecture (LOH Fragmentation Prevention):</b>
/// The source code of the YouTube player (<c>base.js</c>) is ~2.5 MB. Strings of this size are allocated in the Large Object Heap (LOH).
/// Long-term retention of such strings in memory inevitably leads to LOH fragmentation and triggers expensive GC Gen 2 collections.
/// After compiling the context into the native engine, the <see cref="ReleaseRawScripts"/> method forcibly frees the original
/// <see cref="BaseJs"/>, minimizing pressure on the garbage collector.
/// </para>
/// <para>
/// <b>QuickJS Bytecode Safety (Native Crash Prevention):</b>
/// Native QuickJS-NG bytecode is binary incompatible across different compiler versions or bridge configurations.
/// Attempting to load outdated or incompatible bytecode causes a native process SegFault, crashing the entire .NET application.
/// To guard against such crashes, the bytecode cache includes the native bridge ABI marker in its filename (<see cref="GetBytecodeCachePath"/>).
/// </para>
/// </remarks>
public sealed partial class PlayerContext
{
    private const int MaxCachedVersions = 10;
    private const int MaxAgeDays = 14;

    /// <summary>
    /// A lightweight synchronization object (.NET 9+) that protects the script compilation
    /// and optimization process against thread races.
    /// </summary>
    private readonly Lock _prepLock = new();

    /// <summary>
    /// Hash identifier of the YouTube player version (e.g., <c>"06cb0078"</c>).
    /// Used as a key marker in all file operations and cache paths.
    /// </summary>
    public string Version { get; }

    /// <summary>
    /// Complete original source code of the player (<c>base.js</c>), loaded from network or disk.
    /// </summary>
    /// <value>
    /// Can be reset to <see cref="string.Empty"/> after context initialization via <see cref="ReleaseRawScripts"/>
    /// to prevent retaining memory in LOH.
    /// </value>
    public string BaseJs { get; private set; }

    /// <summary>
    /// JS code optimized via Tree Shaking and ready for execution.
    /// </summary>
    /// <remarks>
    /// Contains only those functions and dependencies directly involved in bypassing the signature (Sig) and N-token.
    /// </remarks>
    public string? PreprocessedJs { get; private set; }

    /// <summary>
    /// Signature timestamp (STS / Signature Timestamp).
    /// Required to construct valid requests to YouTube video delivery servers (video cookies).
    /// </summary>
    public string? Sts { get; private set; }

    /// <summary>
    /// Current InnerTube Web client version, extracted dynamically from the player script code.
    /// </summary>
    public string? ClientVersion { get; private set; }

    /// <summary>
    /// Creation timestamp of the object in application memory.
    /// Used to control rotation and invalidation of stale player contexts within YouTube's 12-hour cycle.
    /// </summary>
    public DateTimeOffset CachedAt { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayerContext"/> class.
    /// </summary>
    /// <param name="version">Unique string version of the player.</param>
    /// <param name="baseJs">Original player JS code.</param>
    /// <param name="preprocessedJs">Optional pre-optimized JS code.</param>
    /// <param name="sts">Optional signature timestamp. If passed as <c>null</c>, it will be extracted automatically.</param>
    /// <param name="clientVersion">Optional InnerTube client version. If <c>null</c>, extracted from <paramref name="baseJs"/>.</param>
    public PlayerContext(
        string version,
        string baseJs,
        string? preprocessedJs = null,
        string? sts = null,
        string? clientVersion = null)
    {
        Version = version;
        BaseJs = baseJs;
        PreprocessedJs = preprocessedJs;
        Sts = sts ?? (string.IsNullOrEmpty(baseJs) ? null : YoutubeAstSolver.ExtractSts(baseJs));
        ClientVersion = clientVersion ?? (string.IsNullOrEmpty(baseJs) ? null : ExtractClientVersion(baseJs));
        CachedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Extracts the current InnerTube Web client version from the player code.
    /// </summary>
    public static string? ExtractClientVersion(string baseJs)
    {
        if (string.IsNullOrEmpty(baseJs)) return null;

        var match = ClientVersionRegex().Match(baseJs);
        if (!match.Success) return null;

        var val = match.Groups[1].Value;
        return string.IsNullOrWhiteSpace(val) ? null : val;
    }

    /// <summary>
    /// Returns the preprocessed JS code or compiles it on the fly (Lazy Initialization)
    /// using the provided optimization delegate.
    /// </summary>
    /// <param name="preprocessor">Delegate performing AST parsing and Tree Shaking of the player.</param>
    /// <returns>A string containing optimized JavaScript code.</returns>
    /// <remarks>
    /// This method is thread-safe. Upon successful compilation, the result is automatically written to disk asynchronously.
    /// </remarks>
    public string GetOrPrepareScript(Func<string> preprocessor)
    {
        if (!string.IsNullOrEmpty(PreprocessedJs)) return PreprocessedJs;

        lock (_prepLock)
        {
            if (!string.IsNullOrEmpty(PreprocessedJs)) return PreprocessedJs;

            string js = preprocessor();
            PreprocessedJs = js;

            // Asynchronous cache write to avoid blocking the main execution thread
            _ = SavePreprocessedCacheAsync();

            return js;
        }
    }

    /// <summary>
    /// Releases the massive original player script string (<see cref="BaseJs"/>)
    /// and the intermediate preprocessed script (<see cref="PreprocessedJs"/>) from RAM.
    /// Call strictly after initializing the native QuickJS context.
    /// </summary>
    public void ReleaseRawScripts()
    {
        lock (_prepLock)
        {
            BaseJs = string.Empty;
            PreprocessedJs = null;
            Log.Debug($"[PlayerContext] Discarded raw base.js and preprocessed script from memory for player version: {Version}.");
        }
    }

    /// <summary>
    /// Checks whether this player context in RAM is still fresh.
    /// </summary>
    /// <returns><c>true</c> if less than 12 hours have elapsed since caching (matches YouTube's rotation cycle).</returns>
    public bool IsValid() => (DateTimeOffset.UtcNow - CachedAt).TotalHours < 12;

    /// <summary>
    /// Asynchronously writes the optimized JS version and STS timestamp to disk.
    /// </summary>
    public async Task SavePreprocessedCacheAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(PreprocessedJs)) return;

            var prepPath = GetPreprocessedCachePath(Version);
            var stsPath = GetStsCachePath(Version);
            var cverPath = GetClientVersionCachePath(Version);

            await AtomicFile.WriteTextAsync(prepPath, PreprocessedJs, createBackup: false).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(Sts))
            {
                await AtomicFile.WriteTextAsync(stsPath, Sts, createBackup: false).ConfigureAwait(false);
            }
            if (!string.IsNullOrEmpty(ClientVersion))
            {
                await AtomicFile.WriteTextAsync(cverPath, ClientVersion, createBackup: false).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[PlayerContext] Preprocessed cache save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Performs full asynchronous caching: original code, optimized JS, and metadata to disk.
    /// Triggers automatic cleanup of stale files.
    /// </summary>
    public async Task SaveCacheAsync()
    {
        try
        {
            var path = GetCachePath(Version);

            if (!string.IsNullOrEmpty(BaseJs))
            {
                await AtomicFile.WriteTextAsync(path, BaseJs, createBackup: false).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(ClientVersion))
            {
                var cverPath = GetClientVersionCachePath(Version);
                await AtomicFile.WriteTextAsync(cverPath, ClientVersion, createBackup: false).ConfigureAwait(false);
            }

            await SavePreprocessedCacheAsync().ConfigureAwait(false);
            CleanupOldVersions();
        }
        catch (Exception ex)
        {
            Log.Debug($"[PlayerContext] Cache save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Loads metadata from cache. Avoids reading heavy JS files if compiled bytecode is available.
    /// </summary>
    public static PlayerContext? LoadFromCache(string version)
    {
        try
        {
            var prepPath = GetPreprocessedCachePath(version);
            var stsPath = GetStsCachePath(version);
            var cverPath = GetClientVersionCachePath(version);
            var baseJsPath = GetCachePath(version);
            var bytecodePath = GetBytecodeCachePath(version);

            string? clientVersion = File.Exists(cverPath) ? File.ReadAllText(cverPath).Trim() : null;

            if (File.Exists(bytecodePath) && File.Exists(stsPath))
            {
                var sts = File.ReadAllText(stsPath).Trim();
                Log.Debug($"[PlayerContext] Bytecode and STS caches exist for {version}. Skipping base.js/preprocessed.js disk I/O.");
                return new PlayerContext(version, string.Empty, preprocessedJs: null, sts, clientVersion);
            }

            if (File.Exists(prepPath) && File.Exists(stsPath))
            {
                var age = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(prepPath);
                if (age.TotalDays <= MaxAgeDays)
                {
                    var sts = File.ReadAllText(stsPath).Trim();
                    Log.Debug($"[PlayerContext] Loaded metadata cache for {version} (sts={sts})");
                    return new PlayerContext(version, string.Empty, preprocessedJs: null, sts, clientVersion);
                }
            }

            if (!File.Exists(baseJsPath)) return null;

            var baseAge = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(baseJsPath);
            if (baseAge.TotalDays > MaxAgeDays)
            {
                Log.Debug($"[PlayerContext] Cache expired for {version} ({baseAge.TotalDays:F0}d old)");
                return null;
            }

            var baseJs = File.ReadAllText(baseJsPath);
            return new PlayerContext(version, baseJs, preprocessedJs: null, sts: null, clientVersion);
        }
        catch (Exception ex)
        {
            Log.Debug($"[PlayerContext] Cache load failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Loads cached player context from disk without checking expiration.
    /// </summary>
    /// <param name="version">Player version hash.</param>
    /// <param name="requireRawBaseJs">If <c>true</c>, loads original <c>base.js</c> for a full AST parser pass.</param>
    public static PlayerContext? LoadFromCacheNoExpiry(string version, bool requireRawBaseJs = false)
    {
        try
        {
            var prepPath = GetPreprocessedCachePath(version);
            var stsPath = GetStsCachePath(version);
            var cverPath = GetClientVersionCachePath(version);
            var baseJsPath = GetCachePath(version);

            string? clientVersion = File.Exists(cverPath) ? File.ReadAllText(cverPath).Trim() : null;

            if (!requireRawBaseJs && File.Exists(prepPath) && File.Exists(stsPath))
            {
                var preprocessedJs = File.ReadAllText(prepPath);
                var sts = File.ReadAllText(stsPath).Trim();

                if (!string.IsNullOrWhiteSpace(preprocessedJs))
                {
                    return new PlayerContext(version, string.Empty, preprocessedJs, sts, clientVersion);
                }
            }

            if (!File.Exists(baseJsPath)) return null;

            var baseJs = File.ReadAllText(baseJsPath);
            if (string.IsNullOrWhiteSpace(baseJs)) return null;

            return new PlayerContext(version, baseJs, preprocessedJs: null, sts: null, clientVersion);
        }
        catch (Exception ex)
        {
            Log.Debug($"[PlayerContext] Cache load failed for {version}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Returns a list of all player versions that have saved <c>base.js</c> or <c>preprocessed.js</c> in the local cache (newest to oldest).
    /// </summary>
    public static string[] GetCachedVersions()
    {
        try
        {
            var dir = G.Folder.NTokenCache;
            if (!Directory.Exists(dir)) return [];

            var files = Directory.GetFiles(dir, "player_*_basejs.txt");
            if (files.Length == 0)
                files = Directory.GetFiles(dir, "player_*_preprocessed.js");

            if (files.Length == 0) return [];

            var items = new (string Version, DateTime LastWriteUtc)[files.Length];
            int count = 0;

            for (int i = 0; i < files.Length; i++)
            {
                var match = PlayerVersionAnyFileRegex().Match(Path.GetFileName(files[i]));
                if (match.Success)
                {
                    items[count++] = (match.Groups[1].Value, File.GetLastWriteTimeUtc(files[i]));
                }
            }

            Array.Sort(items, 0, count, Comparer<(string Version, DateTime LastWriteUtc)>.Create(
                static (a, b) => b.LastWriteUtc.CompareTo(a.LastWriteUtc)));

            var result = new string[count];
            for (int i = 0; i < count; i++)
                result[i] = items[i].Version;

            return result;
        }
        catch
        {
            return [];
        }
    }

    private static string GetCachePath(string version) =>
        Path.Combine(G.Folder.NTokenCache, $"player_{version}_basejs.txt");

    private static string GetPreprocessedCachePath(string version) =>
        Path.Combine(G.Folder.NTokenCache, $"player_{version}_preprocessed.js");

    private static string GetStsCachePath(string version) =>
        Path.Combine(G.Folder.NTokenCache, $"player_{version}_sts.txt");

    private static string GetClientVersionCachePath(string version) =>
        Path.Combine(G.Folder.NTokenCache, $"player_{version}_cver.txt");

    public static string GetBytecodeCachePath(string version) =>
        Path.Combine(G.Folder.NTokenCache, $"player_{version}_abi{QuickJsNative.BridgeAbi}_bytecode.bin");

    private static void CleanupOldVersions()
    {
        try
        {
            var dir = G.Folder.NTokenCache;
            if (!Directory.Exists(dir)) return;

            var files = Directory.GetFiles(dir, "player_*_basejs.txt");
            if (files.Length <= MaxCachedVersions) return;

            var now = DateTime.UtcNow;

            var candidates = files
                .Select(static f => new FileInfo(f))
                .OrderByDescending(static f => f.LastWriteTimeUtc)
                .Skip(MaxCachedVersions)
                .Where(f => (now - f.LastWriteTimeUtc).TotalDays > MaxAgeDays)
                .ToArray();

            foreach (var file in candidates)
            {
                try
                {
                    var name = file.Name;
                    var versionMatch = PlayerVersionRegex().Match(name);
                    if (versionMatch.Success)
                    {
                        var version = versionMatch.Groups[1].Value;
                        var prepPath = GetPreprocessedCachePath(version);
                        var stsPath = GetStsCachePath(version);
                        var cverPath = GetClientVersionCachePath(version);
                        var bytecodePath = GetBytecodeCachePath(version);

                        if (File.Exists(prepPath)) File.Delete(prepPath);
                        if (File.Exists(stsPath)) File.Delete(stsPath);
                        if (File.Exists(cverPath)) File.Delete(cverPath);
                        if (File.Exists(bytecodePath)) File.Delete(bytecodePath);
                    }

                    file.Delete();
                    Log.Debug($"[PlayerContext] Cleaned up old cache: {file.Name}");
                }
                catch
                {
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[PlayerContext] Scheduled cleanup failed: {ex.Message}");
        }
    }

    public static async Task<(string Version, string[] Urls)?> DetectVersionAsync(
        HttpClient http,
        CancellationToken ct = default)
    {
        try
        {
            var iframeApi = await http.GetStringAsync("https://www.youtube.com/iframe_api", ct).ConfigureAwait(false);
            var match = PlayerVersionIframeRegex().Match(iframeApi);
            if (!match.Success) return null;

            var version = match.Groups[1].Value;
            string[] urls = [$"https://www.youtube.com/s/player/{version}/player_es6.vflset/ru_RU/base.js"];

            return (version, urls);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"[PlayerContext] Version detection failed: {ex.Message}");
            return null;
        }
    }

    public static void ClearDiskCache(string version)
    {
        try
        {
            var prepPath = GetPreprocessedCachePath(version);
            var stsPath = GetStsCachePath(version);
            var cverPath = GetClientVersionCachePath(version);
            var baseJsPath = GetCachePath(version);
            var bytecodePath = GetBytecodeCachePath(version);

            if (File.Exists(prepPath)) File.Delete(prepPath);
            if (File.Exists(stsPath)) File.Delete(stsPath);
            if (File.Exists(cverPath)) File.Delete(cverPath);
            if (File.Exists(baseJsPath)) File.Delete(baseJsPath);
            if (File.Exists(bytecodePath)) File.Delete(bytecodePath);

            Log.Info($"[PlayerContext] Cleared disk cache files for version {version}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[PlayerContext] Failed to clear disk cache for {version}: {ex.Message}");
        }
    }

    [GeneratedRegex(@"player_([a-fA-F0-9]+)_basejs\.txt")]
    private static partial Regex PlayerVersionRegex();

    [GeneratedRegex(@"player_([a-fA-F0-9]+)_(?:basejs\.txt|preprocessed\.js)")]
    private static partial Regex PlayerVersionAnyFileRegex();

    [GeneratedRegex(@"player\\?/([0-9a-fA-F]{8})\\?/")]
    private static partial Regex PlayerVersionIframeRegex();

    [GeneratedRegex(@"(?:INNERTUBE_CONTEXT_CLIENT_VERSION|clientVersion)\s*[:=]\s*[""']([^""']+)[""']", RegexOptions.Compiled)]
    private static partial Regex ClientVersionRegex();
}