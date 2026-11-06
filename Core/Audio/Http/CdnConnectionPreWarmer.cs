using System.Diagnostics;

namespace LMP.Core.Audio.Http;

/// <summary>
/// Speculative pre-warming of TCP+TLS connections to YouTube CDN nodes.
/// </summary>
internal static class CdnConnectionPreWarmer
{
    private const string GoogleVideoCdnSuffix = ".googlevideo.com";
    private const string GenerateEndpoint = "/generate_204";
    private const int MaxTrackedHosts = 4;
    private const int TunnelDeadTimeoutThreshold = 3;

    private static readonly TimeSpan WarmCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WarmTimeout = TimeSpan.FromMilliseconds(5000);
    private static readonly TimeSpan SpeculativeThrottle = TimeSpan.FromSeconds(30);

    private static DateTime _lastSpeculativeWarmTime = DateTime.MinValue;

    private static readonly Lock _lock = new();
    private static readonly LinkedList<(string Host, DateTime WarmTime)> _recentHosts = new();

    /// <summary>
    /// Invoked when N consecutive timeouts are detected for a single CDN host.
    /// AudioEngine subscribes and calls NotifyNetworkStarvation() proactively,
    /// before the buffer is exhausted.
    /// </summary>
    internal static event Action? OnTunnelDeadDetected;

    // Counter for consecutive timeouts per host.
    // ConcurrentDictionary: WarmHostCoreAsync is called from a fire-and-forget Task.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int>
        _consecutiveTimeouts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a CDN host after a successful HTTP response.
    /// </summary>
    public static void RecordHost(string url)
    {
        if (!TryExtractHost(url, out var host))
            return;

        CdnHostStatsStore.RecordHit(host);
        TrackRecentWarmHost(host);
    }

    private static void TrackRecentWarmHost(string host)
    {
        lock (_lock)
        {
            TrackRecentWarmHostMustHoldLock(host, DateTime.UtcNow);
        }
    }

    private static void TrackRecentWarmHostMustHoldLock(string host, DateTime nowUtc)
    {
        var node = _recentHosts.First;
        while (node != null)
        {
            var next = node.Next;
            if (string.Equals(node.Value.Host, host, StringComparison.OrdinalIgnoreCase))
                _recentHosts.Remove(node);
            node = next;
        }

        _recentHosts.AddFirst((host, nowUtc));

        while (_recentHosts.Count > MaxTrackedHosts)
            _recentHosts.RemoveLast();
    }

    /// <summary>
    /// Speculative pre-warming of connections to recent known CDN hosts.
    /// </summary>
    public static void PreWarmRecentHosts(HttpClient httpClient, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        string[] hostsToWarm;

        lock (_lock)
        {
            if (now - _lastSpeculativeWarmTime < SpeculativeThrottle)
                return;

            if (_recentHosts.Count == 0)
                return;

            _lastSpeculativeWarmTime = now;

            var buffer = new string[_recentHosts.Count];
            int count = 0;

            foreach (var (host, warmTime) in _recentHosts)
            {
                if (now - warmTime < WarmCooldown)
                    continue;

                buffer[count++] = host;
            }

            if (count == 0)
                return;

            hostsToWarm = buffer[..count];
        }

        for (int i = 0; i < hostsToWarm.Length; i++)
            _ = WarmHostCoreAsync(httpClient, hostsToWarm[i], ct);

        Log.Debug($"[CdnPreWarmer] Speculative warm-up fired for {hostsToWarm.Length} recent CDN host(s)");
    }

    /// <summary>
    /// Targeted connection pre-warming for a specific CDN host extracted from a URL.
    /// </summary>
    public static void PreWarmHost(HttpClient httpClient, string url, CancellationToken ct)
    {
        if (!TryExtractHost(url, out var host))
            return;

        var now = DateTime.UtcNow;

        lock (_lock)
        {
            foreach (var (trackedHost, warmTime) in _recentHosts)
            {
                if (string.Equals(trackedHost, host, StringComparison.OrdinalIgnoreCase)
                    && now - warmTime < WarmCooldown)
                {
                    Log.Debug($"[CdnPreWarmer] Host {TruncateHost(host)}... still warm or in-flight, skipping");
                    return;
                }
            }

            // Register host immediately BEFORE starting network request (in-flight dedup),
            // so concurrent calls in the same millisecond do not open duplicate TCP+TLS connections.
            TrackRecentWarmHostMustHoldLock(host, now);
        }

        _ = WarmHostCoreAsync(httpClient, host, ct);
        Log.Debug($"[CdnPreWarmer] Targeted warm-up fired for {TruncateHost(host)}...");
    }

    /// <summary>
    /// Performs TCP+TLS pre-warming of a CDN node via <c>GET /generate_204</c>.
    /// </summary>
    private static async Task WarmHostCoreAsync(HttpClient httpClient, string host, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(WarmTimeout);

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://{host}{GenerateEndpoint}");

            SharedHttpClient.ApplyUserAgentFromUrl(request, $"https://{host}/");

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            sw.Stop();

            _consecutiveTimeouts.TryRemove(host, out _);
            TrackRecentWarmHost(host);

            CdnHostStatsStore.RecordTtfb(host, sw.ElapsedMilliseconds);
            CdnHostStatsStore.FlushIfNeeded();

            Log.Debug($"[CdnPreWarmer] {TruncateHost(host)}... warm in {sw.ElapsedMilliseconds}ms (HTTP {(int)response.StatusCode})");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            sw.Stop();

            // Penalize cluster in storage so the dead node does not resurface on the next startup
            CdnHostStatsStore.RecordFailure(host);

            int count = _consecutiveTimeouts.AddOrUpdate(host, 1, (_, c) => c + 1);

            Log.Debug($"[CdnPreWarmer] {TruncateHost(host)}... timed out ({sw.ElapsedMilliseconds}ms) [consecutive timeouts: {count}/{TunnelDeadTimeoutThreshold}]");

            if (count >= TunnelDeadTimeoutThreshold)
            {
                Log.Warn($"[CdnPreWarmer] {TruncateHost(host)}... {TunnelDeadTimeoutThreshold} consecutive timeouts — tunnel likely dead, firing OnTunnelDeadDetected");

                AudioSourceFactory.CdnBlacklist.MarkBlocked(host);
                _consecutiveTimeouts.TryRemove(host, out _);
                OnTunnelDeadDetected?.Invoke();
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            CdnHostStatsStore.RecordFailure(host);
            Log.Debug($"[CdnPreWarmer] {TruncateHost(host)}... failed ({sw.ElapsedMilliseconds}ms): {ex.Message}");
        }
    }

    /// <summary>
    /// Pre-warms a single specific host. Used by <see cref="CdnHostStatsStore.PreWarmTopClustersAsync"/>.
    /// </summary>
    internal static Task WarmSingleHostAsync(HttpClient httpClient, string host, CancellationToken ct)
        => WarmHostCoreAsync(httpClient, host, ct);

    /// <summary>
    /// Extracts the hostname from a URL if it belongs to YouTube CDN.
    /// </summary>
    private static bool TryExtractHost(string url, out string host)
    {
        host = string.Empty;

        if (string.IsNullOrEmpty(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (!uri.Host.EndsWith(GoogleVideoCdnSuffix, StringComparison.OrdinalIgnoreCase))
            return false;

        host = uri.Host;
        return true;
    }

    /// <summary>
    /// Returns a truncated hostname for logging output.
    /// </summary>
    /// <param name="host">CDN hostname.</param>
    /// <param name="maxLength">Maximum output length (default is 40).</param>
    /// <returns>Hostname truncated to <paramref name="maxLength"/> characters.</returns>
    private static string TruncateHost(string host, int maxLength = 40) =>
        host.Length <= maxLength ? host : host[..maxLength];
}