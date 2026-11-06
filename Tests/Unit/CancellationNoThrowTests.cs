using System.Diagnostics;
using LMP.Tests.Framework;
using TokenExtensions = LMP.Core.Helpers.Extensions.CancellationTokenExtensions;

namespace LMP.Tests.Unit;

/// <summary>
/// Exception-free waiting contracts and performance measurement of the <c>Task.Delay(ms, ct)</c> pattern,
/// which was eliminated from the wait loops in AudioPlayer.Seek.cs and retries in AudioPipeline.DecoderLoopAsync.
/// </summary>
public static class CancellationNoThrowTests
{
    private const int CancelIterations = 2_000;

    /// <summary>Number of benchmark rounds: taking the median mitigates one-off GC pauses and JIT re-compilation mid-loop.</summary>
    private const int MeasureRounds = 5;

    private const int RoundIterations = CancelIterations / MeasureRounds;

    /// <summary>
    /// Upper time limit for the throwing branch. Without a debugger attached, 2,000 cancellations take about 10 ms;
    /// the limit triggers only under a profiler or other exception interceptor.
    /// </summary>
    private static readonly TimeSpan ThrowingPathBudget = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Verifies contracts of <see cref="TokenExtensions.DelayNoThrowAsync(CancellationToken, int, bool)"/>
    /// and <see cref="TokenExtensions.WhenCanceledAsync"/>: exception-free execution and 0 B allocations on the synchronous path.
    /// </summary>
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

        // 3. Cancellation during wait: false without OperationCanceledException and without waiting for the full interval
        using var midwayCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        long start = Stopwatch.GetTimestamp();
        bool completed = await TokenExtensions.DelayNoThrowAsync(midwayCts.Token, 10_000, continueOnCapturedContext: false).ConfigureAwait(false);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

        TestAssert.False(completed, "Delay canceled midway must return false");
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

    /// <summary>
    /// Measures the cost of a single cancellation: <c>Task.Delay(ms, ct)</c> with exception catching versus <c>DelayNoThrowAsync</c>.
    /// Both paths complete synchronously on a single thread, ensuring the per-thread allocation counter is accurate.
    /// The duration of each path is the median of <see cref="MeasureRounds"/> rounds so that an isolated outlier does not skew the result.
    /// Under a debugger, the throwing branch is skipped: every first-chance exception pauses
    /// all managed threads in the process, including the audio thread, for 10–20 ms.
    /// </summary>
    [TestMethod(TestCategory.Benchmark, "Cancellation Cost: Task.Delay(ms, ct) vs DelayNoThrowAsync",
        Group = TestGroups.System, Order = 21, TimeoutSeconds = 60)]
    public static async Task TestCancellationCostBenchmarkAsync()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var token = cts.Token;
        bool debuggerAttached = Debugger.IsAttached;

        // Warmup (JIT, initial stack unwind). Skip the throwing branch under a debugger.
        for (int i = 0; i < 50; i++)
        {
            if (!debuggerAttached)
                _ = await ThrowingDelayAsync(token).ConfigureAwait(false);
            _ = await TokenExtensions.DelayNoThrowAsync(token, 100, continueOnCapturedContext: false).ConfigureAwait(false);
        }

        int threadId = Environment.CurrentManagedThreadId;

        // 1. Target pattern of Invariant 3: DelayNoThrowAsync. Safe in any environment.
        // Round array is created before taking the snapshot to avoid including its allocations in the count
        double[] noThrowRounds = new double[MeasureRounds];
        int canceledNoThrow = 0;
        long noThrowAllocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int round = 0; round < MeasureRounds; round++)
        {
            long roundStart = Stopwatch.GetTimestamp();
            for (int i = 0; i < RoundIterations; i++)
            {
                if (!await TokenExtensions.DelayNoThrowAsync(token, 100, continueOnCapturedContext: false).ConfigureAwait(false))
                    canceledNoThrow++;
            }

            noThrowRounds[round] = Stopwatch.GetElapsedTime(roundStart).TotalNanoseconds / RoundIterations;
        }
        long noThrowBytes = GC.GetAllocatedBytesForCurrentThread() - noThrowAllocBefore;
        double noThrowNs = MedianInPlace(noThrowRounds);

        if (Environment.CurrentManagedThreadId != threadId)
            TestAssert.Skip("Continuation hopped threads; per-thread allocation counter is not comparable");

        Log.Info($"[Benchmark] DelayNoThrowAsync        | {noThrowNs,10:F1} ns/cancel | {(double)noThrowBytes / CancelIterations,8:F1} B/cancel | median of {MeasureRounds}");

        TestAssert.Equal(CancelIterations, canceledNoThrow, "Every pre-canceled DelayNoThrowAsync must return false");
        TestAssert.Equal(0L, noThrowBytes, "DelayNoThrowAsync cancellation path must allocate 0 B");

        if (debuggerAttached)
        {
            // Every exception under a debugger = full process freeze: 2,000 cancellations caused 20–45 s of audio stutters and UI lag
            TestAssert.Skip("Debugger attached: throwing path skipped, every first-chance exception suspends all managed threads");
        }
        else
        {
            // 2. Forbidden pattern: Task.Delay(ms, ct) + OperationCanceledException, time-bounded
            // The budget is checked between rounds: at least one round always executes; the median is computed over completed rounds
            double[] throwRounds = new double[MeasureRounds];
            int completedRounds = 0;
            int thrown = 0;
            int throwIterations = 0;
            long throwAllocBefore = GC.GetAllocatedBytesForCurrentThread();
            long throwStart = Stopwatch.GetTimestamp();
            while (completedRounds < MeasureRounds && Stopwatch.GetElapsedTime(throwStart) <= ThrowingPathBudget)
            {
                long roundStart = Stopwatch.GetTimestamp();
                for (int i = 0; i < RoundIterations; i++)
                {
                    if (await ThrowingDelayAsync(token).ConfigureAwait(false))
                        thrown++;
                }

                throwIterations += RoundIterations;
                throwRounds[completedRounds++] = Stopwatch.GetElapsedTime(roundStart).TotalNanoseconds / RoundIterations;
            }
            long throwBytes = GC.GetAllocatedBytesForCurrentThread() - throwAllocBefore;
            double throwNs = MedianInPlace(throwRounds.AsSpan(0, completedRounds));

            if (Environment.CurrentManagedThreadId != threadId)
                TestAssert.Skip("Continuation hopped threads; per-thread allocation counter is not comparable");

            Log.Info($"[Benchmark] Task.Delay(ms, ct) + catch | {throwNs,10:F1} ns/cancel | {(double)throwBytes / throwIterations,8:F1} B/cancel | n={throwIterations} | median of {completedRounds}");

            TestAssert.Equal(throwIterations, thrown, "Every pre-canceled Task.Delay must throw");
            TestAssert.True(throwBytes > 0, "Throwing cancellation path is expected to allocate (exception + canceled task)");
            TestAssert.True(throwNs >= noThrowNs * 10.0, $"Expected >=10x overhead for throwing path: {throwNs:F1}ns vs {noThrowNs:F1}ns");
        }
    }

    private static async Task<bool> ThrowingDelayAsync(CancellationToken token)
    {
        try
        {
            // Intentionally reproducing the forbidden pattern to measure its performance cost
            await Task.Delay(100, token).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    /// <summary>Median of round measurements. Sorts the provided slice in-place without allocations.</summary>
    private static double MedianInPlace(Span<double> rounds)
    {
        rounds.Sort();
        int mid = rounds.Length / 2;
        return (rounds.Length & 1) != 0 ? rounds[mid] : 0.5 * (rounds[mid - 1] + rounds[mid]);
    }
}