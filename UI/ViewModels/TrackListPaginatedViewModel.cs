using Avalonia.Threading;
using LMP.UI.Features.Shared;

namespace LMP.UI.ViewModels;

/// <summary>
/// Абстрактный базовый класс для всех экранов с пагинированным списком треков.
/// Фиксирует generic-параметры PaginatedViewModel на (TrackInfo, TrackItemViewModel)
/// и добавляет Smart Parent паттерн: O(1) обновление активного трека и прогресса загрузки.
/// </summary>
public abstract class TrackListPaginatedViewModel
    : PaginatedViewModel<TrackInfo, TrackItemViewModel>
{
    #region Fields

    protected readonly AudioEngine Audio;
    protected readonly DownloadService Downloads;
    protected readonly TrackViewModelFactory VmFactory;

    private TrackItemViewModel? _currentActiveVm;

    #endregion

    #region Constructor

    protected TrackListPaginatedViewModel(
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
        if (_currentActiveVm != null && _currentActiveVm.Id != currentTrack?.Id)
        {
            _currentActiveVm.SetActive(false, false);
            _currentActiveVm = null;
        }

        if (currentTrack is null) return;

        if (_currentActiveVm == null)
        {
            var count = Items.Count;
            for (int i = 0; i < count; i++)
            {
                var vm = Items[i];
                if (vm.Id == currentTrack.Id)
                {
                    _currentActiveVm = vm;
                    break;
                }
            }
        }

        _currentActiveVm?.SetActive(true, isPlaying);
    }

    #endregion

    #region Smart Parent — Downloads

    private void HandleDownloadProgress(string id, float progress)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var count = Items.Count;
            for (int i = 0; i < count; i++)
            {
                var vm = Items[i];
                if (vm.Id == id)
                {
                    vm.SetDownloadState(true, progress);
                    break;
                }
            }
        });
    }

    private void HandleDownloadCompleted(string id, bool ok, string? path)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var count = Items.Count;
            for (int i = 0; i < count; i++)
            {
                var vm = Items[i];
                if (vm.Id == id)
                {
                    vm.SetDownloadState(false, 0f);
                    break;
                }
            }
        });
    }

    #endregion

    #region PaginatedViewModel Overrides

    protected sealed override string GetItemId(TrackInfo item) => item.Id;

    protected sealed override bool FilterItem(TrackInfo item, string query) =>
        TrackFilters.MatchesTitleOrAuthor(item, query);

    protected sealed override TrackItemViewModel CreateItemViewModel(TrackInfo track)
    {
        var vm = VmFactory.GetOrCreate(track, OnPlay);

        if (Audio.CurrentTrack?.Id == track.Id)
        {
            vm.SetActive(true, Audio.IsPlaying);
            _currentActiveVm = vm;
        }

        return vm;
    }

    #endregion

    #region Abstract

    protected abstract void OnPlay(TrackInfo track);

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
        }
        base.Dispose(disposing);
    }

    #endregion
}