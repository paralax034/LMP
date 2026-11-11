namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Unified control point for player version and base.js.
/// Singleton, thread-safe.
/// </summary>
/// <param name="httpProvider">HTTP client factory for downloading player scripts.</param>
public class PlayerContextManager(Func<HttpClient> httpProvider)
{
    private readonly Func<HttpClient> _httpProvider = httpProvider ?? throw new ArgumentNullException(nameof(httpProvider));

    /// <summary>
    /// Single-flight semaphore: guarantees a single active <see cref="PlayerContext.DetectVersionAsync"/>
    /// across any number of concurrent <see cref="GetOrLoadAsync"/> calls.
    /// </summary>
    private readonly SemaphoreSlim _initSemaphore = new(1, 1);

    private volatile PlayerContext? _current;
    private volatile string? _cachedSignatureTimestamp;

    /// <summary>
    /// Returns the current player context.
    /// If no valid in-memory context exists, sequentially checks
    /// the disk cache and performs a network fetch.
    /// Falls back to the disk cache if the network is unavailable.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// Failed to detect the player version or download base.js.
    /// </exception>
    public virtual async Task<PlayerContext> GetOrLoadAsync(CancellationToken ct = default)
    {
        // Fast path: volatile read without acquiring the semaphore
        var current = _current;
        if (current?.IsValid() == true) return current;

        await _initSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            current = _current;
            if (current?.IsValid() == true) return current;

            var http = _httpProvider();

            // Attempting to detect version via network
            (string Version, string[] Urls)? versionInfo;
            try
            {
                versionInfo = await PlayerContext.DetectVersionAsync(http, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // Internal HttpClient timeout (does not cancel caller code)
                versionInfo = null;
            }

            // Network unavailable → fallback to disk cache
            if (versionInfo is not { } versionData)
            {
                var diskFallback = TryLoadFromDiskCache();
                if (diskFallback != null)
                {
                    ApplyClientVersion(diskFallback.ClientVersion);
                    _current = diskFallback;
                    return diskFallback;
                }

                throw new InvalidOperationException("Failed to detect player version");
            }

            string version = versionData.Version;
            string[] urls = versionData.Urls;

            var cached = PlayerContext.LoadFromCache(version);
            if (cached is not null)
            {
                ApplyClientVersion(cached.ClientVersion);
                _current = cached;
                Log.Debug($"[PlayerContextManager] Loaded from cache: {version}");
                return cached;
            }

            foreach (var url in urls)
            {
                try
                {
                    Log.Debug($"[PlayerContextManager] Downloading: {url}");
                    var baseJs = await http.GetStringAsync(url, ct).ConfigureAwait(false);
                    var newContext = new PlayerContext(version, baseJs);
                    await newContext.SaveCacheAsync().ConfigureAwait(false);
                    ApplyClientVersion(newContext.ClientVersion);
                    _current = newContext;
                    Log.Info($"[PlayerContextManager] Loaded fresh: {version} ({baseJs.Length / 1024}KB)");
                    return newContext;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Debug($"[PlayerContextManager] Download failed ({url}): {ex.Message}");
                }
            }

            var lastResort = TryLoadFromDiskCache();
            if (lastResort != null)
            {
                ApplyClientVersion(lastResort.ClientVersion);
                _current = lastResort;
                return lastResort;
            }

            throw new InvalidOperationException("Failed to download base.js from all candidate URLs");
        }
        finally
        {
            _initSemaphore.Release();
        }
    }

    /// <summary>
    /// Scans the disk cache and loads the freshest valid context
    /// without network requests. Used as a fallback when
    /// <see cref="PlayerContext.DetectVersionAsync"/> is unavailable.
    /// </summary>
    /// <returns>Player context or <c>null</c> if no valid cache exists.</returns>
    internal static PlayerContext? TryLoadFromDiskCache()
    {
        try
        {
            var dir = G.Folder.NTokenCache;
            if (!Directory.Exists(dir)) return null;

            var stsFiles = Directory.GetFiles(dir, "player_*_sts.txt");
            if (stsFiles.Length == 0) return null;

            // Freshest first — single-pass modification time collection to avoid O(N log N) disk I/O
            var fileInfos = new (string Path, DateTime LastWriteUtc)[stsFiles.Length];
            for (int i = 0; i < stsFiles.Length; i++)
            {
                fileInfos[i] = (stsFiles[i], File.GetLastWriteTimeUtc(stsFiles[i]));
            }

            Array.Sort(fileInfos, static (a, b) => b.LastWriteUtc.CompareTo(a.LastWriteUtc));

            const string prefix = "player_";
            const string suffix = "_sts.txt";

            for (int i = 0; i < fileInfos.Length; i++)
            {
                var fileName = Path.GetFileName(fileInfos[i].Path);
                var span = fileName.AsSpan();

                if (!span.StartsWith(prefix.AsSpan(), StringComparison.Ordinal) ||
                    !span.EndsWith(suffix.AsSpan(), StringComparison.Ordinal))
                    continue;

                var version = span[prefix.Length..^suffix.Length].ToString();
                if (string.IsNullOrEmpty(version)) continue;

                var context = PlayerContext.LoadFromCache(version);
                if (context?.IsValid() == true)
                {
                    ApplyClientVersion(context.ClientVersion);
                    Log.Info($"[PlayerContextManager] Disk cache fallback: loaded version {version}");
                    return context;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[PlayerContextManager] TryLoadFromDiskCache failed: {ex.Message}");
        }

        return null;
    }

    private static void ApplyClientVersion(string? clientVersion)
    {
        if (!string.IsNullOrWhiteSpace(clientVersion))
        {
            YoutubeHttpHandler.WebClientVersion = clientVersion;
        }
    }

    /// <summary>
    /// Returns the cached signatureTimestamp.
    /// Single source of truth for all <see cref="VideoController"/> instances.
    /// </summary>
    internal string? GetCachedSignatureTimestamp() => _cachedSignatureTimestamp;

    /// <summary>Writes signatureTimestamp to cache.</summary>
    /// <param name="sts">STS value or <c>null</c> to reset.</param>
    public void SetCachedSignatureTimestamp(string? sts) => _cachedSignatureTimestamp = sts;

    /// <summary>
    /// Resets the signatureTimestamp cache.
    /// Invoked via <see cref="VideoController.InvalidateSignatureTimestamp"/>
    /// during 403 recovery — guarantees reset for all <see cref="VideoController"/> instances.
    /// </summary>
    public void InvalidateSignatureTimestamp()
    {
        _cachedSignatureTimestamp = null;
        Log.Debug("[PlayerContextManager] SignatureTimestamp cache invalidated");
    }

    /// <summary>
    /// Soft invalidation: resets in-memory context, preserving disk cache.
    /// </summary>
    public void InvalidateContext() => _current = null;

    /// <summary>
    /// Hard invalidation: resets in-memory context and physically deletes disk cache.
    /// </summary>
    public void Invalidate()
    {
        // Snapshot before nulling: correct during concurrent write from GetOrLoadAsync
        var snapshot = _current;
        _current = null;
        if (snapshot != null)
            PlayerContext.ClearDiskCache(snapshot.Version);
    }
}