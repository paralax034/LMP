using System.Text;
using LMP.Core.Youtube.Bridge;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Модульные тесты потокового парсинга структуры ответа плеера PlayerResponse.
/// Проверяют валидацию метаданных трека, распознавание кодеков и шифрованных стримов.
/// </summary>
public static class PlayerResponseTests
{
  private const string SamplePlayerJson = """
    {
      "playabilityStatus": {
        "status": "OK"
      },
      "videoDetails": {
        "videoId": "vid_player_01",
        "title": "Player Test Track",
        "lengthSeconds": "245",
        "channelId": "UC_PLAYER_CHANNEL",
        "author": "Player Artist",
        "viewCount": "1234567",
        "thumbnail": {
          "thumbnails": [
            { "url": "https://img.test/player_thumb.jpg", "width": 640, "height": 360 }
          ]
        }
      },
      "playerConfig": {
        "audioConfig": {
          "perceptualLoudnessDb": -9.54
        }
      },
      "streamingData": {
        "formats": [
          {
            "itag": 22,
            "url": "https://googlevideo.test/videoplayback?itag=22",
            "mimeType": "video/mp4; codecs=\"avc1.64001F, mp4a.40.2\"",
            "bitrate": 1500000
          }
        ],
        "adaptiveFormats": [
          {
            "itag": 251,
            "url": "https://googlevideo.test/videoplayback?itag=251",
            "mimeType": "audio/webm; codecs=\"opus\"",
            "bitrate": 160000
          },
          {
            "itag": 140,
            "signatureCipher": "s=SIG_TEST_ABC%3D%3D&sp=sig&url=https%3A%2F%2Fgooglevideo.test%2Fvideoplayback%3Fitag%3D140",
            "mimeType": "audio/mp4; codecs=\"mp4a.40.2\"",
            "bitrate": 128000
          }
        ]
      }
    }
    """;

  [TestMethod(TestCategory.Unit, "PlayerResponse JSON Stream Parsing", Group = TestGroups.Youtube, Order = 1)]
  public static async Task TestPlayerResponseIntegrityAsync()
  {
    var bytes = Encoding.UTF8.GetBytes(SamplePlayerJson);
    using var stream = new MemoryStream(bytes);

    var response = await PlayerResponse.ParseAsync(stream).ConfigureAwait(false);

    TestAssert.NotNull(response);
    TestAssert.True(response.IsPlayable);
    TestAssert.Equal("Player Test Track", response.Title);
    TestAssert.Equal("Player Artist", response.Author);
    TestAssert.Equal("UC_PLAYER_CHANNEL", response.ChannelId);
    TestAssert.Equal(TimeSpan.FromSeconds(245), response.Duration);
    TestAssert.Equal(1234567, response.ViewCount);
    TestAssert.True(Math.Abs(response.PerceptualLoudnessDb - (-9.54f)) < 0.01f);

    TestAssert.Equal(3, response.Streams.Count);

    // Прямой аудио-стрим Opus itag 251
    var s251 = response.Streams.FirstOrDefault(s => s.Itag == 251);
    TestAssert.NotNull(s251);
    TestAssert.Equal("opus", s251.AudioCodec);
    TestAssert.Equal("webm", s251.Container);
    TestAssert.True(string.IsNullOrEmpty(s251.Signature));

    // Зашифрованный аудио-стрим itag 140
    var s140 = response.Streams.FirstOrDefault(s => s.Itag == 140);
    TestAssert.NotNull(s140);
    TestAssert.Equal("SIG_TEST_ABC==", s140.Signature);
    TestAssert.Equal("sig", s140.SignatureParameter);
  }

  [TestMethod(TestCategory.Unit, "PlayabilityErrorClassifier & LOGIN_REQUIRED Edge Cases", Group = TestGroups.Youtube, Order = 2)]
  public static Task TestPlayabilityClassifierAndLoginReasonsAsync()
  {
    // 1. CopyrightBlocked + извлечение правообладателя из обоих стандартных шаблонов YouTube (hl=en)
    var reasonFrom = Core.Youtube.Utils.PlayabilityErrorClassifier.Classify(
        "This video contains content from WMG, who has blocked it on copyright grounds.",
        out var claimantFrom);
    TestAssert.Equal(Core.Youtube.Exceptions.StreamUnavailableReason.CopyrightBlocked, reasonFrom);
    TestAssert.Equal("WMG", claimantFrom);

    var reasonBy = Core.Youtube.Utils.PlayabilityErrorClassifier.Classify(
        "Video unavailable. This video includes claimed content by Sony Music Entertainment.",
        out var claimantBy);
    TestAssert.Equal(Core.Youtube.Exceptions.StreamUnavailableReason.CopyrightBlocked, reasonBy);
    TestAssert.Equal("Sony Music Entertainment", claimantBy);

    // 2. RegionBlocked, Private, Removed, AgeRestricted, PaymentRequired
    TestAssert.Equal(
        Core.Youtube.Exceptions.StreamUnavailableReason.RegionBlocked,
        Core.Youtube.Utils.PlayabilityErrorClassifier.Classify("The uploader has not made this video available in your country", out _));
    TestAssert.Equal(
        Core.Youtube.Exceptions.StreamUnavailableReason.Private,
        Core.Youtube.Utils.PlayabilityErrorClassifier.Classify("This video is private", out _));
    TestAssert.Equal(
        Core.Youtube.Exceptions.StreamUnavailableReason.Removed,
        Core.Youtube.Utils.PlayabilityErrorClassifier.Classify("This video has been removed by the uploader", out _));
    TestAssert.Equal(
        Core.Youtube.Exceptions.StreamUnavailableReason.AgeRestricted,
        Core.Youtube.Utils.PlayabilityErrorClassifier.Classify("Sign in to confirm your age. This video may be inappropriate", out _));
    TestAssert.Equal(
        Core.Youtube.Exceptions.StreamUnavailableReason.PaymentRequired,
        Core.Youtube.Utils.PlayabilityErrorClassifier.Classify("This video is available to this channel's members-only", out _));

    // 3. Разбор LOGIN_REQUIRED в PlayerResponse (BotDetection vs AgeRestricted)
    var botResp = PlayerResponse.Parse("""
    {
      "playabilityStatus": {
        "status": "LOGIN_REQUIRED",
        "reason": "Sign in to confirm you’re not a bot"
      }
    }
    """);
    TestAssert.True(botResp.IsLoginRequired);
    TestAssert.Equal(Core.Youtube.Exceptions.LoginRequiredReason.BotDetection, botResp.LoginRequiredReason);

    var ageResp = PlayerResponse.Parse("""
    {
      "playabilityStatus": {
        "status": "LOGIN_REQUIRED",
        "reason": "Sign in to confirm your age",
        "desktopLegacyAgeGateReason": 1
      }
    }
    """);
    TestAssert.True(ageResp.IsLoginRequired);
    TestAssert.Equal(Core.Youtube.Exceptions.LoginRequiredReason.AgeRestricted, ageResp.LoginRequiredReason);

    return Task.CompletedTask;
  }

  [TestMethod(TestCategory.Unit, "VideoController Fallback Matrix: BotDetection Cooldown, Muxed itag=18 & Fast-Fail", Group = TestGroups.Youtube, Order = 3)]
  public static async Task TestVideoControllerFallbackMatrixAsync()
  {
    Core.Youtube.Videos.VideoController.ResetBotDetectionState();
    try
    {
      var calledClients = new List<string>();
      int scenario = 0;

      using var handler = new MockPlayerRouteHandler((clientName, _) =>
      {
        calledClients.Add(clientName);

        return scenario switch
        {
          // Сценарий 0 (Кейс jNQXAC9IVRw из лога):
          // WEB_REMIX -> Video unavailable; ANDROID_VR -> BotDetection (уходит в карантин); WEB -> muxed itag=18 (успех!)
          0 => clientName switch
          {
            "WEB_REMIX" => CreateJsonResponse("""{"playabilityStatus":{"status":"ERROR","reason":"Video unavailable"}}"""),
            "ANDROID_VR" => CreateJsonResponse("""{"playabilityStatus":{"status":"LOGIN_REQUIRED","reason":"Sign in to confirm you’re not a bot"}}"""),
            "WEB" => CreateJsonResponse("""
            {
              "playabilityStatus": { "status": "OK" },
              "videoDetails": { "videoId": "jNQXAC9IVRw", "title": "Me at the zoo" },
              "streamingData": {
                "formats": [
                  {
                    "itag": 18,
                    "url": "https://rr5.googlevideo.test/videoplayback?itag=18",
                    "mimeType": "video/mp4; codecs=\"avc1.42001E, mp4a.40.2\"",
                    "audioChannels": 2,
                    "bitrate": 300000
                  }
                ]
              }
            }
            """),
            _ => CreateJsonResponse("""{"playabilityStatus":{"status":"ERROR","reason":"Unknown client"}}""")
          },

          // Сценарий 1: Повторный запрос после карантина ANDROID_VR + проверка forceMobilePriority
          1 => CreateJsonResponse("""
          {
            "playabilityStatus": { "status": "OK" },
            "streamingData": {
              "adaptiveFormats": [
                {
                  "itag": 251,
                  "url": "https://rr1.googlevideo.test/videoplayback?itag=251",
                  "mimeType": "audio/webm; codecs=\"opus\"",
                  "bitrate": 160000
                }
              ]
            }
          }
          """),

          // Сценарий 2: Hard Legal Restriction (CopyrightBlocked) на первом клиенте -> обязан сделать fast-fail без опроса остальных!
          2 => CreateJsonResponse("""
          {
            "playabilityStatus": {
              "status": "UNPLAYABLE",
              "reason": "This video contains content from UMG, who has blocked it on copyright grounds."
            }
          }
          """),

          // Сценарий 3: Все клиенты падают по сети -> обязан выбросить YoutubeNetworkException, а не VideoUnplayableException
          _ => throw new HttpRequestException(
              "Simulated network outage",
              new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.NetworkUnreachable))
        };
      });

      using var http = new HttpClient(handler);
      var playerManager = new Core.Youtube.Bridge.Common.PlayerContextManager(() => http);
      playerManager.SetCachedSignatureTimestamp("20753");
      var controller = new Core.Youtube.Videos.VideoController(http, playerManager);
      var videoId = Core.Youtube.Videos.VideoId.Parse("jNQXAC9IVRw");

      // --- Проверка Сценария 0 ---
      scenario = 0;
      calledClients.Clear();
      var (resp0, client0) = await controller.GetPlayerResponseWithFallbackAsync(
          videoId, CancellationToken.None, isAuthenticated: true, preferWebRemix: true).ConfigureAwait(false);

      TestAssert.Equal("WEB", client0);
      TestAssert.True(resp0.IsPlayable);
      TestAssert.Equal(3, calledClients.Count);
      TestAssert.Equal("WEB_REMIX", calledClients[0]);
      TestAssert.Equal("ANDROID_VR", calledClients[1]);
      TestAssert.Equal("WEB", calledClients[2]);

      // --- Проверка Сценария 1A: ANDROID_VR должен быть пропущен из-за карантина BotDetection ---
      scenario = 1;
      calledClients.Clear();
      var (_, client1A) = await controller.GetPlayerResponseWithFallbackAsync(
          videoId, CancellationToken.None, isAuthenticated: true, preferWebRemix: false, forceMobilePriority: true).ConfigureAwait(false);

      TestAssert.Equal("WEB_REMIX", client1A);
      TestAssert.False(calledClients.Contains("ANDROID_VR"), "ANDROID_VR must be skipped while in BotDetection cooldown");

      // --- Проверка Сценария 1B: После сброса карантина forceMobilePriority ставит ANDROID_VR первым ---
      Core.Youtube.Videos.VideoController.ResetBotDetectionState();
      calledClients.Clear();
      var (_, client1B) = await controller.GetPlayerResponseWithFallbackAsync(
          videoId, CancellationToken.None, isAuthenticated: true, preferWebRemix: true, forceMobilePriority: true).ConfigureAwait(false);

      TestAssert.Equal("ANDROID_VR", client1B);
      TestAssert.Equal(1, calledClients.Count);
      TestAssert.Equal("ANDROID_VR", calledClients[0]);

      // --- Проверка Сценария 2: Fast-Fail при CopyrightBlocked ---
      scenario = 2;
      calledClients.Clear();
      bool caughtCopyright = false;
      try
      {
        _ = await controller.GetPlayerResponseWithFallbackAsync(
            videoId, CancellationToken.None, isAuthenticated: true, preferWebRemix: true).ConfigureAwait(false);
      }
      catch (Core.Youtube.Exceptions.StreamUnavailableException ex)
          when (ex.Reason == Core.Youtube.Exceptions.StreamUnavailableReason.CopyrightBlocked)
      {
        caughtCopyright = true;
      }

      TestAssert.True(caughtCopyright, "CopyrightBlocked must throw StreamUnavailableException");
      TestAssert.Equal(1, calledClients.Count, "Hard legal restriction must fast-fail on the 1st client without querying fallbacks");

      // --- Проверка Сценария 3: Полный отвал сети на всех клиентах -> YoutubeNetworkException ---
      scenario = 3;
      calledClients.Clear();
      bool caughtNetEx = false;
      try
      {
        _ = await controller.GetPlayerResponseWithFallbackAsync(
            videoId, CancellationToken.None, isAuthenticated: true, preferWebRemix: true).ConfigureAwait(false);
      }
      catch (Core.Youtube.Exceptions.YoutubeNetworkException)
      {
        caughtNetEx = true;
      }

      TestAssert.True(caughtNetEx, "When all clients fail due to network, VideoController must throw YoutubeNetworkException");
    }
    finally
    {
      Core.Youtube.Videos.VideoController.ResetBotDetectionState();
    }
  }

  private static HttpResponseMessage CreateJsonResponse(string json) =>
      new(System.Net.HttpStatusCode.OK)
      {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
      };

  private sealed class MockPlayerRouteHandler(
      Func<string, HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
  {
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
      string body = request.Content != null
          ? await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
          : "";

      string clientName = "UNKNOWN";
      if (body.Contains("\"WEB_REMIX\"", StringComparison.Ordinal)) clientName = "WEB_REMIX";
      else if (body.Contains("\"ANDROID_VR\"", StringComparison.Ordinal)) clientName = "ANDROID_VR";
      else if (body.Contains("\"WEB\"", StringComparison.Ordinal)) clientName = "WEB";

      return responder(clientName, request);
    }
  }
}