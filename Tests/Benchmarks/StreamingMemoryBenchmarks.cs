using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using LMP.Core.Youtube;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Core.Youtube.Music;
using LMP.Tests.Framework;
using TokenExtensions = LMP.Core.Helpers.Extensions.CancellationTokenExtensions;

namespace LMP.Tests.Benchmarks;

/// <summary>
/// Throughput, memory efficiency, heap compaction, and exception-free cancellation benchmarks.
/// </summary>
public static class StreamingMemoryBenchmarks
{
    private const int CancelIterations = 2_000;
    private const int MeasureRounds = 5;
    private const int RoundIterations = CancelIterations / MeasureRounds;
    private static readonly TimeSpan ThrowingPathBudget = TimeSpan.FromMilliseconds(250);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "_SetProcessWorkingSetSize")]
    private static extern bool SetProcessWorkingSetSize(
        [UnsafeAccessorType("LMP.Core.Helpers.MemoryHelpers, LMP.Core")] object? owner,
        nint process,
        nint minimumWorkingSetSize,
        nint maximumWorkingSetSize);

    [TestMethod(TestCategory.Benchmark, "YTM 600-Track Parser Throughput & LOH Invariance", Group = TestGroups.Youtube, Order = 1)]
    public static async Task BenchmarkYtmStreaming600TracksAsync()
    {
        var payload = GenerateSyntheticMusicPlaylistJson(trackCount: 600);

        long gen2Before = GC.CollectionCount(2);
        long startBytes = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 5; i++)
        {
            using var ms = new MemoryStream(payload);
            var (data, _, _) = await PlaylistStreamingParser.ParseBrowseAsync(ms).ConfigureAwait(false);
            TestAssert.Equal(600, data.Tracks.Count);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - startBytes;
        long gen2Delta = GC.CollectionCount(2) - gen2Before;

        TestAssert.True(gen2Delta <= 1, $"Excessive LOH GC pressure: {gen2Delta} Gen2 collections");
        Log.Info($"[Benchmark] Parsed 600 tracks x 5 runs. Total allocated: {allocated / 1024:N0} KB");
    }

    [TestMethod(TestCategory.Benchmark, "SAPISIDHASH Zero-Intermediate-Allocation Benchmark", Group = TestGroups.Youtube, Order = 2)]
    public static Task BenchmarkSapisidHashZeroIntermediateAllocAsync()
    {
        const string sapisid = "1234567890abcdef1234567890abcdef12";
        const string origin = "https://music.youtube.com";

        var warmup = YoutubeHttpHandler.GetAuthHeader(sapisid, origin);
        TestAssert.NotNull(warmup);
        TestAssert.True(warmup.StartsWith("SAPISIDHASH ", StringComparison.Ordinal));

        long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        const int iterations = 1_000;

        for (int i = 0; i < iterations; i++)
        {
            _ = YoutubeHttpHandler.GetAuthHeader(sapisid, origin)
                ?? throw new InvalidOperationException();
        }

        long totalAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
        long bytesPerCall = totalAllocated / iterations;

        TestAssert.True(bytesPerCall <= 168, $"Expected single string allocation (<= 168 B), got {bytesPerCall} B/call");
        Log.Info($"[Benchmark] SAPISIDHASH allocation: {bytesPerCall} B/call ({totalAllocated / 1024.0:F2} KB for {iterations} calls)");

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Benchmark, "PlayerContext ReleaseRawScripts & Post-AST Heap Compaction", Group = TestGroups.Cipher, Order = 3)]
    public static Task BenchmarkAstAndLohCompactionAsync()
    {
        if (OperatingSystem.IsWindows())
            TestAssert.False(
                SetProcessWorkingSetSize(null, nint.Zero, -1, -1),
                "SetProcessWorkingSetSize must reject a null process handle");

        MemoryHelpers.CompactManagedHeapNow();
        var baselineInfo = GC.GetGCMemoryInfo();
        long baselineCommittedMb = baselineInfo.TotalCommittedBytes / (1024 * 1024);

        var ctx = CreateSimulatedBloatedPlayerContext();
        TestAssert.Equal(16_384, ctx.PreprocessedJs!.Length);

        var bloatedInfo = GC.GetGCMemoryInfo();
        long bloatedCommitted = bloatedInfo.TotalCommittedBytes;
        long bloatedCommittedMb = bloatedCommitted / (1024 * 1024);

        ctx.ReleaseRawScripts();
        TestAssert.Equal(string.Empty, ctx.BaseJs);
        TestAssert.Null(ctx.PreprocessedJs);

        var sw = Stopwatch.StartNew();
        MemoryHelpers.CompactManagedHeapNow();
        sw.Stop();

        var compactedInfo = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        long compactedCommitted = compactedInfo.TotalCommittedBytes;
        long compactedCommittedMb = compactedCommitted / (1024 * 1024);
        long reclaimedMb = (bloatedCommitted - compactedCommitted) / (1024 * 1024);
        long lohBeforeBytes = compactedInfo.GenerationInfo.Length > 3
            ? compactedInfo.GenerationInfo[3].SizeBeforeBytes
            : 0;
        long lohAfterBytes = compactedInfo.GenerationInfo.Length > 3
            ? compactedInfo.GenerationInfo[3].SizeAfterBytes
            : 0;

        Log.Info(
            $"[Benchmark] Heap Compaction: Baseline={baselineCommittedMb} MB -> Peak={bloatedCommittedMb} MB -> " +
            $"Compacted={compactedCommittedMb} MB (Reclaimed: {reclaimedMb} MB, LOH: {lohBeforeBytes / 1024} KB -> {lohAfterBytes / 1024} KB, " +
            $"Duration: {sw.Elapsed.TotalMilliseconds:F1} ms)");

        long lohReclaimedMb = (lohBeforeBytes - lohAfterBytes) / (1024 * 1024);

        TestAssert.True(compactedCommitted < bloatedCommitted, $"Expected committed heap to shrink after CompactManagedHeapNow ({bloatedCommitted} -> {compactedCommitted})");
        TestAssert.True(lohReclaimedMb >= 55, $"Expected at least 55 MB of LOH memory reclaimed, got {lohReclaimedMb} MB");
        TestAssert.True(reclaimedMb >= 25, $"Expected at least 25 MB of OS committed pages decommitted immediately, got {reclaimedMb} MB");

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Benchmark, "Cancellation Cost: Task.Delay(ms, ct) vs DelayNoThrowAsync",
        Group = TestGroups.System, Order = 21, TimeoutSeconds = 60)]
    public static async Task TestCancellationCostBenchmarkAsync()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var token = cts.Token;
        bool debuggerAttached = Debugger.IsAttached;

        for (int i = 0; i < 50; i++)
        {
            if (!debuggerAttached)
                _ = await ThrowingDelayAsync(token).ConfigureAwait(false);
            _ = await TokenExtensions.DelayNoThrowAsync(token, 100, continueOnCapturedContext: false).ConfigureAwait(false);
        }

        int threadId = Environment.CurrentManagedThreadId;

        double[] noThrowRounds = new double[MeasureRounds];
        int canceledNoThrow = 0;
        long noThrowAllocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int round = 0; round < MeasureRounds; round++)
        {
            long roundStart = Stopwatch.GetTimestamp();
            for (int i = 0; i < RoundIterations; i++)
            {
                if (!await TokenExtensions.DelayNoThrowAsync(token, 100, continueOnCapturedContext: false).ConfigureAwait(false))
                    canceledNoThrow++;
            }

            noThrowRounds[round] = Stopwatch.GetElapsedTime(roundStart).TotalNanoseconds / RoundIterations;
        }
        long noThrowBytes = GC.GetAllocatedBytesForCurrentThread() - noThrowAllocBefore;
        double noThrowNs = MedianInPlace(noThrowRounds);

        if (Environment.CurrentManagedThreadId != threadId)
            TestAssert.Skip("Continuation hopped threads; per-thread allocation counter is not comparable");

        Log.Info($"[Benchmark] DelayNoThrowAsync        | {noThrowNs,10:F1} ns/cancel | {(double)noThrowBytes / CancelIterations,8:F1} B/cancel | median of {MeasureRounds}");

        TestAssert.Equal(CancelIterations, canceledNoThrow, "Every pre-canceled DelayNoThrowAsync must return false");
        TestAssert.Equal(0L, noThrowBytes, "DelayNoThrowAsync cancellation path must allocate 0 B");

        if (debuggerAttached)
        {
            TestAssert.Skip("Debugger attached: throwing path skipped, every first-chance exception suspends all managed threads");
        }
        else
        {
            double[] throwRounds = new double[MeasureRounds];
            int completedRounds = 0;
            int thrown = 0;
            int throwIterations = 0;
            long throwAllocBefore = GC.GetAllocatedBytesForCurrentThread();
            long throwStart = Stopwatch.GetTimestamp();
            while (completedRounds < MeasureRounds && Stopwatch.GetElapsedTime(throwStart) <= ThrowingPathBudget)
            {
                long roundStart = Stopwatch.GetTimestamp();
                for (int i = 0; i < RoundIterations; i++)
                {
                    if (await ThrowingDelayAsync(token).ConfigureAwait(false))
                        thrown++;
                }

                throwIterations += RoundIterations;
                throwRounds[completedRounds++] = Stopwatch.GetElapsedTime(roundStart).TotalNanoseconds / RoundIterations;
            }
            long throwBytes = GC.GetAllocatedBytesForCurrentThread() - throwAllocBefore;
            double throwNs = MedianInPlace(throwRounds.AsSpan(0, completedRounds));

            if (Environment.CurrentManagedThreadId != threadId)
                TestAssert.Skip("Continuation hopped threads; per-thread allocation counter is not comparable");

            Log.Info($"[Benchmark] Task.Delay(ms, ct) + catch | {throwNs,10:F1} ns/cancel | {(double)throwBytes / throwIterations,8:F1} B/cancel | n={throwIterations} | median of {completedRounds}");

            TestAssert.Equal(throwIterations, thrown, "Every pre-canceled Task.Delay must throw");
            TestAssert.True(throwBytes > 0, "Throwing cancellation path is expected to allocate (exception + canceled task)");
            TestAssert.True(throwNs >= noThrowNs * 10.0, $"Expected >=10x overhead for throwing path: {throwNs:F1}ns vs {noThrowNs:F1}ns");
        }
    }

    private static async Task<bool> ThrowingDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(100, token).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    private static double MedianInPlace(Span<double> rounds)
    {
        rounds.Sort();
        int mid = rounds.Length / 2;
        return (rounds.Length & 1) != 0 ? rounds[mid] : 0.5 * (rounds[mid - 1] + rounds[mid]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PlayerContext CreateSimulatedBloatedPlayerContext()
    {
        string syntheticBaseJs = new string('a', 1_500_000) + "signatureTimestamp:20590;clientVersion:\"2.20260301.01.00\"";
        var ctx = new PlayerContext("deadbeef", syntheticBaseJs);
        _ = ctx.GetOrPrepareScript(static () => new string('x', 16_384));

        var tempBuffers = new List<byte[]>(64);
        for (int i = 0; i < 64; i++)
        {
            var buf = new byte[1024 * 1024];
            buf[0] = (byte)i;
            buf[^1] = (byte)i;
            tempBuffers.Add(buf);
        }

        GC.Collect(1, GCCollectionMode.Forced, blocking: true);
        GC.KeepAlive(tempBuffers);

        return ctx;
    }

    private static byte[] GenerateSyntheticMusicPlaylistJson(int trackCount)
    {
        var sb = new StringBuilder(trackCount * 1024);
        sb.Append("""
        {
          "header": {
            "musicResponsiveHeaderRenderer": {
              "title": { "runs": [ { "text": "Benchmark Playlist" } ] }
            }
          },
          "contents": {
            "singleColumnBrowseResultsRenderer": {
              "tabs": [
                {
                  "tabRenderer": {
                    "content": {
                      "sectionListRenderer": {
                        "contents": [
                          {
                            "musicPlaylistShelfRenderer": {
                              "contents": [
        """);

        for (int i = 0; i < trackCount; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($$"""
            {
              "musicResponsiveListItemRenderer": {
                "playlistItemData": {
                  "videoId": "vid_track_{{i:D4}}",
                  "playlistSetVideoId": "set_item_{{i:D4}}"
                },
                "flexColumns": [
                  {
                    "musicResponsiveListItemFlexColumnRenderer": {
                      "text": { "runs": [ { "text": "Track {{i}}" } ] }
                    }
                  },
                  {
                    "musicResponsiveListItemFlexColumnRenderer": {
                      "text": { "runs": [ { "text": "Artist {{i}}" } ] }
                    }
                  }
                ],
                "fixedColumns": [
                  {
                    "musicResponsiveListItemFixedColumnRenderer": {
                      "text": { "runs": [ { "text": "3:30" } ] }
                    }
                  }
                ],
                "thumbnail": {
                  "musicThumbnailRenderer": {
                    "thumbnail": {
                      "thumbnails": [ { "url": "https://img.test/thumb_{{i}}.jpg" } ]
                    }
                  }
                }
              }
            }
            """);
        }

        sb.Append("""
                              ]
                            }
                          }
                        ]
                      }
                    }
                  }
                }
              ]
            }
          }
        }
        """);

        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}