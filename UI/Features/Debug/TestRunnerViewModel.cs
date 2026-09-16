using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using LMP.Tests.Framework;

namespace LMP.UI.Features.Debug;

/// <summary>
/// ViewModel для вкладки "Tests" в Debug-окне.
/// <para>
/// Возможности:
/// <list type="bullet">
///   <item>Автоматический discovery тестов через рефлексию</item>
///   <item>Фильтрация по категории (Unit/Integration/Benchmark)</item>
///   <item>Фильтрация по тематической группе (NToken/SigCipher/Pipeline/Cache/Solver)</item>
///   <item>Полнотекстовый поиск по имени теста</item>
///   <item>Запуск одного теста, группы, или всех</item>
///   <item>Редактирование test-config.json без перекомпиляции</item>
/// </list>
/// </para>
/// </summary>
public sealed partial class TestRunnerViewModel : ViewModelBase
{
    private readonly TestRunner _runner;
    private CancellationTokenSource? _runCts;

    // PROPERTIES

    /// <summary>Все обнаруженные тесты.</summary>
    public ObservableCollection<TestItemViewModel> AllTests { get; } = [];

    /// <summary>Отфильтрованные тесты для отображения.</summary>
    [ObservableProperty]
    public partial ObservableCollection<TestItemViewModel> FilteredTests { get; set; } = [];

    /// <summary>Выбранный фильтр категории (null = все).</summary>
    [ObservableProperty]
    public partial TestCategory? SelectedCategory { get; set; }

    /// <summary>Выбранный фильтр тематической группы (null = все).</summary>
    [ObservableProperty]
    public partial string? SelectedGroup { get; set; }

    /// <summary>Текст поиска по имени теста.</summary>
    [ObservableProperty]
    public partial string SearchFilter { get; set; } = "";

    /// <summary>Идёт ли запуск тестов.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunUnitCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunIntegrationCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunBenchmarksCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunFilteredCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunSingleCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsRunning { get; set; }

    /// <summary>Суммарная статистика.</summary>
    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    /// <summary>Лог выполнения.</summary>
    [ObservableProperty]
    public partial string LogOutput { get; set; } = "";

    /// <summary>Прогресс batch-запуска (0-100).</summary>
    [ObservableProperty]
    public partial int Progress { get; set; }

    /// <summary>
    /// Все доступные тематические группы для UI-кнопок фильтра.
    /// Заполняется при discovery.
    /// </summary>
    public ObservableCollection<GroupFilterItem> AvailableGroups { get; } = [];

    // COMMANDS

    /// <summary>Запустить ВСЕ тесты.</summary>
    public IAsyncRelayCommand RunAllCommand { get; }

    /// <summary>Запустить только Unit.</summary>
    public IAsyncRelayCommand RunUnitCommand { get; }

    /// <summary>Запустить только Integration.</summary>
    public IAsyncRelayCommand RunIntegrationCommand { get; }

    /// <summary>Запустить только Benchmarks.</summary>
    public IAsyncRelayCommand RunBenchmarksCommand { get; }

    /// <summary>Запустить отфильтрованные тесты (по текущему фильтру группы/категории).</summary>
    public IAsyncRelayCommand RunFilteredCommand { get; }

    /// <summary>Запустить один тест по клику.</summary>
    public IAsyncRelayCommand<TestItemViewModel> RunSingleCommand { get; }

    /// <summary>Переключить фильтр группы (toggle).</summary>
    public IRelayCommand<string?> ToggleGroupFilterCommand { get; }

    /// <summary>Отменить текущий запуск.</summary>
    public IRelayCommand CancelCommand { get; }

    /// <summary>Сбросить все результаты.</summary>
    public IRelayCommand ResetCommand { get; }

    /// <summary>Очистить лог.</summary>
    public IRelayCommand ClearLogCommand { get; }

    /// <summary>Открыть test-config.json в редакторе по умолчанию.</summary>
    public IRelayCommand OpenConfigCommand { get; }

    /// <summary>Перезагрузить test-config.json из файла.</summary>
    public IRelayCommand ReloadConfigCommand { get; }

    // CTOR

    public TestRunnerViewModel()
    {
        _runner = new TestRunner(AppEntry.Services);
        _runner.TestStarting += OnTestStarting;
        _runner.TestCompleted += OnTestCompleted;

        RunAllCommand = new AsyncRelayCommand(
            () => RunBatchByCategoryAsync(null),
            () => !IsRunning);

        RunUnitCommand = new AsyncRelayCommand(
            () => RunBatchByCategoryAsync(TestCategory.Unit),
            () => !IsRunning);

        RunIntegrationCommand = new AsyncRelayCommand(
            () => RunBatchByCategoryAsync(TestCategory.Integration),
            () => !IsRunning);

        RunBenchmarksCommand = new AsyncRelayCommand(
            () => RunBatchByCategoryAsync(TestCategory.Benchmark),
            () => !IsRunning);

        RunFilteredCommand = new AsyncRelayCommand(
            RunFilteredAsync,
            () => !IsRunning);

        RunSingleCommand = new AsyncRelayCommand<TestItemViewModel>(
            RunSingleAsync,
            _ => !IsRunning);

        ToggleGroupFilterCommand = new RelayCommand<string?>(group =>
        {
            // Toggle: если та же группа — сбрасываем, иначе ставим
            SelectedGroup = SelectedGroup == group ? null : group;
        });

        CancelCommand = new RelayCommand(
            () => _runCts?.Cancel(),
            () => IsRunning);

        ResetCommand = new RelayCommand(ResetAll);
        ClearLogCommand = new RelayCommand(() =>
        {
            _logBuilder.Clear();
            LogOutput = "";
        });

        // ═══ CONFIG COMMANDS ═══
        OpenConfigCommand = new RelayCommand(() =>
        {
            TestConfig.OpenInEditor();
            AppendLog($"Opened config: {TestConfig.GetConfigPath()}");
        });

        ReloadConfigCommand = new RelayCommand(() =>
        {
            TestConfig.Reload();
            AppendLog("Reloaded test-config.json from disk.");
        });

        DiscoverTests();
        UpdateSummary();
    }

    partial void OnSelectedCategoryChanged(TestCategory? value) => ApplyFilter();
    partial void OnSelectedGroupChanged(string? value) => ApplyFilter();
    partial void OnSearchFilterChanged(string value) => ApplyFilter();

    // DISCOVERY

    private void DiscoverTests()
    {
        var ordered = TestDiscovery.GetAllOrdered();

        foreach (var descriptor in ordered)
            AllTests.Add(new TestItemViewModel(descriptor));

        // Собираем уникальные группы для кнопок-фильтров
        var groups = TestDiscovery.GetAllGroups();
        foreach (var group in groups.OrderBy(g => g, StringComparer.Ordinal))
        {
            var count = ordered.Count(t => t.Group == group);
            AvailableGroups.Add(new GroupFilterItem(group, count));
        }

        ApplyFilter();
        AppendLog($"Discovered {AllTests.Count} tests in {groups.Count} groups");
        AppendLog($"Config: {TestConfig.GetConfigPath()}");
    }

    // RUNNING

    /// <summary>Запускает тесты по категории (null = все).</summary>
    private async Task RunBatchByCategoryAsync(TestCategory? category)
    {
        var tests = category is not null
            ? AllTests.Where(t => t.Descriptor.Category == category.Value).ToList()
            : [.. AllTests];

        await RunBatchCoreAsync(tests, category?.ToString() ?? "All");
    }

    /// <summary>Запускает тесты по текущему фильтру (группа + категория + поиск).</summary>
    private async Task RunFilteredAsync()
    {
        var tests = FilteredTests.ToList();
        var label = BuildFilterLabel();
        await RunBatchCoreAsync(tests, label);
    }

    /// <summary>Общая логика запуска batch.</summary>
    private async Task RunBatchCoreAsync(IReadOnlyList<TestItemViewModel> tests, string label)
    {
        if (tests.Count == 0)
        {
            AppendLog("No tests to run.");
            return;
        }

        IsRunning = true;
        Progress = 0;
        _runCts = new CancellationTokenSource();

        AppendLog($"\n{new string('═', 60)}");
        AppendLog($"  Running {tests.Count} tests [{label}]...");
        AppendLog($"{new string('═', 60)}\n");

        try
        {
            var descriptors = tests.Select(t => t.Descriptor).ToList();
            int completed = 0;

            void OnProgress(TestDescriptor _, TestResult __)
            {
                var pct = (int)(Interlocked.Increment(ref completed) * 100.0 / descriptors.Count);
                Dispatcher.UIThread.Post(() => Progress = pct);
            }

            _runner.TestCompleted += OnProgress;
            try
            {
                await Task.Run(() => _runner.RunBatchAsync(descriptors, _runCts.Token));
            }
            finally
            {
                _runner.TestCompleted -= OnProgress;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Batch error: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
            Progress = 100;
            UpdateSummary();

            AppendLog($"\n{new string('═', 60)}");
            AppendLog($"  {Summary}");
            AppendLog($"{new string('═', 60)}\n");
        }
    }

    /// <summary>Запускает один тест по клику.</summary>
    private async Task RunSingleAsync(TestItemViewModel? item)
    {
        if (item is null) return;

        IsRunning = true;
        _runCts = new CancellationTokenSource();

        AppendLog($"\n▶ Running: {item.DisplayName}...");

        try
        {
            await Task.Run(() => _runner.RunAsync(item.Descriptor, _runCts.Token));
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
            UpdateSummary();
        }
    }

    // EVENT HANDLERS (background thread → UI thread)

    private void OnTestStarting(TestDescriptor descriptor)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var item = AllTests.FirstOrDefault(t => t.Descriptor.Id == descriptor.Id);
            if (item is not null)
            {
                item.State = TestRunState.Running;
                item.Duration = "";
                item.ErrorMessage = null;
            }
        });
    }

    private void OnTestCompleted(TestDescriptor descriptor, TestResult result)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var item = AllTests.FirstOrDefault(t => t.Descriptor.Id == descriptor.Id);
            if (item is not null)
            {
                item.State = result.State;
                item.Duration = result.DurationFormatted;
                item.ErrorMessage = result.ErrorMessage;
            }

            var icon = result.State switch
            {
                TestRunState.Passed => "✓",
                TestRunState.Failed => "✗",
                TestRunState.Skipped => "⊘",
                _ => "?",
            };

            var line = $"  {icon} [{descriptor.Group}] {descriptor.DisplayName} ({result.DurationFormatted})";
            if (result.ErrorMessage is not null)
                line += $" — {result.ErrorMessage}";

            AppendLog(line);
        });
    }

    // FILTER

    /// <summary>Применяет все три фильтра: категория + группа + текст.</summary>
    private void ApplyFilter()
    {
        var filtered = AllTests.AsEnumerable();

        // Фильтр по категории
        if (SelectedCategory is not null)
            filtered = filtered.Where(t => t.Descriptor.Category == SelectedCategory.Value);

        // Фильтр по тематической группе
        if (SelectedGroup is not null)
            filtered = filtered.Where(t => t.Descriptor.Group == SelectedGroup);

        // Фильтр по тексту
        if (!string.IsNullOrWhiteSpace(SearchFilter))
        {
            var search = SearchFilter;
            filtered = filtered.Where(t =>
                t.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                t.Descriptor.ClassName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                t.Descriptor.Group.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        FilteredTests = new ObservableCollection<TestItemViewModel>(filtered);

        // Обновляем IsSelected у GroupFilterItems
        foreach (var g in AvailableGroups)
            g.IsSelected = g.Name == SelectedGroup;
    }

    /// <summary>Строит строку-описание текущего фильтра для лога.</summary>
    private string BuildFilterLabel()
    {
        var parts = new List<string>(3);
        if (SelectedCategory is not null) parts.Add(SelectedCategory.Value.ToString());
        if (SelectedGroup is not null) parts.Add(SelectedGroup);
        if (!string.IsNullOrWhiteSpace(SearchFilter)) parts.Add($"\"{SearchFilter}\"");
        return parts.Count > 0 ? string.Join(" + ", parts) : "All";
    }

    // HELPERS

    private void ResetAll()
    {
        foreach (var test in AllTests)
        {
            test.State = TestRunState.NotRun;
            test.Duration = "";
            test.ErrorMessage = null;
        }
        Progress = 0;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int passed = AllTests.Count(t => t.State == TestRunState.Passed);
        int failed = AllTests.Count(t => t.State == TestRunState.Failed);
        int skipped = AllTests.Count(t => t.State == TestRunState.Skipped);
        int notRun = AllTests.Count(t => t.State == TestRunState.NotRun);
        int running = AllTests.Count(t => t.State == TestRunState.Running);

        var sb = new StringBuilder(64);
        if (passed > 0) sb.Append($"{passed} passed");
        if (failed > 0) { if (sb.Length > 0) sb.Append(", "); sb.Append($"{failed} failed"); }
        if (skipped > 0) { if (sb.Length > 0) sb.Append(", "); sb.Append($"{skipped} skipped"); }
        if (running > 0) { if (sb.Length > 0) sb.Append(", "); sb.Append($"{running} running"); }
        if (notRun > 0) { if (sb.Length > 0) sb.Append(", "); sb.Append($"{notRun} pending"); }

        Summary = sb.Length > 0 ? sb.ToString() : "No tests";
    }

    private readonly StringBuilder _logBuilder = new(4096);

    private void AppendLog(string line)
    {
        _logBuilder.AppendLine(line);
        LogOutput = _logBuilder.ToString();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _runCts?.Cancel();
            _runCts?.Dispose();
            _runner.TestStarting -= OnTestStarting;
            _runner.TestCompleted -= OnTestCompleted;
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// UI-модель одного теста в списке.
/// </summary>
public sealed partial class TestItemViewModel : ViewModelBase
{
    public TestDescriptor Descriptor { get; }

    public string DisplayName => Descriptor.DisplayName;
    public TestCategory Category => Descriptor.Category;
    public string Group => Descriptor.Group;
    public bool RequiresNetwork => Descriptor.RequiresNetwork;

    [ObservableProperty]
    public partial TestRunState State { get; set; } = TestRunState.NotRun;

    [ObservableProperty]
    public partial string Duration { get; set; } = "";

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    /// <summary>Иконка состояния.</summary>
    public string StateIcon => State switch
    {
        TestRunState.NotRun => "○",
        TestRunState.Running => "◉",
        TestRunState.Passed => "✓",
        TestRunState.Failed => "✗",
        TestRunState.Skipped => "⊘",
        _ => "?",
    };

    /// <summary>Цвет состояния (hex).</summary>
    public string StateColor => State switch
    {
        TestRunState.Passed => "#4CAF50",
        TestRunState.Failed => "#F44336",
        TestRunState.Running => "#2196F3",
        TestRunState.Skipped => "#9E9E9E",
        _ => "#757575",
    };

    /// <summary>Badge категории.</summary>
    public string CategoryBadge => Category switch
    {
        TestCategory.Unit => "UNIT",
        TestCategory.Integration => "INT",
        TestCategory.Benchmark => "BENCH",
        _ => "?",
    };

    public TestItemViewModel(TestDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    partial void OnStateChanged(TestRunState value)
    {
        OnPropertyChanged(nameof(StateIcon));
        OnPropertyChanged(nameof(StateColor));
    }
}

/// <summary>
/// Элемент фильтра по тематической группе. Используется в UI как toggle-кнопка.
/// </summary>
public sealed partial class GroupFilterItem : ViewModelBase
{
    /// <summary>Имя группы: "NToken", "SigCipher" и т.д.</summary>
    public string Name { get; }

    /// <summary>Количество тестов в группе.</summary>
    public int Count { get; }

    /// <summary>Текст для кнопки: "NToken (5)".</summary>
    public string Label => $"{Name} ({Count})";

    /// <summary>Выбрана ли эта группа в фильтре.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public GroupFilterItem(string name, int count)
    {
        Name = name;
        Count = count;
    }
}