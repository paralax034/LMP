using System.Runtime.CompilerServices;
using LMP.Core.Youtube.Exceptions;

namespace LMP.Core.Youtube.Utils;

/// <summary>
/// Централизованный исполнитель запросов с поддержкой повторных попыток (Retry Policy) для повышения сетевой устойчивости.
/// </summary>
internal static class ResilienceExecutor
{
    /// <summary>
    /// Выполняет асинхронную операцию с автоматическим перезапуском при возникновении сетевых ошибок или сбоев парсинга.
    /// </summary>
    /// <typeparam name="T">Тип возвращаемого значения.</typeparam>
    /// <param name="operation">Делегат выполняемой сетевой операции.</param>
    /// <param name="maxRetries">Максимальное количество повторных попыток.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
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