using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LMP.Core.Audio.Http;

namespace LMP.Core.Services;

/// <summary>
/// Централизованный сервис управления жизненным циклом сетевых клиентов и состоянием адаптеров.
/// </summary>
public sealed class NetworkManager : IDisposable
{
    private const int RebuildCooldownMs = 15_000;
    private const int ClientDrainTimeoutSeconds = 30;

    private readonly Lock _rebuildLock = new();
    private readonly Lock _stateLock = new();
    private static IPAddress? _cachedOutboundIp;
    private static bool _cachedVpnStatus;
    private static readonly Lock _routeCacheLock = new();

    private volatile HttpClient _audioClient;
    private volatile HttpClient _apiClient;
    private volatile HttpClient _imageClient;
    private volatile HttpClient _probeClient;

    private ProxySettings? _currentProxy;
    private readonly InternetProfile _currentProfile = InternetProfile.Medium;
    private string? _lastOutboundIp;
    private bool _isVpnActive;
    private long _lastRebuildTick;
    private bool _disposed;

    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenSource? _addressChangeDebounceCts;
    private readonly Task _watchdogTask;

    /// <inheritdoc/>
    public HttpClient AudioClient => _audioClient;

    /// <inheritdoc/>
    public HttpClient ApiClient => _apiClient;

    /// <inheritdoc/>
    public HttpClient ImageClient => _imageClient;

    /// <inheritdoc/>
    public HttpClient ProbeClient => _probeClient;

    /// <inheritdoc/>
    public ProxySettings? CurrentProxy
    {
        get { lock (_stateLock) return _currentProxy; }
    }

    /// <inheritdoc/>
    public InternetProfile CurrentProfile
    {
        get { lock (_stateLock) return _currentProfile; }
    }

    /// <inheritdoc/>
    public string? OutboundIp => Volatile.Read(ref _lastOutboundIp);

    /// <inheritdoc/>
    public bool IsVpnActive => Volatile.Read(ref _isVpnActive);

    /// <summary>
    /// Выполняет динамическую проверку активного исходящего сетевого маршрута на принадлежность к VPN/TUN-туннелю.
    /// </summary>
    public bool CheckIsVpnActive()
    {
        var ip = GetOutboundIpAddress();
        return ip != null && DetectVpnRoute(ip);
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

    /// <inheritdoc/>
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

        (_audioClient, _apiClient, _imageClient, _probeClient) = CreateClientCluster(_currentProxy);

        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        _watchdogTask = Task.Run(NetworkWatchdogLoopAsync);

        Log.Info($"[NetworkManager] Initialized. Outbound IP: {_lastOutboundIp ?? "(none)"}, VPN: {_isVpnActive}");
    }

    /// <inheritdoc/>
    public void UpdateProxy(ProxySettings? proxy)
    {
        lock (_stateLock)
        {
            _currentProxy = proxy;
        }
        RebuildAll("Proxy settings updated", force: true);
    }

    /// <inheritdoc/>
    public void RebuildAll(string reason, bool force = false)
    {
        long now = Environment.TickCount64;

        if (!force)
        {
            long elapsed = now - Volatile.Read(ref _lastRebuildTick);
            if (elapsed < RebuildCooldownMs)
            {
                Log.Debug($"[NetworkManager] Rebuild skipped (cooldown: {elapsed}ms < {RebuildCooldownMs}ms). Reason: {reason}");
                return;
            }
        }

        Volatile.Write(ref _lastRebuildTick, now);

        HttpClient oldAudio, oldApi, oldImage, oldProbe;
        HttpClient newAudio, newApi, newImage, newProbe;

        ProxySettings? proxy;
        lock (_stateLock)
        {
            proxy = _currentProxy;
        }

        (newAudio, newApi, newImage, newProbe) = CreateClientCluster(proxy);

        lock (_rebuildLock)
        {
            oldAudio = Interlocked.Exchange(ref _audioClient, newAudio);
            oldApi = Interlocked.Exchange(ref _apiClient, newApi);
            oldImage = Interlocked.Exchange(ref _imageClient, newImage);
            oldProbe = Interlocked.Exchange(ref _probeClient, newProbe);
        }

        DohResolver.InvalidateCache();
        AudioSourceFactory.CdnBlacklist.Clear();

        ScheduleDrainDisposal(oldAudio, oldApi, oldImage, oldProbe);

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

    private static (HttpClient Audio, HttpClient Api, HttpClient Image, HttpClient Probe) CreateClientCluster(ProxySettings? proxy)
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
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            AllowAutoRedirect = false,
            UseCookies = false
        };

        var apiClient = new HttpClient(apiHandler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

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

        return (audioClient, apiClient, imageClient, probeClient);
    }

    /// <summary>
    /// Неблокирующее мягкое освобождение старых пулов. Использование Timer вместо Task.Delay
    /// позволяет избежать лишней State Machine аллокации.
    /// </summary>
    private static void ScheduleDrainDisposal(params HttpClient[] oldClients)
    {
        Timer? timer = null;
        timer = new Timer(_ =>
        {
            for (int i = 0; i < oldClients.Length; i++)
            {
                try
                {
                    oldClients[i].CancelPendingRequests();
                    oldClients[i].Dispose();
                }
                catch (ObjectDisposedException) { /* Игнорируем штатный dispose */ }
                catch (Exception ex)
                {
                    Log.Debug($"[NetworkManager] Silent exception during graceful client disposal: {ex.Message}");
                }
            }
            timer?.Dispose();
        }, null, TimeSpan.FromSeconds(ClientDrainTimeoutSeconds), Timeout.InfiniteTimeSpan);
    }

    #region Network Watchdog & Address Monitoring

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        Log.Debug("[NetworkManager] System NetworkAddressChanged event received");

        lock (_routeCacheLock)
        {
            _cachedOutboundIp = null;
        }

        lock (_stateLock)
        {
            _addressChangeDebounceCts?.Cancel();
            _addressChangeDebounceCts?.Dispose();
            _addressChangeDebounceCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        }

        _ = HandleAddressChangedDebouncedAsync(_addressChangeDebounceCts.Token);
    }

    private async Task HandleAddressChangedDebouncedAsync(CancellationToken ct)
    {
        try
        {
            // Увеличенный дебаунс: даём TUN/VPN адаптеру стабилизировать таблицу маршрутизации
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);

            var (currentIp, isVpn) = EvaluateOutboundRoute();
            if (currentIp == null)
            {
                Log.Debug("[NetworkManager] Address change ignored — no outbound route");
                return;
            }

            var previousIp = Volatile.Read(ref _lastOutboundIp);
            bool previousVpn = Volatile.Read(ref _isVpnActive);

            Volatile.Write(ref _isVpnActive, isVpn);
            Volatile.Write(ref _lastOutboundIp, currentIp);

            if (string.Equals(currentIp, previousIp, StringComparison.Ordinal) && previousVpn == isVpn)
            {
                Log.Debug($"[NetworkManager] Address change ignored — outbound IP and VPN state unchanged ({currentIp})");
                return;
            }

            RebuildAll($"Adapter route changed ({previousIp ?? "(none)"} → {currentIp}, VPN: {previousVpn} → {isVpn})", force: false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"[NetworkManager] Address change resolution failed: {ex.Message}");
        }
    }

    private async Task NetworkWatchdogLoopAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token).ConfigureAwait(false);

            while (!_cts.Token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(5), _cts.Token).ConfigureAwait(false);

                var (currentIp, isVpn) = EvaluateOutboundRoute();
                var previousIp = Volatile.Read(ref _lastOutboundIp);
                bool previousVpn = Volatile.Read(ref _isVpnActive);

                if (currentIp != null && (previousIp == null || !string.Equals(currentIp, previousIp, StringComparison.Ordinal) || previousVpn != isVpn))
                {
                    Log.Info($"[NetworkManager] Watchdog detected unhandled route change: {previousIp} (VPN: {previousVpn}) → {currentIp} (VPN: {isVpn})");
                    OnNetworkAddressChanged(this, EventArgs.Empty);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"[NetworkManager] Watchdog error: {ex.Message}");
        }
    }

    private static (string? OutboundIp, bool IsVpn) EvaluateOutboundRoute()
    {
        var ip = GetOutboundIpAddress();
        if (ip == null)
            return (null, false);

        lock (_routeCacheLock)
        {
            if (ip.Equals(_cachedOutboundIp))
            {
                return (ip.ToString(), _cachedVpnStatus);
            }

            bool isVpn = DetectVpnRoute(ip);
            _cachedOutboundIp = ip;
            _cachedVpnStatus = isVpn;
            return (ip.ToString(), isVpn);
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
        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4) return false;

        // RFC 2544 — Benchmark / Fake-IP (используется Clash, sing-box, Mihomo, v2ray в TUN-режиме)
        if (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19)) return true;

        // RFC 5737 — Documentation ranges (используются некоторыми виртуальными TUN-адаптерами)
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
            Log.Debug($"[NetworkManager] Active interface detected as VPN by type: {iface.NetworkInterfaceType} ({iface.Name} / {iface.Description})");
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

    #endregion

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _cts.Cancel();
        _cts.Dispose();

        try
        {
            _watchdogTask.Wait(TimeSpan.FromMilliseconds(200));
        }
        catch { }

        _audioClient.Dispose();
        _apiClient.Dispose();
        _imageClient.Dispose();
        _probeClient.Dispose();
    }
}