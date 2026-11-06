using System.Runtime.CompilerServices;
using LMP.Core.Youtube.Exceptions;

namespace LMP.Core.Youtube.Utils;

/// <summary>
/// Centralized request executor with Retry Policy support for enhanced network resilience.
/// </summary>
internal static class ResilienceExecutor
{
    /// <summary>
    /// Executes an asynchronous operation with automatic retries on network errors or parsing failures.
    /// </summary>
    /// <typeparam name="T">The return value type.</typeparam>
    /// <param name="operation">The network operation delegate to execute.</param>
    /// <param name="maxRetries">Maximum number of retry attempts.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static async ValueTask<T> ExecuteWithRetryAsync<T>(
        Func<ValueTask<T>> operation,
        int maxRetries,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt <= maxRetries && (ex is HttpRequestException or IOException or YoutubeExplodeException))
            {
                Log.Warn($"[ResilienceExecutor] Attempt {attempt} failed: {ex.Message}. Retrying...");
                if (!await cancellationToken.DelayNoThrowAsync(150 * attempt, continueOnCapturedContext: false).ConfigureAwait(false))
                    cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }
}