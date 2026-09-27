using System.Collections;
using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LMP.UI.Features.Shared;

namespace LMP.UI.Controls;

public partial class TrackListControl : UserControl
{
    #region Constants

    private const string DragFormatTrackIndex = "application/x-lmp-track-index";

    private static readonly DataFormat<string> TrackIndexDataFormat =
        DataFormat.CreateInProcessFormat<string>(DragFormatTrackIndex);

    /// <summary>
    /// Фиксированная высота строки трека — O(1) hit-test при drag-and-drop.
    /// </summary>
    private const double ItemHeight = 62.0;

    /// <summary>
    /// Минимальное смещение в пикселях для начала drag.
    /// </summary>
    private const double DragThreshold = 6.0;

    private const int AutoScrollMargin = 50;
    private const double AutoScrollAmount = 12.0;
    private const double NearBottomThreshold = 80.0;

    #endregion

    #region Fields

    private readonly EventHandler<string> _languageChangedHandler;

    // Selection Engine
    private readonly HashSet<TrackItemViewModel> _selectedSet = [];
    private readonly HashSet<TrackItemViewModel> _preDragSelectionSnapshot = [];
    private int _selectionAnchorIndex = -1;
    private int _leadIndex = -1;
    private TrackItemViewModel? _deferredSingleSelectVm;
    private int _deferredSingleSelectIndex = -1;

    // Drag & Drop
    private Point _dragStartPoint;
    private int _dragSourceIndex = -1;
    private bool _isDragging;
    private Control? _lastHighlightedItem;
    private PointerPressedEventArgs? _dragPressedArgs;
    private static IReadOnlyList<int>? _activeInProcessDragIndices;

    // Scroll & Layout
    private ScrollViewer? _scrollViewer;
    private ItemsRepeater? _repeater;
    private DispatcherTimer? _autoScrollTimer;
    private double _autoScrollOffset;

    private SnapScrollHelper? _snapScroll;

    #endregion

    #region Styled Properties

    public static readonly StyledProperty<bool> EnableSnapScrollProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(EnableSnapScroll), true);

    /// <summary>
    /// Включает выравнивание позиции скролла по сетке высоты трека (60px).
    /// Устраняет sub-pixel рендеринг текста и иконок, снижает нагрузку на GPU.
    /// Touchpad не затрагивается — пропорциональный scroll сохраняется.
    /// </summary>
    public bool EnableSnapScroll
    {
        get => GetValue(EnableSnapScrollProperty);
        set => SetValue(EnableSnapScrollProperty, value);
    }

    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<TrackListControl, IEnumerable?>(nameof(Items));

    public IEnumerable? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public static readonly StyledProperty<ICommand?> LoadMoreCommandProperty =
        AvaloniaProperty.Register<TrackListControl, ICommand?>(nameof(LoadMoreCommand));

    public ICommand? LoadMoreCommand
    {
        get => GetValue(LoadMoreCommandProperty);
        set => SetValue(LoadMoreCommandProperty, value);
    }

    public static readonly StyledProperty<ICommand?> MoveItemCommandProperty =
        AvaloniaProperty.Register<TrackListControl, ICommand?>(nameof(MoveItemCommand));

    public ICommand? MoveItemCommand
    {
        get => GetValue(MoveItemCommandProperty);
        set => SetValue(MoveItemCommandProperty, value);
    }

    public static readonly StyledProperty<bool> EnableReorderingProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(EnableReordering), false);

    public bool EnableReordering
    {
        get => GetValue(EnableReorderingProperty);
        set => SetValue(EnableReorderingProperty, value);
    }

    public static readonly StyledProperty<bool> IsLoadingProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(IsLoading));

    public bool IsLoading
    {
        get => GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public static readonly StyledProperty<bool> IsLoadingMoreProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(IsLoadingMore));

    public bool IsLoadingMore
    {
        get => GetValue(IsLoadingMoreProperty);
        set => SetValue(IsLoadingMoreProperty, value);
    }

    public static readonly StyledProperty<bool> IsFetchingFromNetworkProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(IsFetchingFromNetwork));

    public bool IsFetchingFromNetwork
    {
        get => GetValue(IsFetchingFromNetworkProperty);
        set => SetValue(IsFetchingFromNetworkProperty, value);
    }

    public static readonly StyledProperty<bool> ReachedEndProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(ReachedEnd));

    public bool ReachedEnd
    {
        get => GetValue(ReachedEndProperty);
        set => SetValue(ReachedEndProperty, value);
    }

    public static readonly StyledProperty<bool> UseSearchLoaderProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(UseSearchLoader), false);

    public bool UseSearchLoader
    {
        get => GetValue(UseSearchLoaderProperty);
        set => SetValue(UseSearchLoaderProperty, value);
    }

    public static readonly StyledProperty<bool> IsPlaylistContextProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(IsPlaylistContext), false);

    public bool IsPlaylistContext
    {
        get => GetValue(IsPlaylistContextProperty);
        set => SetValue(IsPlaylistContextProperty, value);
    }

    public static readonly StyledProperty<string?> FilterTextProperty =
        AvaloniaProperty.Register<TrackListControl, string?>(nameof(FilterText));

    /// <summary>
    /// Текст локального фильтра. При изменении сбрасывает вертикальный скролл в начало,
    /// предотвращая десинхронизацию виртуализации ItemsRepeater.
    /// </summary>
    public string? FilterText
    {
        get => GetValue(FilterTextProperty);
        set => SetValue(FilterTextProperty, value);
    }

    public static readonly StyledProperty<bool> IsQueueContextProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(IsQueueContext), false);

    public bool IsQueueContext
    {
        get => GetValue(IsQueueContextProperty);
        set => SetValue(IsQueueContextProperty, value);
    }

    /// <summary>
    /// Прямое свойство вычисления видимости футера без использования MultiBinding-конвертеров.
    /// </summary>
    public static readonly DirectProperty<TrackListControl, bool> IsLoaderOrFooterVisibleProperty =
        AvaloniaProperty.RegisterDirect<TrackListControl, bool>(
            nameof(IsLoaderOrFooterVisible), static o => o.IsLoaderOrFooterVisible);

    /// <summary>
    /// Возвращает истину, если отображается индикатор дозагрузки либо плашка конца списка.
    /// </summary>
    public bool IsLoaderOrFooterVisible => IsLoadingMore || IsFooterVisible;

    #endregion

    #region Direct Properties

    public static readonly DirectProperty<TrackListControl, string> SearchingTextProperty =
        AvaloniaProperty.RegisterDirect<TrackListControl, string>(
            nameof(SearchingText), static o => o.SearchingText, static (o, v) => o.SearchingText = v);

    public string SearchingText
    {
        get;
        private set => SetAndRaise(SearchingTextProperty, ref field, value);
    } = "Searching...";

    public static readonly DirectProperty<TrackListControl, string> LoadingMoreTextProperty =
        AvaloniaProperty.RegisterDirect<TrackListControl, string>(
            nameof(LoadingMoreText), static o => o.LoadingMoreText, static (o, v) => o.LoadingMoreText = v);

    public string LoadingMoreText
    {
        get;
        private set => SetAndRaise(LoadingMoreTextProperty, ref field, value);
    } = "Searching for more";

    public static readonly DirectProperty<TrackListControl, string> EndOfListTextProperty =
        AvaloniaProperty.RegisterDirect<TrackListControl, string>(
            nameof(EndOfListText), static o => o.EndOfListText, static (o, v) => o.EndOfListText = v);

    public string EndOfListText
    {
        get;
        private set => SetAndRaise(EndOfListTextProperty, ref field, value);
    } = "End of list";

    public static readonly DirectProperty<TrackListControl, bool> IsFooterVisibleProperty =
        AvaloniaProperty.RegisterDirect<TrackListControl, bool>(
            nameof(IsFooterVisible), static o => o.IsFooterVisible);

    public bool IsFooterVisible
    {
        get;
        private set => SetAndRaise(IsFooterVisibleProperty, ref field, value);
    }

    public static readonly DirectProperty<TrackListControl, int> SelectedCountProperty =
        AvaloniaProperty.RegisterDirect<TrackListControl, int>(
            nameof(SelectedCount), static o => o.SelectedCount);

    public int SelectedCount => _selectedSet.Count;

    #endregion

    #region Constructor

    public TrackListControl()
    {
        InitializeComponent();

        _languageChangedHandler = (_, _) =>
        {
            if (Dispatcher.UIThread.CheckAccess())
                UpdateLocalizedTexts();
            else
                Dispatcher.UIThread.Post(UpdateLocalizedTexts);
        };

        UpdateLocalizedTexts();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
    }

    #endregion

    #region Lifecycle

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;
        UpdateLocalizedTexts();

        _repeater = this.FindControl<ItemsRepeater>("MainRepeater");

        SubscribeToCollectionChanged(Items);
        ResyncSelectionFromItems();

        Dispatcher.UIThread.Post(() =>
        {
            _scrollViewer = this.FindAncestorOfType<ScrollViewer>();

            if (_scrollViewer != null && EnableSnapScroll)
            {
                _snapScroll?.Dispose();
                _snapScroll = new SnapScrollHelper(_scrollViewer);
            }
        }, DispatcherPriority.Loaded);

        _autoScrollTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, OnAutoScrollTick);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;
        base.OnDetachedFromVisualTree(e);

        UnsubscribeFromCollectionChanged(Items);
        DetachSelectionState();

        _autoScrollTimer?.Stop();
        _autoScrollTimer = null;
        _snapScroll?.Dispose();
        _snapScroll = null;
        _repeater = null;
        _scrollViewer = null;
        _lastHighlightedItem = null;
    }

    #endregion

    #region Keyboard Navigation

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        bool isCtrlOrMeta = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        bool isShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (e.Key == Key.A && isCtrlOrMeta)
        {
            SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_isDragging)
            {
                CleanupDragStyles();
                _isDragging = false;
                _activeInProcessDragIndices = null;
            }
            else
            {
                ClearSelection();
            }
            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back)
        {
            if (IsPlaylistContext || IsQueueContext)
            {
                ExecuteDeleteSelection();
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Down)
        {
            HandleArrowNavigation(isDown: true, isShift: isShift, isCtrl: isCtrlOrMeta);
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            HandleArrowNavigation(isDown: false, isShift: isShift, isCtrl: isCtrlOrMeta);
            e.Handled = true;
        }
        else if (e.Key == Key.Home)
        {
            HandleHomeEndNavigation(isHome: true, isShift: isShift);
            e.Handled = true;
        }
        else if (e.Key == Key.End)
        {
            HandleHomeEndNavigation(isHome: false, isShift: isShift);
            e.Handled = true;
        }
        else if (e.Key == Key.Space)
        {
            if (Items is IList list && _leadIndex >= 0 && _leadIndex < list.Count)
            {
                if (list[_leadIndex] is TrackItemViewModel vm)
                {
                    if (isCtrlOrMeta)
                    {
                        ToggleItemSelection(vm);
                        _selectionAnchorIndex = _leadIndex;
                    }
                    else
                    {
                        vm.PlayCommand.Execute(null);
                    }
                    e.Handled = true;
                }
            }
        }
    }

    private void HandleArrowNavigation(bool isDown, bool isShift, bool isCtrl)
    {
        if (Items is not IList list || list.Count == 0) return;

        int current = _leadIndex >= 0 ? _leadIndex : (isDown ? -1 : list.Count);
        int target = isDown ? Math.Min(current + 1, list.Count - 1) : Math.Max(current - 1, 0);

        _leadIndex = target;

        if (isCtrl)
        {
            ScrollToTrackIndex(target, smooth: false);
            return;
        }

        if (isShift)
        {
            int anchor = _selectionAnchorIndex >= 0 ? _selectionAnchorIndex : current;
            if (_selectionAnchorIndex < 0) _selectionAnchorIndex = anchor;
            SelectRange(anchor, target, addToExisting: true);
        }
        else
        {
            int oldCount = _selectedSet.Count;
            ClearSelectionInternal();
            if (list[target] is TrackItemViewModel vm)
            {
                vm.IsSelected = true;
                _selectedSet.Add(vm);
                _selectionAnchorIndex = target;
                RaisePropertyChanged(SelectedCountProperty, oldCount, 1);
            }
        }

        ScrollToTrackIndex(target, smooth: false);
    }

    private void HandleHomeEndNavigation(bool isHome, bool isShift)
    {
        if (Items is not IList list || list.Count == 0) return;

        int target = isHome ? 0 : list.Count - 1;
        _leadIndex = target;

        if (isShift)
        {
            int anchor = _selectionAnchorIndex >= 0 ? _selectionAnchorIndex : 0;
            SelectRange(anchor, target, addToExisting: true);
        }
        else
        {
            int oldCount = _selectedSet.Count;
            ClearSelectionInternal();
            if (list[target] is TrackItemViewModel vm)
            {
                vm.IsSelected = true;
                _selectedSet.Add(vm);
                _selectionAnchorIndex = target;
                RaisePropertyChanged(SelectedCountProperty, oldCount, 1);
            }
        }

        ScrollToTrackIndex(target, smooth: false);
    }

    private void ExecuteDeleteSelection()
    {
        if (_selectedSet.Count == 0) return;

        var rep = _selectedSet.FirstOrDefault();
        if (rep == null) return;

        if (IsPlaylistContext)
        {
            rep.RemoveFromPlaylistCommand.Execute(null);
        }
        else if (IsQueueContext)
        {
            rep.RemoveFromQueueCommand.Execute(null);
        }
    }

    #endregion

    #region Property Changed

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == FilterTextProperty)
        {
            ClearSelection();
            ResetScrollPosition();
        }
        else if (change.Property == ItemsProperty)
        {
            UnsubscribeFromCollectionChanged(change.GetOldValue<IEnumerable?>());
            SubscribeToCollectionChanged(change.GetNewValue<IEnumerable?>());

            ResyncSelectionFromItems();
            UpdateItemsContext();
        }
        else if (change.Property == IsPlaylistContextProperty ||
                 change.Property == IsQueueContextProperty)
        {
            UpdateItemsContext();
        }
        else if (change.Property == ReachedEndProperty)
        {
            var sv = EnsureScrollViewer();
            if (sv != null)
                UpdateFooterVisibility(sv.Offset);
            else
                IsFooterVisible = ReachedEnd;
        }
        else if (change.Property == EnableSnapScrollProperty)
        {
            var sv = EnsureScrollViewer();
            if (sv == null) return;
            _snapScroll?.Dispose();
            _snapScroll = change.GetNewValue<bool>()
                ? new SnapScrollHelper(sv)
                : null;
        }

        if (change.Property == IsLoadingMoreProperty || change.Property == IsFooterVisibleProperty)
        {
            RaisePropertyChanged(IsLoaderOrFooterVisibleProperty, !IsLoaderOrFooterVisible, IsLoaderOrFooterVisible);
        }
    }

    private void SubscribeToCollectionChanged(IEnumerable? items)
    {
        if (items is INotifyCollectionChanged incc)
            incc.CollectionChanged += OnItemsCollectionChanged;
    }

    private void UnsubscribeFromCollectionChanged(IEnumerable? items)
    {
        if (items is INotifyCollectionChanged incc)
            incc.CollectionChanged -= OnItemsCollectionChanged;
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems != null)
        {
            int oldCount = _selectedSet.Count;
            bool selectionChanged = false;
            for (int i = 0; i < e.OldItems.Count; i++)
            {
                if (e.OldItems[i] is TrackItemViewModel vm && _selectedSet.Remove(vm))
                {
                    vm.IsSelected = false;
                    selectionChanged = true;
                }
            }

            if (selectionChanged)
            {
                RaisePropertyChanged(SelectedCountProperty, oldCount, _selectedSet.Count);
            }

            if (Items is IList list)
            {
                _selectionAnchorIndex = Math.Clamp(_selectionAnchorIndex, -1, list.Count - 1);
                _leadIndex = Math.Clamp(_leadIndex, -1, list.Count - 1);
            }
            else
            {
                _selectionAnchorIndex = -1;
                _leadIndex = -1;
            }
        }
        else if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
        {
            for (int i = 0; i < e.NewItems.Count; i++)
            {
                if (e.NewItems[i] is TrackItemViewModel vm)
                    vm.SelectionProvider = GetSelectedTrackInfos;
            }
        }
        else if (e.Action is NotifyCollectionChangedAction.Reset)
        {
            ResyncSelectionFromItems();
        }
    }

    private void UpdateFooterVisibility(Vector offset)
    {
        if (_scrollViewer == null) { IsFooterVisible = false; return; }

        double distanceToBottom =
            _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height - offset.Y;
        bool isNearBottom = distanceToBottom <= NearBottomThreshold
                            || _scrollViewer.Extent.Height <= _scrollViewer.Viewport.Height;
        IsFooterVisible = ReachedEnd && isNearBottom;
    }

    #endregion

    #region Selection Engine

    public void ClearSelection()
    {
        if (_selectedSet.Count == 0) return;

        int oldCount = _selectedSet.Count;
        ClearSelectionInternal();
        RaisePropertyChanged(SelectedCountProperty, oldCount, 0);
    }

    private void ClearSelectionInternal()
    {
        foreach (var item in _selectedSet)
        {
            item.IsSelected = false;
        }

        _selectedSet.Clear();
        _selectionAnchorIndex = -1;
        _leadIndex = -1;
        _deferredSingleSelectVm = null;
        _deferredSingleSelectIndex = -1;
    }

    private void DetachSelectionState()
    {
        _selectedSet.Clear();
        _preDragSelectionSnapshot.Clear();
        _selectionAnchorIndex = -1;
        _leadIndex = -1;
        _deferredSingleSelectVm = null;
        _deferredSingleSelectIndex = -1;
    }

    private void ResyncSelectionFromItems()
    {
        int oldCount = _selectedSet.Count;
        _selectedSet.Clear();
        _preDragSelectionSnapshot.Clear();
        _selectionAnchorIndex = -1;
        _leadIndex = -1;
        _deferredSingleSelectVm = null;
        _deferredSingleSelectIndex = -1;

        if (Items == null)
        {
            if (oldCount != 0)
                RaisePropertyChanged(SelectedCountProperty, oldCount, 0);
            return;
        }

        int firstIndex = -1;
        int currentIndex = 0;

        if (Items is IList list)
        {
            int count = list.Count;
            for (int i = 0; i < count; i++)
            {
                if (list[i] is TrackItemViewModel vm)
                {
                    vm.SelectionProvider = GetSelectedTrackInfos;
                    if (vm.IsSelected)
                    {
                        _selectedSet.Add(vm);
                        if (firstIndex < 0) firstIndex = i;
                    }
                }
            }
        }
        else
        {
            foreach (var item in Items)
            {
                if (item is TrackItemViewModel vm)
                {
                    vm.SelectionProvider = GetSelectedTrackInfos;
                    if (vm.IsSelected)
                    {
                        _selectedSet.Add(vm);
                        if (firstIndex < 0) firstIndex = currentIndex;
                    }
                }
                currentIndex++;
            }
        }

        _selectionAnchorIndex = firstIndex;
        _leadIndex = firstIndex;

        if (oldCount != _selectedSet.Count)
            RaisePropertyChanged(SelectedCountProperty, oldCount, _selectedSet.Count);
    }

    public void SelectAll()
    {
        if (Items == null) return;

        int oldCount = _selectedSet.Count;

        if (Items is IList list)
        {
            int count = list.Count;
            for (int i = 0; i < count; i++)
            {
                if (list[i] is TrackItemViewModel vm && _selectedSet.Add(vm))
                {
                    vm.IsSelected = true;
                }
            }
            _leadIndex = list.Count - 1;
            if (_selectionAnchorIndex < 0 && list.Count > 0)
                _selectionAnchorIndex = 0;
        }
        else
        {
            int idx = 0;
            foreach (var item in Items)
            {
                if (item is TrackItemViewModel vm && _selectedSet.Add(vm))
                {
                    vm.IsSelected = true;
                }
                idx++;
            }
            _leadIndex = idx - 1;
            if (_selectionAnchorIndex < 0 && idx > 0)
                _selectionAnchorIndex = 0;
        }

        if (_selectedSet.Count != oldCount)
        {
            RaisePropertyChanged(SelectedCountProperty, oldCount, _selectedSet.Count);
        }
    }

    private void SelectRange(int fromIndex, int toIndex, bool addToExisting)
    {
        if (Items is not IList list || list.Count == 0) return;

        int start = Math.Clamp(Math.Min(fromIndex, toIndex), 0, list.Count - 1);
        int end = Math.Clamp(Math.Max(fromIndex, toIndex), 0, list.Count - 1);

        int oldCount = _selectedSet.Count;

        if (!addToExisting)
        {
            foreach (var item in _selectedSet)
            {
                item.IsSelected = false;
            }
            _selectedSet.Clear();
        }

        for (int i = start; i <= end; i++)
        {
            if (list[i] is TrackItemViewModel vm && _selectedSet.Add(vm))
            {
                vm.IsSelected = true;
            }
        }

        if (oldCount != _selectedSet.Count)
            RaisePropertyChanged(SelectedCountProperty, oldCount, _selectedSet.Count);
    }

    private void ToggleItemSelection(TrackItemViewModel vm)
    {
        int oldCount = _selectedSet.Count;

        if (vm.IsSelected)
        {
            vm.IsSelected = false;
            _selectedSet.Remove(vm);
        }
        else
        {
            vm.IsSelected = true;
            _selectedSet.Add(vm);
        }

        RaisePropertyChanged(SelectedCountProperty, oldCount, _selectedSet.Count);
    }

    private IReadOnlyList<TrackInfo> GetSelectedTrackInfos()
    {
        if (_selectedSet.Count == 0) return [];

        var result = new List<TrackInfo>(_selectedSet.Count);

        if (Items is IList list)
        {
            int count = list.Count;
            for (int i = 0; i < count; i++)
            {
                if (list[i] is TrackItemViewModel vm && _selectedSet.Contains(vm))
                {
                    result.Add(vm.Track);
                }
            }
        }
        else if (Items != null)
        {
            foreach (var item in Items)
            {
                if (item is TrackItemViewModel vm && _selectedSet.Contains(vm))
                {
                    result.Add(vm.Track);
                }
            }
        }

        return result;
    }

    private List<TrackItemViewModel> GetSelectedViewModelsOrdered()
    {
        var result = new List<TrackItemViewModel>(_selectedSet.Count);
        if (Items is IList list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is TrackItemViewModel vm && _selectedSet.Contains(vm))
                    result.Add(vm);
            }
        }
        else if (Items != null)
        {
            foreach (var item in Items)
            {
                if (item is TrackItemViewModel vm && _selectedSet.Contains(vm))
                    result.Add(vm);
            }
        }
        return result;
    }

    #endregion

    #region Drag & Drop

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual visual)
        {
            var parent = visual;
            while (parent != null && parent != this)
            {
                if (parent is Border border && border.Classes.Contains("track-row"))
                    return;

                parent = parent.GetVisualParent();
            }
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            bool isShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool isCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);

            this.Focus();

            if (!isShift && !isCtrl)
            {
                ClearSelection();
            }
        }
    }

    private void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual visual && IsInteractiveChild(visual))
            return;

        if (sender is Control { DataContext: TrackItemViewModel vm })
        {
            vm.PlayCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual visual && IsInteractiveChild(visual))
            return;

        if (sender is not Control sourceControl || sourceControl.DataContext is not TrackItemViewModel vm)
            return;

        this.Focus();

        int itemIndex = Items is IList list ? list.IndexOf(vm) : -1;
        if (itemIndex < 0) return;

        var point = e.GetCurrentPoint(this);

        if (point.Properties.IsLeftButtonPressed)
        {
            bool isShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool isCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);

            if (isShift)
            {
                int anchor = _selectionAnchorIndex >= 0 ? _selectionAnchorIndex : itemIndex;
                if (_selectionAnchorIndex < 0) _selectionAnchorIndex = itemIndex;

                SelectRange(anchor, itemIndex, addToExisting: true);
                _leadIndex = itemIndex;
                _deferredSingleSelectVm = null;
                _deferredSingleSelectIndex = -1;
            }
            else if (isCtrl)
            {
                ToggleItemSelection(vm);
                _selectionAnchorIndex = itemIndex;
                _leadIndex = itemIndex;
                _deferredSingleSelectVm = null;
                _deferredSingleSelectIndex = -1;
            }
            else
            {
                if (vm.IsSelected)
                {
                    _deferredSingleSelectVm = vm;
                    _deferredSingleSelectIndex = itemIndex;
                }
                else
                {
                    int oldCount = _selectedSet.Count;
                    ClearSelectionInternal();
                    vm.IsSelected = true;
                    _selectedSet.Add(vm);
                    _selectionAnchorIndex = itemIndex;
                    _leadIndex = itemIndex;
                    _deferredSingleSelectVm = null;
                    _deferredSingleSelectIndex = -1;
                    RaisePropertyChanged(SelectedCountProperty, oldCount, 1);
                }
            }

            if (!EnableReordering) return;

            _dragStartPoint = e.GetPosition(this);
            _isDragging = false;
            _dragPressedArgs = e;
            _dragSourceIndex = itemIndex;
        }
        else if (point.Properties.IsRightButtonPressed)
        {
            if (!vm.IsSelected)
            {
                int oldCount = _selectedSet.Count;
                ClearSelectionInternal();
                vm.IsSelected = true;
                _selectedSet.Add(vm);
                _selectionAnchorIndex = itemIndex;
                _leadIndex = itemIndex;
                RaisePropertyChanged(SelectedCountProperty, oldCount, 1);
            }

            vm.SelectionProvider = GetSelectedTrackInfos;
            ShowSharedFlyout(sourceControl, true);
            e.Handled = true;
        }
    }

    private async void OnItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!EnableReordering || _isDragging || _dragSourceIndex < 0 || _dragPressedArgs is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var pos = e.GetPosition(this);
        double deltaX = Math.Abs(pos.X - _dragStartPoint.X);
        double deltaY = Math.Abs(pos.Y - _dragStartPoint.Y);

        if (deltaX < DragThreshold && deltaY < DragThreshold) return;

        _deferredSingleSelectVm = null;
        _deferredSingleSelectIndex = -1;

        if (sender is not Control source) return;

        _isDragging = true;

        IReadOnlyList<int> dragIndices;
        if (source.DataContext is TrackItemViewModel vm && vm.IsSelected && _selectedSet.Count > 1)
        {
            var orderedVms = GetSelectedViewModelsOrdered();
            var indices = new List<int>(orderedVms.Count);
            if (Items is IList list)
            {
                for (int i = 0; i < orderedVms.Count; i++)
                {
                    int idx = list.IndexOf(orderedVms[i]);
                    if (idx >= 0) indices.Add(idx);
                }
            }
            dragIndices = indices.Count > 0 ? indices : [_dragSourceIndex];
        }
        else
        {
            dragIndices = [_dragSourceIndex];
        }

        _activeInProcessDragIndices = dragIndices;

        var dragData = new DataTransfer();
        var item = new DataTransferItem();
        string payload = string.Join(',', dragIndices);
        item.Set(TrackIndexDataFormat, payload);
        item.Set(DataFormat.Text, payload);
        dragData.Add(item);

        source.Classes.Add("dragging");

        try
        {
            await DragDrop.DoDragDropAsync(_dragPressedArgs, dragData, DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            Log.Error($"[TrackList] DoDragDropAsync failed: {ex.Message}");
        }
        finally
        {
            source.Classes.Remove("dragging");
            CleanupDragStyles();
            _isDragging = false;
            _dragSourceIndex = -1;
            _dragPressedArgs = null;
            _activeInProcessDragIndices = null;
        }
    }

    private void OnItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left)
        {
            bool isShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool isCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);

            var deferredVm = _deferredSingleSelectVm;
            int deferredIdx = _deferredSingleSelectIndex;

            _deferredSingleSelectVm = null;
            _deferredSingleSelectIndex = -1;

            if (!_isDragging && deferredVm != null && !isShift && !isCtrl)
            {
                int oldCount = _selectedSet.Count;
                ClearSelectionInternal();
                deferredVm.IsSelected = true;
                _selectedSet.Add(deferredVm);
                _selectionAnchorIndex = deferredIdx;
                _leadIndex = deferredIdx;
                RaisePropertyChanged(SelectedCountProperty, oldCount, 1);
            }

            _isDragging = false;
            _dragSourceIndex = -1;
            _dragPressedArgs = null;
            CleanupDragStyles();
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (!EnableReordering || !HasTrackIndexData(e) || _repeater == null)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Move;

        var (_, overItem) = ResolveDropTarget(e);

        if (_lastHighlightedItem != null && _lastHighlightedItem != overItem)
        {
            _lastHighlightedItem.Classes.Remove("drop-target");
        }

        if (overItem == null)
        {
            _lastHighlightedItem = null;
            return;
        }

        _lastHighlightedItem = overItem;
        overItem.Classes.Add("drop-target");

        HandleAutoScroll(e);
    }

    private void OnDragLeave(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Visual visual && this.Bounds.Contains(visual.TranslatePoint(new Point(0, 0), this) ?? new Point(-1, -1)))
        {
            return;
        }

        CleanupDragStyles();
        _autoScrollTimer?.Stop();
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        CleanupDragStyles();
        _autoScrollTimer?.Stop();

        if (!EnableReordering || !HasTrackIndexData(e) || _repeater == null)
            return;

        var oldIndices = GetTrackIndices(e);
        if (oldIndices.Count == 0) return;

        var (targetIndex, _) = ResolveDropTarget(e);
        if (targetIndex < 0) return;

        if (oldIndices.Count == 1)
        {
            int oldIndex = oldIndices[0];
            if (oldIndex != targetIndex)
            {
                if (MoveItemCommand is IAsyncRelayCommand asyncCmd)
                    await asyncCmd.ExecuteAsync((oldIndex, targetIndex));
                else
                    MoveItemCommand?.Execute((oldIndex, targetIndex));

                ResyncSelectionFromItems();
                _selectionAnchorIndex = targetIndex;
                _leadIndex = targetIndex;
            }
        }
        else
        {
            await ExecuteBatchMoveAsync(oldIndices, targetIndex);
        }
    }

    private async Task ExecuteBatchMoveAsync(IReadOnlyList<int> sourceIndices, int targetIndex)
    {
        if (Items is not IList list || list.Count == 0) return;

        var sortedSources = sourceIndices.Distinct().OrderBy(x => x).ToList();
        var movingVms = new List<TrackItemViewModel>(sortedSources.Count);
        for (int i = 0; i < sortedSources.Count; i++)
        {
            int idx = sortedSources[i];
            if (idx >= 0 && idx < list.Count && list[idx] is TrackItemViewModel vm)
                movingVms.Add(vm);
        }

        if (movingVms.Count == 0) return;

        int clampedTarget = Math.Clamp(targetIndex, 0, list.Count - 1);
        var targetVm = list[clampedTarget] as TrackItemViewModel;
        if (targetVm == null) return;

        if (movingVms.Contains(targetVm)) return;

        for (int i = 0; i < movingVms.Count; i++)
        {
            var vm = movingVms[i];
            int currentFrom = list.IndexOf(vm);
            int currentTarget = list.IndexOf(targetVm);

            if (currentFrom < 0 || currentTarget < 0 || currentFrom == currentTarget)
                continue;

            if (MoveItemCommand is IAsyncRelayCommand asyncCmd)
                await asyncCmd.ExecuteAsync((currentFrom, currentTarget));
            else
                MoveItemCommand?.Execute((currentFrom, currentTarget));
        }

        ClearSelectionInternal();
        for (int i = 0; i < movingVms.Count; i++)
        {
            movingVms[i].IsSelected = true;
            _selectedSet.Add(movingVms[i]);
        }
        RaisePropertyChanged(SelectedCountProperty, -1, _selectedSet.Count);

        int newAnchor = list.IndexOf(targetVm);
        _selectionAnchorIndex = newAnchor >= 0 ? newAnchor : clampedTarget;
        _leadIndex = _selectionAnchorIndex;
    }

    private (int index, Control? rowControl) ResolveDropTarget(DragEventArgs e)
    {
        if (_repeater == null || Items is not IList list || list.Count == 0)
            return (-1, null);

        Visual? visual = e.Source as Visual;
        while (visual != null && visual != _repeater && visual != this)
        {
            if (visual is Border border && border.Classes.Contains("track-row") && border.DataContext is TrackItemViewModel vm)
            {
                int index = list.IndexOf(vm);
                if (index >= 0)
                    return (index, border);
            }
            visual = visual.GetVisualParent();
        }

        var repeaterPos = e.GetPosition(_repeater);
        if (_repeater.InputHitTest(repeaterPos) is Visual hitVisual)
        {
            visual = hitVisual;
            while (visual != null && visual != _repeater && visual != this)
            {
                if (visual is Border border && border.Classes.Contains("track-row") && border.DataContext is TrackItemViewModel vm)
                {
                    int index = list.IndexOf(vm);
                    if (index >= 0)
                        return (index, border);
                }
                visual = visual.GetVisualParent();
            }
        }

        for (int i = 0; i < list.Count; i++)
        {
            if (_repeater.TryGetElement(i) is Control child)
            {
                var bounds = child.Bounds;
                if (repeaterPos.Y >= bounds.Top && repeaterPos.Y <= bounds.Bottom)
                {
                    return (i, child);
                }
            }
        }

        if (repeaterPos.Y < 0)
            return (0, _repeater.TryGetElement(0));

        int lastIdx = list.Count - 1;
        return (lastIdx, _repeater.TryGetElement(lastIdx));
    }

    private static bool HasTrackIndexData(DragEventArgs e)
    {
        if (_activeInProcessDragIndices != null && _activeInProcessDragIndices.Count > 0)
            return true;

        var formats = e.DataTransfer.Formats;
        for (int i = 0; i < formats.Count; i++)
        {
            if (formats[i].Identifier == DragFormatTrackIndex || formats[i] == DataFormat.Text)
                return true;
        }

        return false;
    }

    private static IReadOnlyList<int> GetTrackIndices(DragEventArgs e)
    {
        if (_activeInProcessDragIndices != null && _activeInProcessDragIndices.Count > 0)
            return _activeInProcessDragIndices;

        var items = e.DataTransfer.Items;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            string? raw = item.TryGetValue(TrackIndexDataFormat) ?? item.TryGetValue(DataFormat.Text);
            if (!string.IsNullOrEmpty(raw))
            {
                var parts = raw.Split(',');
                var list = new List<int>(parts.Length);
                for (int j = 0; j < parts.Length; j++)
                {
                    if (int.TryParse(parts[j], out int idx))
                        list.Add(idx);
                }
                if (list.Count > 0) return list;
            }
        }

        return [];
    }

    private void CleanupDragStyles()
    {
        if (_lastHighlightedItem == null) return;
        _lastHighlightedItem.Classes.Remove("drop-target");
        _lastHighlightedItem = null;
    }

    private static bool IsInteractiveChild(Visual visual)
    {
        var parent = visual;
        while (parent != null)
        {
            if (parent is Button) return true;
            if (parent is Border border && border.Classes.Contains("track-row")) return false;
            parent = parent.GetVisualParent();
        }
        return false;
    }

    #endregion

    #region Scroll

    /// <summary>
    /// Гарантирует наличие ссылки на родительский ScrollViewer.
    /// </summary>
    private ScrollViewer? EnsureScrollViewer()
    {
        _scrollViewer ??= this.FindAncestorOfType<ScrollViewer>();
        return _scrollViewer;
    }

    /// <summary>
    /// Прокручивает список так, чтобы трек с указанным индексом отобразился на экране.
    /// </summary>
    /// <param name="index">Индекс целевого элемента.</param>
    /// <param name="smooth">Использовать плавное перемещение (true) или мгновенное позиционирование (false).</param>
    public void ScrollToTrackIndex(int index, bool smooth = true)
    {
        var sv = EnsureScrollViewer();
        if (sv == null || index < 0) return;
        if (Items is not ICollection col || index >= col.Count) return;

        double targetY = Math.Max(0, (index * ItemHeight) - ItemHeight);

        double maxScroll = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        if (maxScroll > 0)
        {
            targetY = Math.Clamp(targetY, 0, maxScroll);
        }

        if (smooth && _snapScroll != null)
        {
            _snapScroll.AnimateTo(targetY);
        }
        else
        {
            if (_snapScroll != null)
                _snapScroll.JumpTo(targetY);
            else
                sv.Offset = new Vector(sv.Offset.X, targetY);
        }

        _repeater?.InvalidateMeasure();
    }

    /// <summary>
    /// Сбрасывает вертикальную позицию скролла в начало списка при обновлении фильтра.
    /// </summary>
    public void ResetScrollPosition()
    {
        var sv = EnsureScrollViewer();
        if (sv == null) return;

        _snapScroll?.CancelAnimation();
        sv.Offset = new Vector(sv.Offset.X, 0);
    }

    private void HandleAutoScroll(DragEventArgs e)
    {
        if (_scrollViewer == null) return;
        var pos = e.GetPosition(_scrollViewer);

        if (pos.Y < AutoScrollMargin)
        { _autoScrollOffset = -AutoScrollAmount; _autoScrollTimer?.Start(); }
        else if (pos.Y > _scrollViewer.Bounds.Height - AutoScrollMargin)
        { _autoScrollOffset = AutoScrollAmount; _autoScrollTimer?.Start(); }
        else
        { _autoScrollOffset = 0; _autoScrollTimer?.Stop(); }
    }

    private void OnAutoScrollTick(object? sender, EventArgs e)
    {
        if (_scrollViewer == null || _autoScrollOffset == 0) return;
        _scrollViewer.Offset += new Vector(0, _autoScrollOffset);
    }

    #endregion

    #region Context Menu

    private void OnMoreButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not TrackItemViewModel vm) return;

        this.Focus();
        int itemIndex = Items is IList list ? list.IndexOf(vm) : -1;

        if (!vm.IsSelected)
        {
            int oldCount = _selectedSet.Count;
            ClearSelectionInternal();
            vm.IsSelected = true;
            _selectedSet.Add(vm);
            _selectionAnchorIndex = itemIndex;
            _leadIndex = itemIndex;
            RaisePropertyChanged(SelectedCountProperty, oldCount, 1);
        }

        vm.SelectionProvider = GetSelectedTrackInfos;
        ShowSharedFlyout(c, false);
    }

    /// <summary>
    /// Показывает контекстное меню трека.
    /// showAtPointer=true — при ПКМ, меню под курсором.
    /// showAtPointer=false — при клике на "три точки", меню привязано к кнопке.
    /// </summary>
    private void ShowSharedFlyout(Control target, bool showAtPointer)
    {
        if (this.Resources.TryGetValue("SharedTrackMenuFlyout", out var res) && res is MenuFlyout f)
            f.ShowAt(target, showAtPointer);
    }

    private void OnMenuFlyoutOpened(object? sender, EventArgs e)
    {
        if (sender is MenuFlyout { Target: Control t } && t.DataContext is TrackItemViewModel vm)
        {
            vm.IsMenuOpen = true;
            _ = vm.PreparePlaylistSubmenuAsync();
        }
    }

    private void OnMenuFlyoutClosed(object? sender, EventArgs e)
    {
        if (sender is MenuFlyout { Target: Control t } && t.DataContext is TrackItemViewModel vm)
        {
            vm.IsMenuOpen = false;
            vm.ClearPlaylistSubmenu();
        }
    }

    #endregion

    #region Private Methods

    private void UpdateLocalizedTexts()
    {
        var L = LocalizationService.Instance;
        SearchingText = L["Search_Searching"] ?? "Searching...";
        LoadingMoreText = L["Search_LoadingMore"] ?? "Searching for more";
        EndOfListText = L["Search_EndOfList"] ?? "End of list";
    }

    /// <summary>
    /// Проставляет контекстные флаги всем VM в коллекции.
    /// Пропускает VM, у которых значение уже совпадает — устраняет
    /// лавину RaisePropertyChanged при повторных вызовах с теми же данными.
    /// Исключает аллокацию IEnumerator при приведении к списочному интерфейсу.
    /// </summary>
    private void UpdateItemsContext()
    {
        if (Items == null) return;

        var isPlaylist = IsPlaylistContext;
        var isQueue = IsQueueContext;

        if (Items is IList<TrackItemViewModel> list)
        {
            int count = list.Count;
            for (int i = 0; i < count; i++)
            {
                var vm = list[i];
                if (vm.IsPlaylistContext != isPlaylist) vm.IsPlaylistContext = isPlaylist;
                if (vm.IsQueueContext != isQueue) vm.IsQueueContext = isQueue;
                vm.SelectionProvider = GetSelectedTrackInfos;
            }
            return;
        }

        foreach (var item in Items)
        {
            if (item is not TrackItemViewModel vm) continue;
            if (vm.IsPlaylistContext != isPlaylist) vm.IsPlaylistContext = isPlaylist;
            if (vm.IsQueueContext != isQueue) vm.IsQueueContext = isQueue;
            vm.SelectionProvider = GetSelectedTrackInfos;
        }
    }

    #endregion
}