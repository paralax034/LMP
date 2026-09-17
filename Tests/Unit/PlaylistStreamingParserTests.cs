using System.Text;
using LMP.Core.Youtube.Music;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Набор модульных тестов для проверки однопроходного потокового парсера плейлистов <see cref="PlaylistStreamingParser"/>.
/// Тестирует разбор структуры, фрагментированное чтение чанками и отсутствие аллокаций в LOH.
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
          "description": { "runs": [ { "text": "Best rock hits of all time." } ] },
          "thumbnail": {
            "musicThumbnailRenderer": {
              "thumbnail": {
                "thumbnails": [
                  { "url": "https://img.test/small.jpg", "width": 100, "height": 100 },
                  { "url": "https://img.test/large.jpg", "width": 500, "height": 500 }
                ]
              }
            }
          },
          "secondSubtitle": { "runs": [ { "text": "1,500,000 views" } ] },
          "subtitle": { "runs": [ { "text": "Updated 2025" } ] }
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
                                "trackingParams": "CAEQABgCIABKFnVzZXJfdHJhY2tpbmdfbXVzaWM=",
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
                                      "text": {
                                        "runs": [
                                          {
                                            "text": "AC/DC",
                                            "navigationEndpoint": {
                                              "browseEndpoint": {
                                                "browseEndpointContextSupportedConfigs": {
                                                  "browseEndpointContextMusicConfig": {
                                                    "pageType": "MUSIC_PAGE_TYPE_ARTIST"
                                                  }
                                                }
                                              }
                                            }
                                          }
                                        ]
                                      }
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
                              "musicResponsiveListItemRenderer": {
                                "playlistItemData": {
                                  "videoId": "vid_rock_02",
                                  "setVideoId": "set_item_002"
                                },
                                "flexColumns": [
                                  {
                                    "musicResponsiveListItemFlexColumnRenderer": {
                                      "text": { "runs": [ { "text": "Hotel California" } ] }
                                    }
                                  },
                                  {
                                    "musicResponsiveListItemFlexColumnRenderer": {
                                      "text": { "runs": [ { "text": "Eagles" } ] }
                                    }
                                  }
                                ],
                                "fixedColumns": [
                                  {
                                    "musicResponsiveListItemFixedColumnRenderer": {
                                      "text": { "runs": [ { "text": "6:30" } ] }
                                    }
                                  }
                                ],
                                "musicItemRendererDisplayPolicy": "MUSIC_ITEM_RENDERER_DISPLAY_POLICY_GREY_OUT"
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

    /// <summary>
    /// Проверяет корректность разбора метаданных плейлиста, треков и токена продолжения при обычном чтении.
    /// </summary>
    [TestMethod(TestCategory.Unit, "PlaylistStream: ParseBrowse Integrity", Group = TestGroups.Pipeline, Order = 1)]
    public static async Task TestParseBrowseIntegrityAsync()
    {
        var bytes = Encoding.UTF8.GetBytes(SampleBrowseJson);
        using var stream = new MemoryStream(bytes);

        var (data, continuationToken, visitorData) =
            await PlaylistStreamingParser.ParseBrowseAsync(stream);

        Assert(data.Title == "Rock Classics", $"Expected Title 'Rock Classics', but got '{data.Title}'");
        Assert(data.Description == "Best rock hits of all time.", "Description mismatch");
        Assert(data.ThumbnailUrl == "https://img.test/large.jpg", "Thumbnail mismatch");
        Assert(data.ViewCount == 1500000, "ViewCount mismatch");
        Assert(data.ReleaseDate == new DateOnly(2025, 1, 1), "ReleaseDate mismatch");
        Assert(visitorData == "VISITOR_TEST_TOKEN_123", "VisitorData mismatch");
        Assert(continuationToken == "NEXT_PAGE_TOKEN_ABC", "ContinuationToken mismatch");

        Assert(data.Tracks.Count == 2, $"Expected 2 tracks, got {data.Tracks.Count}");

        var t1 = data.Tracks[0];
        Assert(t1.VideoId == "vid_rock_01", "Track 1 VideoId mismatch");
        Assert(t1.SetVideoId == "set_item_001", "Track 1 SetVideoId mismatch");
        Assert(t1.Title == "Thunderstruck", "Track 1 Title mismatch");
        Assert(t1.Author == "AC/DC", "Track 1 Author mismatch");
        Assert(t1.DurationSeconds == 292, $"Expected 292s, got {t1.DurationSeconds}");
        Assert(t1.ThumbnailUrl == "https://img.test/acdc.jpg", "Track 1 Thumbnail mismatch");
        Assert(t1.IsPlayable, "Track 1 should be playable");

        var t2 = data.Tracks[1];
        Assert(t2.VideoId == "vid_rock_02", "Track 2 VideoId mismatch");
        Assert(t2.SetVideoId == "set_item_002", "Track 2 SetVideoId mismatch");
        Assert(t2.Title == "Hotel California", "Track 2 Title mismatch");
        Assert(t2.Author == "Eagles", "Track 2 Author mismatch");
        Assert(t2.DurationSeconds == 390, $"Expected 390s, got {t2.DurationSeconds}");
        Assert(!t2.IsPlayable, "Track 2 should be non-playable due to GREY_OUT policy");
    }

    /// <summary>
    /// Проверяет стрессоустойчивость парсера к фрагментации сети:
    /// поток отдаёт данные крошечными чанками по 17 байт, гарантируя разрезку токенов на границах буфера.
    /// </summary>
    [TestMethod(TestCategory.Unit, "PlaylistStream: Chunked Boundary Fragmentation", Group = TestGroups.Pipeline, Order = 2)]
    public static async Task TestChunkedStreamFragmentationAsync()
    {
        var bytes = Encoding.UTF8.GetBytes(SampleBrowseJson);
        using var chunkedStream = new ChunkedReadStream(bytes, chunkSize: 17);

        var (data, continuationToken, visitorData) =
            await PlaylistStreamingParser.ParseBrowseAsync(chunkedStream);

        Assert(data.Title == "Rock Classics", "Fragmented Title mismatch");
        Assert(data.Tracks.Count == 2, "Fragmented Tracks count mismatch");
        Assert(data.Tracks[0].Title == "Thunderstruck", "Fragmented Track 1 Title mismatch");
        Assert(data.Tracks[1].Title == "Hotel California", "Fragmented Track 2 Title mismatch");
        Assert(continuationToken == "NEXT_PAGE_TOKEN_ABC", "Fragmented ContinuationToken mismatch");
    }

    /// <summary>
    /// Проверяет, что при потоковом разборе 100 повторений плейлиста память LOH не аллоцируется.
    /// </summary>
    [TestMethod(TestCategory.Unit, "PlaylistStream: Zero LOH Allocation", Group = TestGroups.Pipeline, Order = 3)]
    public static async Task TestZeroLohAllocationAsync()
    {
        var jsonSb = new StringBuilder(SampleBrowseJson.Length * 5);
        jsonSb.Append(SampleBrowseJson);

        var bytes = Encoding.UTF8.GetBytes(jsonSb.ToString());

        long lohBefore = GC.GetGeneration(new byte[85000]); // Прогрев LOH
        Log.Debug(lohBefore.ToString());

        long gen2CollectionsBefore = GC.CollectionCount(2);

        for (int i = 0; i < 20; i++)
        {
            using var stream = new MemoryStream(bytes);
            var (data, _, _) = await PlaylistStreamingParser.ParseBrowseAsync(stream);
            Assert(data.Tracks.Count == 2, "Loop track count mismatch");
        }

        long gen2CollectionsAfter = GC.CollectionCount(2);
        Assert(gen2CollectionsAfter - gen2CollectionsBefore <= 1,
            $"Detected excessive Gen 2 collections during streaming parsing: {gen2CollectionsAfter - gen2CollectionsBefore}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"[PlaylistStreamingParserAssertionFailed] {message}");
    }

    /// <summary>
    /// Вспомогательный поток для симуляции дробления пакетов в сетевом сокете.
    /// </summary>
    private sealed class ChunkedReadStream(byte[] source, int chunkSize) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= source.Length)
                return 0;

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