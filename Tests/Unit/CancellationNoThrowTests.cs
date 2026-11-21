using System.Diagnostics;
using LMP.Tests.Framework;
using TokenExtensions = LMP.Core.Helpers.Extensions.CancellationTokenExtensions;

namespace LMP.Tests.Unit;

/// <summary>
/// Exception-free waiting contracts for <see cref="TokenExtensions.DelayNoThrowAsync(CancellationToken, int, bool)"/>
/// and <see cref="TokenExtensions.WhenCanceledAsync"/>.
/// </summary>
public static class CancellationNoThrowTests
{
    [TestMethod(TestCategory.Unit, "DelayNoThrowAsync & WhenCanceledAsync Contracts", Group = TestGroups.System, Order = 20)]
    public static async Task TestDelayNoThrowContractsAsync()
    {
        using var canceledCts = new CancellationTokenSource();
        canceledCts.Cancel();
        var canceled = canceledCts.Token;

        // 1. Already canceled token: synchronous false from a cached task, 0 B
        Task<bool> preCanceled = TokenExtensions.DelayNoThrowAsync(canceled, 10_000);
        TestAssert.True(preCanceled.IsCompletedSuccessfully, "Pre-canceled delay must complete synchronously");
        TestAssert.False(await preCanceled.ConfigureAwait(false), "Pre-canceled delay must return false");

        TestAssert.ZeroAlloc(() =>
        {
            var pending = TokenExtensions.DelayNoThrowAsync(canceled, 10_000, continueOnCapturedContext: false);
            if (!pending.IsCompletedSuccessfully)
                TestAssert.Fail("Pre-canceled DelayNoThrowAsync must complete synchronously");
        }, warmup: 10, iterations: 5_000, context: "DelayNoThrowAsync (pre-canceled)");

        // 2. Zero delay completes synchronously returning true
        TestAssert.True(await TokenExtensions.DelayNoThrowAsync(CancellationToken.None, 0).ConfigureAwait(false), "Zero delay must return true");

        // 3. Cancellation during wait: false without first-chance OperationCanceledException and without waiting for the full interval
        var firstChanceScope = new AsyncLocal<bool>();
        int cancelExceptionsCount = 0;
        void OnFirstChanceException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (firstChanceScope.Value && e.Exception is OperationCanceledException)
                Interlocked.Increment(ref cancelExceptionsCount);
        }

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
        firstChanceScope.Value = true;
        bool completed;
        TimeSpan elapsed;
        try
        {
            using var midwayCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
            long start = Stopwatch.GetTimestamp();
            completed = await TokenExtensions.DelayNoThrowAsync(midwayCts.Token, 10_000, continueOnCapturedContext: false).ConfigureAwait(false);
            elapsed = Stopwatch.GetElapsedTime(start);
        }
        finally
        {
            firstChanceScope.Value = false;
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
        }

        TestAssert.False(completed, "Delay canceled midway must return false");
        TestAssert.Equal(0, Volatile.Read(ref cancelExceptionsCount), "SuppressThrowing must not raise first-chance OperationCanceledException");
        TestAssert.True(elapsed < TimeSpan.FromSeconds(5), $"Canceled delay did not return promptly: {elapsed.TotalMilliseconds:F0}ms");

        // 4. WhenCanceledAsync: graceful completion at the moment of cancellation
        TestAssert.True(TokenExtensions.WhenCanceledAsync(canceled).IsCompletedSuccessfully, "Pre-canceled WhenCanceledAsync must complete synchronously");

        using var laterCts = new CancellationTokenSource();
        Task whenCanceled = TokenExtensions.WhenCanceledAsync(laterCts.Token);
        TestAssert.False(whenCanceled.IsCompleted, "WhenCanceledAsync must stay pending until cancellation");

        laterCts.Cancel();
        Task winner = await Task.WhenAny(whenCanceled, Task.Delay(5_000)).ConfigureAwait(false);
        TestAssert.True(ReferenceEquals(winner, whenCanceled) && whenCanceled.IsCompletedSuccessfully, "WhenCanceledAsync must complete successfully on cancel");
    }
}