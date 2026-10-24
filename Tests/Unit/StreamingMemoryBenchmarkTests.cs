using System.Text;
using LMP.Core.Youtube.Music;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Бенчмарки пропускной способности и эффективности использования памяти потоковых парсеров.
/// Проверяют отсутствие утечек в LOH на больших пакетах данных без устаревших эмуляторов легаси-классов.
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