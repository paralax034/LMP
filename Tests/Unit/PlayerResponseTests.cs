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
}