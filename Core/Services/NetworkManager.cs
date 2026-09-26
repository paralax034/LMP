using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LMP.Core.Audio.Http;

namespace LMP.Core.Services;

/// <summary>
/// Централизованный сервис управления жизненным циклом сетевых клиентов и состоянием адаптеров.
/// </summary>
public sealed class NetworkManager
{
    private const int RebuildCooldownMs = 15_000;
    private const int ClientDrainTimeoutSeconds = 30;

    private readonly Lock _rebuildLock = new();
    private readonly Lock _stateLock = new();

    private volatile HttpClient _audioClient;
    private volatile HttpClient _apiClient;
    private volatile HttpClient _imageClient;
    private volatile HttpClient _probeClient;

    private ProxySettings? _currentProxy;
    private InternetProfile _currentProfile = InternetProfile.Medium;
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

    /// <inheritdoc/>
    public event Action? NetworkRebuilt;

    /// <summary>
    /// Инициализирует NetworkManager с начальными параметрами сети.
    /// </summary>
    public NetworkManager(ProxySettings? initialProxy = null, InternetProfile initialProfile = InternetProfile.Medium)
    {
        _currentProxy = initialProxy;
        _currentProfile = initialProfile;
        _lastOutboundIp = GetOutboundIp();
        _isVpnActive = _lastOutboundIp != null && IsVpnTunAddress(_lastOutboundIp);

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
    public void UpdateProfile(InternetProfile profile)
    {
        lock (_stateLock)
        {
            _currentProfile = profile;
        }
        AudioSourceFactory.ApplyInternetProfile(profile);
        Log.Info($"[NetworkManager] Internet profile updated: {profile}");
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
            PooledConnectionLifetime = TimeSpan.FromSeconds(90),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
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
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
            MaxConnectionsPerServer = 20,
            EnableMultipleHttp2Connections = true,
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            KeepAlivePingDelay = TimeSpan.FromSeconds(15),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
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
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            EnableMultipleHttp2Connections = true,
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            KeepAlivePingDelay = TimeSpan.FromSeconds(15),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
            ConnectTimeout = TimeSpan.FromSeconds(6)
        };

        var imageClient = new HttpClient(imageHandler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        // 4. Probe Client (HTTP/1.1, быстрые таймауты)
        var probeHandler = new SocketsHttpHandler
        {
            ConnectCallback = hasExplicitProxy ? null : SharedHttpClient.ConnectWithKeepAliveAsync,
            Proxy = effectiveProxy,
            UseProxy = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
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

    private static void ScheduleDrainDisposal(params HttpClient[] oldClients)
    {
        _ = Task.Delay(TimeSpan.FromSeconds(ClientDrainTimeoutSeconds)).ContinueWith(_ =>
        {
            foreach (var client in oldClients)
            {
                try { client.Dispose(); } catch { }
            }
        }, TaskScheduler.Default);
    }

    #region Network Watchdog & Address Monitoring

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        Log.Debug("[NetworkManager] System NetworkAddressChanged event received");

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

            var currentIp = GetOutboundIp();
            if (currentIp == null)
            {
                Log.Debug("[NetworkManager] Address change ignored — no outbound route");
                return;
            }

            var previousIp = Volatile.Read(ref _lastOutboundIp);
            if (string.Equals(currentIp, previousIp, StringComparison.Ordinal))
            {
                Log.Debug($"[NetworkManager] Address change ignored — outbound IP unchanged ({currentIp})");
                return;
            }

            bool isTun = IsVpnTunAddress(currentIp);
            Volatile.Write(ref _isVpnActive, isTun);
            Volatile.Write(ref _lastOutboundIp, currentIp);

            RebuildAll($"Adapter route changed ({previousIp ?? "(none)"} → {currentIp}, VPN: {isTun})", force: false);
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
                await Task.Delay(TimeSpan.FromMinutes(2), _cts.Token).ConfigureAwait(false);

                var currentIp = GetOutboundIp();
                var previousIp = Volatile.Read(ref _lastOutboundIp);

                if (currentIp != null && previousIp != null && !string.Equals(currentIp, previousIp, StringComparison.Ordinal))
                {
                    Log.Info($"[NetworkManager] Watchdog detected unhandled IP change: {previousIp} → {currentIp}");
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

    private static string? GetOutboundIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsVpnTunAddress(string ip)
    {
        if (!IPAddress.TryParse(ip, out var addr))
            return false;

        var bytes = addr.GetAddressBytes();
        if (bytes.Length != 4) return false;

        // RFC 1918 — стандартные приватные диапазоны (LAN, Radmin VPN, OpenVPN client-side)
        if (bytes[0] == 10) return true;
        if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) return true;
        if (bytes[0] == 192 && bytes[1] == 168) return true;

        // Loopback
        if (bytes[0] == 127) return true;

        // RFC 6598 — CGNAT / shared address space (Tailscale, WireGuard, МГТС CGNAT)
        if (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) return true;

        // RFC 2544 — benchmark testing (часто назначается TUN-адаптерами)
        if (bytes[0] == 198 && bytes[1] is 18 or 19) return true;

        // RFC 5737 — documentation ranges (используются sing-box tun mode)
        if (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) return true;
        if (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) return true;

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