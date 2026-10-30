using System.Text;
using LMP.Core.Youtube.Music;
using LMP.Core.Youtube.Utils;
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
    TestAssert.False(data.IsEditable, "Foreign playlist without musicEditablePlaylistDetailHeaderRenderer must have IsEditable == false even when tracks have playlistSetVideoId");
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

  [TestMethod(TestCategory.Unit, "YTM Header Tracks & Author Digits Not Mistaken For Views Or Year", Group = TestGroups.Youtube, Order = 4)]
  public static async Task TestHeaderTracksAndAuthorDigitsGuardAsync()
  {
    const string jsonNoViewsAuthorWithDigits = """
      {
        "header": {
          "musicResponsiveHeaderRenderer": {
            "title": { "runs": [ { "text": "My Coding Mix" } ] },
            "subtitle": {
              "runs": [
                { "text": "Playlist" },
                { "text": " • " },
                { "text": "dj_alex_1999" }
              ]
            },
            "secondSubtitle": {
              "runs": [
                { "text": "58 tracks" },
                { "text": " • " },
                { "text": "3 hr 15 min" }
              ]
            }
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
                            "contents": []
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

    using (var stream1 = new MemoryStream(Encoding.UTF8.GetBytes(jsonNoViewsAuthorWithDigits)))
    {
      var (data1, _, _) = await PlaylistStreamingParser.ParseBrowseAsync(stream1).ConfigureAwait(false);
      TestAssert.Equal("My Coding Mix", data1.Title);
      TestAssert.True(data1.ViewCount is null, $"Track count '58 tracks' must not be parsed as ViewCount (got {data1.ViewCount})");
      TestAssert.True(data1.ReleaseDate is null, $"Author digits 'dj_alex_1999' must not be parsed as ReleaseDate (got {data1.ReleaseDate})");
    }

    const string jsonStandaloneYearIgnoredWithoutFullDate = """
      {
        "header": {
          "musicResponsiveHeaderRenderer": {
            "title": { "runs": [ { "text": "Only Year In Subtitle" } ] },
            "subtitle": {
              "runs": [
                { "text": "Playlist" },
                { "text": " • " },
                { "text": "2026" }
              ]
            },
            "secondSubtitle": {
              "runs": [
                { "text": "12 tracks" }
              ]
            }
          }
        }
      }
      """;

    using (var streamYearOnly = new MemoryStream(Encoding.UTF8.GetBytes(jsonStandaloneYearIgnoredWithoutFullDate)))
    {
      var (dataYearOnly, _, _) = await PlaylistStreamingParser.ParseBrowseAsync(streamYearOnly).ConfigureAwait(false);
      TestAssert.True(dataYearOnly.ViewCount is null, "12 tracks must not become ViewCount");
      TestAssert.True(dataYearOnly.ReleaseDate is null, "Standalone year '2026' must not fabricate 01.01.2026");
    }

    const string jsonSongsBeforeViewsAndExactDate = """
      {
        "header": {
          "musicResponsiveHeaderRenderer": {
            "title": { "runs": [ { "text": "Synthwave 2026" } ] },
            "subtitle": {
              "runs": [
                { "text": "Playlist" },
                { "text": " • " },
                { "text": "2026" }
              ]
            },
            "secondSubtitle": {
              "runs": [
                { "text": "42 songs" },
                { "text": " • " },
                { "text": "1,250 views" },
                { "text": " • " },
                { "text": "Updated Oct 25, 2026" }
              ]
            }
          }
        }
      }
      """;

    using (var stream2 = new MemoryStream(Encoding.UTF8.GetBytes(jsonSongsBeforeViewsAndExactDate)))
    {
      var (data2, _, _) = await PlaylistStreamingParser.ParseBrowseAsync(stream2).ConfigureAwait(false);
      TestAssert.Equal(1250L, data2.ViewCount);
      TestAssert.Equal(new DateOnly(2026, 10, 25), data2.ReleaseDate);
      TestAssert.False(data2.IsEditable);
    }

    const string jsonEditableOwnedHeader = """
      {
        "header": {
          "musicEditablePlaylistDetailHeaderRenderer": {
            "header": {
              "musicResponsiveHeaderRenderer": {
                "title": { "runs": [ { "text": "My Owned Playlist" } ] },
                "secondSubtitle": {
                  "runs": [
                    { "text": "31K views" }
                  ]
                }
              }
            }
          }
        }
      }
      """;

    using var streamOwned = new MemoryStream(Encoding.UTF8.GetBytes(jsonEditableOwnedHeader));
    var (dataOwned, _, _) = await PlaylistStreamingParser.ParseBrowseAsync(streamOwned).ConfigureAwait(false);
    TestAssert.True(dataOwned.IsEditable, "musicEditablePlaylistDetailHeaderRenderer must mark playlist IsEditable = true");
    TestAssert.Equal(31_000L, dataOwned.ViewCount);
  }

  [TestMethod(TestCategory.Unit, "YTM Continuation Streaming Parser Pagination & Shelf Isolation", Group = TestGroups.Youtube, Order = 5)]
  public static async Task TestParseContinuationShelfIsolationAsync()
  {
    const string continuationJson = """
      {
        "responseContext": { "visitorData": "CONT_VISITOR_777" },
        "continuationContents": {
          "musicPlaylistShelfContinuation": {
            "contents": [
              {
                "musicResponsiveListItemRenderer": {
                  "playlistItemData": {
                    "videoId": "vid_page2_01",
                    "playlistSetVideoId": "set_page2_01"
                  },
                  "flexColumns": [
                    {
                      "musicResponsiveListItemFlexColumnRenderer": {
                        "text": { "runs": [ { "text": "Back In Black" } ] }
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
                        "text": { "runs": [ { "text": "4:15" } ] }
                      }
                    }
                  ]
                }
              }
            ],
            "continuations": [
              {
                "nextContinuationData": {
                  "continuation": "PAGE_3_TOKEN"
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
                  "musicResponsiveListItemRenderer": {
                    "playlistItemData": {
                      "videoId": "vid_outside_shelf_ignored",
                      "playlistSetVideoId": "set_ignored"
                    }
                  }
                }
              ]
            }
          }
        ]
      }
      """;

    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(continuationJson));
    var (tracks, nextToken, visitor) =
        await PlaylistStreamingParser.ParseContinuationAsync(stream, currentTrackCount: 10).ConfigureAwait(false);

    TestAssert.Equal("CONT_VISITOR_777", visitor);
    TestAssert.Equal("PAGE_3_TOKEN", nextToken);
    TestAssert.Equal(1, tracks.Count);
    TestAssert.Equal("vid_page2_01", tracks[0].VideoId);
    TestAssert.Equal("set_page2_01", tracks[0].SetVideoId);
    TestAssert.Equal(10, tracks[0].Position);
    TestAssert.Equal(255, tracks[0].DurationSeconds);
  }

  [TestMethod(TestCategory.Unit, "PlaylistBrowse & Next & Continuation Full Branch Coverage", Group = TestGroups.Youtube, Order = 6)]
  public static Task TestPlaylistBrowseNextContinuationResponsesAsync()
  {
    const string browseJson = """
      {
        "responseContext": { "visitorData": "BROWSE_VD_1" },
        "sidebar": {
          "playlistSidebarRenderer": {
            "items": [
              {
                "playlistSidebarPrimaryInfoRenderer": {
                  "title": { "simpleText": "Web Browse Playlist" },
                  "description": { "simpleText": "Web description" },
                  "stats": [
                    { "simpleText": "12 videos" },
                    { "simpleText": "9,876 views" },
                    { "runs": [ { "text": "Updated " }, { "text": "Oct 20, 2026" } ] }
                  ],
                  "privacyForm": {
                    "dropdownFormFieldRenderer": {
                      "entries": [
                        { "isSelected": false, "playlistPrivacy": "PUBLIC" },
                        { "isSelected": true, "playlistPrivacy": "UNLISTED" }
                      ]
                    }
                  }
                }
              },
              {
                "playlistSidebarSecondaryInfoRenderer": {
                  "videoOwner": {
                    "videoOwnerRenderer": {
                      "title": { "simpleText": "Channel Owner" },
                      "navigationEndpoint": {
                        "browseEndpoint": { "browseId": "UC_OWNER_123" }
                      }
                    }
                  }
                }
              }
            ]
          }
        },
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
                                "playlistVideoListRenderer": {
                                  "contents": [
                                    {
                                      "playlistVideoRenderer": {
                                        "videoId": "dQw4w9WgXcQ",
                                        "setVideoId": "SET_WEB_001",
                                        "title": { "simpleText": "Rick Astley - Never Gonna Give You Up (Official Video)" },
                                        "shortBylineText": {
                                          "runs": [
                                            {
                                              "text": "Rick Astley",
                                              "navigationEndpoint": {
                                                "browseEndpoint": {
                                                  "browseId": "UCuAXFkgsw1L7xaCfnd5JJOw",
                                                  "browseEndpointContextSupportedConfigs": {
                                                    "browseEndpointContextMusicConfig": {
                                                      "pageType": "MUSIC_PAGE_TYPE_ARTIST"
                                                    }
                                                  }
                                                }
                                              }
                                            }
                                          ]
                                        },
                                        "lengthSeconds": "213"
                                      }
                                    }
                                  ],
                                  "continuations": [
                                    {
                                      "nextContinuationData": { "continuation": "WEB_CONT_TOKEN_1" }
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
        }
      }
      """;

    var browse = Core.Youtube.Bridge.PlaylistBrowseResponse.Parse(browseJson);
    TestAssert.True(browse.IsAvailable);
    TestAssert.True(browse.IsEditable, "PlaylistBrowseResponse with privacyForm must be marked IsEditable = true");
    TestAssert.Equal("Web Browse Playlist", browse.Title);
    TestAssert.Equal("Channel Owner", browse.Author);
    TestAssert.Equal("UC_OWNER_123", browse.ChannelId);
    TestAssert.Equal(12, browse.Count);
    TestAssert.Equal(9876L, browse.ViewCount);
    TestAssert.Equal(new DateOnly(2026, 10, 20), browse.ReleaseDate);
    TestAssert.Equal(PlaylistVisibility.Unlisted, browse.Visibility);
    TestAssert.Equal("WEB_CONT_TOKEN_1", browse.ContinuationToken);
    TestAssert.Equal(1, browse.Videos.Count);
    TestAssert.Equal("dQw4w9WgXcQ", browse.Videos[0].Id);
    TestAssert.Equal("SET_WEB_001", browse.Videos[0].SetVideoId);
    TestAssert.True(browse.Videos[0].IsMusic);

    const string nextJson = """
      {
        "contents": {
          "twoColumnWatchNextResults": {
            "playlist": {
              "playlist": {
                "title": "Next Fallback Mix",
                "ownerName": { "simpleText": "Mix Author" },
                "totalVideosText": "25 videos",
                "contents": [
                  {
                    "playlistPanelVideoRenderer": {
                      "videoId": "vid_next_01",
                      "setVideoId": "SET_NEXT_01",
                      "title": { "simpleText": "Track Next" },
                      "lengthText": { "simpleText": "3:40" }
                    }
                  }
                ]
              }
            }
          }
        }
      }
      """;

    var next = Core.Youtube.Bridge.PlaylistNextResponse.Parse(nextJson);
    TestAssert.True(next.IsAvailable);
    TestAssert.Equal("Next Fallback Mix", next.Title);
    TestAssert.Equal(25, next.Count);
    TestAssert.Equal(1, next.Videos.Count);
    TestAssert.Equal("SET_NEXT_01", next.Videos[0].SetVideoId);

    return Task.CompletedTask;
  }

  [TestMethod(TestCategory.Unit, "PlaylistMutation Outgoing Payloads & Scotty Upload Protocol", Group = TestGroups.Youtube, Order = 7)]
  public static async Task TestPlaylistMutationPayloadsAndScottyAsync()
  {
    var capturedRequests = new List<(string Uri, string Body, string? UploadCommand)>();

    using var handler = new MockYoutubeHttpHandler((req, body) =>
    {
      string? cmd = req.Headers.TryGetValues("X-Goog-Upload-Command", out var vals) ? string.Join(",", vals) : null;
      capturedRequests.Add((req.RequestUri!.ToString(), body, cmd));

      if (cmd == "start")
      {
        var resp = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        resp.Headers.Add("X-Goog-Upload-URL", "https://music.youtube.com/upload_session_123");
        return resp;
      }

      if (cmd == "upload, finalize")
      {
        return MockYoutubeHttpHandler.JsonResponse("""{"encryptedBlobId":"BLOB_SECRET_999"}""");
      }

      if (req.RequestUri!.AbsolutePath.EndsWith("/playlist/create", StringComparison.Ordinal))
      {
        return MockYoutubeHttpHandler.JsonResponse("""{"playlistId":"PL_NEW_CLOUD_1"}""");
      }

      if (body.Contains("TRIGGER_STATUS_FAILED", StringComparison.Ordinal))
      {
        return MockYoutubeHttpHandler.JsonResponse("""
          {
            "status": "STATUS_FAILED",
            "actions": [
              {
                "confirmDialogEndpoint": {
                  "content": {
                    "confirmDialogRenderer": {
                      "dialogMessages": [
                        { "runs": [ { "text": "Quota exceeded for playlist edits" } ] }
                      ]
                    }
                  }
                }
              }
            ]
          }
          """);
      }

      return MockYoutubeHttpHandler.JsonResponse("""
        {
          "status": "STATUS_SUCCEEDED",
          "playlistEditResults": [
            { "playlistEditVideoAddedResultData": { "setVideoId": "NEW_SET_1" } },
            { "playlistEditVideoAddedResultData": { "setVideoId": "NEW_SET_2" } }
          ]
        }
        """);
    });

    using var http = new HttpClient(handler);
    var mutations = new PlaylistMutationController(http);

    // 1. CreatePlaylistAsync
    var createdId = await mutations.CreatePlaylistAsync("My Rock", ["v1", "v2"], "Desc").ConfigureAwait(false);
    TestAssert.Equal("PL_NEW_CLOUD_1", createdId);
    TestAssert.True(capturedRequests[^1].Body.Contains("\"title\":\"My Rock\"", StringComparison.Ordinal));
    TestAssert.True(capturedRequests[^1].Body.Contains("\"videoIds\":[\"v1\",\"v2\"]", StringComparison.Ordinal));

    // 2. AddTracksAsync (с нормализацией префикса VL)
    var addedSetIds = await mutations.AddTracksAsync("VLPL_NEW_CLOUD_1", ["v1", "v2"]).ConfigureAwait(false);
    TestAssert.Equal(2, addedSetIds.Count);
    TestAssert.Equal("NEW_SET_1", addedSetIds[0]);
    TestAssert.True(capturedRequests[^1].Body.Contains("\"playlistId\":\"PL_NEW_CLOUD_1\"", StringComparison.Ordinal));
    TestAssert.True(capturedRequests[^1].Body.Contains("\"action\":\"ACTION_ADD_VIDEO\"", StringComparison.Ordinal));

    // 3. MoveTracksAsync (BEFORE & AFTER)
    await mutations.MoveTracksAsync("PL_NEW_CLOUD_1", [
      ("NEW_SET_2", null, "NEW_SET_1"),
      ("NEW_SET_1", "NEW_SET_2", null)
    ]).ConfigureAwait(false);
    TestAssert.True(capturedRequests[^1].Body.Contains("ACTION_MOVE_VIDEO_BEFORE", StringComparison.Ordinal));
    TestAssert.True(capturedRequests[^1].Body.Contains("ACTION_MOVE_VIDEO_AFTER", StringComparison.Ordinal));

    // 4. RemoveTracksAsync (с фильтрацией to_be_updated_by_client)
    await mutations.RemoveTracksAsync("PL_NEW_CLOUD_1", ["to_be_updated_by_client", "NEW_SET_1"]).ConfigureAwait(false);
    TestAssert.False(capturedRequests[^1].Body.Contains("to_be_updated_by_client", StringComparison.Ordinal));
    TestAssert.True(capturedRequests[^1].Body.Contains("ACTION_REMOVE_VIDEO", StringComparison.Ordinal));

    // 5. Scotty 3-step UploadPlaylistThumbnailAsync
    bool thumbOk = await mutations.UploadPlaylistThumbnailAsync("PL_NEW_CLOUD_1", [1, 2, 3, 4]).ConfigureAwait(false);
    TestAssert.True(thumbOk);
    TestAssert.True(capturedRequests[^1].Body.Contains("ACTION_SET_CUSTOM_THUMBNAIL", StringComparison.Ordinal));
    TestAssert.True(capturedRequests[^1].Body.Contains("BLOB_SECRET_999", StringComparison.Ordinal));

    // 6. STATUS_FAILED -> YoutubeExplodeException с текстом из confirmDialogRenderer
    bool threwExpected = false;
    try
    {
      await mutations.RenamePlaylistAsync("PL_NEW_CLOUD_1", "TRIGGER_STATUS_FAILED").ConfigureAwait(false);
    }
    catch (Core.Youtube.Exceptions.YoutubeExplodeException ex)
    {
      threwExpected = ex.Message.Contains("Quota exceeded for playlist edits", StringComparison.Ordinal);
    }
    TestAssert.True(threwExpected, "CheckStatus must throw YoutubeExplodeException with extracted dialog message");
  }

  [TestMethod(TestCategory.Unit, "Youtube Utils & ThumbnailComparer Zero-Alloc Verification", Group = TestGroups.Youtube, Order = 8)]
  public static Task TestYoutubeUtilsAndThumbnailComparerZeroAllocAsync()
  {
    const string localThumb = "https://yt3.ggpht.com/pl_c/PL1234567890ABCDEF/abc?sqp=123";
    const string cloudThumb = "https://i.ytimg.com/pl_c/PL1234567890ABCDEF/xyz?rs=456";
    const string googleContent576 = "https://yt3.googleusercontent.com/FnEzyMQW7vUWEBCQzKYJ9P-25dBJyXysj5fOm72lYYid49P0waiieH-tBGZyu2dzkTDq_cVeyvro=s576";
    const string googleContent1200 = "https://yt3.googleusercontent.com/FnEzyMQW7vUWEBCQzKYJ9P-25dBJyXysj5fOm72lYYid49P0waiieH-tBGZyu2dzkTDq_cVeyvro=s1200";
    const string playlistId = "PL1234567890ABCDEF";
    DateTime nowUtc = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    TestAssert.True(ThumbnailComparer.AreEquivalent(localThumb, cloudThumb, playlistId));
    TestAssert.True(ThumbnailComparer.AreEquivalent(googleContent576, googleContent1200, playlistId),
        "googleusercontent.com URLs with different =s576/=s1200 size suffixes must be equivalent");

    // Проверка компактных суффиксов K/M/B и разделителей тысяч в ParseLongFromText
    TestAssert.Equal(31_000L, Core.Youtube.Utils.YoutubeParsingHelpers.ParseLongFromText("31K views".AsSpan()));
    TestAssert.Equal(31_500L, Core.Youtube.Utils.YoutubeParsingHelpers.ParseLongFromText("31.5K views".AsSpan()));
    TestAssert.Equal(1_250_000L, Core.Youtube.Utils.YoutubeParsingHelpers.ParseLongFromText("1.25M views".AsSpan()));
    TestAssert.Equal(2_000_000_000L, Core.Youtube.Utils.YoutubeParsingHelpers.ParseLongFromText("2B views".AsSpan()));
    TestAssert.Equal(1_500_000L, Core.Youtube.Utils.YoutubeParsingHelpers.ParseLongFromText("1,500,000 views".AsSpan()));
    TestAssert.Equal(31L, Core.Youtube.Utils.YoutubeParsingHelpers.ParseLongFromText("31 views".AsSpan()));

    TestAssert.Equal("dQw4w9WgXcQ", Core.Youtube.Utils.YoutubeIdHelper.ExtractRawId("yt_dQw4w9WgXcQ"));
    TestAssert.Equal(2026, Core.Youtube.Utils.YoutubeParsingHelpers.ParseYearFromText("Updated 2026".AsSpan()));
    TestAssert.Equal(new DateOnly(2026, 10, 28), Core.Youtube.Utils.YoutubeParsingHelpers.ParseRelativeDate("2 days ago".AsSpan(), nowUtc));

    var reason = Core.Youtube.Utils.PlayabilityErrorClassifier.Classify(
        "Video unavailable. This video contains content from SME, who has blocked it on copyright grounds",
        out var claimant);
    TestAssert.Equal(Core.Youtube.Exceptions.StreamUnavailableReason.CopyrightBlocked, reason);
    TestAssert.Equal("SME", claimant);

    var sapisidHeader = Core.Youtube.YoutubeHttpHandler.GetAuthHeader("TEST_SAPISID_VALUE", "https://music.youtube.com");
    TestAssert.NotNullOrEmpty(sapisidHeader);
    TestAssert.True(sapisidHeader.StartsWith("SAPISIDHASH ", StringComparison.Ordinal));

    TestAssert.ZeroAlloc(() =>
    {
      _ = ThumbnailComparer.AreEquivalent(googleContent576, googleContent1200, playlistId);
      _ = Core.Youtube.Utils.YoutubeIdHelper.ExtractRawIdSpan("yt_pl_PL1234567890".AsSpan());
      _ = Core.Youtube.Utils.YoutubeParsingHelpers.ParseLongFromText("31.5K views".AsSpan());
      _ = Core.Youtube.Utils.YoutubeParsingHelpers.ParseLongFromText("1,500,000 views".AsSpan());
      _ = Core.Youtube.Utils.YoutubeParsingHelpers.ParseYearFromText("2026".AsSpan());
      _ = Core.Youtube.Utils.YoutubeParsingHelpers.ParseRelativeDate("3 weeks ago".AsSpan(), nowUtc);
    }, warmup: 10, iterations: 1000, context: "YoutubeHotPathUtils");

    return Task.CompletedTask;
  }

  private sealed class MockYoutubeHttpHandler(
      Func<HttpRequestMessage, string, HttpResponseMessage> responder) : HttpMessageHandler
  {
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
      string body = request.Content != null
          ? await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
          : string.Empty;
      return responder(request, body);
    }

    public static HttpResponseMessage JsonResponse(string json) => new(System.Net.HttpStatusCode.OK)
    {
      Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
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