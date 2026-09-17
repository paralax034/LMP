using System.Text;
using LMP.Core.Youtube.Bridge;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Модульные тесты для проверки разбора структуры плеера <see cref="PlayerResponse"/>.
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
            "bitrate": 1500000,
            "width": 1280,
            "height": 720,
            "fps": 30
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

    /// <summary>
    /// Проверяет корректность разбора метаданных плеера, прямых и зашифрованных аудио/видео стримов.
    /// </summary>
    [TestMethod(TestCategory.Unit, "Player: Stream Parse Integrity", Group = TestGroups.Pipeline, Order = 6)]
    public static async Task TestPlayerResponseIntegrityAsync()
    {
        var bytes = Encoding.UTF8.GetBytes(SamplePlayerJson);
        using var stream = new MemoryStream(bytes);

        var response = await PlayerResponse.ParseAsync(stream).ConfigureAwait(false);

        Assert(response != null, "PlayerResponse is null");
        Assert(response!.IsPlayable, "Should be playable");
        Assert(response.Title == "Player Test Track", "Title mismatch");
        Assert(response.Author == "Player Artist", "Author mismatch");
        Assert(response.ChannelId == "UC_PLAYER_CHANNEL", "ChannelId mismatch");
        Assert(response.Duration == TimeSpan.FromSeconds(245), "Duration mismatch");
        Assert(response.ViewCount == 1234567, "ViewCount mismatch");
        Assert(Math.Abs(response.PerceptualLoudnessDb - (-9.54f)) < 0.01f, "Loudness mismatch");

        // Проверка стримов
        Assert(response.Streams.Count == 3, $"Expected 3 streams, got {response.Streams.Count}");

        // Прямой аудио-стрим itag 251 (Opus)
        var s251 = response.Streams.FirstOrDefault(s => s.Itag == 251);
        Assert(s251 != null, "Stream 251 not found");
        Assert(s251!.AudioCodec == "opus", $"Expected opus codec, got '{s251.AudioCodec}'");
        Assert(s251.Container == "webm", $"Expected webm container, got '{s251.Container}'");
        Assert(s251.Url == "https://googlevideo.test/videoplayback?itag=251", "Stream 251 URL mismatch");
        Assert(s251.Signature == null, "Stream 251 should not have cipher");

        // Зашифрованный аудио-стрим itag 140 (M4A cipher)
        var s140 = response.Streams.FirstOrDefault(s => s.Itag == 140);
        Assert(s140 != null, "Stream 140 not found");
        Assert(s140!.AudioCodec == "mp4a.40.2", "Stream 140 codec mismatch");
        Assert(s140.Signature == "SIG_TEST_ABC==", $"Signature mismatch: '{s140.Signature}'");
        Assert(s140.SignatureParameter == "sig", $"SignatureParameter mismatch: '{s140.SignatureParameter}'");
        Assert(s140.Url == "https://googlevideo.test/videoplayback?itag=140", "Decoded Cipher URL mismatch");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"[PlayerResponseAssertionFailed] {message}");
    }
}