namespace LMP.Core.Youtube.Utils;

/// <summary>
/// Отслеживает внешний IP, к которому YouTube привязывает выданные stream URL (параметр <c>ip</c>).
/// </summary>
/// <remarks>
/// <para>
/// googlevideo отвечает HTTP 403 на URL, выданный для другого внешнего IP. За VPN в режиме fake-ip (TUN 198.18.0.0/15)
/// локальный адрес при смене узла не меняется, поэтому NetworkManager такую смену не видит. Достоверный источник —
/// параметр <c>ip</c> свежих URL из ответа player API: это адрес, с которого YouTube видит клиента.
/// </para>
/// <para>
/// Пока свежих URL не было (холодный старт), привязка считается неизвестной и кэшированные URL допускаются.
/// URL с <c>ipbits=0</c> к адресу не привязаны.
/// </para>
/// </remarks>
internal sealed class StreamUrlEgressGuard
{
    private string? _observedIp;

    /// <summary>
    /// Общий экземпляр приложения.
    /// </summary>
    internal static StreamUrlEgressGuard Shared { get; } = new();

    /// <summary>
    /// Внешний IP из последнего свежего URL; <c>null</c>, если свежих URL ещё не было.
    /// </summary>
    internal string? ObservedIp => Volatile.Read(ref _observedIp);

    /// <summary>
    /// Фиксирует внешний IP из URL, только что выданного player API.
    /// </summary>
    /// <param name="freshUrl">Свежий videoplayback URL.</param>
    /// <returns><c>true</c>, если ранее наблюдался другой IP (смена узла VPN или сети).</returns>
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
    /// Проверяет, выдан ли URL для текущего внешнего IP.
    /// </summary>
    /// <param name="url">Кэшированный videoplayback URL.</param>
    /// <returns><c>false</c> только если оба IP известны и различаются.</returns>
    internal bool IsBoundToCurrentEgress(string? url) => IsBoundTo(url, ObservedIp);

    /// <summary>
    /// Сравнивает IP привязки URL с наблюдаемым внешним IP. Неизвестная сторона несовпадением не считается.
    /// </summary>
    /// <param name="url">Проверяемый videoplayback URL.</param>
    /// <param name="observedIp">Наблюдаемый внешний IP; <c>null</c> — неизвестен.</param>
    /// <returns><c>false</c> только если оба IP известны и различаются.</returns>
    internal static bool IsBoundTo(string? url, string? observedIp)
    {
        if (string.IsNullOrEmpty(observedIp))
            return true;

        string? ip = TryGetBoundIp(url);
        return ip is null || string.Equals(ip, observedIp, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Извлекает IP привязки (параметр <c>ip</c>) из videoplayback URL.
    /// </summary>
    /// <param name="url">videoplayback URL.</param>
    /// <returns>IP привязки либо <c>null</c>, если URL к адресу не привязан или параметра нет.</returns>
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