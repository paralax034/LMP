using System.Security.Authentication;
using LMP.Core.Exceptions;
using LMP.Core.Youtube.Exceptions;

namespace LMP.Core.Helpers;

/// <summary>
/// Helper for analyzing and classifying network and cancellation exceptions.
/// Shared between audio pipelines, HTTP handlers, and UI error orchestrators.
/// </summary>
public static class NetworkErrorHelper
{
    /// <summary>
    /// Walks the exception chain to the innermost cause and returns a compact,
    /// log-friendly reason string including <see cref="System.Net.Sockets.SocketError"/>
    /// and HTTP status when available. Intended for DPI/RST vs timeout disambiguation.
    /// </summary>
    /// <param name="exception">Exception to inspect.</param>
    /// <returns>Compact reason, e.g. <c>socket/ConnectionReset</c> or <c>http/403</c>.</returns>
    public static string DescribeTransportFailure(Exception? exception)
    {
        if (exception is null)
            return "none";

        System.Net.Sockets.SocketException? socketEx = null;
        HttpRequestException? httpEx = null;
        IOException? ioEx = null;

        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case System.Net.Sockets.SocketException se:
                    socketEx = se;
                    break;
                case HttpRequestException he:
                    httpEx ??= he;
                    break;
                case IOException io:
                    ioEx ??= io;
                    break;
            }
        }

        if (socketEx is not null)
            return $"socket/{socketEx.SocketErrorCode} ({(int)socketEx.SocketErrorCode})";

        if (httpEx is { StatusCode: { } status })
            return $"http/{(int)status}";

        if (ioEx is not null)
            return $"io/{Truncate(ioEx.Message, 80)}";

        return $"{exception.GetType().Name}/{Truncate(exception.Message, 80)}";

        static string Truncate(string s, int max)
            => s.Length <= max ? s : s[..max];
    }

    /// <summary>
    /// Checks if the exception is a pure user-initiated cancellation.
    /// <para>
    /// Returns <c>false</c> for HTTP timeouts (<see cref="TaskCanceledException"/>
    /// with <c>CancellationToken.IsCancellationRequested == false</c>),
    /// network I/O errors, and <see cref="YoutubeNetworkException"/>.
    /// </para>
    /// </summary>
    /// <param name="exception">Exception to evaluate.</param>
    /// <returns><c>true</c> if it is a pure user cancellation; <c>false</c> if it is a network/timeout error.</returns>
    public static bool IsCancellationLike(Exception? exception)
    {
        if (exception is null)
            return false;

        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is ChunkDownloadFatalException or YoutubeNetworkException)
                return false;

            // An explicit token-backed OperationCanceledException/TaskCanceledException
            // takes precedence over any inner IOException/SocketException produced when
            // aborting an in-flight socket or SSL read.
            if (current is OperationCanceledException oce)
                return oce.CancellationToken.IsCancellationRequested;

            if (current is TimeoutException or
                IOException or
                System.Net.Sockets.SocketException or
                System.Net.WebException or
                HttpRequestException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if the exception is caused by an SSL/TLS handshake failure or a connection reset
    /// (common in DPI/firewall blocking).
    /// </summary>
    /// <param name="exception">Exception to evaluate.</param>
    /// <returns><c>true</c> if DPI/handshake/timeout block is detected; otherwise <c>false</c>.</returns>
    public static bool IsSslOrTlsHandshakeFailure(Exception? exception)
    {
        if (exception is null)
            return false;

        if (exception is YoutubeNetworkException { IsSslFailure: true })
            return true;

        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is YoutubeNetworkException { IsSslFailure: true })
                return true;

            if (current is AuthenticationException or TimeoutException)
                return true;

            if (current is AggregateException agg)
            {
                var inners = agg.InnerExceptions;
                for (int i = 0; i < inners.Count; i++)
                {
                    if (IsSslOrTlsHandshakeFailure(inners[i]))
                        return true;
                }
            }

            if (current is IOException ioEx)
            {
                var msg = ioEx.Message;
                if (msg.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("handshake failed", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("closed the transport stream", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("unexpected packet format", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("decryption operation failed", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("Connection reset", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("Connection timed out", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("Response ended prematurely", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("EnsureFullTlsFrame", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            if (current is System.Net.Sockets.SocketException sockEx)
            {
                if (sockEx.SocketErrorCode is
                    System.Net.Sockets.SocketError.ConnectionReset or
                    System.Net.Sockets.SocketError.TimedOut or
                    System.Net.Sockets.SocketError.ConnectionAborted or
                    System.Net.Sockets.SocketError.ConnectionRefused)
                {
                    return true;
                }
            }

            if (current is System.Net.WebException webEx &&
                webEx.Status == System.Net.WebExceptionStatus.SecureChannelFailure)
            {
                return true;
            }

            var rawMsg = current.Message;
            if (!string.IsNullOrEmpty(rawMsg))
            {
                var span = rawMsg.AsSpan();
                if (span.Contains("SSL".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                    span.Contains("TLS".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                    span.Contains("secure channel".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                    span.Contains("authentication".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                    span.Contains("EnsureFullTlsFrame".AsSpan(), StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}