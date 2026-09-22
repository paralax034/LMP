using System.Collections.ObjectModel;
using Avalonia.Threading;
using LMP.Core.Youtube.Search;

namespace LMP.UI.Dialogs;

public enum MergeAction { Skip, Merge, Duplicate }

public sealed record SyncDecision(PlaylistSearchResult Playlist, MergeAction Action);

public sealed record SyncActionOption(MergeAction Action, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed partial class SyncSelectionViewModel : ViewModelBase
{
    private readonly List<SyncItemViewModel> _allItems = [];
    private readonly List<SyncItemViewModel> _conflictingItems = [];
    private readonly List<SyncItemViewModel> _nonConflictingItems = [];

    // Флаг для предотвращения рекурсии при смене шаблона
    private bool _suppressTemplateSync;
    private readonly DispatcherTimer _searchDebounceTimer;

    public ObservableCollection<SyncItemViewModel> Items { get; } = [];

    // Глобальные шаблоны (замена Toggles)

    public List<SyncActionOption> ConflictTemplates { get; }
    public List<SyncActionOption> NewTemplates { get; }

    [ObservableProperty]
    public partial SyncActionOption SelectedConflictTemplate { get; set; }

    [ObservableProperty]
    public partial SyncActionOption SelectedNewTemplate { get; set; }

    // Статистика

    [ObservableProperty]
    public partial string TotalSummary { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedSummary { get; set; } = "";

    [ObservableProperty]
    public partial string SearchQuery { get; set; } = "";

    // Команды

    public IRelayCommand ConfirmCommand { get; }
    public IRelayCommand CancelCommand { get; }

    /// <summary>
    /// Callback для закрытия диалога с результатом.
    /// </summary>
    public Action<List<SyncDecision>>? OnResult { get; set; }

    public SyncSelectionViewModel(
        IEnumerable<PlaylistSearchResult> playlists,
        ISet<string> existingLocalNames,
        IReadOnlyDictionary<string, int>? trackCounts = null)
    {
        var L = LocalizationService.Instance;

        // Инициализация списков шаблонов
        ConflictTemplates =
        [
            new SyncActionOption(MergeAction.Merge, L["Sync_Action_Merge"]),
            new SyncActionOption(MergeAction.Duplicate, L["Sync_Action_CreateNew"]),
            new SyncActionOption(MergeAction.Skip, L["Sync_Action_Skip"])
        ];

        NewTemplates =
        [
            new SyncActionOption(MergeAction.Duplicate, L["Sync_Action_Import"]),
            new SyncActionOption(MergeAction.Skip, L["Sync_Action_Skip"])
        ];
        SelectedConflictTemplate = ConflictTemplates[0];
        SelectedNewTemplate = NewTemplates[0];

        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchDebounceTimer.Tick += (s, e) =>
        {
            _searchDebounceTimer.Stop();
            ApplyFilter();
        };

        // Создание элементов
        foreach (var p in playlists)
        {
            var hasConflict = existingLocalNames.Contains(p.Title);
            int trackCount = 0;
            trackCounts?.TryGetValue(p.Id.Value, out trackCount);

            var item = new SyncItemViewModel(p, hasConflict, trackCount);

            // Сразу применяем дефолтный шаблон к элементу
            item.SelectedOption = hasConflict
                ? item.AvailableActions.First(a => a.Action == SelectedConflictTemplate.Action)
                : item.AvailableActions.First(a => a.Action == SelectedNewTemplate.Action);

            item.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SyncItemViewModel.SelectedOption))
                {
                    UpdateSelectedSummary();
                    SyncTemplatesFromItems();
                }
            };

            _allItems.Add(item);
            Items.Add(item);

            if (hasConflict) _conflictingItems.Add(item);
            else _nonConflictingItems.Add(item);
        }

        // Команды
        ConfirmCommand = new RelayCommand(() =>
        {
            var result = _allItems.Where(x => x.SelectedAction != MergeAction.Skip)
                .Select(x => new SyncDecision(x.Original, x.SelectedAction))
                .ToList();
            OnResult?.Invoke(result);
        });

        CancelCommand = new RelayCommand(() =>
        {
            OnResult?.Invoke([]);
        });

        ApplyFilter();
        UpdateTotalSummary();
        UpdateSelectedSummary();
    }

    partial void OnSelectedConflictTemplateChanged(SyncActionOption value)
    {
        if (_suppressTemplateSync || value == null) return;
        _suppressTemplateSync = true;
        foreach (var item in _conflictingItems)
        {
            item.SelectedOption = item.AvailableActions.First(a => a.Action == value.Action);
        }
        _suppressTemplateSync = false;
        UpdateSelectedSummary();
    }

    partial void OnSelectedNewTemplateChanged(SyncActionOption value)
    {
        if (_suppressTemplateSync || value == null) return;
        _suppressTemplateSync = true;
        foreach (var item in _nonConflictingItems)
        {
            item.SelectedOption = item.AvailableActions.First(a => a.Action == value.Action);
        }
        _suppressTemplateSync = false;
        UpdateSelectedSummary();
    }

    partial void OnSearchQueryChanged(string value)
    {
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    /// <summary>
    /// Если пользователь вручную поменял элемент, проверяем, не сбился ли шаблон.
    /// (Если все элементы группы одинаковые - ставим этот шаблон, иначе ничего не делаем).
    /// </summary>
    private void SyncTemplatesFromItems()
    {
        if (_suppressTemplateSync) return;
        _suppressTemplateSync = true;

        if (_conflictingItems.Count > 0)
        {
            var firstAction = _conflictingItems[0].SelectedAction;
            if (_conflictingItems.All(x => x.SelectedAction == firstAction))
            {
                SelectedConflictTemplate = ConflictTemplates.First(x => x.Action == firstAction);
            }
        }

        if (_nonConflictingItems.Count > 0)
        {
            var firstAction = _nonConflictingItems[0].SelectedAction;
            if (_nonConflictingItems.All(x => x.SelectedAction == firstAction))
            {
                SelectedNewTemplate = NewTemplates.First(x => x.Action == firstAction);
            }
        }

        _suppressTemplateSync = false;
    }

    private void UpdateTotalSummary()
    {
        var totalPlaylists = _allItems.Count;
        var totalConflicts = _conflictingItems.Count;
        var totalTracks = _allItems.Sum(x => x.TrackCount);

        var parts = new List<string>(3) { SL.GetPlural("Library_PlaylistWord", totalPlaylists) };
        if (totalConflicts > 0) parts.Add(string.Format(SL["Sync_ConflictsCount"], totalConflicts));
        if (totalTracks > 0) parts.Add($"~{SL.GetPlural("Library_TrackWord", totalTracks)}");

        TotalSummary = string.Join(" • ", parts);
    }

    private void UpdateSelectedSummary()
    {
        var selectedItems = _allItems.Where(x => x.SelectedAction != MergeAction.Skip).ToList();
        var selectedCount = selectedItems.Count;
        var selectedConflicts = selectedItems.Count(x => x.HasConflict);
        var selectedNonConflicts = selectedCount - selectedConflicts;
        var selectedTracks = selectedItems.Sum(x => x.TrackCount);

        if (selectedCount == 0)
        {
            SelectedSummary = SL["Sync_NoneSelected"] ?? "0 выбрано";
            return;
        }

        var main = string.Format(SL["Sync_SelectedDetails"], selectedCount, selectedConflicts, selectedNonConflicts);
        SelectedSummary = selectedTracks > 0 ? $"{main} • ~{SL.GetPlural("Library_TrackWord", selectedTracks)}" : main;
    }

    private void ApplyFilter()
    {
        var query = SearchQuery?.Trim() ?? "";
        List<SyncItemViewModel> desired;

        if (string.IsNullOrEmpty(query))
        {
            desired = [.. _allItems.OrderByDescending(x => x.HasConflict)];
            foreach (var item in _allItems) item.IsHighlighted = false;
        }
        else
        {
            desired = [.. _allItems.OrderByDescending(x => IsMatch(x, query)).ThenByDescending(x => x.HasConflict)];
            foreach (var item in _allItems) item.IsHighlighted = IsMatch(item, query);
        }

        for (int i = 0; i < desired.Count; i++)
        {
            int currentIndex = Items.IndexOf(desired[i]);
            if (currentIndex != i && currentIndex >= 0)
                Items.Move(currentIndex, i);
        }
    }

    private static bool IsMatch(SyncItemViewModel item, string query)
    {
        return item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrEmpty(item.Author) && item.Author.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _searchDebounceTimer.Stop();
        }
        base.Dispose(disposing);
    }
}

public sealed partial class SyncItemViewModel : ObservableObject
{
    public PlaylistSearchResult Original { get; }
    public string PlaylistUrl => Original.Url;
    public bool HasConflict { get; }
    public string Name => Original.Title;
    public string Author => Original.Author?.ChannelTitle ?? "";
    public string? ThumbnailUrl => Original.Thumbnails.Count > 0 ? Original.Thumbnails[0].Url : null;
    public bool HasThumbnail => !string.IsNullOrEmpty(ThumbnailUrl);
    public int TrackCount { get; }
    public bool HasTrackCount => TrackCount > 0;

    public string FormattedTrackCount => HasTrackCount
        ? LocalizationService.Instance.GetPlural("Playlist_TracksCount", TrackCount) : "";

    [ObservableProperty]
    public partial bool IsHighlighted { get; set; }
    public List<SyncActionOption> AvailableActions { get; }

    [ObservableProperty]
    public partial SyncActionOption? SelectedOption { get; set; }

    public MergeAction SelectedAction => SelectedOption?.Action ?? MergeAction.Skip;
    public static LocalizationService L => LocalizationService.Instance;

    /// <summary>
    /// Флаг доступности стратегии слияния (только для конфликтных элементов).
    /// </summary>
    public bool CanMerge => HasConflict;

    /// <summary>
    /// Активна ли стратегия слияния.
    /// </summary>
    public bool IsMergeSelected => SelectedAction == MergeAction.Merge;

    /// <summary>
    /// Активна ли стратегия создания копии / прямого импорта.
    /// </summary>
    public bool IsDuplicateSelected => SelectedAction == MergeAction.Duplicate;

    /// <summary>
    /// Активна ли стратегия пропуска элемента.
    /// </summary>
    public bool IsSkipSelected => SelectedAction == MergeAction.Skip;

    /// <summary>
    /// Подсказка для кнопки создания копии или импорта в зависимости от наличия конфликта.
    /// </summary>
    public string DuplicateActionTooltip => HasConflict
        ? L["Sync_Action_CreateNew"]
        : L["Sync_Action_Import"];

    partial void OnSelectedOptionChanged(SyncActionOption? value)
    {
        OnPropertyChanged(nameof(SelectedAction));
        OnPropertyChanged(nameof(IsMergeSelected));
        OnPropertyChanged(nameof(IsDuplicateSelected));
        OnPropertyChanged(nameof(IsSkipSelected));
    }

    /// <summary>
    /// Выбирает действие слияния для плейлиста.
    /// </summary>
    [RelayCommand]
    public void SelectMerge() => SetActionByKind(MergeAction.Merge);

    /// <summary>
    /// Выбирает действие дублирования / импорта для плейлиста.
    /// </summary>
    [RelayCommand]
    public void SelectDuplicate() => SetActionByKind(MergeAction.Duplicate);

    /// <summary>
    /// Выбирает действие пропуска для плейлиста.
    /// </summary>
    [RelayCommand]
    public void SelectSkip() => SetActionByKind(MergeAction.Skip);

    private void SetActionByKind(MergeAction action)
    {
        var target = AvailableActions.Find(a => a.Action == action);
        if (target != null)
            SelectedOption = target;
    }

    public SyncItemViewModel(
        PlaylistSearchResult original,
        bool hasConflict,
        int trackCount = 0)
    {
        Original = original;
        HasConflict = hasConflict;
        TrackCount = trackCount;

        var l = LocalizationService.Instance;
        AvailableActions = hasConflict
            ? [
                new SyncActionOption(MergeAction.Merge, l["Sync_Action_Merge"]),
                new SyncActionOption(MergeAction.Duplicate, l["Sync_Action_CreateNew"]),
                new SyncActionOption(MergeAction.Skip, l["Sync_Action_Skip"])
            ]
            : [
                new SyncActionOption(MergeAction.Duplicate, l["Sync_Action_Import"]),
                new SyncActionOption(MergeAction.Skip, l["Sync_Action_Skip"])
            ];
    }
}