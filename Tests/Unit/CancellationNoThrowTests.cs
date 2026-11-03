using System.Diagnostics;
using LMP.Tests.Framework;
using TokenExtensions = LMP.Core.Helpers.Extensions.CancellationTokenExtensions;

namespace LMP.Tests.Unit;

/// <summary>
/// Контракты безысключительного ожидания и измерение цены паттерна <c>Task.Delay(ms, ct)</c>,
/// от которого очищены циклы ожидания AudioPlayer.Seek.cs и повторы AudioPipeline.DecoderLoopAsync.
/// </summary>
public static class CancellationNoThrowTests
{
    private const int CancelIterations = 2_000;

    /// <summary>Число раундов замера: медиана по ним гасит разовые паузы GC и перекомпиляцию JIT посреди цикла.</summary>
    private const int MeasureRounds = 5;

    private const int RoundIterations = CancelIterations / MeasureRounds;

    /// <summary>
    /// Верхняя граница времени на бросающую ветку. Без отладчика 2 000 отмен занимают около 10 мс,
    /// граница срабатывает только под профилировщиком или иным перехватчиком исключений.
    /// </summary>
    private static readonly TimeSpan ThrowingPathBudget = TimeSpan.FromMilliseconds(250);

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
    /// Время каждого пути — медиана <see cref="MeasureRounds"/> раундов, чтобы одиночный выброс не решал исход сравнения.
    /// Под отладчиком бросающая ветка не запускается: каждое first-chance исключение останавливает
    /// все управляемые потоки процесса, включая аудиопоток, на 10–20 мс.
    /// </summary>
    [TestMethod(TestCategory.Benchmark, "Cancellation Cost: Task.Delay(ms, ct) vs DelayNoThrowAsync",
        Group = TestGroups.System, Order = 21, TimeoutSeconds = 60)]
    public static async Task TestCancellationCostBenchmarkAsync()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var token = cts.Token;
        bool debuggerAttached = Debugger.IsAttached;

        // Прогрев (JIT, первичная раскрутка стека). Бросающую ветку под отладчиком не трогаем.
        for (int i = 0; i < 50; i++)
        {
            if (!debuggerAttached)
                _ = await ThrowingDelayAsync(token).ConfigureAwait(false);
            _ = await TokenExtensions.DelayNoThrowAsync(token, 100, continueOnCapturedContext: false).ConfigureAwait(false);
        }

        int threadId = Environment.CurrentManagedThreadId;

        // 1. Целевой паттерн инварианта 3: DelayNoThrowAsync. Безопасен в любом окружении.
        // Массив раундов создаётся до снимка счётчика, чтобы не попасть в учёт аллокаций
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
            // Каждое исключение под отладчиком = остановка всего процесса: 2 000 отмен давали 20-45 с заиканий звука и UI
            TestAssert.Skip("Debugger attached: throwing path skipped, every first-chance exception suspends all managed threads");
        }
        else
        {
            // 2. Запрещённый паттерн: Task.Delay(ms, ct) + OperationCanceledException, ограничен по времени
            // Бюджет проверяется между раундами: хотя бы один раунд выполняется всегда, медиана берётся по завершённым
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
            // Намеренно воспроизводим запрещённый паттерн, чтобы измерить его цену
            await Task.Delay(100, token).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    /// <summary>Медиана замеров раундов. Сортирует переданный срез на месте и не аллоцирует.</summary>
    private static double MedianInPlace(Span<double> rounds)
    {
        rounds.Sort();
        int mid = rounds.Length / 2;
        return (rounds.Length & 1) != 0 ? rounds[mid] : 0.5 * (rounds[mid - 1] + rounds[mid]);
    }
}