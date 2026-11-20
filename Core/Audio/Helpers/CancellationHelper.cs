using LMP.Core.Exceptions;
using LMP.Core.Youtube.Exceptions;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// Single source of truth for classifying explicit user/token-initiated cancellations
/// without misclassifying network timeouts or fatal chunk download errors.
/// </summary>
public static class CancellationHelper
{
    /// <summary>
    /// Determines whether the exception represents an explicit user/token-initiated cancellation
    /// rather than a network timeout (such as <see cref="HttpClient"/> timeout), transport I/O error,
    /// or fatal stream error.
    /// </summary>
    /// <param name="exception">The exception to check. <c>null</c> returns <c>false</c>.</param>
    /// <returns><c>true</c> if a token-backed cancellation is detected; otherwise <c>false</c>.</returns>
    public static bool IsUserCancellation(Exception? exception)
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
    /// Compatibility alias for <see cref="IsUserCancellation"/>.
    /// </summary>
    /// <param name="exception">The exception to check. <c>null</c> returns <c>false</c>.</param>
    /// <returns><c>true</c> if a token-backed cancellation is detected; otherwise <c>false</c>.</returns>
    public static bool IsCancellationLike(Exception? exception) =>
        IsUserCancellation(exception);

    /// <summary>
    /// Determines whether the caller token is canceled or the exception represents an explicit user cancellation.
    /// </summary>
    /// <param name="exception">The exception to check.</param>
    /// <param name="ct">The cancellation token for additional checking.</param>
    /// <returns><c>true</c> if <paramref name="ct"/> is canceled or <paramref name="exception"/> is a user cancellation.</returns>
    public static bool IsCancellationOrTokenCancelled(Exception? exception, CancellationToken ct) =>
        ct.IsCancellationRequested || IsUserCancellation(exception);
}