using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Exceptions;
using NAudio.Wave;

namespace LMP.Core.Audio.Backends;

/// <summary>
/// Аппаратный бэкенд вывода аудио на базе Windows Multimedia API (WinMM).
/// </summary>
/// <remarks>
/// <para>
/// Использует прямые вызовы к системной библиотеке <c>winmm.dll</c> через <see cref="LibraryImportAttribute"/> 
/// с ручным управлением неуправляемыми буферами <c>WAVEHDR</c> в нативной памяти (<see cref="NativeMemory"/>).
/// Полностью совместим с Native AOT и агрессивным триммингом сборок (zero-reflection).
/// </para>
/// <para>
/// <b>Внимание:</b> Данный бэкенд предназначен исключительно для семейства операционных систем Windows.
/// При исполнении на Linux и macOS среда генерирует платформенное исключение загрузки динамической библиотеки.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class NAudioBackend : IPlaybackBackend
{
    #region WinMM Native Structs & LibraryImports

    private const int MMSYSERR_NOERROR = 0;
    private const uint WAVE_MAPPER = unchecked((uint)-1);
    private const uint CALLBACK_EVENT = 0x00050000;
    private const int WHDR_DONE = 0x00000001;
    private const int WHDR_INQUEUE = 0x00000010;

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public nint lpData;
        public int dwBufferLength;
        public int dwBytesRecorded;
        public nint dwUser;
        public int dwFlags;
        public int dwLoops;
        public nint lpNext;
        public nint reserved;
    }

    [LibraryImport("winmm.dll")]
    private static partial int waveOutGetNumDevs();

    [LibraryImport("winmm.dll")]
    private static partial int waveOutOpen(
        out nint phwo,
        uint uDeviceID,
        in WAVEFORMATEX pwfx,
        nint dwCallback,
        nint dwInstance,
        uint fdwOpen);

    [LibraryImport("winmm.dll")]
    private static partial int waveOutPrepareHeader(nint hwo, nint pwh, uint cbwh);

    [LibraryImport("winmm.dll")]
    private static partial int waveOutUnprepareHeader(nint hwo, nint pwh, uint cbwh);

    [LibraryImport("winmm.dll")]
    private static partial int waveOutWrite(nint hwo, nint pwh, uint cbwh);

    [LibraryImport("winmm.dll")]
    private static partial int waveOutReset(nint hwo);

    [LibraryImport("winmm.dll")]
    private static partial int waveOutClose(nint hwo);

    [LibraryImport("winmm.dll")]
    private static partial int waveOutSetVolume(nint hwo, uint dwVolume);

    [LibraryImport("winmm.dll")]
    private static partial int waveOutGetVolume(nint hwo, out uint pdwVolume);

    #endregion

    #region Constants

    private const double InternalBufferSeconds = 0.5;
    private const int DesiredLatencyMs = 300;
    private const int NumberOfBuffers = 3;
    private const double BufferHighWaterMark = 0.8;
    private const int IdleSleepMs = 10;
    private const int EmptyCallbackSleepMs = 5;
    private const int PostFlushSleepMs = 10;
    private const int ErrorSleepMs = 100;
    private const int FillWakeupTimeoutMs = 200;
    private const int FillThreadJoinTimeoutMs = 500;
    private const int FadeFrames = 2400;
    private const int UnderrunLogThreshold = 50;
    private const int DeviceHealthCheckInterval = 50;
    private const int ChunkDivisor = 20;
    private const int StarvationThreshold = 200;
    private const int DeviceRecoveryDelayMs = 300;
    private const int DeviceRecoveryMaxRetries = 3;
    private const int PostDisposeSettleMs = 50;
    private const int DeviceWatchIntervalMs = 2000;

    #endregion

    #region Fields

    private nint _hWaveOut;
    private AutoResetEvent? _waveCallbackEvent;
    private Thread? _playbackThread;
    private volatile bool _playbackRunning;

    private unsafe WAVEHDR*[]? _headers;
    private nint[]? _bufferPointers;
    private int _bufferByteSize;

    private BufferedWaveProvider? _provider;
    private GainWaveProvider? _gainProvider;
    private AudioDataCallback? _callback;

    private int _channels;
    private int _sampleRate;
    private float[]? _floatBuffer;
    private byte[]? _byteBuffer;

    private volatile bool _fillActive;
    private volatile bool _gateOpen;
    private volatile bool _deviceLost;

    private Action? _onDeviceAvailable;
    private Timer? _deviceWatchTimer;
    private volatile bool _disposed;

    private readonly Lock _stateLock = new();
    private int _flushGeneration;

    private Thread? _fillThread;
    private CancellationTokenSource? _cts;
    private readonly ManualResetEventSlim _fillWakeup = new(false);

    private int _consecutiveUnderrunCount;
    private int _fillLoopIterations;
    private Action? _onDeviceLost;

    private float _fadeGain;
    private volatile bool _fadingIn;
    private volatile bool _fadingOut;

    private Action? _onStarvation;

    #endregion

    #region Properties

    /// <inheritdoc/>
    public string Name => "WinMM-AOT";

    /// <inheritdoc/>
    public float Volume
    {
        get;
        set
        {
            field = Math.Clamp(value, 0f, 1f);
            if (_hWaveOut != 0)
            {
                uint val = (uint)(field * 0xFFFF) & 0xFFFF;
                uint stereo = val | (val << 16);
                int res = waveOutSetVolume(_hWaveOut, stereo);
                if (res != MMSYSERR_NOERROR)
                {
                    _deviceLost = true;
                    StartDeviceWatcher();
                    Log.Warn($"[NAudioBackend] waveOutSetVolume failed (code {res})");
                }
            }
        }
    } = 1.0f;

    /// <inheritdoc/>
    public bool IsPlaying => _gateOpen && !_fadingOut;

    /// <inheritdoc/>
    public bool IsDeviceLost => _deviceLost;

    /// <inheritdoc/>
    public int BufferedSamples =>
        _provider != null ? _provider.BufferedBytes / sizeof(float) : 0;

    /// <inheritdoc/>
    public int BufferedBytes =>
        _provider?.BufferedBytes ?? 0;

    #endregion

    #region Initialize / Reinitialize

    /// <inheritdoc/>
    public void Initialize(int sampleRate, int channels, AudioDataCallback dataCallback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(dataCallback);

        _callback = dataCallback;
        _channels = channels;
        _sampleRate = sampleRate;

        bool wasDeviceLost = _deviceLost;
        int maxAttempts = wasDeviceLost ? DeviceRecoveryMaxRetries : 0;

        for (int attempt = 0; attempt <= maxAttempts; attempt++)
        {
            if (wasDeviceLost)
            {
                int delay = DeviceRecoveryDelayMs * (attempt + 1);
                Log.Info($"[NAudioBackend] Device recovery attempt {attempt + 1}/{maxAttempts + 1}, waiting {delay}ms for endpoint stabilization");
                Thread.Sleep(delay);
            }

            StopFillThread();
            DisposeWaveOutSafe();
            Thread.Sleep(PostDisposeSettleMs);

            try
            {
                CreateWaveOut(sampleRate, channels);
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"[NAudioBackend] CreateWaveOut attempt {attempt + 1} failed: {ex.Message}");

                if (attempt >= maxAttempts)
                {
                    _deviceLost = true;
                    DisposeWaveOutSafe();
                    StartDeviceWatcher();
                    Log.Error($"[NAudioBackend] Failed to open audio device after {attempt + 1} attempts: {ex.Message}");
                    throw new AudioDeviceException(GetDeviceErrorMessage(), ex);
                }
            }
        }

        AllocateBuffers(sampleRate, channels);
        StartFillThread();
        StartPlaybackThread();

        _deviceLost = false;
        StopDeviceWatcher();
        Log.Info($"[NAudioBackend] Initialized WinMM (never-stop AOT-hardened): {sampleRate}Hz, {channels}ch");
    }

    /// <inheritdoc/>
    public void Reinitialize(int sampleRate, int channels, AudioDataCallback dataCallback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(dataCallback);

        if (_hWaveOut == 0 || _deviceLost)
        {
            Initialize(sampleRate, channels, dataCallback);
            return;
        }

        _callback = dataCallback;

        if (!_playbackRunning)
        {
            _deviceLost = true;
            Initialize(sampleRate, channels, dataCallback);
            return;
        }

        lock (_stateLock)
        {
            _fillActive = false;
            _gateOpen = false;
            _fadingIn = false;
            _fadingOut = false;
            _fadeGain = 0f;
        }

        Volatile.Write(ref _consecutiveUnderrunCount, 0);
        Interlocked.Increment(ref _flushGeneration);

        if (sampleRate == _sampleRate && channels == _channels)
        {
            _provider?.ClearBuffer();
            Log.Info($"[NAudioBackend] Reinit fast path: {sampleRate}Hz, {channels}ch");
            return;
        }

        Initialize(sampleRate, channels, dataCallback);
    }

    /// <inheritdoc/>
    public void SetDeviceLostCallback(Action? callback) => _onDeviceLost = callback;

    /// <inheritdoc/>
    public void SetStarvationCallback(Action? callback) => _onStarvation = callback;

    /// <inheritdoc/>
    public void SetDeviceAvailableCallback(Action? callback)
    {
        _onDeviceAvailable = callback;
        if (callback != null && _deviceLost && !_disposed)
            StartDeviceWatcher();
        else if (callback == null)
            StopDeviceWatcher();
    }

    #endregion

    #region Native AOT Playback Engine

    private unsafe void CreateWaveOut(int sampleRate, int channels)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

        _provider = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromSeconds(InternalBufferSeconds),
            DiscardOnBufferOverflow = true
        };

        _gainProvider = new GainWaveProvider(_provider);

        var wfx = new WAVEFORMATEX
        {
            wFormatTag = 0x0003, // WAVE_FORMAT_IEEE_FLOAT
            nChannels = (ushort)channels,
            nSamplesPerSec = (uint)sampleRate,
            nAvgBytesPerSec = (uint)(sampleRate * channels * sizeof(float)),
            nBlockAlign = (ushort)(channels * sizeof(float)),
            wBitsPerSample = 32,
            cbSize = 0
        };

        _waveCallbackEvent = new AutoResetEvent(false);

        int res = waveOutOpen(
            out _hWaveOut,
            WAVE_MAPPER,
            in wfx,
            _waveCallbackEvent.SafeWaitHandle.DangerousGetHandle(),
            0,
            CALLBACK_EVENT);

        if (res != MMSYSERR_NOERROR)
            throw new InvalidOperationException($"waveOutOpen failed with error code: {res}");

        _bufferByteSize = (int)(wfx.nAvgBytesPerSec * (DesiredLatencyMs / 1000.0) / NumberOfBuffers);
        _bufferByteSize -= _bufferByteSize % wfx.nBlockAlign;

        _headers = new WAVEHDR*[NumberOfBuffers];
        _bufferPointers = new nint[NumberOfBuffers];

        uint headerSize = (uint)sizeof(WAVEHDR);

        for (int i = 0; i < NumberOfBuffers; i++)
        {
            nint pData = (nint)NativeMemory.AllocZeroed((nuint)_bufferByteSize);
            _bufferPointers[i] = pData;

            WAVEHDR* pHdr = (WAVEHDR*)NativeMemory.AllocZeroed(headerSize);
            pHdr->lpData = pData;
            pHdr->dwBufferLength = _bufferByteSize;
            pHdr->dwFlags = 0;

            res = waveOutPrepareHeader(_hWaveOut, (nint)pHdr, headerSize);
            if (res != MMSYSERR_NOERROR)
                throw new InvalidOperationException($"waveOutPrepareHeader failed with error code: {res}");

            _headers[i] = pHdr;
        }

        Volume = Volume;
    }

    private void StartPlaybackThread()
    {
        _playbackRunning = true;
        _playbackThread = new Thread(NativePlaybackLoop)
        {
            Name = "AotWaveOutPlayback",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };
        _playbackThread.Start();
    }

    private unsafe void NativePlaybackLoop()
    {
        byte[] tempBuffer = new byte[_bufferByteSize];
        uint headerSize = (uint)sizeof(WAVEHDR);

        while (_playbackRunning && !_disposed)
        {
            try
            {
                if (_hWaveOut == 0 || _headers == null || _bufferPointers == null || _gainProvider == null)
                    break;

                bool wroteAny = false;

                for (int i = 0; i < NumberOfBuffers; i++)
                {
                    WAVEHDR* hdr = _headers[i];
                    if ((hdr->dwFlags & WHDR_INQUEUE) == 0)
                    {
                        int read = _gainProvider.Read(tempBuffer, 0, _bufferByteSize);
                        if (read < _bufferByteSize)
                            Array.Clear(tempBuffer, read, _bufferByteSize - read);

                        Marshal.Copy(tempBuffer, 0, _bufferPointers[i], _bufferByteSize);

                        int res = waveOutWrite(_hWaveOut, (nint)hdr, headerSize);
                        if (res != MMSYSERR_NOERROR)
                        {
                            Log.Error($"[NAudioBackend] waveOutWrite failed: code {res}");
                            _playbackRunning = false;
                            break;
                        }
                        wroteAny = true;
                    }
                }

                if (!wroteAny)
                {
                    _waveCallbackEvent?.WaitOne(DesiredLatencyMs / NumberOfBuffers);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[NAudioBackend] Playback thread exception: {ex.Message}");
                break;
            }
        }

        _playbackRunning = false;
    }

    private unsafe void DisposeWaveOutSafe()
    {
        _playbackRunning = false;
        _waveCallbackEvent?.Set();

        if (_playbackThread is { IsAlive: true })
            _playbackThread.Join(FillThreadJoinTimeoutMs);

        _playbackThread = null;

        if (_hWaveOut != 0)
        {
            waveOutReset(_hWaveOut);

            if (_headers != null && _bufferPointers != null)
            {
                uint headerSize = (uint)sizeof(WAVEHDR);
                for (int i = 0; i < _headers.Length; i++)
                {
                    if (_headers[i] != null)
                    {
                        waveOutUnprepareHeader(_hWaveOut, (nint)_headers[i], headerSize);
                        NativeMemory.Free(_headers[i]);
                        _headers[i] = null;
                    }

                    if (_bufferPointers[i] != 0)
                    {
                        NativeMemory.Free((void*)_bufferPointers[i]);
                        _bufferPointers[i] = 0;
                    }
                }
            }

            waveOutClose(_hWaveOut);
            _hWaveOut = 0;
        }

        _waveCallbackEvent?.Dispose();
        _waveCallbackEvent = null;

        _headers = null;
        _bufferPointers = null;
        _provider = null;
        _gainProvider = null;
    }

    #endregion

    #region Playback Flow & Health Check

    /// <inheritdoc/>
    public void ActivateFillLoop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hWaveOut == 0) return;

        lock (_stateLock)
        {
            _fillActive = true;
            _gateOpen = false;
            _fadingIn = false;
            _fadingOut = false;
            _fadeGain = 0f;
        }

        _fillWakeup.Set();
    }

    /// <inheritdoc/>
    public bool WaitForWarmup(int timeoutMs = 100) => !_disposed && _hWaveOut != 0;

    /// <inheritdoc/>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hWaveOut == 0) return;

        if (_deviceLost)
            throw new AudioDeviceException(GetDeviceErrorMessage());

        lock (_stateLock)
        {
            if (_gateOpen && !_fadingOut) return;

            _fillActive = true;
            _gateOpen = true;
            _fadingOut = false;
            _fadingIn = true;
            if (_fadeGain <= 0f) _fadeGain = 0f;
        }

        _fillWakeup.Set();
    }

    /// <inheritdoc/>
    public void Stop()
    {
        if (_hWaveOut == 0) return;

        lock (_stateLock)
        {
            if (!_gateOpen && !_fadingIn) return;
            _fadingIn = false;
            _fadingOut = true;
        }
    }

    /// <inheritdoc/>
    public void Flush()
    {
        if (_provider == null || _disposed) return;

        lock (_stateLock)
        {
            _fillActive = false;
            _gateOpen = false;
            _fadingIn = false;
            _fadingOut = false;
            _fadeGain = 0f;
        }

        Interlocked.Increment(ref _flushGeneration);
        _provider.ClearBuffer();
        Volatile.Write(ref _consecutiveUnderrunCount, 0);
    }

    private void FillBufferLoop(CancellationToken ct)
    {
        int lastGeneration = Volatile.Read(ref _flushGeneration);

        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                var provider = _provider;
                var callback = _callback;
                var floatBuf = _floatBuffer;
                var byteBuf = _byteBuffer;

                if (provider == null || callback == null || floatBuf == null || byteBuf == null)
                {
                    Thread.Sleep(IdleSleepMs);
                    continue;
                }

                int currentGeneration = Volatile.Read(ref _flushGeneration);
                if (currentGeneration != lastGeneration)
                {
                    lastGeneration = currentGeneration;
                    _fadingIn = false;
                    _fadingOut = false;
                    _fadeGain = 0f;
                    Thread.Sleep(PostFlushSleepMs);
                    continue;
                }

                if (!_fillActive)
                {
                    _fillWakeup.Reset();
                    _fillWakeup.Wait(FillWakeupTimeoutMs, ct);
                    continue;
                }

                if (_gateOpen && !_deviceLost)
                {
                    _fillLoopIterations++;
                    if (_fillLoopIterations >= DeviceHealthCheckInterval)
                    {
                        _fillLoopIterations = 0;
                        CheckDeviceHealth();
                    }
                }

                if (!_gateOpen)
                {
                    _fillWakeup.Reset();
                    _fillWakeup.Wait(FillWakeupTimeoutMs, ct);
                    continue;
                }

                if (provider.BufferedDuration.TotalSeconds > InternalBufferSeconds * BufferHighWaterMark)
                {
                    Thread.Sleep(IdleSleepMs);
                    continue;
                }

                int framesRead = callback(floatBuf);

                int generationAfterRead = Volatile.Read(ref _flushGeneration);
                if (generationAfterRead != lastGeneration)
                {
                    lastGeneration = generationAfterRead;
                    _fadingIn = false;
                    _fadingOut = false;
                    _fadeGain = 0f;
                    continue;
                }

                if (framesRead <= 0)
                {
                    int underruns = Interlocked.Increment(ref _consecutiveUnderrunCount);

                    if (underruns == UnderrunLogThreshold)
                    {
                        Log.Warn($"[NAudioBackend] ⚠ {underruns} underruns. BufferedMs={(int)provider.BufferedDuration.TotalMilliseconds}");
                    }

                    if (underruns == StarvationThreshold)
                    {
                        Log.Error($"[NAudioBackend] Starvation detected: {underruns} consecutive underruns");
                        var cb = _onStarvation;
                        if (cb != null) Task.Run(cb, ct);
                    }

                    Thread.Sleep(EmptyCallbackSleepMs);
                    continue;
                }

                Volatile.Write(ref _consecutiveUnderrunCount, 0);

                bool fadeOutDone = ApplyFadeEnvelope(floatBuf, framesRead);

                int bytes = framesRead * _channels * sizeof(float);
                Buffer.BlockCopy(floatBuf, 0, byteBuf, 0, bytes);

                try
                {
                    provider.AddSamples(byteBuf, 0, bytes);
                }
                catch (Exception ex)
                {
                    Log.Warn($"[NAudioBackend] AddSamples failed: {ex.Message}");
                    Thread.Sleep(EmptyCallbackSleepMs);
                    continue;
                }

                if (fadeOutDone)
                {
                    lock (_stateLock)
                    {
                        _gateOpen = false;
                        _fadingOut = false;
                        _fadeGain = 0f;
                    }
                    provider.ClearBuffer();
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Log.Error($"[NAudioBackend] Fill loop error: {ex.Message}");
                Thread.Sleep(ErrorSleepMs);
            }
        }
    }

    private void CheckDeviceHealth()
    {
        if (_hWaveOut == 0 || _disposed) return;

        try
        {
            if (!_playbackRunning && _gateOpen && !_fadingOut)
            {
                _deviceLost = true;
                Log.Error("[NAudioBackend] Device lost during playback (playback thread stopped)");

                lock (_stateLock)
                {
                    _gateOpen = false;
                    _fillActive = false;
                }

                StartDeviceWatcher();

                var cb = _onDeviceLost;
                if (cb != null) Task.Run(cb);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[NAudioBackend] Health check error: {ex.Message}");
        }
    }

    private bool ApplyFadeEnvelope(float[] buffer, int frames)
    {
        if (!_fadingIn && !_fadingOut) return false;

        float gain = _fadeGain;
        float step = 1.0f / FadeFrames;

        for (int frame = 0; frame < frames; frame++)
        {
            if (_fadingIn)
            {
                gain = MathF.Min(1f, gain + step);
                if (gain >= 1f)
                {
                    _fadingIn = false;
                    _fadeGain = 1f;
                    break;
                }
            }
            else
            {
                gain = MathF.Max(0f, gain - step);
                if (gain <= 0f)
                {
                    int remainingSamples = (frames - frame) * _channels;
                    Array.Clear(buffer, frame * _channels, remainingSamples);
                    _fadeGain = 0f;
                    return true;
                }
            }

            for (int ch = 0; ch < _channels; ch++)
            {
                buffer[frame * _channels + ch] *= gain;
            }
        }

        _fadeGain = gain;
        return false;
    }

    #endregion

    #region Helpers & Watcher

    private void StartDeviceWatcher()
    {
        if (_disposed) return;
        StopDeviceWatcher();

        var timer = new Timer(OnDeviceWatchTick, null, DeviceWatchIntervalMs, DeviceWatchIntervalMs);
        var existing = Interlocked.Exchange(ref _deviceWatchTimer, timer);
        existing?.Dispose();

        if (_disposed)
            Interlocked.Exchange(ref _deviceWatchTimer, null)?.Dispose();
    }

    private void StopDeviceWatcher() =>
        Interlocked.Exchange(ref _deviceWatchTimer, null)?.Dispose();

    private void OnDeviceWatchTick(object? state)
    {
        if (_disposed || !_deviceLost)
        {
            StopDeviceWatcher();
            return;
        }

        try
        {
            int deviceCount = waveOutGetNumDevs();
            if (deviceCount > 0)
            {
                StopDeviceWatcher();
                Log.Info($"[NAudioBackend] Audio device detected ({deviceCount} available) — triggering auto-recovery");

                var cb = _onDeviceAvailable;
                if (cb != null) Task.Run(cb);
            }
        }
        catch { }
    }

    private void AllocateBuffers(int sampleRate, int channels)
    {
        int samplesPerRead = sampleRate * channels / ChunkDivisor;
        _floatBuffer = new float[samplesPerRead];
        _byteBuffer = new byte[samplesPerRead * sizeof(float)];
    }

    private void StartFillThread()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _fillThread = new Thread(() => FillBufferLoop(token))
        {
            Name = "AudioFillBuffer",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _fillThread.Start();
    }

    private void StopFillThread()
    {
        _cts?.Cancel();
        _fillWakeup.Set();

        if (_fillThread is { IsAlive: true })
            _fillThread.Join(FillThreadJoinTimeoutMs);

        _cts?.Dispose();
        _cts = null;
        _fillThread = null;
    }

    private static string GetDeviceErrorMessage() =>
        LocalizationService.Instance.Get(
            "Error_NoAudioDevice",
            "Audio output device is not available. Please connect headphones or speakers.");

    /// <inheritdoc/>
    public void SetVolumeGain(float gain) => _gainProvider?.SetVolumeGain(gain);

    #endregion

    #region Dispose

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _fillActive = false;
        _gateOpen = false;

        StopDeviceWatcher();
        StopFillThread();
        DisposeWaveOutSafe();

        _floatBuffer = null;
        _byteBuffer = null;
        _onDeviceAvailable = null;
        _onDeviceLost = null;
        _onStarvation = null;

        _fillWakeup.Dispose();
        Log.Debug("[NAudioBackend] Disposed");
    }

    #endregion
}