using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LMP.Core.Exceptions;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// Standard-compliant WebM/Matroska (EBML) container parser for extracting OPUS packets.
/// Optimized for zero-allocation streaming read, featuring desync protection and EBML padding skip.
/// </summary>
/// <remarks>
/// <para>Complies with RFC 8794 (EBML) and RFC 9559 (Matroska) specifications.</para>
/// </remarks>
internal sealed class WebMParser : IDisposable
{
    //
    // EBML & Matroska Element IDs
    //
    private const uint EBML_ID = 0x1A45DFA3;
    private const uint SEGMENT_ID = 0x18538067;
    private const uint CLUSTER_ID = 0x1F43B675;
    private const uint SIMPLE_BLOCK_ID = 0xA3;
    private const uint BLOCK_GROUP_ID = 0xA0;
    private const uint BLOCK_ID = 0xA1;
    private const uint TIMECODE_ID = 0xE7;
    private const uint INFO_ID = 0x1549A966;
    private const uint DURATION_ID = 0x4489;
    private const uint TIMECODE_SCALE_ID = 0x2AD7B1;
    private const uint TRACKS_ID = 0x1654AE6B;
    private const uint TRACK_ENTRY_ID = 0xAE;
    private const uint TRACK_NUMBER_ID = 0xD7;
    private const uint TRACK_TYPE_ID = 0x83;
    private const uint CODEC_PRIVATE_ID = 0x63A2;
    private const uint AUDIO_ID = 0xE1;
    private const uint SAMPLING_FREQUENCY_ID = 0xB5;
    private const uint CHANNELS_ID = 0x9F;
    private const uint CUES_ID = 0x1C53BB6B;
    private const uint CUE_POINT_ID = 0xBB;
    private const uint CUE_TIME_ID = 0xB3;
    private const uint CUE_TRACK_POSITIONS_ID = 0xB7;
    private const uint CUE_CLUSTER_POSITION_ID = 0xF1;

    //
    // Lacing and EBML constants
    //
    private const int LACING_NONE = 0;
    private const int LACING_XIPH = 1;
    private const int LACING_FIXED = 2;
    private const int LACING_EBML = 3;

    /// <summary>EBML VINT marker indicating unknown size.</summary>
    public const long UNKNOWN_SIZE = -1;

    private const long DEFAULT_TIMECODE_SCALE = 1_000_000; // 1ms in nanoseconds
    private const int ReadBufferSize = 64 * 1024;
    private const int MaxResyncScanBytes = 256 * 1024;

    private readonly Stream _stream;
    private readonly byte[] _readBuffer;
    private int _bufPos;
    private int _bufLen;

    private readonly List<CuePoint> _cuePoints = [];

    /// <summary>
    /// Queue for unpacked frames from Laced blocks.
    /// Guarantees extraction of all audio frames from a single SimpleBlock without data loss.
    /// </summary>
    private readonly Queue<AudioBlock> _lacedFrames = new();
    private readonly int[] _laceFrameSizes = new int[256];
    private readonly Stack<PooledFrameOwner> _ownerPool = new(16);

    private long _segmentOffset;
    private long _currentClusterTimecode;
    private long _timecodeScale = DEFAULT_TIMECODE_SCALE;
    private long _duration;
    private int _audioTrackNumber = 1;
    private bool _headersParsed;
    private bool _requiresResync;

    /// <summary>Byte position of the start of content in the last recognized Cluster.</summary>
    public long LastClusterBytePosition { get; private set; }

    /// <summary>Flag indicating successful resynchronization (used for diagnosing truncated files).</summary>
    public bool ResyncOccurred { get; private set; }

    /// <summary>Media duration in milliseconds.</summary>
    public long DurationMs => _duration * _timecodeScale / 1_000_000;

    /// <summary>Collection of seek points.</summary>
    public IReadOnlyList<CuePoint> CuePoints => _cuePoints;

    /// <summary>Decoder configuration (CodecPrivate).</summary>
    public byte[]? CodecPrivate { get; private set; }

    /// <summary>Audio sampling rate.</summary>
    public int SampleRate { get; private set; }

    /// <summary>Number of channels.</summary>
    public int Channels { get; private set; } = 2;

    /// <summary>
    /// Reusable <see cref="IMemoryOwner{T}"/> wrapper over <see cref="ArrayPool{T}.Shared"/>.
    /// Eliminates the 24-byte allocation of the standard <c>ArrayMemoryPoolBuffer</c> per audio frame.
    /// </summary>
    private sealed class PooledFrameOwner(Stack<PooledFrameOwner> pool) : IMemoryOwner<byte>
    {
        private byte[]? _array;

        public PooledFrameOwner Initialize(int minSize)
        {
            _array = ArrayPool<byte>.Shared.Rent(minSize);
            return this;
        }

        public Memory<byte> Memory => _array.AsMemory();

        public void Dispose()
        {
            var arr = _array;
            if (arr != null)
            {
                _array = null;
                ArrayPool<byte>.Shared.Return(arr);
                if (pool.Count < 32)
                    pool.Push(this);
            }
        }
    }

    private PooledFrameOwner RentFrameOwner(int size)
    {
        return (_ownerPool.TryPop(out var owner) ? owner : new PooledFrameOwner(_ownerPool))
            .Initialize(size);
    }


    /// <summary>Represents a seek point (Cue).</summary>
    public readonly record struct CuePoint(long TimeMs, long ClusterOffset);

    /// <summary>Represents an extracted audio block.</summary>
    public readonly record struct AudioBlock(
        IMemoryOwner<byte> Owner,
        int Length,
        long TimestampMs,
        bool IsKeyFrame
    );

    /// <summary>
    /// Initializes a new instance of the <see cref="WebMParser"/> class.
    /// </summary>
    /// <param name="stream">Input audio stream.</param>
    /// <param name="bufferSize">Intermediate read buffer size.</param>
    public WebMParser(Stream stream, int bufferSize = ReadBufferSize)
    {
        _stream = stream;
        _readBuffer = new byte[bufferSize];
    }

    #region Buffered Reading (Zero-Alloc)

    /// <summary>
    /// Checks for the availability of <paramref name="needed"/> bytes in the buffer synchronously.
    /// If data is insufficient, falls back to asynchronous bulk reading.
    /// </summary>
    private ValueTask<bool> EnsureBufferedAsync(int needed, CancellationToken ct)
    {
        if (_bufLen - _bufPos >= needed)
            return new ValueTask<bool>(true);

        return EnsureBufferedSlowAsync(needed, ct);
    }

    private async ValueTask<bool> EnsureBufferedSlowAsync(int needed, CancellationToken ct)
    {
        int available = _bufLen - _bufPos;
        if (available > 0 && _bufPos > 0)
            Buffer.BlockCopy(_readBuffer, _bufPos, _readBuffer, 0, available);

        _bufPos = 0;
        _bufLen = available;

        while (_bufLen < needed)
        {
            int read = await _stream.ReadAsync(
                _readBuffer.AsMemory(_bufLen, _readBuffer.Length - _bufLen), ct).ConfigureAwait(false);
            if (read == 0) return _bufLen >= needed;
            _bufLen += read;
        }
        return true;
    }

    private async ValueTask FillBufferAsync(CancellationToken ct)
    {
        int available = _bufLen - _bufPos;
        if (available > 0 && _bufPos > 0)
            Buffer.BlockCopy(_readBuffer, _bufPos, _readBuffer, 0, available);

        _bufPos = 0;
        _bufLen = available;

        int toRead = _readBuffer.Length - _bufLen;
        if (toRead > 0)
        {
            int read = await _stream.ReadAsync(_readBuffer.AsMemory(_bufLen, toRead), ct).ConfigureAwait(false);
            _bufLen += read;
        }
    }

    /// <summary>
    /// Tries to synchronously refill the internal buffer if the input stream backs data in memory
    /// (<see cref="MemoryStream"/>, <see cref="FileStream"/>, or <c>AsyncCachingReadStream</c> on RAM hit).
    /// Eliminates async state machine allocation during buffer reloading.
    /// </summary>
    private bool TryFillBufferSync()
    {
        int available = _bufLen - _bufPos;
        if (available > 0 && _bufPos > 0)
            Buffer.BlockCopy(_readBuffer, _bufPos, _readBuffer, 0, available);

        _bufPos = 0;
        _bufLen = available;

        int toRead = _readBuffer.Length - _bufLen;
        if (toRead <= 0) return _bufLen > 0;

        if (_stream is MemoryStream ms)
        {
            int read = ms.Read(_readBuffer.AsSpan(_bufLen, toRead));
            _bufLen += read;
            return read > 0;
        }

        if (_stream is Sources.CachingStreamSource.AsyncCachingReadStream cachingStream)
        {
            int read = cachingStream.TryReadFromRamSync(_readBuffer.AsSpan(_bufLen, toRead));
            _bufLen += read;
            return read > 0;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int BufferedReadByte()
    {
        int pos = _bufPos;
        if ((uint)pos >= (uint)_bufLen) return -1;
        _bufPos = pos + 1;
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_readBuffer), pos);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<byte> BufferedReadSpan(int count)
    {
        int pos = _bufPos;
        _bufPos = pos + count;
        return MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_readBuffer), pos),
            count);
    }

    /// <summary>
    /// Reads an exact number of bytes. If data is already in the buffer, copies synchronously without state machine allocation.
    /// </summary>
    private ValueTask ReadExactAsync(Memory<byte> buffer, CancellationToken ct)
    {
        int remaining = buffer.Length;
        if (_bufLen - _bufPos >= remaining)
        {
            _readBuffer.AsSpan(_bufPos, remaining).CopyTo(buffer.Span);
            _bufPos += remaining;
            return ValueTask.CompletedTask;
        }

        return ReadExactSlowAsync(buffer, ct);
    }

    private async ValueTask ReadExactSlowAsync(Memory<byte> buffer, CancellationToken ct)
    {
        int offset = 0;
        int remaining = buffer.Length;

        int buffered = Math.Min(remaining, _bufLen - _bufPos);
        if (buffered > 0)
        {
            _readBuffer.AsSpan(_bufPos, buffered).CopyTo(buffer.Span[..buffered]);
            _bufPos += buffered;
            offset += buffered;
            remaining -= buffered;
        }

        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();

            if (remaining < _readBuffer.Length)
            {
                await FillBufferAsync(ct).ConfigureAwait(false);
                int fromBuf = Math.Min(remaining, _bufLen - _bufPos);
                if (fromBuf == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new EndOfStreamException("Unexpected EOF while reading exact payload.");
                }
                _readBuffer.AsSpan(_bufPos, fromBuf).CopyTo(buffer.Span.Slice(offset, fromBuf));
                _bufPos += fromBuf;
                offset += fromBuf;
                remaining -= fromBuf;
            }
            else
            {
                int read = await _stream.ReadAsync(buffer[offset..], ct).ConfigureAwait(false);
                if (read == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new EndOfStreamException("Unexpected EOF while reading exact payload.");
                }
                offset += read;
                remaining -= read;
            }
        }
    }

    private ValueTask SkipBytesAsync(long count, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        int buffered = (int)Math.Min(count, _bufLen - _bufPos);
        _bufPos += buffered;
        count -= buffered;

        if (count <= 0)
            return ValueTask.CompletedTask;

        if (_stream.CanSeek)
        {
            _stream.Position += count;
            _bufPos = 0;
            _bufLen = 0;
            return ValueTask.CompletedTask;
        }

        return SkipBytesSlowAsync(count, ct);
    }

    private async ValueTask SkipBytesSlowAsync(long count, CancellationToken ct)
    {
        while (count > 0)
        {
            await FillBufferAsync(ct).ConfigureAwait(false);
            int skip = (int)Math.Min(count, _bufLen - _bufPos);
            if (skip == 0) throw new EndOfStreamException($"Unexpected EOF skipping {count} bytes.");
            _bufPos += skip;
            count -= skip;
        }
    }

    private long StreamPositionWithBuffer() => _stream.Position - (_bufLen - _bufPos);

    #endregion

    #region Header Parsing

    /// <summary>
    /// Parses container headers up to the start of the first Cluster.
    /// </summary>
    public async ValueTask<bool> ParseHeadersAsync(CancellationToken ct = default)
    {
        if (_headersParsed) return true;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                long elementStart = StreamPositionWithBuffer();
                var (id, size) = await ReadElementHeaderAsync(ct).ConfigureAwait(false);

                if (id == 0) return false;

                switch (id)
                {
                    case INFO_ID:
                        await ParseInfoAsync(size, ct).ConfigureAwait(false);
                        break;

                    case TRACKS_ID:
                        await ParseTracksAsync(size, ct).ConfigureAwait(false);
                        break;

                    case CUES_ID:
                        await ParseCuesAsync(size, ct).ConfigureAwait(false);
                        break;

                    case CLUSTER_ID:
                        // Audio data found. Rolling back so ReadNextBlockAsync enters the cluster itself.
                        if (_stream.CanSeek)
                        {
                            _stream.Position = elementStart;
                            _bufPos = 0;
                            _bufLen = 0;
                        }
                        _headersParsed = true;
                        return true;

                    case EBML_ID:
                    case SEGMENT_ID:
                        if (id == SEGMENT_ID) _segmentOffset = StreamPositionWithBuffer();
                        continue;

                    default:
                        if (size != UNKNOWN_SIZE && size > 0)
                            await SkipBytesAsync(size, ct).ConfigureAwait(false);
                        break;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private async ValueTask ParseInfoAsync(long size, CancellationToken ct)
    {
        long endPosition = size == UNKNOWN_SIZE ? long.MaxValue : StreamPositionWithBuffer() + size;

        while (StreamPositionWithBuffer() < endPosition)
        {
            var (id, elementSize) = await ReadElementHeaderAsync(ct).ConfigureAwait(false);
            if (id == 0) break;

            switch (id)
            {
                case TIMECODE_SCALE_ID:
                    if (!await EnsureBufferedAsync((int)elementSize, ct).ConfigureAwait(false)) return;
                    _timecodeScale = ReadUnsignedInt(BufferedReadSpan((int)elementSize));
                    break;

                case DURATION_ID:
                    if (!await EnsureBufferedAsync((int)elementSize, ct).ConfigureAwait(false)) return;
                    _duration = (long)ReadFloat(BufferedReadSpan((int)elementSize));
                    break;

                default:
                    if (elementSize != UNKNOWN_SIZE) await SkipBytesAsync(elementSize, ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async ValueTask ParseTracksAsync(long size, CancellationToken ct)
    {
        long endPosition = size == UNKNOWN_SIZE ? long.MaxValue : StreamPositionWithBuffer() + size;

        while (StreamPositionWithBuffer() < endPosition)
        {
            var (id, elementSize) = await ReadElementHeaderAsync(ct).ConfigureAwait(false);
            if (id == 0) break;

            if (id == TRACK_ENTRY_ID)
                await ParseTrackEntryAsync(elementSize, ct).ConfigureAwait(false);
            else if (elementSize != UNKNOWN_SIZE)
                await SkipBytesAsync(elementSize, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask ParseTrackEntryAsync(long size, CancellationToken ct)
    {
        long endPosition = size == UNKNOWN_SIZE ? long.MaxValue : StreamPositionWithBuffer() + size;
        int trackNumber = 0;
        int trackType = 0;
        byte[]? trackCodecPrivate = null;

        while (StreamPositionWithBuffer() < endPosition)
        {
            var (id, elementSize) = await ReadElementHeaderAsync(ct).ConfigureAwait(false);
            if (id == 0) break;

            switch (id)
            {
                case TRACK_NUMBER_ID:
                    if (!await EnsureBufferedAsync((int)elementSize, ct).ConfigureAwait(false)) return;
                    trackNumber = (int)ReadUnsignedInt(BufferedReadSpan((int)elementSize));
                    break;

                case TRACK_TYPE_ID:
                    if (!await EnsureBufferedAsync((int)elementSize, ct).ConfigureAwait(false)) return;
                    trackType = (int)ReadUnsignedInt(BufferedReadSpan((int)elementSize));
                    break;

                case CODEC_PRIVATE_ID:
                    trackCodecPrivate = new byte[elementSize];
                    await ReadExactAsync(trackCodecPrivate, ct).ConfigureAwait(false);
                    break;

                case AUDIO_ID:
                    await ParseAudioSettingsAsync(elementSize, ct).ConfigureAwait(false);
                    break;

                default:
                    if (elementSize != UNKNOWN_SIZE) await SkipBytesAsync(elementSize, ct).ConfigureAwait(false);
                    break;
            }
        }

        if (trackType == 2 && trackNumber > 0)
        {
            _audioTrackNumber = trackNumber;
            if (trackCodecPrivate != null)
                CodecPrivate = trackCodecPrivate;
        }
    }

    private async ValueTask ParseAudioSettingsAsync(long size, CancellationToken ct)
    {
        long endPosition = size == UNKNOWN_SIZE ? long.MaxValue : StreamPositionWithBuffer() + size;

        while (StreamPositionWithBuffer() < endPosition)
        {
            var (id, elementSize) = await ReadElementHeaderAsync(ct).ConfigureAwait(false);
            if (id == 0) break;

            switch (id)
            {
                case SAMPLING_FREQUENCY_ID:
                    if (!await EnsureBufferedAsync((int)elementSize, ct).ConfigureAwait(false)) return;
                    SampleRate = (int)ReadFloat(BufferedReadSpan((int)elementSize));
                    break;

                case CHANNELS_ID:
                    if (!await EnsureBufferedAsync((int)elementSize, ct).ConfigureAwait(false)) return;
                    Channels = (int)ReadUnsignedInt(BufferedReadSpan((int)elementSize));
                    break;

                default:
                    if (elementSize != UNKNOWN_SIZE) await SkipBytesAsync(elementSize, ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async ValueTask ParseCuesAsync(long size, CancellationToken ct)
    {
        long endPosition = size == UNKNOWN_SIZE ? long.MaxValue : StreamPositionWithBuffer() + size;

        while (StreamPositionWithBuffer() < endPosition)
        {
            var (id, elementSize) = await ReadElementHeaderAsync(ct).ConfigureAwait(false);
            if (id == 0) break;

            if (id == CUE_POINT_ID)
            {
                var cuePoint = await ParseCuePointAsync(elementSize, ct).ConfigureAwait(false);
                if (cuePoint.HasValue) _cuePoints.Add(cuePoint.Value);
            }
            else if (elementSize != UNKNOWN_SIZE)
            {
                await SkipBytesAsync(elementSize, ct).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<CuePoint?> ParseCuePointAsync(long size, CancellationToken ct)
    {
        long endPosition = size == UNKNOWN_SIZE ? long.MaxValue : StreamPositionWithBuffer() + size;
        long time = 0;
        long clusterPosition = 0;

        while (StreamPositionWithBuffer() < endPosition)
        {
            var (id, elementSize) = await ReadElementHeaderAsync(ct).ConfigureAwait(false);
            if (id == 0) break;

            switch (id)
            {
                case CUE_TIME_ID:
                    if (!await EnsureBufferedAsync((int)elementSize, ct).ConfigureAwait(false)) return null;
                    time = ReadUnsignedInt(BufferedReadSpan((int)elementSize));
                    break;

                case CUE_TRACK_POSITIONS_ID:
                    long posEnd = StreamPositionWithBuffer() + elementSize;
                    while (StreamPositionWithBuffer() < posEnd)
                    {
                        var (posId, posSize) = await ReadElementHeaderAsync(ct).ConfigureAwait(false);
                        if (posId == CUE_CLUSTER_POSITION_ID)
                        {
                            if (!await EnsureBufferedAsync((int)posSize, ct).ConfigureAwait(false)) return null;
                            clusterPosition = ReadUnsignedInt(BufferedReadSpan((int)posSize));
                        }
                        else if (posSize != UNKNOWN_SIZE)
                            await SkipBytesAsync(posSize, ct).ConfigureAwait(false);
                    }
                    break;

                default:
                    if (elementSize != UNKNOWN_SIZE) await SkipBytesAsync(elementSize, ct).ConfigureAwait(false);
                    break;
            }
        }
        return new CuePoint(time * _timecodeScale / 1_000_000, clusterPosition);
    }

    #endregion

    #region Block Reading (Streaming Flat-Loop)

    /// <summary>
    /// Resets parser state and prepares it to scan for a new sync marker.
    /// </summary>
    public void RequireResync()
    {
        ResyncOccurred = true;
        _requiresResync = true;
        _bufPos = 0;
        _bufLen = 0;

        while (_lacedFrames.TryDequeue(out var frame))
            frame.Owner.Dispose();
    }

    /// <summary>
    /// Reads the next audio block. Uses a synchronous fast-path when data is available in the internal buffer,
    /// completely eliminating async state machine allocations in the hot decoding loop.
    /// </summary>
    public ValueTask<AudioBlock?> ReadNextBlockAsync(CancellationToken ct = default)
    {
        if (!_requiresResync)
        {
            if (_lacedFrames.TryDequeue(out var queuedFrame))
                return new ValueTask<AudioBlock?>(queuedFrame);

            if (TryReadNextBlockFromBufferSync(ct, out var syncBlock))
                return new ValueTask<AudioBlock?>(syncBlock);

            if (TryFillBufferSync() && TryReadNextBlockFromBufferSync(ct, out syncBlock))
                return new ValueTask<AudioBlock?>(syncBlock);
        }

        return ReadNextBlockSlowAsync(ct);
    }

    /// <summary>
    /// Synchronous fast-path for parsing Cluster/SimpleBlock elements from an already filled <see cref="_readBuffer"/>.
    /// Performs zero heap allocations (even in Debug builds).
    /// </summary>
    private bool TryReadNextBlockFromBufferSync(CancellationToken ct, out AudioBlock? result)
    {
        result = null;

        if (_lacedFrames.TryDequeue(out var initialFrame))
        {
            result = initialFrame;
            return true;
        }

        int errorKind = 0;
        long errorValue = 0;

        while (!ct.IsCancellationRequested)
        {
            int savedPos = _bufPos;
            int headerStatus = TryReadElementHeaderSync(out uint id, out long size, out int badVIntLen);
            if (headerStatus <= 0)
            {
                if (headerStatus < 0)
                {
                    errorKind = headerStatus;
                    errorValue = badVIntLen;
                    break;
                }

                _bufPos = savedPos;
                return false;
            }

            switch (id)
            {
                case CLUSTER_ID:
                    LastClusterBytePosition = StreamPositionWithBuffer();
                    continue;

                case TIMECODE_ID:
                    if ((ulong)size > int.MaxValue)
                    {
                        errorKind = 1;
                        errorValue = size;
                        break;
                    }
                    int timecodeSize = (int)size;
                    if (_bufLen - _bufPos < timecodeSize)
                    {
                        _bufPos = savedPos;
                        return false;
                    }
                    _currentClusterTimecode = ReadUnsignedInt(BufferedReadSpan(timecodeSize));
                    continue;

                case SIMPLE_BLOCK_ID:
                case BLOCK_ID:
                    if ((ulong)size > int.MaxValue)
                    {
                        errorKind = 2;
                        errorValue = size;
                        break;
                    }
                    int blockSize = (int)size;
                    if (_bufLen - _bufPos < blockSize)
                    {
                        _bufPos = savedPos;
                        return false;
                    }
                    ParseBlockPayloadSync(blockSize, id == SIMPLE_BLOCK_ID);
                    if (_lacedFrames.TryDequeue(out var lacedFrame))
                    {
                        result = lacedFrame;
                        return true;
                    }
                    continue;

                case BLOCK_GROUP_ID:
                    continue;

                default:
                    if (size != UNKNOWN_SIZE && size > 0)
                    {
                        if (_bufLen - _bufPos < size)
                        {
                            _bufPos = savedPos;
                            return false;
                        }
                        _bufPos += (int)size;
                    }
                    continue;
            }

            if (errorKind != 0)
                break;
        }

        if (errorKind != 0)
        {
            if (errorKind == -1)
                ThrowInvalidIdVIntLength((int)errorValue);
            if (errorKind == -2)
                ThrowInvalidSizeVIntLength((int)errorValue);
            ThrowInvalidElementSize(errorValue, errorKind == 1 ? "Cluster Timecode" : "Block");
        }

        return false;
    }

    /// <summary>
    /// Tries to synchronously read the EBML element header from the buffer without async calls or bounds checks.
    /// Returns: <c>1</c> — success, <c>0</c> — insufficient data in buffer, <c>-1</c> — invalid ID VINT, <c>-2</c> — invalid Size VINT.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int TryReadElementHeaderSync(out uint id, out long size, out int invalidVIntLen)
    {
        id = 0;
        size = 0;
        invalidVIntLen = 0;

        int pos = _bufPos;
        int len = _bufLen;
        ref byte bufRef = ref MemoryMarshal.GetArrayDataReference(_readBuffer);

        while (true)
        {
            if (pos >= len)
            {
                _bufPos = pos;
                return 0;
            }
            byte b = Unsafe.Add(ref bufRef, pos);
            if (b != 0x00) break;
            pos++;
        }

        byte firstIdByte = Unsafe.Add(ref bufRef, pos++);
        int idLength = GetVIntLength(firstIdByte);
        if ((uint)(idLength - 1) > 3u)
        {
            invalidVIntLen = idLength;
            return -1;
        }

        if (len - pos < idLength - 1)
            return 0;

        uint parsedId = firstIdByte;
        for (int i = 1; i < idLength; i++)
            parsedId = (parsedId << 8) | Unsafe.Add(ref bufRef, pos++);

        if (pos >= len)
            return 0;

        byte firstSizeByte = Unsafe.Add(ref bufRef, pos++);
        int sizeLength = GetVIntLength(firstSizeByte);
        if ((uint)(sizeLength - 1) > 7u)
        {
            invalidVIntLen = sizeLength;
            return -2;
        }

        if (len - pos < sizeLength - 1)
            return 0;

        long parsedSize = firstSizeByte & (0xFF >> sizeLength);
        for (int i = 1; i < sizeLength; i++)
            parsedSize = (parsedSize << 8) | Unsafe.Add(ref bufRef, pos++);

        _bufPos = pos;

        long maxVal = (1L << (7 * sizeLength)) - 1;
        if (parsedSize == maxVal) parsedSize = UNKNOWN_SIZE;

        id = parsedId;
        size = parsedSize;
        return 1;
    }

    /// <summary>
    /// Synchronously parses the contents of SimpleBlock/Block when the entire block is guaranteed to reside in <see cref="_readBuffer"/>.
    /// </summary>
    private void ParseBlockPayloadSync(int payloadSize, bool isSimpleBlock)
    {
        int blockEndBufPos = _bufPos + payloadSize;

        int trackByte = BufferedReadByte();
        int trackLen = GetVIntLength((byte)trackByte);

        long trackNum;
        if (trackLen == 1)
        {
            trackNum = trackByte & 0x7F;
        }
        else
        {
            if ((uint)(trackLen - 1) > 7u)
                ThrowInvalidTrackNumberVInt(trackLen);

            Span<byte> trackBytes = stackalloc byte[8];
            trackBytes[0] = (byte)trackByte;
            BufferedReadSpan(trackLen - 1).CopyTo(trackBytes.Slice(1, trackLen - 1));
            trackNum = ReadVInt(trackBytes[..trackLen]);
        }

        short timecodeOffset = BinaryPrimitives.ReadInt16BigEndian(BufferedReadSpan(2));
        int flags = BufferedReadByte();

        if (_bufPos > blockEndBufPos)
            ThrowBlockHeaderExceeded();

        int remainingPayload = blockEndBufPos - _bufPos;
        if (trackNum != _audioTrackNumber)
        {
            _bufPos = blockEndBufPos;
            return;
        }

        bool isKeyFrame = isSimpleBlock && (flags & 0x80) != 0;
        int lacingType = (flags >> 1) & 0x03;

        if (remainingPayload <= 0) return;

        long timestampMs = (_currentClusterTimecode + timecodeOffset) * _timecodeScale / 1_000_000;

        if (lacingType == LACING_NONE)
        {
            var mem = RentFrameOwner(remainingPayload);
            BufferedReadSpan(remainingPayload).CopyTo(mem.Memory.Span[..remainingPayload]);
            _lacedFrames.Enqueue(new AudioBlock(mem, remainingPayload, timestampMs, isKeyFrame));
            return;
        }

        ParseLacingSync(blockEndBufPos, lacingType, timestampMs, isKeyFrame);
    }

    /// <summary>
    /// Synchronously unpacks Laced frames (Xiph, EBML, Fixed) from <see cref="_readBuffer"/> without array allocations.
    /// </summary>
    private void ParseLacingSync(int blockEndBufPos, int lacing, long timestampMs, bool isKeyFrame)
    {
        int frameCountByte = BufferedReadByte();
        if (frameCountByte < 0) return;

        int framesCount = frameCountByte + 1;
        if (lacing == LACING_XIPH)
            ComputeXiphLaceSizesSync(framesCount, blockEndBufPos);
        else if (lacing == LACING_EBML)
            ComputeEbmlLaceSizesSync(framesCount, blockEndBufPos);
        else if (lacing == LACING_FIXED)
            ComputeFixedLaceSizesSync(framesCount, blockEndBufPos);

        EnqueueLacedFramesSync(framesCount, blockEndBufPos, timestampMs, isKeyFrame);
        _bufPos = blockEndBufPos;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private void ComputeXiphLaceSizesSync(int framesCount, int blockEndBufPos)
    {
        ref int sizesRef = ref MemoryMarshal.GetArrayDataReference(_laceFrameSizes);
        ref byte bufRef = ref MemoryMarshal.GetArrayDataReference(_readBuffer);

        int pos = _bufPos;
        int totalSizes = 0;
        bool eofError = false;

        for (int i = 0; i < framesCount - 1; i++)
        {
            int size = 0;
            while (true)
            {
                if (pos >= blockEndBufPos)
                {
                    eofError = true;
                    break;
                }
                int b = Unsafe.Add(ref bufRef, pos++);
                size += b;
                if (b < 255) break;
            }
            if (eofError) break;
            Unsafe.Add(ref sizesRef, i) = size;
            totalSizes += size;
        }

        _bufPos = pos;
        if (eofError)
            ThrowXiphLacingEof();

        Unsafe.Add(ref sizesRef, framesCount - 1) = (blockEndBufPos - pos) - totalSizes;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private void ComputeEbmlLaceSizesSync(int framesCount, int blockEndBufPos)
    {
        ref int sizesRef = ref MemoryMarshal.GetArrayDataReference(_laceFrameSizes);
        ref byte bufRef = ref MemoryMarshal.GetArrayDataReference(_readBuffer);
        int pos = _bufPos;
        int bufLen = _bufLen;
        int totalSizes = 0;
        int badLen = -1;

        if (framesCount > 1)
        {
            if ((uint)pos >= (uint)bufLen)
            {
                badLen = 0;
            }
            else
            {
                byte first0 = Unsafe.Add(ref bufRef, pos++);
                int len0 = GetVIntLength(first0);
                if ((uint)(len0 - 1) > 7u || pos + len0 - 1 > bufLen)
                {
                    badLen = len0;
                }
                else
                {
                    long val0 = first0 & (0xFF >> len0);
                    for (int j = 1; j < len0; j++)
                        val0 = (val0 << 8) | Unsafe.Add(ref bufRef, pos++);

                    int firstSize = (int)val0;
                    Unsafe.Add(ref sizesRef, 0) = firstSize;
                    totalSizes = firstSize;
                    int prevSize = firstSize;

                    for (int i = 1; i < framesCount - 1; i++)
                    {
                        if ((uint)pos >= (uint)bufLen)
                        {
                            badLen = 0;
                            break;
                        }

                        byte first = Unsafe.Add(ref bufRef, pos++);
                        int len = GetVIntLength(first);
                        if ((uint)(len - 1) > 7u || pos + len - 1 > bufLen)
                        {
                            badLen = len;
                            break;
                        }

                        long val = len == 1 ? (first & 0x7F) : (first & (0xFF >> len));
                        for (int j = 1; j < len; j++)
                            val = (val << 8) | Unsafe.Add(ref bufRef, pos++);

                        long shift = (1L << ((7 * len) - 1)) - 1;
                        int nextSize = prevSize + (int)(val - shift);
                        Unsafe.Add(ref sizesRef, i) = nextSize;
                        totalSizes += nextSize;
                        prevSize = nextSize;
                    }
                }
            }
        }

        _bufPos = pos;
        if (badLen >= 0)
            ThrowInvalidSignedVIntLength(badLen);

        Unsafe.Add(ref sizesRef, framesCount - 1) = (blockEndBufPos - pos) - totalSizes;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private void ComputeFixedLaceSizesSync(int framesCount, int blockEndBufPos)
    {
        ref int sizesRef = ref MemoryMarshal.GetArrayDataReference(_laceFrameSizes);
        int size = (blockEndBufPos - _bufPos) / framesCount;
        for (int i = 0; i < framesCount; i++)
            Unsafe.Add(ref sizesRef, i) = size;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EnqueueLacedFramesSync(int framesCount, int blockEndBufPos, long timestampMs, bool isKeyFrame)
    {
        ref int sizesRef = ref MemoryMarshal.GetArrayDataReference(_laceFrameSizes);
        for (int i = 0; i < framesCount; i++)
        {
            int size = Unsafe.Add(ref sizesRef, i);
            if (size <= 0) continue;

            if (_bufPos + size > blockEndBufPos)
            {
                size = blockEndBufPos - _bufPos;
                if (size <= 0) break;
            }

            var mem = RentFrameOwner(size);
            BufferedReadSpan(size).CopyTo(mem.Memory.Span[..size]);
            _lacedFrames.Enqueue(new AudioBlock(mem, size, timestampMs, isKeyFrame));
        }
    }

    private async ValueTask<AudioBlock?> ReadNextBlockSlowAsync(CancellationToken ct)
    {
        if (_requiresResync)
        {
            _requiresResync = false;
            if (!await TryResyncToNextClusterAsync(ct).ConfigureAwait(false))
                return null;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_lacedFrames.TryDequeue(out var frame))
                    return frame;

                if (_bufLen - _bufPos < 16)
                    await FillBufferAsync(ct).ConfigureAwait(false);

                if (TryReadNextBlockFromBufferSync(ct, out var syncFrame))
                    return syncFrame;

                var (id, size) = await ReadElementHeaderAsync(ct).ConfigureAwait(false);
                if (id == 0) return null;

                switch (id)
                {
                    case CLUSTER_ID:
                        LastClusterBytePosition = StreamPositionWithBuffer();
                        continue;

                    case TIMECODE_ID:
                        int timecodeSize = CheckedSizeToInt32(size, "Cluster Timecode");
                        await EnsureBufferedOrThrowAsync(timecodeSize, "cluster timecode", ct).ConfigureAwait(false);
                        _currentClusterTimecode = ReadUnsignedInt(BufferedReadSpan(timecodeSize));
                        continue;

                    case SIMPLE_BLOCK_ID:
                    case BLOCK_ID:
                        await ParseBlockPayloadAsync(CheckedSizeToInt32(size, "Block"), id == SIMPLE_BLOCK_ID, ct).ConfigureAwait(false);
                        if (_lacedFrames.TryDequeue(out var lacedFrame))
                            return lacedFrame;
                        continue;

                    case BLOCK_GROUP_ID:
                        continue;

                    default:
                        if (size != UNKNOWN_SIZE && size > 0)
                            await SkipBytesAsync(size, ct).ConfigureAwait(false);
                        continue;
                }
            }
            // null only on caller token cancellation; read epoch cancellation is rethrown (otherwise false EOF)
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch (EndOfStreamException ex) when (!ct.IsCancellationRequested && _stream.CanSeek)
            {
                if (ct.IsCancellationRequested || _requiresResync)
                    return null;

                throw new ParserCorruptionException(StreamPositionWithBuffer(), "Unexpected EOF inside EBML stream", ex);
            }
            catch (InvalidDataException ex) when (!ct.IsCancellationRequested && _stream.CanSeek)
            {
                if (ct.IsCancellationRequested || _requiresResync)
                    return null;

                throw new ParserCorruptionException(StreamPositionWithBuffer(), "EBML structural corruption detected", ex);
            }
        }
        return null;
    }

    private async ValueTask ParseBlockPayloadAsync(int payloadSize, bool isSimpleBlock, CancellationToken ct)
    {
        long blockEndPos = StreamPositionWithBuffer() + payloadSize;

        await EnsureBufferedOrThrowAsync(1, "track number", ct).ConfigureAwait(false);
        int trackByte = BufferedReadByte();
        int trackLen = GetVIntLength((byte)trackByte);

        long trackNum;
        if (trackLen == 1) trackNum = trackByte & 0x7F;
        else
        {
            if (trackLen == 0 || trackLen > 8)
                throw new ParserCorruptionException(StreamPositionWithBuffer(), $"Invalid EBML Track Number VINT length: {trackLen}");

            await EnsureBufferedOrThrowAsync(trackLen - 1, "track number payload", ct).ConfigureAwait(false);
            Span<byte> trackBytes = stackalloc byte[trackLen];
            trackBytes[0] = (byte)trackByte;
            BufferedReadSpan(trackLen - 1).CopyTo(trackBytes[1..]);
            trackNum = ReadVInt(trackBytes);
        }

        await EnsureBufferedOrThrowAsync(3, "timecode and flags", ct).ConfigureAwait(false);
        short timecodeOffset = BinaryPrimitives.ReadInt16BigEndian(BufferedReadSpan(2));
        int flags = BufferedReadByte();

        if (StreamPositionWithBuffer() > blockEndPos)
        {
            throw new ParserCorruptionException(StreamPositionWithBuffer(), "Block header parsing exceeded allocated element payload size.");
        }

        long remainingPayload = blockEndPos - StreamPositionWithBuffer();

        if (trackNum != _audioTrackNumber)
        {
            if (remainingPayload > 0) await SkipBytesAsync(remainingPayload, ct).ConfigureAwait(false);
            return;
        }

        bool isKeyFrame = isSimpleBlock && (flags & 0x80) != 0;
        int lacingType = (flags >> 1) & 0x03;

        if (remainingPayload <= 0) return;

        long timestampMs = (_currentClusterTimecode + timecodeOffset) * _timecodeScale / 1_000_000;

        if (lacingType == LACING_NONE)
        {
            var mem = RentFrameOwner((int)remainingPayload);
            await ReadExactAsync(mem.Memory[..(int)remainingPayload], ct).ConfigureAwait(false);
            _lacedFrames.Enqueue(new AudioBlock(mem, (int)remainingPayload, timestampMs, isKeyFrame));
            return;
        }

        await ParseLacingAsync((int)remainingPayload, lacingType, timestampMs, isKeyFrame, ct).ConfigureAwait(false);
    }

    private async ValueTask ParseLacingAsync(int payloadSize, int lacing, long timestampMs, bool isKeyFrame, CancellationToken ct)
    {
        long payloadEndPos = StreamPositionWithBuffer() + payloadSize;

        await EnsureBufferedOrThrowAsync(1, "lacing frame count", ct).ConfigureAwait(false);
        int frameCountByte = BufferedReadByte();
        if (frameCountByte < 0) return;

        int framesCount = frameCountByte + 1;
        int[] frameSizes = _laceFrameSizes;

        if (lacing == LACING_XIPH)
        {
            int totalSizes = 0;
            if (framesCount > 1)
            {
                for (int i = 0; i < framesCount - 1; i++)
                {
                    int size = 0;
                    while (true)
                    {
                        await EnsureBufferedOrThrowAsync(1, "xiph size", ct).ConfigureAwait(false);
                        int b = BufferedReadByte();
                        if (b < 0) throw new EndOfStreamException("EOF in xiph lacing size");
                        size += b;
                        if (b < 255) break;
                    }
                    frameSizes[i] = size;
                    totalSizes += size;
                }
            }
            frameSizes[framesCount - 1] = (int)(payloadEndPos - StreamPositionWithBuffer()) - totalSizes;
        }
        else if (lacing == LACING_EBML)
        {
            int totalSizes = 0;
            if (framesCount > 1)
            {
                long firstSize = await ReadVIntFromStreamAsync(ct).ConfigureAwait(false);
                frameSizes[0] = (int)firstSize;
                totalSizes += frameSizes[0];

                for (int i = 1; i < framesCount - 1; i++)
                {
                    long delta = await ReadSignedVIntFromStreamAsync(ct).ConfigureAwait(false);
                    frameSizes[i] = frameSizes[i - 1] + (int)delta;
                    totalSizes += frameSizes[i];
                }
            }
            frameSizes[framesCount - 1] = (int)(payloadEndPos - StreamPositionWithBuffer()) - totalSizes;
        }
        else if (lacing == LACING_FIXED)
        {
            int size = (int)(payloadEndPos - StreamPositionWithBuffer()) / framesCount;
            for (int i = 0; i < framesCount; i++) frameSizes[i] = size;
        }

        for (int i = 0; i < framesCount; i++)
        {
            int size = frameSizes[i];
            if (size <= 0) continue;

            if (StreamPositionWithBuffer() + size > payloadEndPos)
            {
                size = (int)(payloadEndPos - StreamPositionWithBuffer());
                if (size <= 0) break;
            }

            var mem = RentFrameOwner(size);
            await ReadExactAsync(mem.Memory[..size], ct).ConfigureAwait(false);
            _lacedFrames.Enqueue(new AudioBlock(mem, size, timestampMs, isKeyFrame));
        }

        if (StreamPositionWithBuffer() != payloadEndPos && _stream.CanSeek)
        {
            _stream.Position = payloadEndPos;
            _bufPos = 0;
            _bufLen = 0;
        }
    }

    #endregion

    #region EBML VINT Helpers (Safe Bitwise)

    private async ValueTask<(uint Id, long Size)> ReadElementHeaderAsync(CancellationToken ct)
    {
        int firstByte;
        while (true)
        {
            if (!await EnsureBufferedAsync(1, ct).ConfigureAwait(false)) return (0, 0);
            firstByte = BufferedReadByte();
            if (firstByte < 0) return (0, 0);
            if (firstByte != 0x00) break;
        }

        int idLength = GetVIntLength((byte)firstByte);
        if (idLength == 0 || idLength > 4)
            throw new InvalidDataException($"Invalid EBML ID VINT length: {idLength}");

        if (idLength > 1 && !await EnsureBufferedAsync(idLength - 1, ct).ConfigureAwait(false)) return (0, 0);

        uint id = (uint)firstByte;
        for (int i = 1; i < idLength; i++)
            id = (id << 8) | (uint)BufferedReadByte();

        if (!await EnsureBufferedAsync(1, ct).ConfigureAwait(false)) return (0, 0);
        firstByte = BufferedReadByte();
        if (firstByte < 0) return (0, 0);

        int sizeLength = GetVIntLength((byte)firstByte);
        if (sizeLength == 0 || sizeLength > 8)
            throw new InvalidDataException($"Invalid EBML Size VINT length: {sizeLength}");

        if (sizeLength > 1 && !await EnsureBufferedAsync(sizeLength - 1, ct).ConfigureAwait(false)) return (0, 0);

        long size = firstByte & (0xFF >> sizeLength);
        for (int i = 1; i < sizeLength; i++)
            size = (size << 8) | (uint)BufferedReadByte();

        long maxVal = (1L << (7 * sizeLength)) - 1;
        if (size == maxVal) size = UNKNOWN_SIZE;

        return (id, size);
    }

    private async ValueTask<long> ReadVIntFromStreamAsync(CancellationToken ct)
    {
        await EnsureBufferedOrThrowAsync(1, "vint", ct).ConfigureAwait(false);
        int first = BufferedReadByte();
        int len = GetVIntLength((byte)first);
        if (len == 0 || len > 8)
            throw new InvalidDataException($"Invalid EBML VINT length: {len}");

        if (len == 1) return first & 0x7F;

        await EnsureBufferedOrThrowAsync(len - 1, "vint payload", ct).ConfigureAwait(false);
        long val = first & (0xFF >> len);
        for (int i = 1; i < len; i++)
            val = (val << 8) | (uint)BufferedReadByte();

        return val;
    }

    private async ValueTask<long> ReadSignedVIntFromStreamAsync(CancellationToken ct)
    {
        await EnsureBufferedOrThrowAsync(1, "signed vint", ct).ConfigureAwait(false);
        int first = BufferedReadByte();
        int len = GetVIntLength((byte)first);
        if (len == 0 || len > 8)
            throw new InvalidDataException($"Invalid EBML Signed VINT length: {len}");

        long val;
        if (len == 1)
        {
            val = first & 0x7F;
        }
        else
        {
            await EnsureBufferedOrThrowAsync(len - 1, "signed vint payload", ct).ConfigureAwait(false);
            val = first & (0xFF >> len);
            for (int i = 1; i < len; i++)
                val = (val << 8) | (uint)BufferedReadByte();
        }

        long shift = (1L << ((7 * len) - 1)) - 1;
        return val - shift;
    }

    private static long ReadVInt(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return 0;

        ref byte dataRef = ref MemoryMarshal.GetReference(data);
        byte first = dataRef;
        int length = GetVIntLength(first);
        if ((uint)(length - 1) > 7u || length > data.Length) return 0;

        long value = first & (0xFF >> length);
        for (int i = 1; i < length; i++)
            value = (value << 8) | Unsafe.Add(ref dataRef, i);

        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetVIntLength(byte b) =>
        b == 0 ? 0 : BitOperations.LeadingZeroCount(b) - 23;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ReadUnsignedInt(ReadOnlySpan<byte> data)
    {
        long value = 0;
        ref byte dataRef = ref MemoryMarshal.GetReference(data);
        int len = data.Length;
        for (int i = 0; i < len; i++)
            value = (value << 8) | Unsafe.Add(ref dataRef, i);
        return value;
    }

    private static double ReadFloat(ReadOnlySpan<byte> data) => data.Length switch
    {
        4 => BinaryPrimitives.ReadSingleBigEndian(data),
        8 => BinaryPrimitives.ReadDoubleBigEndian(data),
        _ => 0
    };

    private ValueTask EnsureBufferedOrThrowAsync(int needed, string context, CancellationToken ct)
    {
        if (needed <= 0 || _bufLen - _bufPos >= needed)
            return ValueTask.CompletedTask;

        return EnsureBufferedOrThrowSlowAsync(needed, context, ct);
    }

    private async ValueTask EnsureBufferedOrThrowSlowAsync(int needed, string context, CancellationToken ct)
    {
        if (await EnsureBufferedSlowAsync(needed, ct).ConfigureAwait(false)) return;
        throw new EndOfStreamException($"Unexpected EOF reading {context}.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CheckedSizeToInt32(long size, string context)
    {
        if ((ulong)size > int.MaxValue)
            ThrowInvalidElementSize(size, context);
        return (int)size;
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidElementSize(long size, string context)
    {
        if (size == UNKNOWN_SIZE)
            throw new InvalidDataException($"WebM {context} has unknown size, which is unsupported for this element.");
        throw new InvalidDataException($"WebM {context} size {size} is out of supported memory range.");
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidIdVIntLength(int idLength) =>
        throw new InvalidDataException($"Invalid EBML ID VINT length: {idLength}");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidSizeVIntLength(int sizeLength) =>
        throw new InvalidDataException($"Invalid EBML Size VINT length: {sizeLength}");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidSignedVIntLength(int len) =>
        throw new InvalidDataException($"Invalid EBML Signed VINT length: {len}");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowXiphLacingEof() =>
        throw new EndOfStreamException("EOF in xiph lacing size");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowInvalidTrackNumberVInt(int trackLen) =>
        throw new ParserCorruptionException(StreamPositionWithBuffer(), $"Invalid EBML Track Number VINT length: {trackLen}");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowBlockHeaderExceeded() =>
        throw new ParserCorruptionException(StreamPositionWithBuffer(), "Block header parsing exceeded allocated element payload size.");

    #endregion

    #region Resync & Reset Utilities

    private async ValueTask<bool> TryResyncToNextClusterAsync(CancellationToken ct)
    {
        _bufPos = 0;
        _bufLen = 0;
        if (!_stream.CanSeek) return false;

        long startPos = _stream.Position;
        long endPos = Math.Min(startPos + MaxResyncScanBytes, _stream.Length);

        int window = 0;
        while (_stream.Position < endPos && !ct.IsCancellationRequested)
        {
            await FillBufferAsync(ct).ConfigureAwait(false);
            if (_bufLen == 0) break;

            while (_bufPos < _bufLen)
            {
                window = (window << 8) | _readBuffer[_bufPos++];

                if (window == unchecked((int)CLUSTER_ID))
                {
                    long clusterStart = _stream.Position - (_bufLen - _bufPos) - 4;
                    _stream.Position = clusterStart;
                    _bufPos = 0;
                    _bufLen = 0;
                    _currentClusterTimecode = 0;
                    ResyncOccurred = true;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Finds the seek position for the specified time in milliseconds.
    /// </summary>
    public long? FindSeekPosition(long targetMs)
    {
        if (_cuePoints.Count == 0) return null;
        int low = 0, high = _cuePoints.Count - 1;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (_cuePoints[mid].TimeMs <= targetMs) low = mid;
            else high = mid - 1;
        }
        return _segmentOffset + _cuePoints[low].ClusterOffset;
    }

    /// <summary>
    /// Resets parser buffer state.
    /// </summary>
    public void Reset()
    {
        _bufPos = 0;
        _bufLen = 0;
        _currentClusterTimecode = 0;
        _requiresResync = false;
        ResyncOccurred = false;

        while (_lacedFrames.TryDequeue(out var frame))
            frame.Owner.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        while (_lacedFrames.TryDequeue(out var frame))
            frame.Owner.Dispose();
    }

    #endregion
}