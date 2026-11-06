using System.Buffers;
using System.Net.Http.Headers;
using LMP.Core.Audio.Http;
using LMP.Core.Audio.Interfaces;

namespace LMP.Core.Audio.Cache;

/// <summary>
/// Lightweight gap-fill downloader for <see cref="AudioCacheManager"/>.
/// Downloads missing ranges (gaps) only, without instantiating <see cref="Sources.CachingStreamSource"/>,
/// container parsers, or ring buffers.
/// </summary>
/// <remarks>
/// <para>Thread-safe: relies on <see cref="AudioCacheManager.WriteRangeAsync"/>
/// with internal file locking and I/O tracking.</para>
/// <para>Idempotent: re-writing an already downloaded range is a safe no-op
/// (overlaps are handled by <see cref="AudioCacheEntry.MarkRangeDownloaded"/>).</para>
/// <para>Safe for concurrent execution with an active <see cref="Sources.CachingStreamSource"/>:
/// both writers are serialized via the <c>_fileLocks</c> semaphore;
/// overlap writes do not corrupt data (same itag = same bytes).</para>
/// </remarks>
public static class CacheDownloadHelper
{
    /// <summary>
    /// Chunk size for HTTP range requests.
    /// Balances overhead (too many requests) against data loss on drop (large chunk).
    /// </summary>
    private const int ChunkSize = 512 * 1024;

    /// <summary>
    /// Guarantees full track caching by downloading only the missing ranges (gaps).
    /// </summary>
    /// <param name="descriptor">Resolved stream descriptor with a live URL.</param>
    /// <param name="httpClient">HTTP client sharing a common connection pool.</param>
    /// <param name="cacheManager">Disk cache manager.</param>
    /// <param name="progress">
    /// Progress callback (0.0–1.0) relative to gap bytes.
    /// If the track is 80% cached, progress reflects the completion of the remaining 20%.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if the track is fully cached; otherwise, <c>false</c>.</returns>
    /// <exception cref="ArgumentException">Descriptor does not contain a live URL.</exception>
    public static async Task<bool> EnsureFullyCachedAsync(
        ResolvedStreamDescriptor descriptor,
        HttpClient httpClient,
        AudioCacheManager cacheManager,
        IProgress<float>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cacheManager);

        if (!descriptor.HasLiveUrl)
            throw new ArgumentException("Descriptor must have a live URL for gap-fill.", nameof(descriptor));

        if (descriptor.ContentLengthBytes <= 0)
            throw new ArgumentException("Descriptor must have a positive content length.", nameof(descriptor));

        string cacheKey = AudioSourceFactory.BuildCacheKey(
            descriptor.TrackId, descriptor.Format, descriptor.BitrateKbps);

        return await EnsureFullyCachedCoreAsync(
            cacheKey,
            descriptor.TrackId,
            descriptor.Url,
            descriptor.ContentLengthBytes,
            descriptor.Format,
            descriptor.Codec,
            descriptor.BitrateKbps,
            httpClient,
            cacheManager,
            progress,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Core: creates/updates entry, computes gaps, downloads via HTTP range requests, and writes to cache.
    /// </summary>
    private static async Task<bool> EnsureFullyCachedCoreAsync(
        string cacheKey,
        string trackId,
        string url,
        long contentLength,
        AudioFormat format,
        AudioCodec codec,
        int bitrate,
        HttpClient httpClient,
        AudioCacheManager cacheManager,
        IProgress<float>? progress,
        CancellationToken ct)
    {
        var entry = cacheManager.CreateOrUpdate(
            cacheKey, trackId, url, contentLength, format, codec, bitrate);

        if (entry.IsComplete)
            return true;

        var ranges = entry.GetDownloadedRangesSnapshot();
        var gaps = ComputeGaps(ranges, contentLength);

        if (gaps.Count == 0)
        {
            if (entry.DownloadedBytes >= entry.TotalSize && !entry.IsComplete)
            {
                entry.MarkFullyDownloaded();
                entry.IsComplete = true;
                entry.CompletedAt = DateTime.UtcNow;
                entry.ActualFileSize = Math.Max(entry.ActualFileSize, entry.TotalSize);
            }

            return entry.IsComplete;
        }

        long totalGapBytes = 0;
        for (int i = 0; i < gaps.Count; i++)
            totalGapBytes += gaps[i].Length;

        long downloadedGapBytes = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);

        try
        {
            for (int gapIdx = 0; gapIdx < gaps.Count; gapIdx++)
            {
                ct.ThrowIfCancellationRequested();

                long gapOffset = gaps[gapIdx].Start;
                long gapRemaining = gaps[gapIdx].Length;

                while (gapRemaining > 0)
                {
                    ct.ThrowIfCancellationRequested();

                    int requestSize = (int)Math.Min(ChunkSize, gapRemaining);
                    long requestEnd = gapOffset + requestSize - 1;

                    using var request = CreateRangeRequest(url, gapOffset, requestEnd);
                    using var response = await httpClient.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                    if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        Log.Warn($"[CacheDownloadHelper] HTTP 403 for range {gapOffset}-{requestEnd}. URL expired.");
                        return false;
                    }

                    if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
                    {
                        Log.Warn($"[CacheDownloadHelper] HTTP 416 for range {gapOffset}-{requestEnd}. Content length mismatch.");
                        return false;
                    }

                    response.EnsureSuccessStatusCode();

                    int totalRead = await ReadStreamToBufferAsync(
                        response, buffer, requestSize, ct).ConfigureAwait(false);

                    if (totalRead == 0)
                    {
                        Log.Warn($"[CacheDownloadHelper] Empty response for range {gapOffset}-{requestEnd}");
                        return false;
                    }

                    await cacheManager.WriteRangeAsync(
                        cacheKey, gapOffset, buffer.AsMemory(0, totalRead), ct).ConfigureAwait(false);

                    downloadedGapBytes += totalRead;
                    gapOffset += totalRead;
                    gapRemaining -= totalRead;

                    float progressValue = totalGapBytes > 0
                        ? Math.Min((float)downloadedGapBytes / totalGapBytes, 1.0f)
                        : 1.0f;
                    progress?.Report(progressValue);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var finalEntry = cacheManager.GetCacheInfo(cacheKey);
        return finalEntry?.IsComplete ?? false;
    }

    /// <summary>
    /// Reads the HTTP response stream into a rented buffer.
    /// </summary>
    private static async ValueTask<int> ReadStreamToBufferAsync(
        HttpResponseMessage response,
        byte[] buffer,
        int expectedLength,
        CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        int totalRead = 0;
        while (totalRead < expectedLength)
        {
            int read = await stream.ReadAsync(
                buffer.AsMemory(totalRead, expectedLength - totalRead), ct).ConfigureAwait(false);
            if (read == 0) break;
            totalRead += read;
        }

        return totalRead;
    }

    /// <summary>
    /// Computes uncached ranges (gaps) based on a snapshot of downloaded ranges.
    /// </summary>
    private static List<(long Start, long Length)> ComputeGaps(CacheByteRange[] ranges, long totalSize)
    {
        var gaps = new List<(long Start, long Length)>();
        long scanPos = 0;

        for (int i = 0; i < ranges.Length; i++)
        {
            if (ranges[i].Start > scanPos)
                gaps.Add((scanPos, ranges[i].Start - scanPos));

            if (ranges[i].EndExclusive > scanPos)
                scanPos = ranges[i].EndExclusive;
        }

        if (scanPos < totalSize)
            gaps.Add((scanPos, totalSize - scanPos));

        return gaps;
    }

    /// <summary>
    /// Creates an HTTP range request with a valid User-Agent for YouTube CDN.
    /// </summary>
    private static HttpRequestMessage CreateRangeRequest(string url, long start, long end)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(start, end);
        SharedHttpClient.ApplyUserAgentFromUrl(request, url);
        return request;
    }
}