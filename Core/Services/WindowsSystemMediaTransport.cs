using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace LMP.Core.Services;

/// <summary>
/// Native AOT-safe Windows 10/11 <c>SystemMediaTransportControls</c> implementation using direct COM/WinRT ABI vtables.
/// Zero reflection, zero WinRT projection bloat, zero polling timers, and agile MTA callback delivery.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe partial class WindowsSystemMediaTransport : ISystemMediaTransport
{
    #region Constants & GUIDs

    private const int S_OK = 0;
    private const int E_NOINTERFACE = unchecked((int)0x80004002);
    private const int MaxThumbnailBytes = 2 * 1024 * 1024;
    private const int ThumbnailChunkSize = 16 * 1024;

    private static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidIAgileObject = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");
    private static readonly Guid IidSmtcInterop = new("DDB0472D-C911-4A1F-86D9-DC3D71A95F5A");
    private static readonly Guid IidSmtc = new("99FA3FF4-1742-42A6-902E-087D41F965EC");
    private static readonly Guid IidSmtc2 = new("EA98D2F6-7F3C-4AF2-A586-72889808EFB1");
    private static readonly Guid IidTimelineProps = new("5125316A-C3A2-475B-8507-93534DC88F15");
    private static readonly Guid IidButtonPressedArgs = new("B7F47116-A56F-4DC8-9E11-92031F4A87C2");
    private static readonly Guid IidPositionChangeArgs = new("B4493F88-EB28-4961-9C14-335E44F3E125");
    private static readonly Guid IidRasRefStatics = new("857309DC-3FBF-4E7D-986F-EF3B1A07A964");
    private static readonly Guid IidRandomAccessStream = new("905A0FE1-BC53-11DF-8C49-001E4FC686DA");

    private static readonly Guid IidButtonPressedHandler = ComputeTypedEventHandlerIid(
        "Windows.Media.SystemMediaTransportControlsButtonPressedEventArgs",
        IidButtonPressedArgs);

    private static readonly Guid IidPositionChangeHandler = ComputeTypedEventHandlerIid(
        "Windows.Media.PlaybackPositionChangeRequestedEventArgs",
        IidPositionChangeArgs);

    private static readonly nint* ButtonPressedVtable = AllocateHandlerVtable(&OnButtonPressedInvoke);
    private static readonly nint* PositionChangeVtable = AllocateHandlerVtable(&OnPositionChangeInvoke);

    private const int MediaPlaybackTypeMusic = 1;
    private const int MediaPlaybackStatusClosed = 0;
    private const int MediaPlaybackStatusChanging = 1;
    private const int MediaPlaybackStatusStopped = 2;
    private const int MediaPlaybackStatusPlaying = 3;
    private const int MediaPlaybackStatusPaused = 4;

    #endregion

    #region Native Imports & Structs

    [LibraryImport("combase.dll")]
    private static partial int CoIncrementMTAUsage(out nint cookie);

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint activatableClassId, Guid* iid, nint* factory);

    [LibraryImport("combase.dll")]
    private static partial int RoActivateInstance(nint activatableClassId, nint* instance);

    [LibraryImport("combase.dll")]
    private static partial int WindowsCreateStringReference(
        char* sourceString,
        uint length,
        HStringHeader* hstringHeader,
        nint* hstring);

    [LibraryImport("shlwapi.dll")]
    private static partial nint SHCreateMemStream(byte* pInit, uint cbInit);

    [LibraryImport("shcore.dll")]
    private static partial int CreateRandomAccessStreamOverStream(
        nint stream,
        int options,
        Guid* riid,
        nint* ppv);

    [StructLayout(LayoutKind.Sequential)]
    private struct HStringHeader
    {
        public nint P0;
        public int I1;
        public int I2;
        public int I3;
        public int I4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHandlerBlock
    {
        public nint* Vtable;
        public int RefCount;
        public nint GcHandle;
        public Guid ExpectedIid;
    }

    #endregion

    #region Fields

    private readonly Lock _comLock = new();
    private nint _smtc;
    private nint _smtc2;
    private nint _updater;
    private nint _musicProps;
    private nint _timelineProps;
    private nint _rasRefStatics;

    private nint _buttonPressedHandler;
    private long _buttonPressedToken;
    private nint _positionChangeHandler;
    private long _positionChangeToken;

    private volatile bool _isSupported = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240);
    private int _detached = 1;
    private int _disposed;
    private volatile string? _lastCoverPath;
    private bool _hasActiveMetadata;
    private int _lastPlaybackStatus = MediaPlaybackStatusClosed;
    private int _mtaIncremented;

    #endregion

    /// <inheritdoc />
    public bool IsSupported => _isSupported && Volatile.Read(ref _disposed) == 0;

    /// <inheritdoc />
    public event Action<MediaTransportCommand, TimeSpan>? CommandReceived;

    /// <inheritdoc />
    public void AttachWindow(nint windowHandle)
    {
        if (!IsSupported || windowHandle == 0)
            return;

        lock (_comLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_isSupported)
                return;

            DetachWindowLocked();

            try
            {
                if (Interlocked.Exchange(ref _mtaIncremented, 1) == 0)
                    _ = CoIncrementMTAUsage(out _);

                nint interop = ActivateFactory("Windows.Media.SystemMediaTransportControls", IidSmtcInterop);
                if (interop == 0)
                {
                    DisableSupport("RoGetActivationFactory failed for SystemMediaTransportControls");
                    return;
                }

                try
                {
                    Guid smtcIid = IidSmtc;
                    nint smtc = 0;
                    int hr = ((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)GetVtableSlot(interop, 6))(
                        interop, windowHandle, &smtcIid, &smtc);

                    if (hr < 0 || smtc == 0)
                    {
                        DisableSupport($"ISystemMediaTransportControlsInterop.GetForWindow failed: 0x{hr:X8}");
                        return;
                    }

                    _smtc = smtc;
                }
                finally
                {
                    ReleaseCom(ref interop);
                }

#if DEBUG
                DumpIidsDebug(_smtc, "SMTC");
#endif

                _smtc2 = QueryInterface(_smtc, IidSmtc2);
#if DEBUG
                Debug.WriteLine($"[SMTC-DBG] smtc=0x{_smtc:X}, smtc2=0x{_smtc2:X}");
#endif

                nint updater = 0;
                int hrUpdater = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)GetVtableSlot(_smtc, 8))(_smtc, &updater);
#if DEBUG
                Debug.WriteLine($"[SMTC-DBG] DisplayUpdater hr=0x{hrUpdater:X8}, ptr=0x{updater:X}");
#endif
                if (hrUpdater >= 0 && updater != 0)
                {
                    _updater = updater;
                    _ = ((delegate* unmanaged[Stdcall]<nint, int, int>)GetVtableSlot(_updater, 7))(_updater, MediaPlaybackTypeMusic);
                    nint musicProps = 0;
                    int hrMusic = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)GetVtableSlot(_updater, 12))(_updater, &musicProps);
                    if (hrMusic >= 0)
                        _musicProps = musicProps;
#if DEBUG
                    Debug.WriteLine($"[SMTC-DBG] MusicProperties hr=0x{hrMusic:X8}, ptr=0x{_musicProps:X}");
#endif
                }

                _rasRefStatics = ActivateFactory("Windows.Storage.Streams.RandomAccessStreamReference", IidRasRefStatics);
                _timelineProps = ActivateTimelineProperties();

                _buttonPressedHandler = CreateEventHandler(ButtonPressedVtable, IidButtonPressedHandler);
                long bpToken = 0;
                int hrBp = ((delegate* unmanaged[Stdcall]<nint, nint, long*, int>)GetVtableSlot(_smtc, 32))(
                    _smtc, _buttonPressedHandler, &bpToken);

                if (hrBp < 0)
                {
                    DisableSupport($"SMTC.add_ButtonPressed failed: 0x{hrBp:X8}");
                    DetachWindowLocked();
                    return;
                }
                _buttonPressedToken = bpToken;

                if (_smtc2 != 0)
                {
                    _positionChangeHandler = CreateEventHandler(PositionChangeVtable, IidPositionChangeHandler);
                    long posToken = 0;
                    int hrPos = ((delegate* unmanaged[Stdcall]<nint, nint, long*, int>)GetVtableSlot(_smtc2, 13))(
                        _smtc2, _positionChangeHandler, &posToken);

                    if (hrPos >= 0)
                    {
                        _positionChangeToken = posToken;
                    }
                    else
                    {
                        Log.Debug($"[SMTC] add_PlaybackPositionChangeRequested skipped: 0x{hrPos:X8}");
                        ReleaseCom(ref _positionChangeHandler);
                    }
                }

                Volatile.Write(ref _detached, 0);
                Log.Info("[SMTC] Windows SystemMediaTransportControls attached");
            }
            catch (Exception ex)
            {
                DisableSupport($"SMTC attach exception: {ex.Message}");
                DetachWindowLocked();
            }
        }
    }

    /// <inheritdoc />
    public void DetachWindow()
    {
        lock (_comLock)
        {
            DetachWindowLocked();
        }
    }

    /// <inheritdoc />
    public void UpdateState(PlaybackState state)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        lock (_comLock)
        {
            if (_smtc == 0 || Volatile.Read(ref _detached) != 0)
                return;

            int smtcStatus = state switch
            {
                PlaybackState.Playing => MediaPlaybackStatusPlaying,
                PlaybackState.Paused => MediaPlaybackStatusPaused,
                PlaybackState.Loading or PlaybackState.Buffering => MediaPlaybackStatusChanging,
                PlaybackState.Stopped or PlaybackState.Error => _hasActiveMetadata ? MediaPlaybackStatusStopped : MediaPlaybackStatusClosed,
                _ => MediaPlaybackStatusClosed
            };

            _lastPlaybackStatus = smtcStatus;
            byte enabled = _hasActiveMetadata || state is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Loading or PlaybackState.Buffering
                ? (byte)1
                : (byte)0;

#if DEBUG
            Debug.WriteLine($"[SMTC-DBG] UpdateState state={state}, smtcStatus={smtcStatus}, enabled={enabled}");
#endif
            _ = ((delegate* unmanaged[Stdcall]<nint, byte, int>)GetVtableSlot(_smtc, 11))(_smtc, enabled);
            _ = ((delegate* unmanaged[Stdcall]<nint, int, int>)GetVtableSlot(_smtc, 7))(_smtc, smtcStatus);
        }
    }

    /// <inheritdoc />
    public void UpdateCapabilities(in MediaTransportCapabilities capabilities)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        lock (_comLock)
        {
            if (_smtc == 0 || Volatile.Read(ref _detached) != 0)
                return;

#if DEBUG
            Debug.WriteLine($"[SMTC-DBG] UpdateCapabilities Play={capabilities.CanPlay} Next={capabilities.CanGoNext} Prev={capabilities.CanGoPrevious} Seek={capabilities.CanSeek}");
#endif
            _ = ((delegate* unmanaged[Stdcall]<nint, byte, int>)GetVtableSlot(_smtc, 13))(_smtc, capabilities.CanPlay ? (byte)1 : (byte)0);
            _ = ((delegate* unmanaged[Stdcall]<nint, byte, int>)GetVtableSlot(_smtc, 15))(_smtc, capabilities.CanStop ? (byte)1 : (byte)0);
            _ = ((delegate* unmanaged[Stdcall]<nint, byte, int>)GetVtableSlot(_smtc, 17))(_smtc, capabilities.CanPause ? (byte)1 : (byte)0);
            _ = ((delegate* unmanaged[Stdcall]<nint, byte, int>)GetVtableSlot(_smtc, 21))(_smtc, capabilities.CanSeek ? (byte)1 : (byte)0);
            _ = ((delegate* unmanaged[Stdcall]<nint, byte, int>)GetVtableSlot(_smtc, 23))(_smtc, capabilities.CanSeek ? (byte)1 : (byte)0);
            _ = ((delegate* unmanaged[Stdcall]<nint, byte, int>)GetVtableSlot(_smtc, 25))(_smtc, capabilities.CanGoPrevious ? (byte)1 : (byte)0);
            _ = ((delegate* unmanaged[Stdcall]<nint, byte, int>)GetVtableSlot(_smtc, 27))(_smtc, capabilities.CanGoNext ? (byte)1 : (byte)0);
        }
    }

    /// <inheritdoc />
    public void UpdateMetadata(in MediaTransportMetadata metadata, string? coverFilePath)
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _detached) != 0)
            return;

        string? snapshotCoverPath;
        lock (_comLock)
        {
            if (_smtc == 0 || _updater == 0 || Volatile.Read(ref _detached) != 0)
                return;

            snapshotCoverPath = _lastCoverPath;
        }

        // Read/decode/encode cover bytes off-lock so the UI thread never stalls on disk I/O or Skia.
        bool prepareAttempted = !string.IsNullOrEmpty(coverFilePath)
                             && !string.Equals(snapshotCoverPath, coverFilePath, StringComparison.Ordinal);
        PreparedThumbnail prepared = prepareAttempted
            ? PrepareThumbnailFromFile(coverFilePath!)
            : default;

        try
        {
            for (int pass = 0; pass < 2; pass++)
            {
                bool needsRetryPrepare = false;

                lock (_comLock)
                {
                    if (_smtc == 0 || _updater == 0 || Volatile.Read(ref _detached) != 0)
                        return;

                    _hasActiveMetadata = true;
                    _ = ((delegate* unmanaged[Stdcall]<nint, byte, int>)GetVtableSlot(_smtc, 11))(_smtc, 1);
                    if (_lastPlaybackStatus == MediaPlaybackStatusClosed)
                    {
                        _lastPlaybackStatus = MediaPlaybackStatusStopped;
                        _ = ((delegate* unmanaged[Stdcall]<nint, int, int>)GetVtableSlot(_smtc, 7))(_smtc, MediaPlaybackStatusStopped);
                    }
                    _ = ((delegate* unmanaged[Stdcall]<nint, int, int>)GetVtableSlot(_updater, 7))(_updater, MediaPlaybackTypeMusic);

                    if (_musicProps == 0)
                    {
                        nint musicProps = 0;
                        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)GetVtableSlot(_updater, 12))(_updater, &musicProps) >= 0)
                            _musicProps = musicProps;
                    }

                    if (_musicProps == 0)
                        return;

#if DEBUG
                    Debug.WriteLine($"[SMTC-DBG] UpdateMetadata title='{metadata.Title}' cover='{coverFilePath ?? "null"}'");
#endif
                    SetHStringProperty(_musicProps, 7, metadata.Title);
                    SetHStringProperty(_musicProps, 11, metadata.Artist);

                    if (!string.Equals(_lastCoverPath, coverFilePath, StringComparison.Ordinal))
                    {
                        if (!string.IsNullOrEmpty(coverFilePath) && !prepareAttempted && pass == 0)
                        {
                            needsRetryPrepare = true;
                        }
                        else
                        {
                            _lastCoverPath = ApplyThumbnailPayloadLocked(coverFilePath, in prepared) ? coverFilePath : null;
                        }
                    }

                    if (!needsRetryPrepare)
                    {
                        _ = ((delegate* unmanaged[Stdcall]<nint, int>)GetVtableSlot(_updater, 17))(_updater);
                        return;
                    }
                }

                prepared.Dispose();
                prepared = PrepareThumbnailFromFile(coverFilePath!);
                prepareAttempted = true;
            }
        }
        finally
        {
            prepared.Dispose();
        }
    }

    /// <inheritdoc />
    public void UpdateTimeline(in MediaTransportTimeline timeline)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        lock (_comLock)
        {
            if (_smtc2 == 0 || _timelineProps == 0 || Volatile.Read(ref _detached) != 0)
                return;

            long durationTicks = timeline.Duration > TimeSpan.Zero ? timeline.Duration.Ticks : 0;
            long posTicks = Math.Clamp(timeline.Position.Ticks, 0, durationTicks);

#if DEBUG
            Debug.WriteLine($"[SMTC-DBG] UpdateTimeline pos={posTicks} dur={durationTicks}");
#endif
            _ = ((delegate* unmanaged[Stdcall]<nint, long, int>)GetVtableSlot(_timelineProps, 7))(_timelineProps, 0);
            _ = ((delegate* unmanaged[Stdcall]<nint, long, int>)GetVtableSlot(_timelineProps, 9))(_timelineProps, durationTicks);
            _ = ((delegate* unmanaged[Stdcall]<nint, long, int>)GetVtableSlot(_timelineProps, 11))(_timelineProps, 0);
            _ = ((delegate* unmanaged[Stdcall]<nint, long, int>)GetVtableSlot(_timelineProps, 13))(_timelineProps, durationTicks);
            _ = ((delegate* unmanaged[Stdcall]<nint, long, int>)GetVtableSlot(_timelineProps, 15))(_timelineProps, posTicks);

            _ = ((delegate* unmanaged[Stdcall]<nint, nint, int>)GetVtableSlot(_smtc2, 12))(_smtc2, _timelineProps);
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_comLock)
        {
            _hasActiveMetadata = false;
            _lastPlaybackStatus = MediaPlaybackStatusClosed;
            _lastCoverPath = null;
            ReleaseCom(ref _musicProps);

            if (_updater != 0)
            {
                _ = ((delegate* unmanaged[Stdcall]<nint, int>)GetVtableSlot(_updater, 16))(_updater);
                _ = ((delegate* unmanaged[Stdcall]<nint, int>)GetVtableSlot(_updater, 17))(_updater);
            }

            if (_smtc != 0)
            {
                _ = ((delegate* unmanaged[Stdcall]<nint, int, int>)GetVtableSlot(_smtc, 7))(_smtc, MediaPlaybackStatusClosed);
                _ = ((delegate* unmanaged[Stdcall]<nint, byte, int>)GetVtableSlot(_smtc, 11))(_smtc, 0);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        DetachWindow();
    }

    #region Internal COM Helpers

    private void DetachWindowLocked()
    {
        Volatile.Write(ref _detached, 1);
        _hasActiveMetadata = false;
        _lastPlaybackStatus = MediaPlaybackStatusClosed;
        _lastCoverPath = null;

        if (_updater != 0)
        {
            _ = ((delegate* unmanaged[Stdcall]<nint, int>)GetVtableSlot(_updater, 16))(_updater);
            _ = ((delegate* unmanaged[Stdcall]<nint, int>)GetVtableSlot(_updater, 17))(_updater);
        }

        if (_smtc != 0)
        {
            _ = ((delegate* unmanaged[Stdcall]<nint, int, int>)GetVtableSlot(_smtc, 7))(_smtc, MediaPlaybackStatusClosed);
            _ = ((delegate* unmanaged[Stdcall]<nint, byte, int>)GetVtableSlot(_smtc, 11))(_smtc, 0);

            if (_buttonPressedToken != 0)
            {
                _ = ((delegate* unmanaged[Stdcall]<nint, long, int>)GetVtableSlot(_smtc, 33))(_smtc, _buttonPressedToken);
                _buttonPressedToken = 0;
            }
        }

        if (_smtc2 != 0 && _positionChangeToken != 0)
        {
            _ = ((delegate* unmanaged[Stdcall]<nint, long, int>)GetVtableSlot(_smtc2, 14))(_smtc2, _positionChangeToken);
            _positionChangeToken = 0;
        }

        ReleaseCom(ref _buttonPressedHandler);
        ReleaseCom(ref _positionChangeHandler);
        ReleaseCom(ref _musicProps);
        ReleaseCom(ref _updater);
        ReleaseCom(ref _timelineProps);
        ReleaseCom(ref _smtc2);
        ReleaseCom(ref _smtc);
        ReleaseCom(ref _rasRefStatics);
    }

    private void DisableSupport(string reason)
    {
        _isSupported = false;
        Log.Warn($"[SMTC] Disabled: {reason}");
    }

    private readonly struct PreparedThumbnail : IDisposable
    {
        public readonly byte[]? RentedBuffer;
        public readonly int ByteLength;
        public readonly SKData? EncodedJpeg;

        public PreparedThumbnail(byte[]? rentedBuffer, int byteLength, SKData? encodedJpeg)
        {
            RentedBuffer = rentedBuffer;
            ByteLength = byteLength;
            EncodedJpeg = encodedJpeg;
        }

        public bool HasData => (RentedBuffer != null && ByteLength > 0) || (EncodedJpeg != null && EncodedJpeg.Size > 0);

        public void Dispose()
        {
            EncodedJpeg?.Dispose();
            if (RentedBuffer != null)
                ArrayPool<byte>.Shared.Return(RentedBuffer);
        }
    }

    private bool ApplyThumbnailPayloadLocked(string? coverFilePath, in PreparedThumbnail prepared)
    {
        if (_updater == 0)
            return false;

        if (string.IsNullOrEmpty(coverFilePath) || _rasRefStatics == 0)
        {
            _ = ((delegate* unmanaged[Stdcall]<nint, nint, int>)GetVtableSlot(_updater, 11))(_updater, 0);
            return string.IsNullOrEmpty(coverFilePath);
        }

        nint rasRef = CreateStreamReferenceFromPayload(in prepared);
        try
        {
#if DEBUG
            Debug.WriteLine($"[SMTC-DBG] ApplyThumbnailPayloadLocked rasRef=0x{rasRef:X}");
#endif
            _ = ((delegate* unmanaged[Stdcall]<nint, nint, int>)GetVtableSlot(_updater, 11))(_updater, rasRef);
            return rasRef != 0;
        }
        finally
        {
            ReleaseCom(ref rasRef);
        }
    }

    private static PreparedThumbnail PrepareThumbnailFromFile(string filePath)
    {
        try
        {
            using var fs = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: ThumbnailChunkSize,
                options: FileOptions.SequentialScan);

            long len = fs.Length;
            if (len <= 0 || len > MaxThumbnailBytes)
                return default;

            Span<byte> header = stackalloc byte[12];
            int headerRead = 0;
            while (headerRead < header.Length)
            {
                int n = fs.Read(header[headerRead..]);
                if (n <= 0) break;
                headerRead += n;
            }

            if (headerRead == 0)
                return default;

            if (IsNativeSmtcImageHeader(header[..headerRead]))
            {
                int totalLen = (int)len;
                byte[] buffer = ArrayPool<byte>.Shared.Rent(totalLen);
                bool ownershipTransferred = false;
                try
                {
                    header[..headerRead].CopyTo(buffer);
                    int offset = headerRead;
                    while (offset < totalLen)
                    {
                        int n = fs.Read(buffer, offset, totalLen - offset);
                        if (n <= 0) break;
                        offset += n;
                    }

                    ownershipTransferred = true;
                    return new PreparedThumbnail(buffer, offset, null);
                }
                finally
                {
                    if (!ownershipTransferred)
                        ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            fs.Position = 0;
            using var bitmap = SKBitmap.Decode(fs);
            if (bitmap == null)
                return default;

            using var image = SKImage.FromBitmap(bitmap);
            if (image == null)
                return default;

            var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 85);
            if (encoded == null || encoded.Size <= 0 || encoded.Size > MaxThumbnailBytes)
            {
                encoded?.Dispose();
                return default;
            }

            return new PreparedThumbnail(null, 0, encoded);
        }
        catch (Exception ex)
        {
            Log.Debug($"[SMTC] Thumbnail read skipped: {ex.Message}");
            return default;
        }
    }

    private nint CreateStreamReferenceFromPayload(in PreparedThumbnail prepared)
    {
        if (!prepared.HasData || _rasRefStatics == 0)
            return 0;

        nint iStream = 0;
        nint ras = 0;
        nint rasRef = 0;

        try
        {
            if (prepared.EncodedJpeg != null)
            {
                iStream = SHCreateMemStream((byte*)prepared.EncodedJpeg.Data, (uint)prepared.EncodedJpeg.Size);
            }
            else if (prepared.RentedBuffer != null)
            {
                fixed (byte* pBuf = prepared.RentedBuffer)
                {
                    iStream = SHCreateMemStream(pBuf, (uint)prepared.ByteLength);
                }
            }

            if (iStream == 0)
                return 0;

            Guid rasIid = IidRandomAccessStream;
            if (CreateRandomAccessStreamOverStream(iStream, 0, &rasIid, &ras) < 0 || ras == 0)
                return 0;

            if (((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)GetVtableSlot(_rasRefStatics, 8))(
                _rasRefStatics, ras, &rasRef) < 0)
            {
                return 0;
            }

            nint result = rasRef;
            rasRef = 0;
            return result;
        }
        catch (Exception ex)
        {
            Log.Debug($"[SMTC] Thumbnail stream skipped: {ex.Message}");
            return 0;
        }
        finally
        {
            ReleaseCom(ref rasRef);
            ReleaseCom(ref ras);
            ReleaseCom(ref iStream);
        }
    }

    private static bool IsNativeSmtcImageHeader(ReadOnlySpan<byte> header)
    {
        // JPEG
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            return true;

        // PNG
        if (header.Length >= 8
            && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
            && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
            return true;

        // BMP
        return header.Length >= 2 && header[0] == 0x42 && header[1] == 0x4D;
    }

    private static nint ActivateFactory(string className, Guid iid)
    {
        fixed (char* pClass = className)
        {
            HStringHeader header = default;
            nint hstr = 0;
            if (WindowsCreateStringReference(pClass, (uint)className.Length, &header, &hstr) < 0)
                return 0;

            nint factory = 0;
            int hr = RoGetActivationFactory(hstr, &iid, &factory);
            return hr >= 0 ? factory : 0;
        }
    }

    private static nint ActivateTimelineProperties()
    {
        const string className = "Windows.Media.SystemMediaTransportControlsTimelineProperties";
        nint inspectable = 0;
        fixed (char* pClass = className)
        {
            HStringHeader header = default;
            nint hstr = 0;
            if (WindowsCreateStringReference(pClass, (uint)className.Length, &header, &hstr) < 0)
                return 0;

            if (RoActivateInstance(hstr, &inspectable) < 0 || inspectable == 0)
                return 0;
        }

        try
        {
            return QueryInterface(inspectable, IidTimelineProps);
        }
        finally
        {
            ReleaseCom(ref inspectable);
        }
    }

    private static nint QueryInterface(nint unknown, Guid iid)
    {
        if (unknown == 0)
            return 0;

        nint target = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)GetVtableSlot(unknown, 0))(
            unknown, &iid, &target);

        return hr >= 0 ? target : 0;
    }

#if DEBUG
    private static void DumpIidsDebug(nint inspectable, string label)
    {
        if (inspectable == 0)
            return;

        uint count = 0;
        Guid* iids = null;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint*, Guid**, int>)GetVtableSlot(inspectable, 3))(
            inspectable, &count, &iids);

        if (hr < 0 || iids == null)
        {
            Debug.WriteLine($"[SMTC-DBG] {label}.GetIids failed: 0x{hr:X8}");
            return;
        }

        try
        {
            for (uint i = 0; i < count; i++)
                Debug.WriteLine($"[SMTC-DBG] {label} iid[{i}] = {iids[i]:B}");
        }
        finally
        {
            Marshal.FreeCoTaskMem((nint)iids);
        }
    }
#endif

    private static void SetHStringProperty(nint comPtr, int vtableSlot, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            _ = ((delegate* unmanaged[Stdcall]<nint, nint, int>)GetVtableSlot(comPtr, vtableSlot))(comPtr, 0);
            return;
        }

        fixed (char* pStr = value)
        {
            HStringHeader header = default;
            nint hstr = 0;
            if (WindowsCreateStringReference(pStr, (uint)value.Length, &header, &hstr) >= 0)
                _ = ((delegate* unmanaged[Stdcall]<nint, nint, int>)GetVtableSlot(comPtr, vtableSlot))(comPtr, hstr);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nint GetVtableSlot(nint comPtr, int slot)
    {
        nint* vtable = *(nint**)comPtr;
        return vtable[slot];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReleaseCom(ref nint comPtr)
    {
        nint ptr = comPtr;
        if (ptr == 0)
            return;

        comPtr = 0;
        _ = ((delegate* unmanaged[Stdcall]<nint, uint>)GetVtableSlot(ptr, 2))(ptr);
    }

    #endregion

    #region Unmanaged Agile WinRT Event Handlers

    private nint CreateEventHandler(nint* vtable, Guid expectedIid)
    {
        var block = (EventHandlerBlock*)NativeMemory.AllocZeroed((nuint)sizeof(EventHandlerBlock));
        block->Vtable = vtable;
        block->RefCount = 1;
        block->GcHandle = GCHandle.ToIntPtr(GCHandle.Alloc(this));
        block->ExpectedIid = expectedIid;
        return (nint)block;
    }

    private static nint* AllocateHandlerVtable(delegate* unmanaged[Stdcall]<nint, nint, nint, int> invokeFn)
    {
        nint* vtable = (nint*)NativeMemory.Alloc((nuint)(4 * sizeof(nint)));
        vtable[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&HandlerQueryInterface;
        vtable[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&HandlerAddRef;
        vtable[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&HandlerRelease;
        vtable[3] = (nint)invokeFn;
        return vtable;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int HandlerQueryInterface(nint thisPtr, Guid* riid, nint* ppv)
    {
        if (ppv == null)
            return E_NOINTERFACE;

        *ppv = 0;
        if (thisPtr == 0 || riid == null)
            return E_NOINTERFACE;

        var block = (EventHandlerBlock*)thisPtr;
        Guid requested = *riid;

        if (requested == IidIUnknown || requested == IidIAgileObject || requested == block->ExpectedIid)
        {
            *ppv = thisPtr;
            Interlocked.Increment(ref block->RefCount);
            return S_OK;
        }

        return E_NOINTERFACE;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint HandlerAddRef(nint thisPtr)
    {
        if (thisPtr == 0)
            return 0;

        var block = (EventHandlerBlock*)thisPtr;
        return (uint)Interlocked.Increment(ref block->RefCount);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint HandlerRelease(nint thisPtr)
    {
        if (thisPtr == 0)
            return 0;

        var block = (EventHandlerBlock*)thisPtr;
        int remaining = Interlocked.Decrement(ref block->RefCount);
        if (remaining <= 0)
        {
            if (block->GcHandle != 0)
            {
                GCHandle.FromIntPtr(block->GcHandle).Free();
                block->GcHandle = 0;
            }
            NativeMemory.Free(block);
            return 0;
        }

        return (uint)remaining;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnButtonPressedInvoke(nint thisPtr, nint sender, nint args)
    {
        if (thisPtr == 0 || args == 0)
            return S_OK;

        try
        {
            var owner = TryGetLiveOwner((EventHandlerBlock*)thisPtr);
            if (owner == null)
                return S_OK;

            int button = -1;
            if (((delegate* unmanaged[Stdcall]<nint, int*, int>)GetVtableSlot(args, 6))(args, &button) < 0)
                return S_OK;

            MediaTransportCommand? mapped = button switch
            {
                0 => MediaTransportCommand.Play,
                1 => MediaTransportCommand.Pause,
                2 => MediaTransportCommand.Stop,
                4 => MediaTransportCommand.FastForward,
                5 => MediaTransportCommand.Rewind,
                6 => MediaTransportCommand.Next,
                7 => MediaTransportCommand.Previous,
                _ => null
            };

            if (mapped is { } cmd)
                owner.CommandReceived?.Invoke(cmd, TimeSpan.Zero);
        }
        catch
        {
            // Prevent managed exceptions from crossing the UnmanagedCallersOnly boundary under Native AOT.
        }

        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnPositionChangeInvoke(nint thisPtr, nint sender, nint args)
    {
        if (thisPtr == 0 || args == 0)
            return S_OK;

        try
        {
            var owner = TryGetLiveOwner((EventHandlerBlock*)thisPtr);
            if (owner == null)
                return S_OK;

            long ticks = 0;
            if (((delegate* unmanaged[Stdcall]<nint, long*, int>)GetVtableSlot(args, 6))(args, &ticks) >= 0 && ticks >= 0)
                owner.CommandReceived?.Invoke(MediaTransportCommand.Seek, TimeSpan.FromTicks(ticks));
        }
        catch
        {
            // Prevent managed exceptions from crossing the UnmanagedCallersOnly boundary under Native AOT.
        }

        return S_OK;
    }

    private static WindowsSystemMediaTransport? TryGetLiveOwner(EventHandlerBlock* block)
    {
        if (block->GcHandle == 0)
            return null;

        var handle = GCHandle.FromIntPtr(block->GcHandle);
        if (handle.Target is not WindowsSystemMediaTransport owner)
            return null;

        if (Volatile.Read(ref owner._disposed) != 0 || Volatile.Read(ref owner._detached) != 0)
            return null;

        return owner;
    }

    private static Guid ComputeTypedEventHandlerIid(string argsRuntimeClass, Guid argsDefaultInterfaceIid)
    {
        ReadOnlySpan<byte> nsBytes =
        [
            0x11, 0xf4, 0x7a, 0xd5,
            0x7b, 0x73,
            0x42, 0xc0,
            0xab, 0xae,
            0x87, 0x8b, 0x1e, 0x16, 0xad, 0xee
        ];

        string sig = $"pinterface({{9de1c534-6ae1-11e0-84e1-18a905bcc53f}};rc(Windows.Media.SystemMediaTransportControls;{{{IidSmtc:D}}});rc({argsRuntimeClass};{{{argsDefaultInterfaceIid:D}}}))";
        int utf8Bytes = Encoding.UTF8.GetByteCount(sig);
        int totalLen = 16 + utf8Bytes;

        byte[]? rented = totalLen > 512 ? ArrayPool<byte>.Shared.Rent(totalLen) : null;
        try
        {
            Span<byte> buffer = rented != null ? rented.AsSpan(0, totalLen) : stackalloc byte[totalLen];
            nsBytes.CopyTo(buffer);
            int written = Encoding.UTF8.GetBytes(sig, buffer[16..]);

            Span<byte> hash = stackalloc byte[20];
            SHA1.HashData(buffer[..(16 + written)], hash);
            hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
            hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
            return new Guid(hash[..16], bigEndian: true);
        }
        finally
        {
            if (rented != null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    #endregion
}