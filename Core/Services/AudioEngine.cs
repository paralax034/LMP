using System.Collections.Concurrent;
using System.Threading.Channels;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Http;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Normalization;

namespace LMP.Core.Services;

/// <summary>
/// Central audio playback engine.
/// Coordinates AudioPlayer, track queue, volume, and UI events.
/// </summary>
/// <remarks>
/// <para>Decoupled from inheriting the ViewModelBase UI class to maintain strict separation of Core and UI layers.</para>
/// </remarks>
public sealed partial class AudioEngine : ObservableObject, ISuspendable, IDisposable, IAsyncDisposable
{
    #region Engine Command Types

    /// <summary>Marker interface for typed AudioEngine commands.</summary>
    private interface IEngineCommand { }

    /// <summary>Play a specific track starting from an optional position (for seamless playback restoration).</summary>
    /// <param name="Track">Track information.</param>
    /// <param name="Session">Session ID to cancel obsolete commands.</param>
    /// <param name="SeekPosition">Position to start playback from.</param>
    /// <param name="IsRetry">Flag indicating an automatic retry on cache error.</param>
    private sealed record PlayTrackCommand(
        TrackInfo Track,
        int Session,
        TimeSpan? SeekPosition = null,
        bool IsRetry = false) : IEngineCommand;

    /// <summary>Start the queue from the specified track.</summary>
    private sealed record StartQueueCommand(IEnumerable<TrackInfo> Tracks, TrackInfo StartTrack, int Session) : IEngineCommand;

    /// <summary>Play the current queue index; when StartPlaying = false, the track is loaded in a paused state.</summary>
    private sealed record PlayCurrentIndexCommand(int Session, bool StartPlaying = true) : IEngineCommand;

    /// <summary>Navigate forward/backward.</summary>
    /// <param name="Forward">Navigation direction.</param>
    /// <param name="UserInitiated">Whether the action was user-initiated.</param>
    /// <param name="StartPlaying">Whether to start playback on the target track.</param>
    /// <param name="EndedTrackId">Id of the naturally finished track for auto-transition; null for transition without stale check.</param>
    private sealed record NavigateCommand(bool Forward, bool UserInitiated, bool StartPlaying = true, string? EndedTrackId = null) : IEngineCommand;

    /// <summary>Change format/quality of the active track (enqueued to prevent race conditions).</summary>
    private sealed record SwitchQualityCommand(
        TrackInfo Track,
        TimeSpan Position,
        AudioFormat Format,
        int Bitrate,
        int Session) : IEngineCommand;

    #endregion

    #region Record Structs

    /// <summary>
    /// Result of acquiring a continuation URL with associated stream variant metadata.
    /// </summary>
    private readonly record struct ContinuationUrlResult(
        string Url,
        long Size,
        int Bitrate,
        AudioFormat Format,
        AudioCodec Codec,
        float IntegratedLufs = float.NaN);

    /// <summary>Context of the n-token warning.</summary>
    public readonly record struct NTokenWarningInfo(TrackInfo? Track, bool WasSkipped);

    #endregion

    #region Constants

    private const int CommandQueueCapacity = 32;

    /// <summary>Base volume range (0-200 = 0-100% without boost).</summary>
    public const int VolumeNormalRange = 200;

    /// <summary>Maximum gain (overdrive protection).</summary>
    public const float MaxGain = 4.0f;

    private const int QualitySwitchCooldownMs = 2000;

    /// <summary>
    /// Target size of the local contiguous prefix for Partial Cache Fast Start.
    /// </summary>
    private const int PartialCacheBootstrapTargetMs = 12_000;

    /// <summary>
    /// Lower bound of the contiguous bootstrap prefix in bytes.
    /// </summary>
    private const int PartialCacheBootstrapMinBytes = 96 * 1024;

    /// <summary>
    /// Upper bound of the contiguous bootstrap prefix in bytes.
    /// </summary>
    private const int PartialCacheBootstrapMaxBytes = 384 * 1024;

    /// <summary>
    /// Maximum number of automatic retry attempts after
    /// recoverable <see cref="Exceptions.CacheInvalidatedException"/> for a single track.
    /// </summary>
    private const int MaxCacheAutoRetries = 2;

    /// <summary>
    /// EBU R128 pre-scan window for full-cache sources (LocalFileSource).
    /// The file is fully available locally, allowing for a deeper analysis.
    /// </summary>
    private const int PreScanDurationFullCacheMs = 80_000;

    /// <summary>
    /// EBU R128 pre-scan window for streaming and partial-cache sources.
    /// Constrained to minimize first audio frame latency.
    /// </summary>
    private const int PreScanDurationStreamingMs = 45_000;

    #endregion

    #region Dependencies

    private readonly NetworkManager _networkManager;
    private readonly YoutubeProvider _youtube;
    private readonly LibraryService _library;
    private readonly AudioPlayer _player;
    private readonly TrackRegistry _trackRegistry;

    #endregion

    #region Synchronization

    private readonly Channel<IEngineCommand> _commandQueue;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Lock _queueLock = new();

    private SessionGuard _session;
    private CancellationTokenSource? _sessionCts;
    private readonly Lock _sessionLock = new();

    /// <summary>
    /// Single-flight tasks for initial continuation URL resolution by trackId.
    /// Shared between background priming and source-level lazy acquire.
    /// </summary>
    private readonly ConcurrentDictionary<string, Task<ContinuationUrlResult?>> _pendingUrlAcquisitions
        = new(StringComparer.Ordinal);

    /// <summary>
    /// Task for the command processing loop (<see cref="ProcessCommandsAsync"/>).
    /// Retained for deterministic awaiting during disposal:
    /// without awaiting, a race condition may occur—the command is already dequeued from the channel,
    /// but its handler has not yet finished mutating the player state.
    /// </summary>
    private Task? _commandProcessorTask;

    /// <summary>
    /// Task for the volume persistence loop (<see cref="VolumeSaveLoopAsync"/>).
    /// Awaited during disposal to guarantee flushing the last pending-write
    /// to persistent storage before closing the database.
    /// </summary>
    private Task? _volumeSaveTask;

    /// <summary>
    /// The single active playback preparation task.
    /// Guarantees single-flight: when a new PlayTrackCoreAsync arrives,
    /// the previous task is canceled via the session CTS, and the new one is awaited by the actor.
    /// Retained for deterministic awaiting to prevent overlapping
    /// of multiple concurrent ResolveStreamAsync / _player.PlayAsync executions.
    /// </summary>
    private Task? _activePlayTask;

    #endregion

    #region Playback State

    private volatile bool _isSuspended;
    private DateTime _lastQualitySwitchTime = DateTime.MinValue;
    private string? _nTokenActiveTrackId;
    private string? _nTokenWarnedTrackId;
    private string? _sealedFailedTrackId;
    private volatile bool _isManualLoading;

    // Playback intent. Not derived from _player.State: a track loaded "paused" stays in Stopped, not Paused
    private volatile bool _playIntent;

    /// <summary>
    /// Queue of pending integrated loudness writes to the database.
    /// Used for the new LUFS model.
    /// </summary>
    private readonly ConcurrentQueue<(string TrackId, float IntegratedLufs, LoudnessSource Source)> _pendingNormalizationWrites = new();

    /// <summary>
    /// Flag indicating completed disposal. Prevents double disposal
    /// when both paths (<see cref="DisposeAsync"/> and <see cref="Dispose(bool)"/>) are invoked.
    /// </summary>
    private volatile bool _disposed;

    /// <summary>
    /// Automatic retry counter for the current track on recoverable cache errors.
    /// Reset when changing tracks in <see cref="HandlePlayTrackAsync"/>
    /// and <see cref="HandleStartQueueAsync"/>.
    /// </summary>
    private int _cacheRetryCount;

    /// <summary>
    /// Indicates whether the active <see cref="Audio.Sources.CachingStreamSource"/>
    /// was actually suspended by the engine's lifecycle policy.
    /// </summary>
    private int _sourceLifecycleSuspended;

    #endregion

    #region Observable Properties

    [ObservableProperty] public partial TrackInfo? CurrentTrack { get; private set; }

    private AudioStreamInfo _streamInfo = AudioStreamInfo.Empty;
    public AudioStreamInfo StreamInfo
    {
        get => _streamInfo;
        private set => SetProperty(ref _streamInfo, value);
    }

    public bool IsPlaying => _player.State == PlaybackState.Playing;
    public bool IsPaused => _player.State == PlaybackState.Paused;

    /// <summary>
    /// Returns true if the player is currently buffering, loading, or seeking.
    /// </summary>
    public bool IsLoading => _isManualLoading || _player.State is PlaybackState.Loading or PlaybackState.Buffering || _player.DetailedState == PlayerState.Seeking;

    public int CurrentQueueIndex => Volatile.Read(ref _currentIndex);
    public bool ShuffleEnabled { get; set; }
    public RepeatMode RepeatMode { get; set; }

    public TimeSpan CurrentPosition => _player.Position;
    public TimeSpan TotalDuration => _player.Duration;
    public double BufferProgress => _player.BufferProgress;
    public bool IsFullyBuffered => _player.IsFullyBuffered;

    /// <summary>Current gain after volume curve, boost, and dB adjustment.</summary>
    public float CurrentGain => _currentGain;

    #endregion

    #region Events

    public event Action<TrackInfo?>? OnTrackChanged;
    public event Action<TimeSpan>? OnPositionChanged;
    public event Action<TimeSpan>? OnSeekCompleted;
    public event Action<bool, bool>? OnPlaybackStateChanged;
    public event Action? OnQueueChanged;
    public event Action<bool>? OnLoadingStateChanged;
    public event Action<int>? OnMaxVolumeChanged;
    public event Action? OnEqualizerChanged;
    public event Action? OnAudioSettingsChanged;
    public event Action<AudioStreamInfo>? OnStreamInfoChanged;
    public event Action<BufferState>? OnBufferStateChanged;
    public event Action? OnDeviceLost;
    public event Action? OnDeviceRestored;
    public event Action<Exception>? OnErrorOccurred;
    public event Action<NTokenWarningInfo>? OnNTokenDecryptionWarning;

    private readonly Action<TimeSpan> _positionChangedHandler;
    private readonly Action _raisePositionChangedOnUIDelegate;
    private long _currentPositionTicks;

    private readonly Action<BufferState> _bufferStateChangedHandler;
    private readonly Action _raiseBufferStateOnUIDelegate;
    private BufferState _currentBufferState;
    private readonly Lock _bufferStateLock = new();

    private readonly Action<TimeSpan> _seekCompletedHandler;
    private readonly Action _raiseSeekCompletedOnUIDelegate;
    private long _seekCompletedTicks;

    private readonly Action _deviceLostHandler;
    private readonly Action _raiseDeviceLostOnUIDelegate;

    private readonly Action _deviceRestoredHandler;
    private readonly Action _raiseDeviceRestoredOnUIDelegate;

    #endregion

    #region Constructor & Initialization

    /// <summary>
    /// Initializes the central playback engine.
    /// </summary>
    public AudioEngine(
        NetworkManager networkManager,
        YoutubeProvider youtube,
        LibraryService library,
        TrackRegistry trackRegistry)
    {
        _networkManager = networkManager;
        _youtube = youtube;
        _library = library;
        _trackRegistry = trackRegistry;

        StreamInfo = AudioStreamInfo.Empty;

        _positionChangedHandler = HandlePositionChangedInternal;
        _raisePositionChangedOnUIDelegate = () => OnPositionChanged?.Invoke(TimeSpan.FromTicks(Volatile.Read(ref _currentPositionTicks)));

        _bufferStateChangedHandler = HandleBufferStateChangedInternal;
        _raiseBufferStateOnUIDelegate = () => OnBufferStateChanged?.Invoke(GetLatestBufferState());

        _seekCompletedHandler = HandleSeekCompletedInternal;
        _raiseSeekCompletedOnUIDelegate = () => OnSeekCompleted?.Invoke(TimeSpan.FromTicks(Volatile.Read(ref _seekCompletedTicks)));

        _deviceLostHandler = HandleDeviceLostInternal;
        _raiseDeviceLostOnUIDelegate = () => OnDeviceLost?.Invoke();

        _deviceRestoredHandler = HandleDeviceRestoredInternal;
        _raiseDeviceRestoredOnUIDelegate = () => OnDeviceRestored?.Invoke();

        _player = new AudioPlayer(new AudioPlayerOptions
        {
            UrlProvider = ProvideStreamUrlAsync,
            PositionUpdateInterval = TimeSpan.FromMilliseconds(500),
            MaxRetryAttempts = 3,
            UseNullBackend = false,
            OnPipelineConfiguring = ConfigurePipelineBeforeStart,
            OnIntegratedLufsResolved = CommitIntegratedLufs,
            ShouldFastReplay = () => RepeatMode == RepeatMode.One,
            OnStarvationDetected = NotifyNetworkStarvation
        });

        if (_library.IsInitialized)
        {
            InitializeFromSettings();
        }
        else
        {
            ApplyStreamingProfile();
            _library.OnInitialized += InitializeFromSettings;
        }

        SubscribeToPlayerEvents();
        SubscribeNetworkManagerEvents();

        _youtube.OnNTokenDecryptionStarted += HandleNTokenDecryptionStarted;
        CdnConnectionPreWarmer.OnTunnelDeadDetected += HandleCdnTunnelDead;
        Audio.Sources.CachingStreamSource.OnNetworkStalled += HandleSourceNetworkStalled;
        Audio.Sources.CachingStreamSource.OnNetworkRecovered += HandleSourceNetworkRecovered;

        _commandQueue = Channel.CreateBounded<IEngineCommand>(
            new BoundedChannelOptions(CommandQueueCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest
            });

        _commandProcessorTask = Task.Run(ProcessCommandsAsync);
        _volumeSaveTask = Task.Run(VolumeSaveLoopAsync);

        LifecycleRegistry.Instance?.RegisterBackgroundSuspendable(this);
    }

    /// <summary>
    /// Retrieves saved playback parameters from settings and applies them to the active pipeline.
    /// </summary>
    private void InitializeFromSettings()
    {
        ApplyStreamingProfile();

        var settings = _library.Settings;
        ShuffleEnabled = settings.ShuffleEnabled;
        RepeatMode = settings.RepeatMode;

        InitializeVolumeFromSettings();
    }

    private void ApplyStreamingProfile()
    {
        AudioSourceFactory.ApplyInternetProfile(_library.Settings.InternetProfile);
    }

    #endregion

    #region Internal Event Delegates

    private void HandlePositionChangedInternal(TimeSpan pos)
    {
        Volatile.Write(ref _currentPositionTicks, pos.Ticks);
        RaiseOnUI(_raisePositionChangedOnUIDelegate);
    }

    private void HandleBufferStateChangedInternal(BufferState state)
    {
        lock (_bufferStateLock)
        {
            _currentBufferState = state;
        }
        RaiseOnUI(_raiseBufferStateOnUIDelegate);
    }

    private BufferState GetLatestBufferState()
    {
        lock (_bufferStateLock)
        {
            return _currentBufferState;
        }
    }

    private void HandleSeekCompletedInternal(TimeSpan t)
    {
        Volatile.Write(ref _seekCompletedTicks, t.Ticks);
        RaiseOnUI(_raiseSeekCompletedOnUIDelegate);
    }

    private void HandleDeviceLostInternal()
    {
        RaiseOnUI(_raiseDeviceLostOnUIDelegate);
    }

    private void HandleDeviceRestoredInternal()
    {
        RaiseOnUI(_raiseDeviceRestoredOnUIDelegate);
    }

    #endregion

    #region Session Management

    private int BeginNewSession()
    {
        int session = _session.BeginNew();
        CancellationTokenSource? oldCts;

        lock (_sessionLock)
        {
            oldCts = _sessionCts;
            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        }

        if (oldCts != null)
        {
            // Perform cancellation and socket closing on the thread pool,
            // to avoid freezing the UI dispatcher with synchronous CancellationToken callbacks.
            ThreadPool.UnsafeQueueUserWorkItem(static state =>
            {
                var cts = (CancellationTokenSource)state!;
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
                try { cts.Dispose(); } catch (ObjectDisposedException) { }
            }, oldCts);
        }

        return session;
    }

    private CancellationToken GetSessionToken()
    {
        lock (_sessionLock) return _sessionCts?.Token ?? _lifetimeCts.Token;
    }

    #endregion

    #region Playback Control API

    public Task PlayTrackAsync(TrackInfo track)
    {
        if (track == null) return Task.CompletedTask;
        ResetSealedFailedTrack();
        int session = BeginNewSession();
        EnqueueCommand(new PlayTrackCommand(track, session, null));
        return Task.CompletedTask;
    }

    public Task StartQueueAsync(IEnumerable<TrackInfo> tracks, TrackInfo startTrack)
    {
        ResetSealedFailedTrack();
        int session = BeginNewSession();
        EnqueueCommand(new StartQueueCommand(tracks, startTrack, session));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Starts or pauses playback and records user playback intent.
    /// </summary>
    /// <param name="shouldPlay">true to play, false to pause.</param>
    public async Task SetPlaybackStateAsync(bool shouldPlay)
    {
        _playIntent = shouldPlay;
        if (shouldPlay)
        {
            if (_player.State == PlaybackState.Paused) _player.Resume();
            else if (_player.State is PlaybackState.Stopped or PlaybackState.Error && CurrentTrack != null)
            {
                ResetSealedFailedTrack();
                int session = BeginNewSession();
                EnqueueCommand(new PlayCurrentIndexCommand(session));
            }
        }
        else _player.Pause();
    }

    /// <summary>
    /// Stops playback and clears track state.
    /// </summary>
    public void Stop()
    {
        _playIntent = false;
        BeginNewSession();
        _player.Stop();

        CurrentTrack = null;
        StreamInfo = AudioStreamInfo.Empty;

        RaiseOnUI(() =>
        {
            OnTrackChanged?.Invoke(null);
        });
    }

    public Task PlayNextAsync(bool startPlaying = true) { ResetSealedFailedTrack(); EnqueueCommand(new NavigateCommand(true, true, startPlaying)); return Task.CompletedTask; }
    public Task PlayPreviousAsync(bool startPlaying = true) { ResetSealedFailedTrack(); EnqueueCommand(new NavigateCommand(false, true, startPlaying)); return Task.CompletedTask; }

    #endregion

    #region Seek

    /// <summary>
    /// Performs seek immediately.
    /// </summary>
    public ValueTask SeekAsync(TimeSpan position)
    {
        return _player.SeekAsync(position);
    }

    #endregion

    #region Statistics

    internal AudioPipeline? GetActivePipeline() => _player.GetActivePipeline();
    public long GetDownloadedBytes() => _player.GetDownloadedBytes();
    public IReadOnlyList<(double Start, double End)> GetBufferedRanges() => _player.GetBufferedRanges();

    public (AudioFormat Format, int Bitrate, bool IsReady) GetCurrentStreamInfo()
    {
        var info = StreamInfo;
        return (info.Format, info.Bitrate, info.IsValid);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Thread-safely updates the engine manual load status on the UI thread.
    /// </summary>
    private void SetManualLoading(bool loading)
    {
        RaiseOnUI(() =>
        {
            if (_isManualLoading != loading)
            {
                _isManualLoading = loading;
                OnPropertyChanged(nameof(IsLoading));
                OnLoadingStateChanged?.Invoke(IsLoading);
            }
        });
    }

    private static void RaiseOnUI(Action action)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(action);
    }

    public static Task ReinitializeWithProfileAsync(InternetProfile profile)
    {
        AudioSourceFactory.ApplyInternetProfile(profile);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Extracts <see cref="CdnUnavailableException"/> from any level of the exception chain.
    /// </summary>
    private static Exceptions.CdnUnavailableException? TryExtractCdnException(Exception ex)
    {
        var current = ex;
        while (current is not null)
        {
            if (current is Exceptions.CdnUnavailableException cdn)
                return cdn;
            current = current.InnerException;
        }
        return null;
    }

    #endregion
}