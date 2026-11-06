using System.Net;
using System.Net.Http.Headers;

namespace LMP.Core.Audio.Http;

/// <summary>
/// Checks the availability of the media path to a YouTube CDN host.
/// Compares <c>/generate_204</c> (health check) with <c>Range GET</c>
/// to <c>/videoplayback</c> (media path).
/// A discrepancy is a key indicator of TSPU DPI blocking.
/// </summary>
internal static class MediaPathProbe
{
    private const int DefaultTimeoutMs = 6_000;

    /// <summary>
    /// Result of checking a single CDN host via both paths.
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
        /// <summary>TSPU pattern: health check passes, media is dropped.</summary>
        internal bool IsTspuPattern => HealthCheckOk && !MediaOk;

        /// <summary>Full host block (both paths unavailable).</summary>
        internal bool IsFullBlock => !HealthCheckOk && !MediaOk;

        /// <summary>
        /// Host serves media data. This is the primary criterion.
        /// 204 may flap due to transient reasons — the media path takes precedence.
        /// </summary>
        internal bool IsMediaAvailable => MediaOk;

        /// <summary>Host is fully operational (both paths).</summary>
        internal bool IsFullyAvailable => HealthCheckOk && MediaOk;
    }

    /// <summary>
    /// Checks a CDN host via both paths: <c>/generate_204</c> and <c>Range GET</c> to the media URL.
    /// Runs both requests in parallel to minimize latency.
    /// </summary>
    internal static async Task<HostProbeResult> ProbeHostAsync(
        string mediaUrl,
        int timeoutMs = DefaultTimeoutMs,
        CancellationToken ct = default)
    {
        string host = ExtractHost(mediaUrl);

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
    /// <c>Range GET</c> to a media URL with valid YouTube session parameters.
    /// Uses the shared operational SharedHttpClient.Instance.
    /// </summary>
    private static async Task<(bool Ok, int Ms, int Status, long Bytes, string? Error)>
        ProbeMediaRangeAsync(string mediaUrl, int timeoutMs, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(Math.Max(timeoutMs, DefaultTimeoutMs));

            // Construct the URL with playback parameters (rn/rbuf) to prevent CDN rejection
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
    /// <c>GET /generate_204</c> — host availability check.
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
    /// Extracts the hostname from a media URL without unnecessary allocations on the hot path.
    /// </summary>
    internal static string ExtractHost(string url) => new Uri(url).Host;

    /// <summary>
    /// Resolves the hostname to an IPv4 address for diagnostic output.
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