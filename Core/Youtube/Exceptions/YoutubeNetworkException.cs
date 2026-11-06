using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;

namespace LMP.Core.Youtube.Exceptions;

/// <summary>
/// Specifies the type of network error encountered during YouTube API interaction.
/// </summary>
public enum NetworkErrorType
{
    /// <summary>Unclassified network error.</summary>
    Unknown = 0,

    /// <summary>HTTP connection or response timeout elapsed.</summary>
    Timeout,

    /// <summary>SSL/TLS handshake failure (often caused by DPI blocking).</summary>
    SslHandshakeFailure,

    /// <summary>DNS resolution failed or connection was refused.</summary>
    ConnectionFailed
}

/// <summary>
/// Thrown when a network error occurs while communicating with YouTube servers.
/// <para>
/// Separates network failures (timeouts, SSL/TLS, DNS) from content errors
/// (video unavailable, age restrictions) to display correct user guidance:
/// "check VPN/internet connection" instead of "video unavailable".
/// </para>
/// </summary>
public sealed class YoutubeNetworkException : YoutubeExplodeException
{
    /// <summary>
    /// Gets the network error type.
    /// </summary>
    public NetworkErrorType ErrorType { get; }

    /// <summary>
    /// <c>true</c> if the error was caused by an HTTP request timeout.
    /// </summary>
    public bool IsTimeout => ErrorType == NetworkErrorType.Timeout;

    /// <summary>
    /// <c>true</c> if the error was caused by an SSL/TLS handshake failure.
    /// A typical cause is DPI blocking or a broken proxy.
    /// </summary>
    public bool IsSslFailure => ErrorType == NetworkErrorType.SslHandshakeFailure;

    /// <summary>
    /// <c>true</c> if a TCP connection could not be established (DNS, connection refused, socket error).
    /// </summary>
    public bool IsConnectionFailed => ErrorType == NetworkErrorType.ConnectionFailed;

    /// <summary>
    /// Gets the localization key for this network error type.
    /// </summary>
    public string GetLocalizationKey() => ErrorType switch
    {
        NetworkErrorType.Timeout => "Error_Network_Timeout",
        NetworkErrorType.SslHandshakeFailure => "Error_SslHandshake_Failed",
        NetworkErrorType.ConnectionFailed => "Error_Network_ConnectionFailed",
        _ => "Error_Network_Generic"
    };

    /// <summary>
    /// Gets the localization key for the user recommendation.
    /// </summary>
    public string GetRecommendationKey() => ErrorType switch
    {
        NetworkErrorType.SslHandshakeFailure => "Recommendation_DpiBlocked",
        _ => "Recommendation_CheckNetwork"
    };

    /// <summary>
    /// Initializes a new instance of the YouTube network error.
    /// </summary>
    /// <param name="message">Human-readable error description.</param>
    /// <param name="errorType">Network error classification.</param>
    /// <param name="innerException">The original exception that caused the failure.</param>
    public YoutubeNetworkException(string message, NetworkErrorType errorType, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorType = errorType;
    }

    /// <summary>
    /// Attempts to classify an arbitrary exception as a YouTube network error.
    /// </summary>
    /// <param name="exception">The original exception.</param>
    /// <param name="cancellationToken">
    /// The caller's cancellation token. Used to distinguish user cancellation from HTTP timeout.
    /// </param>
    /// <returns>
    /// A <see cref="YoutubeNetworkException"/> with the appropriate <see cref="ErrorType"/>,
    /// or <c>null</c> if the exception is not a network error.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static YoutubeNetworkException? TryClassify(Exception exception, CancellationToken cancellationToken)
    {
        // 1. TaskCanceledException / TimeoutException from HttpClient.Timeout (not user-canceled)
        if (!cancellationToken.IsCancellationRequested &&
            (exception is TimeoutException ||
             exception is TaskCanceledException ||
             ContainsInChain<TimeoutException>(exception)))
        {
            return new YoutubeNetworkException(
                $"Connection timed out: {exception.Message}",
                NetworkErrorType.Timeout,
                exception);
        }

        // 2. OperationCanceledException from internal timeout (not user-canceled)
        if (exception is OperationCanceledException oce
            && !cancellationToken.IsCancellationRequested
            && !oce.CancellationToken.IsCancellationRequested)
        {
            return new YoutubeNetworkException(
                $"Operation timed out: {oce.Message}",
                NetworkErrorType.Timeout,
                oce);
        }

        // 3. HttpRequestException — inner chain analysis
        if (exception is HttpRequestException httpEx)
        {
            if (ContainsInChain<AuthenticationException>(httpEx))
            {
                return new YoutubeNetworkException(
                    $"SSL/TLS handshake failed: {httpEx.Message}",
                    NetworkErrorType.SslHandshakeFailure,
                    httpEx);
            }

            if (ContainsInChain<SocketException>(httpEx))
            {
                return new YoutubeNetworkException(
                    $"Connection failed: {httpEx.Message}",
                    NetworkErrorType.ConnectionFailed,
                    httpEx);
            }

            // Generic HttpRequestException without a specific inner exception — connection failed
            return new YoutubeNetworkException(
                $"Network error: {httpEx.Message}",
                NetworkErrorType.ConnectionFailed,
                httpEx);
        }

        // 4. IOException (broken pipe, connection reset)
        if (exception is IOException ioEx)
        {
            if (ContainsInChain<AuthenticationException>(ioEx))
            {
                return new YoutubeNetworkException(
                    $"SSL/TLS error: {ioEx.Message}",
                    NetworkErrorType.SslHandshakeFailure,
                    ioEx);
            }

            return new YoutubeNetworkException(
                $"I/O error: {ioEx.Message}",
                NetworkErrorType.ConnectionFailed,
                ioEx);
        }

        return null;
    }

    /// <summary>
    /// Checks whether the <see cref="Exception.InnerException"/> chain contains an exception of the specified type.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ContainsInChain<T>(Exception root) where T : Exception
    {
        var current = root.InnerException;
        int depth = 0;
        while (current is not null && depth < 10)
        {
            if (current is T)
                return true;
            current = current.InnerException;
            depth++;
        }
        return false;
    }
}