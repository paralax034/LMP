using LMP.UI.Features.Shared;

namespace LMP.UI.Services;

/// <summary>
/// A factory for creating isolated <see cref="TrackItemViewModel"/> instances.
/// </summary>
public sealed class TrackViewModelFactory
{
    private readonly LibraryService _library;
    private readonly DialogService _dialog;
    private readonly PlayerControlService _playerControl;
    private readonly PlaylistService _playlistService;
    private readonly DownloadService _downloads;
    private readonly TrackRegistry _registry;

    public TrackViewModelFactory(
        LibraryService library,
        DialogService dialog,
        PlayerControlService playerControl,
        PlaylistService playlistService,
        DownloadService downloads,
        TrackRegistry registry)
    {
        _library = library;
        _dialog = dialog;
        _playerControl = playerControl;
        _playlistService = playlistService;
        _downloads = downloads;
        _registry = registry;
    }

    public TrackInfo GetCanonicalTrack(TrackInfo track) =>
        _registry.RegisterOrUpdate(track);

    public TrackItemViewModel GetOrCreate(TrackInfo track, Action<TrackInfo>? playAction = null)
    {
        var canonical = _registry.RegisterOrUpdate(track);
        return CreateVmInstance(canonical, playAction);
    }

    public TrackItemViewModel CreateForQueue(TrackInfo track, Action<TrackInfo>? playAction = null)
    {
        var canonical = _registry.RegisterOrUpdate(track);
        var vm = CreateVmInstance(canonical, playAction);
        vm.IsQueueContext = true;
        return vm;
    }

    private TrackItemViewModel CreateVmInstance(TrackInfo track, Action<TrackInfo>? playAction)
    {
        return new TrackItemViewModel(
            track,
            _playerControl,
            _playlistService,
            _downloads,
            _dialog,
            _library,
            playAction);
    }
}