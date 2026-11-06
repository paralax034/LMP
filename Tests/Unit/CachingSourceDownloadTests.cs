using System.Buffers;
using System.Diagnostics;
using LMP.Tests.Framework;
using CacheByteRange = LMP.Core.Audio.Cache.CacheByteRange;
using CachingStreamSource = LMP.Core.Audio.Sources.CachingStreamSource;
using RamRangeBlock = LMP.Core.Audio.Sources.CachingStreamSource.RamRangeBlock;
using SlidingRamCache = LMP.Core.Audio.Sources.CachingStreamSource.SlidingRamCache;
using SlotWaitOutcome = LMP.Core.Audio.Sources.CachingStreamSource.SlotWaitOutcome;

namespace LMP.Tests.Unit;

/// <summary>
/// Regression tests for the <see cref="CachingStreamSource"/> download pipeline: priority boost instead of
/// waiting for a slot, disk read window without RAM block overlap, cache reading directly into the caller's buffer,
/// HTTP response body idle deadline, and linear merging of buffered ranges.
/// </summary>
public static class CachingSourceDownloadTests
{
    /// <summary>
    /// Critical reader does not wait for a slot behind background download: priority boost completes the wait immediately,
    /// abandoned wait returns the slot to the semaphore, timeout and cancellation complete without exceptions.
    /// </summary>
    [TestMethod(TestCategory.Unit, "Download Slot Wait: Promotion Skips Queue & Returns Slot", Group = TestGroups.Audio, Order = 26)]
    public static async Task TestSlotWaitPromotionAsync()
    {
        using var slots = new SemaphoreSlim(1, 1);
        Task never = new TaskCompletionSource().Task;

        // 1. An available slot is granted immediately
        var acquired = await CachingStreamSource
            .WaitForSlotOrPromotionAsync(slots, 1_000, never, CancellationToken.None)
            .ConfigureAwait(false);
        TestAssert.Equal(SlotWaitOutcome.Acquired, acquired);

        // 2. The only slot is occupied: priority boost completes the wait well before the timeout
        var promotion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long start = Stopwatch.GetTimestamp();
        Task<SlotWaitOutcome> queued = CachingStreamSource
            .WaitForSlotOrPromotionAsync(slots, 30_000, promotion.Task, CancellationToken.None);
        TestAssert.False(queued.IsCompleted, "Waiter must queue while the only slot is held");

        promotion.TrySetResult();
        var promoted = await queued.ConfigureAwait(false);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
        TestAssert.Equal(SlotWaitOutcome.Promoted, promoted);
        TestAssert.True(elapsed < TimeSpan.FromSeconds(5),
            $"Promotion must end the slot wait promptly, took {elapsed.TotalMilliseconds:F0}ms");

        // 3. Abandoned wait gets the released slot and immediately returns it to the semaphore
        slots.Release();
        bool slotReturned = SpinWait.SpinUntil(() => slots.CurrentCount == 1, TimeSpan.FromSeconds(5));
        TestAssert.True(slotReturned, $"Abandoned slot wait must hand the slot back, CurrentCount={slots.CurrentCount}");

        // 4. Without priority boost, an occupied slot results in TimedOut
        await slots.WaitAsync().ConfigureAwait(false);
        var timedOut = await CachingStreamSource
            .WaitForSlotOrPromotionAsync(slots, 50, never, CancellationToken.None)
            .ConfigureAwait(false);
        TestAssert.Equal(SlotWaitOutcome.TimedOut, timedOut);

        // 5. Cancellation completes the wait cleanly
        using var cts = new CancellationTokenSource();
        Task<SlotWaitOutcome> canceled = CachingStreamSource
            .WaitForSlotOrPromotionAsync(slots, 30_000, never, cts.Token);
        cts.Cancel();
        TestAssert.Equal(SlotWaitOutcome.Cancelled, await canceled.ConfigureAwait(false));
        slots.Release();
    }

    /// <summary>
    /// Disk read window starts after the left RAM neighbor and ends before the right one,
    /// so the loaded block is accepted by <see cref="SlidingRamCache.TryAdd"/> rather than discarded.
    /// </summary>
    [TestMethod(TestCategory.Unit, "Disk Load Window Never Overlaps RAM Neighbors", Group = TestGroups.Audio, Order = 27)]
    public static Task TestDiskLoadWindowRespectsRamNeighborsAsync()
    {
        const int alignment = 4_096;
        const int desiredLength = 65_536;

        using var cache = new SlidingRamCache();
        AddBlock(cache, 0, 1_000);       // left neighbor does not end on an alignment boundary
        AddBlock(cache, 8_192, 4_096);   // right neighbor is inside the desired window

        // 1. Position inside a block: no free window
        TestAssert.False(cache.TryGetFreeWindow(500, out _, out _), "Covered position must not report a free window");

        // 2. Free gap between neighbors
        TestAssert.True(cache.TryGetFreeWindow(1_500, out long freeStart, out long freeEnd), "Gap position must report a free window");
        TestAssert.Equal(1_000L, freeStart);
        TestAssert.Equal(8_192L, freeEnd);

        // 3. Disk covers [0, 100 000), but the window is restricted to the gap [1000, 8192)
        TestAssert.True(CachingStreamSource.TryComputeDiskLoadWindow(
            1_500, desiredLength, alignment, 0, 100_000, freeStart, freeEnd,
            out long loadStart, out int loadLength), "Window must contain the miss position");
        TestAssert.Equal(1_000L, loadStart);
        TestAssert.Equal(7_192, loadLength);

        // 4. Legacy window AlignDown + desiredLength overlaps both neighbors and is rejected by cache — root cause of re-reads
        TestAssert.False(TryAddBlock(cache, 0, desiredLength), "Legacy aligned window must collide with RAM neighbors");
        TestAssert.True(TryAddBlock(cache, loadStart, loadLength), "Window computed from the free gap must be committed to RAM");

        // 5. No neighbors: align down and truncate at the end of the disk range
        TestAssert.True(CachingStreamSource.TryComputeDiskLoadWindow(
            5_000, desiredLength, alignment, 4_096, 6_000, 0, long.MaxValue,
            out long tailStart, out int tailLength), "Tail window must contain the miss position");
        TestAssert.Equal(4_096L, tailStart);
        TestAssert.Equal(1_904, tailLength);

        return Task.CompletedTask;
    }

    /// <summary>
    /// <see cref="Core.Audio.Cache.AudioCacheManager.ReadRangeIntoAsync"/> reads the downloaded range directly into
    /// the caller's buffer and returns 0 for undownloaded, partially downloaded ranges, or an unknown key.
    /// </summary>
    [TestMethod(TestCategory.Unit, "AudioCacheManager ReadRangeIntoAsync Fills Caller Buffer", Group = TestGroups.Storage, Order = 28)]
    public static async Task TestReadRangeIntoCallerBufferAsync()
    {
        const string cacheKey = "test_read_into";
        const int totalSize = 8_192;
        string tempDir = Path.Combine(Path.GetTempPath(), "LMP_ReadIntoTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            using var cacheManager = new Core.Audio.Cache.AudioCacheManager(tempDir, maxCacheSizeMb: 16);
            cacheManager.CreateOrUpdate(
                cacheKey, "yt_read_into", "https://example.com/audio.mp4", totalSize,
                AudioFormat.Mp4, Core.Audio.Interfaces.AudioCodec.Aac, bitrate: 128, alignmentBytes: 4_096);

            byte[] payload = new byte[4_096];
            for (int i = 0; i < payload.Length; i++)
                payload[i] = (byte)(i * 7);
            await cacheManager.WriteRangeAsync(cacheKey, 0, payload).ConfigureAwait(false);

            byte[] destination = new byte[1_024];

            // 1. Downloaded range is read completely
            int read = await cacheManager.ReadRangeIntoAsync(cacheKey, 1_000, destination).ConfigureAwait(false);
            TestAssert.Equal(1_024, read);
            TestAssert.True(destination.AsSpan().SequenceEqual(payload.AsSpan(1_000, 1_024)), "Bytes must match the written payload");

            // 2. Range on the boundary of the downloaded part — cache miss without partial data
            TestAssert.Equal(0, await cacheManager.ReadRangeIntoAsync(cacheKey, 3_584, destination).ConfigureAwait(false));

            // 3. Undownloaded range and unknown key — cache miss
            TestAssert.Equal(0, await cacheManager.ReadRangeIntoAsync(cacheKey, 4_096, destination).ConfigureAwait(false));
            TestAssert.Equal(0, await cacheManager.ReadRangeIntoAsync("missing_key", 0, destination).ConfigureAwait(false));
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// HTTP response body idle deadline: a hung connection is aborted, while a slow but active transfer
    /// longer than the deadline finishes successfully because each chunk resets the timer.
    /// </summary>
    [TestMethod(TestCategory.Unit, "HTTP Body Idle Deadline: Stall Aborts, Slow Transfer Survives", Group = TestGroups.Audio, Order = 29)]
    public static async Task TestReadStreamFullyIdleDeadlineAsync()
    {
        byte[] buffer = new byte[8 * 1_024];

        // 1. Two chunks then silence: read completes on deadline rather than hanging indefinitely
        using (var stalled = new ScriptedBodyStream(chunkSize: 1_024, chunksBeforeStall: 2, chunkDelayMs: 0))
        using (var idleCts = new CancellationTokenSource())
        {
            long start = Stopwatch.GetTimestamp();
            int read = await CachingStreamSource
                .ReadStreamFullyAsync(stalled, buffer, idleCts, idleTimeoutMs: 300)
                .ConfigureAwait(false);
            TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

            TestAssert.Equal(2_048, read);
            TestAssert.True(idleCts.IsCancellationRequested, "Stalled body must trip the idle deadline");
            TestAssert.True(elapsed < TimeSpan.FromSeconds(5), $"Stalled body must abort promptly, took {elapsed.TotalMilliseconds:F0}ms");
        }

        // 2. Eight chunks at 150 ms each (around 1.2 s) with a 600 ms deadline
        using (var slow = new ScriptedBodyStream(chunkSize: 1_024, chunksBeforeStall: int.MaxValue, chunkDelayMs: 150))
        using (var idleCts = new CancellationTokenSource())
        {
            int read = await CachingStreamSource
                .ReadStreamFullyAsync(slow, buffer, idleCts, idleTimeoutMs: 600)
                .ConfigureAwait(false);

            TestAssert.Equal(buffer.Length, read);
            TestAssert.False(idleCts.IsCancellationRequested, "Live transfer must not trip the idle deadline");
        }
    }

    /// <summary>
    /// Linear merging of disk ranges and RAM blocks: overlapping, adjacency, and gaps,
    /// with zero-allocation result counting and population.
    /// </summary>
    [TestMethod(TestCategory.Unit, "MergeBufferedRanges Linear Disk+RAM Merge (0 Alloc)", Group = TestGroups.Audio, Order = 30)]
    public static Task TestMergeBufferedRangesAsync()
    {
        const long contentLength = 10_000;
        CacheByteRange[] disk = [new(0, 1_000), new(3_000, 4_000), new(8_000, 9_000)];

        using var cache = new SlidingRamCache();
        AddBlock(cache, 900, 600);     // overlaps [0, 1000)  -> [0, 1500)
        AddBlock(cache, 4_000, 500);   // adjacent to [3000, 4000)  -> [3000, 4500)
        AddBlock(cache, 6_000, 500);   // standalone segment
        RamRangeBlock[] ram = cache.GetRangesSnapshot();

        int count = CachingStreamSource.MergeBufferedRanges(disk, ram, contentLength, []);
        TestAssert.Equal(4, count);

        var merged = new (double Start, double End)[count];
        TestAssert.Equal(4, CachingStreamSource.MergeBufferedRanges(disk, ram, contentLength, merged));
        TestAssert.Equal((0.0, 0.15), merged[0]);
        TestAssert.Equal((0.3, 0.45), merged[1]);
        TestAssert.Equal((0.6, 0.65), merged[2]);
        TestAssert.Equal((0.8, 0.9), merged[3]);

        TestAssert.ZeroAlloc(() =>
        {
            if (CachingStreamSource.MergeBufferedRanges(disk, ram, contentLength, default) != 4
                || CachingStreamSource.MergeBufferedRanges(disk, ram, contentLength, merged) != 4)
                TestAssert.Fail("MergeBufferedRanges must return the same segment count on every pass");
        }, warmup: 10, iterations: 5_000, context: "MergeBufferedRanges");

        return Task.CompletedTask;
    }

    /// <summary>
    /// A critical request from an aligned-down start covers the entire reader window from an unaligned seek position
    /// and does not exceed the end of the content.
    /// </summary>
    [TestMethod(TestCategory.Unit, "Critical Download Span Covers Unaligned Seek Head", Group = TestGroups.Audio, Order = 31)]
    public static Task TestCriticalSpanCoversUnalignedRequestAsync()
    {
        const int alignment = 16_384;
        const int minimumLength = 49_152;
        const long contentLength = 3_657_225;
        const long requested = 1_090_059;
        const long planStart = requested - requested % alignment; // 1_081_344: head of 8_715 bytes before position

        // 1. Unaligned position: length increases by the head, plan ends exactly where the reader window ends
        int span = CachingStreamSource.ComputeCriticalSpan(requested, minimumLength, planStart, contentLength);
        TestAssert.Equal(57_867, span);
        TestAssert.Equal(requested + minimumLength, planStart + span, "Critical plan must end exactly where the reader window ends");

        // 2. Aligned position: exactly the minimum
        TestAssert.Equal(minimumLength, CachingStreamSource.ComputeCriticalSpan(planStart, minimumLength, planStart, contentLength));

        // 3. Start shifted past locally available data: minimum is calculated from the plan start
        TestAssert.Equal(minimumLength, CachingStreamSource.ComputeCriticalSpan(requested, minimumLength, planStart + 3 * alignment, contentLength));

        // 4. File tail: length is truncated by the end of content, plan is empty past the end
        TestAssert.Equal(18_656, CachingStreamSource.ComputeCriticalSpan(requested, minimumLength, planStart, 1_100_000));
        TestAssert.Equal(0, CachingStreamSource.ComputeCriticalSpan(requested, minimumLength, contentLength, contentLength));

        return Task.CompletedTask;
    }

    private static void AddBlock(SlidingRamCache cache, long offset, int length)
    {
        if (!TryAddBlock(cache, offset, length))
            TestAssert.Fail($"Failed to add RAM block [{offset}, {offset + length})");
    }

    private static bool TryAddBlock(SlidingRamCache cache, long offset, int length)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        var block = new RamRangeBlock(offset, rented, length);
        if (cache.TryAdd(block))
            return true;

        block.Dispose();
        return false;
    }

    /// <summary>
    /// HTTP response body scenario: delayed chunks, then hanging until token cancellation.
    /// </summary>
    private sealed class ScriptedBodyStream(int chunkSize, int chunksBeforeStall, int chunkDelayMs) : Stream
    {
        private int _served;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // Hung connection: no new data, waiting completes only via cancellation, like a socket
            if (_served >= chunksBeforeStall)
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

            if (chunkDelayMs > 0)
                await Task.Delay(chunkDelayMs, cancellationToken).ConfigureAwait(false);

            _served++;
            int length = Math.Min(chunkSize, buffer.Length);
            buffer.Span[..length].Fill(0x5A);
            return length;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}