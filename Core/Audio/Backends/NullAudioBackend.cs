using System.Diagnostics;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Interfaces;

namespace LMP.Core.Audio.Backends;

/// <summary>
/// Stub backend for testing without real audio output.
///
/// <para>Simulates PCM data consumption via an internal consume loop.
/// All device-loss callbacks are no-ops: NullBackend never loses
/// a "device" and does not generate starvation events.</para>
/// </summary>
public sealed class NullAudioBackend : IPlaybackBackend
{
    #region Fields

    private AudioDataCallback? _callback;
    private int _sampleRate;
    private int _channels;
    private volatile float _volume = 1.0f;
    private volatile bool _playing;
    private volatile bool _disposed;

    private Task? _consumeTask;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Serializes callback invocation and clock publication: <see cref="RenderClock"/> allows only a single writer.
    /// </summary>
    private readonly Lock _pullLock = new();
    private readonly RenderClock _renderClock = new();
    private long _renderedSamples;

    #endregion

    #region Properties

    /// <inheritdoc/>
    public string Name => "Null";

    /// <inheritdoc/>
    public float Volume { get => _volume; set => _volume = Math.Clamp(value, 0f, 1f); }

    /// <inheritdoc/>
    public bool IsPlaying => _playing;

    /// <inheritdoc/>
    public int BufferedSamples => 0;

    /// <inheritdoc/>
    public int BufferedBytes => 0;

    /// <inheritdoc/>
    /// <remarks>NullBackend never loses a device — always <c>false</c>.</remarks>
    public bool IsDeviceLost => false;

    /// <inheritdoc/>
    /// <remarks>NullBackend has no device queue: a sample is considered played the moment it is produced by the callback.</remarks>
    public long RenderedSamples => Volatile.Read(ref _renderedSamples);

    /// <inheritdoc/>
    public RenderClockSnapshot ReadRenderClock() => _renderClock.Read();

    #endregion

    #region Initialize / Reinitialize

    /// <inheritdoc/>
    public void Initialize(int sampleRate, int channels, AudioDataCallback dataCallback)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _callback = dataCallback;

        Log.Debug($"[NullAudioBackend] Initialized: {sampleRate}Hz, {channels}ch");
    }

    /// <inheritdoc/>
    public void Reinitialize(int sampleRate, int channels, AudioDataCallback dataCallback)
        => Initialize(sampleRate, channels, dataCallback);

    #endregion

    #region Warmup Protocol

    /// <inheritdoc/>
    public void ActivateFillLoop() { }

    /// <inheritdoc/>
    /// <remarks>NullBackend is always "warmed up" — immediately returns <c>true</c>.</remarks>
    public bool WaitForWarmup(int timeoutMs = 3000) => true;

    #endregion

    #region Start / Stop / Flush

    /// <inheritdoc/>
    public void Start()
    {
        if (_disposed || _playing || _callback == null) return;

        _playing = true;
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _consumeTask = Task.Run(() => ConsumeLoopAsync(cts.Token));

        Log.Debug("[NullAudioBackend] Started");
    }

    /// <inheritdoc/>
    public void Stop()
    {
        _playing = false;
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { }

        Log.Debug("[NullAudioBackend] Stopped");
    }

    /// <inheritdoc/>
    public void Flush() { }

    /// <summary>
    /// Simulates PCM consumption at real-time playback rate without throwing TaskCanceledException on stop.
    /// </summary>
    private async Task ConsumeLoopAsync(CancellationToken ct)
    {
        var buffer = new float[1024 * Math.Max(1, _channels)];
        int samplesPerSecond = Math.Max(1, _sampleRate * _channels);
        int delayMs = Math.Max(1000 * buffer.Length / samplesPerSecond, 10);

        while (!ct.IsCancellationRequested && _playing)
        {
            ConsumeTick(buffer, samplesPerSecond);
            if (!await ct.DelayNoThrowAsync(delayMs, continueOnCapturedContext: false).ConfigureAwait(false))
                break;
        }
    }

    /// <summary>
    /// Fetches one block from the callback and publishes the render clock under <see cref="_pullLock"/>.
    /// </summary>
    private void ConsumeTick(float[] buffer, int samplesPerSecond)
    {
        lock (_pullLock)
        {
            int frames = _callback?.Invoke(buffer) ?? 0;
            long rendered = _renderedSamples;
            if (frames > 0)
            {
                rendered += (long)frames * Math.Max(1, _channels);
                Volatile.Write(ref _renderedSamples, rendered);
            }

            _renderClock.Publish(rendered, 0, Stopwatch.GetTimestamp(), samplesPerSecond);
        }
    }

    #endregion

    #region Device Loss — no-op stubs

    /// <inheritdoc/>
    /// <remarks>
    /// NullBackend has no real device — the callback will never be invoked.
    /// The stub implements the interface contract without side effects.
    /// </remarks>
    public void SetDeviceLostCallback(Action? callback) { }

    /// <inheritdoc/>
    /// <remarks>
    /// NullBackend does not run a device watcher — the device is never lost.
    /// The stub implements the interface contract without side effects.
    /// </remarks>
    public void SetDeviceAvailableCallback(Action? callback) { }

    /// <inheritdoc/>
    /// <remarks>
    /// NullBackend does not generate starvation events — the consume loop always drains data.
    /// The stub implements the interface contract without side effects.
    /// </remarks>
    public void SetStarvationCallback(Action? callback) { }

    /// <inheritdoc/>
    public void SetVolumeGain(float gain) { }

    #endregion

    #region Dispose

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _playing = false;
        _cts?.Cancel();

        try { _consumeTask?.Wait(500); }
        catch { /* Ignore */ }

        _cts?.Dispose();

        Log.Debug("[NullAudioBackend] Disposed");
    }

    #endregion
}