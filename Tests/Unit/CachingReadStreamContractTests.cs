using LMP.Tests.Framework;
using CachingStreamSource = LMP.Core.Audio.Sources.CachingStreamSource;
using RamRangeBlock = LMP.Core.Audio.Sources.CachingStreamSource.RamRangeBlock;
using SlidingRamCache = LMP.Core.Audio.Sources.CachingStreamSource.SlidingRamCache;

namespace LMP.Tests.Unit;

/// <summary>
/// Контрактные тесты <see cref="CachingStreamSource.AsyncCachingReadStream"/> и <see cref="SlidingRamCache"/>:
/// отмена и утилизация не маскируются под EOF, RAM-хит не аллоцирует.
/// </summary>
public static class CachingReadStreamContractTests
{
    private const int ContentLength = 4096;

    private enum ReadOutcome : byte
    {
        Data,
        EndOfStream,
        Canceled,
        Disposed
    }

    /// <summary>
    /// Доказывает P1: ReadAsync возвращает 0 (EOF по контракту Stream) при отмене и утилизации источника,
    /// хотя позиция далека от конца; Mp4BinaryReader превращает это в пустой заголовок,
    /// а Fragment.cs — в ParserCorruptionException.
    /// </summary>
    [TestMethod(TestCategory.Unit, "AsyncCachingReadStream Cancel & Dispose Are Not EOF", Group = TestGroups.Audio, Order = 24)]
    public static async Task TestReadAsyncCancellationIsNotEndOfStreamAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "LMP_ReadContractTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            using var cacheManager = new Core.Audio.Cache.AudioCacheManager(tempDir, maxCacheSizeMb: 16);

            // Конструктор без побочных эффектов: Init/Preload не запускаются, сеть не используется
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
                // 1. Отменённый внешний токен на позиции 0 из 4096: ожидается OperationCanceledException, а не EOF
                using (var cts = new CancellationTokenSource())
                {
                    cts.Cancel();
                    var outcome = await ReadOutcomeAsync(stream, buffer, cts.Token).ConfigureAwait(false);
                    TestAssert.Equal(ReadOutcome.Canceled, outcome,
                        $"Canceled ReadAsync at position 0 of {ContentLength} must throw OperationCanceledException, got {outcome}");
                    TestAssert.Equal(0L, stream.Position, "Canceled read must not advance position");
                }

                // 2. Утилизированный источник: ожидается ObjectDisposedException, а не EOF
                source.Dispose();
                var disposedOutcome = await ReadOutcomeAsync(stream, buffer, CancellationToken.None).ConfigureAwait(false);
                TestAssert.Equal(ReadOutcome.Disposed, disposedOutcome,
                    $"ReadAsync on a disposed source must throw ObjectDisposedException, got {disposedOutcome}");
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
    /// Честное покрытие горячего пути RAM-хита (взамен HotPathAllocationTests Order 13):
    /// TryRead и TryReadSpan внутри блока и на стыке блоков — 0 B.
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
            // Чтение внутри блока и на стыке: второе ограничено остатком первого блока
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