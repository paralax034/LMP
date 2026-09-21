using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Exceptions;

namespace LMP.Core.Audio.Backends;

/// <summary>
/// Аппаратный бэкенд вывода аудио на базе Windows Multimedia API (WinMM).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WinAudioBackend : IPlaybackBackend
{
    #region WinMM Native Structs & LibraryImports

    private const int MMSYSERR_NOERROR = 0;
    private const uint WAVE_MAPPER = unchecked((uint)-1);
    private const uint CALLBACK_EVENT = 0x00050000;
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

    #endregion

    #region Constants

    private const int DesiredLatencyMs = 300;
    private const int NumberOfBuffers = 3;
    private const int PlaybackThreadJoinTimeoutMs = 500;
    private const int FadeFrames = 2400;
    private const int UnderrunLogThreshold = 50;
    private const int DeviceHealthCheckInterval = 50;
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
    private int _bufferFloatCount;

    private AudioDataCallback? _callback;
    private GainProcessor? _gainProcessor;

    private int _channels;
    private int _sampleRate;

    private volatile bool _gateOpen;
    private volatile bool _deviceLost;

    private Action? _onDeviceAvailable;
    private Timer? _deviceWatchTimer;
    private volatile bool _disposed;

    private readonly Lock _stateLock = new();

    private int _consecutiveUnderrunCount;
    private int _playbackLoopIterations;
    private Action? _onDeviceLost;

    private float _fadeGain;
    private volatile bool _fadingIn;
    private volatile bool _fadingOut;

    private Action? _onStarvation;

    #endregion

    #region Properties

    /// <inheritdoc/>
    public string Name => "WinAudio-AOT";

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
                    Log.Warn($"[WinAudioBackend] waveOutSetVolume failed (code {res})");
                }
            }
        }
    } = 1.0f;

    /// <inheritdoc/>
    public bool IsPlaying => _gateOpen && !_fadingOut;

    /// <inheritdoc/>
    public bool IsDeviceLost => _deviceLost;

    /// <inheritdoc/>
    public int BufferedSamples => 0;

    /// <inheritdoc/>
    public int BufferedBytes => 0;

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
                Log.Info($"[WinAudioBackend] Device recovery attempt {attempt + 1}/{maxAttempts + 1}, waiting {delay}ms for endpoint stabilization");
                Thread.Sleep(delay);
            }

            StopPlaybackThread();
            DisposeWaveOutSafe();
            Thread.Sleep(PostDisposeSettleMs);

            try
            {
                CreateWaveOut(sampleRate, channels);
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"[WinAudioBackend] CreateWaveOut attempt {attempt + 1} failed: {ex.Message}");

                if (attempt >= maxAttempts)
                {
                    _deviceLost = true;
                    DisposeWaveOutSafe();
                    StartDeviceWatcher();
                    Log.Error($"[WinAudioBackend] Failed to open audio device after {attempt + 1} attempts: {ex.Message}");
                    throw new AudioDeviceException(GetDeviceErrorMessage(), ex);
                }
            }
        }

        _gainProcessor = new GainProcessor();
        StartPlaybackThread();

        _deviceLost = false;
        StopDeviceWatcher();
        Log.Info($"[WinAudioBackend] Initialized WinMM: {sampleRate}Hz, {channels}ch");
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
            _gateOpen = false;
            _fadingIn = false;
            _fadingOut = false;
            _fadeGain = 0f;
        }

        Volatile.Write(ref _consecutiveUnderrunCount, 0);

        if (sampleRate == _sampleRate && channels == _channels)
        {
            Log.Info($"[WinAudioBackend] Reinit fast path: {sampleRate}Hz, {channels}ch");
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
        _bufferFloatCount = _bufferByteSize / sizeof(float);

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
            Name = "WinAudioPlayback",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };
        _playbackThread.Start();
    }

    /// <summary>
    /// Основной цикл воспроизведения.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Читает PCM float-данные напрямую из <see cref="_callback"/> без промежуточных managed-буферов.
    /// Применяет fade envelope и volume gain in-place перед единственным <see cref="Marshal.Copy"/>
    /// в нативный <c>WAVEHDR.lpData</c>.
    /// </para>
    /// <para>
    /// При ошибке <see cref="waveOutWrite"/> (например, MMSYSERR_INVALHANDLE = 6 при смене устройства)
    /// немедленно переводит бэкенд в состояние DeviceLost, закрывает gate и оповещает плеер.
    /// </para>
    /// </remarks>
    private unsafe void NativePlaybackLoop()
    {
        float[] floatBuffer = new float[_bufferFloatCount];
        uint headerSize = (uint)sizeof(WAVEHDR);

        while (_playbackRunning && !_disposed)
        {
            try
            {
                if (_hWaveOut == 0 || _headers == null || _bufferPointers == null)
                    break;

                bool wroteAny = false;

                for (int i = 0; i < NumberOfBuffers; i++)
                {
                    WAVEHDR* hdr = _headers[i];
                    if ((hdr->dwFlags & WHDR_INQUEUE) != 0)
                        continue;

                    int framesRead = 0;

                    if (_gateOpen)
                    {
                        _playbackLoopIterations++;
                        if (_playbackLoopIterations >= DeviceHealthCheckInterval)
                        {
                            _playbackLoopIterations = 0;
                            CheckDeviceHealth();
                        }

                        var cb = _callback;
                        if (cb != null)
                            framesRead = cb(floatBuffer.AsSpan(0, _bufferFloatCount));

                        if (framesRead <= 0)
                        {
                            int underruns = Interlocked.Increment(ref _consecutiveUnderrunCount);

                            if (underruns == UnderrunLogThreshold)
                                Log.Warn($"[WinAudioBackend] ⚠ {underruns} underruns");

                            if (underruns == StarvationThreshold)
                            {
                                Log.Error($"[WinAudioBackend] Starvation detected: {underruns} consecutive underruns");
                                var starvationCb = _onStarvation;
                                if (starvationCb != null) Task.Run(starvationCb);
                            }

                            Array.Clear(floatBuffer, 0, _bufferFloatCount);
                        }
                        else
                        {
                            Volatile.Write(ref _consecutiveUnderrunCount, 0);

                            bool fadeOutDone = ApplyFadeEnvelope(floatBuffer, framesRead);

                            _gainProcessor?.Process(floatBuffer.AsSpan(0, framesRead * _channels));

                            if (fadeOutDone)
                            {
                                lock (_stateLock)
                                {
                                    _gateOpen = false;
                                    _fadingOut = false;
                                    _fadeGain = 0f;
                                }
                                Array.Clear(floatBuffer, framesRead * _channels,
                                    _bufferFloatCount - framesRead * _channels);
                            }
                            else if (framesRead * _channels < _bufferFloatCount)
                            {
                                Array.Clear(floatBuffer, framesRead * _channels,
                                    _bufferFloatCount - framesRead * _channels);
                            }
                        }
                    }
                    else
                    {
                        Array.Clear(floatBuffer, 0, _bufferFloatCount);
                    }

                    Marshal.Copy(floatBuffer, 0, _bufferPointers[i], _bufferFloatCount);

                    int res = waveOutWrite(_hWaveOut, (nint)hdr, headerSize);
                    if (res != MMSYSERR_NOERROR)
                    {
                        Log.Error($"[WinAudioBackend] waveOutWrite failed: code {res}");
                        _playbackRunning = false;

                        lock (_stateLock)
                        {
                            _gateOpen = false;
                        }

                        _deviceLost = true;
                        StartDeviceWatcher();

                        var lostCb = _onDeviceLost;
                        if (lostCb != null) Task.Run(lostCb);

                        break;
                    }

                    wroteAny = true;
                }

                if (!wroteAny)
                    _waveCallbackEvent?.WaitOne(DesiredLatencyMs / NumberOfBuffers);
            }
            catch (Exception ex)
            {
                Log.Error($"[WinAudioBackend] Playback thread exception: {ex.Message}");
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
            _playbackThread.Join(PlaybackThreadJoinTimeoutMs);

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
            _gateOpen = false;
            _fadingIn = false;
            _fadingOut = false;
            _fadeGain = 0f;
        }
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

            _gateOpen = true;
            _fadingOut = false;
            _fadingIn = true;
            if (_fadeGain <= 0f) _fadeGain = 0f;
        }
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
        if (_disposed) return;

        lock (_stateLock)
        {
            _gateOpen = false;
            _fadingIn = false;
            _fadingOut = false;
            _fadeGain = 0f;
        }

        Volatile.Write(ref _consecutiveUnderrunCount, 0);
    }

    private void CheckDeviceHealth()
    {
        if (_hWaveOut == 0 || _disposed) return;

        try
        {
            if (!_playbackRunning && _gateOpen && !_fadingOut)
            {
                _deviceLost = true;
                Log.Error("[WinAudioBackend] Device lost during playback (playback thread stopped)");

                lock (_stateLock)
                {
                    _gateOpen = false;
                }

                StartDeviceWatcher();

                var cb = _onDeviceLost;
                if (cb != null) Task.Run(cb);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[WinAudioBackend] Health check error: {ex.Message}");
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

    private const uint WAVE_FORMAT_QUERY = 0x00000001;

    /// <summary>
    /// Периодический опрос готовности дефолтного аудио-устройства после потери хэндла.
    /// </summary>
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
                var wfx = new WAVEFORMATEX
                {
                    wFormatTag = 0x0003, // WAVE_FORMAT_IEEE_FLOAT
                    nChannels = (ushort)(_channels > 0 ? _channels : 2),
                    nSamplesPerSec = (uint)(_sampleRate > 0 ? _sampleRate : 48000),
                    nAvgBytesPerSec = (uint)((_sampleRate > 0 ? _sampleRate : 48000) * (_channels > 0 ? _channels : 2) * sizeof(float)),
                    nBlockAlign = (ushort)((_channels > 0 ? _channels : 2) * sizeof(float)),
                    wBitsPerSample = 32,
                    cbSize = 0
                };

                int queryRes = waveOutOpen(out _, WAVE_MAPPER, in wfx, 0, 0, WAVE_FORMAT_QUERY);
                if (queryRes == MMSYSERR_NOERROR)
                {
                    StopDeviceWatcher();
                    Log.Info($"[WinAudioBackend] Audio endpoint verified available via query — triggering auto-recovery");

                    var cb = _onDeviceAvailable;
                    if (cb != null) Task.Run(cb);
                }
            }
        }
        catch { }
    }

    private void StopPlaybackThread()
    {
        _playbackRunning = false;
        _waveCallbackEvent?.Set();

        if (_playbackThread is { IsAlive: true })
            _playbackThread.Join(PlaybackThreadJoinTimeoutMs);

        _playbackThread = null;
    }

    private static string GetDeviceErrorMessage() =>
        LocalizationService.Instance.Get(
            "Error_NoAudioDevice",
            "Audio output device is not available. Please connect headphones or speakers.");

    /// <inheritdoc/>
    public void SetVolumeGain(float gain) => _gainProcessor?.SetVolumeGain(gain);

    #endregion

    #region Dispose

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _gateOpen = false;

        StopDeviceWatcher();
        StopPlaybackThread();
        DisposeWaveOutSafe();

        _gainProcessor = null;
        _onDeviceAvailable = null;
        _onDeviceLost = null;
        _onStarvation = null;

        Log.Debug("[WinAudioBackend] Disposed");
    }

    #endregion
}