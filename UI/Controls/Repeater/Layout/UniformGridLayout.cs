using System.Collections.Specialized;
using Avalonia.Logging;

namespace Avalonia.Layout
{
    public enum UniformGridLayoutItemsJustification
    {
        Start = 0,
        Center = 1,
        End = 2,
        SpaceAround = 3,
        SpaceBetween = 4,
        SpaceEvenly = 5,
    }

    public enum UniformGridLayoutItemsStretch
    {
        None = 0,
        Fill = 1,
        Uniform = 2,
    }

    public class UniformGridLayout : VirtualizingLayout, IFlowLayoutAlgorithmDelegates
    {
        public static readonly StyledProperty<UniformGridLayoutItemsJustification> ItemsJustificationProperty =
            AvaloniaProperty.Register<UniformGridLayout, UniformGridLayoutItemsJustification>(nameof(ItemsJustification));

        public static readonly StyledProperty<UniformGridLayoutItemsStretch> ItemsStretchProperty =
            AvaloniaProperty.Register<UniformGridLayout, UniformGridLayoutItemsStretch>(nameof(ItemsStretch));

        public static readonly StyledProperty<double> MinColumnSpacingProperty =
            AvaloniaProperty.Register<UniformGridLayout, double>(nameof(MinColumnSpacing));

        public static readonly StyledProperty<double> MinItemHeightProperty =
            AvaloniaProperty.Register<UniformGridLayout, double>(nameof(MinItemHeight));

        public static readonly StyledProperty<double> MinItemWidthProperty =
            AvaloniaProperty.Register<UniformGridLayout, double>(nameof(MinItemWidth));

        public static readonly StyledProperty<double> MinRowSpacingProperty =
            AvaloniaProperty.Register<UniformGridLayout, double>(nameof(MinRowSpacing));

        public static readonly StyledProperty<int> MaximumRowsOrColumnsProperty =
            AvaloniaProperty.Register<UniformGridLayout, int>(nameof(MaximumRowsOrColumns));

        public static readonly StyledProperty<Orientation> OrientationProperty =
            StackLayout.OrientationProperty.AddOwner<UniformGridLayout>();

        private readonly OrientationBasedMeasures _orientation = new OrientationBasedMeasures();
        private double _minItemWidth = double.NaN;
        private double _minItemHeight = double.NaN;
        private double _minRowSpacing;
        private double _minColumnSpacing;
        private UniformGridLayoutItemsJustification _itemsJustification;
        private UniformGridLayoutItemsStretch _itemsStretch;
        private int _maximumRowsOrColumns = int.MaxValue;

        public UniformGridLayout()
        {
            LayoutId = "UniformGridLayout";
        }

        static UniformGridLayout()
        {
            OrientationProperty.OverrideDefaultValue<UniformGridLayout>(Orientation.Horizontal);
        }

        public UniformGridLayoutItemsJustification ItemsJustification
        {
            get => GetValue(ItemsJustificationProperty);
            set => SetValue(ItemsJustificationProperty, value);
        }

        public UniformGridLayoutItemsStretch ItemsStretch
        {
            get => GetValue(ItemsStretchProperty);
            set => SetValue(ItemsStretchProperty, value);
        }

        public double MinColumnSpacing
        {
            get => GetValue(MinColumnSpacingProperty);
            set => SetValue(MinColumnSpacingProperty, value);
        }

        public double MinItemHeight
        {
            get => GetValue(MinItemHeightProperty);
            set => SetValue(MinItemHeightProperty, value);
        }

        public double MinItemWidth
        {
            get => GetValue(MinItemWidthProperty);
            set => SetValue(MinItemWidthProperty, value);
        }

        public double MinRowSpacing
        {
            get => GetValue(MinRowSpacingProperty);
            set => SetValue(MinRowSpacingProperty, value);
        }

        public int MaximumRowsOrColumns
        {
            get => GetValue(MaximumRowsOrColumnsProperty);
            set => SetValue(MaximumRowsOrColumnsProperty, value);
        }

        public Orientation Orientation
        {
            get => GetValue(OrientationProperty);
            set => SetValue(OrientationProperty, value);
        }

        internal double LineSpacing => Orientation == Orientation.Horizontal ? _minRowSpacing : _minColumnSpacing;
        internal double MinItemSpacing => Orientation == Orientation.Horizontal ? _minColumnSpacing : _minRowSpacing;

        Size IFlowLayoutAlgorithmDelegates.Algorithm_GetMeasureSize(
            int index,
            Size availableSize,
            VirtualizingLayoutContext context)
        {
            _ = index;
            _ = availableSize;
            var gridState = (UniformGridLayoutState)context.LayoutState!;
            return new Size(gridState.EffectiveItemWidth, gridState.EffectiveItemHeight);
        }

        Size IFlowLayoutAlgorithmDelegates.Algorithm_GetProvisionalArrangeSize(
            int index,
            Size measureSize,
            Size desiredSize,
            VirtualizingLayoutContext context)
        {
            _ = index;
            _ = measureSize;
            _ = desiredSize;
            var gridState = (UniformGridLayoutState)context.LayoutState!;
            return new Size(gridState.EffectiveItemWidth, gridState.EffectiveItemHeight);
        }

        bool IFlowLayoutAlgorithmDelegates.Algorithm_ShouldBreakLine(int index, double remainingSpace)
        {
            _ = index;
            return remainingSpace < 0;
        }

        FlowLayoutAnchorInfo IFlowLayoutAlgorithmDelegates.Algorithm_GetAnchorForRealizationRect(
            Size availableSize,
            VirtualizingLayoutContext context)
        {
            Rect bounds = new Rect(double.NaN, double.NaN, double.NaN, double.NaN);
            int anchorIndex = -1;

            int itemsCount = context.ItemCount;
            var realizationRect = context.RealizationRect;
            if (itemsCount > 0 && _orientation.MajorSize(realizationRect) > 0)
            {
                var gridState = (UniformGridLayoutState)context.LayoutState!;
                var lastExtent = gridState.FlowAlgorithm.LastExtent;
                var itemsPerLine = Math.Min(
                    Math.Max(1u, (uint)(_orientation.Minor(availableSize) / GetMinorSizeWithSpacing(context))),
                    Math.Max(1u, (uint)_maximumRowsOrColumns));
                var majorSize = itemsCount / itemsPerLine * GetMajorSizeWithSpacing(context);
                var realizationWindowStartWithinExtent = _orientation.MajorStart(realizationRect) - _orientation.MajorStart(lastExtent);
                if ((realizationWindowStartWithinExtent + _orientation.MajorSize(realizationRect)) >= 0 && realizationWindowStartWithinExtent <= majorSize)
                {
                    double offset = Math.Max(0.0, _orientation.MajorStart(realizationRect) - _orientation.MajorStart(lastExtent));
                    int anchorRowIndex = (int)(offset / GetMajorSizeWithSpacing(context));

                    anchorIndex = (int)Math.Max(0, Math.Min(itemsCount - 1, anchorRowIndex * itemsPerLine));
                    bounds = GetLayoutRectForDataIndex(availableSize, anchorIndex, lastExtent, context);
                }
            }

            return new FlowLayoutAnchorInfo
            {
                Index = anchorIndex,
                Offset = _orientation.MajorStart(bounds)
            };
        }

        FlowLayoutAnchorInfo IFlowLayoutAlgorithmDelegates.Algorithm_GetAnchorForTargetElement(
            int targetIndex,
            Size availableSize,
            VirtualizingLayoutContext context)
        {
            int index = -1;
            double offset = double.NaN;
            int count = context.ItemCount;
            if (targetIndex >= 0 && targetIndex < count)
            {
                int itemsPerLine = (int)Math.Min(
                    Math.Max(1u, (uint)(_orientation.Minor(availableSize) / GetMinorSizeWithSpacing(context))),
                    Math.Max(1u, _maximumRowsOrColumns));
                int indexOfFirstInLine = targetIndex / itemsPerLine * itemsPerLine;
                index = indexOfFirstInLine;
                var state = (UniformGridLayoutState)context.LayoutState!;
                offset = _orientation.MajorStart(GetLayoutRectForDataIndex(availableSize, indexOfFirstInLine, state.FlowAlgorithm.LastExtent, context));
            }

            return new FlowLayoutAnchorInfo
            {
                Index = index,
                Offset = offset
            };
        }

        Rect IFlowLayoutAlgorithmDelegates.Algorithm_GetExtent(
            Size availableSize,
            VirtualizingLayoutContext context,
            Layoutable? firstRealized,
            int firstRealizedItemIndex,
            Rect firstRealizedLayoutBounds,
            Layoutable? lastRealized,
            int lastRealizedItemIndex,
            Rect lastRealizedLayoutBounds)
        {
            _ = lastRealized;
            var extent = new Rect();

            int itemsCount = context.ItemCount;
            double availableSizeMinor = _orientation.Minor(availableSize);
            int itemsPerLine =
                (int)Math.Min(
                    Math.Max(1u, !double.IsInfinity(availableSizeMinor)
                        ? (uint)(availableSizeMinor / GetMinorSizeWithSpacing(context))
                        : (uint)itemsCount),
                Math.Max(1u, _maximumRowsOrColumns));
            double lineSize = GetMajorSizeWithSpacing(context);

            if (itemsCount > 0)
            {
                _orientation.SetMinorSize(
                    ref extent,
                    !double.IsInfinity(availableSizeMinor) && _itemsStretch == UniformGridLayoutItemsStretch.Fill ?
                    availableSizeMinor :
                    Math.Max(0.0, itemsPerLine * GetMinorSizeWithSpacing(context) - MinItemSpacing));
                _orientation.SetMajorSize(
                    ref extent,
                    Math.Max(0.0, itemsCount / itemsPerLine * lineSize - LineSpacing));

                if (firstRealized != null)
                {
                    _orientation.SetMajorStart(
                        ref extent,
                        _orientation.MajorStart(firstRealizedLayoutBounds) - firstRealizedItemIndex / itemsPerLine * lineSize);
                    int remainingItems = itemsCount - lastRealizedItemIndex - 1;
                    _orientation.SetMajorSize(
                        ref extent,
                        _orientation.MajorEnd(lastRealizedLayoutBounds) - _orientation.MajorStart(extent) + remainingItems / itemsPerLine * lineSize);
                }
                else
                {
                    Logger.TryGet(LogEventLevel.Verbose, "Repeater")?.Log(this, "{LayoutId}: Estimating extent with no realized elements",
                        LayoutId);
                }
            }

            Logger.TryGet(LogEventLevel.Verbose, "Repeater")?.Log(this, "{LayoutId}: Extent is ({Size}). Based on lineSize {LineSize} and items per line {ItemsPerLine}",
                LayoutId, extent.Size, lineSize, itemsPerLine);
            return extent;
        }

        void IFlowLayoutAlgorithmDelegates.Algorithm_OnElementMeasured(Layoutable element, int index, Size availableSize, Size measureSize, Size desiredSize, Size provisionalArrangeSize, VirtualizingLayoutContext context)
        {
            _ = element;
            _ = index;
            _ = availableSize;
            _ = measureSize;
            _ = desiredSize;
            _ = provisionalArrangeSize;
            _ = context;
        }

        void IFlowLayoutAlgorithmDelegates.Algorithm_OnLineArranged(int startIndex, int countInLine, double lineSize, VirtualizingLayoutContext context)
        {
            _ = startIndex;
            _ = countInLine;
            _ = lineSize;
            _ = context;
        }

        protected internal override void InitializeForContextCore(VirtualizingLayoutContext context)
        {
            var state = context.LayoutState;
            var gridState = state as UniformGridLayoutState;

            if (gridState == null)
            {
                if (state != null)
                {
                    throw new InvalidOperationException("LayoutState must derive from UniformGridLayoutState.");
                }

                gridState = new UniformGridLayoutState();
            }

            gridState.InitializeForContext(context, this);
        }

        protected internal override void UninitializeForContextCore(VirtualizingLayoutContext context)
        {
            var gridState = (UniformGridLayoutState)context.LayoutState!;
            gridState.UninitializeForContext(context);
        }

        protected internal override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
        {
            var gridState = (UniformGridLayoutState)context.LayoutState!;
            gridState.EnsureElementSize(availableSize, context, _minItemWidth, _minItemHeight, _itemsStretch, Orientation, MinRowSpacing, MinColumnSpacing, _maximumRowsOrColumns);

            var desiredSize = GetFlowAlgorithm(context).Measure(
                availableSize,
                true,
                MinItemSpacing,
                LineSpacing,
                _maximumRowsOrColumns,
                _orientation.ScrollOrientation,
                false,
                LayoutId);

            gridState.EnsureFirstElementOwnership(context);

            return desiredSize;
        }

        protected internal override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
        {
            var value = GetFlowAlgorithm(context).Arrange(
               finalSize,
               true,
               (FlowLayoutAlgorithm.LineAlignment)_itemsJustification,
               LayoutId);
            return new Size(value.Width, value.Height);
        }

        protected internal override void OnItemsChangedCore(VirtualizingLayoutContext context, object? source, NotifyCollectionChangedEventArgs args)
        {
            GetFlowAlgorithm(context).OnItemsSourceChanged(source, args);
            InvalidateLayout();

            var gridState = (UniformGridLayoutState)context.LayoutState!;
            gridState.ClearElementOnDataSourceChange(context, args);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == OrientationProperty)
            {
                var orientation = change.GetNewValue<Orientation>();
                var scrollOrientation = (orientation == Orientation.Horizontal) ? ScrollOrientation.Vertical : ScrollOrientation.Horizontal;
                _orientation.ScrollOrientation = scrollOrientation;
            }
            else if (change.Property == MinColumnSpacingProperty)
            {
                _minColumnSpacing = change.GetNewValue<double>();
            }
            else if (change.Property == MinRowSpacingProperty)
            {
                _minRowSpacing = change.GetNewValue<double>();
            }
            else if (change.Property == ItemsJustificationProperty)
            {
                _itemsJustification = change.GetNewValue<UniformGridLayoutItemsJustification>();
            }
            else if (change.Property == ItemsStretchProperty)
            {
                _itemsStretch = change.GetNewValue<UniformGridLayoutItemsStretch>();
            }
            else if (change.Property == MinItemWidthProperty)
            {
                _minItemWidth = change.GetNewValue<double>();
            }
            else if (change.Property == MinItemHeightProperty)
            {
                _minItemHeight = change.GetNewValue<double>();
            }
            else if (change.Property == MaximumRowsOrColumnsProperty)
            {
                _maximumRowsOrColumns = change.GetNewValue<int>();
            }

            InvalidateLayout();
        }

        private double GetMinorSizeWithSpacing(VirtualizingLayoutContext context)
        {
            var minItemSpacing = MinItemSpacing;
            var gridState = (UniformGridLayoutState)context.LayoutState!;
            return _orientation.ScrollOrientation == ScrollOrientation.Vertical ?
                gridState.EffectiveItemWidth + minItemSpacing :
                gridState.EffectiveItemHeight + minItemSpacing;
        }

        private double GetMajorSizeWithSpacing(VirtualizingLayoutContext context)
        {
            var lineSpacing = LineSpacing;
            var gridState = (UniformGridLayoutState)context.LayoutState!;
            return _orientation.ScrollOrientation == ScrollOrientation.Vertical ?
                gridState.EffectiveItemHeight + lineSpacing :
                gridState.EffectiveItemWidth + lineSpacing;
        }

        private Rect GetLayoutRectForDataIndex(
            Size availableSize,
            int index,
            Rect lastExtent,
            VirtualizingLayoutContext context)
        {
            int itemsPerLine = (int)Math.Min(
                Math.Max(1u, (uint)(_orientation.Minor(availableSize) / GetMinorSizeWithSpacing(context))),
                Math.Max(1u, _maximumRowsOrColumns));
            int rowIndex = index / itemsPerLine;
            int indexInRow = index - (rowIndex * itemsPerLine);

            var gridState = (UniformGridLayoutState)context.LayoutState!;
            Rect bounds = _orientation.MinorMajorRect(
                indexInRow * GetMinorSizeWithSpacing(context) + _orientation.MinorStart(lastExtent),
                rowIndex * GetMajorSizeWithSpacing(context) + _orientation.MajorStart(lastExtent),
                _orientation.ScrollOrientation == ScrollOrientation.Vertical ? gridState.EffectiveItemWidth : gridState.EffectiveItemHeight,
                _orientation.ScrollOrientation == ScrollOrientation.Vertical ? gridState.EffectiveItemHeight : gridState.EffectiveItemWidth);

            return bounds;
        }

        private void InvalidateLayout() => InvalidateMeasure();

        private static FlowLayoutAlgorithm GetFlowAlgorithm(VirtualizingLayoutContext context) => ((UniformGridLayoutState)context.LayoutState!).FlowAlgorithm;
    }
}