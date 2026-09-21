using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Audio.Http;

/// <summary>
/// Глобальный HTTP-клиент для CDN-запросов аудио.
/// Поддерживает горячую замену при смене сетевого интерфейса или настроек прокси.
/// </summary>
public static class SharedHttpClient
{
    private static INetworkManager? _networkManager;
    private static long _connectionSequence;

    /// <summary>
    /// Инициализирует статический фасад ссылкой на централизованный NetworkManager.
    /// </summary>
    public static void Initialize(INetworkManager networkManager)
    {
        _networkManager = networkManager;
    }

    /// <summary>Текущий активный экземпляр клиента.</summary>
    public static HttpClient Instance =>
        _networkManager?.AudioClient ?? FallbackClient;

    private static readonly HttpClient FallbackClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromSeconds(90),
        MaxConnectionsPerServer = 6
    });

    /// <summary>
    /// Пересоздаёт HTTP-клиент с новым пулом соединений.
    /// Старый клиент утилизируется через 30 секунд — чтобы не обрывать активные range-запросы.
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
    /// Callback установки TCP-соединения для <see cref="SocketsHttpHandler"/>.
    /// При DNS-блокировке YouTube-доменов автоматически переключается на DoH
    /// (<see cref="DohResolver"/>).
    /// </summary>
    internal static async ValueTask<Stream> ConnectWithKeepAliveAsync(
           SocketsHttpConnectionContext ctx,
           CancellationToken ct)
    {
        IPAddress[] addresses;

        // Если запрос идёт к локальному прокси (127.0.0.1 / localhost) — подключаемся напрямую без DNS/DoH
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
            Log.Warn($"[SharedHttpClient] DNS blocked for {ctx.DnsEndPoint.Host}, trying DoH fallback...");

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
            $"[SharedHttpClient] Connect#{connectionId} opening: " +
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
                $"[SharedHttpClient] Connect#{connectionId} connected: " +
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
    /// Резолвит DNS с фильтрацией до IPv4 для WinDivert-совместимости.
    /// Выделен из ConnectCallback для переиспользования в DoH fallback path.
    /// </summary>
    private static async Task<IPAddress[]> ResolveWithIpv4PreferenceAsync(
        string host,
        AddressFamily requestedFamily,
        CancellationToken ct)
    {
        var allAddresses = await Dns.GetHostAddressesAsync(
            host, requestedFamily, ct).ConfigureAwait(false);

        if (requestedFamily != AddressFamily.Unspecified)
            return allAddresses;

        // При Unspecified DNS возвращает A и AAAA.
        // Оставляем только IPv4: WinDivert-based bypass работает только на IPv4.
        var ipv4Only = Array.FindAll(
            allAddresses,
            static a => a.AddressFamily == AddressFamily.InterNetwork);

        return ipv4Only.Length > 0 ? ipv4Only : allAddresses;
    }

    /// <summary>
    /// Создаёт Range-запрос с User-Agent, соответствующим клиенту из параметра <c>c=</c> в URL.
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
    /// Устанавливает заголовок User-Agent по параметру <c>c=</c> в URL.
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