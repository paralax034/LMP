using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Exceptions;

namespace LMP.Core.Audio;

/// <summary>
/// Audio player utilizing an actor model for command processing.
/// </summary>
/// <remarks>
/// <para><b>Decomposition (partial classes):</b></para>
/// <list type="bullet">
///   <item><c>AudioPlayer.cs</c> — core: command loop, state machine, play/stop/resume.</item>
///   <item><c>AudioPlayer.Seek.cs</c> — two-phase seek.</item>
///   <item><c>AudioPlayer.DeviceRecovery.cs</c> — recovery after device loss.</item>
///   <item><c>AudioPlayer.Timers.cs</c> — management of UI update timers.</item>
/// </list>
/// </remarks>
internal sealed partial class AudioPlayer : IAsyncDisposable, IDisposable
{
    #region Constants

    private const int CommandChannelCapacity = 32;
    private const int StopTaskTimeoutSec = 2;
    private const int DisposeTaskTimeoutSec = 2;
    private const int ResumeMinBufferMs = 100;
    private const int ResumeWarmupTimeoutMs = 500;
    /// <summary>Decoder stop timeout during fast-rewind (ms).</summary>
    private const int RewindDecoderStopTimeoutMs = 300;
    private const int BitsPerByte = 8;

    #endregion

    #region Nested Types

    /// <summary>
    /// Warmup plan before opening the playback gate.
    /// </summary>
    /// <param name="PcmThresholdSamples">Minimum PCM volume in ring buffer.</param>
    /// <param name="SourceAheadMs">Minimum continuous data headroom in source.</param>
    /// <param name="WarmupTimeoutMs">Active warmup wait timeout.</param>
    private readonly record struct PlaybackWarmupPlan(
        int PcmThresholdSamples,
        int SourceAheadMs,
        int WarmupTimeoutMs);

    /// <summary>
    /// User intent regarding the playback lifecycle.
    /// Separated from the operational state machine.
    /// </summary>
    private enum PlaybackIntent
    {
        Stop = 0,
        Pause = 1,
        Play = 2
    }

    #endregion

    #region Fields

    private readonly AudioPlayerOptions _options;
    private readonly AudioPlayerEvents _events = new();
    private readonly Channel<IAudioCommand> _commandChannel;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Task _commandProcessorTask;
    private readonly IPlaybackBackend _sharedBackend;

    private volatile AudioPipeline? _activePipeline;
    private volatile PlayerState _state = PlayerState.Idle;
    private volatile bool _disposed;

    private int _playbackIntent = (int)PlaybackIntent.Stop;
    private int _seekGeneration;

    private TaskCompletionSource<bool>? _pendingPlayCompletion;
    private CancellationTokenSource? _activePlayCts;

    /// <summary>
    /// Task for disposing the previous pipeline.
    /// </summary>
    private Task? _oldPipelineDisposeTask;

    private SessionGuard _session;

    #endregion

    #region Properties

    /// <summary>Player events.</summary>
    public AudioPlayerEvents Events => _events;

    /// <summary>Current detailed state.</summary>
    public PlayerState DetailedState => _state;

    /// <summary>
    /// Current playback position according to the active pipeline render clock: clean read without locks, mutations, or allocations.
    /// </summary>
    public TimeSpan Position => _activePipeline?.GetPlaybackPosition() ?? TimeSpan.Zero;

    /// <summary>Duration of the current track.</summary>
    public TimeSpan Duration => _activePipeline != null
        ? TimeSpan.FromMilliseconds(_activePipeline.StreamInfo.DurationMs)
        : TimeSpan.Zero;

    /// <summary>Current public playback state.</summary>
    public PlaybackState State => MapState(_state);

    /// <summary>Current stream information.</summary>
    public AudioStreamInfo StreamInfo => _activePipeline?.StreamInfo ?? AudioStreamInfo.Empty;

    #endregion

    #region Constructor

    /// <summary>Creates a new player instance.</summary>
    public AudioPlayer(AudioPlayerOptions? options = null)
    {
        _options = options ?? new AudioPlayerOptions();

        _commandChannel = Channel.CreateBounded<IAudioCommand>(
            new BoundedChannelOptions(CommandChannelCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            },
            static dropped =>
            {
                if (dropped is PlayCommand { Completion: { } playTcs })
                    playTcs.TrySetCanceled();
                else if (dropped is SeekCommand { Completion: { } seekTcs })
                    seekTcs.TrySetCanceled();
            });

        _sharedBackend = CreateSharedBackend(_options);
        _commandProcessorTask = Task.Run(ProcessCommandsAsync);
    }

    private static IPlaybackBackend CreateSharedBackend(AudioPlayerOptions options)
    {
        if (options.UseNullBackend)
            return new Backends.NullAudioBackend();

        // Runtime platform check (folds to true when building Native AOT for win-x64)
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return new Backends.WinAudioBackend();
            }
            catch (Exception ex)
            {
                Log.Warn($"[AudioPlayer] Windows audio backend initialization failed: {ex.Message}, falling back to NullAudioBackend");
                return new Backends.NullAudioBackend();
            }
        }

        Log.Warn("[AudioPlayer] Native audio playback on Linux and macOS requires a dedicated platform backend. Falling back to NullAudioBackend.");
        return new Backends.NullAudioBackend();
    }

    #endregion

    #region Public API

    /// <summary>
    /// Initiates playback start.
    /// </summary>
    private void Play(
        ResolvedStreamDescriptor descriptor,
        TimeSpan? seekPosition = null,
        CancellationToken ct = default,
        TaskCompletionSource<bool>? completion = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        CompletePendingPlayCanceled();
        if (completion != null)
            Volatile.Write(ref _pendingPlayCompletion, completion);

        SetPlaybackIntent(PlaybackIntent.Play);
        CancelActiveSeek();
        CancelActivePlayInit();

        int session = _session.BeginNew();

        if (!_commandChannel.Writer.TryWrite(
            new PlayCommand(descriptor, session, seekPosition, ct, completion)))
        {
            if (completion != null)
                Interlocked.CompareExchange(ref _pendingPlayCompletion, null, completion);
            completion?.TrySetCanceled();
        }
    }

    /// <summary>
    /// Starts playback and awaits Playing state.
    /// </summary>
    public Task PlayAsync(
        ResolvedStreamDescriptor descriptor,
        CancellationToken ct = default,
        TimeSpan? seekPosition = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Play(descriptor, seekPosition, ct, tcs);
        return ct.CanBeCanceled ? AwaitPlayCompletionAsync(tcs, ct) : tcs.Task;
    }

    private static async Task AwaitPlayCompletionAsync(TaskCompletionSource<bool> tcs, CancellationToken ct)
    {
        using var reg = ct.UnsafeRegister(static state =>
            ((TaskCompletionSource<bool>)state!).TrySetCanceled(), tcs);

        await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>Pauses playback.</summary>
    public void Pause()
    {
        if (_disposed) return;

        SetPlaybackIntent(PlaybackIntent.Pause);
        CancelActiveSeek();
        _commandChannel.Writer.TryWrite(new PauseCommand(_session.Current));
    }

    /// <summary>Resumes playback.</summary>
    public void Resume()
    {
        if (_disposed) return;

        SetPlaybackIntent(PlaybackIntent.Play);
        _commandChannel.Writer.TryWrite(new ResumeCommand(_session.Current));
    }

    /// <summary>Stops playback.</summary>
    public void Stop()
    {
        SetPlaybackIntent(PlaybackIntent.Stop);
        CancelActiveSeek();
        CancelActivePlayInit();
        CompletePendingPlayCanceled();

        int session = _session.BeginNew();
        if (_state is PlayerState.Idle or PlayerState.Disposed)
            return;

        _commandChannel.Writer.TryWrite(new StopCommand(session));
    }

    /// <summary>Asynchronous stop.</summary>
    public async Task StopAsync()
    {
        SetPlaybackIntent(PlaybackIntent.Stop);
        CancelActiveSeek();
        CancelActivePlayInit();
        CompletePendingPlayCanceled();

        int session = _session.BeginNew();
        if (_state is PlayerState.Idle or PlayerState.Disposed)
            return;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnState(PlaybackState s)
        {
            if (s != PlaybackState.Stopped) return;
            _events.StateChanged -= OnState;
            tcs.TrySetResult(true);
        }

        _events.StateChanged += OnState;
        await _commandChannel.Writer.WriteAsync(new StopCommand(session)).ConfigureAwait(false);

        try
        {
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(StopTaskTimeoutSec)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _events.StateChanged -= OnState;
        }
    }

    /// <summary>Immediately applies volume gain.</summary>
    public void SetVolumeGain(float gain) => _sharedBackend.SetVolumeGain(gain);

    #endregion

    #region Command Processing

    /// <summary>
    /// Main actor loop.
    /// </summary>
    private async Task ProcessCommandsAsync()
    {
        try
        {
            await foreach (var command in _commandChannel.Reader
                .ReadAllAsync(CancellationToken.None)
                .ConfigureAwait(false))
            {
                if (_session.IsStale(command.SessionId) && command is not DisposeCommand)
                {
                    if (command is SeekCommand { Completion: { } tcs })
                        tcs.TrySetCanceled();
                    else if (command is PlayCommand { Completion: { } stalePlayTcs })
                        stalePlayTcs.TrySetCanceled();

                    continue;
                }

                if (!PlayerStateTransitions.CanAcceptCommand(_state, command))
                {
                    Log.Debug($"[AudioPlayer] Command {command.GetType().Name} rejected in state {_state}");

                    if (command is SeekCommand { Completion: { } failTcs })
                        failTcs.TrySetResult(false);
                    else if (command is PlayCommand { Completion: { } rejectPlayTcs })
                        rejectPlayTcs.TrySetCanceled();

                    continue;
                }

                try
                {
                    switch (command)
                    {
                        case PlayCommand play:
                            await HandlePlayAsync(play).ConfigureAwait(false);
                            break;

                        case StopCommand:
                            await HandleStopAsync().ConfigureAwait(false);
                            break;

                        case PauseCommand:
                            await HandlePauseAsync().ConfigureAwait(false);
                            break;

                        case ResumeCommand resume:
                            await HandleResumeAsync(resume).ConfigureAwait(false);
                            break;

                        case SeekCommand seek:
                            await HandleSeekAsync(seek).ConfigureAwait(false);
                            break;

                        case DeferredResumeCommand deferredResume:
                            await HandleDeferredResumeAsync(deferredResume).ConfigureAwait(false);
                            break;

                        case StarvationCommand starvation:
                            await HandleStarvationAsync(starvation).ConfigureAwait(false);
                            break;

                        case TrackEndedCommand trackEnded:
                            await HandleTrackEndedAsync(trackEnded).ConfigureAwait(false);
                            break;

                        case DeviceLostCommand deviceLost:
                            await HandleDeviceLostAsync(deviceLost).ConfigureAwait(false);
                            break;

                        case DeviceAvailableCommand deviceAvailable:
                            await HandleDeviceAvailableAsync(deviceAvailable).ConfigureAwait(false);
                            break;

                        case DeviceRecoveryCommand recovery:
                            await HandleDeviceRecoveryAsync(recovery).ConfigureAwait(false);
                            break;

                        case PlayerErrorCommand error:
                            await HandlePlayerErrorAsync(error).ConfigureAwait(false);
                            break;

                        case DisposeCommand:
                            await HandleDisposeAsync().ConfigureAwait(false);
                            return;
                    }
                }
                catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (CancellationHelper.IsCancellationLike(ex) || _session.IsStale(command.SessionId))
                        continue;

                    Log.Error($"[AudioPlayer] Command error: {ex.Message}", ex);
                    HandleError(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Task HandlePauseAsync()
    {
        SetPlaybackIntent(PlaybackIntent.Pause);
        CancelActiveSeek();
        StopTimers();

        var pipeline = _activePipeline;
        pipeline?.Stop();

        if (_state is not (PlayerState.Idle or PlayerState.Disposed))
            SetState(PlayerState.Paused);

        CompletePendingPlayResult(false);
        return Task.CompletedTask;
    }

    private async Task HandleResumeAsync(ResumeCommand cmd)
    {
        SetPlaybackIntent(PlaybackIntent.Play);

        var pipeline = _activePipeline;
        if (pipeline == null) return;

        if (pipeline.IsDeviceLost)
        {
            SetState(PlayerState.Buffering);
            await HandleDeviceRecoveryAsync(new DeviceRecoveryCommand(cmd.SessionId)).ConfigureAwait(false);
            return;
        }

        // Proactive check: if URL expired during a long pause/sleep,
        // refresh it BEFORE opening gates and warming up the buffer
        if (pipeline.Source is Sources.CachingStreamSource cachingSource)
        {
            await cachingSource.EnsureStreamFreshnessAsync(_lifetimeCts.Token).ConfigureAwait(false);
        }

        int minBytes = pipeline.SampleRate * pipeline.Channels * sizeof(float) * ResumeMinBufferMs / 1000;
        if (_sharedBackend.BufferedBytes < minBytes)
        {
            pipeline.ActivateFillLoop();
            await pipeline.WaitForBackendWarmupAsync(ResumeWarmupTimeoutMs, _lifetimeCts.Token).ConfigureAwait(false);
        }

        ResumePlaybackSequence(pipeline, startTimers: true, configurePipeline: false);
    }

    private Task HandleStopAsync()
    {
        SetPlaybackIntent(PlaybackIntent.Stop);
        CancelActiveSeek();
        StopTimers();
        CompletePendingPlayCanceled();

        var pipeline = Interlocked.Exchange(ref _activePipeline, null);
        // Capture tail and flush backend BEFORE starting background disposal so AudioCallback never hits a disposed ring buffer.
        _sharedBackend.Flush();
        if (pipeline != null)
        {
            pipeline.Source.CancelPendingOperations();
            TrackAndFirePipelineDispose(pipeline);
        }

        SetState(PlayerState.Idle);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles the Dispose command.
    /// </summary>
    private async Task HandleDisposeAsync()
    {
        SetPlaybackIntent(PlaybackIntent.Stop);
        await HandleStopAsync().ConfigureAwait(false);

        var oldDisposeTask = Volatile.Read(ref _oldPipelineDisposeTask);
        if (oldDisposeTask != null && !oldDisposeTask.IsCompleted)
        {
            try
            {
                await oldDisposeTask.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Log.Warn("[AudioPlayer] Old pipeline dispose did not complete within HandleDisposeAsync timeout");
            }
            catch
            {
            }
        }

        DisposeTimers();
        SetState(PlayerState.Disposed);
    }

    /// <summary>
    /// Unified playback resume sequence.
    /// </summary>
    private void ResumePlaybackSequence(
        AudioPipeline pipeline,
        bool startTimers,
        bool configurePipeline)
    {
        // The track is retrieved directly from the pipeline: the player has no internal "current track" that a Stop/Play race could zero out
        if (configurePipeline)
            _options.OnPipelineConfiguring?.Invoke(pipeline, pipeline.StreamInfo.TrackId);

        pipeline.ActivateFillLoop();
        pipeline.Start();

        if (startTimers)
            StartTimers();

        SetState(PlayerState.Playing);
        CompletePendingPlayResult(true);
    }

    /// <summary>
    /// Builds an adaptive playback gate warmup plan.
    /// </summary>
    private static PlaybackWarmupPlan ComputePlaybackWarmupPlan(AudioPipeline pipeline, bool isSeek)
    {
        if (pipeline.Source.IsFullyBuffered || pipeline.Source is Sources.LocalFileSource)
        {
            // For local file/full cache, wait for at least 120 ms of decoded PCM samples (with up to 500 ms timeout buffer),
            // so that on Seek to the end of a 10-second WebM cluster, the decoder manages to complete 160 ms of pre-roll AND buffer
            // at least two full 60-ms WASAPI horizons before opening the gate (prevents delayed underrun 15 ms after Seek!).
            int localPcmSamples = pipeline.SampleRate * pipeline.Channels * 120 / 1000;
            return new PlaybackWarmupPlan(Math.Max(localPcmSamples, pipeline.Channels * 2), 0, 500);
        }

        double avgPingMs = pipeline.Source is Sources.CachingStreamSource cs
            ? cs.AveragePingMs
            : 0;

        int sourceAheadMs = avgPingMs switch
        {
            <= 0 => isSeek ? 500 : 750,
            <= 150 => isSeek ? 600 : 800,
            <= 400 => isSeek ? 800 : 1200,
            <= 900 => isSeek ? 1200 : 2000,
            <= 2500 => isSeek ? 2000 : 3500,
            _ => isSeek ? 3000 : 5000
        };

        int pcmThresholdMs = avgPingMs switch
        {
            <= 0 => isSeek ? 60 : 80,
            <= 150 => isSeek ? 80 : 100,
            <= 400 => isSeek ? 100 : 120,
            <= 900 => isSeek ? 150 : 180,
            <= 2500 => isSeek ? 250 : 300,
            _ => isSeek ? 400 : 450
        };

        int warmupTimeoutMs = avgPingMs switch
        {
            <= 0 => isSeek ? 200 : 600,
            <= 150 => isSeek ? 300 : 800,
            <= 400 => isSeek ? 500 : 1500,
            <= 900 => isSeek ? 1000 : 3000,
            <= 2500 => isSeek ? 2000 : 7000,
            _ => isSeek ? 4000 : 10000
        };

        // NToken / network-contention guard
        if (!isSeek
            && pipeline.Source is Sources.CachingStreamSource csForBandwidth
            && csForBandwidth.EstimatedSpeedBytesPerSec <= 0)
        {
            sourceAheadMs = Math.Max(sourceAheadMs, 3000);
            warmupTimeoutMs = Math.Max(warmupTimeoutMs, 8000);
        }

        int samples = pipeline.SampleRate * pipeline.Channels * pcmThresholdMs / 1000;
        return new PlaybackWarmupPlan(Math.Max(samples, pipeline.Channels), sourceAheadMs, warmupTimeoutMs);
    }

    /// <summary>
    /// Returns continuous source-ahead headroom in milliseconds.
    /// </summary>
    private static int GetSourceBufferedAheadMs(AudioPipeline pipeline)
    {
        if (pipeline.Source.IsFullyBuffered || pipeline.Source is Sources.LocalFileSource)
            return int.MaxValue;

        if (pipeline.Source is Sources.CachingStreamSource cs)
        {
            if (cs.IsTailLocallyBuffered)
                return int.MaxValue;

            return cs.BufferedAheadMs;
        }

        return 0;
    }

    /// <summary>
    /// Checks whether the source-ahead headroom is sufficient to open the gate.
    /// </summary>
    private static bool IsSourceReadyForResume(AudioPipeline pipeline, int requiredAheadMs)
    {
        if (requiredAheadMs <= 0) return true;
        return GetSourceBufferedAheadMs(pipeline) >= requiredAheadMs;
    }

    /// <summary>
    /// Prepares the pipeline and starts the decoder.
    /// </summary>
    private async Task PrepareAndStartDecodingAsync(
        AudioPipeline pipeline,
        PlayCommand cmd,
        CancellationToken ct)
    {
        // Launch normalization pre-scan concurrently so decoder startup does not stall on disk/codec I/O.
        pipeline.StartPreScanNormalization();

        if (cmd.SeekPosition is { TotalMilliseconds: > 0 } seekPos)
        {
            long seekMs = (long)seekPos.TotalMilliseconds;
            pipeline.PrepareForSeek(seekMs);

            if (await pipeline.Source.SeekAsync(seekMs, ct).ConfigureAwait(false))
            {
                pipeline.SetDecodedSamplesPosition(
                    (long)(seekMs / 1000.0 * pipeline.SampleRate * pipeline.Channels));
            }
        }

        pipeline.StartDecoding(
            _options,
            CreateTrackEndedCallback(cmd.SessionId),
            CreateErrorCallback(cmd.SessionId, pipeline));
    }

    /// <summary>
    /// Plays a new stream descriptor in the context of the session.
    /// </summary>
    private async Task HandlePlayAsync(PlayCommand cmd)
    {
        Log.Info($"[AudioPlayer] HandlePlayAsync -> {cmd.Descriptor}, seek={cmd.SeekPosition?.TotalMilliseconds ?? 0}ms");

        var prevCompletion = Interlocked.Exchange(ref _pendingPlayCompletion, cmd.Completion);
        if (!ReferenceEquals(prevCompletion, cmd.Completion))
            prevCompletion?.TrySetCanceled();

        SetPlaybackIntent(PlaybackIntent.Play);
        SetState(PlayerState.Loading);
        CancelActiveSeek();

        ResetPerTrackCounters();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCts.Token, cmd.ExternalCancellationToken);
        var prevPlayCts = Interlocked.Exchange(ref _activePlayCts, linkedCts);
        CancelCtsAsync(prevPlayCts);
        var ct = linkedCts.Token;

        float previousGain = _activePipeline?.GetLockedNormalizationGain() ?? 1.0f;

        // Before stopping the previous track, call _sharedBackend.Flush() while the old _pcmBuffer is still full!
        // This allows WinAudioBackend to synchronously fetch 10 ms of the live continuation of the old track
        // for an absolutely seamless wave-to-wave crossfade to the new track without a single pop/click.
        var previousPipeline = Interlocked.Exchange(ref _activePipeline, null);
        if (previousPipeline != null)
        {
            StopTimers();
            _sharedBackend.Flush();
            previousPipeline.Source.CancelPendingOperations();
            TrackAndFirePipelineDispose(previousPipeline);
        }

        try
        {
            ct.ThrowIfCancellationRequested();

            var pipeline = await AudioPipeline.CreateAsync(
                cmd.Descriptor,
                _options,
                _sharedBackend,
                ct).ConfigureAwait(false);

            if (_session.IsStale(cmd.SessionId))
            {
                CompletePendingPlayCanceled();
                await pipeline.DisposeAsync().ConfigureAwait(false);
                return;
            }

            ct.ThrowIfCancellationRequested();

            StopTimers();

            var oldPipeline = Interlocked.Exchange(ref _activePipeline, pipeline);
            if (oldPipeline != null)
            {
                _sharedBackend.Flush();
                oldPipeline.Source.CancelPendingOperations();
                TrackAndFirePipelineDispose(oldPipeline);
            }

            // Reinitialize inside AudioPipeline.CreateAsync already captured the tail and rebased the render clock to 0.
            pipeline.SetDecodedSamplesPosition(0);

            _events.RaiseStreamInfo(pipeline.StreamInfo);

            int capturedSession = cmd.SessionId;
            pipeline.SetDeviceLostHandler(() => OnPipelineDeviceLost(pipeline, capturedSession));
            pipeline.SetDeviceAvailableHandler(() => OnPipelineDeviceAvailable(pipeline, capturedSession));
            pipeline.SetStarvationHandler(() => OnPipelineStarvation(pipeline, capturedSession));

            pipeline.SetInitialNormalizationGain(previousGain);
            _options.OnPipelineConfiguring?.Invoke(pipeline, cmd.Descriptor.TrackId);

            var lockedTrackId = cmd.Descriptor.TrackId;
            if (lockedTrackId != null && _options.OnIntegratedLufsResolved != null)
            {
                var lufsCb = _options.OnIntegratedLufsResolved;
                pipeline.Analyzer.SetIntegratedLufsCallback(
                    lufs => lufsCb(lockedTrackId, lufs, Normalization.LoudnessSource.EbuMeasured));
            }

            if (pipeline.IsDeviceLost)
            {
                await PrepareAndStartDecodingAsync(pipeline, cmd, ct).ConfigureAwait(false);
                SetState(PlayerState.Paused);
                CompletePendingPlayResult(false);
                _events.RaiseDeviceLost();
                return;
            }

            await PrepareAndStartDecodingAsync(pipeline, cmd, ct).ConfigureAwait(false);
            SetState(PlayerState.Buffering);

            var warmupPlan = ComputePlaybackWarmupPlan(pipeline, isSeek: false);

            bool pcmReady = await pipeline.WaitForBufferAsync(
                warmupPlan.PcmThresholdSamples,
                warmupPlan.WarmupTimeoutMs,
                ct).ConfigureAwait(false);

            bool sourceReady = IsSourceReadyForResume(pipeline, warmupPlan.SourceAheadMs);

            if (_session.IsStale(cmd.SessionId))
            {
                CompletePendingPlayCanceled();
                var stale = Interlocked.Exchange(ref _activePipeline, null);
                if (stale != null)
                    await stale.DisposeAsync().ConfigureAwait(false);
                return;
            }

            ct.ThrowIfCancellationRequested();

            if (CurrentPlaybackIntent != PlaybackIntent.Play)
            {
                pipeline.Stop();
                SetState(PlayerState.Paused);
                CompletePendingPlayResult(false);
                return;
            }

            ResumeOrDefer(pipeline, pcmReady, sourceReady, warmupPlan, cmd.SessionId,
                startTimers: true, configurePipeline: false, logContext: "Initial warmup");

            _ = WatchPipelineLifetimeAsync(pipeline, cmd.SessionId);
        }
        catch (OperationCanceledException)
        {
            CompletePendingPlayCanceled();
            if (_state is PlayerState.Loading or PlayerState.Buffering)
                SetState(PlayerState.Idle);
        }
        catch (AudioDeviceException ex)
        {
            CompletePendingPlayException(ex);
            SetState(PlayerState.Error);
            _events.RaiseError(new AudioPlayerError(GetDeviceErrorMessage(), ex));
        }
        catch (Exception ex) when (
            cmd.ExternalCancellationToken.IsCancellationRequested ||
            _session.IsStale(cmd.SessionId) ||
            CancellationHelper.IsCancellationLike(ex))
        {
            CompletePendingPlayCanceled();
            if (_state is PlayerState.Loading or PlayerState.Buffering)
                SetState(PlayerState.Idle);
        }
        catch (Exception ex)
        {
            CompletePendingPlayException(ex);
            SetState(PlayerState.Error);
            _events.RaiseError(new AudioPlayerError(ex.Message, ex));
        }
        finally
        {
            Interlocked.CompareExchange(ref _activePlayCts, null, linkedCts);
        }
    }

    private void CancelActivePlayInit()
    {
        var cts = Interlocked.Exchange(ref _activePlayCts, null);
        CancelCtsAsync(cts);
    }

    private async Task WatchPipelineLifetimeAsync(AudioPipeline pipeline, int sessionId)
    {
        await pipeline.LifetimeToken.WhenCanceledAsync().ConfigureAwait(false);

        if (_disposed || _session.IsStale(sessionId) || _activePipeline != pipeline)
            return;

        try
        {
            _commandChannel.Writer.TryWrite(new PlayerErrorCommand(
                sessionId,
                pipeline,
                new AudioDeviceException(GetDeviceErrorMessage())));

            _commandChannel.Writer.TryWrite(new StopCommand(sessionId));
        }
        catch (ChannelClosedException)
        {
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Switches the pipeline to buffering mode and starts background waiting
    /// before opening the playback gate.
    /// </summary>
    private void LaunchDeferredResume(
        AudioPipeline pipeline,
        PlaybackWarmupPlan warmupPlan,
        int sessionId)
    {
        pipeline.ActivateBufferingMode();

        int seekGeneration = Volatile.Read(ref _seekGeneration);
        var deferredResumeCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        var previousCts = Interlocked.Exchange(ref _deferredResumeCts, deferredResumeCts);
        CancelCtsAsync(previousCts);

        _ = AwaitDeferredSeekBufferAndResumeAsync(
            pipeline,
            warmupPlan.PcmThresholdSamples,
            warmupPlan.SourceAheadMs,
            sessionId,
            seekGeneration,
            deferredResumeCts);
    }

    /// <summary>
    /// Resumes playback immediately if buffers are ready,
    /// or delegates to <see cref="LaunchDeferredResume"/>.
    /// </summary>
    private void ResumeOrDefer(
        AudioPipeline pipeline,
        bool pcmReady,
        bool sourceReady,
        PlaybackWarmupPlan warmupPlan,
        int sessionId,
        bool startTimers,
        bool configurePipeline,
        string? logContext = null)
    {
        // If the entire track tail up to EOF is already downloaded locally and the ring buffer contains decoded samples,
        // resume playback immediately, even if the remaining track at the very end is shorter than PcmThresholdSamples.
        if (!pcmReady
            && pipeline.BufferedSamples > 0
            && GetSourceBufferedAheadMs(pipeline) == int.MaxValue)
        {
            pcmReady = true;
        }

        if (pcmReady && sourceReady)
        {
            ResumePlaybackSequence(pipeline, startTimers, configurePipeline);
            return;
        }

        Log.Warn($"[AudioPlayer] {logContext ?? "Warmup"} incomplete " +
                 $"(ring={pipeline.BufferedSamples}/{warmupPlan.PcmThresholdSamples}, " +
                 $"ahead={GetSourceBufferedAheadMs(pipeline)}ms/{warmupPlan.SourceAheadMs}ms). " +
                 "Deferred resume.");

        LaunchDeferredResume(pipeline, warmupPlan, sessionId);
    }

    /// <summary>
    /// Returns <c>true</c> if the pipeline command is stale and should be discarded.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsPipelineCommandStale(AudioPipeline pipeline, int sessionId) =>
        _disposed || _activePipeline != pipeline || _session.IsStale(sessionId);

    /// <summary>
    /// Starts disposing the old pipeline in the background.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void TrackAndFirePipelineDispose(AudioPipeline pipeline)
    {
        var previousDispose = Volatile.Read(ref _oldPipelineDisposeTask);
        var disposeTask = Task.Run(async () =>
        {
            try
            {
                await pipeline.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }

            if (previousDispose is { IsCompleted: false })
            {
                try
                {
                    await previousDispose.ConfigureAwait(false);
                }
                catch
                {
                }
            }
        });

        Volatile.Write(ref _oldPipelineDisposeTask, disposeTask);
    }

    /// <summary>
    /// Handles natural track completion inside the actor loop.
    /// </summary>
    private async Task HandleTrackEndedAsync(TrackEndedCommand cmd)
    {
        if (_state is not (PlayerState.Buffering or PlayerState.Playing or PlayerState.Paused or PlayerState.Seeking))
            return;

        if (_activePipeline == null)
            return;

        if (_options.ShouldFastReplay?.Invoke() == true)
        {
            var pipeline = _activePipeline;
            if (pipeline is { IsDisposed: false, IsDeviceLost: false }
                && pipeline.Source.CanSeek)
            {
                if (await FastRewindInternalAsync(pipeline, cmd.SessionId).ConfigureAwait(false))
                    return;
            }
        }

        SetPlaybackIntent(PlaybackIntent.Stop);
        StopTimers();
        _sharedBackend.Flush();

        _events.RaiseTrackEnded();

        var old = Interlocked.Exchange(ref _activePipeline, null);
        if (old != null)
            TrackAndFirePipelineDispose(old);

        SetState(PlayerState.Idle);
    }

    /// <summary>
    /// Fast-rewinds the current pipeline to the beginning of the track
    /// without recreating the source, decoder, or file descriptors.
    /// </summary>
    private async Task<bool> FastRewindInternalAsync(AudioPipeline pipeline, int sessionId)
    {
        try
        {
            SetState(PlayerState.Buffering);
            StopTimers();

            await pipeline.StopDecodingAsync(
                 TimeSpan.FromMilliseconds(RewindDecoderStopTimeoutMs)).ConfigureAwait(false);

            pipeline.Flush();
            pipeline.PrepareForSeek(0);

            bool seeked = await pipeline.Source
                .SeekAsync(0, _lifetimeCts.Token).ConfigureAwait(false);

            if (!seeked)
            {
                Log.Warn("[AudioPlayer] Fast rewind: seek(0) failed");
                return false;
            }

            pipeline.SetDecodedSamplesPosition(0);

            if (_disposed || _session.IsStale(sessionId))
                return false;

            pipeline.StartDecoding(
                _options,
                CreateTrackEndedCallback(sessionId),
                CreateErrorCallback(sessionId, pipeline));

            var warmupPlan = ComputePlaybackWarmupPlan(pipeline, isSeek: false);

            bool pcmReady = warmupPlan.PcmThresholdSamples <= 0;

            if (!pcmReady && warmupPlan.WarmupTimeoutMs > 0)
            {
                pcmReady = await pipeline.WaitForBufferAsync(
                    warmupPlan.PcmThresholdSamples,
                    warmupPlan.WarmupTimeoutMs,
                    _lifetimeCts.Token).ConfigureAwait(false);
            }

            bool sourceReady = IsSourceReadyForResume(pipeline, warmupPlan.SourceAheadMs);

            if (_disposed || _session.IsStale(sessionId))
                return false;

            if (CurrentPlaybackIntent != PlaybackIntent.Play)
            {
                pipeline.Stop();
                SetState(PlayerState.Paused);
                return true;
            }

            ResumeOrDefer(pipeline, pcmReady, sourceReady, warmupPlan, sessionId,
                     startTimers: true, configurePipeline: false, logContext: "Fast rewind warmup");

            Log.Info("[AudioPlayer] Fast rewind completed (repeat-one)");
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioPlayer] Fast rewind failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Handles starvation and switches playback to adaptive rebuffer.
    /// </summary>
    private Task HandleStarvationAsync(StarvationCommand cmd)
    {
        if (IsPipelineCommandStale(cmd.Pipeline, cmd.SessionId))
            return Task.CompletedTask;

        // Accept starvation from Playing and Buffering:
        // from Playing — normal path (ring emptied during playback);
        // from Buffering — network dropped during post-seek rebuffer (deferred resume is already active,
        // but its CTS will be replaced by a new one in LaunchDeferredResume).
        if (_state is not (PlayerState.Playing or PlayerState.Buffering))
            return Task.CompletedTask;

        if (CurrentPlaybackIntent != PlaybackIntent.Play)
            return Task.CompletedTask;

        Log.Warn($"[AudioPlayer] Starvation in state={_state} — closing gate for adaptive rebuffer");

        var pipeline = cmd.Pipeline;

        // Stop only audio output and the position timer; keep the buffer timer active
        // so the UI displays download progress and ping during adaptive rebuffering.
        if (_state == PlayerState.Playing)
        {
            pipeline.Stop();
            StopPositionTimer();
            StartBufferTimer();
        }

        SetState(PlayerState.Buffering);
        _options.OnStarvationDetected?.Invoke();

        var recoveryPlan = ComputeStarvationRecoveryPlan(pipeline);
        LaunchDeferredResume(pipeline, recoveryPlan, cmd.SessionId);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Calculates a recovery plan after starvation using elevated thresholds.
    /// </summary>
    private static PlaybackWarmupPlan ComputeStarvationRecoveryPlan(AudioPipeline pipeline)
    {
        var basePlan = ComputePlaybackWarmupPlan(pipeline, isSeek: false);

        int recoverySourceAheadMs = basePlan.SourceAheadMs > 0
            ? Math.Min(basePlan.SourceAheadMs * 2, 8000)
            : 3000;

        // basePlan.PcmThresholdSamples = 0 when IsFullyBuffered=true.
        // A zero threshold yields pcmReady=true immediately when ring=0 → resume without data.
        // A minimum of 100ms samples ensures the ring contains real decoded data.
        int recoveryPcmSamples = basePlan.PcmThresholdSamples > 0
            ? (int)(basePlan.PcmThresholdSamples * 1.5)
            : pipeline.SampleRate * pipeline.Channels * 100 / 1000;

        return new PlaybackWarmupPlan(
            Math.Max(recoveryPcmSamples, pipeline.Channels * 2),
            recoverySourceAheadMs,
            basePlan.WarmupTimeoutMs);
    }

    /// <summary>
    /// Starvation handler from the pipeline.
    /// </summary>
    private void OnPipelineStarvation(AudioPipeline pipeline, int sessionId)
    {
        if (_disposed || _activePipeline != pipeline) return;
        if (_session.IsStale(sessionId)) return;

        _commandChannel.Writer.TryWrite(new StarvationCommand(sessionId, pipeline));
    }

    /// <summary>
    /// Handles a background error inside the actor loop.
    /// </summary>
    private Task HandlePlayerErrorAsync(PlayerErrorCommand cmd)
    {
        if (IsPipelineCommandStale(cmd.Pipeline, cmd.SessionId))
            return Task.CompletedTask;

        if (CancellationHelper.IsCancellationLike(cmd.Error))
            return Task.CompletedTask;

        HandleError(cmd.Error);
        return Task.CompletedTask;
    }

    private void HandleError(Exception ex)
    {
        CompletePendingPlayException(ex);
        SetState(PlayerState.Error);

        string message = ex switch
        {
            AudioDeviceException => GetDeviceErrorMessage(),
            CacheInvalidatedException => LocalizationService.Instance["Error_CacheInvalidated"],
            _ => ex.Message
        };

        _events.RaiseError(new AudioPlayerError(message, ex));
    }

    private void CompletePendingPlayResult(bool started) =>
        Interlocked.Exchange(ref _pendingPlayCompletion, null)?.TrySetResult(started);

    private void CompletePendingPlayCanceled() =>
        Interlocked.Exchange(ref _pendingPlayCompletion, null)?.TrySetCanceled();

    private void CompletePendingPlayException(Exception ex) =>
        Interlocked.Exchange(ref _pendingPlayCompletion, null)?.TrySetException(ex);

    private static string GetDeviceErrorMessage() =>
        LocalizationService.Instance["Error_NoAudioDevice"];

    /// <summary>
    /// Performs a state machine transition.
    /// </summary>
    private bool SetState(PlayerState newState, [CallerMemberName] string? caller = null)
    {
        var oldState = _state;
        if (oldState == newState) return true;

        if (!PlayerStateTransitions.CanTransition(oldState, newState))
        {
            Log.Error($"[AudioPlayer] Invalid state transition: {oldState} -> {newState}. " +
                      $"Caller={caller}, Session={_session.Current}, SeekGeneration={Volatile.Read(ref _seekGeneration)}, " +
                      $"Intent={CurrentPlaybackIntent}");
            return false;
        }

        _state = newState;
        _events.RaiseStateChanged(MapState(newState));
        return true;
    }

    private PlaybackState MapState(PlayerState state) => state switch
    {
        PlayerState.Idle => PlaybackState.Stopped,
        PlayerState.Loading => PlaybackState.Loading,
        PlayerState.Buffering => PlaybackState.Buffering,
        PlayerState.Playing => PlaybackState.Playing,
        PlayerState.Paused => PlaybackState.Paused,
        PlayerState.Seeking => CurrentPlaybackIntent == PlaybackIntent.Pause ? PlaybackState.Paused : PlaybackState.Playing,
        PlayerState.Error => PlaybackState.Error,
        PlayerState.Disposed => PlaybackState.Stopped,
        _ => PlaybackState.Stopped
    };

    private void SetPlaybackIntent(PlaybackIntent intent) =>
        Volatile.Write(ref _playbackIntent, (int)intent);

    private PlaybackIntent CurrentPlaybackIntent =>
        (PlaybackIntent)Volatile.Read(ref _playbackIntent);

    /// <summary>
    /// Creates a session-bound track completion callback.
    /// </summary>
    private Action CreateTrackEndedCallback(int sessionId)
    {
        return () =>
        {
            if (_disposed) return;
            _commandChannel.Writer.TryWrite(new TrackEndedCommand(sessionId));
        };
    }

    /// <summary>
    /// Creates a session-bound background error callback.
    /// </summary>
    private Action<Exception> CreateErrorCallback(int sessionId, AudioPipeline pipeline)
    {
        return ex =>
        {
            if (_disposed) return;
            _commandChannel.Writer.TryWrite(new PlayerErrorCommand(sessionId, pipeline, ex));
        };
    }

    #endregion

    #region Statistics

    internal AudioPipeline? GetActivePipeline() => _activePipeline;

    public double BufferProgress => _activePipeline?.Source.BufferProgress ?? 0;

    public bool IsFullyBuffered => _activePipeline?.Source.IsFullyBuffered ?? false;

    public long GetDownloadedBytes()
    {
        var pipeline = _activePipeline;
        if (pipeline == null) return 0;

        return pipeline.Source switch
        {
            Sources.CachingStreamSource caching => caching.DownloadedBytes,
            Sources.LocalFileSource => pipeline.Source.IsFullyBuffered
                ? pipeline.StreamInfo.DurationMs * pipeline.StreamInfo.Bitrate / BitsPerByte
                : 0,
            _ => (long)(pipeline.Source.BufferProgress / 100.0
                * pipeline.StreamInfo.DurationMs * pipeline.StreamInfo.Bitrate / BitsPerByte)
        };
    }

    public IReadOnlyList<(double Start, double End)> GetBufferedRanges() =>
        _activePipeline?.Source.GetBufferedRanges() ?? [];

    #endregion

    #region Dispose

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CancelActiveSeek();

        _commandChannel.Writer.TryWrite(new DisposeCommand(int.MaxValue));
        _commandChannel.Writer.TryComplete();

        try
        {
            _commandProcessorTask.Wait(TimeSpan.FromSeconds(DisposeTaskTimeoutSec));
        }
        catch
        {
        }

        _lifetimeCts.Cancel();
        _sharedBackend.Dispose();
        _lifetimeCts.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        CancelActiveSeek();

        await _commandChannel.Writer.WriteAsync(new DisposeCommand(int.MaxValue))
            .ConfigureAwait(false);

        _commandChannel.Writer.TryComplete();

        try
        {
            await _commandProcessorTask
                .WaitAsync(TimeSpan.FromSeconds(DisposeTaskTimeoutSec))
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Warn("[AudioPlayer] Command processor did not finish within dispose timeout");
        }
        catch
        {
        }

        _lifetimeCts.Cancel();

        _sharedBackend.Dispose();
        _lifetimeCts.Dispose();
    }

    #endregion
}