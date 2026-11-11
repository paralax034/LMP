using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace LMP.Core.Audio.Parsers;

/// <summary>
/// Zero-alloc binary reader for MP4 box-level I/O.
/// Single responsibility: reading primitives from a stream without heap allocations.
/// </summary>
/// <remarks>
/// <para><b>Architecture:</b> Single <see cref="_ioBuffer"/> per reader instance.
/// All operations execute via stack or this buffer — no ArrayPool, no <c>new byte[]</c>.</para>
/// <para><b>FourCC:</b> Box type is represented as <see cref="uint"/> (big-endian),
/// matching via numeric constants — no string allocations.</para>
/// <para><b>Thread safety:</b> Not thread-safe. One instance per parser.</para>
/// </remarks>
internal sealed class Mp4BinaryReader
{
    #region FourCC Constants

    /// <summary>FourCC: "moov" — Movie box, contains track metadata.</summary>
    public const uint FCC_MOOV = 0x6D6F6F76;

    /// <summary>FourCC: "mvhd" — Movie header box.</summary>
    public const uint FCC_MVHD = 0x6D766864;

    /// <summary>FourCC: "mvex" — Movie extends box (fragmented MP4).</summary>
    public const uint FCC_MVEX = 0x6D766578;

    /// <summary>FourCC: "trex" — Track extends defaults.</summary>
    public const uint FCC_TREX = 0x74726578;

    /// <summary>FourCC: "trak" — Track box.</summary>
    public const uint FCC_TRAK = 0x7472616B;

    /// <summary>FourCC: "mdia" — Media box.</summary>
    public const uint FCC_MDIA = 0x6D646961;

    /// <summary>FourCC: "hdlr" — Handler reference box.</summary>
    public const uint FCC_HDLR = 0x68646C72;

    /// <summary>FourCC: "mdhd" — Media header box.</summary>
    public const uint FCC_MDHD = 0x6D646864;

    /// <summary>FourCC: "minf" — Media information box.</summary>
    public const uint FCC_MINF = 0x6D696E66;

    /// <summary>FourCC: "stbl" — Sample table box.</summary>
    public const uint FCC_STBL = 0x7374626C;

    /// <summary>FourCC: "stsd" — Sample description box.</summary>
    public const uint FCC_STSD = 0x73747364;

    /// <summary>FourCC: "stsz" — Sample size box.</summary>
    public const uint FCC_STSZ = 0x7374737A;

    /// <summary>FourCC: "stsc" — Sample-to-chunk box.</summary>
    public const uint FCC_STSC = 0x73747363;

    /// <summary>FourCC: "stco" — Chunk offset box (32-bit).</summary>
    public const uint FCC_STCO = 0x7374636F;

    /// <summary>FourCC: "co64" — Chunk offset box (64-bit).</summary>
    public const uint FCC_CO64 = 0x636F3634;

    /// <summary>FourCC: "stts" — Time-to-sample box.</summary>
    public const uint FCC_STTS = 0x73747473;

    /// <summary>FourCC: "esds" — ES descriptor box.</summary>
    public const uint FCC_ESDS = 0x65736473;

    /// <summary>FourCC: "sidx" — Segment index box.</summary>
    public const uint FCC_SIDX = 0x73696478;

    /// <summary>FourCC: "moof" — Movie fragment box.</summary>
    public const uint FCC_MOOF = 0x6D6F6F66;

    /// <summary>FourCC: "mdat" — Media data box.</summary>
    public const uint FCC_MDAT = 0x6D646174;

    /// <summary>FourCC: "traf" — Track fragment box.</summary>
    public const uint FCC_TRAF = 0x74726166;

    /// <summary>FourCC: "tfhd" — Track fragment header box.</summary>
    public const uint FCC_TFHD = 0x74666864;

    /// <summary>FourCC: "tfdt" — Track fragment decode time box.</summary>
    public const uint FCC_TFDT = 0x74666474;

    /// <summary>FourCC: "trun" — Track run box.</summary>
    public const uint FCC_TRUN = 0x7472756E;

    /// <summary>FourCC: "soun" — Sound handler type.</summary>
    public const uint FCC_SOUN = 0x736F756E;

    #endregion

    /// <summary>
    /// Instance shared I/O buffer — eliminates 80,000+ ArrayPool Rent/Return calls per parsing run.
    /// 16 bytes is sufficient for max read (uint64 = 8 bytes), with padding for box header (8 + 8).
    /// </summary>
    private readonly byte[] _ioBuffer = new byte[16];

    /// <summary>Wrapped stream.</summary>
    private readonly Stream _stream;

    /// <summary>
    /// Creates a reader for the specified stream.
    /// </summary>
    /// <param name="stream">Stream to read. Must support <see cref="Stream.CanRead"/>.</param>
    public Mp4BinaryReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    /// <summary>Current stream position.</summary>
    public long Position
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _stream.Position;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _stream.Position = value;
    }

    /// <summary>Stream length.</summary>
    public long Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _stream.Length;
    }

    /// <summary>Remaining bytes from current position to end.</summary>
    public long Remaining
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _stream.Length - _stream.Position;
    }

    #region Box Header

    /// <summary>
    /// MP4 box header read result.
    /// </summary>
    /// <param name="Size">Total box size (including header). 0 = EOF or error.</param>
    /// <param name="Type">FourCC as uint (big-endian). 0 = EOF.</param>
    /// <param name="HeaderSize">Header size (8 for standard, 16 for extended).</param>
    public readonly record struct BoxHeader(long Size, uint Type, int HeaderSize)
    {
        /// <summary>Header not read (EOF, truncated tail) or size == 0 (zeroed block).</summary>
        public bool IsEmpty => Size == 0;
    }

    private const int CompactBoxHeaderSize = 8;
    private const int ExtendedBoxHeaderSize = 16;

    /// <summary>
    /// Reads an MP4 box header. Partial stream reads (valid for any <see cref="Stream"/>,
    /// including at RAM cache block boundaries) are fully consumed; as long as the stream responds synchronously — allocation-free.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Box header. <see cref="BoxHeader.IsEmpty"/> = true on EOF, including truncated tails shorter than 8 bytes.
    /// </returns>
    public ValueTask<BoxHeader> ReadBoxHeaderAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        int filled = 0;
        while (filled < CompactBoxHeaderSize)
        {
            var readTask = _stream.ReadAsync(_ioBuffer.AsMemory(filled, CompactBoxHeaderSize - filled), ct);
            if (!readTask.IsCompletedSuccessfully)
                return ReadBoxHeaderSlowAsync(readTask, filled, ct);

            int read = readTask.Result;
            if (read <= 0)
            {
                // Real EOF; cancellation masked by stream as 0 is not converted to an "empty" header
                ct.ThrowIfCancellationRequested();
                return new ValueTask<BoxHeader>(default(BoxHeader));
            }

            filled += read;
        }

        return CompleteBoxHeader(ct);
    }

    /// <summary>
    /// Parses the assembled 8 header bytes; for size == 1, reads the remaining 64-bit largesize.
    /// </summary>
    private ValueTask<BoxHeader> CompleteBoxHeader(CancellationToken ct)
    {
        uint size = BinaryPrimitives.ReadUInt32BigEndian(_ioBuffer);
        uint type = BinaryPrimitives.ReadUInt32BigEndian(_ioBuffer.AsSpan(4));

        if (size != 1)
            return new ValueTask<BoxHeader>(new BoxHeader(size, type, CompactBoxHeaderSize));

        var extendedRead = ReadExactlyAsync(_ioBuffer.AsMemory(0, 8), ct);
        if (extendedRead.IsCompletedSuccessfully)
        {
            extendedRead.GetAwaiter().GetResult();
            return new ValueTask<BoxHeader>(CreateExtendedHeader(type));
        }

        return AwaitExtendedBoxHeaderAsync(extendedRead, type);
    }

    private async ValueTask<BoxHeader> ReadBoxHeaderSlowAsync(ValueTask<int> pendingRead, int filled, CancellationToken ct)
    {
        while (true)
        {
            int read = await pendingRead.ConfigureAwait(false);
            if (read <= 0)
            {
                ct.ThrowIfCancellationRequested();
                return default;
            }

            filled += read;
            if (filled >= CompactBoxHeaderSize)
                break;

            ct.ThrowIfCancellationRequested();
            pendingRead = _stream.ReadAsync(_ioBuffer.AsMemory(filled, CompactBoxHeaderSize - filled), ct);
        }

        return await CompleteBoxHeader(ct).ConfigureAwait(false);
    }

    private async ValueTask<BoxHeader> AwaitExtendedBoxHeaderAsync(ValueTask pendingRead, uint type)
    {
        await pendingRead.ConfigureAwait(false);
        return CreateExtendedHeader(type);
    }

    private BoxHeader CreateExtendedHeader(uint type) =>
        new((long)BinaryPrimitives.ReadUInt64BigEndian(_ioBuffer), type, ExtendedBoxHeaderSize);

    #endregion

    #region Primitives

    /// <summary>Reads a single byte. Returns -1 on EOF.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<int> ReadByteAsync(CancellationToken ct)
    {
        int read = await _stream.ReadAsync(_ioBuffer.AsMemory(0, 1), ct).ConfigureAwait(false);
        return read == 0 ? -1 : _ioBuffer[0];
    }

    /// <summary>Reads big-endian uint16.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<ushort> ReadUInt16BEAsync(CancellationToken ct)
    {
        await ReadExactlyAsync(_ioBuffer.AsMemory(0, 2), ct).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt16BigEndian(_ioBuffer);
    }

    /// <summary>Reads big-endian uint32.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<uint> ReadUInt32BEAsync(CancellationToken ct)
    {
        await ReadExactlyAsync(_ioBuffer.AsMemory(0, 4), ct).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32BigEndian(_ioBuffer);
    }

    /// <summary>Reads big-endian int32.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<int> ReadInt32BEAsync(CancellationToken ct)
    {
        await ReadExactlyAsync(_ioBuffer.AsMemory(0, 4), ct).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32BigEndian(_ioBuffer);
    }

    /// <summary>Reads big-endian uint64.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<ulong> ReadUInt64BEAsync(CancellationToken ct)
    {
        await ReadExactlyAsync(_ioBuffer.AsMemory(0, 8), ct).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt64BigEndian(_ioBuffer);
    }

    /// <summary>Reads FourCC (4 bytes) as big-endian uint.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<uint> ReadFourCCAsync(CancellationToken ct)
    {
        await ReadExactlyAsync(_ioBuffer.AsMemory(0, 4), ct).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32BigEndian(_ioBuffer);
    }

    /// <summary>
    /// Reads expandable length (ISO 14496-1 descriptor length encoding).
    /// Each byte: 7 bits data + 1 bit continuation flag.
    /// </summary>
    public async ValueTask<int> ReadExpandableLengthAsync(CancellationToken ct)
    {
        int length = 0;
        for (int i = 0; i < 4; i++)
        {
            int read = await _stream.ReadAsync(_ioBuffer.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();

            int b = _ioBuffer[0];
            length = (length << 7) | (b & 0x7F);
            if ((b & 0x80) == 0) break;
        }

        return length;
    }

    #endregion

    #region Bulk I/O

    /// <summary>
    /// Reads exactly <paramref name="buffer"/>.Length bytes.
    /// Does not allocate an async state machine on synchronous stream completion (RAM cache or OS buffer).
    /// </summary>
    public ValueTask ReadExactlyAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (buffer.IsEmpty)
            return ValueTask.CompletedTask;

        ct.ThrowIfCancellationRequested();
        var readTask = _stream.ReadAsync(buffer, ct);
        if (readTask.IsCompletedSuccessfully)
        {
            int read = readTask.Result;
            if (read == buffer.Length)
                return ValueTask.CompletedTask;

            if (read == 0)
            {
                ct.ThrowIfCancellationRequested();
                throw new EndOfStreamException();
            }

            return ReadExactlySlowAsync(buffer[read..], ct);
        }

        return ReadExactlyAwaitFirstAsync(readTask, buffer, ct);
    }

    private async ValueTask ReadExactlyAwaitFirstAsync(ValueTask<int> firstReadTask, Memory<byte> buffer, CancellationToken ct)
    {
        int read = await firstReadTask.ConfigureAwait(false);
        if (read == buffer.Length)
            return;

        if (read == 0)
        {
            ct.ThrowIfCancellationRequested();
            throw new EndOfStreamException();
        }

        await ReadExactlySlowAsync(buffer[read..], ct).ConfigureAwait(false);
    }

    private async ValueTask ReadExactlySlowAsync(Memory<byte> remainingBuffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < remainingBuffer.Length)
        {
            ct.ThrowIfCancellationRequested();
            int read = await _stream.ReadAsync(remainingBuffer[totalRead..], ct).ConfigureAwait(false);
            if (read == 0)
            {
                ct.ThrowIfCancellationRequested();
                throw new EndOfStreamException();
            }
            totalRead += read;
        }
    }

    /// <summary>
    /// Reads the specified number of bytes into a fresh array.
    /// Use only for small and critical payload data (decoder config, etc.).
    /// </summary>
    /// <param name="count">Byte count.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Array containing read data.</returns>
    public async ValueTask<byte[]> ReadBytesAsync(int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        await ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        return buffer;
    }

    /// <summary>Skips the specified number of bytes.</summary>
    public async ValueTask SkipBytesAsync(long count, CancellationToken ct)
    {
        if (_stream.CanSeek)
        {
            _stream.Position += count;
            return;
        }

        while (count > 0)
        {
            int toRead = (int)Math.Min(count, _ioBuffer.Length);
            int read = await _stream.ReadAsync(_ioBuffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
            if (read == 0) break;
            count -= read;
        }
    }

    /// <summary>Seeks the stream to the specified position, skipping bytes if necessary.</summary>
    public async ValueTask SkipToAsync(long position, CancellationToken ct)
    {
        if (_stream.Position < position)
            await SkipBytesAsync(position - _stream.Position, ct).ConfigureAwait(false);
    }

    #endregion
}