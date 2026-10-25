using System.Diagnostics;
using System.Reflection;
using System.Text;
using LMP.Core.Helpers;
using LMP.Core.Youtube;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Core.Youtube.Music;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Бенчмарки пропускной способности и эффективности использования памяти потоковых парсеров,
/// AST-дешифратора YouTube и механизма пост-дешифровочной компактификации кучи (Gen 2 / LOH).
/// </summary>
public static class StreamingMemoryBenchmarkTests
{
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

    // Прогрев JIT
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

    // Ровно одна строка длиной 63 символа: 24 (header) + 63 * 2 = ~152 байта (выровнено до 152–160 байт).
    TestAssert.True(bytesPerCall <= 168, $"Expected single string allocation (<= 168 B), got {bytesPerCall} B/call");
    Log.Info($"[Benchmark] SAPISIDHASH allocation: {bytesPerCall} B/call ({totalAllocated / 1024.0:F2} KB for {iterations} calls)");

    return Task.CompletedTask;
  }

  [TestMethod(TestCategory.Benchmark, "PlayerContext ReleaseRawScripts & Post-AST Heap Compaction", Group = TestGroups.Cipher, Order = 3)]
  public static Task BenchmarkAstAndLohCompactionAsync()
  {
    // 1. Проверяем корректность 64-битной сигнатуры P/Invoke в MemoryHelpers
    var pinvokeMethod = typeof(MemoryHelpers).GetMethod(
        "_SetProcessWorkingSetSize",
        BindingFlags.NonPublic | BindingFlags.Static);
    TestAssert.NotNull(pinvokeMethod);

    var parameters = pinvokeMethod.GetParameters();
    TestAssert.Equal(3, parameters.Length);
    TestAssert.Equal(typeof(nint), parameters[1].ParameterType);
    TestAssert.Equal(typeof(nint), parameters[2].ParameterType);

    // 2. Замеряем baseline закоммиченной памяти до тяжелой фазы
    MemoryHelpers.CompactManagedHeapNow();
    var baselineInfo = GC.GetGCMemoryInfo();
    long baselineCommittedMb = baselineInfo.TotalCommittedBytes / (1024 * 1024);

    // 3. Имитируем тяжелую фазу загрузки base.js (3 МБ в LOH) + препроцессированный скрипт + временный граф в Gen2/LOH
    var ctx = CreateSimulatedBloatedPlayerContext();
    TestAssert.Equal(16_384, ctx.PreprocessedJs!.Length);

    var bloatedInfo = GC.GetGCMemoryInfo();
    long bloatedCommitted = bloatedInfo.TotalCommittedBytes;
    long bloatedCommittedMb = bloatedCommitted / (1024 * 1024);
    long lohBeforeBytes = bloatedInfo.GenerationInfo.Length > 3
        ? bloatedInfo.GenerationInfo[3].SizeBeforeBytes
        : 0;

    // 4. Освобождаем скрипты контекста и замеряем время компактификации STW
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

  [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
  private static PlayerContext CreateSimulatedBloatedPlayerContext()
  {
    string syntheticBaseJs = new string('a', 1_500_000) + "signatureTimestamp:20590;clientVersion:\"2.20260301.01.00\"";
    var ctx = new PlayerContext("deadbeef", syntheticBaseJs);
    _ = ctx.GetOrPrepareScript(static () => new string('x', 16_384));

    var tempBuffers = new List<byte[]>(64);
    for (int i = 0; i < 64; i++)
    {
      // 64 * 1 МБ = 64 МБ в LOH + узлы в SOH
      var buf = new byte[1024 * 1024];
      buf[0] = (byte)i;
      buf[^1] = (byte)i;
      tempBuffers.Add(buf);
    }

    // Промоутим буферы через Gen 0 / Gen 1 сборку, как это происходит при парсинге 2.5 МБ AST
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

    var block = new LMP.Core.Audio.Sources.CachingStreamSource.RamRangeBlock(0, rented, chunkSize);

    // 1. Захватываем вторую ссылку для имитации фоновой записи на диск
    bool retained = block.TryRetainForDiskWrite(chunkSize, out var diskMemory);
    TestAssert.True(retained);
    TestAssert.Equal(chunkSize, diskMemory.Length);

    // 2. Имитируем вытеснение блока из SlidingRamCache ДО завершения записи на диск
    block.Dispose();

    // Буфер обязан оставаться валидным и содержать наши данные, пока диск не завершил запись
    TestAssert.Equal((byte)0xAA, diskMemory.Span[0]);
    TestAssert.Equal((byte)0x55, diskMemory.Span[chunkSize - 1]);

    // 3. Завершаем запись на диск — буфер возвращается в ArrayPool, повторное удержание невозможно
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