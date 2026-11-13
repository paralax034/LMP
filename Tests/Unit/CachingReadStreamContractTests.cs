using LMP.Tests.Framework;
using CachingStreamSource = LMP.Core.Audio.Sources.CachingStreamSource;
using RamRangeBlock = LMP.Core.Audio.Sources.CachingStreamSource.RamRangeBlock;
using SlidingRamCache = LMP.Core.Audio.Sources.CachingStreamSource.SlidingRamCache;

namespace LMP.Tests.Unit;

/// <summary>
/// Contract tests for <see cref="CachingStreamSource.AsyncCachingReadStream"/> and <see cref="SlidingRamCache"/>:
/// cancellation and disposal are not masked as EOF, RAM hit does not allocate.
/// </summary>
public static class CachingReadStreamContractTests
{
    private const int ContentLength = 4096;

    /// <summary>Direct access to the source's RAM cache without reflection, compatible with AOT.</summary>
    [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = "_ramCache")]
    private static extern ref SlidingRamCache SourceRamCache(CachingStreamSource source);

    private enum ReadOutcome : byte
    {
        Data,
        EndOfStream,
        Canceled,
        Disposed
    }

    /// <summary>
    /// Proves P1: ReadAsync returns 0 (EOF per Stream contract) upon cancellation and source disposal,
    /// even though position is far from the end; Mp4BinaryReader turns this into an empty header,
    /// and Fragment.cs into a ParserCorruptionException.
    /// Synchronous Read follows the same contract: RAM hit returns data and advances position,
    /// while disposal throws ObjectDisposedException instead of false EOF.
    /// </summary>
    [TestMethod(TestCategory.Unit, "AsyncCachingReadStream Cancel & Dispose Are Not EOF", Group = TestGroups.Audio, Order = 24)]
    public static async Task TestReadAsyncCancellationIsNotEndOfStreamAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "LMP_ReadContractTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            using var cacheManager = new Core.Audio.Cache.AudioCacheManager(tempDir, maxCacheSizeMb: 16);

            // Side-effect-free constructor: Init/Preload are not executed, network is not used
            var source = new CachingStreamSource(
                cacheKey: "test_read_contract",
                trackId: "yt_read_contract",
                url: "https://example.com/audio.mp4",
                contentLength: ContentLength,
                format: AudioFormat.Mp4,
                codec: Core.Audio.Interfaces.AudioCodec.Aac,
                bitrate: 128,
                cacheManager: cacheManager,
                config: new StreamingConfig());
            var stream = new CachingStreamSource.AsyncCachingReadStream(source);
            byte[] buffer = new byte[64];

            try
            {
                // 1. Canceled outer token at position 0 of 4096: OperationCanceledException expected, not EOF
                using (var cts = new CancellationTokenSource())
                {
                    cts.Cancel();
                    var outcome = await ReadOutcomeAsync(stream, buffer, cts.Token).ConfigureAwait(false);
                    TestAssert.Equal(ReadOutcome.Canceled, outcome,
                        $"Canceled ReadAsync at position 0 of {ContentLength} must throw OperationCanceledException, got {outcome}");
                    TestAssert.Equal(0L, stream.Position, "Canceled read must not advance position");
                }

                // 2. Synchronous RAM hit yields data and advances position via CAS
                byte[] payload = new byte[2048];
                for (int i = 0; i < payload.Length; i++)
                    payload[i] = (byte)(i * 3);
                AddBlock(SourceRamCache(source), payload, 0, payload.Length);

                int syncRead = stream.Read(buffer, 0, buffer.Length);
                TestAssert.Equal(buffer.Length, syncRead, "Sync Read must serve a RAM hit in full");
                TestAssert.Equal(buffer.Length, stream.Position, "Sync RAM hit must advance position");
                TestAssert.True(buffer.AsSpan().SequenceEqual(payload.AsSpan(0, buffer.Length)),
                    "Sync RAM hit must copy bytes from position 0");

                // 3. Disposed source: ObjectDisposedException expected, not EOF
                source.Dispose();
                var disposedOutcome = await ReadOutcomeAsync(stream, buffer, CancellationToken.None).ConfigureAwait(false);
                TestAssert.Equal(ReadOutcome.Disposed, disposedOutcome,
                    $"ReadAsync on a disposed source must throw ObjectDisposedException, got {disposedOutcome}");

                // 4. Synchronous Read on a disposed source follows the same contract
                var syncDisposedOutcome = SyncReadOutcome(stream, buffer);
                TestAssert.Equal(ReadOutcome.Disposed, syncDisposedOutcome,
                    $"Sync Read on a disposed source must throw ObjectDisposedException, got {syncDisposedOutcome}");
            }
            finally
            {
                stream.Dispose();
                source.Dispose();
            }
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Proper coverage for the RAM hit hot path (replacing HotPathAllocationTests Order 13):
    /// TryRead and TryReadSpan inside a block and across block boundaries — 0 B.
    /// </summary>
    [TestMethod(TestCategory.Unit, "SlidingRamCache RAM Hit TryRead/TryReadSpan (0 Alloc)", Group = TestGroups.Audio, Order = 25)]
    public static Task TestSlidingRamCacheHitZeroAllocAsync()
    {
        const int blockSize = 2048;
        const int edgeOffset = blockSize - 6;

        byte[] data = new byte[blockSize * 2];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)i;

        using var cache = new SlidingRamCache();
        AddBlock(cache, data, 0, blockSize);
        AddBlock(cache, data, blockSize, blockSize);

        byte[] destination = new byte[512];
        Memory<byte> destinationMemory = destination;

        TestAssert.ZeroAlloc(() =>
        {
            // Intra-block and cross-boundary read: the second is constrained by the remainder of the first block
            if (!cache.TryRead(0, destinationMemory, out int inside) || inside != destination.Length)
                TestAssert.Fail("TryRead must serve a full in-block read");
            if (!cache.TryReadSpan(edgeOffset, destination, out int edge) || edge != 6)
                TestAssert.Fail("TryReadSpan must stop at the block boundary");
        }, warmup: 20, iterations: 5_000, context: "SlidingRamCache RAM hit");

        TestAssert.Equal(data[edgeOffset], destination[0], "RAM hit must copy bytes from the requested offset");
        return Task.CompletedTask;
    }

    private static async Task<ReadOutcome> ReadOutcomeAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        try
        {
            int read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            return read == 0 ? ReadOutcome.EndOfStream : ReadOutcome.Data;
        }
        catch (ObjectDisposedException)
        {
            return ReadOutcome.Disposed;
        }
        catch (OperationCanceledException)
        {
            return ReadOutcome.Canceled;
        }
    }

    private static ReadOutcome SyncReadOutcome(Stream stream, byte[] buffer)
    {
        try
        {
            int read = stream.Read(buffer, 0, buffer.Length);
            return read == 0 ? ReadOutcome.EndOfStream : ReadOutcome.Data;
        }
        catch (ObjectDisposedException)
        {
            return ReadOutcome.Disposed;
        }
        catch (OperationCanceledException)
        {
            return ReadOutcome.Canceled;
        }
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
}