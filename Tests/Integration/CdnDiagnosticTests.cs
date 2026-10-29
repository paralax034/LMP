using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using LMP.Core.Audio.Http;
using LMP.Core.Youtube.Videos;
using LMP.Tests.Framework;
using LMP.Tests.Unit;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Tests.Integration;

/// <summary>
/// Диагностические сетевые зонды доступности CDN YouTube, DNS/DoH резолвинга и сигнатур ТСПУ/DPI.
/// Все методы принадлежат категории <see cref="TestCategory.Diagnostic"/>,
/// что исключает ложные сбои при автоматическом тестировании ПО при проблемах у провайдера.
/// </summary>
public static class CdnDiagnosticTests
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

    [TestMethod(TestCategory.Diagnostic, "DNS & Google DoH Resolution Probe",
        Group = TestGroups.Network, Order = 1, RequiresNetwork = true, TimeoutSeconds = 30)]
    public static async Task TestDnsResolutionAsync(CancellationToken ct = default)
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
        var dohResult = await DohResolver.ResolveAsync("www.youtube.com").ConfigureAwait(false);
        if (dohResult is { Length: > 0 })
            Log.Info($"  {"DoH (Google HTTPS)",-35} -> {dohResult[0]} (Bypass verified)");

        if (youtubeBlocked)
            TestAssert.Skip("Provider DNS blocks youtube.com directly. Configure DNS-over-HTTPS in LMP settings.");
        else
            TestAssert.True(resolved > 0, "No domains resolved via system DNS");
    }

    [TestMethod(TestCategory.Diagnostic, "Alternative InnerTube API Endpoint (googleapis)",
        Group = TestGroups.Network, Order = 2, RequiresNetwork = true, TimeoutSeconds = 30)]
    public static async Task TestAlternativeApiEndpointAsync(CancellationToken ct = default)
    {
        const string host = "youtubei.googleapis.com";
        const string path = "/youtubei/v1/player?prettyPrint=false";

        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            UseProxy = false
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };

        var json = """
        {
            "context": {
                "client": { "clientName": "ANDROID", "clientVersion": "19.09.37", "hl": "en", "gl": "US" }
            },
            "videoId": "dQw4w9WgXcQ"
        }
        """;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}{path}");
        request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "com.google.android.youtube/19.09.37 (Linux; U; Android 12)");

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            int code = (int)response.StatusCode;
            TestAssert.True(code is >= 200 and < 500, $"googleapis endpoint returned unexpected error: {code}");
            Log.Info($"  ✓ {host} reachable: HTTP {code}");
        }
        catch (Exception ex)
        {
            TestAssert.Skip($"googleapis endpoint unreachable: {ex.Message}");
        }
    }

    [TestMethod(TestCategory.Diagnostic, "Known CDN Endpoints Raw TCP Probe",
        Group = TestGroups.Network, Order = 3, RequiresNetwork = true, TimeoutSeconds = 60)]
    public static async Task TestTcpConnectivityAsync(IServiceProvider _, CancellationToken ct = default)
    {
        int reachable = 0;

        for (int i = 0; i < KnownCdnEndpoints.Length; i++)
        {
            var (host, ip) = KnownCdnEndpoints[i];
            var sw = Stopwatch.StartNew();

            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(ip), 443), linkedCts.Token).ConfigureAwait(false);
                sw.Stop();
                reachable++;
                Log.Info($"  ✓ TCP OK   {ip,-16} {sw.ElapsedMilliseconds}ms ({host[..Math.Min(host.Length, 32)]})");
            }
            catch
            {
                sw.Stop();
                Log.Warn($"  ✗ TIMEOUT  {ip,-16} ({sw.ElapsedMilliseconds}ms)");
            }
        }

        if (reachable == 0)
            TestAssert.Skip("All known Google CDN IPs unreachable via TCP — network or firewall blocking active");
    }

    [TestMethod(TestCategory.Diagnostic, "DPI Path Discrimination (204 vs Media)",
        Group = TestGroups.Network, Order = 4, RequiresNetwork = true, TimeoutSeconds = 60)]
    public static async Task TestPathDiscriminationAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var youtube = services.GetRequiredService<Lazy<YoutubeProvider>>().Value.GetClient();
        var videoId = TestConfig.Get().Pipeline.DebugVideoId;

        var manifest = await youtube.Videos.Streams.GetManifestAsync(VideoId.Parse(videoId), ct).ConfigureAwait(false);
        var audioStream = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();
        var host = MediaPathProbe.ExtractHost(audioStream.Url);

        Log.Info($"  CDN host: {host}");

        await RunPathTestAsync($"https://{host}/generate_204", "generate_204", audioStream.Url, null, ct).ConfigureAwait(false);
        await RunPathTestAsync(audioStream.Url, "videoplayback bytes=0-1023", audioStream.Url, (0, 1023), ct).ConfigureAwait(false);
    }

    [TestMethod(TestCategory.Diagnostic, "Known CDN Subnets /generate_204 Probe",
        Group = TestGroups.Network, Order = 5, RequiresNetwork = true, TimeoutSeconds = 60)]
    public static async Task TestZapretCoverageAsync(IServiceProvider _, CancellationToken ct = default)
    {
        for (int i = 0; i < KnownCdnEndpoints.Length; i++)
        {
            var (host, ip) = KnownCdnEndpoints[i];
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
                Log.Info($"  {ip,-16} -> {(code is 200 or 204 ? "✓" : "⚠")} HTTP {code} ({host[..Math.Min(host.Length, 32)]})");
            }
            catch (Exception ex)
            {
                Log.Warn($"  {ip,-16} -> ✗ {ex.Message}");
            }
        }
    }

    [TestMethod(TestCategory.Diagnostic, "Full Live Chain (DNS -> TLS -> 204 -> Media)",
        Group = TestGroups.Network, Order = 6, RequiresNetwork = true, TimeoutSeconds = 90)]
    public static async Task TestLiveFullChainAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var youtube = services.GetRequiredService<Lazy<YoutubeProvider>>().Value.GetClient();
        var videoId = TestConfig.Get().Pipeline.DebugVideoId;

        var manifest = await youtube.Videos.Streams.GetManifestAsync(VideoId.Parse(videoId), ct).ConfigureAwait(false);
        var stream = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();
        var host = MediaPathProbe.ExtractHost(stream.Url);

        var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
        TestAssert.True(addresses.Length > 0, $"DNS resolution failed for {host}");

        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = SharedHttpClient.ConnectWithKeepAliveAsync,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };

        using var req204 = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/generate_204");
        SharedHttpClient.ApplyUserAgentFromUrl(req204, stream.Url);
        using var resp204 = await client.SendAsync(req204, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        using var reqMedia = new HttpRequestMessage(HttpMethod.Get, stream.Url);
        reqMedia.Headers.Range = new RangeHeaderValue(0, 1023);
        SharedHttpClient.ApplyUserAgentFromUrl(reqMedia, stream.Url);
        using var respMedia = await client.SendAsync(reqMedia, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

        TestAssert.True(respMedia.IsSuccessStatusCode || respMedia.StatusCode == HttpStatusCode.PartialContent,
            $"Media request failed: HTTP {(int)respMedia.StatusCode}");

        Log.Info($"  ✓ Full CDN chain verified for {host}: HTTP {(int)respMedia.StatusCode}");
    }

    [TestMethod(TestCategory.Diagnostic, "Multi-Manifest CDN Discovery & Probe",
        Group = TestGroups.Network, Order = 7, RequiresNetwork = true, TimeoutSeconds = 120)]
    public static async Task TestMultiManifestMediaProbeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var youtube = services.GetRequiredService<Lazy<YoutubeProvider>>().Value.GetClient();
        var videoIds = TestConfig.Get().Pipeline.TestVideoIds;
        var hostToUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < videoIds.Length; i++)
        {
            try
            {
                var manifest = await youtube.Videos.Streams.GetManifestAsync(VideoId.Parse(videoIds[i]), ct).ConfigureAwait(false);
                foreach (var stream in manifest.GetAudioOnlyStreams())
                {
                    var host = MediaPathProbe.ExtractHost(stream.Url);
                    hostToUrl.TryAdd(host, stream.Url);
                }
            }
            catch { }
        }

        if (hostToUrl.Count == 0)
            TestAssert.Skip("Failed to resolve any CDN manifests for multi-video probe");

        int available = 0;
        foreach (var (host, url) in hostToUrl)
        {
            var result = await MediaPathProbe.ProbeHostAsync(url, timeoutMs: 5000).ConfigureAwait(false);
            if (result.MediaOk) available++;
            Log.Info($"  {host,-36} | 204:{(result.HealthCheckOk ? "✓" : "✗")} | Media:{(result.MediaOk ? "✓" : "✗")} | {result.MediaMs}ms");
        }

        Log.Info($"[Diagnostic] Multi-manifest probe: {available}/{hostToUrl.Count} hosts available for playback");
    }

    [TestMethod(TestCategory.Diagnostic, "Probe Recently Cached Session Streams",
        Group = TestGroups.Network, Order = 8, RequiresNetwork = true, TimeoutSeconds = 30)]
    public static async Task TestActiveStreamProbeAsync()
    {
        var trackIds = SessionCacheStore.GetAllCachedTrackIds();
        if (trackIds.Count == 0)
        {
            TestAssert.Skip("No cached stream manifests in session cache. Play audio first.");
        }

        int available = 0;
        int probed = 0;

        foreach (var trackId in trackIds.Take(5))
        {
            var manifest = SessionCacheStore.GetManifest(trackId);
            if (manifest?.Variants is not { Count: > 0 }) continue;

            var url = manifest.Variants[0].Url;
            if (string.IsNullOrWhiteSpace(url)) continue;

            probed++;
            var result = await MediaPathProbe.ProbeHostAsync(url, timeoutMs: 4000).ConfigureAwait(false);
            if (result.MediaOk) available++;
            Log.Info($"  Track {trackId,-12} | Media:{(result.MediaOk ? "✓" : "✗")} | {result.MediaMs}ms");
        }

        TestAssert.True(probed > 0, "No valid URLs found in cached manifests");
        Log.Info($"[Diagnostic] Active stream probe: {available}/{probed} streams reachable");
    }

    [TestMethod(TestCategory.Diagnostic, "Network Transition Baseline & Diff",
        Group = TestGroups.Network, Order = 9, RequiresNetwork = true, TimeoutSeconds = 90)]
    public static async Task TestNetworkTransitionAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var youtube = services.GetRequiredService<Lazy<YoutubeProvider>>().Value.GetClient();
        var videoId = TestConfig.Get().Pipeline.DebugVideoId;

        var manifest = await youtube.Videos.Streams.GetManifestAsync(VideoId.Parse(videoId), ct).ConfigureAwait(false);
        var currentSnapshot = new List<CdnHostSnapshot>();

        foreach (var stream in manifest.GetAudioOnlyStreams())
        {
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

    [TestMethod(TestCategory.Diagnostic, "Zapret Lists & Config Environment Audit",
        Group = TestGroups.Network, Order = 98, RequiresNetwork = false, TimeoutSeconds = 10)]
    public static Task TestZapretConfigAuditAsync(IServiceProvider _)
    {
        var envPath = Environment.GetEnvironmentVariable("LMP_ZAPRET_PATH");
        var zapretBase = !string.IsNullOrWhiteSpace(envPath)
            ? envPath
            : @"C:\Users\paralax\Desktop\zapret-windows\lists";

        if (!Directory.Exists(zapretBase))
        {
            TestAssert.Skip($"Zapret lists directory not found: {zapretBase}. Set LMP_ZAPRET_PATH if installed.");
        }

        var ipsetFile = Path.Combine(zapretBase, "ipset-all.txt");
        if (File.Exists(ipsetFile))
        {
            var text = File.ReadAllText(ipsetFile);
            if (!text.Contains("74.125.0.0/16", StringComparison.Ordinal))
            {
                Log.Warn($"[Diagnostic] Google subnet 74.125.0.0/16 not explicitly found in {ipsetFile}");
            }
            else
            {
                Log.Info($"  ✓ Zapret ipset verified: {ipsetFile}");
            }
        }

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Diagnostic, "Direct Probe to Target Problematic CDN Node",
        Group = TestGroups.Network, Order = 99, RequiresNetwork = true, TimeoutSeconds = 30)]
    public static async Task TestProblemIpDirectAsync(IServiceProvider _, CancellationToken ct = default)
    {
        var targetHost = KnownCdnEndpoints[0].Host;
        await RunPathTestAsync($"https://{targetHost}/generate_204", "Direct 204 Ping", $"https://{targetHost}/", null, ct).ConfigureAwait(false);
    }

    private static async Task RunPathTestAsync(
        string url, string label, string referenceUrl, (long Start, long End)? range, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = SharedHttpClient.ConnectWithKeepAliveAsync,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (range.HasValue)
            request.Headers.Range = new RangeHeaderValue(range.Value.Start, range.Value.End);

        SharedHttpClient.ApplyUserAgentFromUrl(request, referenceUrl);

        try
        {
            var sw = Stopwatch.StartNew();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            sw.Stop();
            int code = (int)response.StatusCode;
            Log.Info($"  {(code is 200 or 204 or 206 ? "✓" : "⚠")} HTTP {code} in {sw.ElapsedMilliseconds}ms — {label}");
        }
        catch (Exception ex)
        {
            Log.Warn($"  ✗ FAIL ({label}): {ex.Message}");
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