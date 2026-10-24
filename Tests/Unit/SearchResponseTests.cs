using System.Text;
using LMP.Core.Youtube.Bridge;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Модульные тесты парсинга поисковой выдачи YouTube SearchResponse.
/// Проверяют корректность извлечения треков, плейлистов, каналов и маркеров пагинации.
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

  [TestMethod(TestCategory.Unit, "SearchResponse Mixed Items Parsing", Group = TestGroups.Youtube, Order = 1)]
  public static async Task TestSearchResponseIntegrityAsync()
  {
    var bytes = Encoding.UTF8.GetBytes(SampleSearchJson);
    using var stream = new MemoryStream(bytes);

    var response = await SearchResponse.ParseAsync(stream).ConfigureAwait(false);

    TestAssert.NotNull(response);

    // Видео
    TestAssert.Equal(1, response.Videos.Count);
    TestAssert.Equal("vid_search_01", response.Videos[0].Id);
    TestAssert.Equal("Search Video Title", response.Videos[0].Title);
    TestAssert.Equal(TimeSpan.FromSeconds(260), response.Videos[0].Duration);

    // Плейлисты
    TestAssert.Equal(1, response.Playlists.Count);
    TestAssert.Equal("PL_SEARCH_PLAYLIST_01", response.Playlists[0].Id);

    // Каналы
    TestAssert.Equal(1, response.Channels.Count);
    TestAssert.Equal("UC_SEARCH_CHANNEL_01", response.Channels[0].Id);

    // Токен продолжения
    TestAssert.Equal("SEARCH_CONTINUATION_TOKEN_999", response.ContinuationToken);
  }
}