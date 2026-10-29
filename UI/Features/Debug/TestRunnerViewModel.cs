using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Avalonia.Collections;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LMP.Tests.Framework;
using LMP.UI.Helpers;

namespace LMP.UI.Features.Debug;

/// <summary>
/// Статусный фильтр для быстрого отбора тестов по результату выполнения.
/// </summary>
public enum TestStateFilter : byte
{
    All = 0,
    FailedOnly = 1,
    PassedOnly = 2,
    SkippedOnly = 3,
    PendingOnly = 4
}

/// <summary>
/// Структура отложенного обновления состояния теста для исключения блокировок UI-потока.
/// </summary>
internal readonly struct TestStateUpdate
{
    public readonly string TestId;
    public readonly TestRunState State;
    public readonly string Duration;
    public readonly string? ErrorMessage;
    public readonly bool IsStarting;

    public TestStateUpdate(string testId, TestRunState state, string duration, string? errorMessage, bool isStarting)
    {
        TestId = testId;
        State = state;
        Duration = duration;
        ErrorMessage = errorMessage;
        IsStarting = isStarting;
    }
}

/// <summary>
/// ViewModel вкладки тестов с пакетным обновлением состояния UI, полным отсутствием
/// фризов диспетчера и выводом результатов исключительно в централизованный системный лог.
/// </summary>
public sealed partial class TestRunnerViewModel : ViewModelBase
{
    private readonly TestRunner _runner;
    private readonly Dictionary<string, TestItemViewModel> _testById = new(64, StringComparer.Ordinal);
    private readonly ConcurrentQueue<TestStateUpdate> _pendingUpdates = new();

    private CancellationTokenSource? _runCts;
    private readonly Action _flushUiAction;
    private readonly Timer _uiFlushTimer;
    private long _lastScrollTicks;
    private long _lastUiFlushTicks;
    private int _uiUpdateScheduled;
    private int _activeBatchTotal;
    private int _activeBatchCompleted;
    private bool _isBatchRunning;
    private bool _isDiscovered;

    public event Action<int>? ScrollToIndexRequested;

    public List<TestItemViewModel> AllTests { get; } = new(64);
    public AvaloniaList<TestItemViewModel> FilteredTests { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllCategoriesSelected))]
    [NotifyPropertyChangedFor(nameof(IsUnitCategorySelected))]
    [NotifyPropertyChangedFor(nameof(IsIntegrationCategorySelected))]
    [NotifyPropertyChangedFor(nameof(IsBenchmarkCategorySelected))]
    [NotifyPropertyChangedFor(nameof(IsDiagnosticCategorySelected))]
    [NotifyPropertyChangedFor(nameof(IsActionCategorySelected))]
    public partial TestCategory? SelectedCategory { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllGroupsSelected))]
    public partial string? SelectedGroup { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStateAllSelected))]
    [NotifyPropertyChangedFor(nameof(IsStateFailedSelected))]
    [NotifyPropertyChangedFor(nameof(IsStatePassedSelected))]
    [NotifyPropertyChangedFor(nameof(IsStateSkippedSelected))]
    [NotifyPropertyChangedFor(nameof(IsStatePendingSelected))]
    public partial TestStateFilter StateFilter { get; set; } = TestStateFilter.All;

    [ObservableProperty]
    public partial string SearchFilter { get; set; } = "";

    [ObservableProperty]
    public partial bool ErrorsOnTop { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunUnitCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunIntegrationCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunBenchmarksCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunDiagnosticsCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunActionsCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunFilteredCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunSingleCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial string ShowingCountText { get; set; } = "0 / 0";
    [ObservableProperty] public partial int Progress { get; set; }

    [ObservableProperty] public partial int FailedCount { get; set; }
    [ObservableProperty] public partial int PassedCount { get; set; }
    [ObservableProperty] public partial int SkippedCount { get; set; }
    [ObservableProperty] public partial int PendingCount { get; set; }

    public bool IsAllGroupsSelected => SelectedGroup is null;
    public bool IsAllCategoriesSelected => SelectedCategory is null;
    public bool IsUnitCategorySelected => SelectedCategory == TestCategory.Unit;
    public bool IsIntegrationCategorySelected => SelectedCategory == TestCategory.Integration;
    public bool IsBenchmarkCategorySelected => SelectedCategory == TestCategory.Benchmark;
    public bool IsDiagnosticCategorySelected => SelectedCategory == TestCategory.Diagnostic;
    public bool IsActionCategorySelected => SelectedCategory == TestCategory.Action;

    public bool IsStateAllSelected => StateFilter == TestStateFilter.All;
    public bool IsStateFailedSelected => StateFilter == TestStateFilter.FailedOnly;
    public bool IsStatePassedSelected => StateFilter == TestStateFilter.PassedOnly;
    public bool IsStateSkippedSelected => StateFilter == TestStateFilter.SkippedOnly;
    public bool IsStatePendingSelected => StateFilter == TestStateFilter.PendingOnly;

    public ObservableCollection<GroupFilterItem> AvailableGroups { get; } = [];

    // COMMANDS

    public IAsyncRelayCommand RunAllCommand { get; }
    public IAsyncRelayCommand RunUnitCommand { get; }
    public IAsyncRelayCommand RunIntegrationCommand { get; }
    public IAsyncRelayCommand RunBenchmarksCommand { get; }
    public IAsyncRelayCommand RunDiagnosticsCommand { get; }
    public IAsyncRelayCommand RunActionsCommand { get; }
    public IAsyncRelayCommand RunFilteredCommand { get; }
    public IAsyncRelayCommand<TestItemViewModel> RunSingleCommand { get; }
    public IRelayCommand<TestItemViewModel> SelectTestCommand { get; }
    public IRelayCommand<string?> ToggleGroupFilterCommand { get; }
    public IRelayCommand<TestCategory?> ToggleCategoryFilterCommand { get; }
    public IRelayCommand<TestStateFilter> SetStateFilterCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand ResetCommand { get; }
    public IRelayCommand OpenConfigCommand { get; }
    public IRelayCommand ReloadConfigCommand { get; }

    public TestRunnerViewModel()
    {
        _flushUiAction = ProcessPendingUpdatesOnUi;
        _uiFlushTimer = new Timer(static state =>
        {
            var self = (TestRunnerViewModel)state!;
            Dispatcher.UIThread.Post(self._flushUiAction, DispatcherPriority.Background);
        }, this, Timeout.Infinite, Timeout.Infinite);

        _runner = new TestRunner(AppEntry.Services);
        _runner.TestStarting += OnTestStarting;
        _runner.TestCompleted += OnTestCompleted;

        RunAllCommand = new AsyncRelayCommand(() => RunBatchByCategoryAsync(null), () => !IsRunning);
        RunUnitCommand = new AsyncRelayCommand(() => RunBatchByCategoryAsync(TestCategory.Unit), () => !IsRunning);
        RunIntegrationCommand = new AsyncRelayCommand(() => RunBatchByCategoryAsync(TestCategory.Integration), () => !IsRunning);
        RunBenchmarksCommand = new AsyncRelayCommand(() => RunBatchByCategoryAsync(TestCategory.Benchmark), () => !IsRunning);
        RunDiagnosticsCommand = new AsyncRelayCommand(() => RunBatchByCategoryAsync(TestCategory.Diagnostic), () => !IsRunning);
        RunActionsCommand = new AsyncRelayCommand(() => RunBatchByCategoryAsync(TestCategory.Action), () => !IsRunning);
        RunFilteredCommand = new AsyncRelayCommand(RunFilteredAsync, () => !IsRunning);
        RunSingleCommand = new AsyncRelayCommand<TestItemViewModel>(RunSingleAsync, _ => !IsRunning);
        SelectTestCommand = new RelayCommand<TestItemViewModel>(item =>
        {
            if (item is not null && !IsRunning)
                _ = RunSingleAsync(item);
        });

        ToggleGroupFilterCommand = new RelayCommand<string?>(group => SelectedGroup = SelectedGroup == group ? null : group);
        ToggleCategoryFilterCommand = new RelayCommand<TestCategory?>(cat => SelectedCategory = SelectedCategory == cat ? null : cat);
        SetStateFilterCommand = new RelayCommand<TestStateFilter>(filter => StateFilter = StateFilter == filter ? TestStateFilter.All : filter);

        CancelCommand = new RelayCommand(() => _runCts?.Cancel(), () => IsRunning);
        ResetCommand = new RelayCommand(ResetAll);

        OpenConfigCommand = new RelayCommand(() =>
        {
            TestConfig.OpenInEditor();
            Log.Info($"[TestRunner] Opened config: {TestConfig.GetConfigPath()}");
        });

        ReloadConfigCommand = new RelayCommand(() =>
        {
            TestConfig.Reload();
            Log.Info("[TestRunner] Reloaded test-config.json from disk.");
        });

        EnsureDiscovered();
    }

    /// <summary>
    /// Применяет фильтры командной строки к UI-раннеру и при наличии флага <c>--autorun</c> автоматически запускает отфильтрованные тесты.
    /// </summary>
    public void ApplyCliArguments(string[] args)
    {
        if (args.Length == 0) return;

        bool autoRun = false;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.IsNullOrEmpty(arg)) continue;

            if (string.Equals(arg, "--autorun", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "--run", StringComparison.OrdinalIgnoreCase))
            {
                autoRun = true;
            }
            else if (arg.StartsWith("--test-group=", StringComparison.OrdinalIgnoreCase))
            {
                var rawGroup = arg[13..].Trim();
                if (!string.IsNullOrEmpty(rawGroup))
                    SelectedGroup = TestGroups.Normalize(rawGroup);
            }
            else if (arg.StartsWith("--test-category=", StringComparison.OrdinalIgnoreCase))
            {
                var rawCat = arg[16..].Trim();
                if (Enum.TryParse<TestCategory>(rawCat, ignoreCase: true, out var cat))
                    SelectedCategory = cat;
            }
            else if (arg.StartsWith("--test-filter=", StringComparison.OrdinalIgnoreCase))
            {
                SearchFilter = arg[14..].Trim();
            }
        }

        if (autoRun)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (RunFilteredCommand.CanExecute(null))
                    _ = RunFilteredCommand.ExecuteAsync(null);
            }, DispatcherPriority.Background);
        }
    }

    partial void OnSelectedCategoryChanged(TestCategory? value) => ApplyFilter();
    partial void OnSelectedGroupChanged(string? value) => ApplyFilter();
    partial void OnStateFilterChanged(TestStateFilter value) => ApplyFilter();
    partial void OnSearchFilterChanged(string value) => ApplyFilter();
    partial void OnErrorsOnTopChanged(bool value) => ApplyFilter();

    public void EnsureDiscovered()
    {
        if (_isDiscovered) return;
        _isDiscovered = true;

        var ordered = TestDiscovery.GetAllOrdered();
        var uniqueGroups = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < ordered.Count; i++)
        {
            var descriptor = ordered[i];
            var item = new TestItemViewModel(descriptor, i);
            AllTests.Add(item);
            _testById[descriptor.Id] = item;
            uniqueGroups.Add(descriptor.Group);
        }

        var sortedGroups = new List<string>(uniqueGroups);
        sortedGroups.Sort(StringComparer.Ordinal);

        for (int i = 0; i < sortedGroups.Count; i++)
            AvailableGroups.Add(new GroupFilterItem(sortedGroups[i], 0));

        ApplyFilter();
        UpdateSummary();
        Log.Info($"[TestRunner] Discovered {AllTests.Count} tests across {sortedGroups.Count} groups.");
    }

    private async Task RunBatchByCategoryAsync(TestCategory? category)
    {
        var list = new List<TestItemViewModel>(AllTests.Count);
        for (int i = 0; i < AllTests.Count; i++)
        {
            var t = AllTests[i];
            if (category is null)
            {
                if (t.Descriptor.Category != TestCategory.Action)
                    list.Add(t);
            }
            else if (t.Descriptor.Category == category.Value)
            {
                list.Add(t);
            }
        }

        await RunBatchCoreAsync(list, category?.ToString() ?? "All Tests").ConfigureAwait(false);
    }

    private async Task RunFilteredAsync()
    {
        var list = new List<TestItemViewModel>(FilteredTests.Count);
        for (int i = 0; i < FilteredTests.Count; i++)
            list.Add(FilteredTests[i]);

        await RunBatchCoreAsync(list, BuildFilterLabel()).ConfigureAwait(false);
    }

    private async Task RunBatchCoreAsync(List<TestItemViewModel> tests, string label)
    {
        if (tests.Count == 0)
        {
            Log.Info("[TestRunner] No items match current filter.");
            return;
        }

        SetIsRunningOnUI(true);
        Progress = 0;
        _activeBatchTotal = tests.Count;
        _activeBatchCompleted = 0;
        _isBatchRunning = true;
        _runCts = new CancellationTokenSource();

        var header = $"\n── Running {tests.Count} items [{label}] ──";
        Log.Info(header);

        try
        {
            var descriptors = new TestDescriptor[tests.Count];
            for (int i = 0; i < tests.Count; i++)
                descriptors[i] = tests[i].Descriptor;

            await _runner.RunBatchAsync(descriptors, _runCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"[TestRunner] Batch error: {ex.Message}");
        }
        finally
        {
            _runCts?.Dispose();
            _runCts = null;

            Dispatcher.UIThread.Post(() =>
            {
                ProcessPendingUpdatesOnUi();
                IsRunning = false;
                Progress = 100;
                _isBatchRunning = false;
                _activeBatchTotal = 0;
                _activeBatchCompleted = 0;
                UpdateSummary();
                ApplyFilter();
                var footer = $"── Finished: {Summary} ──";
                Log.Info(footer);
            });
        }
    }

    private async Task RunSingleAsync(TestItemViewModel? item)
    {
        if (item is null) return;

        SetIsRunningOnUI(true);
        _runCts = new CancellationTokenSource();
        _isBatchRunning = false;
        _activeBatchTotal = 1;
        _activeBatchCompleted = 0;

        var header = $"\n▶ [{item.Category}] {item.DisplayName}";
        Log.Info(header);

        try
        {
            await _runner.RunAsync(item.Descriptor, _runCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"[TestRunner] Error: {ex.Message}");
        }
        finally
        {
            _runCts?.Dispose();
            _runCts = null;

            Dispatcher.UIThread.Post(() =>
            {
                ProcessPendingUpdatesOnUi();
                IsRunning = false;
                Progress = 100;
                _activeBatchTotal = 0;
                _activeBatchCompleted = 0;
                UpdateSummary();
                ApplyFilter();
            });
        }
    }

    private void SetIsRunningOnUI(bool value)
    {
        if (Dispatcher.UIThread.CheckAccess())
            IsRunning = value;
        else
            Dispatcher.UIThread.Post(() => IsRunning = value);
    }

    private void OnTestStarting(TestDescriptor descriptor)
    {
        _pendingUpdates.Enqueue(new TestStateUpdate(descriptor.Id, TestRunState.Running, "", null, isStarting: true));
        ScheduleUiFlush();
    }

    private void OnTestCompleted(TestDescriptor descriptor, TestResult result)
    {
        Interlocked.Increment(ref _activeBatchCompleted);

        var icon = result.State switch
        {
            TestRunState.Passed => "✓",
            TestRunState.Failed => "✗",
            TestRunState.Skipped => "⊘",
            _ => "?",
        };

        string line = result.ErrorMessage is not null
            ? $"  {icon} [{descriptor.Group}] {descriptor.DisplayName} ({result.DurationFormatted}) — {result.ErrorMessage}"
            : $"  {icon} [{descriptor.Group}] {descriptor.DisplayName} ({result.DurationFormatted})";

        if (result.State == TestRunState.Failed)
            Log.Error(line);
        else if (result.State == TestRunState.Skipped)
            Log.Warn(line);
        else
            Log.Info(line);

        _pendingUpdates.Enqueue(new TestStateUpdate(descriptor.Id, result.State, result.DurationFormatted, result.ErrorMessage, isStarting: false));
        ScheduleUiFlush();
    }

    private void ScheduleUiFlush()
    {
        if (Interlocked.CompareExchange(ref _uiUpdateScheduled, 1, 0) != 0)
            return;

        long now = Stopwatch.GetTimestamp();
        long last = Volatile.Read(ref _lastUiFlushTicks);
        double elapsedMs = (now - last) * 1000.0 / Stopwatch.Frequency;

        if (_isBatchRunning && elapsedMs < 33.0)
        {
            int delayMs = Math.Clamp(33 - (int)elapsedMs, 10, 33);
            try { _uiFlushTimer.Change(delayMs, Timeout.Infinite); }
            catch (ObjectDisposedException) { }
        }
        else
        {
            Dispatcher.UIThread.Post(ProcessPendingUpdatesOnUi, DispatcherPriority.Background);
        }
    }

    private void ProcessPendingUpdatesOnUi()
    {
        Volatile.Write(ref _lastUiFlushTicks, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref _uiUpdateScheduled, 0);

        bool stateChanged = false;
        long now = Stopwatch.GetTimestamp();

        while (_pendingUpdates.TryDequeue(out var update))
        {
            if (_testById.TryGetValue(update.TestId, out var item))
            {
                item.State = update.State;
                item.Duration = update.Duration;
                item.ErrorMessage = update.ErrorMessage;
                stateChanged = true;

                // Автоскролл активен исключительно при пакетном прогоне, исключая смещение при ручном клике
                if (update.IsStarting && _isBatchRunning)
                {
                    double elapsedMs = (now - _lastScrollTicks) * 1000.0 / Stopwatch.Frequency;
                    if (elapsedMs > 150.0)
                    {
                        _lastScrollTicks = now;
                        int index = FilteredTests.IndexOf(item);
                        if (index >= 0)
                            ScrollToIndexRequested?.Invoke(index);
                    }
                }
            }
        }

        if (stateChanged)
        {
            int total = _activeBatchTotal;
            if (total > 0)
            {
                int completed = Volatile.Read(ref _activeBatchCompleted);
                Progress = Math.Clamp((int)(completed * 100.0 / total), 0, 100);
            }

            UpdateSummary();
        }
    }

    private void ApplyFilter()
    {
        var category = SelectedCategory;
        var group = SelectedGroup;
        var state = StateFilter;
        var search = SearchFilter.AsSpan().Trim();
        bool hasSearch = !search.IsEmpty;

        var matched = new List<TestItemViewModel>(AllTests.Count);

        for (int i = 0; i < AllTests.Count; i++)
        {
            var t = AllTests[i];

            if (category.HasValue && t.Category != category.Value)
                continue;

            if (group is not null && !string.Equals(t.Group, group, StringComparison.Ordinal))
                continue;

            if (state == TestStateFilter.FailedOnly && t.State != TestRunState.Failed) continue;
            if (state == TestStateFilter.PassedOnly && t.State != TestRunState.Passed) continue;
            if (state == TestStateFilter.SkippedOnly && t.State != TestRunState.Skipped) continue;
            if (state == TestStateFilter.PendingOnly && t.State != TestRunState.NotRun) continue;

            if (hasSearch)
            {
                if (!t.DisplayName.AsSpan().Contains(search, StringComparison.OrdinalIgnoreCase) &&
                    !t.Descriptor.ClassName.AsSpan().Contains(search, StringComparison.OrdinalIgnoreCase) &&
                    !t.Group.AsSpan().Contains(search, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            matched.Add(t);
        }

        if (ErrorsOnTop)
        {
            matched.Sort(static (a, b) =>
            {
                int rankA = a.State == TestRunState.Failed ? 0 : (a.State == TestRunState.Running ? 1 : 2);
                int rankB = b.State == TestRunState.Failed ? 0 : (b.State == TestRunState.Running ? 1 : 2);
                int cmp = rankA.CompareTo(rankB);
                if (cmp != 0) return cmp;

                return a.OriginalIndex.CompareTo(b.OriginalIndex);
            });
        }

        FilteredTests.Clear();
        FilteredTests.AddRange(matched);

        for (int i = 0; i < AvailableGroups.Count; i++)
        {
            var g = AvailableGroups[i];
            int countInGroup = 0;

            for (int j = 0; j < AllTests.Count; j++)
            {
                var t = AllTests[j];
                if (category.HasValue && t.Category != category.Value) continue;
                if (state == TestStateFilter.FailedOnly && t.State != TestRunState.Failed) continue;
                if (state == TestStateFilter.PassedOnly && t.State != TestRunState.Passed) continue;
                if (state == TestStateFilter.SkippedOnly && t.State != TestRunState.Skipped) continue;
                if (state == TestStateFilter.PendingOnly && t.State != TestRunState.NotRun) continue;

                if (hasSearch)
                {
                    if (!t.DisplayName.AsSpan().Contains(search, StringComparison.OrdinalIgnoreCase) &&
                        !t.Descriptor.ClassName.AsSpan().Contains(search, StringComparison.OrdinalIgnoreCase) &&
                        !t.Group.AsSpan().Contains(search, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                if (string.Equals(t.Group, g.Name, StringComparison.Ordinal))
                    countInGroup++;
            }

            g.Count = countInGroup;
            g.IsSelected = string.Equals(g.Name, group, StringComparison.Ordinal);
        }

        ShowingCountText = $"{matched.Count} / {AllTests.Count}";
    }

    private string BuildFilterLabel()
    {
        var sb = new StringBuilder(32);
        if (SelectedCategory is not null) sb.Append(SelectedCategory.Value);
        if (SelectedGroup is not null)
        {
            if (sb.Length > 0) sb.Append(" + ");
            sb.Append(SelectedGroup);
        }
        if (StateFilter != TestStateFilter.All)
        {
            if (sb.Length > 0) sb.Append(" + ");
            sb.Append(StateFilter);
        }
        return sb.Length > 0 ? sb.ToString() : "Filtered";
    }

    private void ResetAll()
    {
        for (int i = 0; i < AllTests.Count; i++)
        {
            var test = AllTests[i];
            test.State = TestRunState.NotRun;
            test.Duration = "";
            test.ErrorMessage = null;
        }
        Progress = 0;
        UpdateSummary();
        ApplyFilter();
    }

    private void UpdateSummary()
    {
        int passed = 0, failed = 0, skipped = 0, notRun = 0, running = 0;

        for (int i = 0; i < AllTests.Count; i++)
        {
            switch (AllTests[i].State)
            {
                case TestRunState.Passed: passed++; break;
                case TestRunState.Failed: failed++; break;
                case TestRunState.Skipped: skipped++; break;
                case TestRunState.Running: running++; break;
                default: notRun++; break;
            }
        }

        FailedCount = failed;
        PassedCount = passed;
        SkippedCount = skipped;
        PendingCount = notRun;

        var sb = new StringBuilder(64);
        if (passed > 0) sb.Append(passed).Append(" passed");
        if (failed > 0) { if (sb.Length > 0) sb.Append(" · "); sb.Append(failed).Append(" failed"); }
        if (skipped > 0) { if (sb.Length > 0) sb.Append(" · "); sb.Append(skipped).Append(" skipped"); }
        if (running > 0) { if (sb.Length > 0) sb.Append(" · "); sb.Append(running).Append(" running"); }
        if (notRun > 0) { if (sb.Length > 0) sb.Append(" · "); sb.Append(notRun).Append(" pending"); }

        Summary = sb.Length > 0 ? sb.ToString() : "No tests";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _uiFlushTimer.Dispose();
            _runCts?.Cancel();
            _runCts?.Dispose();
            _runner.TestStarting -= OnTestStarting;
            _runner.TestCompleted -= OnTestCompleted;
        }
        base.Dispose(disposing);
    }
}

public sealed partial class TestItemViewModel : ObservableObject
{
    public TestDescriptor Descriptor { get; }
    public int OriginalIndex { get; }

    public string DisplayName => Descriptor.DisplayName;
    public TestCategory Category => Descriptor.Category;
    public string Group => Descriptor.Group;
    public bool RequiresNetwork => Descriptor.RequiresNetwork;
    public bool IsAction => Descriptor.IsAction;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateIcon))]
    [NotifyPropertyChangedFor(nameof(IsPassed))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsSkipped))]
    public partial TestRunState State { get; set; } = TestRunState.NotRun;

    [ObservableProperty] public partial string Duration { get; set; } = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasError))] public partial string? ErrorMessage { get; set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool IsPassed => State == TestRunState.Passed;
    public bool IsFailed => State == TestRunState.Failed;
    public bool IsRunning => State == TestRunState.Running;
    public bool IsSkipped => State == TestRunState.Skipped;

    public string StateIcon => State switch
    {
        TestRunState.NotRun => IsAction ? "⚡" : "○",
        TestRunState.Running => "◉",
        TestRunState.Passed => "✓",
        TestRunState.Failed => "✗",
        TestRunState.Skipped => "⊘",
        _ => "?",
    };

    public string CategoryBadge => Category switch
    {
        TestCategory.Unit => "UNIT",
        TestCategory.Integration => "INT",
        TestCategory.Benchmark => "BENCH",
        TestCategory.E2E => "E2E",
        TestCategory.Diagnostic => "DIAG",
        TestCategory.Action => "ACT",
        _ => "?",
    };

    public TestItemViewModel(TestDescriptor descriptor, int originalIndex)
    {
        Descriptor = descriptor;
        OriginalIndex = originalIndex;
    }
}

public sealed partial class GroupFilterItem : ObservableObject
{
    public string Name { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    public partial int Count { get; set; }

    public string Label => $"{Name} ({Count})";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public GroupFilterItem(string name, int count)
    {
        Name = name;
        Count = count;
    }
}