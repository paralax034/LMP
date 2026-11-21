using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using LMP.Core.Audio.Http;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Videos;
using LMP.Tests.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Tests.Integration;

/// <summary>
/// Integration tests for the streaming audio pipeline: manifest retrieval, link decryption,
/// URL TTL validation, and container chunk download verification.
/// </summary>
public static class StreamPipelineTests
{
    private static string[] TestVideoIds => TestConfig.Get().Pipeline.TestVideoIds;

    [TestMethod(TestCategory.Integration, "Resolve Manifest, Verify TTL (>4.5h) & 64KB Container Magic Bytes",
        Group = TestGroups.Youtube, Order = 1, RequiresNetwork = true, TimeoutSeconds = 60)]
    public static async Task TestStreamResolutionTtlAndContainerAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var youtube = services.GetRequiredService<Lazy<YoutubeProvider>>().Value.GetClient();
        var auth = services.GetRequiredService<CookieAuthService>();
        var videoId = TestConfig.Get().Pipeline.DebugVideoId;

        var sw = Stopwatch.StartNew();
        Core.Youtube.Videos.Streams.StreamManifest manifest;
        try
        {
            manifest = await youtube.Videos.Streams.GetManifestAsync(VideoId.Parse(videoId), ct).ConfigureAwait(false);
        }
        catch (LoginRequiredException) when (!auth.IsAuthenticated && !string.Equals(videoId, "dQw4w9WgXcQ", StringComparison.Ordinal))
        {
            Log.Warn($"[Pipeline] DebugVideoId '{videoId}' requires auth (AgeRestricted); falling back to public 'dQw4w9WgXcQ'.");
            videoId = "dQw4w9WgXcQ";
            manifest = await youtube.Videos.Streams.GetManifestAsync(VideoId.Parse(videoId), ct).ConfigureAwait(false);
        }
        sw.Stop();

        var audioStreams = manifest.GetAudioOnlyStreams();
        TestAssert.True(audioStreams.Count > 0, "No audio streams found in manifest");

        var best = audioStreams.GetWithHighestBitrate();
        TestAssert.True(UrlEx.TryGetExpireUtc(best.Url, out var expireUtc), "Stream URL is missing valid &expire= parameter");

        var ttlRemaining = expireUtc - DateTime.UtcNow;
        TestAssert.True(ttlRemaining.TotalHours >= 4.5, $"TTL remaining is unexpectedly low: {ttlRemaining.TotalHours:F2}h");
        TestAssert.False(UrlEx.IsUrlExpiredOrExpiringSoon(best.Url, TimeSpan.FromMinutes(5)));

        using var request = new HttpRequestMessage(HttpMethod.Get, best.Url);
        request.Headers.Range = new RangeHeaderValue(0, 65535);
        SharedHttpClient.ApplyUserAgentFromUrl(request, best.Url);

        using var response = await SharedHttpClient.Instance.SendAsync(
            request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

        TestAssert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.PartialContent,
            $"Stream chunk download failed: HTTP {(int)response.StatusCode}");

        var buffer = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        TestAssert.True(buffer.Length > 0, "Downloaded stream chunk is empty");

        bool isWebM = buffer.Length >= 4 &&
            buffer[0] == 0x1A && buffer[1] == 0x45 && buffer[2] == 0xDF && buffer[3] == 0xA3;
        bool isMp4 = buffer.Length >= 8 &&
            buffer[4] == 'f' && buffer[5] == 't' && buffer[6] == 'y' && buffer[7] == 'p';

        TestAssert.True(isWebM || isMp4, "Downloaded bytes do not match WebM or MP4 container signatures");
        Log.Info(
            $"[Pipeline] Resolved {videoId} in {sw.ElapsedMilliseconds}ms, itag={best.Itag}, " +
            $"TTL={ttlRemaining.TotalHours:F2}h, {buffer.Length}B ({(isWebM ? "WebM/Opus" : "MP4/AAC")})");
    }

    [TestMethod(TestCategory.Integration, "Parallel Multi-Video Manifest & Chunk Resolution",
        Group = TestGroups.Youtube, Order = 2, RequiresNetwork = true, TimeoutSeconds = 90)]
    public static async Task TestMultiVideoAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var youtube = services.GetRequiredService<Lazy<YoutubeProvider>>().Value.GetClient();
        var auth = services.GetRequiredService<CookieAuthService>();
        int success = 0;
        int skippedAuth = 0;
        int failed = 0;
        var failureDetails = new System.Collections.Concurrent.ConcurrentQueue<string>();

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
                var audioStreams = manifest.GetAudioOnlyStreams();
                if (audioStreams.Count == 0)
                {
                    Interlocked.Increment(ref failed);
                    string detail = $"{videoId}: empty audio stream list";
                    failureDetails.Enqueue(detail);
                    Log.Warn($"[Pipeline] Multi-video failed — {detail}");
                    return;
                }

                var stream = audioStreams.GetWithHighestBitrate();

                using var request = new HttpRequestMessage(HttpMethod.Get, stream.Url);
                request.Headers.Range = new RangeHeaderValue(0, 1023);
                SharedHttpClient.ApplyUserAgentFromUrl(request, stream.Url);

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

                    string emptyDetail = $"{videoId} (itag={stream.Itag}): empty body";
                    failureDetails.Enqueue(emptyDetail);
                    Log.Warn($"[Pipeline] Multi-video failed — {emptyDetail}");
                }
                else
                {
                    string httpDetail = $"{videoId} (itag={stream.Itag}): HTTP {(int)response.StatusCode}";
                    failureDetails.Enqueue(httpDetail);
                    Log.Warn($"[Pipeline] Multi-video failed — {httpDetail}");
                }

                Interlocked.Increment(ref failed);
            }
            catch (LoginRequiredException) when (!auth.IsAuthenticated)
            {
                Interlocked.Increment(ref skippedAuth);
                Log.Warn($"[Pipeline] Skipping '{videoId}' in unauthenticated multi-video run (LoginRequired/AgeRestricted).");
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                Interlocked.Increment(ref failed);
                string exDetail = $"{videoId}: {ex.GetType().Name} ({ex.Message})";
                failureDetails.Enqueue(exDetail);
                Log.Warn($"[Pipeline] Multi-video failed — {exDetail}");
            }
        }).ConfigureAwait(false);

        TestAssert.Equal(
            0,
            failed,
            $"Multi-video stream resolution failed for {failed} video(s): {string.Join("; ", failureDetails)}");

        if (success == 0 && skippedAuth > 0)
            TestAssert.Skip($"All {skippedAuth} configured TestVideoIds require authentication");

        Log.Info($"[Pipeline] Multi-video: {success}/{TestVideoIds.Length} passed (skippedAuth={skippedAuth})");
    }

    [TestMethod(TestCategory.Diagnostic, "Inspect Video Stream URL & Query Parameters",
        Group = TestGroups.Youtube, Order = 99, RequiresNetwork = true, TimeoutSeconds = 120)]
    public static async Task DebugSpecificStreamAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var config = TestConfig.Get().Pipeline;
        var videoId = config.DebugVideoId;
        var targetItag = config.DebugTargetItag;

        var youtube = services.GetRequiredService<Lazy<YoutubeProvider>>().Value.GetClient();
        var auth = services.GetRequiredService<CookieAuthService>();

        Core.Youtube.Videos.Streams.StreamManifest manifest;
        try
        {
            manifest = await youtube.Videos.Streams.GetManifestAsync(VideoId.Parse(videoId), ct).ConfigureAwait(false);
        }
        catch (LoginRequiredException) when (!auth.IsAuthenticated && !string.Equals(videoId, "dQw4w9WgXcQ", StringComparison.Ordinal))
        {
            Log.Warn($"[Diagnostic] DebugVideoId '{videoId}' requires auth; falling back to public 'dQw4w9WgXcQ'.");
            videoId = "dQw4w9WgXcQ";
            manifest = await youtube.Videos.Streams.GetManifestAsync(VideoId.Parse(videoId), ct).ConfigureAwait(false);
        }

        var audioStreams = manifest.GetAudioOnlyStreams().ToList();
        var stream = (targetItag is not null
            ? audioStreams.Find(s => s.Itag == targetItag)
            : audioStreams.FirstOrDefault()) ?? throw new InvalidOperationException($"Stream itag={targetItag} not found");

        Log.Info($"[Diagnostic] Selected stream itag={stream.Itag}: {stream.Container}/{stream.AudioCodec}, {stream.Bitrate.KiloBitsPerSecond:F0}kbps");
        Log.Info($"[Diagnostic] Host: {new Uri(stream.Url).Host}");
    }
}