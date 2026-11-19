using System.Collections.Concurrent;
using System.Threading.Channels;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Http;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Normalization;
using LMP.Core.Audio.Sources;

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
        bool IsRetry = false,
        int PauseEpoch = -1,
        bool BypassLocalDownload = false) : IEngineCommand;

    /// <summary>Start the queue from the specified track.</summary>
    private sealed record StartQueueCommand(
        IEnumerable<TrackInfo> Tracks,
        TrackInfo StartTrack,
        int Session,
        string? PlaylistId = null,
        string? PlaylistName = null,
        int PauseEpoch = -1) : IEngineCommand;

    /// <summary>Play the current queue index; when StartPlaying = false, the track is loaded in a paused state.</summary>
    private sealed record PlayCurrentIndexCommand(int Session, bool StartPlaying = true, int PauseEpoch = -1) : IEngineCommand;

    /// <summary>Navigate forward/backward.</summary>
    /// <param name="Forward">Navigation direction.</param>
    /// <param name="UserInitiated">Whether the action was user-initiated.</param>
    /// <param name="StartPlaying">Whether to start playback on the target track.</param>
    /// <param name="EndedTrackId">Id of the naturally finished track for auto-transition; null for transition without stale check.</param>
    /// <param name="ReplaceSeq">Queue replacement sequence captured when the command was enqueued.</param>
    /// <param name="Session">Session identifier for auto-advance staleness checks.</param>
    /// <param name="PauseEpoch">Pause generation captured when the command was enqueued.</param>
    private sealed record NavigateCommand(
        bool Forward,
        bool UserInitiated,
        bool StartPlaying = true,
        string? EndedTrackId = null,
        long ReplaceSeq = 0,
        int Session = -1,
        int PauseEpoch = -1) : IEngineCommand;

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

    private const int QualitySwitchCooldownMs = 250;

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
    private readonly Lock _playerDispatchLock = new();
    private long _queueReplaceSeq;
    private int _pauseEpoch;
    private int _nTokenActiveSession;
    private long _lastSwitchPositionTicks;

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
    private int _manualLoadingFlag;
    private int _lastDetailedPlaybackState = -1;

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
    private int _disposeState;
    private bool _disposed => Volatile.Read(ref _disposeState) != 0;

    /// <summary>
    /// Automatic retry counter for the current track on recoverable cache errors.
    /// Reset when changing tracks in <see cref="HandlePlayTrackAsync"/>
    /// and <see cref="HandleStartQueueAsync"/>.
    /// </summary>
    private int _cacheRetryCount;

    /// <summary>
    /// Indicates whether the active <see cref="CachingStreamSource"/>
    /// was actually suspended by the engine's lifecycle policy.
    /// </summary>
    private int _sourceLifecycleSuspended;

    #endregion

    #region Observable Properties

    private TrackInfo? _currentTrack;
    public TrackInfo? CurrentTrack
    {
        get => Volatile.Read(ref _currentTrack);
        private set
        {
            if (ReferenceEquals(Interlocked.Exchange(ref _currentTrack, value), value))
                return;

            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                OnPropertyChanged();
            else
                RaiseOnUI(_raiseCurrentTrackPropertyChangedDelegate);
        }
    }

    private AudioStreamInfo _streamInfo = AudioStreamInfo.Empty;
    public AudioStreamInfo StreamInfo
    {
        get => _streamInfo;
        private set
        {
            if (EqualityComparer<AudioStreamInfo>.Default.Equals(_streamInfo, value))
                return;

            _streamInfo = value;
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                OnPropertyChanged();
            else
                RaiseOnUI(_raiseStreamInfoPropertyChangedDelegate);
        }
    }

    private volatile bool _shuffleEnabled;
    private int _repeatMode = (int)RepeatMode.None;

    public bool IsPlaying => _player.State == PlaybackState.Playing;
    public bool IsPaused => _player.State == PlaybackState.Paused;

    /// <summary>
    /// Returns true if the player is currently buffering or loading.
    /// </summary>
    public bool IsLoading => Volatile.Read(ref _manualLoadingFlag) != 0
        || _player.State is PlaybackState.Loading or PlaybackState.Buffering;

    /// <summary>
    /// Effective playback state including manual loading before pipeline initialization.
    /// </summary>
    public PlaybackState EffectivePlaybackState
    {
        get
        {
            var state = _player.State;
            if (Volatile.Read(ref _manualLoadingFlag) != 0 && state is PlaybackState.Stopped or PlaybackState.Error)
                return PlaybackState.Loading;

            return state;
        }
    }

    /// <summary>Gets whether the user currently intends playback to be active.</summary>
    public bool ShouldPlayIntent => _playIntent;

    public int CurrentQueueIndex => Volatile.Read(ref _currentIndex);

    public bool ShuffleEnabled
    {
        get => _shuffleEnabled;
        set
        {
            if (_shuffleEnabled == value) return;
            _shuffleEnabled = value;
            RaiseOnUI(_raisePlaybackModesChangedOnUIDelegate);
        }
    }

    public RepeatMode RepeatMode
    {
        get => (RepeatMode)Volatile.Read(ref _repeatMode);
        set
        {
            int next = (int)value;
            if (Interlocked.Exchange(ref _repeatMode, next) == next) return;
            RaiseOnUI(_raisePlaybackModesChangedOnUIDelegate);
        }
    }

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
    public event Action<PlaybackState>? OnDetailedPlaybackStateChanged;
    public event Action<RepeatMode, bool>? OnPlaybackModesChanged;
    public event Action? OnQueueChanged;
    public event Action? OnQueueSourceChanged;
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

    private readonly Action _raiseQueueChangedOnUIDelegate;
    private readonly Action _raiseQueueSourceChangedOnUIDelegate;
    private readonly Action _raiseCurrentTrackPropertyChangedDelegate;
    private readonly Action _raiseStreamInfoPropertyChangedDelegate;

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
    private readonly Action _raiseManualLoadingChangedOnUIDelegate;
    private readonly Action _raisePlaybackModesChangedOnUIDelegate;
    private readonly Action _raiseStateChangedOnUIDelegate;

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

        _raiseQueueChangedOnUIDelegate = () =>
        {
            OnQueueChanged?.Invoke();
            OnQueueSourceChanged?.Invoke();
        };
        _raiseQueueSourceChangedOnUIDelegate = () => OnQueueSourceChanged?.Invoke();
        _raiseCurrentTrackPropertyChangedDelegate = () => OnPropertyChanged(nameof(CurrentTrack));
        _raiseStreamInfoPropertyChangedDelegate = () => OnPropertyChanged(nameof(StreamInfo));

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
        _raiseManualLoadingChangedOnUIDelegate = () =>
        {
            OnPropertyChanged(nameof(IsLoading));
            OnLoadingStateChanged?.Invoke(IsLoading);
            RaiseDetailedPlaybackStateIfChanged(EffectivePlaybackState);
        };
        _raisePlaybackModesChangedOnUIDelegate = () =>
            OnPlaybackModesChanged?.Invoke(RepeatMode, ShuffleEnabled);

        _raiseStateChangedOnUIDelegate = RaiseStateChangedOnUI;

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
        CachingStreamSource.OnNetworkStalled += HandleSourceNetworkStalled;
        CachingStreamSource.OnNetworkRecovered += HandleSourceNetworkRecovered;

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
        _networkManager.UpdateProfile(_library.Settings.InternetProfile);
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

    private const long SessionCtsDisposeGraceMs = 3_000;
    private readonly ConcurrentQueue<(CancellationTokenSource Cts, long DisposeAfterTick)> _retiredSessionCts = new();
    private readonly Lock _retiredCtsTimerLock = new();
    private Timer? _retiredCtsTimer;

    private int BeginNewSession()
    {
        int session = _session.BeginNew();
        CancellationTokenSource? oldCts;

        lock (_sessionLock)
        {
            oldCts = _sessionCts;
            _sessionCts = Volatile.Read(ref _disposeState) == 0
                ? CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token)
                : null;
        }

        DrainExpiredRetiredSessionCts();

        if (oldCts != null)
        {
            long disposeAt = Environment.TickCount64 + SessionCtsDisposeGraceMs;
            _retiredSessionCts.Enqueue((oldCts, disposeAt));
            ArmRetiredCtsCleanupTimer();

            // Cancel on ThreadPool to keep UI thread responsive while SessionGuard rejects stale callers synchronously.
            ThreadPool.UnsafeQueueUserWorkItem(static state =>
            {
                try { ((CancellationTokenSource)state!).Cancel(); }
                catch (ObjectDisposedException) { }
            }, oldCts);
        }

        return session;
    }

    private void ArmRetiredCtsCleanupTimer()
    {
        if (Volatile.Read(ref _disposeState) != 0)
            return;

        lock (_retiredCtsTimerLock)
        {
            if (Volatile.Read(ref _disposeState) != 0)
                return;

            _retiredCtsTimer ??= new Timer(static state => ((AudioEngine)state!).OnRetiredCtsCleanupTimer(), this, Timeout.Infinite, Timeout.Infinite);
            _retiredCtsTimer.Change(SessionCtsDisposeGraceMs + 100, Timeout.Infinite);
        }
    }

    private void OnRetiredCtsCleanupTimer()
    {
        DrainExpiredRetiredSessionCts();

        if (!_retiredSessionCts.IsEmpty && Volatile.Read(ref _disposeState) == 0)
            ArmRetiredCtsCleanupTimer();
    }

    private void DrainExpiredRetiredSessionCts()
    {
        long now = Environment.TickCount64;
        while (_retiredSessionCts.TryPeek(out var head) && head.DisposeAfterTick <= now)
        {
            if (_retiredSessionCts.TryDequeue(out var item))
            {
                try { item.Cts.Dispose(); }
                catch (ObjectDisposedException) { }
            }
        }
    }

    private void DisposeRetiredSessionCts()
    {
        lock (_retiredCtsTimerLock)
        {
            _retiredCtsTimer?.Dispose();
            _retiredCtsTimer = null;
        }

        while (_retiredSessionCts.TryDequeue(out var item))
        {
            try { item.Cts.Dispose(); }
            catch (ObjectDisposedException) { }
        }
    }

    private CancellationToken GetSessionToken()
    {
        lock (_sessionLock)
        {
            if (Volatile.Read(ref _disposeState) != 0 || _sessionCts == null)
                return new CancellationToken(canceled: true);

            return _sessionCts.Token;
        }
    }

    #endregion

    #region Playback Control API

    public Task PlayTrackAsync(TrackInfo track)
    {
        if (track == null) return Task.CompletedTask;
        ResetSealedFailedTrack();

        int session;
        int pauseEpoch;
        lock (_playerDispatchLock)
        {
            _playIntent = true;
            Interlocked.Increment(ref _queueReplaceSeq);
            pauseEpoch = Volatile.Read(ref _pauseEpoch);
            session = BeginNewSession();
        }

        EnqueueCommand(new PlayTrackCommand(track, session, null, PauseEpoch: pauseEpoch));
        return Task.CompletedTask;
    }

    public Task StartQueueAsync(
        IEnumerable<TrackInfo> tracks,
        TrackInfo startTrack,
        string? playlistId = null,
        string? playlistName = null)
    {
        ResetSealedFailedTrack();

        int session;
        int pauseEpoch;
        lock (_playerDispatchLock)
        {
            _playIntent = true;
            Interlocked.Increment(ref _queueReplaceSeq);
            pauseEpoch = Volatile.Read(ref _pauseEpoch);
            session = BeginNewSession();
        }

        EnqueueCommand(new StartQueueCommand(tracks, startTrack, session, playlistId, playlistName, PauseEpoch: pauseEpoch));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Starts or resumes playback idempotently and records user playback intent.
    /// </summary>
    public Task PlayAsync()
    {
        bool notifyManualLoadingPlay = false;

        lock (_playerDispatchLock)
        {
            _playIntent = true;

            var state = _player.State;
            if (state is PlaybackState.Stopped or PlaybackState.Error)
            {
                if (Volatile.Read(ref _manualLoadingFlag) == 0 && CurrentTrack != null)
                {
                    ResetSealedFailedTrack();
                    int pauseEpoch = Volatile.Read(ref _pauseEpoch);
                    int session = BeginNewSession();
                    EnqueueCommand(new PlayCurrentIndexCommand(session, StartPlaying: true, PauseEpoch: pauseEpoch));
                }
                else if (Volatile.Read(ref _manualLoadingFlag) != 0)
                {
                    notifyManualLoadingPlay = true;
                }
            }
            else
            {
                _player.Resume();
            }
        }

        if (notifyManualLoadingPlay)
            RaiseOnUI(_raiseManualLoadingChangedOnUIDelegate);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Pauses playback idempotently and records user pause intent.
    /// </summary>
    public Task PauseAsync()
    {
        bool notifyManualLoadingPause = false;

        lock (_playerDispatchLock)
        {
            _playIntent = false;
            Interlocked.Increment(ref _pauseEpoch);
            if (_player.State is PlaybackState.Stopped or PlaybackState.Error
                && Volatile.Read(ref _manualLoadingFlag) != 0)
            {
                notifyManualLoadingPause = true;
            }
            _player.Pause();
        }

        if (notifyManualLoadingPause)
            RaiseOnUI(_raiseManualLoadingChangedOnUIDelegate);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Atomically toggles playback intent under <see cref="_playerDispatchLock"/>.
    /// </summary>
    internal Task TogglePlaybackStateAsync()
    {
        lock (_playerDispatchLock)
        {
            return _playIntent ? PauseAsync() : PlayAsync();
        }
    }

    /// <summary>
    /// Starts or pauses playback and records user playback intent.
    /// </summary>
    /// <param name="shouldPlay">true to play, false to pause.</param>
    public Task SetPlaybackStateAsync(bool shouldPlay) =>
        shouldPlay ? PlayAsync() : PauseAsync();

    /// <summary>
    /// Stops active audio playback and resets position to zero while keeping the current track and queue intact.
    /// </summary>
    public void StopPlaybackKeepTrack()
    {
        StopCore(clearTrackAndSource: false);
    }

    /// <summary>
    /// Stops playback and clears track state.
    /// </summary>
    public void Stop()
    {
        StopCore(clearTrackAndSource: true);
    }

    private void StopCore(bool clearTrackAndSource)
    {
        lock (_playerDispatchLock)
        {
            _playIntent = false;
            Interlocked.Increment(ref _pauseEpoch);
            Interlocked.Increment(ref _queueReplaceSeq);
            BeginNewSession();
            _player.Stop();
        }

        SetManualLoading(false);
        Volatile.Write(ref _lastSwitchPositionTicks, 0);
        Volatile.Write(ref _currentPositionTicks, 0);

        if (!clearTrackAndSource)
        {
            RaiseOnUI(_raisePositionChangedOnUIDelegate);
            return;
        }

        bool sourceChanged = false;
        lock (_queueLock)
        {
            if (_queueSource.Kind != QueueSourceKind.Manual || _isQueuePure)
            {
                _queueSource = TransitionOnClearQueue(ref _queueSourceGeneration);
                _isQueuePure = false;
                sourceChanged = true;
            }
        }

        CurrentTrack = null;
        StreamInfo = AudioStreamInfo.Empty;

        RaiseOnUI(() =>
        {
            OnPositionChanged?.Invoke(TimeSpan.Zero);
            OnTrackChanged?.Invoke(null);
            if (sourceChanged)
                OnQueueSourceChanged?.Invoke();
        });
    }

    public Task PlayNextAsync(bool startPlaying = true)
    {
        ResetSealedFailedTrack();
        long replaceSeq;
        int pauseEpoch;
        lock (_playerDispatchLock)
        {
            replaceSeq = Volatile.Read(ref _queueReplaceSeq);
            pauseEpoch = startPlaying ? Volatile.Read(ref _pauseEpoch) : -1;
        }

        EnqueueCommand(new NavigateCommand(true, true, startPlaying, ReplaceSeq: replaceSeq, PauseEpoch: pauseEpoch));
        return Task.CompletedTask;
    }

    public Task PlayPreviousAsync(bool startPlaying = true)
    {
        ResetSealedFailedTrack();
        long replaceSeq;
        int pauseEpoch;
        lock (_playerDispatchLock)
        {
            replaceSeq = Volatile.Read(ref _queueReplaceSeq);
            pauseEpoch = startPlaying ? Volatile.Read(ref _pauseEpoch) : -1;
        }

        EnqueueCommand(new NavigateCommand(false, true, startPlaying, ReplaceSeq: replaceSeq, PauseEpoch: pauseEpoch));
        return Task.CompletedTask;
    }

    #endregion

    #region Seek

    /// <summary>
    /// Performs seek immediately.
    /// </summary>
    public ValueTask SeekAsync(TimeSpan position)
    {
        Volatile.Write(ref _lastSwitchPositionTicks, position.Ticks);
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
        int newVal = loading ? 1 : 0;
        if (Interlocked.Exchange(ref _manualLoadingFlag, newVal) == newVal)
            return;

        RaiseOnUI(_raiseManualLoadingChangedOnUIDelegate);
    }

    private void RaiseDetailedPlaybackStateIfChanged(PlaybackState effectiveState)
    {
        int next = (int)effectiveState;
        if (Interlocked.Exchange(ref _lastDetailedPlaybackState, next) != next)
            OnDetailedPlaybackStateChanged?.Invoke(effectiveState);
    }

    private void RaiseStateChangedOnUI()
    {
        var liveState = _player.State;
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(TotalDuration));
        OnPlaybackStateChanged?.Invoke(liveState == PlaybackState.Playing, liveState == PlaybackState.Paused);
        OnLoadingStateChanged?.Invoke(IsLoading);

        RaiseDetailedPlaybackStateIfChanged(EffectivePlaybackState);
    }

    private static void RaiseOnUI(Action action)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(action);
    }

    public static Task ReinitializeWithProfileAsync(InternetProfile profile)
    {
        NetworkManager.ApplyGlobalProfile(profile);
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