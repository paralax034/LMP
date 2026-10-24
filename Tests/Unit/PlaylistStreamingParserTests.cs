using System.Text;
using LMP.Core.Youtube.Music;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Модульные тесты потокового парсера плейлистов PlaylistStreamingParser.
/// Проверяют разбор структуры, фрагментированное чтение чанками по 17 байт и отсутствие аллокаций LOH.
/// </summary>
public static class PlaylistStreamingParserTests
{
  private const string SampleBrowseJson = """
    {
      "responseContext": {
        "visitorData": "VISITOR_TEST_TOKEN_123"
      },
      "header": {
        "musicResponsiveHeaderRenderer": {
          "title": { "runs": [ { "text": "Rock Classics" } ] },
          "description": { "runs": [ { "text": "Best rock hits." } ] },
          "thumbnail": {
            "musicThumbnailRenderer": {
              "thumbnail": {
                "thumbnails": [ { "url": "https://img.test/large.jpg", "width": 500, "height": 500 } ]
              }
            }
          },
          "secondSubtitle": { "runs": [ { "text": "1,500,000 views" } ] }
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
                            {
                              "musicResponsiveListItemRenderer": {
                                "playlistItemData": {
                                  "videoId": "vid_rock_01",
                                  "playlistSetVideoId": "set_item_001"
                                },
                                "flexColumns": [
                                  {
                                    "musicResponsiveListItemFlexColumnRenderer": {
                                      "text": { "runs": [ { "text": "Thunderstruck" } ] }
                                    }
                                  },
                                  {
                                    "musicResponsiveListItemFlexColumnRenderer": {
                                      "text": { "runs": [ { "text": "AC/DC" } ] }
                                    }
                                  }
                                ],
                                "fixedColumns": [
                                  {
                                    "musicResponsiveListItemFixedColumnRenderer": {
                                      "text": { "runs": [ { "text": "4:52" } ] }
                                    }
                                  }
                                ],
                                "thumbnail": {
                                  "musicThumbnailRenderer": {
                                    "thumbnail": {
                                      "thumbnails": [ { "url": "https://img.test/acdc.jpg" } ]
                                    }
                                  }
                                }
                              }
                            },
                            {
                              "continuationItemRenderer": {
                                "continuationEndpoint": {
                                  "continuationCommand": {
                                    "token": "NEXT_PAGE_TOKEN_ABC"
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
          ]
        }
      }
    }
    """;

  [TestMethod(TestCategory.Unit, "YTM Browse Streaming Parser Integrity", Group = TestGroups.Youtube, Order = 1)]
  public static async Task TestParseBrowseIntegrityAsync()
  {
    var bytes = Encoding.UTF8.GetBytes(SampleBrowseJson);
    using var stream = new MemoryStream(bytes);

    var (data, continuationToken, visitorData) =
        await PlaylistStreamingParser.ParseBrowseAsync(stream).ConfigureAwait(false);

    TestAssert.Equal("Rock Classics", data.Title);
    TestAssert.Equal("VISITOR_TEST_TOKEN_123", visitorData);
    TestAssert.Equal("NEXT_PAGE_TOKEN_ABC", continuationToken);
    TestAssert.Equal(1, data.Tracks.Count);

    var t1 = data.Tracks[0];
    TestAssert.Equal("vid_rock_01", t1.VideoId);
    TestAssert.Equal("set_item_001", t1.SetVideoId);
    TestAssert.Equal("Thunderstruck", t1.Title);
    TestAssert.Equal("AC/DC", t1.Author);
    TestAssert.Equal(292, t1.DurationSeconds);
  }

  [TestMethod(TestCategory.Unit, "YTM Browse 17-Byte Chunked Fragmentation", Group = TestGroups.Youtube, Order = 2)]
  public static async Task TestChunkedStreamFragmentationAsync()
  {
    var bytes = Encoding.UTF8.GetBytes(SampleBrowseJson);
    using var chunkedStream = new ChunkedReadStream(bytes, chunkSize: 17);

    var (data, continuationToken, _) =
        await PlaylistStreamingParser.ParseBrowseAsync(chunkedStream).ConfigureAwait(false);

    TestAssert.Equal("Rock Classics", data.Title);
    TestAssert.Equal(1, data.Tracks.Count);
    TestAssert.Equal("Thunderstruck", data.Tracks[0].Title);
    TestAssert.Equal("NEXT_PAGE_TOKEN_ABC", continuationToken);
  }

  [TestMethod(TestCategory.Unit, "YTM Browse Zero LOH Allocation", Group = TestGroups.Youtube, Order = 3)]
  public static async Task TestZeroLohAllocationAsync()
  {
    var bytes = Encoding.UTF8.GetBytes(SampleBrowseJson);
    long gen2CollectionsBefore = GC.CollectionCount(2);

    for (int i = 0; i < 20; i++)
    {
      using var stream = new MemoryStream(bytes);
      var (data, _, _) = await PlaylistStreamingParser.ParseBrowseAsync(stream).ConfigureAwait(false);
      TestAssert.Equal(1, data.Tracks.Count);
    }

    long gen2CollectionsAfter = GC.CollectionCount(2);
    TestAssert.True(gen2CollectionsAfter - gen2CollectionsBefore <= 1, "Excessive Gen 2 collections detected");
  }

  private sealed class ChunkedReadStream(byte[] source, int chunkSize) : Stream
  {
    private int _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => source.Length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
      if (_position >= source.Length) return 0;
      int toRead = Math.Min(chunkSize, Math.Min(buffer.Length, source.Length - _position));
      source.AsSpan(_position, toRead).CopyTo(buffer);
      _position += toRead;
      return toRead;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}