using System.Collections.Concurrent;
using LMP.Core.Models;

namespace LMP.Core.Services;

/// <summary>
/// Оркестратор параллельного импорта плейлистов на базе Parallel.ForEachAsync.
/// </summary>
public sealed class PlaylistImportCoordinator
{
    private const int FlushBatchSize = 25;

    private readonly PlaylistService _playlistService;
    private readonly TrackMatcher _matcher;

    public PlaylistImportCoordinator(PlaylistService playlistService, TrackMatcher matcher)
    {
        _playlistService = playlistService;
        _matcher = matcher;
    }

    public async Task<ImportSummary> ImportAsync(
        ExternalPlaylist playlistMetadata,
        IReadOnlyList<ExternalTrack> sourceTracks,
        (long BytesReceived, long BytesSent) sourceTraffic,
        IProgress<ImportProgressReport>? progress = null,
        string? targetPlaylistId = null,
        bool isRetry = false,
        CancellationToken ct = default)
    {
        if (sourceTracks.Count == 0)
        {
            return new ImportSummary(0, 0, 0, 0, sourceTraffic.BytesReceived, sourceTraffic.BytesSent, 0, 0, [], targetPlaylistId ?? string.Empty);
        }

        // 1. Создаём плейлист с локализованным описанием по умолчанию
        string playlistId = targetPlaylistId ?? string.Empty;
        if (string.IsNullOrEmpty(playlistId))
        {
            string defaultDesc = LocalizationService.Instance["Import_Yandex_DefaultDescription"];

            var lmpPlaylist = await _playlistService.CreatePlaylistAsync(
                name: playlistMetadata.Title,
                description: playlistMetadata.Description ?? defaultDesc,
                thumbnailUrl: playlistMetadata.CoverUrl,
                customColor: playlistMetadata.HexColor,
                computedColor: playlistMetadata.HexColor,
                ct: ct).ConfigureAwait(false);

            playlistId = lmpPlaylist.Id;
        }

        int total = sourceTracks.Count;
        int processed = 0;
        int matchedCount = 0;
        int localHits = 0;
        int networkSearches = 0;
        int uncommittedCount = 0; // O(1) счетчик вместо медленного ConcurrentQueue.Count

        var saveQueue = new ConcurrentQueue<TrackInfo>();
        var failedTracks = new ConcurrentBag<ExternalTrack>();
        var flushLock = new SemaphoreSlim(1, 1);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = 3,
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(sourceTracks, parallelOptions, async (track, token) =>
        {
            token.ThrowIfCancellationRequested();

            var matched = await _matcher.MatchAsync(track, isRelaxed: isRetry, token).ConfigureAwait(false);
            var currentProcessed = Interlocked.Increment(ref processed);

            if (matched != null)
            {
                Interlocked.Increment(ref matchedCount);
                saveQueue.Enqueue(matched);

                // O(1) инкремент и сброс пакета без обхода очереди
                if (Interlocked.Increment(ref uncommittedCount) >= FlushBatchSize)
                {
                    Interlocked.Exchange(ref uncommittedCount, 0);
                    await FlushQueueAsync(playlistId, saveQueue, flushLock, token).ConfigureAwait(false);
                }
            }
            else
            {
                failedTracks.Add(track);
            }

            progress?.Report(new ImportProgressReport(currentProcessed, total, $"{track.Artist} — {track.Title}"));
        }).ConfigureAwait(false);

        await FlushQueueAsync(playlistId, saveQueue, flushLock, ct).ConfigureAwait(false);

        long ytWireTrafficBytes = networkSearches * 24L * 1024L;
        long totalTrafficBytes = sourceTraffic.BytesReceived + sourceTraffic.BytesSent + ytWireTrafficBytes;
        long savedBytes = (localHits * 24L * 1024L) + (networkSearches * 180L * 1024L);

        var summary = new ImportSummary(
            Matched: matchedCount,
            Total: total,
            LocalHits: localHits,
            NetworkSearches: networkSearches,
            SourceBytesReceived: sourceTraffic.BytesReceived,
            SourceBytesSent: sourceTraffic.BytesSent,
            TotalTrafficBytes: totalTrafficBytes,
            SavedTrafficBytes: savedBytes,
            FailedTracks: [.. failedTracks],
            PlaylistId: playlistId);

        LogSummary(summary);
        return summary;
    }

    private async Task FlushQueueAsync(
        string playlistId,
        ConcurrentQueue<TrackInfo> queue,
        SemaphoreSlim flushLock,
        CancellationToken ct)
    {
        if (queue.IsEmpty) return;

        await flushLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var batch = new List<TrackInfo>(FlushBatchSize + 8);
            while (queue.TryDequeue(out var t))
            {
                batch.Add(t);
            }

            if (batch.Count > 0)
            {
                await _playlistService.AddTracksToPlaylistAsync(playlistId, batch, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            flushLock.Release();
        }
    }

    private static void LogSummary(ImportSummary s)
    {
        Log.Info("[ImportCoordinator] ═════════ IMPORT TRAFFIC METRICS ═════════");
        Log.Info($"[ImportCoordinator] • Matched tracks:     {s.Matched} of {s.Total}");
        Log.Info($"[ImportCoordinator] • Unresolved tracks:  {s.FailedTracks.Count}");
        Log.Info($"[ImportCoordinator] • Source traffic:     {s.FormattedSourceTraffic}");
        Log.Info($"[ImportCoordinator] • Local-First (L1):   {s.LocalHits} tracks (0 wire bytes)");
        Log.Info($"[ImportCoordinator] • Total wire traffic: ~{s.FormattedTotalTraffic}");
        Log.Info($"[ImportCoordinator] • Bandwidth saved:    ~{s.FormattedSavedTraffic}");
        Log.Info("[ImportCoordinator] ═════════════════════════════════════════");
    }
}
