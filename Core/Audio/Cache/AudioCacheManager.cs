using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using LMP.Core.Audio.Interfaces;
using static LMP.Core.Audio.AudioConstants;
using LMP.Core.Audio.Normalization;
using MemoryPack;

namespace LMP.Core.Audio.Cache;

public sealed partial class AudioCacheManager : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Current cache metadata schema version.
    /// </summary>
    private const int CurrentSchemaVersion = 4;

    /// <summary>
    /// Binary cache index file name.
    /// </summary>
    private const string CacheMetadataBinFileName = "cache_index.bin";

    /// <summary>
    /// Known standard YouTube format and bitrate combinations for orphan file recovery.
    /// </summary>
    private static readonly (AudioFormat Format, int Bitrate, AudioCodec Codec)[] KnownFormatProfiles =
     [
         (AudioFormat.WebM, 160, AudioCodec.Opus),
        (AudioFormat.WebM, 70, AudioCodec.Opus),
        (AudioFormat.WebM, 50, AudioCodec.Opus),
        (AudioFormat.Mp4, 128, AudioCodec.Aac),
        (AudioFormat.Ogg, 160, AudioCodec.Opus)
     ];

    /// <summary>
    /// Cache index wrapper with schema versioning.
    /// </summary>
    [MemoryPackable]
    public sealed partial class AudioCacheIndexEnvelope
    {
        /// <summary>Metadata schema version.</summary>
        public int SchemaVersion { get; set; }

        /// <summary>Cache entries.</summary>
        public List<AudioCacheEntry> Entries { get; set; } = [];
    }

    private readonly string _cacheDirectory;
    private long _maxCacheSize;
    private readonly ConcurrentDictionary<string, AudioCacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _trackIndex = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly SemaphoreSlim _evictLock = new(1, 1);
    private readonly SemaphoreSlim _maintenanceLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CacheFileHandle> _fileHandles = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _timerCts = new();
    private readonly Task _autoSaveTask;
    private volatile bool _disposed;
    private int _dirty;

    public event Action<string, AudioFormat, int, bool>? OnFormatCached;
    public event Action? OnCacheCleared;
    public event Action<string>? OnEntryEvicted;

    public AudioCacheManager(string? cacheDirectory = null, long maxCacheSizeMb = 2048)
    {
        _cacheDirectory = cacheDirectory ?? G.Folder.AudioCache;
        _maxCacheSize = maxCacheSizeMb * 1024 * 1024;
        Directory.CreateDirectory(_cacheDirectory);
        LoadIndex();
        _autoSaveTask = AutoSaveLoopAsync(_timerCts.Token);
        Log.Info($"[AudioCache] Initialized: {_cacheDirectory}, max={maxCacheSizeMb}MB, entries={_entries.Count}");
    }

    #region Public API

    public bool IsTrackFullyCached(string trackId) => FindBestCache(trackId) != null;

    public AudioCacheEntry? FindBestCacheByTrackId(string trackId) => FindBestCache(trackId);

    /// <summary>
    /// Dynamically updates the disk audio cache limit in megabytes and triggers a quota check.
    /// </summary>
    /// <param name="limitMb">New limit in megabytes.</param>
    public void UpdateMaxCacheSizeMb(int limitMb)
    {
        if (limitMb <= 0) return;
        Volatile.Write(ref _maxCacheSize, (long)limitMb * 1024 * 1024);
        _ = EnforceLimitsAsync();
    }

    /// <summary>
    /// Hydrates the caching status for the specified list of tracks.
    /// </summary>
    /// <param name="tracks">List of tracks for cache validation.</param>
    public void HydrateCacheStatus(IEnumerable<TrackInfo> tracks)
    {
        var trackMap = new Dictionary<string, List<TrackInfo>>(StringComparer.Ordinal);
        var durationMap = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);

        foreach (var track in tracks)
        {
            if (track.IsDownloaded || track.IsCached || string.IsNullOrEmpty(track.Id))
                continue;

            if (!trackMap.TryGetValue(track.Id, out var list))
            {
                list = new List<TrackInfo>(1);
                trackMap[track.Id] = list;
                if (track.Duration > TimeSpan.Zero)
                    durationMap[track.Id] = track.Duration;
            }

            list.Add(track);
        }

        if (trackMap.Count == 0) return;

        // Self-Healing: Check for orphan files with size verification
        bool orphansRecovered = RecoverOrphansForTracks(trackMap.Keys, durationMap);

        foreach (var (trackId, tracksList) in trackMap)
        {
            if (!_trackIndex.TryGetValue(trackId, out var keys)) continue;

            AudioCacheEntry? bestEntry = null;

            foreach (var key in keys.Keys)
            {
                if (_entries.TryGetValue(key, out var entry)
                    && entry.IsComplete
                    && EnsureCacheFileIntegrity(entry)
                    && (bestEntry == null || entry.Bitrate > bestEntry.Bitrate))
                {
                    bestEntry = entry;
                }
            }

            if (bestEntry != null)
            {
                foreach (var track in tracksList)
                    track.MarkAsCached(bestEntry.Format, bestEntry.Bitrate);
            }
        }

        if (orphansRecovered)
            _ = SaveIndexAsync();
    }

    /// <summary>
    /// Scans the disk for orphan files matching the provided trackIds and restores them into the index,
    /// validating their size against bitrate and track duration.
    /// </summary>
    /// <param name="trackIds">List of track identifiers.</param>
    /// <param name="trackDurations">Optional dictionary of track durations for sanity check.</param>
    /// <returns><c>true</c> if at least one valid entry was restored.</returns>
    internal bool RecoverOrphansForTracks(
         IEnumerable<string> trackIds,
         IReadOnlyDictionary<string, TimeSpan>? trackDurations = null)
    {
        const long FallbackCompleteMinBytes = 350 * 1024; // 350 KB (~22s audio)
        bool anyRecovered = false;

        HashSet<string> filesOnDisk;
        try
        {
            if (!Directory.Exists(_cacheDirectory))
                return false;

            var paths = Directory.GetFiles(_cacheDirectory, "*" + CacheFileExtension);
            if (paths.Length == 0)
                return false;

            filesOnDisk = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }

        foreach (var rawTrackId in trackIds)
        {
            if (string.IsNullOrEmpty(rawTrackId)) continue;

            string trackId = rawTrackId.StartsWith("yt_", StringComparison.Ordinal)
                ? rawTrackId
                : string.Concat("yt_", rawTrackId);

            TimeSpan duration = TimeSpan.Zero;
            if (trackDurations != null && (!trackDurations.TryGetValue(rawTrackId, out duration) && !trackDurations.TryGetValue(trackId, out duration)))
            {
                duration = TimeSpan.Zero;
            }

            foreach (var (format, bitrate, codec) in KnownFormatProfiles)
            {
                string cacheKey = AudioSourceFactory.BuildCacheKey(trackId, format, bitrate);

                // CRITICAL: If the entry is ALREADY known in the index (even with IsComplete == false, i.e., partial cache with holes from Seek),
                // it is NOT an orphan file! Never promote a partial entry to IsComplete based on fi.Length (in NTFS fi.Length equals the maximum write offset!).
                if (_entries.ContainsKey(cacheKey))
                    continue;

                string filePath = GetCachePath(cacheKey);
                if (!filesOnDisk.Contains(filePath))
                    continue;

                try
                {
                    var fi = new FileInfo(filePath);
                    if (!fi.Exists)
                        continue;

                    long fileLength = fi.Length;
                    DateTime creationUtc = fi.CreationTimeUtc;
                    DateTime lastWriteUtc = fi.LastWriteTimeUtc;
                    long minExpectedBytes;

                    if (duration > TimeSpan.FromSeconds(5))
                    {
                        // 65% of nominal value accounting for VBR encoding
                        minExpectedBytes = (long)(duration.TotalSeconds * bitrate * 1000.0 / 8.0 * 0.65);
                    }
                    else
                    {
                        minExpectedBytes = FallbackCompleteMinBytes;
                    }

                    // If the file is smaller than a full track threshold or contains undownloaded zero-byte holes in the middle —
                    // delete such an unowned fragment that lacks an index range map.
                    if (fileLength < minExpectedBytes || !VerifyOrphanFileNoZeroHoles(filePath, fileLength))
                    {
                        filesOnDisk.Remove(filePath);
                        try { fi.Delete(); } catch { }
                        Log.Warn($"[AudioCache] Incomplete/sparse orphan file deleted: {cacheKey} ({fileLength / 1024}KB)");
                        continue;
                    }

                    var entry = _entries.GetOrAdd(cacheKey, _ => new AudioCacheEntry
                    {
                        CacheKey = cacheKey,
                        TrackId = trackId,
                        OriginalUrl = string.Empty,
                        TotalSize = fileLength,
                        Format = format,
                        Codec = codec,
                        Bitrate = bitrate,
                        DurationMs = (long)duration.TotalMilliseconds,
                        AlignmentBytes = ChunkSize,
                        CreatedAt = creationUtc,
                        LastAccessedAt = DateTime.UtcNow,
                        CompletedAt = lastWriteUtc,
                        IsComplete = true,
                        ActualFileSize = fileLength
                    });

                    entry.TotalSize = fileLength;
                    entry.ActualFileSize = fileLength;
                    entry.IsComplete = true;
                    if (duration > TimeSpan.Zero)
                        entry.DurationMs = (long)duration.TotalMilliseconds;
                    entry.CompletedAt = lastWriteUtc;
                    entry.LastAccessedAt = DateTime.UtcNow;
                    entry.MarkFullyDownloaded();

                    AddToTrackIndex(trackId, cacheKey);
                    MarkDirty();
                    anyRecovered = true;

                    Log.Info($"[AudioCache] Orphaned cache file verified and recovered: {cacheKey} ({fileLength / 1024} KB)");
                }
                catch (Exception ex)
                {
                    Log.Warn($"[AudioCache] Failed to probe orphan file {filePath}: {ex.Message}");
                }
            }
        }

        return anyRecovered;
    }

    /// <summary>
    /// Verifies that an orphan file without an index entry contains no undownloaded zero-byte holes (from random-access Seek writes)
    /// at the start, 25%, 50%, 75%, and end of the file.
    /// </summary>
    private static bool VerifyOrphanFileNoZeroHoles(string filePath, long fileLength)
    {
        const int ProbeSize = 4096;
        if (fileLength < ProbeSize * 2) return false;

        Span<byte> probe = stackalloc byte[ProbeSize];
        ReadOnlySpan<double> checkpoints = [0.0, 0.25, 0.50, 0.75];

        try
        {
            using var handle = File.OpenHandle(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            for (int i = 0; i < checkpoints.Length; i++)
            {
                long offset = Math.Clamp((long)(fileLength * checkpoints[i]), 0, fileLength - ProbeSize);
                int read = RandomAccess.Read(handle, probe, offset);
                if (read < ProbeSize || !probe[..read].ContainsAnyExcept((byte)0))
                    return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal bool IsFullyCached(string cacheKey) =>
        _entries.TryGetValue(cacheKey, out var entry)
        && entry.IsComplete
        && EnsureCacheFileIntegrity(entry);

    /// <summary>
    /// Returns the best local cache for fast startup of a partially cached track.
    /// </summary>
    internal AudioCacheEntry? FindBestStartupCache(string trackId, int minContiguousBytes) =>
        TryWithIdVariants(trackId, id => FindBestStartupCacheCore(id, minContiguousBytes));

    /// <summary>
    /// Returns the best fully cached version of a track.
    /// </summary>
    internal AudioCacheEntry? FindBestCache(string trackId) =>
        TryWithIdVariants(trackId, FindBestCacheCore);

    /// <summary>
    /// Updates integrated loudness across ALL cache entries for the given track.
    /// </summary>
    internal bool TryUpdateIntegratedLufs(string trackId, float integratedLufs, LoudnessSource source)
    {
        if (string.IsNullOrEmpty(trackId) || !float.IsFinite(integratedLufs))
            return false;

        var keys = CollectAllCacheKeysForTrack(trackId);
        if (keys.Count == 0)
            return false;

        bool anyUpdated = false;

        foreach (var cacheKey in keys)
        {
            if (!_entries.TryGetValue(cacheKey, out var entry))
                continue;

            var existingSource = (LoudnessSource)entry.IntegratedLufsSource;

            if (existingSource > source)
                continue;

            if (entry.IntegratedLufs is float existing
                && MathF.Abs(existing - integratedLufs) < 0.01f
                && existingSource == source)
            {
                continue;
            }

            entry.IntegratedLufs = integratedLufs;
            entry.IntegratedLufsSource = (int)source;
            entry.LastAccessedAt = DateTime.UtcNow;
            MarkDirty();
            anyUpdated = true;
        }

        if (anyUpdated)
            _ = SaveIndexAsync();

        return anyUpdated;
    }

    /// <summary>
    /// Collects all cacheKeys for a track, including ID variants with and without domain prefixes.
    /// </summary>
    private List<string> CollectAllCacheKeysForTrack(string trackId)
    {
        var keys = new List<string>(4);

        TryWithIdVariants<object>(trackId, id =>
        {
            AppendKeysFromIndex(id, keys);
            return null;
        });

        return keys;
    }

    /// <summary>
    /// Adds cacheKey from trackIndex to the list.
    /// </summary>
    private void AppendKeysFromIndex(string trackId, List<string> target)
    {
        if (_trackIndex.TryGetValue(trackId, out var index))
        {
            foreach (var key in index.Keys)
                target.Add(key);
        }
    }

    private AudioCacheEntry? FindBestStartupCacheCore(string trackId, int minContiguousBytes)
    {
        if (string.IsNullOrEmpty(trackId)) return null;
        if (!_trackIndex.TryGetValue(trackId, out var keys)) return null;

        AudioCacheEntry? best = null;
        long bestContiguous = long.MinValue;

        foreach (var key in keys.Keys)
        {
            if (!_entries.TryGetValue(key, out var entry))
                continue;

            string path = GetCachePath(entry);
            if (!File.Exists(path))
                continue;

            if (entry.IsComplete && !EnsureCacheFileIntegrity(entry))
                continue;

            long contiguous = entry.IsComplete
                ? entry.TotalSize
                : entry.GetContiguousDownloadedBytesFrom(0);

            if (contiguous < minContiguousBytes)
                continue;

            if (best == null
                || contiguous > bestContiguous
                || (contiguous == bestContiguous && entry.Bitrate > best.Bitrate))
            {
                best = entry;
                bestContiguous = contiguous;
            }
        }

        return best;
    }

    private AudioCacheEntry? FindBestCacheCore(string trackId)
    {
        if (string.IsNullOrEmpty(trackId)) return null;
        if (!_trackIndex.TryGetValue(trackId, out var keys)) return null;

        AudioCacheEntry? best = null;

        foreach (var key in keys.Keys)
        {
            if (_entries.TryGetValue(key, out var entry)
                && entry.IsComplete
                && EnsureCacheFileIntegrity(entry)
                && (best == null || entry.Bitrate > best.Bitrate))
            {
                best = entry;
            }
        }

        return best;
    }

    /// <summary>
    /// Applies the search function <paramref name="find"/> to direct trackId, raw ID, and yt_-prefixed ID,
    /// returning the first non-null result.
    /// </summary>
    /// <remarks>
    /// Eliminates duplication of the "direct → raw → yt_ prefix" pattern from
    /// <see cref="FindBestCache"/>, <see cref="FindBestStartupCache"/>,
    /// and <see cref="CollectAllCacheKeysForTrack"/>.
    /// </remarks>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="trackId">Source track identifier.</param>
    /// <param name="find">Search function for a specific ID.</param>
    /// <returns>First non-null result or <c>null</c>.</returns>
    private static T? TryWithIdVariants<T>(string trackId, Func<string, T?> find) where T : class
    {
        var result = find(trackId);
        if (result != null) return result;

        var rawId = TryGetRawTrackId(trackId);
        if (!string.IsNullOrEmpty(rawId))
        {
            result = find(rawId);
            if (result != null) return result;
        }

        if (!IsPrefixedTrackId(trackId))
            return find(string.Concat("yt_", trackId));

        return null;
    }

    /// <summary>
    /// Determines whether the identifier contains a domain prefix.
    /// </summary>
    private static bool IsPrefixedTrackId(string trackId)
    {
        if (string.IsNullOrEmpty(trackId))
            return false;

        var span = trackId.AsSpan();
        return span.StartsWith("yt_".AsSpan()) || span.StartsWith("yt_pl_".AsSpan());
    }

    /// <summary>
    /// Returns the raw YouTube ID without the domain prefix, if present.
    /// </summary>
    private static string? TryGetRawTrackId(string trackId)
    {
        if (string.IsNullOrEmpty(trackId))
            return null;

        var span = trackId.AsSpan();

        if (span.StartsWith("yt_pl_".AsSpan()))
            return span[6..].ToString();

        if (span.StartsWith("yt_".AsSpan()))
            return span[3..].ToString();

        return null;
    }

    internal AudioCacheEntry? GetCacheInfo(string cacheKey) =>
        _entries.TryGetValue(cacheKey, out var entry) ? entry : null;

    internal string GetCachePath(AudioCacheEntry entry) =>
        entry.CachedFilePath ??= GetCachePath(entry.CacheKey);

    internal string GetCachePath(string cacheKey)
    {
        Span<byte> utf8Buf = cacheKey.Length <= 64
            ? stackalloc byte[256]
            : new byte[Encoding.UTF8.GetMaxByteCount(cacheKey.Length)];

        int written = Encoding.UTF8.GetBytes(cacheKey.AsSpan(), utf8Buf);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(utf8Buf[..written], hash);

        Span<char> fileName = stackalloc char[16 + CacheFileExtension.Length];
        Convert.TryToHexString(hash[..8], fileName, out _);
        CacheFileExtension.AsSpan().CopyTo(fileName[16..]);
        return Path.Join(_cacheDirectory, fileName);
    }

    internal void Touch(string cacheKey)
    {
        if (_entries.TryGetValue(cacheKey, out var entry))
        {
            entry.LastAccessedAt = DateTime.UtcNow;
            MarkDirty();
        }
    }

    /// <summary>
    /// Creates or updates a range-based cache metadata entry.
    /// </summary>
    internal AudioCacheEntry CreateOrUpdate(
        string cacheKey,
        string trackId,
        string url,
        long totalSize,
        AudioFormat format,
        AudioCodec codec,
        int bitrate = 0,
        long durationMs = -1,
        int alignmentBytes = ChunkSize)
    {
        var entry = _entries.GetOrAdd(cacheKey, _ => new AudioCacheEntry
        {
            CacheKey = cacheKey,
            TrackId = trackId,
            OriginalUrl = url,
            TotalSize = totalSize,
            Format = format,
            Codec = codec,
            Bitrate = bitrate,
            DurationMs = durationMs,
            AlignmentBytes = alignmentBytes,
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow
        });

        entry.OriginalUrl = url;
        entry.LastAccessedAt = DateTime.UtcNow;

        if (totalSize > 0 && (entry.TotalSize <= 0 || (entry.TotalSize < totalSize && !entry.IsComplete)))
            entry.TotalSize = totalSize;

        if (bitrate > 0)
            entry.Bitrate = bitrate;

        if (entry.DownloadedBytes == 0 && alignmentBytes > 0)
            entry.AlignmentBytes = alignmentBytes;

        AddToTrackIndex(trackId, cacheKey);
        MarkDirty();
        return entry;
    }

    private void RemoveCache(string cacheKey) => RemoveCacheCore(cacheKey, saveIndex: true);

    private void RemoveCacheCore(string cacheKey, bool saveIndex)
    {
        if (!_entries.TryRemove(cacheKey, out var entry)) return;

        RemoveFromTrackIndex(entry.TrackId, cacheKey);
        _fileLocks.TryRemove(cacheKey, out _);

        var filePath = GetCachePath(entry);
        if (_fileHandles.TryRemove(cacheKey, out var handle))
        {
            handle.MarkForDeleteOnClose();
        }
        else
        {
            try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
        }

        MarkDirty();
        if (saveIndex)
            _ = SaveIndexAsync();
    }

    private void InvalidateCompleteEntry(AudioCacheEntry entry)
    {
        entry.IsComplete = false;
        entry.CompletedAt = null;
        entry.ResetDownloadedRanges();
        entry.ActualFileSize = 0;
        ForceCloseHandle(entry.CacheKey);
        MarkDirty();
        _ = SaveIndexAsync();
    }

    /// <summary>
    /// Writes an arbitrary byte range to a cache file.
    /// </summary>
    internal async ValueTask WriteRangeAsync(
        string cacheKey,
        long offset,
        ReadOnlyMemory<byte> data,
        CancellationToken ct = default)
    {
        if (_disposed || !_entries.TryGetValue(cacheKey, out var entry)) return;
        if (data.IsEmpty) return;
        if (offset < 0 || offset >= entry.TotalSize) return;

        long remaining = entry.TotalSize - offset;
        if (remaining <= 0) return;

        if (data.Length > remaining)
            data = data[..(int)remaining];

        var fileLock = _fileLocks.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        try
        {
            await fileLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            return;
        }

        if (_disposed || !_entries.TryGetValue(cacheKey, out var currentEntry) || !ReferenceEquals(currentEntry, entry))
        {
            try { fileLock.Release(); } catch (ObjectDisposedException) { }
            return;
        }

        var fileHandle = GetFileHandle(cacheKey);
        fileHandle.BeginIo();
        bool completedNow = false;

        try
        {
            var handle = fileHandle.GetOrOpen();
            await RandomAccess.WriteAsync(handle, data, offset, ct).ConfigureAwait(false);

            entry.MarkRangeDownloaded(offset, data.Length);
            entry.LastAccessedAt = DateTime.UtcNow;
            MarkDirty();

            long writtenEnd = offset + data.Length;
            if (writtenEnd > entry.ActualFileSize)
                entry.ActualFileSize = writtenEnd;

            if (!entry.IsComplete && entry.DownloadedBytes >= entry.TotalSize)
            {
                entry.IsComplete = true;
                entry.CompletedAt = DateTime.UtcNow;
                entry.ActualFileSize = Math.Max(entry.ActualFileSize, entry.TotalSize);
                completedNow = true;
                Log.Info($"[AudioCache] Track fully cached: {cacheKey}");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Log.Warn($"[AudioCache] Write range failed: {ex.Message}"); }
        finally
        {
            fileHandle.EndIo();
            try { fileLock.Release(); } catch (ObjectDisposedException) { }
        }

        if (completedNow)
        {
            RaiseFormatCached(entry);
            _ = EnforceLimitsAsync();
        }
    }

    /// <summary>
    /// Reads a byte range from a cache file directly into the caller's buffer,
    /// bypassing intermediate renting and re-copying.
    /// </summary>
    /// <param name="cacheKey">Cache entry key.</param>
    /// <param name="offset">Absolute start offset of the range.</param>
    /// <param name="destination">Destination buffer; its length defines the read length.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Number of bytes read (buffer length truncated at end-of-file);
    /// <c>0</c> if the range is not cached, the file is unavailable, or read incomplete.
    /// </returns>
    internal async ValueTask<int> ReadRangeIntoAsync(
        string cacheKey,
        long offset,
        Memory<byte> destination,
        CancellationToken ct = default)
    {
        if (!_entries.TryGetValue(cacheKey, out var entry)) return 0;
        if (offset < 0 || destination.IsEmpty) return 0;

        long remaining = entry.TotalSize - offset;
        if (remaining <= 0) return 0;

        int expectedLength = destination.Length;
        if (expectedLength > remaining)
            expectedLength = (int)remaining;
        if (!entry.IsRangeDownloaded(offset, expectedLength)) return 0;

        var fileHandle = TryGetFileHandle(cacheKey);
        if (fileHandle == null) return 0;

        fileHandle.BeginIo();
        try
        {
            var handle = fileHandle.GetOrOpen(FileMode.Open);
            var buffer = destination[..expectedLength];
            int totalRead = 0;

            while (totalRead < expectedLength)
            {
                int read = await RandomAccess.ReadAsync(
                    handle, buffer[totalRead..], offset + totalRead, ct).ConfigureAwait(false);
                if (read == 0) break;
                totalRead += read;
            }

            if (totalRead != expectedLength)
            {
                entry.InvalidateRange(offset, expectedLength);
                MarkDirty();
                Log.Warn($"[AudioCache] Short read range of {cacheKey}: " +
                         $"offset={offset}, expected={expectedLength}, got={totalRead}. Range invalidated.");
                return 0;
            }

            entry.LastAccessedAt = DateTime.UtcNow;
            return expectedLength;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return 0;
        }
        finally
        {
            fileHandle.EndIo();
        }
    }

    #endregion

    #region Lease API

    /// <summary>
    /// Registers a lifetime lease on a cache file.
    /// </summary>
    internal void AcquireLease(string cacheKey)
    {
        var entry = _fileHandles.GetOrAdd(cacheKey,
            key => new CacheFileHandle(GetCachePath(key)));
        entry.AddLease();
    }

    /// <summary>
    /// Releases a lifetime lease on a cache file.
    /// </summary>
    internal void ReleaseLease(string cacheKey)
    {
        if (_fileHandles.TryGetValue(cacheKey, out var entry))
            entry.RemoveLease();
    }

    #endregion

    #region Statistics

    /// <summary>
    /// Enumerates all complete and physically valid cache entries.
    /// </summary>
    internal IEnumerable<AudioCacheEntry> GetAllCompleteEntries()
    {
        foreach (var entry in _entries.Values)
        {
            if (entry.IsComplete && EnsureCacheFileIntegrity(entry))
                yield return entry;
        }
    }

    /// <summary>
    /// Returns compact cache statistics.
    /// </summary>
    public (int FileCount, int SizeMb) GetStatsCompact()
    {
        var stats = GetStats();
        int totalFiles = stats.CompleteEntries + stats.PartialEntries;
        return (totalFiles, (int)(stats.TotalSizeBytes / 1024 / 1024));
    }

    /// <summary>
    /// Gathers complete cache disk space usage statistics.
    /// </summary>
    public CacheStats GetStats()
    {
        long totalSize = 0;
        int completeCount = 0;
        int partialCount = 0;
        int totalCount = 0;

        foreach (var entry in _entries.Values)
        {
            if (entry.ActualFileSize <= 0)
                UpdateFileSizeCache(entry);

            if (entry.ActualFileSize > 0)
            {
                totalCount++;
                if (entry.IsComplete) completeCount++;
                else if (entry.DownloadedBytes > 0) partialCount++;
                totalSize += entry.ActualFileSize;
            }
        }

        return new CacheStats
        {
            TotalEntries = totalCount,
            CompleteEntries = completeCount,
            PartialEntries = partialCount,
            TotalSizeBytes = totalSize,
            MaxSizeBytes = Volatile.Read(ref _maxCacheSize)
        };
    }

    public static (int FileCount, int SizeMb) GetDownloadsStats(string? directoryPath = null)
    {
        try
        {
            var targetDir = string.IsNullOrEmpty(directoryPath) ? G.Folder.Downloads : directoryPath;
            var dir = new DirectoryInfo(targetDir);
            if (!dir.Exists) return (0, 0);

            var files = dir.GetFiles("*.*", SearchOption.TopDirectoryOnly);
            long totalBytes = 0;

            for (int i = 0; i < files.Length; i++)
                totalBytes += files[i].Length;

            return (files.Length, (int)(totalBytes / 1024 / 1024));
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioCache] GetDownloadsStats error: {ex.Message}");
            return (0, 0);
        }
    }

    public List<(AudioFormat Format, int Bitrate)> GetCachedFormats(string trackId)
    {
        var result = new List<(AudioFormat, int)>();
        if (!_trackIndex.TryGetValue(trackId, out var keys)) return result;

        foreach (var key in keys.Keys)
        {
            if (_entries.TryGetValue(key, out var entry)
                && entry.IsComplete
                && EnsureCacheFileIntegrity(entry))
            {
                result.Add((entry.Format, entry.Bitrate));
            }
        }

        return result;
    }

    #endregion

    #region Export to Downloads

    public async Task<bool> ExportTrackToDownloadsAsync(
        string trackId,
        Func<string, Task<TrackInfo?>> getTrackFunc,
        Func<TrackInfo, Task> updateTrackFunc,
        CancellationToken ct = default,
        string? targetDirectory = null)
    {
        var entry = FindBestCache(trackId);
        if (entry == null)
        {
            Log.Warn($"[AudioCache] Track {trackId} not fully cached, cannot export");
            return false;
        }

        return await PromoteCacheToDownloadsAsync(entry, getTrackFunc, updateTrackFunc, ct, targetDirectory).ConfigureAwait(false);
    }

    private async Task<bool> PromoteCacheToDownloadsAsync(
        AudioCacheEntry entry,
        Func<string, Task<TrackInfo?>> getTrackFunc,
        Func<TrackInfo, Task> updateTrackFunc,
        CancellationToken ct,
        string? targetDirectory = null)
    {
        if (!await _maintenanceLock.WaitAsync(1000, ct).ConfigureAwait(false))
            return false;

        try
        {
            var track = await getTrackFunc(entry.TrackId).ConfigureAwait(false);
            if (track == null)
            {
                Log.Warn($"[AudioCache] Track not found: {entry.TrackId}");
                return false;
            }

            if (track.IsDownloaded && !string.IsNullOrEmpty(track.LocalPath) && File.Exists(track.LocalPath))
            {
                Log.Debug($"[AudioCache] Already downloaded: {track.Title}");
                return true;
            }

            var cachePath = GetCachePath(entry);
            if (!File.Exists(cachePath))
            {
                Log.Warn($"[AudioCache] Cache file not found: {cachePath}");
                return false;
            }

            if (!entry.IsComplete || !EnsureCacheFileIntegrity(entry))
            {
                Log.Warn($"[AudioCache] Incomplete cache entry: {entry.CacheKey}");
                return false;
            }

            string ext = entry.Format switch
            {
                AudioFormat.WebM => "webm",
                AudioFormat.Mp4 => "m4a",
                AudioFormat.Ogg => "ogg",
                _ => "audio"
            };

            string safeName = SanitizeFileName($"{track.Author} - {track.Title}.{ext}");
            string targetFolder = string.IsNullOrEmpty(targetDirectory) ? G.Folder.Downloads : targetDirectory;
            string destPath = Path.Combine(targetFolder, safeName);

            if (File.Exists(destPath))
            {
                var existing = new FileInfo(destPath);
                if (existing.Length == entry.TotalSize)
                {
                    track.MarkAsDownloaded(destPath, entry.Format, entry.Bitrate);
                    await updateTrackFunc(track).ConfigureAwait(false);
                    return true;
                }

                var baseName = Path.GetFileNameWithoutExtension(safeName);
                destPath = Path.Combine(targetFolder, $"{baseName}_{entry.Bitrate}kbps.{ext}");
            }

            Log.Info($"[AudioCache] Exporting to Downloads: {Path.GetFileName(destPath)}");
            File.Copy(cachePath, destPath, overwrite: true);

            track.MarkAsDownloaded(destPath, entry.Format, entry.Bitrate);
            await updateTrackFunc(track).ConfigureAwait(false);
            OnFormatCached?.Invoke(entry.TrackId, entry.Format, entry.Bitrate, true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[AudioCache] Export failed: {ex.Message}");
            return false;
        }
        finally
        {
            _maintenanceLock.Release();
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", fileName.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
        return sanitized.Length > 200 ? sanitized[..200] : sanitized;
    }

    #endregion

    #region Clear & Maintenance

    /// <summary>
    /// Performs LRU cache file eviction until the allowed disk limit is satisfied.
    /// Does not delete files with active Leases or in-flight I/O operations.
    /// </summary>
    /// <param name="targetLimitBytes">Optional target cache size. Defaults to <see cref="_maxCacheSize"/> if omitted.</param>
    /// <param name="ct">Operation cancellation token.</param>
    /// <returns>Number of evicted cache entries.</returns>
    public async Task<int> EnforceLimitsAsync(long? targetLimitBytes = null, CancellationToken ct = default)
    {
        if (_disposed) return 0;

        long limit = targetLimitBytes ?? Volatile.Read(ref _maxCacheSize);
        if (limit <= 0) return 0;

        await _evictLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            limit = targetLimitBytes ?? Volatile.Read(ref _maxCacheSize);
            if (limit <= 0) return 0;

            var entriesSnapshot = _entries.Values.ToList();
            long currentTotalSize = 0;

            for (int i = 0; i < entriesSnapshot.Count; i++)
            {
                var entry = entriesSnapshot[i];
                if (entry.ActualFileSize <= 0)
                    UpdateFileSizeCache(entry);
                currentTotalSize += entry.ActualFileSize;
            }

            if (currentTotalSize <= limit)
                return 0;

            // Eviction priority:
            // 1. Incomplete (IsComplete == false) entries, ordered by last access time (oldest first).
            // 2. Complete entries, ordered by last access time (LRU).
            entriesSnapshot.Sort(static (a, b) =>
            {
                int compCmp = (a.IsComplete ? 1 : 0).CompareTo(b.IsComplete ? 1 : 0);
                return compCmp != 0 ? compCmp : a.LastAccessedAt.CompareTo(b.LastAccessedAt);
            });

            int evictedCount = 0;

            for (int i = 0; i < entriesSnapshot.Count && currentTotalSize > limit; i++)
            {
                if (ct.IsCancellationRequested) break;

                var candidate = entriesSnapshot[i];
                var key = candidate.CacheKey;

                if (_fileHandles.TryGetValue(key, out var handle))
                {
                    if (handle.LeaseCount > 0 || handle.ActiveIoCount > 0)
                    {
                        Log.Debug($"[AudioCache] Skipping eviction of leased/in-use cache key: {key}");
                        continue;
                    }
                }

                long size = candidate.ActualFileSize;
                string trackId = candidate.TrackId;

                RemoveCacheCore(key, saveIndex: false);

                currentTotalSize -= size;
                evictedCount++;

                try
                {
                    OnEntryEvicted?.Invoke(trackId);
                }
                catch (Exception ex)
                {
                    Log.Warn($"[AudioCache] OnEntryEvicted handler error: {ex.Message}");
                }

                Log.Info($"[AudioCache] Evicted LRU cache entry: {key} ({size / 1024} KB), remaining ~{currentTotalSize / 1024 / 1024} MB");
            }

            if (evictedCount > 0)
            {
                await SaveIndexAsync().ConfigureAwait(false);
            }

            return evictedCount;
        }
        finally
        {
            _evictLock.Release();
        }
    }

    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        if (!await _maintenanceLock.WaitAsync(5000, ct).ConfigureAwait(false))
        {
            Log.Warn("[AudioCache] ClearAllAsync: couldn't acquire lock");
            return;
        }

        bool saveLockAcquired = false;
        try
        {
            saveLockAcquired = await _saveLock.WaitAsync(5000, ct).ConfigureAwait(false);
            Interlocked.Exchange(ref _dirty, 0);

            Log.Info("[AudioCache] Clearing all cache...");
            _entries.Clear();
            _trackIndex.Clear();
            DisposeAllHandles();

            DeleteFilesInDirectory(new DirectoryInfo(_cacheDirectory), "AudioCache");

            Log.Info("[AudioCache] Cache cleared");
        }
        finally
        {
            if (saveLockAcquired)
                try { _saveLock.Release(); } catch (ObjectDisposedException) { }
            _maintenanceLock.Release();
        }

        try { OnCacheCleared?.Invoke(); }
        catch (Exception ex) { Log.Warn($"[AudioCache] OnCacheCleared handler error: {ex.Message}"); }
    }

    public static async Task ClearDownloadsAsync(string? directoryPath = null, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            try
            {
                var targetDir = string.IsNullOrEmpty(directoryPath) ? G.Folder.Downloads : directoryPath;
                var dir = new DirectoryInfo(targetDir);
                if (!dir.Exists) return;

                Log.Info($"[AudioCache] Clearing downloads folder: {targetDir}");
                DeleteFilesInDirectory(dir, "AudioCache");
                Log.Info("[AudioCache] Downloads cleared");
            }
            catch (Exception ex)
            {
                Log.Error($"[AudioCache] ClearDownloadsAsync error: {ex.Message}");
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes all files in the directory with error logging.
    /// </summary>
    /// <param name="dir">Directory.</param>
    /// <param name="logTag">Tag for the error log.</param>
    private static void DeleteFilesInDirectory(DirectoryInfo dir, string logTag)
    {
        if (!dir.Exists) return;

        foreach (var file in dir.GetFiles())
        {
            try { file.Delete(); }
            catch (Exception ex) { Log.Warn($"[{logTag}] Failed to delete {file.Name}: {ex.Message}"); }
        }
    }

    public void RemoveTrackCache(string trackId)
    {
        if (!_trackIndex.TryGetValue(trackId, out var keys)) return;

        var keysToRemove = keys.Keys.ToArray();
        for (int i = 0; i < keysToRemove.Length; i++)
            RemoveCache(keysToRemove[i]);

        Log.Debug($"[AudioCache] Removed {keysToRemove.Length} cache entries for track {trackId}");
    }

    /// <summary>
    /// Validates the physical integrity and plausibility of a complete cache.
    /// </summary>
    /// <param name="entry">Cache entry to validate.</param>
    /// <returns><c>true</c> if the file exists and its size is plausible.</returns>
    private bool EnsureCacheFileIntegrity(AudioCacheEntry entry)
    {
        if (!entry.IsComplete) return false;

        var filePath = GetCachePath(entry);

        try
        {
            if (!IsFilePhysicallyComplete(filePath, entry.TotalSize, out long actualLength))
            {
                if (actualLength == 0)
                {
                    InvalidateCompleteEntry(entry);
                    return false;
                }

                Log.Warn($"[AudioCache] Truncated cache file: {entry.CacheKey} " +
                         $"(disk={actualLength / 1024}KB, expected={entry.TotalSize / 1024}KB)");
                InvalidateCompleteEntry(entry);

                try { File.Delete(filePath); }
                catch (Exception ex) { Log.Warn($"[AudioCache] Failed to delete truncated cache file: {ex.Message}"); }

                return false;
            }

            // Sanity check: file size cannot be absurdly small relative to duration
            if (entry.DurationMs > 10_000 && entry.Bitrate > 0)
            {
                long minPlausibleBytes = (long)(entry.DurationMs / 1000.0 * entry.Bitrate * 1000.0 / 8.0 * 0.50);
                if (actualLength < minPlausibleBytes)
                {
                    Log.Warn($"[AudioCache] Implausibly small cache file rejected: {entry.CacheKey} " +
                             $"(disk={actualLength / 1024}KB < minPlausible={minPlausibleBytes / 1024}KB for {entry.DurationMs}ms)");
                    InvalidateCompleteEntry(entry);

                    try { File.Delete(filePath); }
                    catch (Exception ex) { Log.Warn($"[AudioCache] Failed to delete corrupt cache file: {ex.Message}"); }

                    return false;
                }
            }

            // Verify physical absence of 4KB zero holes ONLY for old orphan entries
            // (which have empty OriginalUrl or DownloadedBytes < TotalSize), without slowing down startup for regular tracks!
            if ((string.IsNullOrEmpty(entry.OriginalUrl) || entry.DownloadedBytes < entry.TotalSize)
                && !VerifyOrphanFileNoZeroHoles(filePath, actualLength))
            {
                Log.Warn($"[AudioCache] Sparse zero-hole detected in orphan/incomplete cache file: {entry.CacheKey} — invalidating corrupt entry");
                InvalidateCompleteEntry(entry);

                try { File.Delete(filePath); }
                catch (Exception ex) { Log.Warn($"[AudioCache] Failed to delete sparse cache file: {ex.Message}"); }

                return false;
            }

            // File is physically valid — self-heal range-state if necessary
            if (entry.DownloadedBytes < entry.TotalSize && entry.TotalSize > 0)
            {
                entry.MarkFullyDownloaded();
                entry.ActualFileSize = actualLength;
                MarkDirty();
                Log.Info($"[AudioCache] Self-healed integrity: {entry.CacheKey}");
                _ = SaveIndexAsync();
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioCache] Integrity check I/O error for {entry.CacheKey}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Invalidates a byte range in the cache.
    /// </summary>
    internal void InvalidateRange(string cacheKey, long offset, int length)
    {
        if (string.IsNullOrEmpty(cacheKey) || length <= 0) return;

        if (_entries.TryGetValue(cacheKey, out var entry))
        {
            entry.IsComplete = false;
            entry.CompletedAt = null;
            entry.InvalidateRange(offset, length);
            MarkDirty();
            UpdateFileSizeCache(entry);
            _ = SaveIndexAsync();

            Log.Info($"[AudioCache] Surgical invalidation: {cacheKey}, range={offset}-{offset + length - 1}");
        }
    }

    /// <summary>
    /// Invalidates a byte range in all cached profiles for the specified track.
    /// </summary>
    internal void InvalidateRangeByTrackId(string trackId, long offset, int length)
    {
        if (string.IsNullOrEmpty(trackId) || length <= 0) return;

        var keys = CollectAllCacheKeysForTrack(trackId);
        for (int i = 0; i < keys.Count; i++)
        {
            InvalidateRange(keys[i], offset, length);
        }
    }

    #endregion

    #region Private Helpers

    private void MarkDirty() => Volatile.Write(ref _dirty, 1);

    private void AddToTrackIndex(string trackId, string cacheKey)
    {
        var keys = _trackIndex.GetOrAdd(trackId, static _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        keys.TryAdd(cacheKey, 1);
    }

    private void RemoveFromTrackIndex(string trackId, string cacheKey)
    {
        if (!_trackIndex.TryGetValue(trackId, out var keys)) return;

        keys.TryRemove(cacheKey, out _);
        if (keys.IsEmpty)
            _trackIndex.TryRemove(trackId, out _);
    }

    /// <summary>Returns or creates a <see cref="CacheFileHandle"/> for cacheKey.</summary>
    private CacheFileHandle GetFileHandle(string cacheKey) =>
        _fileHandles.GetOrAdd(cacheKey, key => new CacheFileHandle(GetCachePath(key)));

    /// <summary>Returns <see cref="CacheFileHandle"/> if it exists.</summary>
    private CacheFileHandle? TryGetFileHandle(string cacheKey) =>
        _fileHandles.TryGetValue(cacheKey, out var entry) ? entry : null;

    /// <summary>Forcibly closes the cache file handle.</summary>
    private void ForceCloseHandle(string cacheKey)
    {
        if (_fileHandles.TryGetValue(cacheKey, out var entry))
            entry.ForceClose();
    }

    /// <summary>Forcibly closes all handles.</summary>
    private void ForceCloseAllHandles()
    {
        foreach (var entry in _fileHandles.Values)
            entry.ForceClose();
    }

    private void DisposeHandle(string cacheKey)
    {
        if (_fileHandles.TryRemove(cacheKey, out var handle))
        {
            try { handle.Dispose(); }
            catch { }
        }
    }

    private void DisposeAllHandles()
    {
        foreach (var key in _fileHandles.Keys)
            DisposeHandle(key);
    }

    private void UpdateFileSizeCache(AudioCacheEntry entry)
    {
        try
        {
            var filePath = GetCachePath(entry);
            var fi = new FileInfo(filePath);
            entry.ActualFileSize = fi.Exists ? fi.Length : 0;
        }
        catch
        {
        }
    }

    private void RaiseFormatCached(AudioCacheEntry entry)
    {
        try
        {
            OnFormatCached?.Invoke(entry.TrackId, entry.Format, entry.Bitrate, false);
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioCache] OnFormatCached handler error: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks that the file exists and its size is not less than expected.
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <param name="minSize">Minimum expected size in bytes.</param>
    /// <param name="fileLength">Actual length of the file on disk; 0 if it does not exist.</param>
    /// <returns><c>true</c> if the file exists and <c>Length &gt;= minSize</c>.</returns>
    private static bool IsFilePhysicallyComplete(string filePath, long minSize, out long fileLength)
    {
        try
        {
            var fi = new FileInfo(filePath);
            fileLength = fi.Exists ? fi.Length : 0;
            return fi.Exists && minSize > 0 && fi.Length >= minSize;
        }
        catch
        {
            fileLength = 0;
            return false;
        }
    }

    private void LoadIndex()
    {
        var binPath = Path.Combine(_cacheDirectory, CacheMetadataBinFileName);
        var bytes = AtomicFile.ReadBytesWithFallback(binPath, out bool loadedFromBinaryBackup);

        if (bytes != null && bytes.Length > 0)
        {
            try
            {
                var envelope = MemoryPackSerializer.Deserialize<AudioCacheIndexEnvelope>(bytes);
                if (envelope?.Entries != null)
                {
                    int restoredCount = 0;
                    for (int i = 0; i < envelope.Entries.Count; i++)
                    {
                        var entry = envelope.Entries[i];
                        if (string.IsNullOrEmpty(entry.CacheKey)) continue;

                        string filePath = GetCachePath(entry);
                        var fi = new FileInfo(filePath);
                        if (!fi.Exists) continue;

                        entry.RestoreAfterLoad();
                        entry.ActualFileSize = fi.Length;
                        _entries.TryAdd(entry.CacheKey, entry);
                        AddToTrackIndex(entry.TrackId, entry.CacheKey);
                        restoredCount++;
                    }

                    Log.Info($"[AudioCache] Binary index loaded successfully (MemoryPack, v{envelope.SchemaVersion}): {restoredCount} entries restored");
                    if (loadedFromBinaryBackup)
                    {
                        MarkDirty();
                        _ = SaveIndexAsync();
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[AudioCache] Failed to deserialize binary index, trying JSON migration fallback: {ex.Message}");
            }
        }

        var legacyIndexPath = Path.Combine(_cacheDirectory, CacheMetadataFileName);
        if (File.Exists(legacyIndexPath))
        {
            MigrateLegacyJsonIndex(legacyIndexPath);
        }
        else
        {
            Log.Info("[AudioCache] Starting with fresh index (no valid index or backup found)");
        }
    }

    /// <summary>
    /// Isolated index schema migration from legacy JSON to binary MemoryPack.
    /// </summary>
    private void MigrateLegacyJsonIndex(string indexPath)
    {
        var json = AtomicFile.ReadTextWithFallback(indexPath, out _);
        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            int loadedSchemaVersion;
            List<AudioCacheEntry>? entries = null;

            var trimmed = json.AsSpan().TrimStart();
            if (trimmed.Length > 0 && trimmed[0] == '{')
            {
                var envelope = JsonSerializer.Deserialize(json, AppJsonContext.Default.AudioCacheIndexEnvelope);
                loadedSchemaVersion = envelope?.SchemaVersion ?? 0;
                entries = envelope?.Entries;
            }
            else
            {
                loadedSchemaVersion = 1;
                entries = JsonSerializer.Deserialize(json, AppJsonContext.Default.ListAudioCacheEntry);
            }

            if (entries == null) return;

            bool needsMigration = loadedSchemaVersion < CurrentSchemaVersion;
            bool needsLufsMigration = loadedSchemaVersion < 3;
            bool needsBitrateMigration = loadedSchemaVersion < 4;
            int migratedComplete = 0;
            int droppedPartial = 0;

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (string.IsNullOrEmpty(entry.CacheKey)) continue;

                string filePath = GetCachePath(entry.CacheKey);
                if (!File.Exists(filePath)) continue;

                if (needsMigration)
                    MigrateEntry(entry, filePath, ref migratedComplete, ref droppedPartial);

                if (needsLufsMigration)
                {
                    entry.IntegratedLufs = null;
                    entry.IntegratedLufsSource = 0;
                }

                if (needsBitrateMigration && !string.IsNullOrEmpty(entry.CacheKey) && entry.Bitrate > 0)
                {
                    string expectedKey = AudioSourceFactory.BuildCacheKey(entry.TrackId, entry.Format, entry.Bitrate);
                    if (!string.Equals(entry.CacheKey, expectedKey, StringComparison.Ordinal))
                    {
                        string oldPath = GetCachePath(entry.CacheKey);
                        string newPath = GetCachePath(expectedKey);

                        if (File.Exists(oldPath) && !File.Exists(newPath))
                        {
                            try { File.Move(oldPath, newPath); }
                            catch (Exception ex) { Log.Warn($"[AudioCache] Bitrate bucket migration move failed: {ex.Message}"); }
                        }

                        // Reindexing: remove the old key, entry will be reloaded with the new one
                        RemoveFromTrackIndex(entry.TrackId, entry.CacheKey);
                        entry = new AudioCacheEntry
                        {
                            CacheKey = expectedKey,
                            TrackId = entry.TrackId,
                            OriginalUrl = entry.OriginalUrl,
                            TotalSize = entry.TotalSize,
                            Format = entry.Format,
                            Codec = entry.Codec,
                            Bitrate = entry.Bitrate,
                            DurationMs = entry.DurationMs,
                            AlignmentBytes = entry.AlignmentBytes,
                            CreatedAt = entry.CreatedAt,
                            LastAccessedAt = entry.LastAccessedAt,
                            CompletedAt = entry.CompletedAt,
                            IsComplete = entry.IsComplete,
                            ActualFileSize = entry.ActualFileSize,
                            IntegratedLufs = entry.IntegratedLufs,
                            IntegratedLufsSource = entry.IntegratedLufsSource,
                            DownloadedRangesData = entry.DownloadedRangesData
                        };
                        entries[i] = entry;
                    }
                }

                entry.RestoreAfterLoad();

                if (entry.IsComplete && entry.DownloadedBytes < entry.TotalSize && entry.TotalSize > 0)
                {
                    if (IsFilePhysicallyComplete(filePath, entry.TotalSize, out long len))
                    {
                        entry.MarkFullyDownloaded();
                        entry.ActualFileSize = len;
                        migratedComplete++;
                        Log.Debug($"[AudioCache] Self-healed complete entry: {entry.CacheKey}");
                    }
                    else if (len == 0)
                    {
                        Log.Warn($"[AudioCache] Self-heal I/O error or missing file for {entry.CacheKey}");
                    }
                }

                UpdateFileSizeCache(entry);
                _entries.TryAdd(entry.CacheKey, entry);
                AddToTrackIndex(entry.TrackId, entry.CacheKey);
            }

            Log.Info($"[AudioCache] Legacy JSON index migrated to MemoryPack (Schema v{loadedSchemaVersion}→v{CurrentSchemaVersion}): {_entries.Count} entries restored");
            MarkDirty();
            SaveIndexSync();

            try
            {
                var bak = indexPath + ".bak";
                if (File.Exists(indexPath) && !File.Exists(bak))
                    File.Copy(indexPath, bak, overwrite: true);

                if (File.Exists(indexPath))
                    File.Delete(indexPath);
            }
            catch { }

            Log.Debug($"[AudioCache] Loaded {_entries.Count} entries");
        }
        catch (Exception ex)
        {
            Log.Error($"[AudioCache] Failed to parse index: {ex.Message}");
        }
    }

    /// <summary>
    /// Atomically saves the cache index to disk in binary format.
    /// </summary>
    private async Task SaveIndexAsync()
    {
        if (_disposed) return;
        if (Volatile.Read(ref _dirty) == 0) return;

        try
        {
            if (!await _saveLock.WaitAsync(CacheSaveLockTimeoutMs).ConfigureAwait(false))
                return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_disposed || Interlocked.Exchange(ref _dirty, 0) == 0)
                return;

            var envelope = BuildIndexEnvelope();
            byte[] bytes = MemoryPackSerializer.Serialize(envelope);
            var binPath = Path.Combine(_cacheDirectory, CacheMetadataBinFileName);
            await AtomicFile.WriteBytesAsync(binPath, bytes, createBackup: false, ct: CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            MarkDirty();
            Log.Warn($"[AudioCache] Failed to atomic save index: {ex.Message}");
        }
        finally
        {
            try { _saveLock.Release(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Synchronous atomic cache index save for the shutdown path.
    /// </summary>
    private void SaveIndexSync()
    {
        if (_disposed) return;
        if (Volatile.Read(ref _dirty) == 0) return;
        if (!_saveLock.Wait(CacheSaveLockTimeoutMs)) return;

        try
        {
            if (Interlocked.Exchange(ref _dirty, 0) == 0)
                return;

            var envelope = BuildIndexEnvelope();
            byte[] bytes = MemoryPackSerializer.Serialize(envelope);
            var binPath = Path.Combine(_cacheDirectory, CacheMetadataBinFileName);
            AtomicFile.WriteBytes(binPath, bytes, createBackup: false);
        }
        catch (Exception ex)
        {
            MarkDirty();
            Log.Warn($"[AudioCache] Failed to sync atomic save index: {ex.Message}");
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>
    /// Migrates an entry from the legacy chunk-based schema to the current range-based schema.
    /// </summary>
    private static void MigrateEntry(
        AudioCacheEntry entry,
        string filePath,
        ref int migratedComplete,
        ref int droppedPartial)
    {
        if (entry.AlignmentBytes <= 0)
            entry.AlignmentBytes = ChunkSize;

        bool hasRangeState = entry.DownloadedRangesData is { Count: > 0 };

        if (entry.IsComplete && !hasRangeState)
        {
            if (IsFilePhysicallyComplete(filePath, entry.TotalSize, out long len))
            {
                entry.MarkFullyDownloaded();
                entry.ActualFileSize = len;
                migratedComplete++;
            }
            else
            {
                entry.IsComplete = false;
                entry.CompletedAt = null;
                droppedPartial++;
            }
        }
        else if (!entry.IsComplete && !hasRangeState)
        {
            entry.ResetDownloadedRanges();
            droppedPartial++;
        }
    }

    /// <summary>
    /// Builds a cache index envelope object from the current state of _entries.
    /// </summary>
    private AudioCacheIndexEnvelope BuildIndexEnvelope()
    {
        var sourceEntries = _entries.Values.ToList();
        var snapshotEntries = new List<AudioCacheEntry>(sourceEntries.Count);

        for (int i = 0; i < sourceEntries.Count; i++)
        {
            var src = sourceEntries[i];
            List<SerializedDownloadedRange>? rangesSnapshot = null;

            var ranges = src.GetDownloadedRangesSnapshot();
            if (ranges.Length > 0)
            {
                rangesSnapshot = new List<SerializedDownloadedRange>(ranges.Length);
                for (int j = 0; j < ranges.Length; j++)
                {
                    rangesSnapshot.Add(new SerializedDownloadedRange
                    {
                        Start = ranges[j].Start,
                        EndExclusive = ranges[j].EndExclusive
                    });
                }
            }

            var clone = new AudioCacheEntry
            {
                CacheKey = src.CacheKey,
                TrackId = src.TrackId,
                OriginalUrl = src.OriginalUrl,
                TotalSize = src.TotalSize,
                Format = src.Format,
                Codec = src.Codec,
                Bitrate = src.Bitrate,
                DurationMs = src.DurationMs,
                AlignmentBytes = src.AlignmentBytes,
                CreatedAt = src.CreatedAt,
                LastAccessedAt = src.LastAccessedAt,
                CompletedAt = src.CompletedAt,
                IsComplete = src.IsComplete,
                ActualFileSize = src.ActualFileSize,
                IntegratedLufs = src.IntegratedLufs,
                IntegratedLufsSource = src.IntegratedLufsSource,
                DownloadedRangesData = rangesSnapshot
            };

            snapshotEntries.Add(clone);
        }

        return new AudioCacheIndexEnvelope
        {
            SchemaVersion = CurrentSchemaVersion,
            Entries = snapshotEntries
        };
    }

    private async Task AutoSaveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await ct.DelayNoThrowAsync(CacheAutoSaveIntervalMs, continueOnCapturedContext: false).ConfigureAwait(false))
                    break;

                await SaveIndexAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"[AudioCache] Auto-save error: {ex.Message}");
            }
        }
    }

    #endregion

    #region Dispose

    public void Dispose()
    {
        if (_disposed) return;

        _timerCts.Cancel();
        try { _autoSaveTask.Wait(200); } catch { }

        SaveIndexSync();

        _disposed = true;
        ForceCloseAllHandles();

        foreach (var fileLock in _fileLocks.Values)
        {
            try { fileLock.Dispose(); } catch { }
        }

        _timerCts.Dispose();
        _maintenanceLock.Dispose();
        _evictLock.Dispose();
        _saveLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        _timerCts.Cancel();

        try { await _autoSaveTask.ConfigureAwait(false); } catch { }

        await SaveIndexAsync().ConfigureAwait(false);

        _disposed = true;
        ForceCloseAllHandles();

        foreach (var fileLock in _fileLocks.Values)
        {
            try { fileLock.Dispose(); } catch { }
        }

        _timerCts.Dispose();
        _maintenanceLock.Dispose();
        _evictLock.Dispose();
        _saveLock.Dispose();
    }

    #endregion

    /// <summary>
    /// Owning record for a cache file handle.
    /// Provides lease-based ownership and in-flight I/O tracking.
    /// </summary>
    private sealed class CacheFileHandle : IDisposable
    {
        private readonly string _filePath;
        private readonly Lock _lock = new();
        private SafeFileHandle? _handle;
        private int _leaseCount;
        private int _activeIoCount;
        private bool _deleteOnClose;
        private TaskCompletionSource? _quiescenceWaiter;

        public CacheFileHandle(string filePath) => _filePath = filePath;

        public int LeaseCount => Volatile.Read(ref _leaseCount);
        public int ActiveIoCount => Volatile.Read(ref _activeIoCount);

        public bool IsClosed
        {
            get { lock (_lock) return _handle is null or { IsClosed: true }; }
        }

        public void AddLease()
        {
            lock (_lock) _leaseCount++;
        }

        public void RemoveLease()
        {
            lock (_lock)
            {
                if (_leaseCount > 0) _leaseCount--;
                TryCloseIfQuiescent();
            }
        }

        public void BeginIo() => Interlocked.Increment(ref _activeIoCount);

        public void EndIo()
        {
            if (Interlocked.Decrement(ref _activeIoCount) <= 0
                && Volatile.Read(ref _leaseCount) <= 0)
            {
                lock (_lock) TryCloseIfQuiescent();
            }
        }

        public SafeFileHandle GetOrOpen(FileMode mode = FileMode.OpenOrCreate)
        {
            lock (_lock)
            {
                if (_handle is { IsClosed: false })
                    return _handle;

                _handle = File.OpenHandle(
                    _filePath, mode,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete,
                    FileOptions.Asynchronous | FileOptions.RandomAccess);

                return _handle;
            }
        }

private void TryCloseIfQuiescent()
{
    if (_leaseCount > 0 || _activeIoCount > 0) return;

    var waiter = _quiescenceWaiter;
    _quiescenceWaiter = null;

    if (_handle is { IsClosed: false })
    {
        try { _handle.Dispose(); } catch { }
        _handle = null;
    }

    if (_deleteOnClose)
    {
        try { if (File.Exists(_filePath)) File.Delete(_filePath); } catch { }
    }

    waiter?.TrySetResult();
}

        public void MarkForDeleteOnClose()
        {
            TaskCompletionSource? waiter = null;
            bool deleteNow = false;
            lock (_lock)
            {
                _deleteOnClose = true;
                if (_leaseCount <= 0 && _activeIoCount <= 0)
                {
                    waiter = _quiescenceWaiter;
                    _quiescenceWaiter = null;

                    if (_handle is { IsClosed: false })
                    {
                        try { _handle.Dispose(); } catch { }
                    }
                    _handle = null;
                    deleteNow = true;
                }
            }

            if (deleteNow)
            {
                try { if (File.Exists(_filePath)) File.Delete(_filePath); } catch { }
            }

            waiter?.TrySetResult();
        }

        public void ForceClose()
        {
            TaskCompletionSource? waiter;
            bool deleteFile;
            lock (_lock)
            {
                waiter = _quiescenceWaiter;
                _quiescenceWaiter = null;
                deleteFile = _deleteOnClose;

                if (_handle is { IsClosed: false })
                {
                    try { _handle.Dispose(); } catch { }
                }
                _handle = null;
            }

            if (deleteFile)
            {
                try { if (File.Exists(_filePath)) File.Delete(_filePath); } catch { }
            }

            waiter?.TrySetResult();
        }

        public void Dispose() => ForceClose();
    }
}