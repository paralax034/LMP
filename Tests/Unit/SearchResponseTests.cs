using System.Text;
using LMP.Core.Youtube.Bridge;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Модульные тесты для проверки парсинга поисковой выдачи <see cref="SearchResponse"/>.
/// </summary>
public static class SearchResponseTests
{
    private const string SampleSearchJson = """
    {
      "contents": {
        "twoColumnSearchResultsRenderer": {
          "primaryContents": {
            "sectionListRenderer": {
              "contents": [
                {
                  "itemSectionRenderer": {
                    "contents": [
                      {
                        "videoRenderer": {
                          "videoId": "vid_search_01",
                          "title": { "runs": [ { "text": "Search Video Title" } ] },
                          "ownerText": { "runs": [ { "text": "Channel Creator" } ] },
                          "lengthText": { "simpleText": "4:20" },
                          "thumbnail": {
                            "thumbnails": [ { "url": "https://img.test/video.jpg", "width": 320, "height": 180 } ]
                          }
                        }
                      },
                      {
                        "playlistRenderer": {
                          "playlistId": "PL_SEARCH_PLAYLIST_01",
                          "title": { "simpleText": "Search Playlist Title" },
                          "shortBylineText": { "runs": [ { "text": "Playlist Author" } ] },
                          "thumbnail": {
                            "thumbnails": [ { "url": "https://img.test/playlist.jpg" } ]
                          }
                        }
                      },
                      {
                        "channelRenderer": {
                          "channelId": "UC_SEARCH_CHANNEL_01",
                          "title": { "simpleText": "Official Search Channel" },
                          "thumbnail": {
                            "thumbnails": [ { "url": "https://img.test/channel.jpg" } ]
                          }
                        }
                      },
                      {
                        "continuationItemRenderer": {
                          "continuationEndpoint": {
                            "continuationCommand": {
                              "token": "SEARCH_CONTINUATION_TOKEN_999"
                            }
                          }
                        }
                      }
                    ]
                  }
                }
              ]
            }
          }
        }
      }
    }
    """;

    /// <summary>
    /// Проверяет, что потоковый разбор SearchResponse корректно извлекает видео, плейлисты, каналы и токен.
    /// </summary>
    [TestMethod(TestCategory.Unit, "Search: Stream Parse Integrity", Group = TestGroups.Pipeline, Order = 5)]
    public static async Task TestSearchResponseIntegrityAsync()
    {
        var bytes = Encoding.UTF8.GetBytes(SampleSearchJson);
        using var stream = new MemoryStream(bytes);

        var response = await SearchResponse.ParseAsync(stream).ConfigureAwait(false);

        Assert(response != null, "SearchResponse is null");

        // 1. Проверка VideoData
        Assert(response!.Videos.Count == 1, $"Expected 1 video, got {response.Videos.Count}");
        var v = response.Videos[0];
        Assert(v.Id == "vid_search_01", "Video ID mismatch");
        Assert(v.Title == "Search Video Title", "Video Title mismatch");
        Assert(v.Author == "Channel Creator", "Video Author mismatch");
        Assert(v.Duration == TimeSpan.FromSeconds(260), "Video Duration mismatch");
        Assert(v.Thumbnails.Count == 1, "Video Thumbnails count mismatch");

        // 2. Проверка PlaylistData
        Assert(response.Playlists.Count == 1, $"Expected 1 playlist, got {response.Playlists.Count}");
        var p = response.Playlists[0];
        Assert(p.Id == "PL_SEARCH_PLAYLIST_01", "Playlist ID mismatch");
        Assert(p.Title == "Search Playlist Title", "Playlist Title mismatch");
        Assert(p.Author == "Playlist Author", "Playlist Author mismatch");

        // 3. Проверка ChannelData
        Assert(response.Channels.Count == 1, $"Expected 1 channel, got {response.Channels.Count}");
        var c = response.Channels[0];
        Assert(c.Id == "UC_SEARCH_CHANNEL_01", "Channel ID mismatch");
        Assert(c.Title == "Official Search Channel", "Channel Title mismatch");

        // 4. Проверка ContinuationToken
        Assert(response.ContinuationToken == "SEARCH_CONTINUATION_TOKEN_999",
            $"ContinuationToken mismatch: '{response.ContinuationToken}'");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"[SearchResponseAssertionFailed] {message}");
    }
}