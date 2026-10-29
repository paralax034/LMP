using System.Diagnostics;
using LMP.Tests.Framework;

namespace LMP.Tests;

/// <summary>
/// Универсальный CLI-раннер тестового фреймворка LMP поверх <see cref="TestDiscovery"/> и <see cref="TestRunner"/>.
/// Поддерживает декларативную фильтрацию по группам, категориям и именам без хардкода тестовых методов.
/// </summary>
/// <remarks>
/// Поддерживаемые аргументы командной строки:
/// <list type="bullet">
///   <item><c>--test</c> или <c>--run-tests</c> — запуск всех автоматических тестов (кроме <see cref="TestCategory.Action"/>).</item>
///   <item><c>--test-group=Storage,Audio</c> — фильтрация по одной или нескольким группам <see cref="TestGroups"/>.</item>
///   <item><c>--test-category=Unit,Integration</c> — фильтрация по категориям <see cref="TestCategory"/>.</item>
///   <item><c>--test-filter=CacheTests</c> — поиск подстроки в <c>ClassName.MethodName</c> или <c>DisplayName</c>.</item>
///   <item><c>--test-list</c> — вывод всех найденных тестов без выполнения.</item>
/// </list>
/// </remarks>
public static class ManualTests
{
    /// <summary>
    /// Проверяет, является ли аргумент командной строки флагом запуска тестового раннера.
    /// </summary>
    public static bool IsTestCliArgument(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return false;

        return string.Equals(arg, "--test", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "--run-tests", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "--run-cache-tests", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "--test-list", StringComparison.OrdinalIgnoreCase)
            || arg.StartsWith("--test-group=", StringComparison.OrdinalIgnoreCase)
            || arg.StartsWith("--test-category=", StringComparison.OrdinalIgnoreCase)
            || arg.StartsWith("--test-filter=", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Проверяет наличие хотя бы одного тестового CLI-аргумента в массиве параметров запуска.
    /// </summary>
    public static bool HasTestCliArguments(ReadOnlySpan<string> args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (IsTestCliArgument(args[i]))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Запускает все автоматические тесты.
    /// </summary>
    public static Task<int> RunAllAsync(CancellationToken ct = default) =>
        RunCliAsync(["--test"], ct);

    /// <summary>
    /// Разбирает CLI-аргументы, фильтрует тесты через <see cref="TestDiscovery"/> и выполняет их через <see cref="TestRunner"/>.
    /// Возвращает код завершения процесса (<c>0</c> — успех, <c>1</c> — есть упавшие тесты или фильтр пуст).
    /// </summary>
    public static async Task<int> RunCliAsync(string[] args, CancellationToken ct = default)
    {
        bool listOnly = false;
        HashSet<string>? groupFilter = null;
        HashSet<TestCategory>? categoryFilter = null;
        List<string>? nameFilters = null;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.IsNullOrEmpty(arg)) continue;

            if (string.Equals(arg, "--test-list", StringComparison.OrdinalIgnoreCase))
            {
                listOnly = true;
            }
            else if (string.Equals(arg, "--run-cache-tests", StringComparison.OrdinalIgnoreCase))
            {
                groupFilter ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                groupFilter.Add(TestGroups.Storage);
                nameFilters ??= [];
                nameFilters.Add("CacheTests");
            }
            else if (arg.StartsWith("--test-group=", StringComparison.OrdinalIgnoreCase))
            {
                groupFilter ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ParseCsvTokens(arg.AsSpan(13), groupFilter);
            }
            else if (arg.StartsWith("--test-category=", StringComparison.OrdinalIgnoreCase))
            {
                categoryFilter ??= [];
                ParseCategories(arg.AsSpan(16), categoryFilter);
            }
            else if (arg.StartsWith("--test-filter=", StringComparison.OrdinalIgnoreCase))
            {
                var pattern = arg[14..].Trim();
                if (!string.IsNullOrEmpty(pattern))
                {
                    nameFilters ??= [];
                    nameFilters.Add(pattern);
                }
            }
        }

        var allOrdered = TestDiscovery.GetAllOrdered();
        var matched = new List<TestDescriptor>(allOrdered.Count);
        bool includeActions = categoryFilter is not null && categoryFilter.Contains(TestCategory.Action);

        for (int i = 0; i < allOrdered.Count; i++)
        {
            var desc = allOrdered[i];

            if (!includeActions && desc.Category == TestCategory.Action)
                continue;

            if (categoryFilter is not null && !categoryFilter.Contains(desc.Category))
                continue;

            if (groupFilter is not null && !groupFilter.Contains(desc.Group))
                continue;

            if (nameFilters is not null && !MatchesAnyFilter(desc, nameFilters))
                continue;

            matched.Add(desc);
        }

        if (listOnly)
        {
            Console.WriteLine($"\nDiscovered {matched.Count} matching tests:");
            for (int i = 0; i < matched.Count; i++)
            {
                var d = matched[i];
                Console.WriteLine($"  [{d.Category}] [{d.Group}] {d.Id} — {d.DisplayName}");
            }
            return 0;
        }

        if (matched.Count == 0)
        {
            Console.Error.WriteLine("\n[TestRunner] No tests matched the specified CLI filters.");
            return 1;
        }

        var sw = Stopwatch.StartNew();

        Console.WriteLine("\n" + new string('=', 70));
        Console.WriteLine($"  LMP AUTOMATED TEST SUITE ({matched.Count} tests)");
        Console.WriteLine(new string('=', 70));

        var runner = new TestRunner(AppEntry.Services);
        int passed = 0, failed = 0, skipped = 0;

        runner.TestCompleted += (descriptor, result) =>
        {
            var icon = result.State switch
            {
                TestRunState.Passed => "✓",
                TestRunState.Failed => "✗",
                TestRunState.Skipped => "⊘",
                _ => "?"
            };

            Console.WriteLine(
                $"  {icon} [{descriptor.Group}] {descriptor.DisplayName} ({result.DurationFormatted})" +
                (result.ErrorMessage is not null ? $"\n    → {result.ErrorMessage}" : ""));

            switch (result.State)
            {
                case TestRunState.Passed: Interlocked.Increment(ref passed); break;
                case TestRunState.Failed: Interlocked.Increment(ref failed); break;
                case TestRunState.Skipped: Interlocked.Increment(ref skipped); break;
            }
        };

        TestCategory? currentCategory = null;
        var batch = new List<TestDescriptor>(matched.Count);

        for (int i = 0; i < matched.Count; i++)
        {
            var test = matched[i];
            if (currentCategory != test.Category)
            {
                if (batch.Count > 0)
                {
                    await runner.RunBatchAsync(batch, ct).ConfigureAwait(false);
                    batch.Clear();
                }

                currentCategory = test.Category;
                Console.WriteLine($"\n▶ {FormatCategoryHeader(currentCategory.Value)}\n");
            }

            batch.Add(test);
        }

        if (batch.Count > 0)
        {
            await runner.RunBatchAsync(batch, ct).ConfigureAwait(false);
        }

        sw.Stop();

        Console.WriteLine("\n" + new string('=', 70));
        Console.WriteLine($"  RESULTS: {passed} passed, {failed} failed, {skipped} skipped ({sw.Elapsed.TotalSeconds:F1}s)");
        Console.WriteLine(new string('=', 70) + "\n");

        return failed > 0 ? 1 : 0;
    }

    private static void ParseCsvTokens(ReadOnlySpan<char> raw, HashSet<string> target)
    {
        foreach (var range in raw.Split(','))
        {
            var token = raw[range].Trim();
            if (!token.IsEmpty)
                target.Add(TestGroups.Normalize(token.ToString()));
        }
    }

    private static void ParseCategories(ReadOnlySpan<char> raw, HashSet<TestCategory> target)
    {
        foreach (var range in raw.Split(','))
        {
            var token = raw[range].Trim();
            if (!token.IsEmpty && Enum.TryParse<TestCategory>(token, ignoreCase: true, out var cat))
                target.Add(cat);
        }
    }

    private static bool MatchesAnyFilter(TestDescriptor desc, List<string> filters)
    {
        for (int i = 0; i < filters.Count; i++)
        {
            var f = filters[i].AsSpan();
            if (desc.Id.AsSpan().Contains(f, StringComparison.OrdinalIgnoreCase) ||
                desc.DisplayName.AsSpan().Contains(f, StringComparison.OrdinalIgnoreCase) ||
                desc.ClassName.AsSpan().Contains(f, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string FormatCategoryHeader(TestCategory category) => category switch
    {
        TestCategory.Unit => "UNIT TESTS",
        TestCategory.Integration => "INTEGRATION TESTS (Network / Services)",
        TestCategory.Benchmark => "BENCHMARKS",
        TestCategory.Diagnostic => "DIAGNOSTIC PROBES",
        _ => category.ToString().ToUpperInvariant()
    };
}