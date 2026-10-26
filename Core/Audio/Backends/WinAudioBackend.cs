using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Exceptions;

namespace LMP.Core.Audio.Backends;

/// <summary>
/// Аппаратный бэкенд вывода аудио на базе Windows Audio Session API (WASAPI) в Shared Paced Zero-Copy режиме.
/// </summary>
/// <remarks>
/// <para><b>Архитектурные инварианты:</b></para>
/// <list type="bullet">
///   <item><b>Strict Thread Affinity (Single MTA Thread):</b> Все COM-интерфейсы (<c>IMMDeviceEnumerator</c>, <c>IAudioClient</c>, <c>IAudioRenderClient</c>) создаются, используются и освобождаются строго внутри одного выделенного потока <c>WASAPI-ProAudio</c>.</item>
///   <item><b>Zero-Alloc COM Interop:</b> Прямые вызовы таблиц виртуальных методов (Raw VTable + <c>delegate* unmanaged[MemberFunction]</c>) без создания RCW/CCW обёрток и <c>COMException</c>.</item>
///   <item><b>Zero-Copy Rendering:</b> Запись PCM-сэмплов из <see cref="AudioPipeline"/> и <see cref="GainProcessor"/> напрямую в память аудио-движка ОС (<c>IAudioRenderClient::GetBuffer</c> → <see cref="Span{T}"/>).</item>
///   <item><b>Dynamic Latency Horizon (~30 CS/sec steady, ~8ms interactive):</b> В стационарном режиме буфер заполняется на 60 мс с шагом сна 30 мс. При активном вращении громкости или Seek горизонт сжимается до 24 мс с мгновенным пробуждением по <see cref="_wakeControlEvent"/>.</item>
///   <item><b>Seamless Seek Continuity:</b> При быстром Seek не проваливает громкость в ноль, позволяя <see cref="AudioPipeline"/> выполнить 20-миллисекундный wave-to-wave кроссфейд поверх живого потока.</item>
///   <item><b>Seamless Endpoint Routing:</b> Прозрачное переоткрытие дефолтного аудио-устройства при переключении наушников/динамиков без потери декодированных фреймов в кольцевом буфере.</item>
/// </list>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class WinAudioBackend : IPlaybackBackend
{
    #region WASAPI Constants & HRESULTs

    private const uint COINIT_MULTITHREADED = 0x0;
    private const uint CLSCTX_ALL = 0x17;

    private const int EDataFlow_eRender = 0;
    private const int ERole_eMultimedia = 1;

    private const int AUDCLNT_SHAREMODE_SHARED = 0;
    private const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    private const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;

    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    private const int AUDCLNT_E_NOT_INITIALIZED = unchecked((int)0x88890001);
    private const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    private const int AUDCLNT_E_ENDPOINT_CREATE_FAILED = unchecked((int)0x8889000F);
    private const int AUDCLNT_E_SERVICE_NOT_RUNNING = unchecked((int)0x88890010);

    private const ushort WAVE_FORMAT_IEEE_FLOAT = 0x0003;
    private const uint INFINITE = 0xFFFFFFFF;
    private const long HnsPerMillisecond = 10_000;

    #endregion

    #region Engine Tuning Constants

    /// <summary>
    /// Ёмкость аппаратного кольцевого буфера WASAPI (100 мс).
    /// </summary>
    private const int BufferDurationMs = 100;

    /// <summary>Горизонт заполнения буфера в стационарном режиме (75 мс) — гарантирует запас > 40 мс даже при 2 квантах сна планировщика Windows (31.25 мс).</summary>
    private const int SteadyTargetBufferMs = 75;

    /// <summary>Шаг сна потока WASAPI в стационарном режиме (20 мс) — полностью исключает просадку padBefore в 0.</summary>
    private const uint SteadySleepMs = 20;

    /// <summary>Сжатый горизонт заполнения буфера при активном вращении громкости или микро-фейде (40 мс — исключает осушение буфера до 0 при микро-задержках ОС).</summary>
    private const int InteractiveTargetBufferMs = 40;

    /// <summary>Шаг сна потока WASAPI в интерактивном режиме (8 мс).</summary>
    private const uint InteractiveSleepMs = 8;

    /// <summary>Время удержания интерактивного режима с низкой задержкой после изменения громкости или Seek (мс).</summary>
    private const long InteractiveHoldDurationMs = 350;

    /// <summary>Таймаут ожидания завершения потока воспроизведения при остановке (мс).</summary>
    private const int PlaybackThreadJoinTimeoutMs = 1000;

    /// <summary>Длительность плавного микро-фейда при Play/Pause (960 фреймов = 20 мс при 48 кГц).</summary>
    private const int FadeFrames = 960;

    /// <summary>Порог подряд идущих пустых тиков для предупреждения в лог.</summary>
    private const int UnderrunLogThreshold = 50;

    /// <summary>Порог подряд идущих пустых тиков (~1.5 сек) для сигнала Starvation.</summary>
    private const int StarvationThreshold = 150;

    /// <summary>Порог тиков в закрытом гейте перед остановкой аппаратного потока WASAPI (~300 мс).</summary>
    private const int IdleTicksBeforeHardwareStop = 15;

    /// <summary>Количество быстрых попыток бесшовного переключения на новое дефолтное устройство.</summary>
    private const int HotSwapMaxRetries = 3;

    /// <summary>Пауза между попытками бесшовного хот-свапа (мс).</summary>
    private const int HotSwapRetryDelayMs = 60;

    /// <summary>Задержка при полном восстановлении после потери всех устройств (мс).</summary>
    private const int DeviceRecoveryDelayMs = 250;

    /// <summary>Максимум попыток при полном пересоздании устройства.</summary>
    private const int DeviceRecoveryMaxRetries = 3;

    /// <summary>Интервал опроса готовности аудио-подсистемы после полного отключения устройств (мс).</summary>
    private const int DeviceWatchIntervalMs = 1500;

    #endregion

    #region Nested Types

    /// <summary>
    /// Состояние огибающей микро-фейда при переходах Play / Pause.
    /// </summary>
    internal enum FadeState : byte
    {
        None = 0,
        FadingIn = 1,
        FadingOut = 2
    }

    #endregion

    #region Static COM GUIDs

    private static readonly Guid CLSID_MMDeviceEnumerator = new(0xBCDE0395, 0xE52F, 0x467C, 0x8E, 0x3D, 0xC4, 0x57, 0x92, 0x91, 0x69, 0x2E);
    private static readonly Guid IID_IMMDeviceEnumerator = new(0xA95664D2, 0x9614, 0x4F35, 0xA7, 0x46, 0xDE, 0x8D, 0xB6, 0x36, 0x17, 0xE6);
    private static readonly Guid IID_IAudioClient = new(0x1CB9AD4C, 0xDBFA, 0x4C32, 0xB1, 0x78, 0xC2, 0xF5, 0x68, 0xA7, 0x03, 0xB2);
    private static readonly Guid IID_IAudioRenderClient = new(0xF294ACFC, 0x3146, 0x4483, 0xA7, 0xBF, 0xAD, 0xDC, 0xA7, 0xC2, 0x60, 0xE2);

    #endregion

    #region Native Structs & LibraryImports

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

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint pvReserved, uint dwCoInit);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid rclsid,
        nint pUnkOuter,
        uint dwClsContext,
        in Guid riid,
        out void* ppv);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateEventW(nint lpEventAttributes, int bManualReset, int bInitialState, nint lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int SetEvent(nint hEvent);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int CloseHandle(nint hObject);

    [LibraryImport("avrt.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint AvSetMmThreadCharacteristicsW(string taskName, out uint taskIndex);

    [LibraryImport("avrt.dll")]
    private static partial int AvRevertMmThreadCharacteristics(nint avrtHandle);

    #endregion

    #region Zero-Alloc Raw VTable Wrappers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SafeRelease(ref void* comPtr)
    {
        void* p = comPtr;
        if (p != null)
        {
            comPtr = null;
            void** vtbl = *(void***)p;
            ((delegate* unmanaged[MemberFunction]<void*, uint>)vtbl[2])(p);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int EnumeratorGetDefaultEndpoint(void* enumerator, int dataFlow, int role, out void* endpoint)
    {
        void* pEndpoint = null;
        void** vtbl = *(void***)enumerator;
        int hr = ((delegate* unmanaged[MemberFunction]<void*, int, int, void**, int>)vtbl[4])(
            enumerator, dataFlow, role, &pEndpoint);
        endpoint = pEndpoint;
        return hr;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DeviceActivate(void* device, in Guid iid, uint clsCtx, out void* iface)
    {
        void* pIface = null;
        void** vtbl = *(void***)device;
        fixed (Guid* pIid = &iid)
        {
            int hr = ((delegate* unmanaged[MemberFunction]<void*, Guid*, uint, nint, void**, int>)vtbl[3])(
                device, pIid, clsCtx, 0, &pIface);
            iface = pIface;
            return hr;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AudioClientInitialize(
        void* audioClient,
        int shareMode,
        uint streamFlags,
        long hnsBufferDuration,
        long hnsPeriodicity,
        in WAVEFORMATEX format)
    {
        void** vtbl = *(void***)audioClient;
        fixed (WAVEFORMATEX* pFormat = &format)
        {
            return ((delegate* unmanaged[MemberFunction]<void*, int, uint, long, long, WAVEFORMATEX*, Guid*, int>)vtbl[3])(
                audioClient, shareMode, streamFlags, hnsBufferDuration, hnsPeriodicity, pFormat, null);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AudioClientGetBufferSize(void* audioClient, out uint bufferFrameCount)
    {
        uint count = 0;
        void** vtbl = *(void***)audioClient;
        int hr = ((delegate* unmanaged[MemberFunction]<void*, uint*, int>)vtbl[4])(audioClient, &count);
        bufferFrameCount = count;
        return hr;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AudioClientGetCurrentPadding(void* audioClient, out uint numPaddingFrames)
    {
        uint padding = 0;
        void** vtbl = *(void***)audioClient;
        int hr = ((delegate* unmanaged[MemberFunction]<void*, uint*, int>)vtbl[6])(audioClient, &padding);
        numPaddingFrames = padding;
        return hr;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AudioClientStart(void* audioClient)
    {
        void** vtbl = *(void***)audioClient;
        return ((delegate* unmanaged[MemberFunction]<void*, int>)vtbl[10])(audioClient);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AudioClientStop(void* audioClient)
    {
        void** vtbl = *(void***)audioClient;
        return ((delegate* unmanaged[MemberFunction]<void*, int>)vtbl[11])(audioClient);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AudioClientReset(void* audioClient)
    {
        void** vtbl = *(void***)audioClient;
        return ((delegate* unmanaged[MemberFunction]<void*, int>)vtbl[12])(audioClient);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AudioClientGetService(void* audioClient, in Guid riid, out void* ppv)
    {
        void* pService = null;
        void** vtbl = *(void***)audioClient;
        fixed (Guid* pRiid = &riid)
        {
            int hr = ((delegate* unmanaged[MemberFunction]<void*, Guid*, void**, int>)vtbl[14])(
                audioClient, pRiid, &pService);
            ppv = pService;
            return hr;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RenderClientGetBuffer(void* renderClient, uint numFramesRequested, out byte* ppData)
    {
        byte* pData = null;
        void** vtbl = *(void***)renderClient;
        int hr = ((delegate* unmanaged[MemberFunction]<void*, uint, byte**, int>)vtbl[3])(
            renderClient, numFramesRequested, &pData);
        ppData = pData;
        return hr;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RenderClientReleaseBuffer(void* renderClient, uint numFramesWritten, uint dwFlags)
    {
        void** vtbl = *(void***)renderClient;
        return ((delegate* unmanaged[MemberFunction]<void*, uint, uint, int>)vtbl[4])(
            renderClient, numFramesWritten, dwFlags);
    }

    #endregion

    #region Fields

    // COM-указатели принадлежат ИСКЛЮЧИТЕЛЬНО потоку WasapiPlaybackLoop
    private void* _deviceEnumerator;
    private void* _device;
    private void* _audioClient;
    private void* _renderClient;

    private nint _wakeControlEvent;
    private uint _bufferFrameCount;

    private Thread? _playbackThread;
    private readonly ManualResetEventSlim _initReadyEvent = new(false);
    private Exception? _initException;

    private volatile bool _playbackRunning;
    private volatile bool _sessionInitialized;

    private AudioDataCallback? _callback;
    private readonly GainProcessor _gainProcessor = new();

    private int _channels;
    private int _sampleRate;
    private int _sourceSampleRate;
    private volatile int _bufferedSamples;

    // Состояние 4-точечного C1 кубического ресемплера Эрмита (для бесшовного воспроизведения 44.1 кГц в 48 кГц сессии WASAPI без пересоздания IAudioClient)
    private readonly float[] _resampleSourceBuf = new float[4096 * 8];
    private readonly float[] _resampleHistory = new float[4 * 8]; // 4 фрейма истории [y_{-1}, y_0, y_1, y_2]
    private int _resampleSourceValidFrames;
    private int _resampleSourceCursor;
    private double _resampleFracPhase;
    private bool _resampleHistoryPrimed;

    private volatile bool _gateOpen;
    private volatile bool _deviceLost;
    private volatile bool _disposed;

    /// <summary>
    /// Защищает переходы состояния гейта И синхронизирует вызов <c>_callback</c> с <see cref="Flush"/>,
    /// гарантируя, что <c>LockFreeRingBuffer.Clear()</c> в <c>AudioPipeline.Flush()</c> никогда не вызовется
    /// параллельно с <c>LockFreeRingBuffer.Read()</c> в потоке WASAPI.
    /// </summary>
    private readonly Lock _stateLock = new();
    private FadeState _fadeState;
    private float _fadeGain;
    private bool _isFlushSeamless;
    private float _lastWasapiL;
    private float _lastWasapiR;
    private float _prevWasapiL;
    private float _prevWasapiR;

    private const int CrossfadeFrames = 576; // 12 мс @ 48 кГц
    private readonly float[] _crossfadeTail = new float[CrossfadeFrames * 8];
    private int _crossfadeTailFrames;

    private long _interactiveUntilTick64;

    private int _consecutiveUnderrunCount;

    private Action? _onDeviceLost;
    private Action? _onDeviceAvailable;
    private Action? _onStarvation;
    private Timer? _deviceWatchTimer;

    #endregion

    #region Properties

    /// <inheritdoc/>
    public string Name => "WASAPI-ZeroCopy";

    /// <inheritdoc/>
    public float Volume
    {
        get;
        set => field = Math.Clamp(value, 0f, 1f);
    } = 1.0f;

    /// <inheritdoc/>
    public bool IsPlaying
    {
        get
        {
            lock (_stateLock)
            {
                return _gateOpen && _fadeState != FadeState.FadingOut;
            }
        }
    }

    /// <inheritdoc/>
    public bool IsDeviceLost => _deviceLost;

    /// <inheritdoc/>
    public int BufferedSamples
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (!_gateOpen) return 0;
            int raw = _bufferedSamples;
            int srcRate = _sourceSampleRate;
            int dstRate = _sampleRate;
            return (srcRate > 0 && dstRate > 0 && srcRate != dstRate)
                ? (int)((long)raw * srcRate / dstRate)
                : raw;
        }
    }

    /// <inheritdoc/>
    public int BufferedBytes => BufferedSamples * sizeof(float);

    #endregion

    #region Initialize / Reinitialize

    /// <inheritdoc/>
    public void Initialize(int sampleRate, int channels, AudioDataCallback dataCallback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(dataCallback);

        _callback = dataCallback;
        _channels = channels > 0 ? channels : AudioConstants.DefaultChannels;
        _sourceSampleRate = sampleRate > 0 ? sampleRate : AudioConstants.DefaultSampleRate;
        // Фиксируем аппаратную сессию WASAPI на канонической частоте 48000 Гц,
        // чтобы переключение между треками 44.1 кГц (AAC) и 48 кГц (Opus) никогда не останавливало IAudioClient!
        _sampleRate = AudioConstants.DefaultSampleRate;
        ResetResamplerStateUnderLock();

        bool wasDeviceLost = _deviceLost;
        int maxAttempts = wasDeviceLost ? DeviceRecoveryMaxRetries : 0;

        for (int attempt = 0; attempt <= maxAttempts; attempt++)
        {
            if (wasDeviceLost && attempt > 0)
            {
                int delay = DeviceRecoveryDelayMs * attempt;
                Log.Info($"[WinAudioBackend] WASAPI recovery attempt {attempt + 1}/{maxAttempts + 1}, waiting {delay}ms");
                Thread.Sleep(delay);
            }

            StopPlaybackThread();

            try
            {
                StartPlaybackThreadAndWaitInit();
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"[WinAudioBackend] WASAPI init attempt {attempt + 1} failed: {ex.Message}");
                StopPlaybackThread();

                if (attempt >= maxAttempts)
                {
                    _deviceLost = true;
                    StartDeviceWatcher();
                    Log.Error($"[WinAudioBackend] Failed to initialize WASAPI after {attempt + 1} attempts: {ex.Message}");
                    throw new AudioDeviceException("Error_NoAudioDevice", ex);
                }
            }
        }

        _deviceLost = false;
        StopDeviceWatcher();
        Log.Info($"[WinAudioBackend] Initialized WASAPI Shared Paced Zero-Copy: engine={_sampleRate}Hz (source={_sourceSampleRate}Hz), {_channels}ch, buffer={_bufferFrameCount} frames (~{_bufferFrameCount * 1000 / Math.Max(1, _sampleRate)}ms)");
    }

    /// <inheritdoc/>
    public void Reinitialize(int sampleRate, int channels, AudioDataCallback dataCallback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(dataCallback);

        int normalizedSourceRate = sampleRate > 0 ? sampleRate : AudioConstants.DefaultSampleRate;
        int normalizedCh = channels > 0 ? channels : AudioConstants.DefaultChannels;

        if (!_sessionInitialized || _deviceLost || !_playbackRunning)
        {
            Initialize(normalizedSourceRate, normalizedCh, dataCallback);
            return;
        }

        lock (_stateLock)
        {
            // Если Flush() ещё не захватил хвост уходящего трека, захватываем его со старым _sourceSampleRate до подмены _callback!
            if (_crossfadeTailFrames == 0 && (_gateOpen || MathF.Abs(_lastWasapiL) > 0.0001f || MathF.Abs(_lastWasapiR) > 0.0001f))
            {
                CaptureCrossfadeTailUnderLock();
            }

            _callback = dataCallback;
            _sourceSampleRate = normalizedSourceRate;
            ResetResamplerStateUnderLock();
            _isFlushSeamless = true;
            _gateOpen = false;
            _fadeState = FadeState.None;
            _fadeGain = 0f;
            _bufferedSamples = 0;
        }

        Volatile.Write(ref _consecutiveUnderrunCount, 0);

        if (normalizedCh == _channels)
        {
            Log.Info($"[WinAudioBackend] WASAPI Reinit fast path: source={normalizedSourceRate}Hz -> engine={_sampleRate}Hz, {normalizedCh}ch");
            return;
        }

        Initialize(normalizedSourceRate, normalizedCh, dataCallback);
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

    #region Thread-Affine WASAPI Session Management

    private static WAVEFORMATEX BuildWaveFormat(int sampleRate, int channels)
    {
        ushort ch = (ushort)(channels > 0 ? channels : 2);
        uint rate = (uint)(sampleRate > 0 ? sampleRate : 48000);
        ushort blockAlign = (ushort)(ch * sizeof(float));

        return new WAVEFORMATEX
        {
            wFormatTag = WAVE_FORMAT_IEEE_FLOAT,
            nChannels = ch,
            nSamplesPerSec = rate,
            nAvgBytesPerSec = rate * blockAlign,
            nBlockAlign = blockAlign,
            wBitsPerSample = 32,
            cbSize = 0
        };
    }

    private void StartPlaybackThreadAndWaitInit()
    {
        _initException = null;
        _initReadyEvent.Reset();
        _playbackRunning = true;

        _playbackThread = new Thread(WasapiPlaybackLoop)
        {
            Name = "WASAPI-ProAudio",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };
        _playbackThread.Start();

        _initReadyEvent.Wait();

        if (_initException != null)
            throw _initException;
    }

    private void InitializeEventsAndEndpointOnRenderThread()
    {
        if (_wakeControlEvent == 0)
        {
            _wakeControlEvent = CreateEventW(0, bManualReset: 0, bInitialState: 0, 0);
            if (_wakeControlEvent == 0)
                throw new InvalidOperationException("CreateEventW for control event failed");
        }

        OpenDefaultEndpointOnRenderThread(_sampleRate, _channels);
        _sessionInitialized = true;
    }

    private void OpenDefaultEndpointOnRenderThread(int sampleRate, int channels)
    {
        ReleaseEndpointInterfacesOnRenderThread();

        if (_deviceEnumerator == null)
        {
            int hrEnum = CoCreateInstance(
                in CLSID_MMDeviceEnumerator,
                0,
                CLSCTX_ALL,
                in IID_IMMDeviceEnumerator,
                out _deviceEnumerator);

            if (hrEnum < 0 || _deviceEnumerator == null)
                throw new InvalidOperationException($"CoCreateInstance(MMDeviceEnumerator) failed: 0x{hrEnum:X8}");
        }

        int hr = EnumeratorGetDefaultEndpoint(_deviceEnumerator, EDataFlow_eRender, ERole_eMultimedia, out _device);
        if (hr < 0 || _device == null)
            throw new InvalidOperationException($"GetDefaultAudioEndpoint failed: 0x{hr:X8}");

        hr = DeviceActivate(_device, in IID_IAudioClient, CLSCTX_ALL, out _audioClient);
        if (hr < 0 || _audioClient == null)
            throw new InvalidOperationException($"IMMDevice::Activate(IAudioClient) failed: 0x{hr:X8}");

        var wfx = BuildWaveFormat(sampleRate, channels);
        long hnsBufferDuration = BufferDurationMs * HnsPerMillisecond;

        const uint streamFlags = AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
                               | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;

        hr = AudioClientInitialize(
            _audioClient,
            AUDCLNT_SHAREMODE_SHARED,
            streamFlags,
            hnsBufferDuration,
            0,
            in wfx);

        if (hr < 0)
            throw new InvalidOperationException($"IAudioClient::Initialize failed: 0x{hr:X8}");

        hr = AudioClientGetBufferSize(_audioClient, out _bufferFrameCount);
        if (hr < 0 || _bufferFrameCount == 0)
            throw new InvalidOperationException($"IAudioClient::GetBufferSize failed: 0x{hr:X8}");

        hr = AudioClientGetService(_audioClient, in IID_IAudioRenderClient, out _renderClient);
        if (hr < 0 || _renderClient == null)
            throw new InvalidOperationException($"IAudioClient::GetService(IAudioRenderClient) failed: 0x{hr:X8}");

        _bufferedSamples = 0;
    }

    private bool TryHotSwapEndpointOnRenderThread(ref bool wasapiStarted)
    {
        wasapiStarted = false;

        for (int attempt = 0; attempt < HotSwapMaxRetries; attempt++)
        {
            if (_disposed || !_playbackRunning)
                return false;

            if (attempt > 0)
                Thread.Sleep(HotSwapRetryDelayMs);

            try
            {
                OpenDefaultEndpointOnRenderThread(_sampleRate, _channels);

                bool shouldStart;
                lock (_stateLock)
                {
                    shouldStart = _gateOpen || _fadeState == FadeState.FadingOut;
                }

                if (shouldStart)
                {
                    int hrStart = AudioClientStart(_audioClient);
                    if (hrStart < 0)
                        continue;

                    wasapiStarted = true;
                }

                Log.Info($"[WinAudioBackend] Seamless WASAPI endpoint hot-swap succeeded on attempt {attempt + 1}");
                return true;
            }
            catch
            {
                // Переходное состояние аудио-графа Windows — повторяем попытку
            }
        }

        return false;
    }

    private void ReleaseEndpointInterfacesOnRenderThread()
    {
        SafeRelease(ref _renderClient);
        SafeRelease(ref _audioClient);
        SafeRelease(ref _device);
        _bufferedSamples = 0;
    }

    private void CleanupAllResourcesOnRenderThread(bool wasapiStarted)
    {
        _sessionInitialized = false;

        if (_audioClient != null && wasapiStarted)
        {
            // Перед остановкой аппаратного клиента (например, при смене 44100Hz <-> 48000Hz)
            // плавно сводим последний сэмпл в 0 и даём буферу мягко завершиться без щелчка AudioClientStop!
            if (_renderClient != null && (MathF.Abs(_lastWasapiL) > 0.0005f || MathF.Abs(_lastWasapiR) > 0.0005f))
            {
                if (AudioClientGetCurrentPadding(_audioClient, out uint pad) >= 0)
                {
                    uint avail = _bufferFrameCount > pad ? _bufferFrameCount - pad : 0;
                    uint rampFrames = Math.Min(avail, 256u);
                    if (rampFrames > 0 && RenderClientGetBuffer(_renderClient, rampFrames, out byte* pData) >= 0 && pData != null)
                    {
                        Span<float> span = new((float*)pData, (int)(rampFrames * (uint)_channels));
                        span.Clear();
                        float inv = 1.0f / rampFrames;
                        for (int f = 0; f < (int)rampFrames; f++)
                        {
                            float t = (f + 1) * inv;
                            float w = 1.0f - (t * t * (3.0f - 2.0f * t));
                            int bIdx = f * _channels;
                            span[bIdx] = _lastWasapiL * w;
                            if (_channels >= 2)
                                span[bIdx + 1] = _lastWasapiR * w;
                        }
                        _ = RenderClientReleaseBuffer(_renderClient, rampFrames, 0);
                        int waitMs = Math.Clamp((int)((pad + rampFrames) * 1000 / Math.Max(1, _sampleRate)), 1, 35);
                        Thread.Sleep(waitMs);
                    }
                }
            }

            _ = AudioClientStop(_audioClient);
        }

        _prevWasapiL = 0f;
        _prevWasapiR = 0f;
        _lastWasapiL = 0f;
        _lastWasapiR = 0f;

        ReleaseEndpointInterfacesOnRenderThread();
        SafeRelease(ref _deviceEnumerator);

        if (_wakeControlEvent != 0)
        {
            _ = CloseHandle(_wakeControlEvent);
            _wakeControlEvent = 0;
        }
    }

    #endregion

    #region Zero-Copy WASAPI Render Loop

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ResetResamplerStateUnderLock()
    {
        _resampleSourceValidFrames = 0;
        _resampleSourceCursor = 0;
        _resampleFracPhase = 0.0;
        _resampleHistoryPrimed = false;
        _resampleHistory.AsSpan().Clear();
    }

    /// <summary>
    /// Читает фреймы из <see cref="_callback"/> и при несовпадении частоты источника (например, 44100 Гц AAC)
    /// с частотой движка WASAPI (48000 Гц) выполняет 4-точечную C1 кубическую интерполяцию Эрмита за 0 B аллокаций.
    /// Вызывается строго под <see cref="_stateLock"/>.
    /// </summary>
    private int ReadCallbackWithResamplingUnderLock(Span<float> targetSpan, int requestedOutFrames)
    {
        if (_callback == null || requestedOutFrames <= 0)
            return 0;

        int ch = Math.Clamp(_channels, 1, 8);
        int srcRate = _sourceSampleRate;
        int dstRate = _sampleRate;

        if (srcRate == dstRate || srcRate <= 0 || dstRate <= 0)
        {
            return _callback(targetSpan[..(requestedOutFrames * ch)]);
        }

        double step = (double)srcRate / dstRate;
        int outFrame = 0;
        Span<float> hist = _resampleHistory.AsSpan(0, 4 * ch);
        Span<float> srcBuf = _resampleSourceBuf.AsSpan();

        while (outFrame < requestedOutFrames)
        {
            // Подкачиваем новые входные фреймы в историю [y_{-1}, y_0, y_1, y_2], пока фаза >= 1.0 или история не инициализирована
            while (!_resampleHistoryPrimed || _resampleFracPhase >= 1.0)
            {
                if (_resampleSourceCursor >= _resampleSourceValidFrames)
                {
                    int neededInFrames = Math.Clamp(
                        (int)Math.Ceiling((requestedOutFrames - outFrame) * step) + 4,
                        64,
                        _resampleSourceBuf.Length / ch);

                    int readIn = _callback(srcBuf[..(neededInFrames * ch)]);
                    if (readIn <= 0)
                    {
                        return outFrame;
                    }

                    _resampleSourceValidFrames = readIn;
                    _resampleSourceCursor = 0;
                }

                if (!_resampleHistoryPrimed)
                {
                    int firstIdx = _resampleSourceCursor * ch;
                    for (int c = 0; c < ch; c++)
                    {
                        float s0 = srcBuf[firstIdx + c];
                        hist[c] = s0;
                        hist[ch + c] = s0;
                        hist[2 * ch + c] = s0;
                        hist[3 * ch + c] = s0;
                    }

                    _resampleSourceCursor++;
                    for (int k = 2; k <= 3; k++)
                    {
                        if (_resampleSourceCursor < _resampleSourceValidFrames)
                        {
                            int idx = _resampleSourceCursor * ch;
                            for (int c = 0; c < ch; c++)
                                hist[k * ch + c] = srcBuf[idx + c];
                            _resampleSourceCursor++;
                        }
                    }

                    _resampleFracPhase = 0.0;
                    _resampleHistoryPrimed = true;
                }
                else
                {
                    // Сдвигаем окно на 1 входной фрейм: [y_0, y_1, y_2, newSample]
                    hist.Slice(ch, 3 * ch).CopyTo(hist[..(3 * ch)]);
                    int nextIdx = _resampleSourceCursor * ch;
                    for (int c = 0; c < ch; c++)
                        hist[3 * ch + c] = srcBuf[nextIdx + c];

                    _resampleSourceCursor++;
                    _resampleFracPhase -= 1.0;
                }
            }

            float t = (float)_resampleFracPhase;
            int outBase = outFrame * ch;

            for (int c = 0; c < ch; c++)
            {
                float ym1 = hist[c];
                float y0 = hist[ch + c];
                float y1 = hist[2 * ch + c];
                float y2 = hist[3 * ch + c];

                // 4-point, 3rd-order Hermite (Catmull-Rom C1 continuous) interpolation
                float c0 = y0;
                float c1 = 0.5f * (y1 - ym1);
                float c2 = ym1 - 2.5f * y0 + 2.0f * y1 - 0.5f * y2;
                float c3 = 0.5f * (y2 - ym1) + 1.5f * (y0 - y1);

                targetSpan[outBase + c] = ((c3 * t + c2) * t + c1) * t + c0;
            }

            outFrame++;
            _resampleFracPhase += step;
        }

        return outFrame;
    }

    private void CaptureCrossfadeTailUnderLock()
    {
        int ch = Math.Clamp(_channels, 1, 8);
        int totalSamples = CrossfadeFrames * ch;
        Span<float> tailSpan = _crossfadeTail.AsSpan(0, totalSamples);
        tailSpan.Clear();

        if (MathF.Abs(_lastWasapiL) <= 0.0005f && MathF.Abs(_lastWasapiR) <= 0.0005f)
        {
            _crossfadeTailFrames = 0;
            return;
        }

        int framesRead = 0;
        if (_callback != null)
        {
            try
            {
                framesRead = Math.Clamp(ReadCallbackWithResamplingUnderLock(tailSpan, CrossfadeFrames), 0, CrossfadeFrames);
            }
            catch
            {
                framesRead = 0;
            }
        }

        float rawTail0L = framesRead > 0 ? tailSpan[0] : 0f;
        float rawTail0R = (framesRead > 0 && ch >= 2) ? tailSpan[1] : rawTail0L;

        if (framesRead > 0)
        {
            Span<float> validTail = tailSpan[..(framesRead * ch)];
            if (_fadeState != FadeState.None)
            {
                _ = ApplyFadeEnvelope(validTail, framesRead, ch, _fadeState, ref _fadeGain);
            }
            _gainProcessor.Process(validTail);

            rawTail0L = validTail[0];
            rawTail0R = ch >= 2 ? validTail[1] : validTail[0];
        }

        float lastL = _lastWasapiL;
        float lastR = _lastWasapiR;
        float slopeL = _lastWasapiL - _prevWasapiL;
        float slopeR = _lastWasapiR - _prevWasapiR;

        if (framesRead >= 2)
        {
            int lastIdx = (framesRead - 1) * ch;
            int prevIdx = (framesRead - 2) * ch;
            lastL = tailSpan[lastIdx];
            lastR = ch >= 2 ? tailSpan[lastIdx + 1] : lastL;
            slopeL = lastL - tailSpan[prevIdx];
            slopeR = (ch >= 2 ? tailSpan[lastIdx + 1] : lastL) - (ch >= 2 ? tailSpan[prevIdx + 1] : tailSpan[prevIdx]);
        }
        else if (framesRead == 1)
        {
            lastL = tailSpan[0];
            lastR = ch >= 2 ? tailSpan[1] : lastL;
            slopeL = lastL - _lastWasapiL;
            slopeR = lastR - _lastWasapiR;
        }

        slopeL = Math.Clamp(slopeL, -0.06f, 0.06f);
        slopeR = Math.Clamp(slopeR, -0.06f, 0.06f);

        int remFrames = CrossfadeFrames - framesRead;
        if (remFrames > 0)
        {
            float invRem = 1.0f / remFrames;
            float m0L = slopeL * remFrames;
            float m0R = slopeR * remFrames;

            for (int f = 0; f < remFrames; f++)
            {
                float t = (f + 1) * invRem;
                float t2 = t * t;
                float t3 = t2 * t;
                float h00 = 2.0f * t3 - 3.0f * t2 + 1.0f;
                float h10 = t3 - 2.0f * t2 + t;

                int idx = (framesRead + f) * ch;
                tailSpan[idx] = Math.Clamp(h00 * lastL + h10 * m0L, -0.985f, 0.985f);
                if (ch >= 2)
                    tailSpan[idx + 1] = Math.Clamp(h00 * lastR + h10 * m0R, -0.985f, 0.985f);
            }
        }

        _crossfadeTailFrames = CrossfadeFrames;

#if AUDIO_SPLICE_AUDIT
        Log.Debug(
            $"[WASAPI-TailCapture] readFromRing={framesRead}f/{CrossfadeFrames}f | " +
            $"lastWasapi=({_lastWasapiL:F4}, {_lastWasapiR:F4}) | rawTail0=({rawTail0L:F4}, {rawTail0R:F4}) | " +
            $"alignedTail0=({tailSpan[0]:F4}, {(ch >= 2 ? tailSpan[1] : tailSpan[0]):F4}) | " +
            $"tailEnd=({tailSpan[(CrossfadeFrames - 1) * ch]:F4}, {(ch >= 2 ? tailSpan[(CrossfadeFrames - 1) * ch + 1] : 0f):F4})");
#endif
    }

    private void WasapiPlaybackLoop()
    {
        _ = CoInitializeEx(0, COINIT_MULTITHREADED);
        nint mmcssHandle = AvSetMmThreadCharacteristicsW("Pro Audio", out _);
        bool wasapiStarted = false;

        try
        {
            InitializeEventsAndEndpointOnRenderThread();
            _initReadyEvent.Set();
        }
        catch (Exception ex)
        {
            _initException = ex;
            _initReadyEvent.Set();
            CleanupAllResourcesOnRenderThread(wasapiStarted: false);
            if (mmcssHandle != 0)
                _ = AvRevertMmThreadCharacteristics(mmcssHandle);
            CoUninitialize();
            return;
        }

        int idleGateClosedTicks = 0;
        uint steadyTargetFrames = Math.Min(_bufferFrameCount, (uint)(_sampleRate * SteadyTargetBufferMs / 1000));
        uint safeSteadySleepPaddingFrames = (uint)(_sampleRate * 50 / 1000);
        uint lowIdlePaddingThreshold = (uint)(_sampleRate * 22 / 1000);

#if AUDIO_SPLICE_AUDIT
        int postSpliceAuditTicksRemaining = 0;
        long lastSleepStartTick64 = Environment.TickCount64;
        uint lastRequestedSleepMs = 0;
#endif

        try
        {
            while (_playbackRunning && !_disposed)
            {
                if (_audioClient == null || _renderClient == null)
                    break;

#if AUDIO_SPLICE_AUDIT
                long wokeUpTick64 = Environment.TickCount64;
                long actualSleepMs = wokeUpTick64 - lastSleepStartTick64;
#endif

                bool isActive;
                FadeState snapshotFadeState;

                lock (_stateLock)
                {
                    snapshotFadeState = _fadeState;
                    isActive = _gateOpen || snapshotFadeState == FadeState.FadingOut;
                }

                if (!isActive)
                {
                    if (wasapiStarted)
                    {
                        if (AudioClientGetCurrentPadding(_audioClient, out uint idlePadding) >= 0)
                        {
                            bool needsIdleRampDown = idlePadding > 0
                                && idlePadding <= lowIdlePaddingThreshold
                                && (MathF.Abs(_lastWasapiL) > 0.0001f || MathF.Abs(_lastWasapiR) > 0.0001f);

                            if (needsIdleRampDown)
                            {
                                uint canWrite = _bufferFrameCount > idlePadding ? _bufferFrameCount - idlePadding : 0;
                                int rampLen = Math.Min((int)canWrite, CrossfadeFrames);
                                if (rampLen > 0 && RenderClientGetBuffer(_renderClient, (uint)rampLen, out byte* pIdleData) >= 0)
                                {
                                    uint flags = AUDCLNT_BUFFERFLAGS_SILENT;
                                    if (pIdleData != null)
                                    {
                                        flags = 0;
                                        Span<float> idleSpan = new((float*)pIdleData, rampLen * _channels);
                                        idleSpan.Clear();

                                        lock (_stateLock)
                                        {
                                            int tailAvailable = _crossfadeTailFrames;
                                            _crossfadeTailFrames = 0;
                                            float invRamp = 1.0f / rampLen;
                                            int ch = Math.Clamp(_channels, 1, 8);

                                            if (tailAvailable > 0)
                                            {
                                                ReadOnlySpan<float> tail = _crossfadeTail.AsSpan(0, tailAvailable * ch);
                                                int useTail = Math.Min(rampLen, tailAvailable);
                                                for (int f = 0; f < useTail; f++)
                                                {
                                                    float t = (f + 1) * invRamp;
                                                    float w = 1.0f - (t * t * (3.0f - 2.0f * t));
                                                    int bIdx = f * _channels;
                                                    int tIdx = f * ch;
                                                    idleSpan[bIdx] = tail[tIdx] * w;
                                                    if (_channels >= 2)
                                                        idleSpan[bIdx + 1] = (ch >= 2 ? tail[tIdx + 1] : tail[tIdx]) * w;
                                                }
                                            }
                                            else
                                            {
                                                for (int f = 0; f < rampLen; f++)
                                                {
                                                    float t = (f + 1) * invRamp;
                                                    float w = 1.0f - (t * t * (3.0f - 2.0f * t));
                                                    int bIdx = f * _channels;
                                                    idleSpan[bIdx] = _lastWasapiL * w;
                                                    if (_channels >= 2)
                                                        idleSpan[bIdx + 1] = _lastWasapiR * w;
                                                }
                                            }
#if AUDIO_SPLICE_AUDIT
                                            Log.Debug($"[WASAPI-Splice] PreventiveIdleRampToZero! idleTicks={idleGateClosedTicks}, idlePadding={idlePadding}f ({idlePadding * 1000 / Math.Max(1, _sampleRate)}ms), from=({_lastWasapiL:F4}, {_lastWasapiR:F4}), usedTail={tailAvailable}f, rampFrames={rampLen}");
#endif
                                            _prevWasapiL = 0f;
                                            _prevWasapiR = 0f;
                                            _lastWasapiL = 0f;
                                            _lastWasapiR = 0f;
                                        }
                                    }
                                    _ = RenderClientReleaseBuffer(_renderClient, (uint)rampLen, flags);
                                }
                            }
                            else if (idlePadding == 0)
                            {
                                lock (_stateLock)
                                {
                                    _crossfadeTailFrames = 0;
                                    _prevWasapiL = 0f;
                                    _prevWasapiR = 0f;
                                    _lastWasapiL = 0f;
                                    _lastWasapiR = 0f;
                                }
                            }
                        }

                        if (++idleGateClosedTicks < IdleTicksBeforeHardwareStop)
                        {
                            _bufferedSamples = 0;
#if AUDIO_SPLICE_AUDIT
                            lastSleepStartTick64 = Environment.TickCount64;
                            lastRequestedSleepMs = InteractiveSleepMs;
#endif
                            _ = WaitForSingleObject(_wakeControlEvent, InteractiveSleepMs);
                            continue;
                        }

                        _ = AudioClientStop(_audioClient);
                        _ = AudioClientReset(_audioClient);
                        wasapiStarted = false;
                        _bufferedSamples = 0;

                        lock (_stateLock)
                        {
                            _prevWasapiL = 0f;
                            _prevWasapiR = 0f;
                            _lastWasapiL = 0f;
                            _lastWasapiR = 0f;
                            _crossfadeTailFrames = 0;
                            _isFlushSeamless = false;
                        }
#if AUDIO_SPLICE_AUDIT
                        Log.Debug("[WASAPI-Splice] Hardware AudioClientStopped after idle timeout");
#endif
                    }

                    idleGateClosedTicks = 0;
#if AUDIO_SPLICE_AUDIT
                    lastSleepStartTick64 = Environment.TickCount64;
                    lastRequestedSleepMs = INFINITE;
#endif
                    _ = WaitForSingleObject(_wakeControlEvent, INFINITE);
                    continue;
                }

#if AUDIO_SPLICE_AUDIT
                int closedTicksBeforeWake = idleGateClosedTicks;
#endif
                idleGateClosedTicks = 0;

                int hrPadding = AudioClientGetCurrentPadding(_audioClient, out uint paddingFrames);
                if (hrPadding < 0)
                {
                    if (IsDeviceInvalidatedHr(hrPadding) && TryHotSwapEndpointOnRenderThread(ref wasapiStarted))
                        continue;

                    HandleFatalDeviceLossOnRenderThread(hrPadding);
                    break;
                }

                _bufferedSamples = (int)(paddingFrames * (uint)_channels);

                bool isInteractive = snapshotFadeState != FadeState.None
                                  || Environment.TickCount64 < Volatile.Read(ref _interactiveUntilTick64);

                bool hasSeamlessPending;
                lock (_stateLock)
                {
                    if (paddingFrames == 0 && _crossfadeTailFrames > 0)
                    {
                        _crossfadeTailFrames = 0;
                        _prevWasapiL = 0f;
                        _prevWasapiR = 0f;
                        _lastWasapiL = 0f;
                        _lastWasapiR = 0f;
                    }
                    hasSeamlessPending = _isFlushSeamless;
                }

                uint targetFillFrames = steadyTargetFrames;
                if (hasSeamlessPending && targetFillFrames < paddingFrames + CrossfadeFrames)
                {
                    targetFillFrames = Math.Min(_bufferFrameCount, paddingFrames + CrossfadeFrames);
                }

                uint availableFrames = targetFillFrames > paddingFrames
                    ? targetFillFrames - paddingFrames
                    : 0;

                if (availableFrames > 0)
                {
                    int hrGet = RenderClientGetBuffer(_renderClient, availableFrames, out byte* pData);
                    if (hrGet < 0)
                    {
                        if (IsDeviceInvalidatedHr(hrGet) && TryHotSwapEndpointOnRenderThread(ref wasapiStarted))
                            continue;

                        HandleFatalDeviceLossOnRenderThread(hrGet);
                        break;
                    }

                    uint releaseFlags = 0;
                    uint framesToCommit = availableFrames;

                    if (pData != null)
                    {
                        int totalFloats = (int)(availableFrames * (uint)_channels);
                        Span<float> targetSpan = new((float*)pData, totalFloats);

                        int pcmFramesRead = 0;
                        int validPcmFrames = 0;
                        FadeState currentFadeState;
                        float currentFadeGain;
                        bool doSeamlessBridge = false;
                        int tailFramesAvailable = 0;
                        float prevLBeforeChunk;
                        float prevRBeforeChunk;
                        float prevSlopeL;
                        float prevSlopeR;
                        float rawFirstNewL = 0f;
                        float rawFirstNewR = 0f;

                        lock (_stateLock)
                        {
                            currentFadeState = _fadeState;
                            currentFadeGain = _fadeGain;
                            prevLBeforeChunk = _lastWasapiL;
                            prevRBeforeChunk = _lastWasapiR;
                            prevSlopeL = _lastWasapiL - _prevWasapiL;
                            prevSlopeR = _lastWasapiR - _prevWasapiR;

                            if ((_gateOpen || currentFadeState == FadeState.FadingOut) && _callback != null)
                            {
                                pcmFramesRead = ReadCallbackWithResamplingUnderLock(targetSpan, (int)availableFrames);
                            }

                            if (pcmFramesRead > 0)
                            {
                                Volatile.Write(ref _consecutiveUnderrunCount, 0);

                                validPcmFrames = Math.Min(pcmFramesRead, (int)availableFrames);
                                int validPcmFloats = validPcmFrames * _channels;
                                Span<float> validPcmSpan = targetSpan[..validPcmFloats];

                                framesToCommit = (uint)validPcmFrames;

                                _gainProcessor.Process(validPcmSpan);

                                if (currentFadeState != FadeState.None)
                                {
                                    bool fadeCompleted = ApplyFadeEnvelope(
                                        validPcmSpan,
                                        validPcmFrames,
                                        _channels,
                                        currentFadeState,
                                        ref currentFadeGain);

                                    if (_fadeState == currentFadeState)
                                    {
                                        _fadeGain = currentFadeGain;
                                        if (fadeCompleted)
                                        {
                                            if (currentFadeState == FadeState.FadingOut)
                                            {
                                                _gateOpen = false;
                                            }

                                            _fadeState = FadeState.None;
                                        }
                                    }
                                }

                                if (_isFlushSeamless)
                                {
                                    _isFlushSeamless = false;
                                    doSeamlessBridge = true;
                                    tailFramesAvailable = _crossfadeTailFrames;
                                    _crossfadeTailFrames = 0;
                                }

                                rawFirstNewL = validPcmSpan[0];
                                rawFirstNewR = _channels >= 2 ? validPcmSpan[1] : rawFirstNewL;

                                if (doSeamlessBridge)
                                {
#if AUDIO_SPLICE_AUDIT
                                    postSpliceAuditTicksRemaining = 5;
#endif
                                    int ch = Math.Clamp(_channels, 1, 8);

                                    if (tailFramesAvailable > 0 && (MathF.Abs(prevLBeforeChunk) > 0.0005f || MathF.Abs(prevRBeforeChunk) > 0.0005f))
                                    {
                                        int mixFrames = Math.Min(validPcmFrames, tailFramesAvailable);
                                        float invMix = 1.0f / mixFrames;
                                        ReadOnlySpan<float> tail = _crossfadeTail.AsSpan(0, tailFramesAvailable * ch);

                                        for (int f = 0; f < mixFrames; f++)
                                        {
                                            float t = f * invMix;
                                            float wNew = t * t * (3.0f - 2.0f * t);
                                            float wOld = 1.0f - wNew;
                                            int bIdx = f * _channels;
                                            int tIdx = f * ch;

                                            validPcmSpan[bIdx] = Math.Clamp(tail[tIdx] * wOld + validPcmSpan[bIdx] * wNew, -0.985f, 0.985f);
                                            if (_channels >= 2)
                                            {
                                                float tR = ch >= 2 ? tail[tIdx + 1] : tail[tIdx];
                                                validPcmSpan[bIdx + 1] = Math.Clamp(tR * wOld + validPcmSpan[bIdx + 1] * wNew, -0.985f, 0.985f);
                                            }
                                        }
                                    }
                                    else
                                    {
                                        int fadeInFrames = Math.Min(validPcmFrames, FadeFrames);
                                        float invFadeIn = 1.0f / fadeInFrames;
                                        for (int f = 0; f < fadeInFrames; f++)
                                        {
                                            float t = (f + 1) * invFadeIn;
                                            float wNew = t * t * (3.0f - 2.0f * t);
                                            int bIdx = f * _channels;
                                            validPcmSpan[bIdx] *= wNew;
                                            if (_channels >= 2)
                                                validPcmSpan[bIdx + 1] *= wNew;
                                        }
                                    }
                                }

                                if (validPcmFrames >= 2)
                                {
                                    int lastBase = (validPcmFrames - 1) * _channels;
                                    int prevBase = (validPcmFrames - 2) * _channels;
                                    _prevWasapiL = validPcmSpan[prevBase];
                                    _prevWasapiR = _channels >= 2 ? validPcmSpan[prevBase + 1] : _prevWasapiL;
                                    _lastWasapiL = validPcmSpan[lastBase];
                                    _lastWasapiR = _channels >= 2 ? validPcmSpan[lastBase + 1] : _lastWasapiL;
                                }
                                else if (validPcmFrames == 1)
                                {
                                    _prevWasapiL = _lastWasapiL;
                                    _prevWasapiR = _lastWasapiR;
                                    _lastWasapiL = validPcmSpan[0];
                                    _lastWasapiR = _channels >= 2 ? validPcmSpan[1] : _lastWasapiL;
                                }
                            }
                        }

                        if (pcmFramesRead <= 0)
                        {
                            if (paddingFrames > (uint)(_sampleRate / 50))
                            {
                                framesToCommit = 0;
                            }
                            else if (MathF.Abs(_lastWasapiL) > 0.0001f || MathF.Abs(_lastWasapiR) > 0.0001f)
                            {
                                targetSpan.Clear();
                                int rampLen = Math.Min((int)availableFrames, CrossfadeFrames);
                                float invRamp = 1.0f / rampLen;
                                for (int f = 0; f < rampLen; f++)
                                {
                                    float t = (f + 1) * invRamp;
                                    float w = 1.0f - (t * t * (3.0f - 2.0f * t));
                                    int bIdx = f * _channels;
                                    targetSpan[bIdx] = _lastWasapiL * w;
                                    if (_channels >= 2)
                                        targetSpan[bIdx + 1] = _lastWasapiR * w;
                                }
#if AUDIO_SPLICE_AUDIT
                                Log.Warn($"[WASAPI-Splice] UnderrunRampToZero! paddingFrames={paddingFrames}f ({paddingFrames * 1000 / Math.Max(1, _sampleRate)}ms), from=({_lastWasapiL:F4}, {_lastWasapiR:F4}), rampLen={rampLen}");
#endif
                                framesToCommit = (uint)rampLen;
                                releaseFlags = 0;

                                lock (_stateLock)
                                {
                                    _prevWasapiL = 0f;
                                    _prevWasapiR = 0f;
                                    _lastWasapiL = 0f;
                                    _lastWasapiR = 0f;
                                    _isFlushSeamless = true;
                                    _crossfadeTailFrames = 0;
                                    if (_fadeState == FadeState.FadingOut)
                                    {
                                        _gateOpen = false;
                                        _fadeState = FadeState.None;
                                        _fadeGain = 0f;
                                    }
                                }
                            }
                            else
                            {
                                releaseFlags = AUDCLNT_BUFFERFLAGS_SILENT;
                                lock (_stateLock)
                                {
                                    if (_fadeState == FadeState.FadingOut)
                                    {
                                        _gateOpen = false;
                                        _fadeState = FadeState.None;
                                        _fadeGain = 0f;
                                    }
                                }
                            }

                            if (_gateOpen)
                            {
                                int underruns = Interlocked.Increment(ref _consecutiveUnderrunCount);
                                if (underruns == UnderrunLogThreshold)
                                    Log.Warn($"[WinAudioBackend] ⚠ {underruns} WASAPI underruns");

                                if (underruns == StarvationThreshold)
                                {
                                    Log.Error($"[WinAudioBackend] Starvation detected: {underruns} consecutive underruns");
                                    var starvationCb = _onStarvation;
                                    if (starvationCb != null)
                                        ThreadPool.UnsafeQueueUserWorkItem(static s => ((Action)s!).Invoke(), starvationCb);
                                }
                            }
                        }
                        else
                        {
#if AUDIO_SPLICE_AUDIT
                            Span<float> validPcmSpan = targetSpan[..(validPcmFrames * _channels)];
                            float new0L = validPcmSpan[0];
                            float new0R = _channels >= 2 ? validPcmSpan[1] : new0L;
                            float boundaryStepL = MathF.Abs(new0L - prevLBeforeChunk);
                            float boundaryStepR = MathF.Abs(new0R - prevRBeforeChunk);
                            float boundaryStepMax = MathF.Max(boundaryStepL, boundaryStepR);

                            float boundaryD2L = MathF.Abs((new0L - prevLBeforeChunk) - prevSlopeL);
                            float boundaryD2R = MathF.Abs((new0R - prevRBeforeChunk) - prevSlopeR);
                            float boundaryD2Max = MathF.Max(boundaryD2L, boundaryD2R);

                            float maxStepBoth = boundaryStepMax;
                            float maxD2Both = boundaryD2Max;
                            int maxStepFrame = 0;
                            int maxD2Frame = 0;

                            float pL = new0L, pR = new0R;
                            float ppL = prevLBeforeChunk, ppR = prevRBeforeChunk;

                            for (int f = 1; f < validPcmFrames; f++)
                            {
                                float cL = validPcmSpan[f * _channels];
                                float cR = _channels >= 2 ? validPcmSpan[f * _channels + 1] : cL;

                                float st = MathF.Max(MathF.Abs(cL - pL), MathF.Abs(cR - pR));
                                if (st > maxStepBoth)
                                {
                                    maxStepBoth = st;
                                    maxStepFrame = f;
                                }

                                float d2 = MathF.Max(MathF.Abs(cL - 2f * pL + ppL), MathF.Abs(cR - 2f * pR + ppR));
                                if (d2 > maxD2Both)
                                {
                                    maxD2Both = d2;
                                    maxD2Frame = f;
                                }

                                ppL = pL; ppR = pR;
                                pL = cL; pR = cR;
                            }

                            if (doSeamlessBridge || postSpliceAuditTicksRemaining > 0 || closedTicksBeforeWake > 0 || paddingFrames == 0)
                            {
                                if (postSpliceAuditTicksRemaining > 0)
                                    postSpliceAuditTicksRemaining--;

                                Log.Debug(
                                    $"[WASAPI-Audit] bridge={doSeamlessBridge}, fade={currentFadeState}(g={currentFadeGain:F2}), tail={tailFramesAvailable}f, " +
                                    $"padBefore={paddingFrames}f ({paddingFrames * 1000 / Math.Max(1, _sampleRate)}ms), req={availableFrames}f, written={validPcmFrames}f, " +
                                    $"sleep={actualSleepMs}ms(req={lastRequestedSleepMs}ms) | " +
                                    $"L:{prevLBeforeChunk:F4}->{new0L:F4}(raw:{rawFirstNewL:F4}) R:{prevRBeforeChunk:F4}->{new0R:F4}(raw:{rawFirstNewR:F4}) | " +
                                    $"bStep={boundaryStepMax:F4}, bD2={boundaryD2Max:F4}, maxStep={maxStepBoth:F4}@{maxStepFrame}, maxD2={maxD2Both:F4}@{maxD2Frame}");
                            }
#endif
                        }
                    }
                    else
                    {
                        releaseFlags = AUDCLNT_BUFFERFLAGS_SILENT;
                    }

                    int hrRel = RenderClientReleaseBuffer(_renderClient, framesToCommit, releaseFlags);
                    if (hrRel < 0)
                    {
                        if (IsDeviceInvalidatedHr(hrRel) && TryHotSwapEndpointOnRenderThread(ref wasapiStarted))
                            continue;

                        HandleFatalDeviceLossOnRenderThread(hrRel);
                        break;
                    }

                    uint totalBufferedFrames = paddingFrames + framesToCommit;
                    _bufferedSamples = (int)(totalBufferedFrames * (uint)_channels);
                    paddingFrames = totalBufferedFrames;
                }

                if (!wasapiStarted)
                {
                    int hrStart = AudioClientStart(_audioClient);
                    if (hrStart < 0)
                    {
                        if (IsDeviceInvalidatedHr(hrStart) && TryHotSwapEndpointOnRenderThread(ref wasapiStarted))
                            continue;

                        HandleFatalDeviceLossOnRenderThread(hrStart);
                        break;
                    }
                    wasapiStarted = true;
                }

                uint sleepMs = (isInteractive || paddingFrames < safeSteadySleepPaddingFrames)
                    ? InteractiveSleepMs
                    : SteadySleepMs;
#if AUDIO_SPLICE_AUDIT
                lastSleepStartTick64 = Environment.TickCount64;
                lastRequestedSleepMs = sleepMs;
#endif
                _ = WaitForSingleObject(_wakeControlEvent, sleepMs);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[WinAudioBackend] WASAPI playback thread exception: {ex.Message}");
        }
        finally
        {
            CleanupAllResourcesOnRenderThread(wasapiStarted);
            _playbackRunning = false;

            if (mmcssHandle != 0)
                _ = AvRevertMmThreadCharacteristics(mmcssHandle);

            CoUninitialize();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsDeviceInvalidatedHr(int hr) =>
        hr is AUDCLNT_E_DEVICE_INVALIDATED
           or AUDCLNT_E_ENDPOINT_CREATE_FAILED
           or AUDCLNT_E_SERVICE_NOT_RUNNING
           or AUDCLNT_E_NOT_INITIALIZED;

    private void HandleFatalDeviceLossOnRenderThread(int hr)
    {
        Log.Error($"[WinAudioBackend] WASAPI endpoint lost (HRESULT: 0x{hr:X8}) — closing gate and preserving ring buffer");

        _playbackRunning = false;
        _bufferedSamples = 0;

        lock (_stateLock)
        {
            _gateOpen = false;
            _fadeState = FadeState.None;
            _fadeGain = 0f;
            _crossfadeTailFrames = 0;
        }

        _deviceLost = true;
        StartDeviceWatcher();

        var lostCb = _onDeviceLost;
        if (lostCb != null)
            ThreadPool.UnsafeQueueUserWorkItem(static s => ((Action)s!).Invoke(), lostCb);
    }

    /// <summary>
    /// Применяет C1-гладкую огибающую микро-фейда (fade-in / fade-out) напрямую к <see cref="Span{T}"/> без аллокаций и гонок.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool ApplyFadeEnvelope(
        Span<float> buffer,
        int frames,
        int channels,
        FadeState fadeState,
        ref float fadeGain)
    {
        if (fadeState == FadeState.None || buffer.IsEmpty || frames <= 0 || channels <= 0)
            return false;

        float gain = fadeGain;
        const float step = 1.0f / FadeFrames;
        ref float bufRef = ref MemoryMarshal.GetReference(buffer);

        int maxFrames = Math.Min(frames, buffer.Length / channels);

        for (int frame = 0; frame < maxFrames; frame++)
        {
            if (fadeState == FadeState.FadingIn)
            {
                gain = MathF.Min(1f, gain + step);
                if (gain >= 1f)
                {
                    fadeGain = 1f;
                    return true;
                }
            }
            else
            {
                gain = MathF.Max(0f, gain - step);
                if (gain <= 0f)
                {
                    int clearStart = frame * channels;
                    if (clearStart < buffer.Length)
                        buffer[clearStart..].Clear();

                    fadeGain = 0f;
                    return true;
                }
            }

            float smoothGain = gain * gain * (3.0f - 2.0f * gain);
            int baseIdx = frame * channels;
            for (int ch = 0; ch < channels; ch++)
            {
                Unsafe.Add(ref bufRef, baseIdx + ch) *= smoothGain;
            }
        }

        fadeGain = gain;
        return false;
    }

    #endregion

    #region Playback Flow Control

    /// <inheritdoc/>
    public void ActivateFillLoop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <inheritdoc/>
    public bool WaitForWarmup(int timeoutMs = 100) => !_disposed && _sessionInitialized;

    /// <inheritdoc/>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_sessionInitialized) return;

        if (_deviceLost)
            throw new AudioDeviceException("Error_NoAudioDevice");

        lock (_stateLock)
        {
            if (_gateOpen && _fadeState != FadeState.FadingOut) return;

            bool wasFadingOut = _fadeState == FadeState.FadingOut && _fadeGain > 0.001f;
            _gateOpen = true;

            if (wasFadingOut)
            {
                _fadeState = FadeState.FadingIn;
                _isFlushSeamless = false;
                _crossfadeTailFrames = 0;
            }
            else if (_isFlushSeamless)
            {
                _fadeState = FadeState.None;
                _fadeGain = 1f;
            }
            else
            {
                _fadeState = FadeState.FadingIn;
                if (_fadeGain <= 0f || _fadeGain >= 1f)
                    _fadeGain = 0f;
            }
        }

        Volatile.Write(ref _interactiveUntilTick64, Environment.TickCount64 + InteractiveHoldDurationMs);
        SignalWakeEvent();
    }

    /// <inheritdoc/>
    public void Stop()
    {
        if (!_sessionInitialized) return;

        lock (_stateLock)
        {
            if (!_gateOpen && _fadeState != FadeState.FadingIn) return;

            if (_fadeState == FadeState.None)
                _fadeGain = 1.0f;

            _fadeState = FadeState.FadingOut;
            _isFlushSeamless = false;
            _crossfadeTailFrames = 0;
        }

        Volatile.Write(ref _interactiveUntilTick64, Environment.TickCount64 + InteractiveHoldDurationMs);
        SignalWakeEvent();
    }

    /// <inheritdoc/>
    public void Flush()
    {
        if (_disposed) return;

        lock (_stateLock)
        {
            if (_gateOpen || _bufferedSamples > 0 || MathF.Abs(_lastWasapiL) > 0.0001f || MathF.Abs(_lastWasapiR) > 0.0001f)
            {
                if (_crossfadeTailFrames == 0)
                {
                    CaptureCrossfadeTailUnderLock();
                }
                _isFlushSeamless = true;
            }

            ResetResamplerStateUnderLock();
            _gateOpen = false;
            _fadeState = FadeState.None;
            _fadeGain = 0f;
            _bufferedSamples = 0;
        }

        Volatile.Write(ref _consecutiveUnderrunCount, 0);
        Volatile.Write(ref _interactiveUntilTick64, Environment.TickCount64 + InteractiveHoldDurationMs);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetVolumeGain(float gain)
    {
        _gainProcessor.SetVolumeGain(gain);
        Volatile.Write(ref _interactiveUntilTick64, Environment.TickCount64 + InteractiveHoldDurationMs);
        SignalWakeEvent();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SignalWakeEvent()
    {
        nint ev = _wakeControlEvent;
        if (ev != 0)
            _ = SetEvent(ev);
    }

    #endregion

    #region Device Watcher & Thread Stop

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

        _ = CoInitializeEx(0, COINIT_MULTITHREADED);
        void* pEnum = null;
        void* pDev = null;

        try
        {
            int hr = CoCreateInstance(
                in CLSID_MMDeviceEnumerator,
                0,
                CLSCTX_ALL,
                in IID_IMMDeviceEnumerator,
                out pEnum);

            if (hr >= 0 && pEnum != null)
            {
                hr = EnumeratorGetDefaultEndpoint(pEnum, EDataFlow_eRender, ERole_eMultimedia, out pDev);
                if (hr >= 0 && pDev != null)
                {
                    StopDeviceWatcher();
                    Log.Info("[WinAudioBackend] Default WASAPI endpoint verified available — triggering auto-recovery");

                    var cb = _onDeviceAvailable;
                    if (cb != null)
                        ThreadPool.UnsafeQueueUserWorkItem(static s => ((Action)s!).Invoke(), cb);
                }
            }
        }
        catch
        {
        }
        finally
        {
            SafeRelease(ref pDev);
            SafeRelease(ref pEnum);
            CoUninitialize();
        }
    }

    private void StopPlaybackThread()
    {
        _playbackRunning = false;
        SignalWakeEvent();

        if (_playbackThread is { IsAlive: true })
            _playbackThread.Join(PlaybackThreadJoinTimeoutMs);

        _playbackThread = null;
    }

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
        _initReadyEvent.Dispose();

        _onDeviceAvailable = null;
        _onDeviceLost = null;
        _onStarvation = null;

        Log.Debug("[WinAudioBackend] Disposed");
    }

    #endregion
}