using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Audio.Http;

/// <summary>
/// Global HTTP client for CDN audio requests.
/// Supports hot swapping on network interface or proxy settings change.
/// </summary>
public static class SharedHttpClient
{
    private static NetworkManager? _networkManager;
    private static Func<NetworkManager>? _lazyNetworkFactory;
    private static long _connectionSequence;

    /// <summary>
    /// Registers a deferred resolution factory for <see cref="NetworkManager"/> from the DI container.
    /// </summary>
    public static void RegisterLazyFactory(Func<NetworkManager> factory)
    {
        _lazyNetworkFactory = factory;
    }

    /// <summary>
    /// Initializes the static facade with a reference to the centralized NetworkManager.
    /// </summary>
    public static void Initialize(NetworkManager networkManager)
    {
        _networkManager = networkManager;
    }

    /// <summary>The currently active client instance (resolves <see cref="NetworkManager"/> lazily on first call).</summary>
    public static HttpClient Instance =>
        (_networkManager ??= _lazyNetworkFactory?.Invoke())?.AudioClient ?? FallbackClient;

    private static readonly HttpClient FallbackClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromSeconds(90),
        MaxConnectionsPerServer = 6
    });

    /// <summary>
    /// Recreates the HTTP client with a new connection pool.
    /// The old client is disposed after 30 seconds to avoid aborting active range requests.
    /// </summary>
    public static void Rebuild(ProxySettings? proxy = null)
    {
        if (_networkManager != null)
        {
            if (proxy != null)
                _networkManager.UpdateProxy(proxy);
            else
                _networkManager.RebuildAll("SharedHttpClient.Rebuild invocation", force: true);
        }
    }

    /// <summary>
    /// TCP connection establishment callback for <see cref="SocketsHttpHandler"/>.
    /// Automatically falls back to DoH (<see cref="DohResolver"/>) when YouTube domains are DNS-blocked.
    /// </summary>
    [DebuggerHidden]
    internal static async ValueTask<Stream> ConnectWithKeepAliveAsync(
               SocketsHttpConnectionContext ctx,
               CancellationToken ct)
    {
        IPAddress[] addresses;

        // If the request goes to a local proxy (127.0.0.1 / localhost), connect directly without DNS/DoH
        if (ctx.DnsEndPoint.Host is "127.0.0.1" or "localhost" ||
            IPAddress.TryParse(ctx.DnsEndPoint.Host, out _))
        {
            var targetHost = ctx.DnsEndPoint.Host;
            var socketDirect = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            try
            {
                await socketDirect.ConnectAsync(targetHost, ctx.DnsEndPoint.Port, ct).ConfigureAwait(false);
                return new NetworkStream(socketDirect, ownsSocket: true);
            }
            catch
            {
                socketDirect.Dispose();
                throw;
            }
        }

        try
        {
            addresses = await ResolveWithIpv4PreferenceAsync(
                ctx.DnsEndPoint.Host, ctx.DnsEndPoint.AddressFamily, ct)
                .ConfigureAwait(false);
        }
        catch (SocketException ex) when (
            ex.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData &&
            DohResolver.IsFallbackDomain(ctx.DnsEndPoint.Host))
        {
            Log.Warn($"[SharedHttp] DNS blocked for {ctx.DnsEndPoint.Host}, trying DoH fallback...");

            var dohResult = await DohResolver.ResolveAsync(ctx.DnsEndPoint.Host, ct)
                .ConfigureAwait(false);

            if (dohResult is null || dohResult.Length == 0)
            {
                throw new SocketException((int)SocketError.HostNotFound);
            }

            addresses = dohResult;
        }

        if (addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        long connectionId = Interlocked.Increment(ref _connectionSequence);
        var initialUri = ctx.InitialRequestMessage.RequestUri;
        var initialRange = ctx.InitialRequestMessage.Headers.Range?.ToString() ?? "-";

        Log.Debug(
            $"[SharedHttp] #{connectionId} opening: " +
            $"endpoint={ctx.DnsEndPoint.Host}:{ctx.DnsEndPoint.Port}, " +
            $"initial={initialUri?.Host ?? "(none)"}{initialUri?.AbsolutePath ?? string.Empty}, " +
            $"range={initialRange}, " +
            $"resolved={addresses.Length}, first={addresses[0]}");

        var socket = new Socket(
            addresses[0].AddressFamily,
            SocketType.Stream,
            ProtocolType.Tcp)
        {
            NoDelay = true,
        };

        try
        {
            await socket.ConnectAsync(addresses, ctx.DnsEndPoint.Port, ct)
                .ConfigureAwait(false);

            Log.Debug(
                $"[SharedHttp] #{connectionId} connected: " +
                $"local={socket.LocalEndPoint}, remote={socket.RemoteEndPoint}");

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Resolves DNS with IPv4 filtering for WinDivert compatibility.
    /// Extracted from ConnectCallback for reuse in the DoH fallback path.
    /// </summary>
    [DebuggerHidden]
    private static async Task<IPAddress[]> ResolveWithIpv4PreferenceAsync(
        string host,
        AddressFamily requestedFamily,
        CancellationToken ct)
    {
        var allAddresses = await Dns.GetHostAddressesAsync(
            host, requestedFamily, ct).ConfigureAwait(false);

        if (requestedFamily != AddressFamily.Unspecified)
            return allAddresses;

        // Unspecified DNS returns both A and AAAA.
        // Keep IPv4 only: WinDivert-based bypass only works on IPv4.
        var ipv4Only = Array.FindAll(
            allAddresses,
            static a => a.AddressFamily == AddressFamily.InterNetwork);

        return ipv4Only.Length > 0 ? ipv4Only : allAddresses;
    }

    /// <summary>
    /// Creates a Range request with a User-Agent matching the client specified in the <c>c=</c> URL parameter.
    /// </summary>
    public static HttpRequestMessage CreateRangeRequest(string url, long start, long end)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(start, end);
        request.Version = HttpVersion.Version11;
        ApplyUserAgentFromUrl(request, url);
        return request;
    }

    /// <summary>
    /// Sets the User-Agent header based on the <c>c=</c> parameter in the URL.
    /// </summary>
    public static void ApplyUserAgentFromUrl(HttpRequestMessage request, string url)
    {
        var clientParam = UrlEx.TryGetQueryParameterValue(url, "c");
        var ua = clientParam is not null
            ? YoutubeClientUtils.GetUserAgentForClient(clientParam)
            : YoutubeClientUtils.UaWebRemix;

        request.Headers.TryAddWithoutValidation("User-Agent", ua);
    }
}