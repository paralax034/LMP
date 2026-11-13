using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using LMP.Core.Audio.Interfaces;
using MemoryPack;

namespace LMP.Core.Audio.Cache;

/// <summary>
/// Metadata for the range-based cache of a single audio stream.
/// </summary>
[MemoryPackable]
public sealed partial class AudioCacheEntry
{
    /// <summary>Unique cache key.</summary>
    public string CacheKey { get; init; } = "";

    /// <summary>Track identifier.</summary>
    public string TrackId { get; init; } = "";

    /// <summary>Source URL.</summary>
    public string OriginalUrl { get; set; } = "";

    /// <summary>Total content size in bytes.</summary>
    public long TotalSize { get; set; }

    /// <summary>Audio container format.</summary>
    public AudioFormat Format { get; init; }

    /// <summary>Audio codec.</summary>
    public AudioCodec Codec { get; set; }

    /// <summary>Actual bitrate in kbps.</summary>
    public int Bitrate { get; set; }

    /// <summary>Track duration in milliseconds.</summary>
    public long DurationMs { get; set; } = -1;

    /// <summary>
    /// Range alignment used by this cache.
    /// </summary>
    public int AlignmentBytes { get; set; }

    /// <summary>Creation date and time of the entry.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>Date and time of the last access.</summary>
    public DateTime LastAccessedAt { get; set; }

    /// <summary>Date and time when caching completed entirely.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Flag indicating full readiness of the local cache.</summary>
    public bool IsComplete { get; set; }

    /// <summary>Physical cache file size on disk.</summary>
    public long ActualFileSize { get; set; }

    /// <summary>
    /// New canonical field for the track's integrated loudness in LUFS.
    /// </summary>
    public float? IntegratedLufs { get; set; }

    /// <summary>
    /// Source of the <see cref="IntegratedLufs"/> value.
    /// </summary>
    public int IntegratedLufsSource { get; set; }

    /// <summary>
    /// Serializable ranges of locally downloaded data.
    /// </summary>
    public List<SerializedDownloadedRange>? DownloadedRangesData { get; set; }

    [MemoryPackIgnore]
    internal string? CachedFilePath { get; set; }

    [MemoryPackIgnore]
    private long _downloadedBytes;

    [MemoryPackIgnore]
    private long _rangesVersion;

    [JsonIgnore]
    [MemoryPackIgnore]
    private List<CacheByteRange>? _downloadedRanges;

    [JsonIgnore]
    [MemoryPackIgnore]
    private readonly Lock _rangesLock = new();

    [JsonIgnore]
    [MemoryPackIgnore]
    private ConcurrentDictionary<long, byte>? _corruptedOfflineRanges;

    /// <summary>Exact number of bytes available locally.</summary>
    [JsonIgnore]
    [MemoryPackIgnore]
    public long DownloadedBytes => Volatile.Read(ref _downloadedBytes);

    /// <summary>Monotonically increasing version of the downloaded ranges list for zero-alloc UI caching.</summary>
    [JsonIgnore]
    [MemoryPackIgnore]
    internal long RangesVersion => Volatile.Read(ref _rangesVersion);

    /// <summary>Download progress percentage.</summary>
    [JsonIgnore]
    [MemoryPackIgnore]
    public double DownloadProgress =>
        TotalSize <= 0 ? 0 : Math.Min(100.0, (double)DownloadedBytes / TotalSize * 100.0);

    /// <summary>
    /// Checks whether the range <c>[offset, offset + length)</c> is fully covered.
    /// </summary>
    public bool IsRangeDownloaded(long offset, long length)
    {
        if (length <= 0) return true;
        if (!NormalizeRange(offset, length, out long start, out long endExclusive))
            return false;

        lock (_rangesLock)
        {
            if (_downloadedRanges is not { Count: > 0 })
                return false;

            for (int i = 0; i < _downloadedRanges.Count; i++)
            {
                var current = _downloadedRanges[i];

                if (current.Start > start)
                    break;

                if (current.EndExclusive <= start)
                    continue;

                return current.Start <= start && current.EndExclusive >= endExclusive;
            }

            return false;
        }
    }

    /// <summary>
    /// Marks a byte range as downloaded.
    /// </summary>
    public void MarkRangeDownloaded(long offset, long length)
    {
        if (!NormalizeRange(offset, length, out long start, out long endExclusive))
            return;

        long addedBytes;

        lock (_rangesLock)
        {
            _downloadedRanges ??= new List<CacheByteRange>(4);

            int insertIndex = 0;
            while (insertIndex < _downloadedRanges.Count
                   && _downloadedRanges[insertIndex].EndExclusive < start)
            {
                insertIndex++;
            }

            long mergedStart = start;
            long mergedEnd = endExclusive;
            long overlapBytes = 0;
            int removeStart = insertIndex;

            while (insertIndex < _downloadedRanges.Count
                   && _downloadedRanges[insertIndex].Start <= mergedEnd)
            {
                var current = _downloadedRanges[insertIndex];

                long overlapStart = Math.Max(start, current.Start);
                long overlapEnd = Math.Min(endExclusive, current.EndExclusive);
                if (overlapStart < overlapEnd)
                    overlapBytes += overlapEnd - overlapStart;

                if (current.Start < mergedStart)
                    mergedStart = current.Start;

                if (current.EndExclusive > mergedEnd)
                    mergedEnd = current.EndExclusive;

                insertIndex++;
            }

            int removeCount = insertIndex - removeStart;
            if (removeCount > 0)
                _downloadedRanges.RemoveRange(removeStart, removeCount);

            _downloadedRanges.Insert(removeStart, new CacheByteRange(mergedStart, mergedEnd));
            addedBytes = endExclusive - start - overlapBytes;
            Interlocked.Increment(ref _rangesVersion);
        }

        if (addedBytes > 0)
            Interlocked.Add(ref _downloadedBytes, addedBytes);
    }

    /// <summary>
    /// Invalidates a byte range.
    /// </summary>
    public void InvalidateRange(long offset, long length)
    {
        if (!NormalizeRange(offset, length, out long start, out long endExclusive))
            return;

        long removedBytes;

        lock (_rangesLock)
        {
            if (_downloadedRanges is not { Count: > 0 })
                return;

            var updated = new List<CacheByteRange>(_downloadedRanges.Count + 1);
            removedBytes = 0;

            for (int i = 0; i < _downloadedRanges.Count; i++)
            {
                var current = _downloadedRanges[i];

                if (current.EndExclusive <= start || current.Start >= endExclusive)
                {
                    updated.Add(current);
                    continue;
                }

                long overlapStart = Math.Max(current.Start, start);
                long overlapEnd = Math.Min(current.EndExclusive, endExclusive);
                if (overlapStart < overlapEnd)
                    removedBytes += overlapEnd - overlapStart;

                if (current.Start < start)
                    updated.Add(new CacheByteRange(current.Start, start));

                if (current.EndExclusive > endExclusive)
                    updated.Add(new CacheByteRange(endExclusive, current.EndExclusive));
            }

            _downloadedRanges = updated.Count == 0 ? null : updated;
            Interlocked.Increment(ref _rangesVersion);
        }

        if (removedBytes > 0)
            Interlocked.Add(ref _downloadedBytes, -removedBytes);
    }

    /// <summary>
    /// Resets all locally downloaded ranges.
    /// </summary>
    public void ResetDownloadedRanges()
    {
        lock (_rangesLock)
        {
            _downloadedRanges = null;
            Interlocked.Increment(ref _rangesVersion);
        }

        DownloadedRangesData = null;
        Volatile.Write(ref _downloadedBytes, 0);
    }

    /// <summary>
    /// Marks the entire file as fully downloaded.
    /// </summary>
    public void MarkFullyDownloaded()
    {
        lock (_rangesLock)
        {
            _downloadedRanges = TotalSize > 0
                ? [new CacheByteRange(0, TotalSize)]
                : null;
            Interlocked.Increment(ref _rangesVersion);
        }

        DownloadedRangesData = TotalSize > 0
            ? [new() { Start = 0, EndExclusive = TotalSize }]
            : null;

        Volatile.Write(ref _downloadedBytes, Math.Max(0, TotalSize));
    }

    /// <summary>
    /// Tries to return a contiguous range containing the specified position.
    /// </summary>
    internal bool TryGetContainingRange(long offset, out long start, out long endExclusive)
    {
        if (offset < 0 || offset >= TotalSize)
        {
            start = 0;
            endExclusive = 0;
            return false;
        }

        lock (_rangesLock)
        {
            if (_downloadedRanges is not { Count: > 0 })
            {
                start = 0;
                endExclusive = 0;
                return false;
            }

            for (int i = 0; i < _downloadedRanges.Count; i++)
            {
                var current = _downloadedRanges[i];

                if (current.Start > offset)
                    break;

                if (current.EndExclusive <= offset)
                    continue;

                start = current.Start;
                endExclusive = current.EndExclusive;
                return true;
            }
        }

        start = 0;
        endExclusive = 0;
        return false;
    }

    /// <summary>
    /// Returns the number of contiguously available bytes forward from the position.
    /// </summary>
    public long GetContiguousDownloadedBytesFrom(long offset)
    {
        return TryGetContainingRange(offset, out _, out long endExclusive)
            ? endExclusive - offset
            : 0;
    }

    /// <summary>
    /// Finds the first aligned probe offset in <c>[minProbe, endExclusive)</c> fully covered by a cached range.
    /// Returns <see cref="long.MaxValue"/> if no probe is covered.
    /// </summary>
    internal long FindFirstCoveredProbeOffset(long minProbe, long endExclusive, int alignment, long originStart)
    {
        lock (_rangesLock)
        {
            if (_downloadedRanges is not { Count: > 0 })
                return long.MaxValue;

            for (int i = 0; i < _downloadedRanges.Count; i++)
            {
                var current = _downloadedRanges[i];
                if (current.Start >= endExclusive) break;
                if (current.EndExclusive <= minProbe) continue;

                long candidate = current.Start <= minProbe
                    ? minProbe
                    : originStart + AlignUpOffset(current.Start - originStart, alignment);

                if (candidate >= endExclusive) continue;

                long probeEnd = Math.Min(endExclusive, candidate + alignment);
                if (current.Start <= candidate && current.EndExclusive >= probeEnd)
                    return candidate;
            }
        }

        return long.MaxValue;
    }

    private static long AlignUpOffset(long value, int alignment)
    {
        long remainder = value % alignment;
        return remainder == 0 ? value : value + (alignment - remainder);
    }

    internal CacheByteRange[] GetDownloadedRangesSnapshot() =>
          GetDownloadedRangesSnapshot(out _);

    /// <summary>
    /// Atomically returns a snapshot of downloaded ranges and the current version under a single lock.
    /// </summary>
    internal CacheByteRange[] GetDownloadedRangesSnapshot(out long version)
    {
        lock (_rangesLock)
        {
            version = _rangesVersion;
            if (_downloadedRanges is not { Count: > 0 })
                return [];

            return [.. _downloadedRanges];
        }
    }

    /// <summary>
    /// Marks an aligned range as corrupted in the current offline session.
    /// </summary>
    public void MarkRangeCorruptedOffline(long alignedStart)
    {
        _corruptedOfflineRanges ??= new ConcurrentDictionary<long, byte>();
        _corruptedOfflineRanges.TryAdd(alignedStart, 1);
    }

    /// <summary>
    /// Restores runtime state after loading from JSON.
    /// </summary>
    public void RestoreAfterLoad()
    {
        lock (_rangesLock)
            _downloadedRanges = null;

        Volatile.Write(ref _downloadedBytes, 0);

        if (DownloadedRangesData is not { Count: > 0 })
        {
            DownloadedRangesData = null;
            return;
        }

        for (int i = 0; i < DownloadedRangesData.Count; i++)
        {
            var range = DownloadedRangesData[i];
            MarkRangeDownloaded(range.Start, range.EndExclusive - range.Start);
        }

        // Release deserialization DTO list to prevent duplicating ranges in Gen 2
        DownloadedRangesData = null;
    }

    private bool NormalizeRange(long offset, long length, out long start, out long endExclusive)
    {
        if (length <= 0 || offset < 0 || offset >= TotalSize)
        {
            start = 0;
            endExclusive = 0;
            return false;
        }

        start = offset;
        endExclusive = offset + length;

        if (endExclusive <= start)
        {
            endExclusive = 0;
            start = 0;
            return false;
        }

        if (endExclusive > TotalSize)
            endExclusive = TotalSize;

        return endExclusive > start;
    }
}

/// <summary>
/// Serializable range of locally downloaded data.
/// </summary>
[MemoryPackable]
public sealed partial class SerializedDownloadedRange
{
    /// <summary>Inclusive start of the range.</summary>
    public long Start { get; set; }

    /// <summary>Exclusive end of the range.</summary>
    public long EndExclusive { get; set; }
}

internal readonly record struct CacheByteRange(long Start, long EndExclusive);

public readonly struct CacheStats
{
    public int TotalEntries { get; init; }
    public int CompleteEntries { get; init; }
    public int PartialEntries { get; init; }
    public long TotalSizeBytes { get; init; }
    public long MaxSizeBytes { get; init; }

    public double UsagePercent =>
        MaxSizeBytes == 0 ? 0 : (double)TotalSizeBytes / MaxSizeBytes * 100;

    public string TotalSizeFormatted => FormatSize(TotalSizeBytes);
    public string MaxSizeFormatted => FormatSize(MaxSizeBytes);

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }
}