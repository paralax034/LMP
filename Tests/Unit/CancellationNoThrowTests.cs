using System.Diagnostics;
using LMP.Tests.Framework;
using TokenExtensions = LMP.Core.Helpers.Extensions.CancellationTokenExtensions;

namespace LMP.Tests.Unit;

/// <summary>
/// Контракты безысключительного ожидания и измерение цены паттерна <c>Task.Delay(ms, ct)</c>,
/// который сейчас используется в AudioPlayer.Seek.cs (стр. 505, 550) и CachingStreamSource.Refresh.cs (стр. 196, 309).
/// </summary>
public static class CancellationNoThrowTests
{
    private const int CancelIterations = 2_000;

    /// <summary>
    /// Проверяет контракты <see cref="TokenExtensions.DelayNoThrowAsync(CancellationToken, int, bool)"/>
    /// и <see cref="TokenExtensions.WhenCanceledAsync"/>: без исключений и с 0 B на синхронной ветке.
    /// </summary>
    [TestMethod(TestCategory.Unit, "DelayNoThrowAsync & WhenCanceledAsync Contracts", Group = TestGroups.System, Order = 20)]
    public static async Task TestDelayNoThrowContractsAsync()
    {
        using var canceledCts = new CancellationTokenSource();
        canceledCts.Cancel();
        var canceled = canceledCts.Token;

        // 1. Уже отменённый токен: синхронный false из кэшированной задачи, 0 B
        Task<bool> preCanceled = TokenExtensions.DelayNoThrowAsync(canceled, 10_000);
        TestAssert.True(preCanceled.IsCompletedSuccessfully, "Pre-canceled delay must complete synchronously");
        TestAssert.False(await preCanceled.ConfigureAwait(false), "Pre-canceled delay must return false");

        TestAssert.ZeroAlloc(() =>
        {
            var pending = TokenExtensions.DelayNoThrowAsync(canceled, 10_000, continueOnCapturedContext: false);
            if (!pending.IsCompletedSuccessfully)
                TestAssert.Fail("Pre-canceled DelayNoThrowAsync must complete synchronously");
        }, warmup: 10, iterations: 5_000, context: "DelayNoThrowAsync (pre-canceled)");

        // 2. Нулевая задержка завершается синхронно с true
        TestAssert.True(await TokenExtensions.DelayNoThrowAsync(CancellationToken.None, 0).ConfigureAwait(false), "Zero delay must return true");

        // 3. Отмена во время ожидания: false без OperationCanceledException и без ожидания полного интервала
        using var midwayCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        long start = Stopwatch.GetTimestamp();
        bool completed = await TokenExtensions.DelayNoThrowAsync(midwayCts.Token, 10_000, continueOnCapturedContext: false).ConfigureAwait(false);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

        TestAssert.False(completed, "Delay canceled midway must return false");
        TestAssert.True(elapsed < TimeSpan.FromSeconds(5), $"Canceled delay did not return promptly: {elapsed.TotalMilliseconds:F0}ms");

        // 4. WhenCanceledAsync: штатное завершение в момент отмены
        TestAssert.True(TokenExtensions.WhenCanceledAsync(canceled).IsCompletedSuccessfully, "Pre-canceled WhenCanceledAsync must complete synchronously");

        using var laterCts = new CancellationTokenSource();
        Task whenCanceled = TokenExtensions.WhenCanceledAsync(laterCts.Token);
        TestAssert.False(whenCanceled.IsCompleted, "WhenCanceledAsync must stay pending until cancellation");

        laterCts.Cancel();
        Task winner = await Task.WhenAny(whenCanceled, Task.Delay(5_000)).ConfigureAwait(false);
        TestAssert.True(ReferenceEquals(winner, whenCanceled) && whenCanceled.IsCompletedSuccessfully, "WhenCanceledAsync must complete successfully on cancel");
    }

    /// <summary>
    /// Измеряет цену одной отмены: <c>Task.Delay(ms, ct)</c> с перехватом исключения против <c>DelayNoThrowAsync</c>.
    /// Оба пути завершаются синхронно на одном потоке, поэтому поточный счётчик аллокаций корректен.
    /// </summary>
    [TestMethod(TestCategory.Benchmark, "Cancellation Cost: Task.Delay(ms, ct) vs DelayNoThrowAsync",
        Group = TestGroups.System, Order = 21, TimeoutSeconds = 60)]
    public static async Task TestCancellationCostBenchmarkAsync()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var token = cts.Token;

        // Прогрев обоих путей (JIT, первичная раскрутка стека)
        for (int i = 0; i < 50; i++)
        {
            _ = await ThrowingDelayAsync(token).ConfigureAwait(false);
            _ = await TokenExtensions.DelayNoThrowAsync(token, 100, continueOnCapturedContext: false).ConfigureAwait(false);
        }

        int threadId = Environment.CurrentManagedThreadId;

        // 1. Текущий паттерн: Task.Delay(ms, ct) + OperationCanceledException
        int thrown = 0;
        long throwAllocBefore = GC.GetAllocatedBytesForCurrentThread();
        long throwStart = Stopwatch.GetTimestamp();
        for (int i = 0; i < CancelIterations; i++)
        {
            if (await ThrowingDelayAsync(token).ConfigureAwait(false))
                thrown++;
        }
        double throwNs = Stopwatch.GetElapsedTime(throwStart).TotalNanoseconds / CancelIterations;
        long throwBytes = GC.GetAllocatedBytesForCurrentThread() - throwAllocBefore;

        // 2. Целевой паттерн инварианта 3: DelayNoThrowAsync
        int canceledNoThrow = 0;
        long noThrowAllocBefore = GC.GetAllocatedBytesForCurrentThread();
        long noThrowStart = Stopwatch.GetTimestamp();
        for (int i = 0; i < CancelIterations; i++)
        {
            if (!await TokenExtensions.DelayNoThrowAsync(token, 100, continueOnCapturedContext: false).ConfigureAwait(false))
                canceledNoThrow++;
        }
        double noThrowNs = Stopwatch.GetElapsedTime(noThrowStart).TotalNanoseconds / CancelIterations;
        long noThrowBytes = GC.GetAllocatedBytesForCurrentThread() - noThrowAllocBefore;

        if (Environment.CurrentManagedThreadId != threadId)
            TestAssert.Skip("Continuation hopped threads; per-thread allocation counter is not comparable");

        Log.Info($"[Benchmark] Task.Delay(ms, ct) + catch | {throwNs,10:F1} ns/cancel | {(double)throwBytes / CancelIterations,8:F1} B/cancel");
        Log.Info($"[Benchmark] DelayNoThrowAsync        | {noThrowNs,10:F1} ns/cancel | {(double)noThrowBytes / CancelIterations,8:F1} B/cancel");

        TestAssert.Equal(CancelIterations, thrown, "Every pre-canceled Task.Delay must throw");
        TestAssert.Equal(CancelIterations, canceledNoThrow, "Every pre-canceled DelayNoThrowAsync must return false");
        TestAssert.Equal(0L, noThrowBytes, "DelayNoThrowAsync cancellation path must allocate 0 B");
        TestAssert.True(throwBytes > 0, "Throwing cancellation path is expected to allocate (exception + canceled task)");
        TestAssert.True(throwNs >= noThrowNs * 10.0, $"Expected >=10x overhead for throwing path: {throwNs:F1}ns vs {noThrowNs:F1}ns");
    }

    private static async Task<bool> ThrowingDelayAsync(CancellationToken token)
    {
        try
        {
            // Намеренно воспроизводим запрещённый паттерн, чтобы измерить его цену
            await Task.Delay(100, token).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }
}