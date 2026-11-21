using System.Text;
using LMP.Core.Youtube.Bridge;
using LMP.Core.Youtube.Channels;
using LMP.Core.Youtube.Playlists;
using LMP.Core.Youtube.Videos;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Unit tests for parsing YouTube SearchResponse search results.
/// Verifies correct extraction of tracks, playlists, channels, and pagination tokens.
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

    // Videos
    TestAssert.Equal(1, response.Videos.Count);
    TestAssert.Equal("vid_search_01", response.Videos[0].Id);
    TestAssert.Equal("Search Video Title", response.Videos[0].Title);
    TestAssert.Equal(TimeSpan.FromSeconds(260), response.Videos[0].Duration);

    // Playlists
    TestAssert.Equal(1, response.Playlists.Count);
    TestAssert.Equal("PL_SEARCH_PLAYLIST_01", response.Playlists[0].Id);

    // Channels
    TestAssert.Equal(1, response.Channels.Count);
    TestAssert.Equal("UC_SEARCH_CHANNEL_01", response.Channels[0].Id);

    // Continuation token
    TestAssert.Equal("SEARCH_CONTINUATION_TOKEN_999", response.ContinuationToken);
  }

  [TestMethod(TestCategory.Unit, "YouTube ID Parsing & DetectQueryType Contracts", Group = TestGroups.Youtube, Order = 2)]
  public static Task TestIdParsingAndDetectQueryTypeContractsAsync()
  {
    // Negative cases: bare search terms, spaces, Cyrillic, and 11-char words must NEVER return non-null structs
    TestAssert.True(PlaylistId.TryParse("linkin park") is null, "Spaces must not parse as PlaylistId");
    TestAssert.True(PlaylistId.TryParse("кино") is null, "Cyrillic must not parse as PlaylistId");
    TestAssert.True(PlaylistId.TryParseUrl("Bearwolf") is null, "Bare word must not parse as Playlist URL");
    TestAssert.True(PlaylistId.TryParseUrl("PL1234567890") is null, "Bare playlist ID must not parse as Playlist URL");
    TestAssert.True(PlaylistId.TryParseUrl("https://www.youtube.com/watch?v=dQw4w9WgXcQ&blacklist=true") is null, "Query param ending in list= must not parse as PlaylistId");

    TestAssert.True(VideoId.TryParse("invalid") is null, "Invalid string must not parse as VideoId");
    TestAssert.True(VideoId.TryParse("кириллица12") is null, "Cyrillic 11-char string must not parse as VideoId");
    TestAssert.True(VideoId.TryParseUrl("dQw4w9WgXcQ") is null, "Bare 11-char ID must not parse as Video URL");
    TestAssert.True(VideoId.TryParseUrl("https://www.youtube.com/watch?rv=12345678901") is null, "Query param ending in v= must not parse as VideoId");
    TestAssert.True(VideoId.TryParseUrl("https://www.youtube.com/watch?nav=12345678901") is null, "11-char query param ending in v= must not parse as VideoId");

    TestAssert.True(ChannelId.TryParse("Bearwolf") is null, "Invalid string must not parse as ChannelId");
    TestAssert.True(ChannelId.TryParse("UCкириллица1234567890123") is null, "Cyrillic must not parse as ChannelId");

    // DetectQueryType: normal search queries (single word, multi-word, 11-char word, Cyrillic, http/https words)
    TestAssert.Equal(QueryType.None, YoutubeProvider.DetectQueryType("   "));
    TestAssert.Equal(QueryType.Search, YoutubeProvider.DetectQueryType("Bearwolf"));
    TestAssert.Equal(QueryType.Search, YoutubeProvider.DetectQueryType("linkin park"));
    TestAssert.Equal(QueryType.Search, YoutubeProvider.DetectQueryType("Soundtracks"));
    TestAssert.Equal(QueryType.Search, YoutubeProvider.DetectQueryType("кино группа"));
    TestAssert.Equal(QueryType.Search, YoutubeProvider.DetectQueryType("crystal castles - char slowed"));
    TestAssert.Equal(QueryType.Search, YoutubeProvider.DetectQueryType("http"));
    TestAssert.Equal(QueryType.Search, YoutubeProvider.DetectQueryType("https"));
    TestAssert.Equal(QueryType.Search, YoutubeProvider.DetectQueryType("http 404 remix"));

    // DetectQueryType & ID extraction: video URLs (including fragments and preceding params ending with 'v')
    TestAssert.Equal(QueryType.DirectUrl, YoutubeProvider.DetectQueryType("https://www.youtube.com/watch?v=dQw4w9WgXcQ"));
    TestAssert.Equal(QueryType.DirectUrl, YoutubeProvider.DetectQueryType("https://www.youtube.com/watch?si=abc&v=dQw4w9WgXcQ"));
    TestAssert.Equal(QueryType.DirectUrl, YoutubeProvider.DetectQueryType("https://www.youtube.com/watch?rv=1&v=dQw4w9WgXcQ#t=30"));
    TestAssert.Equal(QueryType.DirectUrl, YoutubeProvider.DetectQueryType("https://youtu.be/dQw4w9WgXcQ"));
    TestAssert.Equal(QueryType.DirectUrl, YoutubeProvider.DetectQueryType("https://youtu.be/dQw4w9WgXcQ#t=30"));
    TestAssert.Equal(QueryType.DirectUrl, YoutubeProvider.DetectQueryType("https://www.youtube.com/live/dQw4w9WgXcQ"));
    TestAssert.Equal(QueryType.DirectUrl, YoutubeProvider.DetectQueryType("https://www.youtube.com/shorts/dQw4w9WgXcQ"));
    TestAssert.Equal(QueryType.DirectUrl, YoutubeProvider.DetectQueryType("https://www.youtube.com/v/dQw4w9WgXcQ"));
    TestAssert.Equal("dQw4w9WgXcQ", YoutubeProvider.ExtractVideoId("https://www.youtube.com/watch?rv=1&v=dQw4w9WgXcQ#t=30"));
    TestAssert.Equal("dQw4w9WgXcQ", YoutubeProvider.ExtractVideoId("https://youtu.be/dQw4w9WgXcQ#t=30"));

    // DetectQueryType & ID extraction: playlist URLs (including youtu.be, embed, shorts, live, fragments)
    TestAssert.Equal(QueryType.Playlist, YoutubeProvider.DetectQueryType("https://www.youtube.com/playlist?list=PL1234567890"));
    TestAssert.Equal(QueryType.Playlist, YoutubeProvider.DetectQueryType("https://music.youtube.com/watch?v=dQw4w9WgXcQ&list=RDAMVM123"));
    TestAssert.Equal(QueryType.Playlist, YoutubeProvider.DetectQueryType("https://youtu.be/dQw4w9WgXcQ?list=PL1234567890#t=10"));
    TestAssert.Equal(QueryType.Playlist, YoutubeProvider.DetectQueryType("https://www.youtube.com/embed/dQw4w9WgXcQ?list=PL1234567890"));
    TestAssert.Equal(QueryType.Playlist, YoutubeProvider.DetectQueryType("https://www.youtube.com/shorts/dQw4w9WgXcQ?list=PL1234567890"));
    TestAssert.Equal(QueryType.Playlist, YoutubeProvider.DetectQueryType("https://www.youtube.com/live/dQw4w9WgXcQ?list=PL1234567890"));
    TestAssert.Equal("PL1234567890", PlaylistId.TryParseUrl("https://youtu.be/dQw4w9WgXcQ?list=PL1234567890#t=10")?.Value);
    TestAssert.Equal("PL1234567890", PlaylistId.TryParseUrl("https://www.youtube.com/embed/dQw4w9WgXcQ?list=PL1234567890")?.Value);

    // Positive ChannelId check (including fragment)
    var parsedChannel = ChannelId.TryParse("https://www.youtube.com/channel/UC_x5XG1OV2P6uZZ5FSM9Ttw#about");
    TestAssert.True(parsedChannel is { Value: "UC_x5XG1OV2P6uZZ5FSM9Ttw" }, "Valid channel URL must parse");

    return Task.CompletedTask;
  }
}