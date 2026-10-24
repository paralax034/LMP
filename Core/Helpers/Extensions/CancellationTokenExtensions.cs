using System.Runtime.CompilerServices;

namespace LMP.Core.Helpers.Extensions;

/// <summary>
/// Высокопроизводительные безысключительные методы расширения для <see cref="CancellationToken"/>.
/// </summary>
public static class CancellationTokenExtensions
{
    /// <summary>
    /// Ожидает задержку без генерации <see cref="OperationCanceledException"/>.
    /// Возвращает <see langword="false"/> при отмене и <see langword="true"/> по истечении времени.
    /// </summary>
    private static readonly Task<bool> TrueTask = Task.FromResult(true);
    private static readonly Task<bool> FalseTask = Task.FromResult(false);

    /// <summary>
    /// Ожидает задержку без генерации <see cref="OperationCanceledException"/>.
    /// Возвращает <see langword="false"/> при отмене и <see langword="true"/> по истечении времени.
    /// Синхронные ветки (уже отменённый токен или нулевая задержка) не аллоцируют стейт-машину (0 B).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Task<bool> DelayNoThrowAsync(
        this CancellationToken token,
        int millisecondsDelay,
        bool continueOnCapturedContext = true)
    {
        if (token.IsCancellationRequested) return FalseTask;
        if (millisecondsDelay <= 0) return TrueTask;

        return DelayNoThrowCoreAsync(token, millisecondsDelay, continueOnCapturedContext);
    }

    private static async Task<bool> DelayNoThrowCoreAsync(
        CancellationToken token,
        int millisecondsDelay,
        bool continueOnCapturedContext)
    {
        var options = continueOnCapturedContext
            ? ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext
            : ConfigureAwaitOptions.SuppressThrowing;

        await Task.Delay(millisecondsDelay, token).ConfigureAwait(options);
        return !token.IsCancellationRequested;
    }

    /// <summary>
    /// Ожидает задержку без генерации <see cref="OperationCanceledException"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Task<bool> DelayNoThrowAsync(
        this CancellationToken token,
        TimeSpan delay,
        bool continueOnCapturedContext = true)
    {
        long ms = (long)delay.TotalMilliseconds;
        return token.DelayNoThrowAsync(ms > int.MaxValue ? int.MaxValue : (int)ms, continueOnCapturedContext);
    }

    /// <summary>
    /// Асинхронно ожидает сигнала отмены токена без генерации исключений.
    /// Завершает задачу штатно в момент отмены.
    /// </summary>
    public static Task WhenCanceledAsync(this CancellationToken token)
    {
        if (token.IsCancellationRequested)
            return Task.CompletedTask;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reg = token.UnsafeRegister(static s => ((TaskCompletionSource)s!).TrySetResult(), tcs);

        tcs.Task.ContinueWith(
            static (_, r) => ((CancellationTokenRegistration)r!).Dispose(),
            reg,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return tcs.Task;
    }
}