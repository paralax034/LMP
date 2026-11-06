using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;

namespace LMP.Core.Services;

public enum ImageQuality
{
    Low = 120,
    Medium = 200,
    High = 400,
    Ultra = 800
}

/// <summary>
/// Ultra-optimized image caching service.
/// Direct-to-Disk + atomic operations + Lock-Free LRU.
///
/// <para><b>Memory Architecture:</b></para>
/// <list type="bullet">
///   <item>Memory cache: Dictionary{ulong} (under lock) + LinkedList for O(1) LRU.
///     Key is ulong FNV-1a hash: zero-alloc lookup, 8 bytes vs 40+ bytes string.</item>
///   <item>ConcurrentDictionary removed: all access to it was under _lruLock
///     (double synchronization = pure overhead).</item>
///   <item>Disk cache: direct write via FileStream without MemoryStream.</item>
///   <item>Deduplication: Lazy&lt;Task&gt; with a fix for the AddRef bug.</item>
/// </list>
/// </summary>
public sealed class ImageCacheService : IDisposable
{
    private readonly NetworkManager _networkManager;
    private readonly LibraryService _library;
    private readonly SemaphoreSlim _downloadSemaphore = new(6);

    // App-level CTS: manages background download lifecycle.
    // Control-level CTs are used as decode-gate only,
    // but do NOT interrupt writing the file to disk.
    private readonly CancellationTokenSource _appCts = new();

    /// <summary>
    /// Memory cache. Key is FNV-1a hash (ulong), not string.
    /// Zero-alloc lookup on hot path: hash is computed arithmetically,
    /// Dictionary{ulong} uses GetHashCode() = (int)key, without boxing.
    /// All operations are under _lruLock.
    /// </summary>
    private readonly Dictionary<ulong, RefCountedBitmap> _memoryCache = [];
    private readonly LinkedList<ulong> _lruOrder = new();
    private readonly Dictionary<ulong, LinkedListNode<ulong>> _lruIndex = [];
    private readonly Lock _lruLock = new();

    /// <summary>
    /// Deduplication of parallel downloads for the same URL.
    /// Lazy guarantees the factory is invoked exactly once during concurrent access.
    /// Key is ulong hash.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, Lazy<Task<Bitmap?>>> _pendingLoads = [];

    /// <summary>
    /// Deduplication of parallel network downloads to disk by file hash (independent of decodeWidth).
    /// </summary>
    private readonly ConcurrentDictionary<ulong, Task> _pendingDiskDownloads = [];

    private long _currentDiskCacheBytes;
    private int _currentDiskFileCount;
    private long _currentMemoryCacheBytes;
    private bool _isDisposed;
    private int _loadCounter;
    private const int CleanupInterval = 50;

    /// <summary>
    /// Minimum cache size covers two track screens (~20 visible × 2) + buffer.
    /// At Low quality (120px): 80 × 57KB ≈ 4.5MB — a reasonable default.
    /// Configured via Settings.Storage.MaxBitmapCacheItems.
    /// </summary>
    private int MaxMemoryItems => _library.Settings.Storage.MaxBitmapCacheItems > 0
        ? _library.Settings.Storage.MaxBitmapCacheItems
        : 80;

    // Actual byte limit:
    // Low (120px):  120×120×4 = 57.6KB × 25 items ≈ 1.4MB — correct
    // High (400px): 400×400×4 = 640KB × 25 items ≈ 16MB — must account for
    private long MaxMemoryBytes => MaxMemoryItems * 400L * 400 * 4; // margin for High quality

    public ImageCacheService(NetworkManager networkManager, LibraryService library)
    {
        _networkManager = networkManager;
        _library = library;

        if (!Directory.Exists(G.Folder.ImageCache))
            Directory.CreateDirectory(G.Folder.ImageCache);

        _ = Task.Run(InitializeDiskCacheAsync);
    }

    /// <summary>
    /// Synchronously provides a bitmap from RAM without delays or Task allocations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetFromMemory(string url, int decodeWidth, out Bitmap? bitmap)
    {
        bitmap = null;
        if (_isDisposed || string.IsNullOrEmpty(url)) return false;

        int normalizedWidth = decodeWidth switch
        {
            <= 0 => 0,
            <= 120 => 120,
            <= 200 => 200,
            <= 400 => 400,
            _ => 800
        };

        var memKey = ComputeMemoryKeyHash(url, normalizedWidth);

        lock (_lruLock)
        {
            if (_memoryCache.TryGetValue(memKey, out var cached))
            {
                TouchLruUnsafe(memKey);
                bitmap = cached.Bitmap;
                return true;
            }
        }

        return false;
    }

    public Task<Bitmap?> GetImageAsync(string url, ImageQuality quality = ImageQuality.Low, CancellationToken ct = default)
        => GetImageAsync(url, (int)quality, ct);

    /// <summary>
    /// Normalizes <paramref name="decodeWidth"/> once at entry.
    /// The normalized value is used as both the cache key and the actual decode width —
    /// guarantees that all callers with different raw values in the same bucket get
    /// an identical bitmap of the required quality.
    /// </summary>
    public async Task<Bitmap?> GetImageAsync(string url, int decodeWidth, CancellationToken ct = default)
    {
        if (_isDisposed || string.IsNullOrEmpty(url)) return null;

        // Normalization in ONE place: cache key and actual decode use the same value.
        // Without this: width=44 → key=120, decode=44 → cache contains 44px bitmap under key 120.
        // Next request width=100 → key=120 → hit → gets 44px instead of 100px.
        int normalizedWidth = decodeWidth switch
        {
            <= 0 => 0,
            <= 120 => 120,
            <= 200 => 200,
            <= 400 => 400,
            _ => 800
        };

        var memKey = ComputeMemoryKeyHash(url, normalizedWidth);

        // 1. Hot path: Memory cache
        lock (_lruLock)
        {
            if (_memoryCache.TryGetValue(memKey, out var cached))
            {
                TouchLruUnsafe(memKey);
                return cached.Bitmap;
            }
        }

        // 2. Cold path: deduplication
        var appToken = _appCts.Token;
        var lazyTask = _pendingLoads.GetOrAdd(
            memKey,
            static (k, state) => new Lazy<Task<Bitmap?>>(() =>
                state.self.LoadImageInternalAsync(state.url, k, state.normalizedWidth, state.appToken)),
            (self: this, url, normalizedWidth, appToken));  // ← normalizedWidth, not decodeWidth

        Bitmap? bitmap;
        try
        {
            bitmap = await lazyTask.Value.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return null; }
        catch { return null; }
        finally
        {
            _pendingLoads.TryRemove(memKey, out _);

            if (Interlocked.Increment(ref _loadCounter) % CleanupInterval == 0)
                _ = Task.Run(PerformMaintenanceAsync, CancellationToken.None);
        }

        if (ct.IsCancellationRequested) return null;
        return bitmap;
    }

    /// <summary>
    /// Prefetches images to disk without decoding into RAM.
    /// Useful for pre-caching cover art (e.g., top-10 search results).
    /// </summary>
    public async Task PrefetchAsync(IEnumerable<string> urls, CancellationToken ct = default)
    {
        if (_isDisposed) return;

        // "Already on disk" filtering occurs inside EnsureDiskCachedAsync with double-check.
        var candidates = urls
            .Where(static u => !string.IsNullOrEmpty(u))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .Select(u => EnsureDiskCachedAsync(u, ct));

        try { await Task.WhenAll(candidates).ConfigureAwait(false); }
        catch { /* Prefetch errors are silently ignored — this is a background optimization */ }
    }

    /// <summary>
    /// Downloads a file to disk if it is not already present. Nothing is retained in RAM.
    /// </summary>
    private async Task EnsureDiskCachedAsync(string url, CancellationToken ct)
    {
        var diskHash = ComputeDiskKeyHash(url);
        var diskPath = Path.Combine(G.Folder.ImageCache, diskHash.ToString("X16"));

        if (File.Exists(diskPath)) return;

        try
        {
            await EnsureDiskDownloadTaskAsync(url, diskHash, diskPath).WaitAsync(ct).ConfigureAwait(false);
        }
        catch { }
    }

    private Task EnsureDiskDownloadTaskAsync(string url, ulong diskHash, string diskPath)
    {
        if (File.Exists(diskPath)) return Task.CompletedTask;

        return _pendingDiskDownloads.GetOrAdd(diskHash, _ => DownloadThrottledAsync(url, diskHash, diskPath));
    }

    private async Task DownloadThrottledAsync(string url, ulong diskHash, string diskPath)
    {
        try
        {
            await _downloadSemaphore.WaitAsync(_appCts.Token).ConfigureAwait(false);
            try
            {
                if (!File.Exists(diskPath))
                    await DownloadDirectToDiskAsync(url, diskPath, _appCts.Token).ConfigureAwait(false);
            }
            finally
            {
                _downloadSemaphore.Release();
            }
        }
        finally
        {
            _pendingDiskDownloads.TryRemove(diskHash, out _);
        }
    }

    /// <summary>
    /// Internal fetch pipeline: disk-check → download → decode → memory cache.
    /// Accepts an app-level <paramref name="ct"/> (not control-level):
    /// file download is not interrupted when a list item is recycled.
    /// </summary>
    /// <param name="url">Image web address.</param>
    /// <param name="memKey">Hash key for RAM cache.</param>
    /// <param name="decodeWidth">Target decode width.</param>
    /// <param name="ct">App-level cancellation token.</param>
    /// <returns>Decoded <see cref="Bitmap"/> or <see langword="null"/> on cancellation.</returns>
    private async Task<Bitmap?> LoadImageInternalAsync(string url, ulong memKey, int decodeWidth, CancellationToken ct)
    {
        var diskHash = ComputeDiskKeyHash(url);
        var diskPath = Path.Combine(G.Folder.ImageCache, diskHash.ToString("X16"));

        if (!File.Exists(diskPath))
        {
            try
            {
                await EnsureDiskDownloadTaskAsync(url, diskHash, diskPath).WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return null; }
            catch { return null; }
        }

        if (!File.Exists(diskPath)) return null;

        Bitmap? bitmap = null;
        try
        {
            bitmap = await Task.Run(() =>
            {
                if (ct.IsCancellationRequested) return null;

                try
                {
                    using var stream = new FileStream(
                        diskPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read | FileShare.Delete,
                        bufferSize: 4096,
                        useAsync: false);

                    return decodeWidth > 0
                        ? Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.LowQuality)
                        : new Bitmap(stream);
                }
                catch (Exception ex)
                {
                    Log.Debug($"[ImageCache] Decode failed: {ex.Message}");
                    try { File.Delete(diskPath); } catch { }
                    return null;
                }
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        if (bitmap != null && !ct.IsCancellationRequested)
            AddToMemoryCache(memKey, bitmap);

        return bitmap;
    }

    /// <summary>
    /// Downloads a file to disk via isolated ImageClient from <see cref="NetworkManager"/>.
    /// </summary>
    private async Task DownloadDirectToDiskAsync(string url, string finalPath, CancellationToken ct)
    {
        // Unique temporary file to prevent IOException during parallel downloads of the same URL
        var tmpPath = $"{finalPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            using var response = await _networkManager.ImageClient
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return;

            await using (var fs = new FileStream(
                tmpPath, FileMode.Create, FileAccess.Write,
                FileShare.None, 81920, useAsync: true))
            await using (var net = await response.Content
                .ReadAsStreamAsync(ct)
                .ConfigureAwait(false))
            {
                await net.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            if (File.Exists(finalPath))
            {
                try { File.Delete(tmpPath); } catch { }
                return;
            }

            try
            {
                File.Move(tmpPath, finalPath, overwrite: true);
                Interlocked.Add(ref _currentDiskCacheBytes, new FileInfo(finalPath).Length);
                Interlocked.Increment(ref _currentDiskFileCount);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException && File.Exists(finalPath))
            {
                try { File.Delete(tmpPath); } catch { }
            }
        }
        catch
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Adds a bitmap to the memory cache with LRU eviction.
    /// GC pressure calls are moved OUTSIDE <c>_lruLock</c>:
    /// p/invoke into CLR runtime must not block concurrent cache readers.
    /// </summary>
    /// <summary>
    /// Adds a bitmap to the memory cache with LRU eviction.
    /// Releasing evicted native buffers and GC pressure calls are moved OUTSIDE <c>_lruLock</c>:
    /// p/invoke into CLR runtime and Skia must not block concurrent cache readers.
    /// </summary>
    private void AddToMemoryCache(ulong key, Bitmap bitmap)
    {
        var entry = new RefCountedBitmap(bitmap);
        long estimatedBytes = entry.EstimatedBytes;
        bool added = false;
        long evictedBytes = 0;
        List<RefCountedBitmap>? evictedEntries = null;

        // LRU eviction under lock
        lock (_lruLock)
        {
            while ((_memoryCache.Count >= MaxMemoryItems ||
                    _currentMemoryCacheBytes + estimatedBytes > MaxMemoryBytes)
                && _lruOrder.Last != null)
            {
                evictedBytes += EvictLastUnsafe(out var removed);
                if (removed != null)
                {
                    evictedEntries ??= new List<RefCountedBitmap>(4);
                    evictedEntries.Add(removed);
                }
            }

            if (!_memoryCache.ContainsKey(key))
            {
                _memoryCache[key] = entry;
                var node = _lruOrder.AddFirst(key);
                _lruIndex[key] = node;
                Interlocked.Add(ref _currentMemoryCacheBytes, estimatedBytes);
                added = true;
            }
            else
            {
                entry.Dispose();
            }
        }

        // --- Native memory release and GC pressure outside lock ---
        if (evictedEntries != null)
        {
            for (int i = 0; i < evictedEntries.Count; i++)
                evictedEntries[i].Dispose();
        }

        if (evictedBytes > 0)
            GC.RemoveMemoryPressure(evictedBytes);

        if (added)
            GC.AddMemoryPressure(estimatedBytes);
    }

    /// <summary>
    /// Called with <c>_lruLock</c> held.
    /// Returns the size of the evicted bitmap and the object itself for subsequent
    /// <see cref="RefCountedBitmap.Dispose"/> and <see cref="GC.RemoveMemoryPressure"/> outside lock.
    /// </summary>
    private long EvictLastUnsafe(out RefCountedBitmap? removed)
    {
        var lastNode = _lruOrder.Last!;
        var key = lastNode.Value;
        _lruOrder.RemoveLast();
        _lruIndex.Remove(key);

        if (_memoryCache.Remove(key, out removed) && removed != null)
        {
            var bytes = removed.EstimatedBytes;
            Interlocked.Add(ref _currentMemoryCacheBytes, -bytes);
            return bytes;
        }

        removed = null;
        return 0;
    }

    /// <summary>
    /// Called with _lruLock already held.
    /// Moves key to the head of LRU. O(1).
    /// </summary>
    private void TouchLruUnsafe(ulong key)
    {
        if (_lruIndex.TryGetValue(key, out var node))
        {
            _lruOrder.Remove(node);
            _lruOrder.AddFirst(node);
        }
    }

    /// <summary>
    /// Forcefully brings RAM image cache within specified <see cref="MaxMemoryItems"/> and <see cref="MaxMemoryBytes"/> limits.
    /// </summary>
    /// <param name="aggressive">If <c>true</c>, additionally halves RAM cache under high memory pressure.</param>
    private void EnforceMemoryLimits(bool aggressive = false)
    {
        List<RefCountedBitmap>? evictedEntries = null;
        long evictedBytes = 0;

        lock (_lruLock)
        {
            int targetItems = MaxMemoryItems;
            long targetBytes = MaxMemoryBytes;

            if (aggressive)
                targetItems = Math.Min(targetItems, _memoryCache.Count / 2);

            while ((_memoryCache.Count > targetItems || _currentMemoryCacheBytes > targetBytes)
                   && _lruOrder.Last != null)
            {
                evictedBytes += EvictLastUnsafe(out var removed);
                if (removed != null)
                {
                    evictedEntries ??= new List<RefCountedBitmap>(4);
                    evictedEntries.Add(removed);
                }
            }
        }

        if (evictedEntries != null)
        {
            for (int i = 0; i < evictedEntries.Count; i++)
                evictedEntries[i].Dispose();
        }

        if (evictedBytes > 0)
            GC.RemoveMemoryPressure(evictedBytes);
    }

    private async Task PerformMaintenanceAsync()
    {
        var memInfo = GC.GetGCMemoryInfo();
        bool highLoad = memInfo.MemoryLoadBytes > memInfo.HighMemoryLoadThresholdBytes * 0.85;
        EnforceMemoryLimits(aggressive: highLoad);

        long limitBytes = (long)_library.Settings.Storage.ImageCacheLimitMb * 1024 * 1024;
        if (Volatile.Read(ref _currentDiskCacheBytes) > limitBytes)
            await CleanupDiskCacheAsync(limitBytes).ConfigureAwait(false);
    }

    /// <summary>
    /// Cleans up disk cache by file modification time down to 70% of configured quota.
    /// </summary>
    private async Task CleanupDiskCacheAsync(long limitBytes)
    {
        await Task.Run(() =>
        {
            try
            {
                var files = new DirectoryInfo(G.Folder.ImageCache)
                    .GetFiles()
                    .Where(static f => !f.Extension.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(static f => f.LastWriteTimeUtc)
                    .ToList();

                long targetSize = (long)(limitBytes * 0.7);
                long deletedBytes = 0;
                int deletedCount = 0;

                foreach (var file in files)
                {
                    if (Volatile.Read(ref _currentDiskCacheBytes) - deletedBytes <= targetSize) break;

                    try
                    {
                        var size = file.Length;
                        file.Delete();
                        deletedBytes += size;
                        deletedCount++;
                    }
                    catch { }
                }

                if (deletedBytes > 0)
                    Interlocked.Add(ref _currentDiskCacheBytes, -deletedBytes);
                if (deletedCount > 0)
                    Interlocked.Add(ref _currentDiskFileCount, -deletedCount);
            }
            catch { }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Completely clears memory cache and deterministically disposes all native Skia buffers.
    /// </summary>
    public void ClearMemoryCache()
    {
        long totalBytes;
        RefCountedBitmap[]? toDispose = null;

        lock (_lruLock)
        {
            totalBytes = _currentMemoryCacheBytes;
            if (_memoryCache.Count > 0)
            {
                toDispose = new RefCountedBitmap[_memoryCache.Count];
                _memoryCache.Values.CopyTo(toDispose, 0);
            }

            _memoryCache.Clear();
            _lruOrder.Clear();
            _lruIndex.Clear();
            Volatile.Write(ref _currentMemoryCacheBytes, 0);
        }

        // Deterministic release of Skia native memory outside lock
        if (toDispose != null)
        {
            for (int i = 0; i < toDispose.Length; i++)
                toDispose[i].Dispose();
        }

        if (totalBytes > 0)
            GC.RemoveMemoryPressure(totalBytes);
    }

    public async Task ClearDiskCacheAsync()
    {
        ClearMemoryCache();
        await Task.Run(() =>
        {
            foreach (var f in Directory.GetFiles(G.Folder.ImageCache))
                try { File.Delete(f); } catch { }

            Volatile.Write(ref _currentDiskCacheBytes, 0);
            Volatile.Write(ref _currentDiskFileCount, 0);
        });
    }

    private async Task InitializeDiskCacheAsync()
    {
        try
        {
            var files = new DirectoryInfo(G.Folder.ImageCache)
                .GetFiles()
                .Where(static f => !f.Extension.EndsWith(".tmp"))
                .ToArray();

            long total = 0;
            for (int i = 0; i < files.Length; i++)
                total += files[i].Length;

            Volatile.Write(ref _currentDiskCacheBytes, total);
            Volatile.Write(ref _currentDiskFileCount, files.Length);
        }
        catch { }
    }

    /// <summary>
    /// Returns the full path to the disk cache file for the specified URL (using a unified FNV-1a 64-bit key).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string GetDiskCachePath(ReadOnlySpan<char> url)
    {
        var diskHash = ComputeDiskKeyHash(url);
        return Path.Combine(G.Folder.ImageCache, diskHash.ToString("X16"));
    }

    /// <summary>
    /// Ensures the cover image file is downloaded to disk without decoding the raster into RAM.
    /// </summary>
    public Task EnsureDiskFileAsync(string url, CancellationToken ct = default)
        => EnsureDiskCachedAsync(url, ct);

    #region Cache Key Hashing (FNV-1a 64-bit → ulong)

    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>
    /// FNV-1a 64-bit hash of the URL. Used as a disk cache key.
    /// Zero-alloc: returns ulong, string is created only for the file name on disk (cold path).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ComputeDiskKeyHash(ReadOnlySpan<char> url)
    {
        ulong hash = FnvOffsetBasis;
        foreach (char c in url)
        {
            hash ^= (byte)c;
            hash *= FnvPrime;
            hash ^= (byte)(c >> 8);
            hash *= FnvPrime;
        }
        return hash;
    }

    /// <summary>
    /// FNV-1a 64-bit hash of the URL + width. Width normalization is performed by the calling code
    /// once: <see cref="GetImageAsync"/> normalizes before calling this method.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ComputeMemoryKeyHash(ReadOnlySpan<char> url, int normalizedWidth)
    {
        ulong hash = FnvOffsetBasis;

        foreach (char c in url)
        {
            hash ^= (byte)c;
            hash *= FnvPrime;
            hash ^= (byte)(c >> 8);
            hash *= FnvPrime;
        }

        hash ^= (byte)'_';
        hash *= FnvPrime;
        hash ^= (byte)(normalizedWidth & 0xFF);
        hash *= FnvPrime;
        hash ^= (byte)((normalizedWidth >> 8) & 0xFF);
        hash *= FnvPrime;

        return hash;
    }

    #endregion

    public (int MemoryItems, long MemoryMb, int DiskFiles, long DiskMb) GetStats()
    {
        int memItems;
        lock (_lruLock) memItems = _memoryCache.Count;
        return (memItems, _currentMemoryCacheBytes / 1024 / 1024, Volatile.Read(ref _currentDiskFileCount), _currentDiskCacheBytes / 1024 / 1024);
    }

    /// <summary>
    /// Immediately applies the RAM element limit and triggers a background disk quota check.
    /// </summary>
    public void EnforceLimits()
    {
        EnforceMemoryLimits(aggressive: false);
        _ = PerformMaintenanceAsync();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _appCts.Cancel();
        _appCts.Dispose();
        ClearMemoryCache();
        _downloadSemaphore.Dispose();
    }
}