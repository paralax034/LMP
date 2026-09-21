using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace LMP.Core.Audio.Parsers;

/// <summary>
/// Zero-alloc binary reader для MP4 box-level I/O.
/// Единственная ответственность: чтение примитивов из потока без heap-аллокаций.
/// </summary>
/// <remarks>
/// <para><b>Архитектура:</b> Один <see cref="_ioBuffer"/> на экземпляр reader'а.
/// Вся работа идёт через stack или этот buffer — нет ArrayPool, нет <c>new byte[]</c>.</para>
/// <para><b>FourCC:</b> Box type представлен как <see cref="uint"/> (big-endian),
/// switch по числовым константам — нет string-аллокаций.</para>
/// <para><b>Thread safety:</b> Не потокобезопасен. Один экземпляр на один parser.</para>
/// </remarks>
public sealed class Mp4BinaryReader
{
    #region FourCC Constants

    /// <summary>FourCC: "moov" — Movie box, содержит метаданные трека.</summary>
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
    /// Единый I/O буфер экземпляра — устраняет 80000+ ArrayPool Rent/Return за парсинг.
    /// 16 байт достаточно для max read (uint64 = 8 байт), с запасом для box header (8 + 8).
    /// </summary>
    private readonly byte[] _ioBuffer = new byte[16];

    /// <summary>Обёрнутый поток.</summary>
    private readonly Stream _stream;

    /// <summary>
    /// Создаёт reader для указанного потока.
    /// </summary>
    /// <param name="stream">Поток для чтения. Должен поддерживать <see cref="Stream.CanRead"/>.</param>
    public Mp4BinaryReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    /// <summary>Текущая позиция потока.</summary>
    public long Position
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _stream.Position;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _stream.Position = value;
    }

    /// <summary>Длина потока.</summary>
    public long Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _stream.Length;
    }

    /// <summary>Остаток байт от текущей позиции до конца.</summary>
    public long Remaining
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _stream.Length - _stream.Position;
    }

    #region Box Header

    /// <summary>
    /// Результат чтения заголовка MP4 box.
    /// </summary>
    /// <param name="Size">Полный размер box (включая header). 0 = EOF или ошибка.</param>
    /// <param name="Type">FourCC как uint (big-endian). 0 = EOF.</param>
    /// <param name="HeaderSize">Размер заголовка (8 для обычного, 16 для extended).</param>
    public readonly record struct BoxHeader(long Size, uint Type, int HeaderSize)
    {
        /// <summary>Заголовок не прочитан (EOF или недостаточно данных).</summary>
        public bool IsEmpty => Size == 0;
    }

    /// <summary>
    /// Читает заголовок MP4 box. Zero-alloc: использует <see cref="_ioBuffer"/>.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Заголовок box. <see cref="BoxHeader.IsEmpty"/> = true при EOF.</returns>
    public async ValueTask<BoxHeader> ReadBoxHeaderAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int read = await _stream.ReadAsync(_ioBuffer.AsMemory(0, 8), ct).ConfigureAwait(false);
        if (read < 8)
            return default;

        uint size = BinaryPrimitives.ReadUInt32BigEndian(_ioBuffer);
        uint type = BinaryPrimitives.ReadUInt32BigEndian(_ioBuffer.AsSpan(4));

        if (size == 1)
        {
            await ReadExactlyAsync(_ioBuffer.AsMemory(0, 8), ct).ConfigureAwait(false);
            long extSize = (long)BinaryPrimitives.ReadUInt64BigEndian(_ioBuffer);
            return new BoxHeader(extSize, type, 16);
        }

        return new BoxHeader(size, type, 8);
    }

    #endregion

    #region Primitives

    /// <summary>Читает один байт. Возвращает -1 при EOF.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<int> ReadByteAsync(CancellationToken ct)
    {
        int read = await _stream.ReadAsync(_ioBuffer.AsMemory(0, 1), ct).ConfigureAwait(false);
        return read == 0 ? -1 : _ioBuffer[0];
    }

    /// <summary>Читает uint16 big-endian.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<ushort> ReadUInt16BEAsync(CancellationToken ct)
    {
        await ReadExactlyAsync(_ioBuffer.AsMemory(0, 2), ct).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt16BigEndian(_ioBuffer);
    }

    /// <summary>Читает uint32 big-endian.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<uint> ReadUInt32BEAsync(CancellationToken ct)
    {
        await ReadExactlyAsync(_ioBuffer.AsMemory(0, 4), ct).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32BigEndian(_ioBuffer);
    }

    /// <summary>Читает int32 big-endian.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<int> ReadInt32BEAsync(CancellationToken ct)
    {
        await ReadExactlyAsync(_ioBuffer.AsMemory(0, 4), ct).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32BigEndian(_ioBuffer);
    }

    /// <summary>Читает uint64 big-endian.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<ulong> ReadUInt64BEAsync(CancellationToken ct)
    {
        await ReadExactlyAsync(_ioBuffer.AsMemory(0, 8), ct).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt64BigEndian(_ioBuffer);
    }

    /// <summary>Читает FourCC (4 байта) как uint big-endian.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<uint> ReadFourCCAsync(CancellationToken ct)
    {
        await ReadExactlyAsync(_ioBuffer.AsMemory(0, 4), ct).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32BigEndian(_ioBuffer);
    }

    /// <summary>
    /// Читает expandable length (ISO 14496-1 descriptor length encoding).
    /// Каждый байт: 7 бит данных + 1 бит continuation.
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
    /// Читает ровно <paramref name="buffer"/>.Length байт.
    /// Бросает <see cref="EndOfStreamException"/> при преждевременном EOF.
    /// </summary>
    public async ValueTask ReadExactlyAsync(Memory<byte> buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            ct.ThrowIfCancellationRequested();
            int read = await _stream.ReadAsync(buffer[totalRead..], ct).ConfigureAwait(false);
            if (read == 0)
            {
                ct.ThrowIfCancellationRequested();
                throw new EndOfStreamException();
            }
            totalRead += read;
        }
    }

    /// <summary>
    /// Читает указанное количество байт в свежий массив.
    /// Использовать только для малых и критичных данных (decoder config и т.п.).
    /// </summary>
    /// <param name="count">Количество байт.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Массив с прочитанными данными.</returns>
    public async ValueTask<byte[]> ReadBytesAsync(int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        await ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        return buffer;
    }

    /// <summary>Пропускает указанное количество байт.</summary>
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

    /// <summary>Перемещает позицию потока к указанной, пропуская байты если нужно.</summary>
    public async ValueTask SkipToAsync(long position, CancellationToken ct)
    {
        if (_stream.Position < position)
            await SkipBytesAsync(position - _stream.Position, ct).ConfigureAwait(false);
    }

    #endregion
}