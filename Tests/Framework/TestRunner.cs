using System.Diagnostics;

namespace LMP.Tests.Framework;

/// <summary>
/// Test execution engine with UI thread isolation, CancellationToken support, and Skipped status handling.
/// </summary>
public sealed class TestRunner
{
    private readonly IServiceProvider _services;

    /// <summary>Invoked before executing each test.</summary>
    public event Action<TestDescriptor>? TestStarting;

    /// <summary>Invoked upon completion of each test.</summary>
    public event Action<TestDescriptor, TestResult>? TestCompleted;

    public TestRunner(IServiceProvider services)
    {
        _services = services;
    }

    /// <summary>Runs a single test.</summary>
    public async Task<TestResult> RunAsync(TestDescriptor descriptor, CancellationToken ct = default)
    {
        TestStarting?.Invoke(descriptor);

        var result = await ExecuteTestAsync(descriptor, ct).ConfigureAwait(false);

        TestCompleted?.Invoke(descriptor, result);
        return result;
    }

    /// <summary>Runs a list of tests sequentially.</summary>
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

        MemoryHelpers.SchedulePostHeavyWorkCleanup(1500);
        return results;
    }

    /// <summary>
    /// Runs all automated tests (Unit, Integration, Benchmark, E2E).
    /// Excludes destructive and manual actions (<see cref="TestCategory.Action"/>).
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

            // The method call is ALWAYS offloaded to the thread pool to avoid blocking the Avalonia UI dispatcher
            var task = Task.Run(() => descriptor.Invoke(_services, linkedCts.Token), linkedCts.Token);
            try
            {
                await task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!timeoutCts.IsCancellationRequested
                && !externalCt.IsCancellationRequested
                && (ex is OperationCanceledException || ex.InnerException is OperationCanceledException))
            {
                // OCE from the test itself without a timeout and without user cancellation is a test failure, not a Timeout
                throw new InvalidOperationException(
                    $"Test threw OperationCanceledException without timeout or user cancellation: {(ex.InnerException ?? ex).Message}", ex);
            }

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
}