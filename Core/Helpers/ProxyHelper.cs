using System.Net;

namespace LMP.Core.Helpers;

/// <summary>
/// Helper for properly building WebProxy with HTTP, HTTPS, SOCKS4, and SOCKS5 support.
/// </summary>
internal static class ProxyHelper
{
    /// <summary>
    /// Creates a configured <see cref="IWebProxy"/> from application settings.
    /// Automatically detects the protocol scheme (socks5://, http://) in the Host field.
    /// </summary>
    /// <param name="proxy">Proxy settings.</param>
    /// <returns>A <see cref="WebProxy"/> instance, or <c>null</c> if the proxy is disabled or not configured.</returns>
    internal static WebProxy? CreateWebProxy(ProxySettings? proxy)
    {
        if (proxy is null || !proxy.Enabled || string.IsNullOrWhiteSpace(proxy.Host))
            return null;

        string hostInput = proxy.Host.Trim();
        int port = proxy.Port > 0 ? proxy.Port : 8080;
        Uri proxyUri;

        // If the user specified the scheme manually (socks5://, socks4://, http://, https://)
        if (hostInput.Contains("://", StringComparison.Ordinal))
        {
            if (Uri.TryCreate(hostInput, UriKind.Absolute, out var parsedUri))
            {
                var builder = new UriBuilder(parsedUri);
                if (builder.Port <= 0 || builder.Port == 80)
                    builder.Port = port;

                proxyUri = builder.Uri;
            }
            else
            {
                proxyUri = new Uri($"http://{hostInput}:{port}");
            }
        }
        else
        {
            // Heuristic: if the port is typical for SOCKS (1080, 10808, 10809, 9050), use socks5
            string scheme = port is 1080 or 10808 or 10809 or 9050 ? "socks5" : "http";
            proxyUri = new Uri($"{scheme}://{hostInput}:{port}");
        }

        var webProxy = new WebProxy(proxyUri);

        if (proxy.UseAuth && !string.IsNullOrWhiteSpace(proxy.Username))
        {
            webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);
        }

        return webProxy;
    }
}