using System.Collections;
using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LMP.Core.Models;
using LMP.UI.Features.Shared;
using LMP.UI.Services;
using LMP.UI.ViewModels;

namespace LMP.UI.Controls;

/// <summary>
/// Высокопроизводительный виртуализированный элемент управления списком музыкальных треков.
/// Координирует ввод пользователя, Drag-and-Drop, плавный скроллинг и единый контракт выбора.
/// </summary>
public partial class TrackListControl : UserControl
{
    #region Constants

    private const string DragFormatTrackIndex = "application/x-lmp-track-index";

    private static readonly DataFormat<string> TrackIndexDataFormat =
        DataFormat.CreateInProcessFormat<string>(DragFormatTrackIndex);

    /// <summary>
    /// Фиксированная высота строки трека с учетом внешних отступов (60px + 2px).
    /// </summary>
    private const double ItemHeight = 62.0;

    /// <summary>
    /// Порог смещения курсора в пикселях для активации операции перетаскивания.
    /// </summary>
    private const double DragThreshold = 6.0;

    private const int AutoScrollMargin = 50;
    private const double AutoScrollAmount = 12.0;
    private const double NearBottomThreshold = 80.0;

    #endregion

    #region Fields

    private readonly EventHandler<string> _languageChangedHandler;

    private int _selectionAnchorIndex = -1;
    private int _leadIndex = -1;
    private TrackItemViewModel? _deferredSingleSelectVm;
    private int _deferredSingleSelectIndex = -1;

    private Point _dragStartPoint;
    private int _dragSourceIndex = -1;
    private bool _isDragging;
    private Border? _dropIndicatorLine;
    private TranslateTransform? _dropIndicatorTransform;
    private PointerPressedEventArgs? _dragPressedArgs;
    private static IReadOnlyList<int>? _activeInProcessDragIndices;

    private ScrollViewer? _scrollViewer;
    private ItemsRepeater? _repeater;
    private DispatcherTimer? _autoScrollTimer;
    private double _autoScrollOffset;

    private SnapScrollHelper? _snapScroll;
    private Action? _virtualSelectionHandler;
    private bool _isAttachedToVisualTree;

    #endregion

    #region Styled Properties

    public static readonly StyledProperty<bool> EnableSnapScrollProperty =
        AvaloniaProperty.Register<TrackListControl, bool>(nameof(EnableSnapScroll), true);

    /// <summary>
    /// Включает выравнивание позиции скролла по сетке высоты трека для устранения субпиксельного размытия.
    /// </summary>
    public bool EnableSnapScroll
    {
        get => GetValue(EnableSnapScrollProperty);
        set => SetValue(EnableSnapScrollProperty, value);
    }

    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<TrackListControl, IEnumerable?>(nameof(Items));

    /// <summary>
    /// Источник данных элементов списка, реализующий <see cref="IVirtualTrackList"/>.
    /// </summary>
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
    /// Текст поискового фильтра. При изменении сбрасывает вертикальный скролл в начало.
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

    public static readonly DirectProperty<TrackListControl, bool> IsLoaderOrFooterVisibleProperty =
        AvaloniaProperty.RegisterDirect<TrackListControl, bool>(
            nameof(IsLoaderOrFooterVisible), static o => o.IsLoaderOrFooterVisible);

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

    public int SelectedCount => Items is IVirtualTrackList vtl ? vtl.SelectedCount : 0;

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
        AddHandler(InputElement.PointerPressedEvent, (s, e) => _lastCopyClickModifiers = e.KeyModifiers, RoutingStrategies.Tunnel);
    }

    #endregion

    #region Lifecycle

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttachedToVisualTree = true;

        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;
        UpdateLocalizedTexts();

        _repeater = this.FindControl<ItemsRepeater>("MainRepeater");
        _dropIndicatorLine = this.FindControl<Border>("DropIndicatorLine");
        _dropIndicatorTransform = _dropIndicatorLine?.RenderTransform as TranslateTransform;

        SubscribeToCollectionChanged(Items);
        HookVirtualListSelection(Items);
        UpdateItemsContext();

        Dispatcher.UIThread.Post(() =>
        {
            if (!_isAttachedToVisualTree) return;

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
        _isAttachedToVisualTree = false;
        LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;
        base.OnDetachedFromVisualTree(e);

        UnsubscribeFromCollectionChanged(Items);
        UnhookVirtualListSelection();
        DetachSelectionState();

        if (Items is IVirtualTrackList virtualList)
        {
            if (ReferenceEquals(virtualList.SelectionProvider?.Target, this))
            {
                virtualList.SelectionProvider = null;
            }
        }

        _autoScrollTimer?.Stop();
        _autoScrollTimer = null;
        _snapScroll?.Dispose();
        _snapScroll = null;
        _repeater = null;
        _scrollViewer = null;
        _dropIndicatorLine = null;
        _dropIndicatorTransform = null;
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
            if (Items is IVirtualTrackList virtualList && (uint)_leadIndex < (uint)virtualList.FilteredCount)
            {
                if (isCtrlOrMeta)
                {
                    virtualList.ToggleIndexSelected(_leadIndex);
                    _selectionAnchorIndex = _leadIndex;
                }
                else
                {
                    virtualList[_leadIndex]?.PlayCommand.Execute(null);
                }

                e.Handled = true;
            }
        }
    }

    private void HandleArrowNavigation(bool isDown, bool isShift, bool isCtrl)
    {
        if (Items is not IVirtualTrackList virtualList || virtualList.FilteredCount == 0) return;

        int count = virtualList.FilteredCount;
        int current = _leadIndex >= 0 ? _leadIndex : (isDown ? -1 : count);
        int target = isDown ? Math.Min(current + 1, count - 1) : Math.Max(current - 1, 0);

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
            virtualList.SelectRange(anchor, target, addToExisting: true);
        }
        else
        {
            virtualList.ClearSelection();
            virtualList.SetIndexSelected(target, true);
            _selectionAnchorIndex = target;
        }

        ScrollToTrackIndex(target, smooth: false);
    }

    private void HandleHomeEndNavigation(bool isHome, bool isShift)
    {
        if (Items is not IVirtualTrackList virtualList || virtualList.FilteredCount == 0) return;

        int target = isHome ? 0 : virtualList.FilteredCount - 1;
        _leadIndex = target;

        if (isShift)
        {
            int anchor = _selectionAnchorIndex >= 0 ? _selectionAnchorIndex : 0;
            virtualList.SelectRange(anchor, target, addToExisting: true);
        }
        else
        {
            virtualList.ClearSelection();
            virtualList.SetIndexSelected(target, true);
            _selectionAnchorIndex = target;
        }

        ScrollToTrackIndex(target, smooth: false);
    }

    private void ExecuteDeleteSelection()
    {
        if (Items is not IVirtualTrackList virtualList || virtualList.SelectedCount == 0) return;

        var ordered = virtualList.GetSelectedViewModelsOrdered();
        var rep = ordered.Count > 0
            ? ordered[0]
            : ((uint)_leadIndex < (uint)virtualList.FilteredCount ? virtualList[_leadIndex] : null);

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
            UnhookVirtualListSelection();

            var newItems = change.GetNewValue<IEnumerable?>();
            SubscribeToCollectionChanged(newItems);
            HookVirtualListSelection(newItems);

            DetachSelectionState();
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

    private void HookVirtualListSelection(IEnumerable? items)
    {
        if (items is IVirtualTrackList vtl)
        {
            _virtualSelectionHandler = OnVirtualSelectionChanged;
            vtl.SelectionChanged += _virtualSelectionHandler;
            RaisePropertyChanged(SelectedCountProperty, -1, vtl.SelectedCount);
        }
    }

    private void OnVirtualSelectionChanged()
    {
        if (Items is IVirtualTrackList vtl)
        {
            RaisePropertyChanged(SelectedCountProperty, -1, vtl.SelectedCount);
        }
    }

    private void UnhookVirtualListSelection()
    {
        if (Items is IVirtualTrackList vtl && _virtualSelectionHandler != null)
        {
            vtl.SelectionChanged -= _virtualSelectionHandler;
            _virtualSelectionHandler = null;
        }
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Items is IVirtualTrackList virtualList)
        {
            RaisePropertyChanged(SelectedCountProperty, -1, virtualList.SelectedCount);
        }
    }

    private void UpdateFooterVisibility(Vector offset)
    {
        if (_scrollViewer == null)
        {
            IsFooterVisible = false;
            return;
        }

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
        if (Items is IVirtualTrackList virtualList)
        {
            virtualList.ClearSelection();
            DetachSelectionState();
        }
    }

    private void DetachSelectionState()
    {
        _selectionAnchorIndex = -1;
        _leadIndex = -1;
        _deferredSingleSelectVm = null;
        _deferredSingleSelectIndex = -1;
    }

    public void SelectAll()
    {
        if (Items is IVirtualTrackList virtualList)
        {
            virtualList.SelectAll();
            _leadIndex = virtualList.FilteredCount - 1;
            if (_selectionAnchorIndex < 0 && virtualList.FilteredCount > 0)
                _selectionAnchorIndex = 0;
        }
    }

    private List<TrackInfo> GetSelectedTrackInfos()
    {
        if (Items is IVirtualTrackList virtualList)
        {
            return virtualList.GetSelectedTracks();
        }

        return [];
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

        int itemIndex = _repeater?.GetElementIndex(sourceControl) ?? (Items is IList list ? list.IndexOf(vm) : -1);
        if (itemIndex < 0) return;

        this.Focus();

        var point = e.GetCurrentPoint(this);

        if (point.Properties.IsLeftButtonPressed)
        {
            e.Handled = true;

            bool isShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool isCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);

            if (Items is IVirtualTrackList virtualList)
            {
                if (isShift)
                {
                    int anchor = _selectionAnchorIndex >= 0 ? _selectionAnchorIndex : itemIndex;
                    if (_selectionAnchorIndex < 0) _selectionAnchorIndex = itemIndex;

                    virtualList.SelectRange(anchor, itemIndex, addToExisting: true);
                    _leadIndex = itemIndex;
                    _deferredSingleSelectVm = null;
                    _deferredSingleSelectIndex = -1;
                }
                else if (isCtrl)
                {
                    virtualList.ToggleIndexSelected(itemIndex);
                    _selectionAnchorIndex = itemIndex;
                    _leadIndex = itemIndex;
                    _deferredSingleSelectVm = null;
                    _deferredSingleSelectIndex = -1;
                }
                else
                {
                    if (virtualList.IsIndexSelected(itemIndex))
                    {
                        _deferredSingleSelectVm = vm;
                        _deferredSingleSelectIndex = itemIndex;
                    }
                    else
                    {
                        virtualList.ClearSelection();
                        virtualList.SetIndexSelected(itemIndex, true);
                        _selectionAnchorIndex = itemIndex;
                        _leadIndex = itemIndex;
                        _deferredSingleSelectVm = null;
                        _deferredSingleSelectIndex = -1;
                    }
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
            if (Items is IVirtualTrackList virtualList)
            {
                if (!virtualList.IsIndexSelected(itemIndex))
                {
                    virtualList.ClearSelection();
                    virtualList.SetIndexSelected(itemIndex, true);
                    _selectionAnchorIndex = itemIndex;
                    _leadIndex = itemIndex;
                }
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

        if (source.DataContext is TrackItemViewModel vm && vm.IsSelected && SelectedCount > 1 &&
            Items is IVirtualTrackList vtl)
        {
            var orderedVms = vtl.GetSelectedViewModelsOrdered();
            var indices = new List<int>(orderedVms.Count);
            for (int i = 0; i < orderedVms.Count; i++)
            {
                int idx = vtl.IndexOf(orderedVms[i]);
                if (idx >= 0) indices.Add(idx);
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
            e.Handled = true;

            bool isShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool isCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);

            var deferredVm = _deferredSingleSelectVm;
            int deferredIdx = _deferredSingleSelectIndex;

            _deferredSingleSelectVm = null;
            _deferredSingleSelectIndex = -1;

            if (!_isDragging && deferredVm != null && !isShift && !isCtrl && Items is IVirtualTrackList virtualList)
            {
                virtualList.ClearSelection();
                virtualList.SetIndexSelected(deferredIdx, true);
                _selectionAnchorIndex = deferredIdx;
                _leadIndex = deferredIdx;
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
            if (_dropIndicatorLine != null) _dropIndicatorLine.IsVisible = false;
            return;
        }

        e.DragEffects = DragDropEffects.Move;

        var (targetIndex, overItem, isBelow) = ResolveDropTarget(e);

        if (_dropIndicatorLine != null && _dropIndicatorLine.Parent is Visual indicatorParent)
        {
            double fallbackY = (targetIndex * ItemHeight) + (isBelow ? ItemHeight : 0);
            double targetY = fallbackY;

            if (overItem != null)
            {
                var pt = overItem.TranslatePoint(new Point(0, 0), indicatorParent);
                if (pt.HasValue)
                {
                    targetY = isBelow
                        ? pt.Value.Y + overItem.Bounds.Height + 1.0
                        : pt.Value.Y - 1.0;
                }
            }

            if (_dropIndicatorTransform != null)
            {
                _dropIndicatorTransform.Y = targetY;
            }

            _dropIndicatorLine.IsVisible = true;
        }

        HandleAutoScroll(e);
    }

    private void OnDragLeave(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Visual visual &&
            this.Bounds.Contains(visual.TranslatePoint(new Point(0, 0), this) ?? new Point(-1, -1)))
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

        var (targetIndex, _, isBelow) = ResolveDropTarget(e);
        if (targetIndex < 0) return;

        int finalTargetIndex = isBelow ? targetIndex + 1 : targetIndex;

        if (oldIndices.Count == 1)
        {
            int oldIndex = oldIndices[0];
            int maxValidIndex = (Items is ICollection c ? c.Count : 1) - 1;
            int clampedTarget = Math.Clamp(finalTargetIndex, 0, Math.Max(0, maxValidIndex));

            if (oldIndex != clampedTarget)
            {
                if (MoveItemCommand is IAsyncRelayCommand asyncCmd)
                    await asyncCmd.ExecuteAsync((oldIndex, clampedTarget));
                else
                    MoveItemCommand?.Execute((oldIndex, clampedTarget));

                if (Items is IVirtualTrackList virtualList)
                {
                    virtualList.ClearSelection();
                    virtualList.SetIndexSelected(clampedTarget, true);
                }

                _selectionAnchorIndex = clampedTarget;
                _leadIndex = clampedTarget;
            }
        }
        else
        {
            await ExecuteBatchMoveAsync(oldIndices, finalTargetIndex);
        }
    }

    private async Task ExecuteBatchMoveAsync(IReadOnlyList<int> sourceIndices, int targetIndex)
    {
        if (Items is not IVirtualTrackList virtualList || virtualList.FilteredCount == 0) return;

        var sortedSources = sourceIndices.Distinct().OrderBy(x => x).ToList();
        var movingVms = new List<TrackItemViewModel>(sortedSources.Count);
        for (int i = 0; i < sortedSources.Count; i++)
        {
            int idx = sortedSources[i];
            if (idx >= 0 && idx < virtualList.FilteredCount)
                movingVms.Add(virtualList[idx]);
        }

        if (movingVms.Count == 0) return;

        int clampedTarget = Math.Clamp(targetIndex, 0, virtualList.FilteredCount - 1);
        var targetVm = virtualList[clampedTarget];
        if (targetVm == null || movingVms.Contains(targetVm)) return;

        int initialTargetIdx = virtualList.IndexOf(targetVm);
        bool isMovingDownwards = sortedSources[0] < initialTargetIdx;

        var orderedMovingVms = isMovingDownwards
            ? movingVms.AsEnumerable().Reverse()
            : movingVms;

        foreach (var vm in orderedMovingVms)
        {
            int currentFrom = virtualList.IndexOf(vm);
            int currentTarget = virtualList.IndexOf(targetVm);

            if (currentFrom < 0 || currentTarget < 0 || currentFrom == currentTarget)
                continue;

            if (MoveItemCommand is IAsyncRelayCommand asyncCmd)
                await asyncCmd.ExecuteAsync((currentFrom, currentTarget));
            else
                MoveItemCommand?.Execute((currentFrom, currentTarget));
        }

        virtualList.ClearSelection();
        for (int i = 0; i < movingVms.Count; i++)
        {
            int newIdx = virtualList.IndexOf(movingVms[i]);
            if (newIdx >= 0)
                virtualList.SetIndexSelected(newIdx, true);
        }

        int newAnchor = virtualList.IndexOf(targetVm);
        _selectionAnchorIndex = newAnchor >= 0 ? newAnchor : clampedTarget;
        _leadIndex = _selectionAnchorIndex;
    }

    private (int index, Control? rowControl, bool isBelow) ResolveDropTarget(DragEventArgs e)
    {
        if (_repeater == null || Items is not ICollection col || col.Count == 0)
            return (-1, null, false);

        Visual? visual = e.Source as Visual;
        while (visual != null && visual != _repeater && visual != this)
        {
            if (visual is Border border && border.Classes.Contains("track-row") &&
                border.DataContext is TrackItemViewModel)
            {
                int idx = _repeater.GetElementIndex(border);
                if (idx >= 0)
                {
                    var pos = e.GetPosition(border);
                    bool isBelow = pos.Y >= (border.Bounds.Height / 2.0);
                    return (idx, border, isBelow);
                }
            }

            visual = visual.GetVisualParent();
        }

        var repeaterPos = e.GetPosition(_repeater);
        if (repeaterPos.Y < 0)
            return (0, _repeater.TryGetElement(0), false);

        int estimatedIndex = (int)(repeaterPos.Y / ItemHeight);
        if (estimatedIndex >= col.Count)
        {
            int lastIdx = col.Count - 1;
            return (lastIdx, _repeater.TryGetElement(lastIdx), true);
        }

        double offsetInSlot = repeaterPos.Y - (estimatedIndex * ItemHeight);
        bool below = offsetInSlot >= (ItemHeight / 2.0);
        return (estimatedIndex, _repeater.TryGetElement(estimatedIndex), below);
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
        if (_dropIndicatorLine != null)
            _dropIndicatorLine.IsVisible = false;
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

    private ScrollViewer? EnsureScrollViewer()
    {
        _scrollViewer ??= this.FindAncestorOfType<ScrollViewer>();
        return _scrollViewer;
    }

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
    }

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
        {
            _autoScrollOffset = -AutoScrollAmount;
            _autoScrollTimer?.Start();
        }
        else if (pos.Y > _scrollViewer.Bounds.Height - AutoScrollMargin)
        {
            _autoScrollOffset = AutoScrollAmount;
            _autoScrollTimer?.Start();
        }
        else
        {
            _autoScrollOffset = 0;
            _autoScrollTimer?.Stop();
        }
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

        if (Items is IVirtualTrackList virtualList)
        {
            int itemIndex = virtualList.IndexOf(vm);
            if (itemIndex >= 0 && !virtualList.IsIndexSelected(itemIndex))
            {
                virtualList.ClearSelection();
                virtualList.SetIndexSelected(itemIndex, true);
                _selectionAnchorIndex = itemIndex;
                _leadIndex = itemIndex;
            }
        }

        vm.SelectionProvider = GetSelectedTrackInfos;
        ShowSharedFlyout(c, false);
    }

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
        SearchingText = L["Search_Searching"];
        LoadingMoreText = L["Search_LoadingMore"];
        EndOfListText = L["Search_EndOfList"];
    }

    private void UpdateItemsContext()
    {
        if (Items is not IVirtualTrackList virtualList) return;

        virtualList.IsPlaylistContext = IsPlaylistContext;
        virtualList.IsQueueContext = IsQueueContext;
        virtualList.SelectionProvider = GetSelectedTrackInfos;
    }
    private KeyModifiers _lastCopyClickModifiers;

    private void OnTrackCopyTitleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not TrackItemViewModel vm)
            return;

        bool withAuthor = _lastCopyClickModifiers.HasFlag(KeyModifiers.Shift);
        _lastCopyClickModifiers = KeyModifiers.None;
        vm.CopyTitleCommand.Execute(withAuthor);
    }
    #endregion
}
