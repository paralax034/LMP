using System.Diagnostics;
using System.Reflection;

namespace LMP.Tests.Framework;

/// <summary>
/// Движок выполнения тестов с изоляцией от UI-потока, поддержкой CancellationToken и статуса Skipped.
/// </summary>
public sealed class TestRunner
{
    private readonly IServiceProvider _services;

    /// <summary>Вызывается перед началом выполнения каждого теста.</summary>
    public event Action<TestDescriptor>? TestStarting;

    /// <summary>Вызывается по завершении выполнения каждого теста.</summary>
    public event Action<TestDescriptor, TestResult>? TestCompleted;

    public TestRunner(IServiceProvider services)
    {
        _services = services;
    }

    /// <summary>Запускает один тест.</summary>
    public async Task<TestResult> RunAsync(TestDescriptor descriptor, CancellationToken ct = default)
    {
        TestStarting?.Invoke(descriptor);

        var result = await ExecuteTestAsync(descriptor, ct).ConfigureAwait(false);

        TestCompleted?.Invoke(descriptor, result);
        return result;
    }

    /// <summary>Запускает список тестов последовательно.</summary>
    public async Task<IReadOnlyList<TestResult>> RunBatchAsync(
        IReadOnlyList<TestDescriptor> tests,
        CancellationToken ct = default)
    {
        var results = new List<TestResult>(tests.Count);

        for (int i = 0; i < tests.Count; i++)
        {
            var test = tests[i];
            if (ct.IsCancellationRequested)
            {
                results.Add(new TestResult
                {
                    TestId = test.Id,
                    State = TestRunState.Skipped,
                    Duration = TimeSpan.Zero,
                    ErrorMessage = "Cancelled",
                });
                continue;
            }

            var result = await RunAsync(test, ct).ConfigureAwait(false);
            results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// Запускает все автоматические тесты (Unit, Integration, Benchmark, E2E).
    /// Исключает деструктивные и ручные действия (<see cref="TestCategory.Action"/>).
    /// </summary>
    public Task<IReadOnlyList<TestResult>> RunAllAsync(CancellationToken ct = default)
    {
        var all = TestDiscovery.GetAllOrdered();
        var runnable = new List<TestDescriptor>(all.Count);

        for (int i = 0; i < all.Count; i++)
        {
            var test = all[i];
            if (test.Category != TestCategory.Action)
                runnable.Add(test);
        }

        return RunBatchAsync(runnable, ct);
    }

    private async Task<TestResult> ExecuteTestAsync(
        TestDescriptor descriptor,
        CancellationToken externalCt)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            using var timeoutCts = descriptor.TimeoutSeconds > 0
                ? new CancellationTokenSource(TimeSpan.FromSeconds(descriptor.TimeoutSeconds))
                : new CancellationTokenSource();

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                externalCt, timeoutCts.Token);

            // Вызов метода ВСЕГДА выносится на пул потоков во избежание блокировки UI-диспетчера Avalonia
            var task = Task.Run(() => InvokeTestMethod(descriptor, linkedCts.Token), linkedCts.Token);
            await task.WaitAsync(linkedCts.Token).ConfigureAwait(false);

            sw.Stop();
            return new TestResult
            {
                TestId = descriptor.Id,
                State = TestRunState.Passed,
                Duration = sw.Elapsed,
            };
        }
        catch (Exception ex)
        {
            sw.Stop();

            var inner = ex is AggregateException agg ? agg.InnerException ?? ex : ex;
            if (inner is TargetInvocationException tie)
                inner = tie.InnerException ?? tie;

            if (inner is TestSkippedException skipped)
            {
                return new TestResult
                {
                    TestId = descriptor.Id,
                    State = TestRunState.Skipped,
                    Duration = sw.Elapsed,
                    ErrorMessage = skipped.Reason,
                };
            }

            if (inner is OperationCanceledException)
            {
                if (externalCt.IsCancellationRequested)
                {
                    return new TestResult
                    {
                        TestId = descriptor.Id,
                        State = TestRunState.Skipped,
                        Duration = sw.Elapsed,
                        ErrorMessage = "Cancelled by user",
                    };
                }

                return new TestResult
                {
                    TestId = descriptor.Id,
                    State = TestRunState.Failed,
                    Duration = sw.Elapsed,
                    ErrorMessage = $"Timeout ({descriptor.TimeoutSeconds}s)",
                };
            }

            return new TestResult
            {
                TestId = descriptor.Id,
                State = TestRunState.Failed,
                Duration = sw.Elapsed,
                ErrorMessage = inner.Message,
            };
        }
    }

    private Task InvokeTestMethod(TestDescriptor descriptor, CancellationToken ct)
    {
        object?[] args = descriptor.Signature switch
        {
            TestParameterSignature.ServicesOnly => [_services],
            TestParameterSignature.TokenOnly => [ct],
            TestParameterSignature.ServicesAndToken => [_services, ct],
            TestParameterSignature.TokenAndServices => [ct, _services],
            _ => []
        };

        var result = descriptor.Method.Invoke(null, args);
        return result is Task task ? task : Task.CompletedTask;
    }
}