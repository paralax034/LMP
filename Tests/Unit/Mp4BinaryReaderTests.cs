using LMP.Core.Audio.Parsers;
using LMP.Tests.Framework;
using RamRangeBlock = LMP.Core.Audio.Sources.CachingStreamSource.RamRangeBlock;
using SlidingRamCache = LMP.Core.Audio.Sources.CachingStreamSource.SlidingRamCache;

namespace LMP.Tests.Unit;

/// <summary>
/// Contract tests for <see cref="Mp4BinaryReader"/>: box header assembly on partial reads
/// (valid behavior for any <see cref="Stream"/>), distinguishing actual EOF from 0 B on the synchronous path.
/// </summary>
public static class Mp4BinaryReaderTests
{
    private const int CompactBoxSize = 24;
    private const int ExtendedBoxSize = 40;

    /// <summary>
    /// Proves P0: partial reads (&lt; 8 bytes) are not EOF and must be read to completion,
    /// for both synchronously and asynchronously completing streams.
    /// </summary>
    [TestMethod(TestCategory.Unit, "MP4 Box Header Assembly Under Short Reads", Group = TestGroups.Audio, Order = 20)]
    public static async Task TestReadBoxHeaderShortReadsAsync()
    {
        byte[] compact = BuildCompactBox(Mp4BinaryReader.FCC_MOOF, CompactBoxSize);
        byte[] extended = BuildExtendedBox(Mp4BinaryReader.FCC_MDAT, ExtendedBoxSize);
        var expectedCompact = new Mp4BinaryReader.BoxHeader(CompactBoxSize, Mp4BinaryReader.FCC_MOOF, 8);
        var expectedExtended = new Mp4BinaryReader.BoxHeader(ExtendedBoxSize, Mp4BinaryReader.FCC_MDAT, 16);

        for (int mode = 0; mode < 2; mode++)
        {
            bool forceAsync = mode == 1;
            for (int chunk = 1; chunk <= 8; chunk++)
            {
                // 1. Compact header: 4-byte size + 4-byte type
                using (var stream = new ChunkedReadStream(compact, chunk, forceAsync))
                {
                    var reader = new Mp4BinaryReader(stream);
                    var header = await reader.ReadBoxHeaderAsync(CancellationToken.None).ConfigureAwait(false);

                    TestAssert.False(header.IsEmpty, $"chunk={chunk}, async={forceAsync}: short read was misinterpreted as EOF");
                    TestAssert.Equal(expectedCompact, header, $"chunk={chunk}, async={forceAsync}: compact header mismatch");
                    TestAssert.Equal(8L, reader.Position, $"chunk={chunk}, async={forceAsync}: reader must stop right after the header");
                }

                // 2. Extended header (size == 1 + 64-bit largesize)
                using (var stream = new ChunkedReadStream(extended, chunk, forceAsync))
                {
                    var reader = new Mp4BinaryReader(stream);
                    var header = await reader.ReadBoxHeaderAsync(CancellationToken.None).ConfigureAwait(false);

                    TestAssert.False(header.IsEmpty, $"chunk={chunk}, async={forceAsync}: extended header misinterpreted as EOF");
                    TestAssert.Equal(expectedExtended, header, $"chunk={chunk}, async={forceAsync}: extended header mismatch");
                    TestAssert.Equal(16L, reader.Position, $"chunk={chunk}, async={forceAsync}: reader must stop right after the extended header");
                }
            }
        }
    }

    /// <summary>
    /// Safety contract for the fix: genuine EOF and truncated tail return an empty header without throwing exceptions.
    /// </summary>
    [TestMethod(TestCategory.Unit, "MP4 Box Header EOF Contract", Group = TestGroups.Audio, Order = 21)]
    public static async Task TestReadBoxHeaderEofContractAsync()
    {
        byte[] truncated = BuildCompactBox(Mp4BinaryReader.FCC_MOOF, CompactBoxSize).AsSpan(0, 3).ToArray();

        for (int mode = 0; mode < 2; mode++)
        {
            bool forceAsync = mode == 1;

            // 1. Empty stream: read == 0 on the first call
            using (var empty = new ChunkedReadStream([], 8, forceAsync))
            {
                var reader = new Mp4BinaryReader(empty);
                var header = await reader.ReadBoxHeaderAsync(CancellationToken.None).ConfigureAwait(false);
                TestAssert.True(header.IsEmpty, $"async={forceAsync}: empty stream must yield IsEmpty");
            }

            // 2. Truncated tail: 3 bytes, then EOF — empty header, no EndOfStreamException
            using (var tail = new ChunkedReadStream(truncated, 1, forceAsync))
            {
                var reader = new Mp4BinaryReader(tail);
                var header = await reader.ReadBoxHeaderAsync(CancellationToken.None).ConfigureAwait(false);
                TestAssert.True(header.IsEmpty, $"async={forceAsync}: truncated tail must yield IsEmpty");
                TestAssert.True(reader.Position <= reader.Length, $"async={forceAsync}: position overran stream length");
            }
        }
    }

    /// <summary>
    /// Proves that P0 is achievable in production: <see cref="SlidingRamCache.TryRead"/> returns a partial read
    /// at the boundary of blocks, causing the reader on top of the cache fast path to lose the header.
    /// </summary>
    [TestMethod(TestCategory.Unit, "MP4 Header Across SlidingRamCache Block Boundary", Group = TestGroups.Audio, Order = 22)]
    public static async Task TestBoxHeaderAcrossRamCacheBoundaryAsync()
    {
        const int splitAt = 6; // RAM block boundary inside an 8-byte header
        byte[] data = BuildCompactBox(Mp4BinaryReader.FCC_MOOF, CompactBoxSize);

        using var cache = new SlidingRamCache();
        AddBlock(cache, data, 0, splitAt);
        AddBlock(cache, data, splitAt, data.Length - splitAt);

        // 1. Precondition: blocks are not merged, TryRead is limited by the remainder of the current block
        byte[] probe = new byte[8];
        TestAssert.True(cache.TryRead(0, probe, out int partial), "SlidingRamCache must serve offset 0");
        TestAssert.Equal(splitAt, partial, "SlidingRamCache.TryRead must stop at the block boundary");

        // 2. End-to-end scenario: AsyncCachingReadStream fast path -> Mp4BinaryReader -> ScanAndParseNextFragmentAsync
        using var stream = new RamCacheReadStream(cache, data.Length);
        var reader = new Mp4BinaryReader(stream);
        var header = await reader.ReadBoxHeaderAsync(CancellationToken.None).ConfigureAwait(false);

        TestAssert.False(header.IsEmpty, "Header split across RAM blocks was read as EOF (Fragment.cs -> ParserCorruptionException)");
        TestAssert.Equal(new Mp4BinaryReader.BoxHeader(CompactBoxSize, Mp4BinaryReader.FCC_MOOF, 8), header);
        TestAssert.Equal(8L, reader.Position, "Reader must consume exactly 8 bytes across both blocks");
    }

    /// <summary>
    /// Enforces the fix budget: synchronously completing reads (full and partial) do not allocate.
    /// </summary>
    [TestMethod(TestCategory.Unit, "MP4 Box Header Sync Path (0 Alloc)", Group = TestGroups.Audio, Order = 23)]
    public static Task TestReadBoxHeaderZeroAllocAsync()
    {
        byte[] data = BuildCompactBox(Mp4BinaryReader.FCC_MOOF, CompactBoxSize);

        AssertSyncHeaderReadZeroAlloc(data, chunk: 8);
        AssertSyncHeaderReadZeroAlloc(data, chunk: 3);

        return Task.CompletedTask;
    }

    private static void AssertSyncHeaderReadZeroAlloc(byte[] data, int chunk)
    {
        using var stream = new ChunkedReadStream(data, chunk, forceAsync: false);
        var reader = new Mp4BinaryReader(stream);

        TestAssert.ZeroAlloc(() =>
        {
            stream.Position = 0;
            var pending = reader.ReadBoxHeaderAsync(CancellationToken.None);
            if (!pending.IsCompletedSuccessfully || pending.Result.Type != Mp4BinaryReader.FCC_MOOF)
                TestAssert.Fail("ReadBoxHeaderAsync must complete synchronously with a valid header on a synchronous stream");
        }, warmup: 20, iterations: 2_000, context: $"Mp4BinaryReader.ReadBoxHeaderAsync (chunk={chunk})");
    }

    private static void AddBlock(SlidingRamCache cache, byte[] source, int offset, int length)
    {
        byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(length);
        source.AsSpan(offset, length).CopyTo(rented);

        var block = new RamRangeBlock(offset, rented, length);
        if (!cache.TryAdd(block))
        {
            block.Dispose();
            TestAssert.Fail($"Failed to add RAM block [{offset}, {offset + length})");
        }
    }

    private static byte[] BuildCompactBox(uint type, int size)
    {
        byte[] box = new byte[size];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(box, (uint)size);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(4), type);
        return box;
    }

    private static byte[] BuildExtendedBox(uint type, int size)
    {
        byte[] box = new byte[size];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(box, 1u);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(4), type);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(box.AsSpan(8), (ulong)size);
        return box;
    }

    /// <summary>
    /// Stream that yields no more than <c>maxChunk</c> bytes per call; yields control before reading in async mode.
    /// </summary>
    private sealed class ChunkedReadStream(byte[] data, int maxChunk, bool forceAsync) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            long remaining = data.Length - _position;
            int count = (int)Math.Min(Math.Min(buffer.Length, maxChunk), remaining);
            if (count <= 0)
                return 0;

            data.AsSpan((int)_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            forceAsync
                ? ReadYieldingAsync(buffer, cancellationToken)
                : new ValueTask<int>(Read(buffer.Span));

        private async ValueTask<int> ReadYieldingAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return Read(buffer.Span);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => data.Length + offset
            };
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Replicates the <c>AsyncCachingReadStream.ReadAsync</c> fast path (ReadStream.cs, lines 180–184):
    /// a single <see cref="SlidingRamCache.TryRead"/> call within a single block.
    /// </summary>
    private sealed class RamCacheReadStream(SlidingRamCache cache, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= length || !cache.TryReadSpan(_position, buffer, out int read))
                return 0;

            _position += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= length || !cache.TryRead(_position, buffer, out int read))
                return new ValueTask<int>(0);

            _position += read;
            return new ValueTask<int>(read);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => length + offset
            };
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}