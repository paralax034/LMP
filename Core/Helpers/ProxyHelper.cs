using System.Net;
using LMP.Core.Models;

namespace LMP.Core.Helpers;

/// <summary>
/// Хелпер для корректной сборки WebProxy с поддержкой HTTP, HTTPS, SOCKS4 и SOCKS5.
/// </summary>
internal static class ProxyHelper
{
    /// <summary>
    /// Создаёт сконфигурированный <see cref="IWebProxy"/> из настроек приложения.
    /// Автоматически распознаёт схему протокола (socks5://, http://) в поле Host.
    /// </summary>
    /// <param name="proxy">Настройки прокси.</param>
    /// <returns>Экземпляр <see cref="WebProxy"/> или <c>null</c>, если прокси выключен или не настроен.</returns>
    internal static WebProxy? CreateWebProxy(ProxySettings? proxy)
    {
        if (proxy is null || !proxy.Enabled || string.IsNullOrWhiteSpace(proxy.Host))
            return null;

        string hostInput = proxy.Host.Trim();
        int port = proxy.Port > 0 ? proxy.Port : 8080;
        Uri proxyUri;

        // Если пользователь указал схему вручную (socks5://, socks4://, http://, https://)
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
            // Эвристика: если порт типичен для SOCKS (1080, 10808, 10809, 9050), используем socks5
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