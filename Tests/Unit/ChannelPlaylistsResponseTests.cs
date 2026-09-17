using System.Text;
using LMP.Core.Youtube.Bridge;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Модульные тесты для проверки потокового парсинга списков плейлистов канала <see cref="ChannelPlaylistsResponse"/>.
/// </summary>
public static class ChannelPlaylistsResponseTests
{
    private const string SampleChannelPlaylistsJson = """
    {
      "contents": {
        "twoColumnBrowseResultsRenderer": {
          "tabs": [
            {
              "tabRenderer": {
                "content": {
                  "sectionListRenderer": {
                    "contents": [
                      {
                        "itemSectionRenderer": {
                          "contents": [
                            {
                              "gridRenderer": {
                                "items": [
                                  {
                                    "gridPlaylistRenderer": {
                                      "playlistId": "PL_TEST_GRID_001",
                                      "title": {
                                        "runs": [ { "text": "Grid Playlist Alpha" } ]
                                      },
                                      "thumbnail": {
                                        "thumbnails": [
                                          { "url": "https://img.test/grid_thumb.jpg", "width": 400, "height": 400 }
                                        ]
                                      }
                                    }
                                  },
                                  {
                                    "lockupViewModel": {
                                      "contentId": "PL_TEST_LOCKUP_002",
                                      "metadata": {
                                        "lockupMetadataViewModel": {
                                          "title": { "content": "Lockup Playlist Beta" }
                                        }
                                      },
                                      "contentImage": {
                                        "collectionThumbnailViewModel": {
                                          "primaryThumbnail": {
                                            "thumbnailViewModel": {
                                              "image": {
                                                "sources": [
                                                  { "url": "https://img.test/lockup_thumb.jpg" }
                                                ]
                                              }
                                            }
                                          }
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
                    ]
                  }
                }
              }
            }
          ]
        }
      },
      "onResponseReceivedActions": [
        {
          "appendContinuationItemsAction": {
            "continuationItems": [
              {
                "continuationItemRenderer": {
                  "continuationEndpoint": {
                    "continuationCommand": {
                      "token": "CHANNEL_PAGE_TOKEN_XYZ"
                    }
                  }
                }
              }
            ]
          }
        }
      ]
    }
    """;

    /// <summary>
    /// Проверяет, что потоковый разбор ответа плейлистов канала корректно извлекает плейлисты обоих форматов и токен пагинации.
    /// </summary>
    [TestMethod(TestCategory.Unit, "Channel: Stream Parse Integrity", Group = TestGroups.Pipeline, Order = 4)]
    public static async Task TestChannelPlaylistsStreamParseIntegrityAsync()
    {
        var bytes = Encoding.UTF8.GetBytes(SampleChannelPlaylistsJson);
        using var stream = new MemoryStream(bytes);

        var response = await ChannelPlaylistsResponse.ParseAsync(stream).ConfigureAwait(false);

        Assert(response != null, "ChannelPlaylistsResponse is null");

        var playlists = response!.Playlists.ToList();
        Assert(playlists.Count == 2, $"Expected 2 playlists, got {playlists.Count}");

        // 1. Проверка Grid Playlist
        var p1 = playlists[0];
        Assert(p1.Id == "PL_TEST_GRID_001", $"Playlist 1 ID mismatch: '{p1.Id}'");
        Assert(p1.StoredName == "Grid Playlist Alpha", $"Playlist 1 Title mismatch: '{p1.StoredName}'");
        Assert(p1.ThumbnailUrl == "https://img.test/grid_thumb.jpg", "Playlist 1 Thumb mismatch");

        // 2. Проверка Lockup ViewModel Playlist
        var p2 = playlists[1];
        Assert(p2.Id == "PL_TEST_LOCKUP_002", $"Playlist 2 ID mismatch: '{p2.Id}'");
        Assert(p2.StoredName == "Lockup Playlist Beta", $"Playlist 2 Title mismatch: '{p2.StoredName}'");
        Assert(p2.ThumbnailUrl == "https://img.test/lockup_thumb.jpg", "Playlist 2 Thumb mismatch");

        // 3. Проверка токена пагинации
        Assert(response.ContinuationToken == "CHANNEL_PAGE_TOKEN_XYZ",
            $"ContinuationToken mismatch: '{response.ContinuationToken}'");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"[ChannelPlaylistsResponseAssertionFailed] {message}");
    }
}