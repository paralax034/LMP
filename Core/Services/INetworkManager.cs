namespace LMP.Core.Services;

/// <summary>
/// Единый интерфейс управления сетевым слоем приложения LMP.
/// Предоставляет изолированные пулы HttpClient, отслеживает изменения сетевых интерфейсов
/// и управляет маршрутизацией через системный/пользовательский прокси и DoH.
/// </summary>
public interface INetworkManager : IDisposable
{
    /// <summary>HTTP-клиент для потоковой загрузки PCM аудиоданных (CDN, HTTP/1.1, DoH fallback).</summary>
    HttpClient AudioClient { get; }

    /// <summary>HTTP-клиент для вызовов YouTube InnerTube API (HTTP/2, KeepAlive ping).</summary>
    HttpClient ApiClient { get; }

    /// <summary>HTTP-клиент для загрузки обложек и изображений (HTTP/2, кэширующий пул).</summary>
    HttpClient ImageClient { get; }

    /// <summary>HTTP-клиент для быстрых диагностических запросов и health-check зондов.</summary>
    HttpClient ProbeClient { get; }

    /// <summary>Текущие активные настройки прокси-сервера.</summary>
    ProxySettings? CurrentProxy { get; }

    /// <summary>Текущий профиль сетевого подключения.</summary>
    InternetProfile CurrentProfile { get; }

    /// <summary>Последний зафиксированный исходящий IPv4-адрес.</summary>
    string? OutboundIp { get; }

    /// <summary>Признак наличия активного VPN/TUN интерфейса.</summary>
    bool IsVpnActive { get; }

    /// <summary>Событие, возникающее при пересборке сетевых клиентов.</summary>
    event Action? NetworkRebuilt;

    /// <summary>
    /// Выполняет полную пересборку всех внутренних HTTP-клиентов с плавным выводом старых из эксплуатации.
    /// </summary>
    /// <param name="reason">Причина пересборки (для аудита и логов).</param>
    /// <param name="force">Игнорировать ли кулдаун (true при физической смене IP/VPN).</param>
    void RebuildAll(string reason, bool force = false);

    /// <summary>Обновляет конфигурацию прокси-сервера и инициирует пересборку пулов.</summary>
    void UpdateProxy(ProxySettings? proxy);

    /// <summary>Обновляет сетевой профиль скорости.</summary>
    void UpdateProfile(InternetProfile profile);
}