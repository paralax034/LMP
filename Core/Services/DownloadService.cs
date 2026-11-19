namespace LMP.Core.Services;

public sealed class DownloadService
{
    private readonly YoutubeProvider _youtube;
    private readonly LibraryService _library;

    private readonly Dictionary<string, DownloadTask> _activeTasks = [];
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _downloadSemaphore = new(3);

    public event Action<string, float>? OnProgress;
    public event Action<string, bool, string?>? OnCompleted;

    public DownloadService(YoutubeProvider youtube, LibraryService library)
    {
        _youtube = youtube;
        _library = library;
    }

    public bool IsDownloading(string trackId)
    {
        lock (_lock)
        {
            return _activeTasks.ContainsKey(trackId);
        }
    }

    public int ActiveDownloadsCount
    {
        get
        {
            lock (_lock)
            {
                return _activeTasks.Count;
            }
        }
    }

    /// <summary>
    /// Starts the track download process.
    /// <list type="bullet">
    ///   <item>If the track is fully cached — instant export without network usage.</item>
    ///   <item>If the track is partially cached — downloads missing gaps only.</item>
    ///   <item>If no cache exists — downloads completely via the cache mechanism and exports.</item>
    /// </list>
    /// </summary>
    /// <param name="track">Track information object.</param>
    public void StartDownload(TrackInfo track) => StartDownload(track, checkQuota: true);

    /// <summary>
    /// Starts batch downloads after checking the downloads folder quota once off the UI thread.
    /// </summary>
    internal async Task StartDownloadsBatchAsync(IReadOnlyList<TrackInfo> tracks)
    {
        if (tracks.Count == 0)
            return;

        long limitMb = _library.Settings.Storage.DownloadedTracksLimitMb;
        if (limitMb > 0)
        {
            string downloadPath = _library.DownloadPath;
            var (_, currentSizeMb) = await Task.Run(() =>
                Audio.Cache.AudioCacheManager.GetDownloadsStats(downloadPath)).ConfigureAwait(false);

            if (currentSizeMb >= limitMb)
            {
                Log.Warn($"[DownloadService] Download limit reached: {currentSizeMb}MB >= {limitMb}MB. Batch download aborted.");
                return;
            }
        }

        for (int i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            if (!track.IsDownloaded)
                StartDownload(track, checkQuota: false);
        }
    }

    /// <summary>
    /// Starts the track download process with optional quota pre-verification (skipped when already verified by a batch caller).
    /// </summary>
    internal void StartDownload(TrackInfo track, bool checkQuota, bool allowInstantExport = true)
    {
        lock (_lock)
        {
            if (_activeTasks.ContainsKey(track.Id) || track.IsDownloaded)
                return;
        }

        if (checkQuota)
        {
            long limitMb = _library.Settings.Storage.DownloadedTracksLimitMb;
            if (limitMb > 0)
            {
                var (_, currentSizeMb) = Audio.Cache.AudioCacheManager.GetDownloadsStats(_library.DownloadPath);
                if (currentSizeMb >= limitMb)
                {
                    Log.Warn($"[DownloadService] Download limit reached: {currentSizeMb}MB >= {limitMb}MB. Download aborted for {track.Id}.");
                    OnCompleted?.Invoke(track.Id, false, null);
                    return;
                }
            }
        }

        bool isFullyCached = allowInstantExport && AudioSourceFactory.GlobalCache.IsTrackFullyCached(track.Id);

        lock (_lock)
        {
            if (_activeTasks.ContainsKey(track.Id) || track.IsDownloaded)
                return;

            _activeTasks[track.Id] = isFullyCached
                ? new DownloadTask { Progress = 0f }
                : new DownloadTask { Progress = 0f, CancellationSource = new CancellationTokenSource() };
        }

        if (isFullyCached)
            _ = RunInstantExportAsync(track, AudioSourceFactory.GlobalCache);
        else
            _ = RunGapFillDownloadAsync(track);
    }

    private async Task<bool> TryFinalizeDownloadedTrackAsync(TrackInfo track)
    {
        var updatedTrack = await _library.GetTrackAsync(track.Id).ConfigureAwait(false);
        if (updatedTrack == null || string.IsNullOrEmpty(updatedTrack.LocalPath))
            return false;

        track.MarkAsDownloaded(updatedTrack.LocalPath, updatedTrack.PreferredFormat, updatedTrack.PreferredBitrate);
        OnProgress?.Invoke(track.Id, 1.0f);
        OnCompleted?.Invoke(track.Id, true, updatedTrack.LocalPath);
        return true;
    }

    /// <summary>
    /// Instant export of a fully cached track to Downloads.
    /// </summary>
    private async Task RunInstantExportAsync(TrackInfo track, Audio.Cache.AudioCacheManager cache)
    {
        bool exported = false;
        try
        {
            OnProgress?.Invoke(track.Id, 0f);

            bool success = await cache.ExportTrackToDownloadsAsync(
                track.Id,
                async id => await _library.GetTrackAsync(id).ConfigureAwait(false),
                async t => await _library.AddOrUpdateTrackAsync(t).ConfigureAwait(false),
                CancellationToken.None,
                _library.DownloadPath).ConfigureAwait(false);

            if (success)
                exported = await TryFinalizeDownloadedTrackAsync(track).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"[DownloadService] Instant export failed for {track.Id}: {ex.Message}. Falling back to gap-fill.");
        }
        finally
        {
            lock (_lock)
            {
                _activeTasks.Remove(track.Id);
            }
        }

        if (!exported)
            StartDownload(track, checkQuota: false, allowInstantExport: false);
    }

    /// <summary>
    /// Performs gap-fill download: resolve URL → download gaps → export to Downloads.
    /// </summary>
    private async Task RunGapFillDownloadAsync(TrackInfo track)
    {
        await _downloadSemaphore.WaitAsync().ConfigureAwait(false);
        DownloadTask? removedTask = null;

        try
        {
            CancellationToken ct;
            lock (_lock)
            {
                if (!_activeTasks.TryGetValue(track.Id, out var task))
                    return;
                ct = task.CancellationSource?.Token ?? CancellationToken.None;
            }

            // Step 1: Resolve stream URL (uses session cache → 0 API calls for recently played tracks)
            var descriptor = await _youtube.RefreshStreamAsync(track, false, ct).ConfigureAwait(false);
            if (descriptor == null || !descriptor.Value.HasLiveUrl)
            {
                Log.Warn($"[DownloadService] No live URL for {track.Id}");
                OnCompleted?.Invoke(track.Id, false, null);
                return;
            }

            // Step 2: Gap-fill download (only missing ranges)
            var progress = new Progress<float>(p =>
            {
                lock (_lock)
                {
                    if (_activeTasks.TryGetValue(track.Id, out var task))
                        task.Progress = p;
                }
                OnProgress?.Invoke(track.Id, p);
            });

            bool cached = await Audio.Cache.CacheDownloadHelper.EnsureFullyCachedAsync(
                descriptor.Value,
                Audio.Http.SharedHttpClient.Instance,
                AudioSourceFactory.GlobalCache,
                progress,
                ct).ConfigureAwait(false);

            if (!cached)
            {
                Log.Warn($"[DownloadService] Gap-fill incomplete for {track.Id}");
                OnCompleted?.Invoke(track.Id, false, null);
                return;
            }

            ct.ThrowIfCancellationRequested();

            // Step 3: Export completed cache to Downloads
            bool exported = await AudioSourceFactory.GlobalCache.ExportTrackToDownloadsAsync(
                track.Id,
                async id => await _library.GetTrackAsync(id).ConfigureAwait(false),
                async t => await _library.AddOrUpdateTrackAsync(t).ConfigureAwait(false),
                ct,
                _library.DownloadPath).ConfigureAwait(false);

            if (exported && await TryFinalizeDownloadedTrackAsync(track).ConfigureAwait(false))
                return;

            OnCompleted?.Invoke(track.Id, false, null);
        }
        catch (OperationCanceledException)
        {
            OnCompleted?.Invoke(track.Id, false, null);
        }
        catch (Exception ex)
        {
            Log.Error($"[DownloadService] Gap-fill download failed for {track.Id}: {ex.Message}");
            OnCompleted?.Invoke(track.Id, false, null);
        }
        finally
        {
            lock (_lock)
            {
                if (_activeTasks.Remove(track.Id, out var task))
                    removedTask = task;
            }
            removedTask?.Dispose();
            _downloadSemaphore.Release();
        }
    }

    private sealed class DownloadTask : IDisposable
    {
        public float Progress { get; set; }
        public CancellationTokenSource? CancellationSource { get; set; }

        public void Dispose()
        {
            CancellationSource?.Dispose();
            CancellationSource = null;
        }
    }
}