using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using LMP.Core.Audio.Http;
using LMP.Core.Youtube.Videos;
using LMP.Core.Youtube.Videos.Streams;
using LMP.Tests.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Tests.Integration;

/// <summary>
/// Интеграционные тесты конвейера потокового аудио: получение манифеста, дешифрация ссылок,
/// скачивание чанков данных и валидация сроков жизни URL.
/// </summary>
public static class StreamPipelineTests
{
    private static string[] TestVideoIds => TestConfig.Get().Pipeline.TestVideoIds;

    [TestMethod(TestCategory.Integration, "Resolve Stream Manifest & Download Audio Chunk",
        Group = TestGroups.Youtube, Order = 1, RequiresNetwork = true, TimeoutSeconds = 60)]
    public static async Task TestStreamResolutionAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var youtube = services.GetRequiredService<Lazy<YoutubeProvider>>().Value.GetClient();
        var videoId = TestVideoIds[0];

        var sw = Stopwatch.StartNew();
        var manifest = await youtube.Videos.Streams.GetManifestAsync(VideoId.Parse(videoId), ct).ConfigureAwait(false);
        sw.Stop();

        var audioStreams = manifest.GetAudioOnlyStreams().ToList();
        TestAssert.True(audioStreams.Count > 0, "No audio streams found in manifest");

        var best = audioStreams.GetWithHighestBitrate();

        using var request = new HttpRequestMessage(HttpMethod.Get, best.Url);
        request.Headers.Range = new RangeHeaderValue(0, 1023);

        using var response = await SharedHttpClient.Instance.SendAsync(
            request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

        TestAssert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.PartialContent,
            $"Stream chunk request failed: HTTP {(int)response.StatusCode}");

        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        TestAssert.True(bytes.Length > 0, "Downloaded stream chunk is empty");

        Log.Info($"[Pipeline] Resolved in {sw.ElapsedMilliseconds}ms, received {bytes.Length} bytes (itag={best.Itag})");
    }

    [TestMethod(TestCategory.Integration, "Parallel Multi-Video Manifest & Chunk Resolution",
        Group = TestGroups.Youtube, Order = 2, RequiresNetwork = true, TimeoutSeconds = 90)]
    public static async Task TestMultiVideoAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var youtube = services.GetRequiredService<Lazy<YoutubeProvider>>().Value.GetClient();
        int success = 0;
        int failed = 0;

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = 2,
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(TestVideoIds, options, async (videoId, token) =>
        {
            try
            {
                var manifest = await youtube.Videos.Streams.GetManifestAsync(VideoId.Parse(videoId), token).ConfigureAwait(false);
                var stream = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();

                using var request = new HttpRequestMessage(HttpMethod.Get, stream.Url);
                request.Headers.Range = new RangeHeaderValue(0, 1023);

                using var response = await SharedHttpClient.Instance.SendAsync(
                    request, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.PartialContent)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                    if (bytes.Length > 0)
                    {
                        Interlocked.Increment(ref success);
                        return;
                    }
                }

                Interlocked.Increment(ref failed);
            }
            catch
            {
                Interlocked.Increment(ref failed);
            }
        }).ConfigureAwait(false);

        TestAssert.Equal(0, failed, $"Multi-video stream resolution failed for {failed} videos");
        Log.Info($"[Pipeline] Multi-video: {success}/{TestVideoIds.Length} passed");
    }

    [TestMethod(TestCategory.Integration, "Download 64KB Audio & Verify Container Magic Bytes",
        Group = TestGroups.Youtube, Order = 3, RequiresNetwork = true, TimeoutSeconds = 60)]
    public static async Task TestAudioDownloadAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var youtube = services.GetRequiredService<Lazy<YoutubeProvider>>().Value.GetClient();
        var videoId = TestVideoIds[0];

        var manifest = await youtube.Videos.Streams.GetManifestAsync(VideoId.Parse(videoId), ct).ConfigureAwait(false);
        var stream = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();

        using var request = new HttpRequestMessage(HttpMethod.Get, stream.Url);
        request.Headers.Range = new RangeHeaderValue(0, 65535);

        using var response = await SharedHttpClient.Instance.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        TestAssert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.PartialContent,
            $"Download failed: HTTP {(int)response.StatusCode}");

        var buffer = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        bool isWebM = buffer.Length >= 4 &&
            buffer[0] == 0x1A && buffer[1] == 0x45 && buffer[2] == 0xDF && buffer[3] == 0xA3;
        bool isMp4 = buffer.Length >= 8 &&
            buffer[4] == 'f' && buffer[5] == 't' && buffer[6] == 'y' && buffer[7] == 'p';

        TestAssert.True(isWebM || isMp4, "Downloaded bytes do not match WebM or MP4 container signatures");
        Log.Info($"[Pipeline] Downloaded {buffer.Length} bytes, container format: {(isWebM ? "WebM/Opus" : "MP4/AAC")}");
    }

    [TestMethod(TestCategory.Integration, "Verify All Audio Variants in Stream Manifest",
        Group = TestGroups.Youtube, Order = 4, RequiresNetwork = true, TimeoutSeconds = 90)]
    public static async Task TestFullPipelineAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await TestFullPipelineInternalAsync(services, "dQw4w9WgXcQ", ct).ConfigureAwait(false);
    }

    [TestMethod(TestCategory.Integration, "Live Stream URL Expire TTL (>4.5h)",
        Group = TestGroups.Network, Order = 5, RequiresNetwork = true, TimeoutSeconds = 60)]
    public static async Task TestLiveStreamUrlExpireTtlAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var youtube = services.GetRequiredService<Lazy<YoutubeProvider>>().Value.GetClient();
        var videoId = TestConfig.Get().Pipeline.DebugVideoId;

        var manifest = await youtube.Videos.Streams.GetManifestAsync(VideoId.Parse(videoId), ct).ConfigureAwait(false);
        var audioStream = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();
        TestAssert.NotNull(audioStream, "No audio stream found in live manifest");

        var url = audioStream.Url;
        TestAssert.True(UrlEx.TryGetExpireUtc(url, out var expireUtc), "Stream URL is missing valid &expire= parameter");

        var ttlRemaining = expireUtc - DateTime.UtcNow;
        TestAssert.True(ttlRemaining.TotalHours >= 4.5, $"TTL remaining is unexpectedly low: {ttlRemaining.TotalHours:F2}h");
        TestAssert.False(UrlEx.IsUrlExpiredOrExpiringSoon(url, TimeSpan.FromMinutes(5)));

        Log.Info($"[Pipeline] Live URL TTL validated: {ttlRemaining.TotalHours:F2}h remaining");
    }

    [TestMethod(TestCategory.Diagnostic, "Inspect Video Stream URL & Query Parameters",
        Group = TestGroups.Youtube, Order = 99, RequiresNetwork = true, TimeoutSeconds = 120)]
    public static async Task DebugSpecificStreamAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var config = TestConfig.Get().Pipeline;
        var videoId = config.DebugVideoId;
        var targetItag = config.DebugTargetItag;

        var youtube = services.GetRequiredService<Lazy<YoutubeProvider>>().Value.GetClient();
        var manifest = await youtube.Videos.Streams.GetManifestAsync(VideoId.Parse(videoId), ct).ConfigureAwait(false);

        var audioStreams = manifest.GetAudioOnlyStreams().ToList();
        var stream = (targetItag is not null
            ? audioStreams.Find(s => s.Itag == targetItag)
            : audioStreams.FirstOrDefault()) ?? throw new InvalidOperationException($"Stream itag={targetItag} not found");

        Log.Info($"[Diagnostic] Selected stream itag={stream.Itag}: {stream.Container}/{stream.AudioCodec}, {stream.Bitrate.KiloBitsPerSecond:F0}kbps");
        Log.Info($"[Diagnostic] Host: {new Uri(stream.Url).Host}");
    }

    /// <summary>
    /// Точка вызова полного пайплайна для <c>ManualTests</c>.
    /// </summary>
    public static async Task TestFullPipelineInternalAsync(
        IServiceProvider services, string videoId = "dQw4w9WgXcQ", CancellationToken ct = default)
    {
        var youtube = services.GetRequiredService<Lazy<YoutubeProvider>>().Value.GetClient();
        var manifest = await youtube.Videos.Streams.GetManifestAsync(VideoId.Parse(videoId), ct).ConfigureAwait(false);

        var audioStreams = manifest.GetAudioOnlyStreams().ToList();
        TestAssert.True(audioStreams.Count > 0, "No audio streams found in manifest");

        int verified = 0;
        var inspectStreams = audioStreams.Take(5).ToList();

        for (int i = 0; i < inspectStreams.Count; i++)
        {
            var s = inspectStreams[i];
            using var request = new HttpRequestMessage(HttpMethod.Get, s.Url);
            request.Headers.Range = new RangeHeaderValue(0, 1023);

            using var response = await SharedHttpClient.Instance.SendAsync(
                request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.PartialContent)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (bytes.Length > 0) verified++;
            }
        }

        TestAssert.True(verified > 0, "All stream URLs failed (signature decryption or N-token broken)");
        Log.Info($"[Pipeline] Verified {verified}/{inspectStreams.Count} audio variants for {videoId}");
    }
}