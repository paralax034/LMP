using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using LMP.Core.Youtube;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Core.Youtube.Music;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Throughput and memory efficiency benchmarks for streaming parsers,
/// YouTube AST decipherer, and post-deciphering heap compactification mechanism (Gen 2 / LOH).
/// </summary>
public static class StreamingMemoryBenchmarkTests
{
  /// <summary>
  /// P/Invoke <c>SetProcessWorkingSetSize</c> from MemoryHelpers without reflection: bound strictly to the
  /// <c>(nint, nint, nint) -> bool</c> signature, so falling back to 32-bit parameters causes a <see cref="MissingMethodException"/>.
  /// The first parameter specifies the static owner type and is always <c>null</c>.
  /// </summary>
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

    // JIT warmup
    var warmup = YoutubeHttpHandler.GetAuthHeader(sapisid, origin);
    TestAssert.NotNull(warmup);
    TestAssert.True(warmup.StartsWith("SAPISIDHASH ", StringComparison.Ordinal));

    long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
    const int iterations = 1_000;

    for (int i = 0; i < iterations; i++)
    {
      var header = YoutubeHttpHandler.GetAuthHeader(sapisid, origin);
      if (header is null) throw new InvalidOperationException();
    }

    long totalAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
    long bytesPerCall = totalAllocated / iterations;

    // Exactly one string with a length of 63 characters: 24 (header) + 63 * 2 = ~152 bytes (aligned to 152–160 bytes).
    TestAssert.True(bytesPerCall <= 168, $"Expected single string allocation (<= 168 B), got {bytesPerCall} B/call");
    Log.Info($"[Benchmark] SAPISIDHASH allocation: {bytesPerCall} B/call ({totalAllocated / 1024.0:F2} KB for {iterations} calls)");

    return Task.CompletedTask;
  }

  [TestMethod(TestCategory.Benchmark, "PlayerContext ReleaseRawScripts & Post-AST Heap Compaction", Group = TestGroups.Cipher, Order = 3)]
  public static Task BenchmarkAstAndLohCompactionAsync()
  {
    // 1. Verify the 64-bit P/Invoke signature in MemoryHelpers. Zero handle is invalid: the call reaches kernel32
    //    and returns false without touching the process Working Set before measurement.
    if (OperatingSystem.IsWindows())
      TestAssert.False(
          SetProcessWorkingSetSize(null, nint.Zero, -1, -1),
          "SetProcessWorkingSetSize must reject a null process handle");

    // 2. Measure committed memory baseline prior to the heavy phase
    MemoryHelpers.CompactManagedHeapNow();
    var baselineInfo = GC.GetGCMemoryInfo();
    long baselineCommittedMb = baselineInfo.TotalCommittedBytes / (1024 * 1024);

    // 3. Simulate the heavy base.js loading phase (3 MB in LOH) + preprocessed script + temporary graph in Gen2/LOH
    var ctx = CreateSimulatedBloatedPlayerContext();
    TestAssert.Equal(16_384, ctx.PreprocessedJs!.Length);

    var bloatedInfo = GC.GetGCMemoryInfo();
    long bloatedCommitted = bloatedInfo.TotalCommittedBytes;
    long bloatedCommittedMb = bloatedCommitted / (1024 * 1024);
    long lohBeforeBytes = bloatedInfo.GenerationInfo.Length > 3
        ? bloatedInfo.GenerationInfo[3].SizeBeforeBytes
        : 0;

    // 4. Release context scripts and measure STW compactification time
    ctx.ReleaseRawScripts();
    TestAssert.Equal(string.Empty, ctx.BaseJs);
    TestAssert.Null(ctx.PreprocessedJs);

    var sw = Stopwatch.StartNew();
    MemoryHelpers.CompactManagedHeapNow();
    sw.Stop();

    var compactedInfo = GC.GetGCMemoryInfo();
    long compactedCommitted = compactedInfo.TotalCommittedBytes;
    long compactedCommittedMb = compactedCommitted / (1024 * 1024);
    long reclaimedMb = (bloatedCommitted - compactedCommitted) / (1024 * 1024);
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

  [System.Runtime.CompilerServices.MethodImpl(MethodImplOptions.NoInlining)]
  private static PlayerContext CreateSimulatedBloatedPlayerContext()
  {
    string syntheticBaseJs = new string('a', 1_500_000) + "signatureTimestamp:20590;clientVersion:\"2.20260301.01.00\"";
    var ctx = new PlayerContext("deadbeef", syntheticBaseJs);
    _ = ctx.GetOrPrepareScript(static () => new string('x', 16_384));

    var tempBuffers = new List<byte[]>(64);
    for (int i = 0; i < 64; i++)
    {
      // 64 * 1 MB = 64 MB in LOH + nodes in SOH
      var buf = new byte[1024 * 1024];
      buf[0] = (byte)i;
      buf[^1] = (byte)i;
      tempBuffers.Add(buf);
    }

    // Promote buffers via Gen 0 / Gen 1 collection, matching the behavior during 2.5 MB AST parsing
    GC.Collect(1, GCCollectionMode.Forced, blocking: true);
    GC.KeepAlive(tempBuffers);

    return ctx;
  }

  [TestMethod(TestCategory.Unit, "RamRangeBlock RefCounted Zero-Copy Lifecycle", Group = TestGroups.Audio, Order = 4)]
  public static Task TestRamRangeBlockZeroCopyRefCountAsync()
  {
    const int chunkSize = 384 * 1024;
    byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(chunkSize);
    rented[0] = 0xAA;
    rented[chunkSize - 1] = 0x55;

    var block = new Core.Audio.Sources.CachingStreamSource.RamRangeBlock(0, rented, chunkSize);

    // 1. Acquire a second reference to simulate background disk write
    bool retained = block.TryRetainForDiskWrite(chunkSize, out var diskMemory);
    TestAssert.True(retained);
    TestAssert.Equal(chunkSize, diskMemory.Length);

    // 2. Simulate block eviction from SlidingRamCache BEFORE the disk write completes
    block.Dispose();

    // The buffer must remain valid and retain our data until the disk write completes
    TestAssert.Equal((byte)0xAA, diskMemory.Span[0]);
    TestAssert.Equal((byte)0x55, diskMemory.Span[chunkSize - 1]);

    // 3. Complete disk write — the buffer is returned to the ArrayPool, preventing re-retention
    block.ReleaseReference();
    bool retainedAfterRelease = block.TryRetainForDiskWrite(chunkSize, out _);
    TestAssert.False(retainedAfterRelease);

    return Task.CompletedTask;
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