namespace LMP.Core.Audio.Helpers;

/// <summary>
/// Single source of truth for detecting operation cancellations.
/// Replaces three redundant implementations in AudioEngine, AudioPipeline, and PlaybackErrorOrchestrator.
/// </summary>
public static class CancellationHelper
{
    /// <summary>
    /// Determines whether the exception (or any inner exception) represents an operation cancellation.
    /// </summary>
    /// <param name="exception">The exception to check. null → false.</param>
    /// <returns>true if an <see cref="OperationCanceledException"/> or <see cref="TaskCanceledException"/> is found.</returns>
    public static bool IsCancellationLike(Exception? exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is OperationCanceledException or TaskCanceledException)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Determines whether the exception represents a cancellation or the token is already canceled.
    /// </summary>
    /// <param name="exception">The exception to check.</param>
    /// <param name="ct">The cancellation token for additional checking.</param>
    /// <returns>true if the token is canceled or the exception contains a cancellation.</returns>
    public static bool IsCancellationOrTokenCancelled(Exception? exception, CancellationToken ct) =>
        ct.IsCancellationRequested || IsCancellationLike(exception);
}