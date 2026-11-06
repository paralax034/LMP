using System.Runtime.CompilerServices;

namespace LMP.Core.Helpers.Extensions;

/// <summary>
/// High-performance exception-free extension methods for <see cref="CancellationToken"/>.
/// </summary>
public static class CancellationTokenExtensions
{
    /// <summary>
    /// Waits for a delay without throwing an <see cref="OperationCanceledException"/>.
    /// Returns <see langword="false"/> if canceled and <see langword="true"/> when the delay elapses.
    /// </summary>
    private static readonly Task<bool> TrueTask = Task.FromResult(true);
    private static readonly Task<bool> FalseTask = Task.FromResult(false);

    /// <summary>
    /// Waits for a delay without throwing an <see cref="OperationCanceledException"/>.
    /// Returns <see langword="false"/> if canceled and <see langword="true"/> when the delay elapses.
    /// Synchronous branches (already canceled token or zero delay) do not allocate a state machine (0 B).
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
    /// Waits for a delay without throwing an <see cref="OperationCanceledException"/>.
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
    /// Asynchronously waits for a token cancellation signal without throwing exceptions.
    /// Completes the task normally upon cancellation.
    /// </summary>
    public static Task WhenCanceledAsync(this CancellationToken token)
    {
        if (token.IsCancellationRequested)
            return Task.CompletedTask;

        var state = new WhenCanceledState();
        state.Registration = token.UnsafeRegister(static s =>
        {
            var self = (WhenCanceledState)s!;
            self.Registration.Dispose();
            self.Tcs.TrySetResult();
        }, state);

        return state.Tcs.Task;
    }

    private sealed class WhenCanceledState
    {
        public readonly TaskCompletionSource Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration;
    }
}