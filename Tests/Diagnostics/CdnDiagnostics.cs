using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using LMP.Core.Audio.Http;
using LMP.Core.Youtube.Videos;
using LMP.Tests.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Tests.Diagnostics;

/// <summary>
/// Diagnostic network probes for YouTube CDN availability, DNS/DoH resolution, and TSPU/DPI signatures.
/// All methods belong to <see cref="TestCategory.Diagnostic"/> so ISP/network anomalies never trigger false test failures.
/// </summary>
public static class CdnDiagnostics
{
    private readonly record struct CdnHostSnapshot(
        string Host,
        string Ip,
        bool HealthOk,
        int HealthMs,
        bool MediaOk,
        int MediaMs,
        string? MediaError);

    private static List<CdnHostSnapshot>? _networkBaseline;
    private static string? _baselineOutboundIp;

    private static readonly (string Host, string Ip)[] KnownCdnEndpoints =
    [
        ("rr3---sn-4g5lznsz.googlevideo.com", "173.194.19.40"),
        ("rr3---sn-4g5ednss.googlevideo.com", "173.194.188.104"),
        ("rr5---sn-4g5ednsy.googlevideo.com", "74.125.173.138"),
        ("rr3---sn-4g5edn6k.googlevideo.com", "74.125.111.136"),
        ("rr7---sn-gvnuxaxjvh-n8ml.googlevideo.com", "37.79.210.18"),
        ("rr6---sn-axq7sn76.googlevideo.com", "172.217.130.150"),
        ("rr3---sn-axq7sn7e.googlevideo.com", "74.125.163.21"),
        ("rr4---sn-4g5lznlz.googlevideo.com", "74.125.104.73")
    ];

    [TestMethod(TestCategory.Diagnostic, "DNS, Google DoH & InnerTube API Reachability",
        Group = TestGroups.Network, Order = 1, RequiresNetwork = true, TimeoutSeconds = 30)]
    public static async Task TestDnsAndApiReachabilityAsync(CancellationToken ct = default)
    {
        var domains = new[]
        {
            "www.youtube.com",
            "music.youtube.com",
            "jnn-pa.googleapis.com",
            "rr1---sn-4g5lznsz.googlevideo.com"
        };

        int resolved = 0;
        bool youtubeBlocked = false;

        for (int i = 0; i < domains.Length; i++)
        {
            var domain = domains[i];
            var sw = Stopwatch.StartNew();
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(domain, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
                sw.Stop();

                if (addresses.Length > 0)
                {
                    resolved++;
                    Log.Info($"  {domain,-35} -> {addresses[0]} ({sw.ElapsedMilliseconds}ms)");
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                Log.Warn($"  {domain,-35} -> FAIL: {ex.Message} ({sw.ElapsedMilliseconds}ms)");
                if (domain.Contains("youtube.com", StringComparison.Ordinal))
                    youtubeBlocked = true;
            }
        }

        DohResolver.InvalidateCache();
        var dohResult = await DohResolver.ResolveAsync("www.youtube.com", ct).ConfigureAwait(false);
        if (dohResult is { Length: > 0 })
            Log.Info($"  {"DoH (Google HTTPS)",-35} -> {dohResult[0]} (Bypass verified)");

        using var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5), UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        const string payload = """
        {
            "context": {
                "client": { "clientName": "ANDROID", "clientVersion": "19.09.37", "hl": "en", "gl": "US" }
            },
            "videoId": "dQw4w9WgXcQ"
        }
        """;

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://youtubei.googleapis.com/youtubei/v1/player?prettyPrint=false");
        request.Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "com.google.android.youtube/19.09.37 (Linux; U; Android 12)");

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            Log.Info($"  {"youtubei.googleapis.com",-35} -> HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            Log.Warn($"  {"youtubei.googleapis.com",-35} -> FAIL: {ex.Message}");
        }

        if (youtubeBlocked)
            TestAssert.Skip("Provider DNS blocks youtube.com directly. Configure DNS-over-HTTPS in LMP settings.");
        else
            TestAssert.True(resolved > 0, "No domains resolved via system DNS");
    }

    [TestMethod(TestCategory.Diagnostic, "Full Live Chain & DPI Discrimination (DNS -> 204 -> Media)",
        Group = TestGroups.Network, Order = 2, RequiresNetwork = true, TimeoutSeconds = 90)]
    public static async Task TestLiveFullChainAndDpiProbeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var youtube = services.GetRequiredService<Lazy<YoutubeProvider>>().Value.GetClient();
        var videoId = TestConfig.Get().Pipeline.DebugVideoId;

        var manifest = await youtube.Videos.Streams.GetManifestAsync(VideoId.Parse(videoId), ct).ConfigureAwait(false);
        var stream = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();
        var host = MediaPathProbe.ExtractHost(stream.Url);

        var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
        TestAssert.True(addresses.Length > 0, $"DNS resolution failed for {host}");
        Log.Info($"  CDN host: {host} ({addresses[0]})");

        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = SharedHttpClient.ConnectWithKeepAliveAsync,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };

        var sw = Stopwatch.StartNew();
        using var req204 = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/generate_204");
        SharedHttpClient.ApplyUserAgentFromUrl(req204, stream.Url);
        using var resp204 = await client.SendAsync(req204, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        Log.Info($"  generate_204               -> HTTP {(int)resp204.StatusCode} ({sw.ElapsedMilliseconds}ms)");

        sw.Restart();
        using var reqMedia = new HttpRequestMessage(HttpMethod.Get, stream.Url);
        reqMedia.Headers.Range = new RangeHeaderValue(0, 1023);
        SharedHttpClient.ApplyUserAgentFromUrl(reqMedia, stream.Url);
        using var respMedia = await client.SendAsync(reqMedia, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        Log.Info($"  videoplayback bytes=0-1023 -> HTTP {(int)respMedia.StatusCode} ({sw.ElapsedMilliseconds}ms)");

        TestAssert.True(respMedia.IsSuccessStatusCode || respMedia.StatusCode == HttpStatusCode.PartialContent,
            $"Media request failed (possible DPI path discrimination): HTTP {(int)respMedia.StatusCode}");
    }

    [TestMethod(TestCategory.Diagnostic, "Known CDN Subnets TCP & /generate_204 Probe",
        Group = TestGroups.Network, Order = 3, RequiresNetwork = true, TimeoutSeconds = 60)]
    public static async Task TestKnownCdnSubnetsProbeAsync(CancellationToken ct = default)
    {
        int tcpReachable = 0;

        for (int i = 0; i < KnownCdnEndpoints.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (host, ip) = KnownCdnEndpoints[i];
            var sw = Stopwatch.StartNew();
            bool tcpOk = false;

            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(ip), 443), linkedCts.Token).ConfigureAwait(false);
                tcpOk = true;
                tcpReachable++;
            }
            catch
            {
            }

            long tcpMs = sw.ElapsedMilliseconds;
            if (!tcpOk)
            {
                Log.Warn($"  ✗ TCP TIMEOUT {ip,-16} ({tcpMs}ms) ({host[..Math.Min(host.Length, 32)]})");
                continue;
            }

            using var handler = new SocketsHttpHandler
            {
                ConnectCallback = SharedHttpClient.ConnectWithKeepAliveAsync,
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(3)
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/generate_204");
                SharedHttpClient.ApplyUserAgentFromUrl(request, $"https://{host}/");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                int code = (int)response.StatusCode;
                Log.Info($"  ✓ TCP {tcpMs,3}ms | HTTP {code} | {ip,-16} ({host[..Math.Min(host.Length, 32)]})");
            }
            catch (Exception ex)
            {
                Log.Warn($"  ⚠ TCP {tcpMs,3}ms | TLS/HTTP FAIL ({ex.Message}) | {ip,-16}");
            }
        }

        if (tcpReachable == 0)
            TestAssert.Skip("All known Google CDN IPs unreachable via TCP — network or firewall blocking active");
    }

    [TestMethod(TestCategory.Diagnostic, "Probe Recently Cached Session Streams",
        Group = TestGroups.Network, Order = 4, RequiresNetwork = true, TimeoutSeconds = 30)]
    public static async Task TestActiveStreamProbeAsync(CancellationToken ct = default)
    {
        var trackIds = SessionCacheStore.GetAllCachedTrackIds();
        if (trackIds.Count == 0)
            TestAssert.Skip("No cached stream manifests in session cache. Play audio first.");

        int available = 0;
        int probed = 0;

        foreach (var trackId in trackIds.Take(5))
        {
            ct.ThrowIfCancellationRequested();
            var manifest = SessionCacheStore.GetManifest(trackId);
            if (manifest?.Variants is not { Count: > 0 }) continue;

            var url = manifest.Variants[0].Url;
            if (string.IsNullOrWhiteSpace(url)) continue;

            probed++;
            var result = await MediaPathProbe.ProbeHostAsync(url, timeoutMs: 4000).ConfigureAwait(false);
            if (result.MediaOk) available++;
            Log.Info($"  Track {trackId,-12} | 204:{(result.HealthCheckOk ? "✓" : "✗")} | Media:{(result.MediaOk ? "✓" : "✗")} | {result.MediaMs}ms");
        }

        TestAssert.True(probed > 0, "No valid URLs found in cached manifests");
        Log.Info($"[Diagnostic] Active stream probe: {available}/{probed} streams reachable");
    }

    [TestMethod(TestCategory.Diagnostic, "Network Transition Baseline & Diff",
        Group = TestGroups.Network, Order = 5, RequiresNetwork = true, TimeoutSeconds = 90)]
    public static async Task TestNetworkTransitionAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var youtube = services.GetRequiredService<Lazy<YoutubeProvider>>().Value.GetClient();
        var videoId = TestConfig.Get().Pipeline.DebugVideoId;

        var manifest = await youtube.Videos.Streams.GetManifestAsync(VideoId.Parse(videoId), ct).ConfigureAwait(false);
        var currentSnapshot = new List<CdnHostSnapshot>();

        foreach (var stream in manifest.GetAudioOnlyStreams())
        {
            ct.ThrowIfCancellationRequested();
            var host = MediaPathProbe.ExtractHost(stream.Url);
            var ip = MediaPathProbe.ResolveToIp(host);
            var result = await MediaPathProbe.ProbeHostAsync(stream.Url, timeoutMs: 4000).ConfigureAwait(false);

            currentSnapshot.Add(new CdnHostSnapshot(
                host, ip, result.HealthCheckOk, result.HealthCheckMs, result.MediaOk, result.MediaMs, result.MediaError));
        }

        if (_networkBaseline is null)
        {
            _networkBaseline = currentSnapshot;
            _baselineOutboundIp = GetCurrentOutboundIp();
            Log.Info("[Diagnostic] Network baseline captured. Switch VPN/Wi-Fi and re-run to see differential report.");
        }
        else
        {
            Log.Info($"[Diagnostic] Comparing with baseline (Current IP: {GetCurrentOutboundIp()}, Baseline IP: {_baselineOutboundIp}):");
            var baselineMap = _networkBaseline.ToDictionary(s => s.Host, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < currentSnapshot.Count; i++)
            {
                var cur = currentSnapshot[i];
                if (baselineMap.TryGetValue(cur.Host, out var baseSnap))
                {
                    string status = baseSnap.MediaOk == cur.MediaOk
                        ? (cur.MediaOk ? "STABLE_OK" : "STABLE_BLOCKED")
                        : (cur.MediaOk ? "RECOVERED" : "REGRESSED");
                    Log.Info($"  {cur.Host,-35} | {status} ({cur.MediaMs}ms)");
                }
            }

            _networkBaseline = currentSnapshot;
        }
    }

    private static string GetCurrentOutboundIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 53);
            return ((IPEndPoint)socket.LocalEndPoint!).Address.ToString();
        }
        catch
        {
            return "unknown";
        }
    }
}