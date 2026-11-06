using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace LMP.Core.Audio.Http;

/// <summary>
/// DNS-over-HTTPS fallback resolver for YouTube domains
/// blocked by ISP or Zapret at the UDP DNS level.
/// </summary>
/// <remarks>
/// <para>
/// Used from <see cref="SharedHttpClient.ConnectWithKeepAliveAsync"/>
/// on <see cref="SocketError.HostNotFound"/> for known YouTube domains.
/// </para>
/// <para>
/// DoH uses HTTPS/443 — ISP cannot block it without blocking
/// all of Google, and Zapret does not intercept HTTPS traffic.
/// </para>
/// <para>
/// Caching: positive entries — 5 minutes, negative — 30 seconds.
/// Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </para>
/// </remarks>
internal static class DohResolver
{
    /// <summary>Domains to which DoH fallback is applied.</summary>
    private static readonly string[] FallbackDomains =
    [
        "youtube.com",
        "music.youtube.com",
        "www.youtube.com",
        "googleapis.com",
        "jnn-pa.googleapis.com",
        "googlevideo.com",
    ];

    private const long PositiveTtlTicks = 5 * 60 * TimeSpan.TicksPerSecond; // 5 minutes
    private const long NegativeTtlTicks = 30 * TimeSpan.TicksPerSecond; // 30 seconds
    private const int DohTimeoutMs = 4000;

    /// <summary>
    /// Fallback DoH endpoints when Google DoH is unavailable.
    /// </summary>
    private static readonly string[] FallbackEndpoints =
    [
        "https://dns.google/resolve",
        "https://cloudflare-dns.com/dns-query",
    ];

    private static readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Dedicated HttpClient for DoH requests.
    /// Does not use <see cref="SharedHttpClient"/> — otherwise recursion in ConnectCallback.
    /// Does not use a custom ConnectCallback — standard DNS resolution for dns.google works.
    /// </summary>
    private static readonly HttpClient _dohClient = CreateDohClient();

    private readonly record struct CacheEntry(IPAddress[] Addresses, long ExpiresAtTicks);

    /// <summary>
    /// Checks whether the domain is a candidate for DoH fallback.
    /// </summary>
    internal static bool IsFallbackDomain(string host)
    {
        for (int i = 0; i < FallbackDomains.Length; i++)
        {
            if (host.EndsWith(FallbackDomains[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves the domain via DNS-over-HTTPS with caching.
    /// Returns <c>null</c> on failure.
    /// </summary>
    internal static async ValueTask<IPAddress[]?> ResolveAsync(
        string host,
        CancellationToken ct = default)
    {
        // Check cache
        if (_cache.TryGetValue(host, out var cached))
        {
            if (DateTime.UtcNow.Ticks < cached.ExpiresAtTicks)
            {
                return cached.Addresses.Length > 0 ? cached.Addresses : null;
            }

            _cache.TryRemove(host, out _);
        }

        // DoH request
        for (int i = 0; i < FallbackEndpoints.Length; i++)
        {
            var addresses = await TryDohQueryAsync(
                FallbackEndpoints[i], host, ct).ConfigureAwait(false);

            if (addresses is { Length: > 0 })
            {
                _cache[host] = new CacheEntry(addresses,
                    DateTime.UtcNow.Ticks + PositiveTtlTicks);

                Log.Info($"[DoH] Resolved {host} → {addresses[0]} " +
                         $"({addresses.Length} address(es)) via {GetEndpointName(FallbackEndpoints[i])}");
                return addresses;
            }
        }

        // All DoH endpoints failed — cache the negative result
        _cache[host] = new CacheEntry([], DateTime.UtcNow.Ticks + NegativeTtlTicks);
        Log.Warn($"[DoH] Failed to resolve {host} via all DoH endpoints");
        return null;
    }

    /// <summary>
    /// Invalidates the cache. Called when changing networks.
    /// </summary>
    internal static void InvalidateCache()
    {
        int count = _cache.Count;
        _cache.Clear();
        if (count > 0)
            Log.Debug($"[DoH] Cache cleared ({count} entries)");
    }

    private static async Task<IPAddress[]?> TryDohQueryAsync(
        string endpoint,
        string host,
        CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(DohTimeoutMs);

            // Google DNS: JSON API — easier to parse, follows CNAME automatically
            // Cloudflare: same format with Accept: application/dns-json
            string url = $"{endpoint}?name={Uri.EscapeDataString(host)}&type=A";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/dns-json"));

            using var response = await _dohClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return null;

            using var stream = await response.Content.ReadAsStreamAsync(cts.Token)
                .ConfigureAwait(false);

            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token)
                .ConfigureAwait(false);

            var root = doc.RootElement;

            // Check Status (0 = NOERROR)
            if (root.TryGetProperty("Status", out var status) && status.GetInt32() != 0)
                return null;

            if (!root.TryGetProperty("Answer", out var answers))
                return null;

            var result = new List<IPAddress>(4);

            foreach (var answer in answers.EnumerateArray())
            {
                // type=1 → A record (IPv4)
                if (!answer.TryGetProperty("type", out var type) || type.GetInt32() != 1)
                    continue;

                if (!answer.TryGetProperty("data", out var data))
                    continue;

                if (IPAddress.TryParse(data.GetString(), out var addr))
                    result.Add(addr);
            }

            return result.Count > 0 ? result.ToArray() : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Debug($"[DoH] Timeout querying {GetEndpointName(endpoint)} for {host}");
            return null;
        }
        catch (Exception ex)
        {
            Log.Debug($"[DoH] Error querying {GetEndpointName(endpoint)} for {host}: {ex.Message}");
            return null;
        }
    }

    private static HttpClient CreateDohClient()
    {
        // Cannot use SharedHttpClient.ConnectWithKeepAliveAsync — recursion.
        // Standard SocketsHttpHandler with system DNS.
        // dns.google (8.8.8.8) and cloudflare-dns.com (1.1.1.1) are resolved
        // via DNS records, not YouTube — ISP does not block them.
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(4),
        };

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5), };
    }

    private static string GetEndpointName(string endpoint) => endpoint switch
    {
        _ when endpoint.Contains("google") => "Google",
        _ when endpoint.Contains("cloudflare") => "Cloudflare",
        _ => endpoint
    };
}
