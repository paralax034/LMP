using Avalonia.Collections;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using LMP.UI.Features.Shell;

namespace LMP.UI.ViewModels;

public abstract partial class PaginatedViewModel<TSource, TViewModel> : ViewModelBase, IFilterable, ISmoothTransitionViewModel
    where TViewModel : class, IDisposable
    where TSource : notnull
{
    #region Fields

    protected readonly LibraryService LibService;

    private readonly List<(TSource Source, TViewModel Vm)> _itemPairs = [];
    private readonly HashSet<string> _loadedIds = new(StringComparer.Ordinal);

    private int _consecutiveEmptyLoads;
    private const int MaxConsecutiveEmptyLoads = 5;

    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _filterDebounceCts;
    private bool _canFetchMore;
    private bool _isDisposed;

    // Поля автоматического перехватчика переходов
    private bool _isDataLoading = true;
    private bool _isTransitioning;

    #endregion

    #region Properties

    protected virtual int BatchSize => LibService.Settings.LoadBatchSize > 0
        ? LibService.Settings.LoadBatchSize
        : 20;
    protected virtual int PrefetchThreshold => 10;

    /// <summary>
    /// Управляет видимостью списка. Вычисляет итоговое состояние:
    /// скелетон показывается если грузятся данные ИЛИ идет анимация перехода.
    /// </summary>
    public bool IsLoading
    {
        get => _isDataLoading || _isTransitioning;
        protected set
        {
            if (_isDataLoading == value) return;
            _isDataLoading = value;
            OnPropertyChanged(nameof(IsLoading));
            LoadMoreCommand.NotifyCanExecuteChanged();
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool IsLoadingMore { get; protected set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool IsFetchingFromNetwork { get; protected set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool HasMoreItems { get; protected set; }

    [ObservableProperty]
    public partial bool ReachedEnd { get; protected set; }

    [ObservableProperty]
    public partial string FilterQuery { get; set; } = string.Empty;
    string IFilterable.FilterQuery
    {
        get => FilterQuery;
        set => FilterQuery = value;
    }

    public AvaloniaList<TViewModel> Items { get; } = [];
    protected int TotalCount { get; private set; }

    public IAsyncRelayCommand LoadMoreCommand { get; }

    #endregion

    #region Constructor

    protected PaginatedViewModel()
    {
        LibService = AppEntry.Services.GetRequiredService<LibraryService>();

        LoadMoreCommand = new AsyncRelayCommand(
            LoadNextBatchAsync,
            () => !IsLoadingMore && !IsLoading && !IsFetchingFromNetwork && HasMoreItems);
    }

    #endregion

    #region Filtering

    partial void OnFilterQueryChanged(string value)
    {
        _consecutiveEmptyLoads = 0;

        _filterDebounceCts?.Cancel();
        _filterDebounceCts?.Dispose();
        _filterDebounceCts = new CancellationTokenSource();
        var token = _filterDebounceCts.Token;

        _ = Task.Delay(200, token).ContinueWith(t =>
        {
            if (t.IsCanceled || _isDisposed) return;
            Dispatcher.UIThread.Post(ApplyFilter);
        }, TaskScheduler.Default);
    }

    private void ApplyFilter()
    {
        if (_isDisposed) return;

        var query = FilterQuery;
        bool hasFilter = !string.IsNullOrWhiteSpace(query);

        Items.Clear();

        var matched = new List<TViewModel>(_itemPairs.Count);
        for (int i = 0; i < _itemPairs.Count; i++)
        {
            var pair = _itemPairs[i];
            if (!hasFilter || FilterItem(pair.Source, query))
            {
                matched.Add(pair.Vm);
            }
        }

        Items.AddRange(matched);
    }

    #endregion

    #region ISmoothTransitionViewModel

    /// <inheritdoc />
    public virtual void PrepareForTransition()
    {
        _isTransitioning = true;
        OnPropertyChanged(nameof(IsLoading));
    }

    #endregion

    #region Navigation Lifecycle

    public override async Task OnNavigatedToAsync()
    {
        _isTransitioning = false;
        OnPropertyChanged(nameof(IsLoading));

        await base.OnNavigatedToAsync();
    }

    #endregion

    #region Abstract Methods

    protected abstract TViewModel CreateItemViewModel(TSource item);
    protected abstract bool FilterItem(TSource item, string query);
    protected virtual string GetItemId(TSource item) => item?.GetHashCode().ToString() ?? "";

    protected virtual Task<List<TSource>> FetchMoreFromNetworkAsync(CancellationToken ct)
        => Task.FromResult(new List<TSource>());

    #endregion

    #region Public Methods

    /// <summary>
    /// Переносит элементы в источнике данных и синхронизирует список.
    /// </summary>
    protected virtual void MoveSourceItem(int oldIndex, int newIndex)
    {
        if (oldIndex < 0 || oldIndex >= _itemPairs.Count ||
            newIndex < 0 || newIndex >= _itemPairs.Count ||
            oldIndex == newIndex)
            return;

        var item = _itemPairs[oldIndex];
        _itemPairs.RemoveAt(oldIndex);
        _itemPairs.Insert(newIndex, item);

        ApplyFilter();
    }

    /// <summary>
    /// Инициализирует модель новыми элементами.
    /// </summary>
    protected virtual async Task InitializeItemsAsync(IEnumerable<TSource> items, bool canFetchMore = true)
    {
        if (_isDisposed) return;

        CancelLoading();
        _loadCts = new CancellationTokenSource();
        _canFetchMore = canFetchMore;
        _consecutiveEmptyLoads = 0;

        DisposePairs();

        var itemsList = items as List<TSource> ?? items?.ToList() ?? [];

        _loadedIds.Clear();
        for (int i = 0; i < itemsList.Count; i++)
        {
            var source = itemsList[i];
            var id = GetItemId(source);
            if (!string.IsNullOrEmpty(id))
                _loadedIds.Add(id);

            var vm = CreateItemViewModel(source);
            _itemPairs.Add((source, vm));
        }

        TotalCount = _itemPairs.Count;
        UpdateState();
        ApplyFilter();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Полностью очищает список элементов.
    /// </summary>
    protected virtual void ClearItems()
    {
        DisposePairs();
        Items.Clear();
        _loadedIds.Clear();
        TotalCount = 0;
        _canFetchMore = false;
        UpdateState();
    }

    protected List<TSource> GetItemsSnapshot()
    {
        var list = new List<TSource>(_itemPairs.Count);
        for (int i = 0; i < _itemPairs.Count; i++)
            list.Add(_itemPairs[i].Source);
        return list;
    }

    protected List<string> GetLoadedItemsIds()
    {
        var list = new List<string>(_itemPairs.Count);
        for (int i = 0; i < _itemPairs.Count; i++)
            list.Add(GetItemId(_itemPairs[i].Source));
        return list;
    }

    private void DisposePairs()
    {
        for (int i = 0; i < _itemPairs.Count; i++)
        {
            _itemPairs[i].Vm.Dispose();
        }
        _itemPairs.Clear();
    }

    #endregion

    #region Loading Logic

    protected void CancelLoading()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        IsLoading = false;
        IsLoadingMore = false;
        IsFetchingFromNetwork = false;
    }

    private void UpdateState()
    {
        HasMoreItems = _canFetchMore;
        ReachedEnd = !_canFetchMore && TotalCount > 0;
    }

    protected void SetCanFetchMore(bool value)
    {
        _canFetchMore = value;
        UpdateState();
    }

    private async Task LoadNextBatchAsync()
    {
        if (_isDisposed || IsLoadingMore || IsFetchingFromNetwork || !_canFetchMore) return;

        IsLoadingMore = true;
        IsFetchingFromNetwork = true;

        int countBefore = TotalCount;

        try
        {
            var token = _loadCts?.Token ?? CancellationToken.None;
            var newItems = await FetchMoreFromNetworkAsync(token);

            if (token.IsCancellationRequested || _isDisposed) return;

            if (newItems is { Count: > 0 })
            {
                var newVms = new List<TViewModel>(newItems.Count);
                var hasFilter = !string.IsNullOrWhiteSpace(FilterQuery);

                for (int i = 0; i < newItems.Count; i++)
                {
                    var item = newItems[i];
                    var id = GetItemId(item);
                    if (!string.IsNullOrEmpty(id) && _loadedIds.Add(id))
                    {
                        var vm = CreateItemViewModel(item);
                        _itemPairs.Add((item, vm));
                        TotalCount++;

                        if (!hasFilter || FilterItem(item, FilterQuery))
                            newVms.Add(vm);
                    }
                }

                if (newVms.Count > 0)
                    Items.AddRange(newVms);

                if (TotalCount == countBefore)
                    _consecutiveEmptyLoads++;
                else
                    _consecutiveEmptyLoads = 0;

                if (_consecutiveEmptyLoads >= MaxConsecutiveEmptyLoads)
                    _canFetchMore = false;
            }
            else
            {
                _canFetchMore = false;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error($"[Paginated] Batch load failure: {ex.Message}");
            _canFetchMore = false;
        }
        finally
        {
            IsLoadingMore = false;
            IsFetchingFromNetwork = false;
            UpdateState();
        }
    }

    #endregion

    #region IDisposable

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            Log.Debug("[PaginatedVM] Disposing");

            CancelLoading();

            _filterDebounceCts?.Cancel();
            _filterDebounceCts?.Dispose();
            _filterDebounceCts = null;

            Items.Clear();
            DisposePairs();
        }

        base.Dispose(disposing);
        _isDisposed = true;
    }

    #endregion
}