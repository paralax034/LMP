using LMP.Core.Audio.Interfaces;

namespace LMP.Core.Audio.Backends;

/// <summary>
/// Заглушка backend для тестирования без реального аудио вывода.
///
/// <para>Симулирует потребление PCM данных через внутренний consume loop.
/// Все device-loss callbacks являются no-op: NullBackend никогда не теряет
/// «устройство» и не генерирует starvation события.</para>
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
    /// <remarks>NullBackend никогда не теряет устройство — всегда <c>false</c>.</remarks>
    public bool IsDeviceLost => false;

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
    /// <remarks>NullBackend всегда «прогрет» — немедленно возвращает <c>true</c>.</remarks>
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
            _callback?.Invoke(buffer);
            if (!await ct.DelayNoThrowAsync(delayMs, continueOnCapturedContext: false).ConfigureAwait(false))
                break;
        }
    }

    #endregion

    #region Device Loss — no-op stubs

    /// <inheritdoc/>
    /// <remarks>
    /// NullBackend не имеет реального устройства — callback никогда не будет вызван.
    /// Stub реализует контракт интерфейса без side effects.
    /// </remarks>
    public void SetDeviceLostCallback(Action? callback) { }

    /// <inheritdoc/>
    /// <remarks>
    /// NullBackend не запускает device watcher — устройство никогда не теряется.
    /// Stub реализует контракт интерфейса без side effects.
    /// </remarks>
    public void SetDeviceAvailableCallback(Action? callback) { }

    /// <inheritdoc/>
    /// <remarks>
    /// NullBackend не генерирует starvation события — consume loop всегда дренирует данные.
    /// Stub реализует контракт интерфейса без side effects.
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