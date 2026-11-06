namespace LMP.Core.Youtube.Utils;

/// <summary>
/// Tracks the external IP to which YouTube binds issued stream URLs (the <c>ip</c> parameter).
/// </summary>
/// <remarks>
/// <para>
/// googlevideo returns HTTP 403 for URLs issued to a different external IP. Behind a fake-ip VPN (TUN 198.18.0.0/15),
/// the local address does not change when the node changes, so NetworkManager does not detect the change. The authoritative source is
/// the <c>ip</c> parameter of fresh URLs from the player API response: this is the address from which YouTube sees the client.
/// </para>
/// <para>
/// As long as there are no fresh URLs (cold startup), the binding is considered unknown and cached URLs are allowed.
/// URLs with <c>ipbits=0</c> are not bound to an address.
/// </para>
/// </remarks>
internal sealed class StreamUrlEgressGuard
{
    private string? _observedIp;

    /// <summary>
    /// Shared application instance.
    /// </summary>
    internal static StreamUrlEgressGuard Shared { get; } = new();

    /// <summary>
    /// External IP from the latest fresh URL; <c>null</c> if no fresh URLs have been received yet.
    /// </summary>
    internal string? ObservedIp => Volatile.Read(ref _observedIp);

    /// <summary>
    /// Captures the external IP from a URL just issued by the player API.
    /// </summary>
    /// <param name="freshUrl">Fresh videoplayback URL.</param>
    /// <returns><c>true</c> if a different IP was previously observed (VPN node or network switch).</returns>
    internal bool ObserveFreshUrl(string? freshUrl)
    {
        string? ip = TryGetBoundIp(freshUrl);
        if (ip is null)
            return false;

        string? previous = Interlocked.Exchange(ref _observedIp, ip);
        if (previous is null || string.Equals(previous, ip, StringComparison.OrdinalIgnoreCase))
            return false;

        Log.Warn($"[EgressGuard] YouTube-visible egress IP changed: {previous} -> {ip}. " +
                 "Cached stream URLs bound to the previous IP will be rejected.");
        return true;
    }

    /// <summary>
    /// Checks whether the URL was issued for the current external IP.
    /// </summary>
    /// <param name="url">Cached videoplayback URL.</param>
    /// <returns><c>false</c> only if both IPs are known and differ.</returns>
    internal bool IsBoundToCurrentEgress(string? url) => IsBoundTo(url, ObservedIp);

    /// <summary>
    /// Compares the URL binding IP with the observed external IP. An unknown side is not considered a mismatch.
    /// </summary>
    /// <param name="url">The videoplayback URL to check.</param>
    /// <param name="observedIp">The observed external IP; <c>null</c> if unknown.</param>
    /// <returns><c>false</c> only if both IPs are known and differ.</returns>
    internal static bool IsBoundTo(string? url, string? observedIp)
    {
        if (string.IsNullOrEmpty(observedIp))
            return true;

        string? ip = TryGetBoundIp(url);
        return ip is null || string.Equals(ip, observedIp, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts the binding IP (parameter <c>ip</c>) from a videoplayback URL.
    /// </summary>
    /// <param name="url">videoplayback URL.</param>
    /// <returns>The binding IP, or <c>null</c> if the URL is not bound to an address or the parameter is missing.</returns>
    internal static string? TryGetBoundIp(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return null;

        if (string.Equals(UrlEx.TryGetQueryParameterValue(url, "ipbits"), "0", StringComparison.Ordinal))
            return null;

        string? ip = UrlEx.TryGetQueryParameterValue(url, "ip");
        return string.IsNullOrWhiteSpace(ip) ? null : ip;
    }
}