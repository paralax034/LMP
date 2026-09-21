using System.Runtime.CompilerServices;
using Avalonia.Threading;
using LMP.UI.Features.Shared;

namespace LMP.UI.ViewModels;

/// <summary>
/// Абстрактный базовый класс для экранов с переупорядочиваемым списком треков (Playlist и т.п.).
/// </summary>
public abstract class TrackListReorderableViewModel
    : ReorderableViewModel<TrackInfo, TrackItemViewModel>
{
    #region Fields

    protected readonly AudioEngine Audio;
    protected readonly DownloadService Downloads;
    protected readonly TrackViewModelFactory VmFactory;

    protected TrackItemViewModel? CurrentActiveVm;

    #endregion

    #region Constructor

    protected TrackListReorderableViewModel(
        AudioEngine audio,
        DownloadService downloads,
        TrackViewModelFactory vmFactory)
    {
        Audio = audio;
        Downloads = downloads;
        VmFactory = vmFactory;

        Audio.OnTrackChanged += HandleTrackChanged;
        Audio.OnPlaybackStateChanged += HandlePlaybackStateChanged;
        Downloads.OnProgress += HandleDownloadProgress;
        Downloads.OnCompleted += HandleDownloadCompleted;

        var cache = AudioSourceFactory.GlobalCache;
        cache?.OnFormatCached += HandleFormatCached;
    }

    #endregion

    #region Source Normalization

    protected override TrackInfo NormalizeSourceItem(TrackInfo item) =>
        VmFactory.GetCanonicalTrack(item);

    protected override void MergeSourceItem(TrackInfo current, TrackInfo fresh)
    {
        if (ReferenceEquals(current, fresh)) return;

        current.UpdateMetadata(fresh);

        if (fresh.IsDownloaded && !current.IsDownloaded)
        {
            if (!string.IsNullOrEmpty(fresh.LocalPath))
            {
                current.MarkAsDownloaded(
                    fresh.LocalPath,
                    fresh.PreferredFormat,
                    fresh.PreferredBitrate);
            }
            else
            {
                current.IsDownloaded = true;
                current.IsCached = true;
            }
        }
        else if (fresh.IsCached && !current.IsCached)
        {
            current.MarkAsCached(fresh.PreferredFormat, fresh.PreferredBitrate);
        }

        if (!string.IsNullOrEmpty(fresh.LocalPath) && fresh.LocalPath != current.LocalPath)
            current.LocalPath = fresh.LocalPath;

        if (fresh.PreferredFormat.HasValue &&
            fresh.PreferredFormat != current.PreferredFormat)
        {
            current.PreferredFormat = fresh.PreferredFormat;
        }

        if (fresh.PreferredBitrate > 0 && fresh.PreferredBitrate != current.PreferredBitrate)
            current.PreferredBitrate = fresh.PreferredBitrate;
    }

    #endregion

    #region Smart Parent — Audio

    private void HandleTrackChanged(TrackInfo? track)
    {
        Dispatcher.UIThread.Post(() => UpdatePlaybackState(track, Audio.IsPlaying));
    }

    private void HandlePlaybackStateChanged(bool isPlaying, bool isPaused)
    {
        Dispatcher.UIThread.Post(() => UpdatePlaybackState(Audio.CurrentTrack, isPlaying));
    }

    private void UpdatePlaybackState(TrackInfo? currentTrack, bool isPlaying)
    {
        if (CurrentActiveVm != null && CurrentActiveVm.Id != currentTrack?.Id)
        {
            CurrentActiveVm.SetActive(false, false);
            CurrentActiveVm = null;
        }

        if (currentTrack is null) return;

        CurrentActiveVm ??= GetCachedVm(currentTrack.Id);
        CurrentActiveVm?.SetActive(true, isPlaying);
    }

    #endregion

    #region Smart Parent — Downloads

    private void HandleDownloadProgress(string id, float progress)
    {
        Dispatcher.UIThread.Post(() => GetCachedVm(id)?.SetDownloadState(true, progress));
    }

    private void HandleDownloadCompleted(string id, bool ok, string? path)
    {
        Dispatcher.UIThread.Post(() => GetCachedVm(id)?.SetDownloadState(false, 0f));
    }

    #endregion

    #region Smart Parent — Cache

    private void HandleFormatCached(string trackId, AudioFormat format, int bitrate, bool isExport)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_sources.TryGetValue(trackId, out var track)) return;
            if (!track.IsCached)
                track.MarkAsCached(format, bitrate);
        });
    }

    /// <summary>
    /// Выполняет фоновую гидратацию статуса кэширования для загруженных треков.
    /// </summary>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Асинхронная задача.</returns>
    protected async Task HydrateCacheStatusAsync(CancellationToken ct = default)
    {
        var cache = AudioSourceFactory.GlobalCache;
        if (cache is null || _sources.Count == 0) return;

        try
        {
            var tracks = GetLoadedItemsSnapshot();
            if (tracks.Count == 0) return;

            await Task.Run(() => cache.HydrateCacheStatus(tracks), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"[TrackListReorderable] Cache hydration error: {ex.Message}");
        }
    }

    #endregion

    #region ReorderableViewModel Overrides

    protected sealed override string GetItemId(TrackInfo item) => item.Id;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected sealed override string GetViewModelId(TrackItemViewModel vm) => vm.Id;

    protected sealed override bool MatchesFilter(TrackInfo item, string query) =>
        TrackFilters.MatchesTitleOrAuthor(item, query);

    protected override TrackItemViewModel CreateViewModel(TrackInfo track)
    {
        var vm = VmFactory.GetOrCreate(track, OnPlay);

        if (Audio.CurrentTrack?.Id == track.Id)
        {
            vm.SetActive(true, Audio.IsPlaying);
            CurrentActiveVm = vm;
        }

        return vm;
    }

    protected sealed override Task<List<TrackInfo>> LoadItemsByIdsAsync(
        IEnumerable<string> ids, CancellationToken ct) =>
        LoadTracksAsync(ids, ct);

    #endregion

    #region Abstract

    protected abstract void OnPlay(TrackInfo track);

    protected abstract Task<List<TrackInfo>> LoadTracksAsync(
        IEnumerable<string> ids, CancellationToken ct);

    #endregion

    #region Cleanup

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Audio.OnTrackChanged -= HandleTrackChanged;
            Audio.OnPlaybackStateChanged -= HandlePlaybackStateChanged;
            Downloads.OnProgress -= HandleDownloadProgress;
            Downloads.OnCompleted -= HandleDownloadCompleted;

            AudioSourceFactory.GlobalCache?.OnFormatCached -= HandleFormatCached;
        }
        base.Dispose(disposing);
    }

    #endregion
}