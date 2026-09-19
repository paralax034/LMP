using System.Net;
using System.Net.Http.Headers;

namespace LMP.Core.Audio.Http;

/// <summary>
/// Проверка доступности медиа-пути к YouTube CDN-хосту.
/// Сравнивает <c>/generate_204</c> (health check) с <c>Range GET</c>
/// к <c>/videoplayback</c> (media path).
/// Расхождение — ключевой индикатор DPI-блокировки ТСПУ.
/// </summary>
internal static class MediaPathProbe
{
    private const int DefaultTimeoutMs = 6_000;

    /// <summary>
    /// Результат проверки одного CDN-хоста по двум путям.
    /// </summary>
    internal readonly record struct HostProbeResult(
        string Host,
        bool HealthCheckOk,
        int HealthCheckMs,
        bool MediaOk,
        int MediaMs,
        int MediaHttpStatus,
        long MediaBytesReceived,
        string? MediaError)
    {
        /// <summary>Паттерн ТСПУ: health check проходит, media дропается.</summary>
        internal bool IsTspuPattern => HealthCheckOk && !MediaOk;

        /// <summary>Полная блокировка хоста (оба пути недоступны).</summary>
        internal bool IsFullBlock => !HealthCheckOk && !MediaOk;

        /// <summary>
        /// Хост отдаёт media-данные. Это главный критерий.
        /// 204 может флапнуть по транзиентным причинам — media path важнее.
        /// </summary>
        internal bool IsMediaAvailable => MediaOk;

        /// <summary>Хост полностью рабочий (оба пути).</summary>
        internal bool IsFullyAvailable => HealthCheckOk && MediaOk;
    }

    /// <summary>
    /// Проверяет CDN-хост по обоим путям: <c>/generate_204</c> и <c>Range GET</c> к media URL.
    /// Запускает оба запроса параллельно для минимизации латентности.
    /// </summary>
    internal static async Task<HostProbeResult> ProbeHostAsync(
        string mediaUrl,
        int timeoutMs = DefaultTimeoutMs,
        CancellationToken ct = default)
    {
        var uri = new Uri(mediaUrl);
        string host = uri.Host;

        var healthTask = Probe204Async(host, mediaUrl, timeoutMs, ct);
        var mediaTask = ProbeMediaRangeAsync(mediaUrl, timeoutMs, ct);

        await Task.WhenAll(healthTask, mediaTask).ConfigureAwait(false);

        var (healthOk, healthMs) = await healthTask.ConfigureAwait(false);
        var (mediaOk, mediaMs, mediaStatus, mediaBytes, mediaError) = await mediaTask.ConfigureAwait(false);

        return new HostProbeResult(
            host,
            healthOk, healthMs,
            mediaOk, mediaMs, mediaStatus, mediaBytes, mediaError);
    }

    /// <summary>
    /// Быстрая проверка: может ли хост отдать медиа-данные (<c>Range: bytes=0-1</c>).
    /// Используется для проактивного probe перед <c>InitializeAsync</c>.
    /// </summary>
    internal static async Task<bool> IsMediaAvailableAsync(
        string mediaUrl,
        int timeoutMs = DefaultTimeoutMs,
        CancellationToken ct = default)
    {
        var (Ok, _, _, Bytes, _) = await ProbeMediaRangeAsync(mediaUrl, timeoutMs, ct)
            .ConfigureAwait(false);
        return Ok && Bytes > 0;
    }

    /// <summary>
    /// <c>Range GET</c> к media URL с корректными параметрами сессии YouTube.
    /// Использует общий рабочий SharedHttpClient.Instance.
    /// </summary>
    private static async Task<(bool Ok, int Ms, int Status, long Bytes, string? Error)>
        ProbeMediaRangeAsync(string mediaUrl, int timeoutMs, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(Math.Max(timeoutMs, DefaultTimeoutMs));

            // Формируем URL с параметрами воспроизведения (rn/rbuf), чтобы CDN не отклонял запрос
            string probeUrl = mediaUrl;
            if (mediaUrl.Contains("googlevideo.com/videoplayback", StringComparison.Ordinal))
            {
                probeUrl = UrlEx.SetQueryParameter(probeUrl, "rn", "1");
                probeUrl = UrlEx.SetQueryParameter(probeUrl, "rbuf", "0");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, probeUrl);
            request.Headers.Range = new RangeHeaderValue(0, 1023);
            request.Version = HttpVersion.Version11;
            SharedHttpClient.ApplyUserAgentFromUrl(request, probeUrl);

            using var response = await SharedHttpClient.Instance
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, probeCts.Token)
                .ConfigureAwait(false);

            int status = (int)response.StatusCode;
            long bytes = 0;

            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.PartialContent)
            {
                var content = await response.Content.ReadAsByteArrayAsync(probeCts.Token).ConfigureAwait(false);
                bytes = content.Length;
            }

            sw.Stop();
            bool ok = (status is 200 or 206) && bytes > 0;
            return (ok, (int)sw.ElapsedMilliseconds, status, bytes,
                ok ? null : $"HTTP {status}, {bytes} bytes");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, (int)sw.ElapsedMilliseconds, 0, 0, "Timeout (Network congested or TSPU drop)");
        }
        catch (HttpRequestException ex)
        {
            return (false, (int)sw.ElapsedMilliseconds, 0, 0, $"HTTP: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            return (false, (int)sw.ElapsedMilliseconds, 0, 0, "Cancelled");
        }
        catch (Exception ex)
        {
            return (false, (int)sw.ElapsedMilliseconds, 0, 0, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// <c>GET /generate_204</c> — проверка доступности хоста.
    /// </summary>
    private static async Task<(bool Ok, int Ms)> Probe204Async(
        string host, string referenceUrl, int timeoutMs, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(Math.Max(timeoutMs, 3000));

            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/generate_204");
            request.Version = HttpVersion.Version11;
            SharedHttpClient.ApplyUserAgentFromUrl(request, referenceUrl);

            using var response = await SharedHttpClient.Instance
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, probeCts.Token)
                .ConfigureAwait(false);

            sw.Stop();
            bool ok = response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK;
            return (ok, (int)sw.ElapsedMilliseconds);
        }
        catch
        {
            return (false, (int)sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Извлекает hostname из media URL без лишних аллокаций на hot path.
    /// </summary>
    internal static string ExtractHost(string url) => new Uri(url).Host;

    /// <summary>
    /// Резолвит hostname в IPv4 для диагностического вывода.
    /// </summary>
    internal static string ResolveToIp(string host)
    {
        try
        {
            var addresses = Dns.GetHostAddresses(host, System.Net.Sockets.AddressFamily.InterNetwork);
            return addresses.Length > 0 ? addresses[0].ToString() : "(unresolved)";
        }
        catch
        {
            return "(DNS error)";
        }
    }
}