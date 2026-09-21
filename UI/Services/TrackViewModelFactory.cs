using LMP.UI.Features.Shared;

namespace LMP.UI.Services;

/// <summary>
/// Фабрика для создания изолированных <see cref="TrackItemViewModel"/>.
/// </summary>
public sealed class TrackViewModelFactory
{
    private readonly LibraryService _library;
    private readonly DialogService _dialog;
    private readonly AudioEngine _audio;
    private readonly PlayerControlService _playerControl;
    private readonly PlaylistService _playlistService;
    private readonly DownloadService _downloads;
    private readonly TrackRegistry _registry;

    public TrackViewModelFactory(
        LibraryService library,
        DialogService dialog,
        AudioEngine audio,
        PlayerControlService playerControl,
        PlaylistService playlistService,
        DownloadService downloads,
        TrackRegistry registry)
    {
        _library = library;
        _dialog = dialog;
        _audio = audio;
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
            _audio,
            _playerControl,
            _playlistService,
            _downloads,
            _dialog,
            _library,
            playAction);
    }
}