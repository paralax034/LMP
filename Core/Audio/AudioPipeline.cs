using System.Buffers;
using System.Numerics;
using LMP.Core.Audio.Decoders;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Normalization;
using LMP.Core.Exceptions;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio;

/// <summary>
/// Full playback pipeline: Source → Decoder → PCM Buffer → Normalization → Gain → Backend.
/// </summary>
internal sealed class AudioPipeline : IAsyncDisposable
{
    #region Constants

    private const int BufferFullDelayMs = 5;
    private const int BufferModerateDelayMs = 25;
    private const int BufferHighDelayMs = 100;
    private const int BufferMaxDelayMs = 180;
    private const int BufferModerateThresholdMs = 400;
    private const int BufferHighThresholdMs = 1_000;
    private const int BufferMaxThresholdMs = 1_400;
    private const int BatchRefillCapacityDivisor = 4;
    private const int MinBatchRefillFrames = 4;
    private const int DrainMinDelayMs = 50;
    private const int DrainMaxDelayMs = 150;
    private const int HResultFileNotFound = unchecked((int)0x80070002);
    private const int HResultPathNotFound = unchecked((int)0x80070003);
    private const int PrematureEndToleranceMs = 2_000;

    /// <summary>Maximum duration of isolated pre-scan in seconds.</summary>
    private const float IsolatedScanMaxSeconds = 30f;

    /// <summary>Minimum audio seconds required for adequate EBU R128 pre-scan (±1-2 LU).</summary>
    private const float MinPreScanSeconds = 10f;

    #endregion

    #region Fields

    private readonly IAudioSource _source;
    private readonly IAudioDecoder _decoder;
    private readonly IPlaybackBackend _backend;
    private readonly LockFreeRingBuffer<float> _pcmBuffer;
    private readonly float[] _decodeBuffer;
    private readonly AudioStreamInfo _streamInfo;
    private readonly CancellationTokenSource _lifetimeCts;
    private readonly EbuR128Analyzer _analyzer;
    private readonly EqualizerProcessor _equalizer;
    private volatile TruePeakLimiter _truePeakLimiter;
    private volatile AudioSpectrumAnalyzer _analyzer3Band;
    private volatile int _activeSampleRate;
    private volatile int _activeChannels;
    private GainCrossfader _gainCrossfader;

    private CancellationTokenSource? _decoderCts;
    private Task? _decoderTask;
    private volatile bool _disposed;

    private TaskCompletionSource? _warmupTcs;
    private int _warmupThreshold;

    private int _skipFramesCounter;
    private int _decoderResetNeeded;
    private long _decodedSamples;
    private long _seekTargetMs = -1;
    private volatile bool _deviceLost;
    private Action? _onDeviceLostExternal;
    private Action? _onDeviceAvailableExternal;
    private Action? _onStarvationExternal;

    private Task? _deviceEventTask;
    private Task? _preScanTask;

    private readonly Lock _lifecycleLock = new();
    private readonly Lock _pauseGateLock = new();
    private TaskCompletionSource? _pauseGateTcs;
    private volatile bool _isPaused;
    private int _pendingRingClear;
    private int _initialCrossfadePending = 1;

    /// <summary>
    /// Render clock anchor point to the stream: position <c>BaseSamples</c> corresponds to
    /// <see cref="IPlaybackBackend.RenderedSamples"/> reading equal to <c>RenderedSamples</c>. Published by atomic reference swap.
    /// </summary>
    private sealed record ClockOrigin(long BaseSamples, long RenderedSamples);

    private ClockOrigin _clockOrigin;

    /// <summary>Spectrum analysis paused due to lack of readers; mutated only by audio callback.</summary>
    private bool _spectrumSuspended;

#if AUDIO_SPLICE_AUDIT
    private int _decoderRestartCount;
#endif

    #endregion

    #region Properties

    /// <summary>Whether the audio device has been lost.</summary>
    public bool IsDeviceLost => _deviceLost;

    /// <summary>Audio stream metadata.</summary>
    public AudioStreamInfo StreamInfo => _streamInfo;

    /// <summary>Raw audio frame source.</summary>
    public IAudioSource Source => _source;

    /// <summary>Audio decoder.</summary>
    public IAudioDecoder Decoder => _decoder;

    /// <summary>System audio backend.</summary>
    public IPlaybackBackend Backend => _backend;

    /// <summary>Pipeline disposed.</summary>
    public bool IsDisposed => _disposed;

    /// <summary>Decoder sample rate.</summary>
    public int SampleRate => _activeSampleRate;

    /// <summary>Decoder channel count.</summary>
    public int Channels => _activeChannels;

    /// <summary>
    /// Stream position in samples (frames × channels) actually rendered on the device.
    /// Reads render clock snapshot and extrapolates to current time lock-free, mutation-free, and allocation-free.
    /// </summary>
    public long PlayedSamples
    {
        get
        {
            ClockOrigin origin = Volatile.Read(ref _clockOrigin);
            RenderClockSnapshot snapshot = _backend.ReadRenderClock();
            long played = RenderClock.Extrapolate(in snapshot, System.Diagnostics.Stopwatch.GetTimestamp());
            return RenderClock.MapToStream(played, origin.RenderedSamples, origin.BaseSamples);
        }
    }

    /// <summary>
    /// Current playback position per render clock, clamped to track duration.
    /// </summary>
    public TimeSpan GetPlaybackPosition()
    {
        int samplesPerSecond = SampleRate * Channels;
        if (samplesPerSecond <= 0) return TimeSpan.Zero;

        double seconds = (double)PlayedSamples / samplesPerSecond;
        double maxSeconds = Math.Max(0L, _streamInfo.DurationMs) / 1000.0;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 0.0, maxSeconds));
    }

    /// <summary>Sample count in backend buffer.</summary>
    public int BackendBufferedSamples => _backend.BufferedSamples;

    /// <summary>Sample count in PCM ring buffer.</summary>
    public int BufferedSamples => _pcmBuffer.Count;

    /// <summary>Pipeline lifetime cancellation token.</summary>
    public CancellationToken LifetimeToken => _lifetimeCts.Token;

    /// <summary>EBU R128 normalization analyzer.</summary>
    public EbuR128Analyzer Analyzer => _analyzer;

    /// <summary>10-band equalizer processor.</summary>
    public EqualizerProcessor Equalizer => _equalizer;

#if AUDIO_SPLICE_AUDIT
    /// <summary>Decoder loop restart count.</summary>
    public int DecoderRestartCount => Volatile.Read(ref _decoderRestartCount);
#endif

    #endregion

    #region Constructor

    private AudioPipeline(
    IAudioSource source,
    IAudioDecoder decoder,
    IPlaybackBackend backend,
    LockFreeRingBuffer<float> pcmBuffer,
    float[] decodeBuffer,
    AudioStreamInfo streamInfo,
    CancellationTokenSource lifetimeCts)
    {
        _source = source;
        _decoder = decoder;
        _backend = backend;
        _pcmBuffer = pcmBuffer;
        _decodeBuffer = decodeBuffer;
        _streamInfo = streamInfo;
        _lifetimeCts = lifetimeCts;

        _activeSampleRate = decoder.SampleRate;
        _activeChannels = decoder.Channels;
        _analyzer = new EbuR128Analyzer();
        _equalizer = new EqualizerProcessor(decoder.SampleRate, decoder.Channels);
        _truePeakLimiter = new TruePeakLimiter(decoder.SampleRate);
        _analyzer3Band = new AudioSpectrumAnalyzer(decoder.SampleRate, decoder.Channels);
        _gainCrossfader = new GainCrossfader(1.0f);
        _clockOrigin = new ClockOrigin(0, backend.RenderedSamples);
    }

    #endregion

    #region Factory

    /// <summary>
    /// Creates pipeline with shared backend from <see cref="ResolvedStreamDescriptor"/>.
    /// </summary>
    public static async Task<AudioPipeline> CreateAsync(
        ResolvedStreamDescriptor descriptor,
        AudioPlayerOptions options,
        IPlaybackBackend sharedBackend,
        CancellationToken ct)
    {
        Log.Info($"[AudioPipeline] CreateAsync -> {descriptor}");

        var lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        IAudioSource? source = null;
        IAudioDecoder? decoder = null;
        LockFreeRingBuffer<float>? pcmBuffer = null;
        float[]? decodeBuffer = null;

        try
        {
            // URL provider passed to source as a whole: source specifies its track and stream variant in request
            source = await AudioSourceFactory.CreateAsync(
                 descriptor,
                 options.UrlProvider,
                 options.StreamingConfig).ConfigureAwait(false);

            bool initialized;
            try
            {
                initialized = await source.InitializeAsync(lifetimeCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is CdnUnavailableException or HttpRequestException
                                       && source is Sources.CachingStreamSource { HasUrlProvider: true } cachingSource)
            {
                if (ex is CdnUnavailableException cdnEx && !string.IsNullOrEmpty(cdnEx.Host))
                {
                    AudioSourceFactory.CdnBlacklist.MarkBlocked(cdnEx.Host);
                    Log.Warn($"[AudioPipeline] CDN host '{cdnEx.Host}' blacklisted on pipeline init failure.");
                }

                lifetimeCts.Token.ThrowIfCancellationRequested();

                Log.Warn($"[AudioPipeline] Initial source initialization failed ({ex.GetType().Name}: {ex.Message}). Proactively refreshing stream URL...");

                // Pipeline not yet published in AudioPlayer: request formed by source itself, context retained.
                var refreshedUrl = await cachingSource.RequestUrlAsync(StreamUrlRequestKind.Refresh, lifetimeCts.Token).ConfigureAwait(false);
                lifetimeCts.Token.ThrowIfCancellationRequested();

                if (!string.IsNullOrEmpty(refreshedUrl))
                {
                    cachingSource.UpdateUrl(refreshedUrl);
                    Log.Info($"[AudioPipeline] Proactive URL refreshed for {descriptor.TrackId}. Retrying source initialization...");
                    initialized = await source.InitializeAsync(lifetimeCts.Token).ConfigureAwait(false);
                }
                else
                {
                    throw;
                }
            }

            if (!initialized)
            {
                lifetimeCts.Token.ThrowIfCancellationRequested();
                ct.ThrowIfCancellationRequested();
                throw new AudioSourceException("Failed to initialize audio source: container parsing returned false");
            }

            decoder = CreateDecoder(source);

            int maxChannels = Math.Max(decoder.Channels, 2);
            int rawSize = Math.Max(decoder.SampleRate, DefaultSampleRate) * maxChannels * BufferSizeSeconds;
            int bufferSize = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(rawSize, 16));
            pcmBuffer = new LockFreeRingBuffer<float>(bufferSize, decoder.Channels);

            decodeBuffer = ArrayPool<float>.Shared.Rent(DecoderBufferFrames * maxChannels);

            var streamInfo = BuildStreamInfo(descriptor, source, decoder);

            Log.Debug($"[AudioPipeline] StreamInfo built: track={streamInfo.TrackId}, container={streamInfo.Container}, codec={streamInfo.Codec}, bitrate={streamInfo.Bitrate}kbps, duration={streamInfo.DurationMs}ms, fromCache={streamInfo.IsFromCache}");

            var pipeline = new AudioPipeline(
                source, decoder, sharedBackend, pcmBuffer, decodeBuffer, streamInfo, lifetimeCts);

            try
            {
                sharedBackend.Reinitialize(decoder.SampleRate, decoder.Channels, pipeline.AudioCallback);
            }
            catch (AudioDeviceException ex)
            {
                pipeline._deviceLost = true;
                Log.Warn($"[AudioPipeline] Created in degraded mode: {ex.Message}");
            }

            // Reinitialize reset resampler and may have advanced render counter: origin taken after it
            pipeline.RebaseRenderClock(0);

            sharedBackend.SetDeviceLostCallback(pipeline.NotifyDeviceLost);
            sharedBackend.SetStarvationCallback(pipeline.NotifyStarvation);
            sharedBackend.SetDeviceAvailableCallback(pipeline.NotifyDeviceAvailable);

            return pipeline;
        }
        catch (OperationCanceledException) { CleanupOnError(source, decoder, pcmBuffer, decodeBuffer, lifetimeCts); throw; }
        catch (AudioSourceException) { CleanupOnError(source, decoder, pcmBuffer, decodeBuffer, lifetimeCts); throw; }
        catch (CdnUnavailableException) { CleanupOnError(source, decoder, pcmBuffer, decodeBuffer, lifetimeCts); throw; }
        catch (Exception ex)
        {
            CleanupOnError(source, decoder, pcmBuffer, decodeBuffer, lifetimeCts);
            if (CancellationHelper.IsCancellationOrTokenCancelled(ex, ct))
                throw new OperationCanceledException("Pipeline creation cancelled", ex, ct);
            throw new AudioSourceException("Failed to initialize audio source", ex);
        }
    }

    private static void CleanupOnError(
        IAudioSource? s,
        IAudioDecoder? d,
        LockFreeRingBuffer<float>? pcm,
        float[]? buf,
        CancellationTokenSource cts)
    {
        try { d?.Dispose(); } catch { }
        try { s?.Dispose(); } catch { }
        pcm?.Dispose();
        if (buf != null) ArrayPool<float>.Shared.Return(buf);
        try { cts.Dispose(); } catch { }
    }

    private static IAudioDecoder CreateDecoder(IAudioSource source)
    {
        int rate = source.SampleRate > 0 ? source.SampleRate : DefaultSampleRate;
        int ch = source.Channels > 0 ? source.Channels : DefaultChannels;

        return source.Codec switch
        {
            AudioCodec.Opus => new OpusDecoder(rate, ch, source.DecoderConfig),
            AudioCodec.Aac => CreateAacDecoder(source, rate, ch),
            _ => throw new NotSupportedException($"Codec {source.Codec} not supported")
        };
    }

    private static AacDecoder CreateAacDecoder(IAudioSource source, int rate, int ch)
    {
        var dec = new AacDecoder(rate, ch);
        if (source.DecoderConfig != null) dec.Initialize(source.DecoderConfig);
        return dec;
    }

    /// <summary>
    /// Builds <see cref="AudioStreamInfo"/> from descriptor and runtime parameters.
    /// Direct mapping without fallback cascade and HTTP detect.
    /// </summary>
    private static AudioStreamInfo BuildStreamInfo(
        ResolvedStreamDescriptor descriptor,
        IAudioSource source,
        IAudioDecoder decoder)
    {
        bool isFromLocalFile = descriptor.Origin == StreamSource.DiskCacheFull
            || (source is Sources.LocalFileSource);

        return AudioStreamInfo.FromDescriptor(
            descriptor,
            sampleRate: decoder.SampleRate > 0 ? decoder.SampleRate : DefaultSampleRate,
            channels: decoder.Channels > 0 ? decoder.Channels : DefaultChannels,
            durationMs: source.DurationMs,
            isFromCache: isFromLocalFile);
    }

    #endregion

    #region Device Loss

    internal void NotifyDeviceLost()
    {
        if (_disposed || _deviceLost) return;
        _deviceLost = true;

        Log.Error("[AudioPipeline] Audio device lost — soft pause (pipeline alive)");
        try { _decoderCts?.Cancel(); } catch (ObjectDisposedException) { }

        EnqueueDeviceEvent(_onDeviceLostExternal);
    }

    private void EnqueueDeviceEvent(Action? handler)
    {
        if (handler == null) return;

        var prev = Volatile.Read(ref _deviceEventTask);
        var next = Task.Run(async () =>
        {
            if (prev is { IsCompleted: false })
                await prev.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            handler();
        });
        Volatile.Write(ref _deviceEventTask, next);
    }

    internal void SetDeviceLostHandler(Action handler) => _onDeviceLostExternal = handler;

    internal async Task RecoverFromDeviceLossAsync(
        AudioPlayerOptions options,
        Action? onTrackEnded,
        Action<Exception>? onError,
        CancellationToken ct)
    {
        if (_disposed || !_deviceLost) return;

        await StopDecodingAsync(TimeSpan.FromMilliseconds(DecoderStopTimeoutMs)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        _backend.Flush();
        _pcmBuffer.Clear();

        _backend.Reinitialize(SampleRate, Channels, AudioCallback);
        _backend.SetDeviceLostCallback(NotifyDeviceLost);
        _backend.SetStarvationCallback(NotifyStarvation);
        _backend.SetDeviceAvailableCallback(NotifyDeviceAvailable);

        // Ring cleared, while decoder resumes from stop position: clock counts from its position
        RebaseRenderClock(Interlocked.Read(ref _decodedSamples));

        ct.ThrowIfCancellationRequested();
        _deviceLost = false;

        StartDecoding(options, onTrackEnded, onError);
        Log.Info("[AudioPipeline] Recovered from device loss");
    }

    internal void NotifyDeviceAvailable()
    {
        if (_disposed || !_deviceLost) return;
        EnqueueDeviceEvent(_onDeviceAvailableExternal);
    }

    internal void SetDeviceAvailableHandler(Action handler) => _onDeviceAvailableExternal = handler;
    internal void SetStarvationHandler(Action handler) => _onStarvationExternal = handler;

    #endregion

    #region Decoder Loop

    /// <summary>
    /// Starts decoding loop. If previous loop is alive after stop timeout, new one waits for it to exit:
    /// SPSC ring has exactly one producer at any time. Active non-cancelled loop is not re-launched.
    /// </summary>
    public void StartDecoding(
     AudioPlayerOptions options,
     Action? onTrackEnded,
     Action<Exception>? onError)
    {
        Task? previousTask;
        CancellationTokenSource? previousCts;
        bool previousAlive;
        CancellationToken token;

        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            previousTask = _decoderTask;
            previousCts = _decoderCts;
            previousAlive = previousTask is { IsCompleted: false };
            if (previousAlive && previousCts?.IsCancellationRequested != true) return;

            if (!previousAlive)
                previousCts?.Dispose();

            _decoderCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            token = _decoderCts.Token;

            _decoderTask = previousAlive
                ? Task.Run(() => RunAfterPreviousLoopAsync(previousTask!, previousCts, options, onTrackEnded, onError, token))
                : Task.Run(() => DecoderLoopAsync(options, onTrackEnded, onError, token));
        }

#if AUDIO_SPLICE_AUDIT
        int restartCount = Interlocked.Increment(ref _decoderRestartCount);
        const int ShortTrackIdLength = 8;

        var trackIdShort = _streamInfo.TrackId?.Length > ShortTrackIdLength
            ? _streamInfo.TrackId[..ShortTrackIdLength]
            : _streamInfo.TrackId ?? "?";

        if (restartCount > 1)
            Log.Debug($"[AudioPipeline] Decoder restart #{restartCount}: {trackIdShort}");
        else
            Log.Debug($"[AudioPipeline] Decoder started: {trackIdShort}");
#endif
    }

    public async Task StopDecodingAsync(TimeSpan timeout)
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_lifecycleLock)
        {
            cts = _decoderCts;
            task = _decoderTask;
        }
        if (cts == null || task == null) return;

        try { cts.Cancel(); } catch (ObjectDisposedException) { }

        if (_source is Sources.CachingStreamSource cachingSource)
            cachingSource.CancelActiveReads();

        try { await task.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            // Hung loop remains registered ring owner alongside its CTS:
            // next StartDecoding waits for its exit instead of launching a second producer
            Log.Warn("[AudioPipeline] Decoder stop timeout");
            return;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn($"[AudioPipeline] Decoder stop error: {ex.Message}"); }

        lock (_lifecycleLock)
        {
            // While waiting, a new loop could start: reset only what was owned at entry
            if (ReferenceEquals(_decoderTask, task)) _decoderTask = null;
            if (ReferenceEquals(_decoderCts, cts)) _decoderCts = null;
        }
        try { cts.Dispose(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// New loop over hung loop: starts only after previous exits, then disposes its CTS.
    /// </summary>
    private async Task RunAfterPreviousLoopAsync(
        Task previousTask,
        CancellationTokenSource? previousCts,
        AudioPlayerOptions options,
        Action? onTrackEnded,
        Action<Exception>? onError,
        CancellationToken ct)
    {
        await previousTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        try { previousCts?.Dispose(); } catch (ObjectDisposedException) { }

        if (Interlocked.Exchange(ref _pendingRingClear, 0) == 1)
            _pcmBuffer.Clear();

        if (ct.IsCancellationRequested) return;
        await DecoderLoopAsync(options, onTrackEnded, onError, ct).ConfigureAwait(false);
    }

    private Task WaitForResumeOrCancelAsync(CancellationToken ct)
    {
        TaskCompletionSource tcs;
        lock (_pauseGateLock)
        {
            if (!_isPaused || _disposed || ct.IsCancellationRequested)
                return Task.CompletedTask;

            tcs = _pauseGateTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        return tcs.Task.WaitAsync(ct);
    }

    private void ReleasePauseGate()
    {
        TaskCompletionSource? tcs;
        lock (_pauseGateLock)
        {
            _isPaused = false;
            tcs = _pauseGateTcs;
            _pauseGateTcs = null;
        }
        tcs?.TrySetResult();
    }

    private async Task DecoderLoopAsync(
           AudioPlayerOptions options,
           Action? onTrackEnded,
           Action<Exception>? onError,
           CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _pendingRingClear, 0) == 1)
            _pcmBuffer.Clear();

        int retryCount = 0;
        int requiredSpace = _decoder.MaxFrameSize * _decoder.Channels;
        int batchRefillSpace = Math.Max(requiredSpace * MinBatchRefillFrames, _pcmBuffer.Capacity / BatchRefillCapacityDivisor);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_pcmBuffer.ProducerCachedAvailable < requiredSpace)
                {
                    if (_pcmBuffer.Available < requiredSpace)
                    {
                        while (_pcmBuffer.Available < batchRefillSpace && !ct.IsCancellationRequested)
                        {
                            if (_isPaused)
                            {
                                await WaitForResumeOrCancelAsync(ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                                break;
                            }

                            int delayMs = ComputeBufferFullDelayMs(_pcmBuffer.Count, _decoder.SampleRate, _decoder.Channels);
                            if (!await ct.DelayNoThrowAsync(delayMs, continueOnCapturedContext: false).ConfigureAwait(false))
                                break;
                        }

                        if (ct.IsCancellationRequested)
                            break;
                        continue;
                    }
                }

                AudioFrame? frame;
                try
                {
                    frame = await _source.ReadFrameAsync(ct).ConfigureAwait(false);
                    retryCount = 0;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (OperationCanceledException) when (retryCount++ < options.MaxRetryAttempts)
                {
                    Log.Warn($"[AudioPipeline] Read transient cancel (retry {retryCount}/{options.MaxRetryAttempts})");
                    if (!await ct.DelayNoThrowAsync(options.RetryDelay, continueOnCapturedContext: false).ConfigureAwait(false))
                        break;
                    continue;
                }
                catch (OperationCanceledException ex)
                {
                    Log.Error($"[AudioPipeline] Read failed after {retryCount} transient retries: {ex.Message}");
                    onError?.Invoke(ex);
                    break;
                }
                catch (ChunkDownloadFatalException) { throw; }
                catch (FileNotFoundException ex)
                {
                    throw new CacheInvalidatedException(
                        "Cache file was deleted during playback.",
                        CacheInvalidationKind.FileDeleted,
                        isRecoverable: true,
                        trackId: _streamInfo.TrackId,
                        inner: ex);
                }
                catch (DirectoryNotFoundException ex)
                {
                    throw new CacheInvalidatedException(
                        "Cache directory was deleted during playback.",
                        CacheInvalidationKind.FileDeleted,
                        isRecoverable: true,
                        trackId: _streamInfo.TrackId,
                        inner: ex);
                }
                catch (IOException ex) when (ex.HResult is HResultFileNotFound or HResultPathNotFound)
                {
                    throw new CacheInvalidatedException(
                        "Cache file became unavailable during playback.",
                        CacheInvalidationKind.FileDeleted,
                        isRecoverable: true,
                        trackId: _streamInfo.TrackId,
                        inner: ex);
                }
                catch (InvalidDataException) { throw; }
                catch (EndOfStreamException ex)
                {
                    Log.Error($"[AudioPipeline] Decoder fatal: {ex.Message}", ex);
                    throw;
                }
                catch (Exception ex) when (ex is not CacheInvalidatedException && retryCount++ < options.MaxRetryAttempts)
                {
                    Log.Warn($"[AudioPipeline] Read retry {retryCount}: {ex.Message}");
                    if (!await ct.DelayNoThrowAsync(options.RetryDelay, continueOnCapturedContext: false).ConfigureAwait(false))
                        break;
                    continue;
                }

                // Read returned after cancellation does not own ring: frame discarded before decoding
                if (ct.IsCancellationRequested) break;

                if (frame == null)
                {
                    if (ct.IsCancellationRequested) break;

                    if (IsPrematureEndOfStream())
                    {
                        var posMs = _source.PositionMs;
                        var durMs = _streamInfo.DurationMs;
                        Log.Warn($"[AudioPipeline] Truncated cache detected after resync: " +
                                 $"pos={posMs}ms/{durMs}ms — invalidating cache entry");

                        throw new CacheInvalidatedException(
                            $"Cache file is truncated (resync was required): reached {posMs}ms of {durMs}ms",
                            CacheInvalidationKind.ParserResync,
                            isRecoverable: true,
                            trackId: _streamInfo.TrackId);
                    }

                    // If EOF reached, immediately unblock WaitForBufferAsync wait,
                    // even if remaining tail is shorter than standard warm-up threshold (e.g., < 80 ms).
                    Volatile.Write(ref _warmupThreshold, 0);
                    Interlocked.Exchange(ref _warmupTcs, null)?.TrySetResult();

                    await DrainBufferAsync(ct).ConfigureAwait(false);
                    if (!ct.IsCancellationRequested) onTrackEnded?.Invoke();
                    break;
                }

                try
                {
                    if (Interlocked.CompareExchange(ref _decoderResetNeeded, 0, 1) == 1)
                    {
                        _decoder.FlushState();
                    }

                    long seekTarget = Volatile.Read(ref _seekTargetMs);
                    if (seekTarget >= 0)
                    {
                        const long DecoderPreRollMs = 120;
                        long frameTs = frame.Value.TimestampMs;

                        if (frameTs < seekTarget)
                        {
                            if (frameTs + DecoderPreRollMs >= seekTarget)
                            {
                                _ = _decoder.Decode(frame.Value.Data.Span, _decodeBuffer);
                                EnsureRuntimeFormatMatchesDecoder();
                                // Decrement the required decoder warmup counter only when a pre-roll frame has actually been decoded!
                                int remSkip = Volatile.Read(ref _skipFramesCounter);
                                if (remSkip > 0)
                                    Interlocked.Decrement(ref _skipFramesCounter);
                            }
                            continue;
                        }

                        Volatile.Write(ref _seekTargetMs, -1L);
                        // Do NOT reset _skipFramesCounter here! If the target lands at the very beginning of a cluster (e.g., 80001ms),
                        // the decoder must skip the remaining 1-2 warmup frames after FlushState() to avoid producing garbage transient spikes in audio!
                    }

                    int skipCount = Volatile.Read(ref _skipFramesCounter);
                    if (skipCount > 0)
                    {
                        // Warm up codec internal overlap/prediction state only; never touch render-thread DSP state from decoder thread.
                        _ = _decoder.Decode(frame.Value.Data.Span, _decodeBuffer);
                        EnsureRuntimeFormatMatchesDecoder();
                        Interlocked.Decrement(ref _skipFramesCounter);
                        continue;
                    }

                    int samplesDecoded = _decoder.Decode(frame.Value.Data.Span, _decodeBuffer);
                    if (samplesDecoded > 0)
                        EnsureRuntimeFormatMatchesDecoder();

                    if (samplesDecoded > 0)
                    {
                        int totalSamples = samplesDecoded * _activeChannels;
                        int written = 0;
                        while (true)
                        {
                            int accepted = _pcmBuffer.Write(_decodeBuffer.AsSpan(written, totalSamples - written));
                            written += accepted;
                            // The clock is rebased from this counter: it contains only what actually entered the ring buffer
                            Interlocked.Add(ref _decodedSamples, accepted);
                            if (written >= totalSamples) break;

                            // While appending the remainder, warmup must have a chance to complete; otherwise the consumer won't start and won't free up space
                            int pendingThreshold = Volatile.Read(ref _warmupThreshold);
                            if (pendingThreshold > 0 && _pcmBuffer.Count >= pendingThreshold)
                            {
                                Volatile.Write(ref _warmupThreshold, 0);
                                Interlocked.Exchange(ref _warmupTcs, null)?.TrySetResult();
                            }

                            if (_isPaused)
                            {
                                await WaitForResumeOrCancelAsync(ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                                if (ct.IsCancellationRequested) break;
                                continue;
                            }

                            // The ring buffer accepted only a part of the block: the remainder is written as space becomes available, rather than dropped
                            int delayMs = ComputeBufferFullDelayMs(_pcmBuffer.Count, _decoder.SampleRate, _decoder.Channels);
                            if (!await ct.DelayNoThrowAsync(delayMs, continueOnCapturedContext: false).ConfigureAwait(false))
                                break;
                        }

                        int threshold = Volatile.Read(ref _warmupThreshold);
                        if (threshold > 0 && _pcmBuffer.Count >= threshold)
                        {
                            Volatile.Write(ref _warmupThreshold, 0);
                            var tcs = Interlocked.Exchange(ref _warmupTcs, null);
                            tcs?.TrySetResult();
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Log.Warn($"[AudioPipeline] Decode error: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (CacheInvalidatedException ex)
        {
            Log.Warn($"[AudioPipeline] Cache invalidated: {ex.Message}");
            onError?.Invoke(ex);
        }
        catch (Exception ex) when (ex is not CacheInvalidatedException)
        {
            Log.Error($"[AudioPipeline] Decoder fatal: {ex.Message}", ex);
            onError?.Invoke(ex);
        }
    }

    /// <summary>
    /// Calculates the adaptive decoder loop delay when the ring buffer is full,
    /// reducing OS context switches when there is a large supply of decoded PCM.
    /// </summary>
    internal static int ComputeBufferFullDelayMs(int bufferedSamples, int sampleRate, int channels)
    {
        int samplesPerSec = sampleRate * channels;
        if (samplesPerSec <= 0) return BufferFullDelayMs;

        int bufferedMs = (int)((long)bufferedSamples * 1000 / samplesPerSec);
        if (bufferedMs >= BufferMaxThresholdMs) return BufferMaxDelayMs;
        if (bufferedMs >= BufferHighThresholdMs) return BufferHighDelayMs;
        if (bufferedMs >= BufferModerateThresholdMs) return BufferModerateDelayMs;
        return BufferFullDelayMs;
    }

    private async Task DrainBufferAsync(CancellationToken ct)
    {
        while (!_pcmBuffer.IsEmpty && !ct.IsCancellationRequested)
        {
            int remainingSamples = _pcmBuffer.Count;
            int samplesPerSecond = SampleRate * Channels;
            int estimatedMs = samplesPerSecond > 0
                ? remainingSamples * 1000 / samplesPerSecond / 2
                : DrainMinDelayMs;

            if (!await ct.DelayNoThrowAsync(Math.Clamp(estimatedMs, DrainMinDelayMs, DrainMaxDelayMs), continueOnCapturedContext: false).ConfigureAwait(false))
                break;
        }
    }

    private void ArmDecoderWarmupAfterSeek(long targetMs)
    {
        int skipFrames = GetSkipFramesAfterSeek(_source.Codec);

        Volatile.Write(ref _skipFramesCounter, skipFrames);
        Volatile.Write(ref _decoderResetNeeded, skipFrames > 0 ? 1 : 0);
        Volatile.Write(ref _seekTargetMs, targetMs);
    }

    private void EnsureRuntimeFormatMatchesDecoder()
    {
        int decRate = _decoder.SampleRate;
        int decCh = _decoder.Channels;
        if (decRate == _activeSampleRate && decCh == _activeChannels)
            return;

        _activeSampleRate = decRate;
        _activeChannels = decCh;

        _pcmBuffer.SetFrameAlignment(decCh);
        _equalizer.UpdateFormat(decRate, decCh);
        _truePeakLimiter = new TruePeakLimiter(decRate);
        _analyzer3Band = new AudioSpectrumAnalyzer(decRate, decCh);

        try
        {
            _backend.Reinitialize(decRate, decCh, AudioCallback);
            _backend.SetDeviceLostCallback(NotifyDeviceLost);
            _backend.SetStarvationCallback(NotifyStarvation);
            _backend.SetDeviceAvailableCallback(NotifyDeviceAvailable);
        }
        catch (AudioDeviceException ex)
        {
            _deviceLost = true;
            Log.Warn($"[AudioPipeline] Runtime format change Reinitialize degraded: {ex.Message}");
        }

        if (_pcmBuffer.IsEmpty)
            RebaseRenderClock(Interlocked.Read(ref _decodedSamples));

        Log.Info($"[AudioPipeline] Runtime format change applied: {decRate}Hz, {decCh}ch");
    }

    private static int GetSkipFramesAfterSeek(AudioCodec codec)
    {
        return codec switch
        {
            AudioCodec.Opus => SkipFramesAfterSeekOpus,
            AudioCodec.Aac => SkipFramesAfterSeekAac,
            _ => 0
        };
    }

    #endregion

    #region Playback Control

    public void ActivateFillLoop()
    {
        if (_disposed) return;
        _backend.ActivateFillLoop();
    }

    public void ActivateBufferingMode()
    {
        if (_disposed) return;
        _source.SetPlaybackActive(true);
        _backend.ActivateFillLoop();
    }

    public bool WaitForBackendWarmup(int timeoutMs = 100)
    {
        if (_disposed) return false;
        return _backend.WaitForWarmup(timeoutMs);
    }

    public ValueTask<bool> WaitForBackendWarmupAsync(int timeoutMs, CancellationToken ct)
    {
        if (_disposed || ct.IsCancellationRequested) return ValueTask.FromResult(false);
        return ValueTask.FromResult(_backend.WaitForWarmup(timeoutMs));
    }

    public void Start()
    {
        if (_disposed) return;
        ReleasePauseGate();
        _source.SetPlaybackActive(true);
        if (Interlocked.Exchange(ref _initialCrossfadePending, 0) == 1)
            SnapCrossfaderToGain();
        _backend.Start();
    }

    /// <summary>
    /// Stops output; spectrum state is reset deferredly inside the audio callback that is still finishing the fade-out.
    /// </summary>
    public void Stop()
    {
        if (_disposed) return;
        lock (_pauseGateLock)
        {
            _isPaused = true;
        }
        _source.SetPlaybackActive(false);
        _backend.Stop();
        _analyzer3Band.RequestReset();
    }

    /// <summary>
    /// Flushes backend buffers and the PCM ring buffer; spectrum state is reset deferredly inside the audio callback.
    /// </summary>
    public void Flush()
    {
        if (_disposed) return;

        // IMPORTANT: call _backend.Flush() first while decoded samples are still in _pcmBuffer!
        // WinAudioBackend.Flush() will synchronously fetch 480 frames (10 ms) of the live old-wave continuation
        // via AudioCallback for a true wave-to-wave crossfade, and only after that do we clear _pcmBuffer!
        _backend.Flush();

        lock (_lifecycleLock)
        {
            if (_decoderTask is { IsCompleted: false })
            {
                Volatile.Write(ref _pendingRingClear, 1);
            }
            else
            {
                Volatile.Write(ref _pendingRingClear, 0);
                _pcmBuffer.Clear();
            }
        }

        ReleasePauseGate();

        Volatile.Write(ref _warmupThreshold, 0);
        var tcs = Interlocked.Exchange(ref _warmupTcs, null);
        tcs?.TrySetResult();

        _analyzer3Band.RequestReset();
    }

    internal void NotifyStarvation()
    {
        if (_disposed) return;

        var decoderAlive = _decoderTask is { IsCompleted: false };
        Log.Error($"[AudioPipeline] Starvation: decoder={(decoderAlive ? "alive" : "dead")}, ring={_pcmBuffer.Count}");

        EnqueueDeviceEvent(_onStarvationExternal);
    }

    /// <summary>
    /// Prepares the DSP chain for seeking; spectrum state is reset deferredly inside the audio callback.
    /// </summary>
    /// <param name="targetMs">Target position in milliseconds; <c>-1</c> if the target is unknown.</param>
    public void PrepareForSeek(long targetMs = -1)
    {
        ArmDecoderWarmupAfterSeek(targetMs);

        _analyzer.PrepareForSeek();
        _equalizer.Reset();
        _truePeakLimiter.Reset();
        _analyzer3Band.RequestReset();

#if AUDIO_SPLICE_AUDIT
        _dbgCallbackTickAfterSeek = 0;
#endif

        float normGain = _analyzer.IsEnabled ? _analyzer.GetLockedGain() : 1.0f;
        _gainCrossfader.Reset(normGain);

        Volatile.Write(ref _warmupThreshold, 0);
        var tcs = Interlocked.Exchange(ref _warmupTcs, null);
        tcs?.TrySetResult();
    }

    public void StartPreScanNormalization()
    {
        if (_disposed || !_analyzer.IsEnabled || _analyzer.IsGainLocked)
            return;

        if (Volatile.Read(ref _preScanTask) != null)
            return;

        var task = RunDeferredPreScanAsync(_lifetimeCts.Token);
        Volatile.Write(ref _preScanTask, task);
    }

    private async Task RunDeferredPreScanAsync(CancellationToken ct)
    {
        // Let the primary playback decoder fill the initial 250ms warmup window first so managed AAC pre-scan never starves startup!
        if (!await ct.DelayNoThrowAsync(250, continueOnCapturedContext: false).ConfigureAwait(false))
            return;

        await PreScanNormalizationAsync(ct).ConfigureAwait(false);
    }

    public async Task PreScanNormalizationAsync(CancellationToken ct)
    {
        if (!_analyzer.IsEnabled) return;
        if (_analyzer.IsGainLocked) return;

        float maxScanSeconds = _analyzer.CurrentConfig.PreScanDurationMs > 0
            ? Math.Clamp(_analyzer.CurrentConfig.PreScanDurationMs / 1000f, MinPreScanSeconds, 120f)
            : IsolatedScanMaxSeconds;

        if (_source.Codec == AudioCodec.Aac)
            maxScanSeconds = Math.Min(maxScanSeconds, IsolatedScanMaxSeconds);

        if (_source is Sources.LocalFileSource localSource)
        {
            await RunPreScanForFileAsync(
                localSource.FilePath, localSource.Codec, ct, maxScanSeconds).ConfigureAwait(false);
            return;
        }

        if (_source is Sources.CachingStreamSource cachingSource)
        {
            var cacheManager = AudioSourceFactory.GlobalCache;
            if (cacheManager == null) return;

            long contiguousBytes = cachingSource.ContiguousPrefixBytes;
            int bitrate = cachingSource.Bitrate;
            long minBytes = (long)(Math.Max(1, bitrate) * 1000.0 / 8.0 * MinPreScanSeconds);

            if (contiguousBytes < minBytes)
            {
                Log.Debug($"[AudioPipeline] Pre-scan skipped: insufficient contiguous prefix " +
                          $"({contiguousBytes / 1024}KB < {minBytes / 1024}KB for {MinPreScanSeconds}s)");
                return;
            }

            string cachePath = cacheManager.GetCachePath(cachingSource.CacheKey);
            if (!File.Exists(cachePath)) return;

            float prefixSeconds = (float)(contiguousBytes / (Math.Max(1, bitrate) * 1000.0 / 8.0));
            float scanSeconds = Math.Min(prefixSeconds, maxScanSeconds);

            await RunPreScanForFileAsync(
                cachePath, cachingSource.Codec, ct, scanSeconds).ConfigureAwait(false);
            return;
        }

        Log.Debug($"[AudioPipeline] Pre-scan skipped: unsupported source type {_source.GetType().Name}");
    }

    private async Task RunPreScanForFileAsync(
     string filePath, AudioCodec codec, CancellationToken ct,
     float scanMaxSeconds = IsolatedScanMaxSeconds)
    {
        try
        {
            var (integratedLufs, rawGain) = await IsolatedPreScanHelper.RunAsync(
                filePath, codec,
                _analyzer.CurrentConfig.TargetLufs,
                _analyzer.CurrentConfig.MaxGain,
                scanMaxSeconds,
                ct).ConfigureAwait(false);

            if (float.IsFinite(integratedLufs))
                _analyzer.NotifyIntegratedLufs(integratedLufs);

            _analyzer.LockGain(rawGain);

            Log.Debug($"[AudioPipeline] Pre-scan complete: lufs={integratedLufs:F2}, " +
                      $"gain={rawGain:F4}x, limit={scanMaxSeconds:F1}s, " +
                      $"file={Path.GetFileName(filePath)}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"[AudioPipeline] Pre-scan failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Sets the decoder position and binds the render clock to it: the next played sample corresponds to <paramref name="samples"/>.
    /// </summary>
    /// <param name="samples">Stream position in samples (frames × channels).</param>
    public void SetDecodedSamplesPosition(long samples)
    {
        Volatile.Write(ref _decodedSamples, samples);
        RebaseRenderClock(samples);
    }

    /// <summary>
    /// Converts device queue size to level display latency in <see cref="System.Diagnostics.Stopwatch"/> ticks.
    /// </summary>
    /// <param name="bufferedDeviceSamples">Samples in device queue (device frames × channels).</param>
    /// <param name="deviceSampleRate">Device sample rate in Hz used for queue calculation.</param>
    /// <param name="channels">Number of channels.</param>
    /// <returns>Latency in ticks; <c>0</c> if queue is empty or sample rate is unknown.</returns>
    internal static long ComputePresentationDelayTicks(int bufferedDeviceSamples, int deviceSampleRate, int channels)
    {
        long samplesPerSecond = (long)deviceSampleRate * channels;
        if (bufferedDeviceSamples <= 0 || samplesPerSecond <= 0)
            return 0;

        return bufferedDeviceSamples * System.Diagnostics.Stopwatch.Frequency / samplesPerSecond;
    }

    /// <summary>
    /// Binds the render clock to the stream position: everything the backend has already passed to the device plays before the anchor point.
    /// </summary>
    private void RebaseRenderClock(long baseSamples) =>
        Volatile.Write(ref _clockOrigin, new ClockOrigin(baseSamples, _backend.RenderedSamples));

    public float GetLockedNormalizationGain() => _analyzer.GetLockedGain();

    public void SetInitialNormalizationGain(float gain)
    {
        if (_disposed) return;
        _analyzer.SetInitialGain(gain);
    }

    public void SnapCrossfaderToGain()
    {
        if (_disposed) return;
        float normGain = _analyzer.IsEnabled ? _analyzer.GetLockedGain() : 1.0f;
        _gainCrossfader.Reset(normGain);
    }

    private bool IsPrematureEndOfStream()
    {
        IContainerParser? parser = _source switch
        {
            Sources.CachingStreamSource cs => cs.Parser,
            Sources.LocalFileSource lf => lf.Parser,
            _ => null
        };

        if (parser is not { ResyncOccurred: true })
            return false;

        long durationMs = _streamInfo.DurationMs;
        if (durationMs <= 0) return false;

        long positionMs = _source.PositionMs;
        if (positionMs < 0) return false;

        return positionMs + PrematureEndToleranceMs < durationMs;
    }

    #endregion

    #region Audio Callback

#if AUDIO_SPLICE_AUDIT
    private float _dbgPrevRawL, _dbgPrevRawR;
    private float _dbgPrevEqL, _dbgPrevEqR;
    private float _dbgPrevLimL, _dbgPrevLimR;
    private int _dbgCallbackTickAfterSeek;
#endif

    private int AudioCallback(Span<float> buffer)
    {
        if (_disposed)
        {
            buffer.Clear();
            _analyzer3Band.Reset();
            return 0;
        }

        int read = _pcmBuffer.Read(buffer);
        if (read < buffer.Length) buffer[read..].Clear();

        int channels = _activeChannels;
        var limiter = _truePeakLimiter;
        var analyzer3Band = _analyzer3Band;

        if (read > 0)
        {
            var samples = buffer[..read];

#if AUDIO_SPLICE_AUDIT
            int frames = read / channels;
            float maxRawStep = 0f, maxRawD2 = 0f;
            int rawStepFrame = -1;
            if (channels >= 2 && frames >= 2)
            {
                float pL = _dbgPrevRawL, pR = _dbgPrevRawR;
                float ppL = pL;
                for (int f = 0; f < frames; f++)
                {
                    float cL = samples[f * channels];
                    float cR = samples[f * channels + 1];
                    float st = MathF.Max(MathF.Abs(cL - pL), MathF.Abs(cR - pR));
                    float d2 = MathF.Abs(cL - 2f * pL + ppL);
                    if (st > maxRawStep) { maxRawStep = st; rawStepFrame = f; }
                    if (d2 > maxRawD2) maxRawD2 = d2;
                    ppL = pL; pL = cL; pR = cR;
                }
                _dbgPrevRawL = pL; _dbgPrevRawR = pR;
            }
#endif

            // 1. Apply 10-band equalizer BEFORE the limiter and spectrum analyzer
            _equalizer.Process(samples);

#if AUDIO_SPLICE_AUDIT
            float maxEqStep = 0f, maxEqD2 = 0f;
            int eqStepFrame = -1;
            if (channels >= 2 && frames >= 2)
            {
                float pL = _dbgPrevEqL, pR = _dbgPrevEqR;
                float ppL = pL;
                for (int f = 0; f < frames; f++)
                {
                    float cL = samples[f * channels];
                    float cR = samples[f * channels + 1];
                    float st = MathF.Max(MathF.Abs(cL - pL), MathF.Abs(cR - pR));
                    float d2 = MathF.Abs(cL - 2f * pL + ppL);
                    if (st > maxEqStep) { maxEqStep = st; eqStepFrame = f; }
                    if (d2 > maxEqD2) maxEqD2 = d2;
                    ppL = pL; pL = cL; pR = cR;
                }
                _dbgPrevEqL = pL; _dbgPrevEqR = pR;
            }
#endif

            float normGain = _analyzer.IsEnabled
                ? _analyzer.ProcessSamples()
                : 1.0f;

            _gainCrossfader.SetTarget(normGain, _activeSampleRate, channels);

            // 2. Always run signal through TruePeakLimiter (0 B allocations)
            limiter.Process(samples, channels, ref _gainCrossfader);

#if AUDIO_SPLICE_AUDIT
            float maxLimStep = 0f, maxLimD2 = 0f;
            int limStepFrame = -1;
            if (channels >= 2 && frames >= 2)
            {
                float pL = _dbgPrevLimL, pR = _dbgPrevLimR;
                float ppL = pL;
                for (int f = 0; f < frames; f++)
                {
                    float cL = samples[f * channels];
                    float cR = samples[f * channels + 1];
                    float st = MathF.Max(MathF.Abs(cL - pL), MathF.Abs(cR - pR));
                    float d2 = MathF.Abs(cL - 2f * pL + ppL);
                    if (st > maxLimStep) { maxLimStep = st; limStepFrame = f; }
                    if (d2 > maxLimD2) maxLimD2 = d2;
                    ppL = pL; pL = cL; pR = cR;
                }
                _dbgPrevLimL = pL; _dbgPrevLimR = pR;

                int cbTick = ++_dbgCallbackTickAfterSeek;
                if (cbTick <= 3 || maxLimStep > 0.08f || maxLimD2 > 0.05f)
                {
                    Log.Debug(
                        $"[Pipeline-Audit] tick#{cbTick} frames={frames} ringRem={_pcmBuffer.Count / channels}f | " +
                        $"RAW(step={maxRawStep:F4}@{rawStepFrame}, d2={maxRawD2:F4}) -> " +
                        $"EQ(step={maxEqStep:F4}@{eqStepFrame}, d2={maxEqD2:F4}) -> " +
                        $"LIM(step={maxLimStep:F4}@{limStepFrame}, d2={maxLimD2:F4}, env={_truePeakLimiter.EnvelopeGain:F3}, norm={_gainCrossfader.CurrentGain:F3})");
                }
            }
#endif

            if (AudioVisualizerBuffer.HasActiveReaders)
            {
                // Spectrum is calculated only for active readers: UI polling extends the lease; without it, callback skips render work
                _spectrumSuspended = false;

                // BufferedSamples is already normalized to source sample rate by the backend
                long delayTicks = ComputePresentationDelayTicks(
                    _backend.BufferedSamples,
                    _activeSampleRate,
                    channels);

                analyzer3Band.ProcessBlock(samples, channels, delayTicks);
            }
            else if (!_spectrumSuspended)
            {
                _spectrumSuspended = true;
                analyzer3Band.Reset();
            }
        }
        else if (_isPaused || !_backend.IsPlaying)
        {
            analyzer3Band.Reset();
        }

        return read / channels;
    }

    #endregion

    #region Buffer Info

    public async Task<bool> WaitForBufferAsync(int minSamples, int maxWaitMs, CancellationToken ct)
    {
        if (_disposed || _pcmBuffer.Count >= minSamples) return true;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _warmupTcs, tcs);
        Volatile.Write(ref _warmupThreshold, minSamples);

        if (_pcmBuffer.Count >= minSamples)
        {
            Volatile.Write(ref _warmupThreshold, 0);
            Interlocked.CompareExchange(ref _warmupTcs, null, tcs);
            tcs.TrySetResult();
            return true;
        }

        try
        {
            await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(maxWaitMs), ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return _pcmBuffer.Count >= minSamples;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            Volatile.Write(ref _warmupThreshold, 0);
            Interlocked.CompareExchange(ref _warmupTcs, null, tcs);
        }
    }

    #endregion

    #region Dispose

    /// <summary>
    /// Disposes the pipeline; shared backend remains alive, so spectrum is reset deferredly rather than from a foreign thread.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Task? decoderTask;
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
            decoderTask = _decoderTask;
        }

        _analyzer3Band.RequestReset();
        ReleasePauseGate();

        try { _lifetimeCts.Cancel(); } catch (ObjectDisposedException) { }
        try { _decoderCts?.Cancel(); } catch (ObjectDisposedException) { }

        if (_source is Sources.CachingStreamSource cachingSource)
            cachingSource.CancelActiveReads();

        var preScanTask = Volatile.Read(ref _preScanTask);
        if (preScanTask != null && !preScanTask.IsCompleted)
        {
            try
            {
                await preScanTask
                    .WaitAsync(TimeSpan.FromMilliseconds(DecoderStopTimeoutMs))
                    .ConfigureAwait(false);
            }
            catch { }
        }

        var deviceTask = Volatile.Read(ref _deviceEventTask);
        if (deviceTask != null && !deviceTask.IsCompleted)
        {
            try
            {
                await deviceTask
                    .WaitAsync(TimeSpan.FromMilliseconds(500))
                    .ConfigureAwait(false);
            }
            catch { }
        }

        bool decoderCompleted = decoderTask == null || decoderTask.IsCompleted;
        if (!decoderCompleted)
        {
            try
            {
                await decoderTask!
                    .WaitAsync(TimeSpan.FromMilliseconds(DecoderStopTimeoutMs))
                    .ConfigureAwait(false);
                decoderCompleted = true;
            }
            catch
            {
                decoderCompleted = decoderTask!.IsCompleted;
            }
        }

        if (decoderCompleted)
        {
            await _source.DisposeAsync().ConfigureAwait(false);
            CleanupOwnedDecoderAndBuffers();
        }
        else
        {
            Log.Warn("[AudioPipeline] Decoder task did not stop within DisposeAsync timeout; deferring buffer/decoder release until exit.");
            _ = FinishHungDisposeAsync(decoderTask!);
        }

#if AUDIO_SPLICE_AUDIT
        Log.Debug($"[AudioPipeline] Disposed (decoder restarts: {_decoderRestartCount})");
#else
        Log.Info("[AudioPipeline] Disposed");
#endif
    }

    private void CleanupOwnedDecoderAndBuffers()
    {
        _decoder.Dispose();
        _pcmBuffer.Dispose();
        ArrayPool<float>.Shared.Return(_decodeBuffer);

        try { _decoderCts?.Dispose(); } catch (ObjectDisposedException) { }
        try { _lifetimeCts.Dispose(); } catch (ObjectDisposedException) { }
    }

    private async Task FinishHungDisposeAsync(Task decoderTask)
    {
        await decoderTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        try { await _source.DisposeAsync().ConfigureAwait(false); } catch { }
        CleanupOwnedDecoderAndBuffers();
    }

    #endregion
}