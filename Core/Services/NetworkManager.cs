using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using LMP.Core.Audio.Http;

namespace LMP.Core.Services;

/// <summary>
/// Централизованный сервис управления жизненным циклом сетевых клиентов и состоянием адаптеров.
/// </summary>
public sealed partial class NetworkManager : IDisposable
{
    private const int RebuildCooldownMs = 15_000;
    private const int ClientDrainTimeoutSeconds = 30;
    private const int AddressChangeDebounceMs = 3_000;
    private const int AddressEventRearmCooldownMs = 250;

    private readonly Lock _rebuildLock = new();
    private readonly Lock _stateLock = new();
    private static uint _cachedBestIfIndex;
    private static IPAddress? _cachedOutboundIp;
    private static string? _cachedOutboundIpStr;
    private static bool _cachedVpnStatus;
    private static readonly Lock _routeCacheLock = new();

    private volatile HttpClient _audioClient;
    private volatile HttpClient _apiClient;
    private volatile HttpClient _imageClient;
    private volatile HttpClient _probeClient;
    private volatile HttpClient _updateClient;

    private ProxySettings? _currentProxy;
    private readonly InternetProfile _currentProfile = InternetProfile.Medium;
    private string? _lastOutboundIp;
    private bool _isVpnActive;
    private long _lastRebuildTick;
    private long _lastAddressEventTick;
    private int _isEvaluatingRoute;
    private bool _disposed;

    private readonly CancellationTokenSource _cts = new();
    private readonly Timer _addressDebounceTimer;
    private readonly Task _watchdogTask;
    public HttpClient AudioClient => _audioClient;
    public HttpClient ApiClient => _apiClient;
    public HttpClient ImageClient => _imageClient;
    public HttpClient ProbeClient => _probeClient;

    /// <summary>
    /// Клиент для проверки и скачивания обновлений с поддержкой редиректов (GitHub Releases / S3 CDN).
    /// </summary>
    public HttpClient UpdateClient => _updateClient;

    public ProxySettings? CurrentProxy
    {
        get
        {
            lock (_stateLock) return _currentProxy;
        }
    }

    public InternetProfile CurrentProfile
    {
        get
        {
            lock (_stateLock) return _currentProfile;
        }
    }

    public string? OutboundIp => Volatile.Read(ref _lastOutboundIp);
    public bool IsVpnActive => Volatile.Read(ref _isVpnActive);

    /// <summary>
    /// Выполняет динамическую проверку активного исходящего сетевого маршрута на принадлежность к VPN/TUN-туннелю.
    /// </summary>
    public bool CheckIsVpnActive()
    {
        var (_, isVpn) = EvaluateOutboundRoute();
        return isVpn;
    }

    /// <summary>
    /// Проверяет наличие и параметры активного прокси-сервера (явно заданного в приложении или системного прокси ОС).
    /// </summary>
    public (bool IsActive, string Host, int Port, bool IsSystemProxy) GetActiveProxyInfo()
    {
        lock (_stateLock)
        {
            if (_currentProxy is { Enabled: true } custom && !string.IsNullOrWhiteSpace(custom.Host))
            {
                int customPort = custom.Port > 0 ? custom.Port : 8080;
                return (true, custom.Host.Trim(), customPort, false);
            }
        }

        try
        {
            var defaultProxy = HttpClient.DefaultProxy;
            if (defaultProxy != null)
            {
                var targetUri = new Uri("https://music.youtube.com");
                if (!defaultProxy.IsBypassed(targetUri))
                {
                    var proxyUri = defaultProxy.GetProxy(targetUri);
                    if (proxyUri != null && !proxyUri.Equals(targetUri))
                    {
                        return (true, proxyUri.Host, proxyUri.Port, true);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[NetworkManager] Error querying system proxy: {ex.Message}");
        }

        return (false, string.Empty, 0, false);
    }

    public event Action? NetworkRebuilt;

    /// <summary>
    /// Инициализирует NetworkManager с начальными параметрами сети.
    /// </summary>
    public NetworkManager(ProxySettings? initialProxy = null, InternetProfile initialProfile = InternetProfile.Medium)
    {
        _currentProxy = initialProxy;
        _currentProfile = initialProfile;

        var (outboundIp, isVpn) = EvaluateOutboundRoute();
        _lastOutboundIp = outboundIp;
        _isVpnActive = isVpn;

        (_audioClient, _apiClient, _imageClient, _probeClient, _updateClient) = CreateClientCluster(_currentProxy);

        _addressDebounceTimer = new Timer(OnAddressDebounceTimerCallback, this, Timeout.Infinite, Timeout.Infinite);
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        _watchdogTask = Task.Run(NetworkWatchdogLoopAsync);

        Log.Info($"[NetworkManager] Initialized. Outbound IP: {_lastOutboundIp ?? "(none)"}, VPN: {_isVpnActive}");
    }

    public void UpdateProxy(ProxySettings? proxy)
    {
        lock (_stateLock)
        {
            _currentProxy = proxy;
        }

        RebuildAll("Proxy settings updated", force: true);
    }

    public void RebuildAll(string reason, bool force = false)
    {
        long now = Environment.TickCount64;

        if (!force)
        {
            long elapsed = now - Volatile.Read(ref _lastRebuildTick);
            if (elapsed < RebuildCooldownMs)
            {
                Log.Debug(
                    $"[NetworkManager] Rebuild skipped (cooldown: {elapsed}ms < {RebuildCooldownMs}ms). Reason: {reason}");
                return;
            }
        }

        Volatile.Write(ref _lastRebuildTick, now);

        HttpClient oldAudio, oldApi, oldImage, oldProbe, oldUpdate;
        HttpClient newAudio, newApi, newImage, newProbe, newUpdate;

        ProxySettings? proxy;
        lock (_stateLock)
        {
            proxy = _currentProxy;
        }

        (newAudio, newApi, newImage, newProbe, newUpdate) = CreateClientCluster(proxy);

        lock (_rebuildLock)
        {
            oldAudio = Interlocked.Exchange(ref _audioClient, newAudio);
            oldApi = Interlocked.Exchange(ref _apiClient, newApi);
            oldImage = Interlocked.Exchange(ref _imageClient, newImage);
            oldProbe = Interlocked.Exchange(ref _probeClient, newProbe);
            oldUpdate = Interlocked.Exchange(ref _updateClient, newUpdate);
        }

        DohResolver.InvalidateCache();
        AudioSourceFactory.CdnBlacklist.Clear();

        ScheduleDrainDisposal(oldAudio, oldApi, oldImage, oldProbe, oldUpdate);

        Log.Info($"[NetworkManager] Network cluster rebuilt. Reason: {reason}, Force: {force}");

        try
        {
            NetworkRebuilt?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn($"[NetworkManager] NetworkRebuilt event handler error: {ex.Message}");
        }
    }

    private static (HttpClient Audio, HttpClient Api, HttpClient Image, HttpClient Probe, HttpClient Update)
        CreateClientCluster(ProxySettings? proxy)
    {
        AudioSourceFactory.CurrentProxySettings = proxy;
        var customProxy = ProxyHelper.CreateWebProxy(proxy);
        bool hasExplicitProxy = customProxy is not null;

        // Если задан явный прокси в LMP — используем его.
        // Если нет — разрешаем .NET использовать системный прокси Windows (HttpClient.DefaultProxy)
        // либо идти через текущий шлюз/TUN адаптер.
        var effectiveProxy = hasExplicitProxy ? customProxy : null;

        // 1. Audio CDN Client (HTTP/1.1, KeepAlive, DoH)
        var audioHandler = new SocketsHttpHandler
        {
            ConnectCallback = hasExplicitProxy ? null : SharedHttpClient.ConnectWithKeepAliveAsync,
            Proxy = effectiveProxy,
            UseProxy = true,
            // Отключаем клиентские таймауты.
            // Позволяем серверу YouTube самому закрыть соединение (TCP FIN).
            // Тогда чтение вернет 0 байт, и IOException не возникнет!
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            MaxConnectionsPerServer = 6,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            ResponseDrainTimeout = TimeSpan.FromSeconds(1),
            ConnectTimeout = TimeSpan.FromSeconds(6),
        };

        var audioClient = new HttpClient(audioHandler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        audioClient.DefaultRequestHeaders.Add("Accept", "*/*");

        // 2. Api Client (HTTP/2, KeepAlive Ping)
        var apiHandler = new SocketsHttpHandler
        {
            ConnectCallback = hasExplicitProxy ? null : SharedHttpClient.ConnectWithKeepAliveAsync,
            Proxy = effectiveProxy,
            UseProxy = true,
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            MaxConnectionsPerServer = 20,
            EnableMultipleHttp2Connections = true,
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            ConnectTimeout = TimeSpan.FromSeconds(6),
            AutomaticDecompression =
                DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            AllowAutoRedirect = false,
            UseCookies = false
        };

        var apiClient = new HttpClient(apiHandler) { Timeout = Timeout.InfiniteTimeSpan };

        // 3. Image Client (HTTP/2)
        var imageHandler = new SocketsHttpHandler
        {
            ConnectCallback = hasExplicitProxy ? null : SharedHttpClient.ConnectWithKeepAliveAsync,
            Proxy = effectiveProxy,
            UseProxy = true,
            MaxConnectionsPerServer = 8,
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            EnableMultipleHttp2Connections = true,
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            ConnectTimeout = TimeSpan.FromSeconds(6)
        };

        var imageClient = new HttpClient(imageHandler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        // 4. Probe Client (HTTP/1.1, быстрые таймауты и своевременная очистка пула)
        var probeHandler = new SocketsHttpHandler
        {
            ConnectCallback = hasExplicitProxy ? null : SharedHttpClient.ConnectWithKeepAliveAsync,
            Proxy = effectiveProxy,
            UseProxy = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
            ResponseDrainTimeout = TimeSpan.FromMilliseconds(250),
            MaxConnectionsPerServer = 4,
            ConnectTimeout = TimeSpan.FromSeconds(4),
            AllowAutoRedirect = false
        };

        var probeClient = new HttpClient(probeHandler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        // 5. Update Client (HTTP/1.1, автоматические редиректы для GitHub Releases и S3 CDN)
        var updateHandler = new SocketsHttpHandler
        {
            ConnectCallback = hasExplicitProxy ? null : SharedHttpClient.ConnectWithKeepAliveAsync,
            Proxy = effectiveProxy,
            UseProxy = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        };

        var updateClient = new HttpClient(updateHandler) { Timeout = TimeSpan.FromSeconds(30) };

        return (audioClient, apiClient, imageClient, probeClient, updateClient);
    }

    private sealed class DrainDisposalState(HttpClient[] clients)
    {
        public readonly HttpClient[] Clients = clients;
        public Timer? Timer;
    }

    /// <summary>
    /// Неблокирующее мягкое освобождение старых пулов. Использование Timer вместо Task.Delay
    /// позволяет избежать лишней State Machine аллокации.
    /// </summary>
    private static void ScheduleDrainDisposal(params HttpClient[] oldClients)
    {
        var state = new DrainDisposalState(oldClients);
        state.Timer = new Timer(static s =>
        {
            var drain = (DrainDisposalState)s!;
            for (int i = 0; i < drain.Clients.Length; i++)
            {
                try
                {
                    drain.Clients[i].CancelPendingRequests();
                    drain.Clients[i].Dispose();
                }
                catch (ObjectDisposedException)
                {
                    /* Игнорируем штатный dispose */
                }
                catch (Exception ex)
                {
                    Log.Debug($"[NetworkManager] Silent exception during graceful client disposal: {ex.Message}");
                }
            }

            drain.Timer?.Dispose();
        }, state, TimeSpan.FromSeconds(ClientDrainTimeoutSeconds), Timeout.InfiniteTimeSpan);
    }

    #region Network Watchdog & Address Monitoring

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed)) return;

        long now = Environment.TickCount64;
        long prev = Volatile.Read(ref _lastAddressEventTick);
        if (now - prev < AddressEventRearmCooldownMs)
            return;

        Volatile.Write(ref _lastAddressEventTick, now);
        Log.Debug("[NetworkManager] System NetworkAddressChanged event received (debouncing)");

        try
        {
            _addressDebounceTimer.Change(AddressChangeDebounceMs, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void OnAddressDebounceTimerCallback(object? state)
    {
        var self = (NetworkManager)state!;
        self.EvaluateAndRebuildIfRouteChanged(fromWatchdog: false);
    }

    private void EvaluateAndRebuildIfRouteChanged(bool fromWatchdog)
    {
        if (Volatile.Read(ref _disposed)) return;
        if (Interlocked.CompareExchange(ref _isEvaluatingRoute, 1, 0) != 0)
            return;

        try
        {
            var (currentIp, isVpn) = EvaluateOutboundRoute(verifySocketIp: true);
            if (currentIp == null)
            {
                if (!fromWatchdog)
                    Log.Debug("[NetworkManager] Address change ignored — no outbound route");
                return;
            }

            var previousIp = Volatile.Read(ref _lastOutboundIp);
            bool previousVpn = Volatile.Read(ref _isVpnActive);

            if (string.Equals(currentIp, previousIp, StringComparison.Ordinal) && previousVpn == isVpn)
            {
                if (!fromWatchdog)
                {
                    Log.Debug(
                        $"[NetworkManager] Address change ignored — outbound IP and VPN state unchanged ({currentIp})");
                }
                return;
            }

            Volatile.Write(ref _isVpnActive, isVpn);
            Volatile.Write(ref _lastOutboundIp, currentIp);

            if (fromWatchdog)
            {
                Log.Info(
                    $"[NetworkManager] Watchdog detected unhandled route change: {previousIp ?? "(none)"} (VPN: {previousVpn}) → {currentIp} (VPN: {isVpn})");
            }

            RebuildAll($"Adapter route changed ({previousIp ?? "(none)"} → {currentIp}, VPN: {previousVpn} → {isVpn})",
                force: false);
        }
        catch (Exception ex)
        {
            Log.Warn($"[NetworkManager] Route evaluation failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _isEvaluatingRoute, 0);
        }
    }

    private async Task NetworkWatchdogLoopAsync()
    {
        try
        {
            if (!await _cts.Token.DelayNoThrowAsync(30_000, continueOnCapturedContext: false).ConfigureAwait(false))
                return;

            while (!_cts.Token.IsCancellationRequested)
            {
                if (!await _cts.Token.DelayNoThrowAsync(300_000, continueOnCapturedContext: false).ConfigureAwait(false))
                    break;

                EvaluateAndRebuildIfRouteChanged(fromWatchdog: true);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[NetworkManager] Watchdog error: {ex.Message}");
        }
    }

    private static (string? OutboundIp, bool IsVpn) EvaluateOutboundRoute(bool verifySocketIp = false)
    {
        // Fast-path на Windows: проверяем индекс лучшего интерфейса маршрутизации через iphlpapi.dll (0 B аллокаций, ~1.5 мкс).
        // 0x08080808 = 8.8.8.8 (в network byte order и little-endian представление идентично).
        uint currentIfIndex = 0;
        if (OperatingSystem.IsWindows())
        {
            if (GetBestInterface(0x08080808u, out uint bestIfIndex) == 0 && bestIfIndex != 0)
            {
                currentIfIndex = bestIfIndex;
                if (!verifySocketIp)
                {
                    lock (_routeCacheLock)
                    {
                        if (currentIfIndex == _cachedBestIfIndex && _cachedOutboundIpStr != null)
                        {
                            return (_cachedOutboundIpStr, _cachedVpnStatus);
                        }
                    }
                }
            }
        }

        var ip = GetOutboundIpAddress();
        if (ip == null)
            return (null, false);

        lock (_routeCacheLock)
        {
            if (ip.Equals(_cachedOutboundIp) && _cachedOutboundIpStr != null)
            {
                _cachedBestIfIndex = currentIfIndex;
                return (_cachedOutboundIpStr, _cachedVpnStatus);
            }

            bool isVpn = DetectVpnRoute(ip);
            string ipStr = ip.ToString();
            _cachedBestIfIndex = currentIfIndex;
            _cachedOutboundIp = ip;
            _cachedOutboundIpStr = ipStr;
            _cachedVpnStatus = isVpn;
            return (ipStr, isVpn);
        }
    }

    private static IPAddress? GetOutboundIpAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch
        {
            try
            {
                using var socket6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                socket6.Connect("2001:4860:4860::8888", 65530);
                return (socket6.LocalEndPoint as IPEndPoint)?.Address;
            }
            catch
            {
                return null;
            }
        }
    }

    private static bool DetectVpnRoute(IPAddress outboundIp)
    {
        if (IsSpecialTunIpRange(outboundIp))
            return true;

        var activeInterface = FindInterfaceByIp(outboundIp);
        return activeInterface != null && IsVpnInterface(activeInterface);
    }

    private static bool IsSpecialTunIpRange(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return false;

        Span<byte> bytes = stackalloc byte[16];
        if (!ip.TryWriteBytes(bytes, out int bytesWritten) || bytesWritten != 4)
            return false;

        // RFC 2544 — Benchmark / Fake-IP (Clash, sing-box, Mihomo, v2ray TUN)
        if (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19)) return true;

        // RFC 5737 — Documentation ranges used by virtual TUN adapters
        if (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) return true;
        if (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) return true;

        return false;
    }

    private static NetworkInterface? FindInterfaceByIp(IPAddress targetIp)
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            for (int i = 0; i < interfaces.Length; i++)
            {
                var iface = interfaces[i];
                if (iface.OperationalStatus != OperationalStatus.Up)
                    continue;

                var unicastAddresses = iface.GetIPProperties().UnicastAddresses;
                for (int j = 0; j < unicastAddresses.Count; j++)
                {
                    if (unicastAddresses[j].Address.Equals(targetIp))
                        return iface;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[NetworkManager] Failed to query network interfaces: {ex.Message}");
        }

        return null;
    }

    private static bool IsVpnInterface(NetworkInterface iface)
    {
        if (iface.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
        {
            Log.Debug(
                $"[NetworkManager] Active interface detected as VPN by type: {iface.NetworkInterfaceType} ({iface.Name} / {iface.Description})");
            return true;
        }

        var name = iface.Name;
        var desc = iface.Description;

        if (ContainsVpnKeyword(name) || ContainsVpnKeyword(desc))
        {
            Log.Debug($"[NetworkManager] Active interface detected as VPN by keyword: {name} / {desc}");
            return true;
        }

        return false;
    }

    private static bool ContainsVpnKeyword(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        ReadOnlySpan<char> span = text.AsSpan();

        if (span.Contains("vpn".AsSpan(), StringComparison.OrdinalIgnoreCase))
            return true;

        if (span.Contains("wireguard".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            span.Contains("wintun".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            span.Contains("openvpn".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            span.Contains("sing-box".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            span.Contains("xray".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            span.Contains("v2ray".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            span.Contains("clash".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            span.Contains("mihomo".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            span.Contains("tailscale".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            span.Contains("zerotier".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            span.Contains("nordlynx".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            span.Contains("warp".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            span.Contains("cisco".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            span.Contains("anyconnect".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            span.Contains("shadowsocks".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            span.Contains("fortinet".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            span.Contains("forticlient".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (span.Contains("tap".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            span.Contains("tun".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            if (IsTunOrTapIdentifier(span))
                return true;
        }

        return false;
    }

    private static bool IsTunOrTapIdentifier(ReadOnlySpan<char> span)
    {
        for (int i = 0; i <= span.Length - 3; i++)
        {
            var slice = span.Slice(i, 3);
            if (slice.Equals("tap".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                slice.Equals("tun".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                bool beforeOk = i == 0 || !char.IsLetter(span[i - 1]) || (i == 1 && (span[0] == 'u' || span[0] == 'U'));
                bool afterOk = i + 3 >= span.Length || !char.IsLetter(span[i + 3]);
                if (beforeOk && afterOk)
                    return true;
            }
        }

        return false;
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial int GetBestInterface(uint dwDestAddr, out uint pdwBestIfIndex);

    #endregion

    public void Dispose()
    {
        if (Volatile.Read(ref _disposed)) return;
        Volatile.Write(ref _disposed, true);

        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _addressDebounceTimer.Dispose();
        _cts.Cancel();
        _cts.Dispose();

        try
        {
            _watchdogTask.Wait(TimeSpan.FromMilliseconds(200));
        }
        catch
        {
        }

        _audioClient.Dispose();
        _apiClient.Dispose();
        _imageClient.Dispose();
        _probeClient.Dispose();
        _updateClient.Dispose();
    }
}
