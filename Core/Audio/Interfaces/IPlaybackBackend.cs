namespace LMP.Core.Audio.Interfaces;

/// <summary>
/// Abstraction over the operating system audio API (WaveOut, ALSA, PulseAudio, etc).
///
/// <para><b>Lifecycle:</b></para>
/// <list type="number">
///   <item><see cref="Initialize"/> — initial initialization (creates the device, starts the fill loop)</item>
///   <item><see cref="Reinitialize"/> — reinitialization for a new track (fast/slow path)</item>
///   <item><see cref="ActivateFillLoop"/> — activates data transfer to the buffer WITHOUT playback</item>
///   <item><see cref="WaitForWarmup"/> — waits for a minimum amount of data to accumulate in the buffer</item>
///   <item><see cref="Start"/> — starts playback</item>
///   <item><see cref="Stop"/> — stops with a micro fade-out</item>
///   <item><see cref="Flush"/> — flushes buffers (for seeking)</item>
/// </list>
///
/// <para><b>Warmup Protocol:</b></para>
/// <para>To prevent artifacts when starting a track, the calling code should:</para>
/// <code>
/// backend.ActivateFillLoop();          // Fill loop starts populating BufferedWaveProvider
/// backend.WaitForWarmup(timeoutMs);    // Wait for ≥200ms of data in the buffer
/// backend.Start();                     // Now safe to start playback
/// </code>
///
/// <para><b>Device Loss Protocol:</b></para>
/// <para>The backend notifies subscribers of device events via callbacks:</para>
/// <code>
/// backend.SetDeviceLostCallback(() => /* soft pause */);
/// backend.SetDeviceAvailableCallback(() => /* auto-recovery */);
/// backend.SetStarvationCallback(() => /* diagnostics */);
/// </code>
/// <para>All callbacks are invoked from a background thread — implementations must not block.</para>
/// </summary>
internal interface IPlaybackBackend : IDisposable
{
    /// <summary>
    /// Initial initialization of the backend.
    /// Creates the audio device and starts the fill loop.
    /// </summary>
    /// <param name="sampleRate">Sample rate (Hz).</param>
    /// <param name="channels">Number of channels.</param>
    /// <param name="dataCallback">PCM data supply callback.</param>
    void Initialize(int sampleRate, int channels, AudioDataCallback dataCallback);

    /// <summary>
    /// Reinitializes the backend for a new track.
    ///
    /// <para><b>Fast path</b> (format matches): updates callback, flushes buffers. ~0ms.</para>
    /// <para><b>Slow path</b> (format changed): recreates the audio device. ~50-200ms.</para>
    /// <para><b>First call</b> (Initialize was not called): delegates to Initialize.</para>
    ///
    /// <para>After Reinitialize, the backend is in a stopped state.
    /// The calling code must use the warmup protocol:
    /// <see cref="ActivateFillLoop"/> → <see cref="WaitForWarmup"/> → <see cref="Start"/>.</para>
    /// </summary>
    /// <param name="sampleRate">Sample rate (Hz).</param>
    /// <param name="channels">Number of channels.</param>
    /// <param name="dataCallback">PCM data supply callback.</param>
    void Reinitialize(int sampleRate, int channels, AudioDataCallback dataCallback);

    /// <summary>
    /// Activates the fill loop to transfer data from the callback to the internal buffer,
    /// WITHOUT starting playback.
    ///
    /// <para>Called before <see cref="WaitForWarmup"/> to fill the buffer
    /// before <see cref="Start"/> launches the audio device.</para>
    ///
    /// <para>If the fill loop is already active — no-op.</para>
    /// </summary>
    void ActivateFillLoop();

    /// <summary>
    /// Waits for the minimum amount of data to accumulate in the internal buffer.
    ///
    /// <para>Blocks the calling thread. Used between
    /// <see cref="ActivateFillLoop"/> and <see cref="Start"/> to guarantee
    /// that the audio device will not start reading from an empty buffer.</para>
    /// </summary>
    /// <param name="timeoutMs">Maximum wait time (ms).</param>
    /// <returns>true if the buffer warmed up to the minimum level; false on timeout.</returns>
    bool WaitForWarmup(int timeoutMs = 3000);

    /// <summary>Starts playback.</summary>
    void Start();

    /// <summary>Stops (pauses) playback with a micro fade-out.</summary>
    void Stop();

    /// <summary>
    /// Flushes the internal buffer. Called during seeking to immediately
    /// jump to a new position without playing stale data.
    /// </summary>
    void Flush();

    //  Device Loss 

    /// <summary>
    /// true if the audio device is lost (BT disconnect, USB unplug, etc.).
    /// When <c>true</c>, calls to <see cref="Start"/> are invalid until restored
    /// via <see cref="Reinitialize"/>.
    /// </summary>
    bool IsDeviceLost { get; }

    /// <summary>
    /// Registers a notification callback for device loss during playback.
    ///
    /// <para>The callback is invoked from a background thread (fill loop / timer).
    /// Implementations must not block — use <see cref="Task.Run(Action)"/> if necessary.</para>
    /// <para>null = unsubscribe.</para>
    /// </summary>
    /// <param name="callback">Device lost event handler.</param>
    void SetDeviceLostCallback(Action? callback);

    /// <summary>
    /// Registers a notification callback for device availability after loss.
    ///
    /// <para>The implementation starts a polling watcher when <see cref="IsDeviceLost"/> = true.
    /// The callback is invoked from the timer thread when a device is detected.
    /// Implementations must not block.</para>
    /// <para>null = stop watcher.</para>
    /// </summary>
    /// <param name="callback">Device available event handler.</param>
    void SetDeviceAvailableCallback(Action? callback);

    /// <summary>
    /// Registers a notification callback for prolonged absence of PCM data.
    ///
    /// <para>Invoked when the fill loop receives no data for over 1 second while the gate is open.
    /// The callback is invoked from the fill thread — must not block.</para>
    /// <para>null = unsubscribe.</para>
    /// </summary>
    /// <param name="callback">Starvation event handler.</param>
    void SetStarvationCallback(Action? callback);

    /// <summary>
    /// Sets the volume gain applied immediately before submitting
    /// PCM to the audio device (on-read path). The change is heard immediately
    /// (latency ≤ one waveOut buffer, ~100ms), bypassing the provider buffer.
    /// </summary>
    /// <remarks>
    /// <para>Used for user volume control (slider).
    /// Normalization gain (<see cref="AudioPipeline"/> → <see cref="GainCrossfader"/>)
    /// is applied separately in AudioCallback when writing to the provider.</para>
    /// <para>Called from the command thread — implementations must be thread-safe.</para>
    /// </remarks>
    /// <param name="gain">Volume gain multiplier [0, MaxVolumeGain].</param>
    void SetVolumeGain(float gain);

    //  Diagnostics 

    /// <summary>Volume (0.0 – 1.0).</summary>
    float Volume { get; set; }

    /// <summary>Gets whether playback is currently active.</summary>
    bool IsPlaying { get; }

    /// <summary>
    /// Number of samples (samples × channels) in the internal buffer
    /// that have been written but not yet played.
    /// </summary>
    int BufferedSamples { get; }

    int BufferedBytes { get; }

    /// <summary>
    /// Monotonic counter of source samples (frames × channels) passed to the device from <see cref="AudioDataCallback"/>
    /// over the lifetime of the backend. Not reset on Flush/Reinitialize: consumer binds to it via a reference point.
    /// </summary>
    long RenderedSamples { get; }

    /// <summary>
    /// Output device sample rate in Hz used to calculate <see cref="BufferedSamples"/>.
    /// <c>0</c> — backend does not resample, and the consumer uses the source rate.
    /// </summary>
    int DeviceSampleRate => 0;

    /// <summary>
    /// Returns a consistent snapshot of the render clock without locks or allocations.
    /// The snapshot is published by a single writer — the device thread.
    /// </summary>
    RenderClockSnapshot ReadRenderClock();

    /// <summary>Backend name for diagnostic purposes.</summary>
    string Name { get; }
}

/// <summary>
/// Callback for requesting PCM data from the backend.
/// </summary>
/// <param name="buffer">Buffer to fill (float32, interleaved).</param>
/// <returns>Number of frames filled (samples / channels).</returns>
internal delegate int AudioDataCallback(Span<float> buffer);