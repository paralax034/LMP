using System.Net;
using Avalonia.Media;
using Avalonia.Threading;
using LMP.Core.Youtube.Utils;

namespace LMP.UI.Features.Settings.ViewModels;

public sealed partial class NetworkSettingsViewModel : ViewModelBase
{
    private readonly NetworkManager _networkManager;
    private readonly LibraryService _library;
    private DispatcherTimer? _proxyDebounceTimer;
    private bool _isLoading;

    public enum NetworkStatusKind
    {
        Unknown,
        Checking,
        Ok,
        NoInternet,
        VpnDetected,
        ProxyActive,
        Error
    }

    [ObservableProperty] public partial NetworkStatusKind NetworkStatus { get; private set; } = NetworkStatusKind.Unknown;

    public Color NetworkStatusColor => NetworkStatus switch
    {
        NetworkStatusKind.Ok => ThemeManagerService.GetThemeColor("Accent"),
        NetworkStatusKind.VpnDetected => ThemeManagerService.GetThemeColor("SystemInfoBlue"),
        NetworkStatusKind.ProxyActive => ThemeManagerService.GetThemeColor("Accent"),
        NetworkStatusKind.Checking => ThemeManagerService.GetThemeColor("SystemWarnOrange"),
        NetworkStatusKind.NoInternet => ThemeManagerService.GetThemeColor("SystemError"),
        NetworkStatusKind.Error => ThemeManagerService.GetThemeColor("SystemError"),
        _ => ThemeManagerService.GetThemeColor("TextSecondary"),
    };

    [ObservableProperty] public partial string NetworkStatusText { get; private set; } = "";
    [ObservableProperty] public partial int NetworkLatencyMs { get; private set; }
    public bool HasLatency => NetworkLatencyMs > 0;

    [ObservableProperty] public partial bool IsNetworkTesting { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<LocalizedItem<InternetProfile>> InternetProfileOptions { get; private set; } = [];
    [ObservableProperty] public partial LocalizedItem<InternetProfile>? SelectedInternetProfile { get; set; }

    [ObservableProperty] public partial bool ProxyEnabled { get; set; }
    [ObservableProperty] public partial string ProxyHost { get; set; } = "127.0.0.1";
    [ObservableProperty] public partial int ProxyPort { get; set; } = 8080;
    [ObservableProperty] public partial bool ProxyAuth { get; set; }
    [ObservableProperty] public partial string ProxyUser { get; set; } = "";
    [ObservableProperty] public partial string ProxyPass { get; set; } = "";

    public IAsyncRelayCommand TestNetworkCommand { get; }

    public NetworkSettingsViewModel(NetworkManager networkManager, LibraryService library)
    {
        _networkManager = networkManager;
        _library = library;

        TestNetworkCommand = new AsyncRelayCommand(TestNetworkAsync);

        RefreshLists();
        LoadSettings();
    }

    public void RefreshLists()
    {
        _isLoading = true;
        try
        {
            var currentProfile = SelectedInternetProfile?.Value ?? _library.Settings.InternetProfile;
            InternetProfileOptions = LocalizedItem.CreateList<InternetProfile>("NetProfile_");
            SelectedInternetProfile = InternetProfileOptions.FindByValue(currentProfile, 1);
        }
        finally
        {
            _isLoading = false;
        }
    }

    public void LoadSettings()
    {
        _isLoading = true;
        try
        {
            var s = _library.Settings;
            SelectedInternetProfile = InternetProfileOptions.FindByValue(s.InternetProfile, 1);

            ProxyEnabled = s.Proxy.Enabled;
            ProxyHost = string.IsNullOrWhiteSpace(s.Proxy.Host) ? "127.0.0.1" : s.Proxy.Host;
            ProxyPort = s.Proxy.Port > 0 ? s.Proxy.Port : 8080;
            ProxyAuth = s.Proxy.UseAuth;
            ProxyUser = s.Proxy.Username;
            ProxyPass = s.Proxy.Password;
        }
        finally
        {
            _isLoading = false;
        }
    }

    partial void OnSelectedInternetProfileChanged(LocalizedItem<InternetProfile>? value)
    {
        if (_isLoading || value is null) return;
        _library.UpdateSettings(s => s.InternetProfile = value.Value);
        _ = AudioEngine.ReinitializeWithProfileAsync(value.Value);
    }

    partial void OnProxyEnabledChanged(bool value) => OnProxyParamChanged();
    partial void OnProxyHostChanged(string value) => OnProxyParamChanged();
    partial void OnProxyPortChanged(int value) => OnProxyParamChanged();
    partial void OnProxyAuthChanged(bool value) => OnProxyParamChanged();
    partial void OnProxyUserChanged(string value) => OnProxyParamChanged();
    partial void OnProxyPassChanged(string value) => OnProxyParamChanged();

    private void OnProxyParamChanged()
    {
        if (_isLoading) return;

        _proxyDebounceTimer?.Stop();
        _proxyDebounceTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(400),
            DispatcherPriority.Normal,
            (_, _) =>
            {
                _proxyDebounceTimer?.Stop();
                SaveNetworkSettings();
            });
        _proxyDebounceTimer.Start();
    }

    private void SaveNetworkSettings()
    {
        var effectiveHost = string.IsNullOrWhiteSpace(ProxyHost) ? "127.0.0.1" : ProxyHost.Trim();
        var effectivePort = ProxyPort > 0 ? ProxyPort : 8080;

        var proxy = new ProxySettings
        {
            Enabled = ProxyEnabled,
            Host = effectiveHost,
            Port = effectivePort,
            UseAuth = ProxyAuth,
            Username = ProxyUser,
            Password = ProxyPass,
        };

        _library.UpdateSettings(s => s.Proxy = proxy);
        _networkManager.UpdateProxy(proxy);

        NetworkStatus = NetworkStatusKind.Unknown;
        NetworkStatusText = "";
        NetworkLatencyMs = 0;
        OnPropertyChanged(nameof(NetworkStatusColor));
        OnPropertyChanged(nameof(HasLatency));

        Log.Info($"[Settings] Network settings applied immediately. Proxy: {effectiveHost}:{effectivePort} (Enabled={ProxyEnabled})");
    }

    public async Task TestNetworkAsync()
    {
        if (IsNetworkTesting) return;

        IsNetworkTesting = true;
        NetworkStatus = NetworkStatusKind.Checking;
        NetworkStatusText = SL["Network_StatusChecking"];
        NetworkLatencyMs = 0;
        OnPropertyChanged(nameof(NetworkStatusColor));

        try
        {
            var isNetAvailable = await Task.Run(
                static () => System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
                .ConfigureAwait(true);

            if (!isNetAvailable)
            {
                SetNetworkStatus(NetworkStatusKind.NoInternet, SL["Network_StatusNoInternet"]);
                return;
            }

            var netInspection = await Task.Run(() =>
            {
                bool vpn = _networkManager.CheckIsVpnActive();
                var proxy = _networkManager.GetActiveProxyInfo();
                return (Vpn: vpn, Proxy: proxy);
            }).ConfigureAwait(true);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool reachable = await ProbeYoutubeAsync().ConfigureAwait(false);
            sw.Stop();

            if (!reachable)
            {
                SetNetworkStatus(NetworkStatusKind.Error, SL["Network_StatusError"]);
                return;
            }

            bool cdnReachable = await ProbeCdnAsync().ConfigureAwait(false);

            NetworkLatencyMs = (int)sw.ElapsedMilliseconds;

            var (proxyActive, proxyHost, proxyPort, isSystemProxy) = netInspection.Proxy;
            bool vpnDetected = netInspection.Vpn;

            Log.Info($"[Settings] Network test: {NetworkLatencyMs}ms (proxy={proxyActive} [{proxyHost}:{proxyPort}, system={isSystemProxy}], vpn={vpnDetected}, cdnReachable={cdnReachable})");

            if (proxyActive && !isSystemProxy)
            {
                SetNetworkStatus(
                    NetworkStatusKind.ProxyActive,
                    string.Format(SL["Network_StatusProxy"], proxyHost, proxyPort));
            }
            else if (vpnDetected)
            {
                SetNetworkStatus(NetworkStatusKind.VpnDetected, SL["Network_StatusVpn"]);
            }
            else if (proxyActive && isSystemProxy)
            {
                SetNetworkStatus(
                    NetworkStatusKind.ProxyActive,
                    string.Format(SL["Network_StatusProxy"], proxyHost, proxyPort));
            }
            else if (!cdnReachable)
            {
                SetNetworkStatus(
                    NetworkStatusKind.Error,
                    SL["Network_StatusCdnBlocked"]);
            }
            else
            {
                SetNetworkStatus(NetworkStatusKind.Ok, SL["Network_StatusOk"]);
            }
        }
        catch (Exception ex)
        {
            SetNetworkStatus(NetworkStatusKind.Error, ex.Message);
            Log.Warn($"[Settings] Error testing network: {ex.Message}");
        }
        finally
        {
            IsNetworkTesting = false;
        }
    }

    private async Task<bool> ProbeCdnAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var request = new HttpRequestMessage(
                HttpMethod.Get, "https://redirector.googlevideo.com/generate_204");

            request.Version = HttpVersion.Version11;
            request.Headers.TryAddWithoutValidation("User-Agent", YoutubeClientUtils.UaWebRemix);

            using var response = await _networkManager.ProbeClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            return (int)response.StatusCode is >= 200 and <= 399;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> ProbeYoutubeAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var request = new HttpRequestMessage(
                HttpMethod.Get, "https://music.youtube.com/generate_204");

            request.Version = HttpVersion.Version11;
            request.Headers.TryAddWithoutValidation("User-Agent", YoutubeClientUtils.UaWebRemix);

            using var response = await _networkManager.ProbeClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            return (int)response.StatusCode is >= 200 and <= 399;
        }
        catch
        {
            return false;
        }
    }

    private void SetNetworkStatus(NetworkStatusKind kind, string text)
    {
        NetworkStatus = kind;
        NetworkStatusText = text;
        OnPropertyChanged(nameof(NetworkStatusColor));
        OnPropertyChanged(nameof(HasLatency));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _proxyDebounceTimer?.Stop();
        }
        base.Dispose(disposing);
    }
}
