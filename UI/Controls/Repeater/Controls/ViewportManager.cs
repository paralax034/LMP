// This source file is adapted from the WinUI project.
// (https://github.com/microsoft/microsoft-ui-xaml)
//
// Licensed to The Avalonia Project under MIT License, courtesy of The .NET Foundation.

using Avalonia.Layout;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Avalonia.Controls
{
    internal class ViewportManager
    {
        private readonly ItemsRepeater _owner;
        private bool _ensuredScroller;
        private IScrollAnchorProvider? _scroller;
        private Control? _makeAnchorElement;
        private Rect _visibleWindow;
        private Rect _layoutExtent;
        private double _maximumHorizontalCacheLength = 2.0;
        private double _maximumVerticalCacheLength = 2.0;
        private bool _isBringIntoViewInProgress;
        // For non-virtualizing layouts, we do not need to keep
        // updating viewports and invalidating measure often. So when
        // a non virtualizing layout is used, we stop doing all that work.
        private bool _managingViewportDisabled;
        private bool _effectiveViewportChangedSubscribed;

        public ViewportManager(ItemsRepeater owner)
        {
            _owner = owner;
        }

        public Control? SuggestedAnchor
        {
            get
            {
                // The element generated during the ItemsRepeater.MakeAnchor call has precedence over the next tick.
                var suggestedAnchor = _makeAnchorElement;
                var owner = _owner;

                if (suggestedAnchor == null)
                {
                    var anchorElement = _scroller?.CurrentAnchor;

                    if (anchorElement != null)
                    {
                        // We can't simply return anchorElement because, in case of nested Repeaters, it may not
                        // be a direct child of ours, or even an indirect child. We need to walk up the tree starting
                        // from anchorElement to figure out what child of ours (if any) to use as the suggested element.
                        var child = anchorElement;
                        var parent = child.GetVisualParent() as Control;

                        while (parent != null)
                        {
                            if (parent == owner)
                            {
                                suggestedAnchor = child;
                                break;
                            }

                            child = parent;
                            parent = parent.GetVisualParent() as Control;
                        }
                    }
                }

                return suggestedAnchor;
            }
        }

        public bool HasScroller => _scroller != null;

        public Control? MadeAnchor => _makeAnchorElement;

        public double HorizontalCacheLength
        {
            get => _maximumHorizontalCacheLength;
            set
            {
                if (_maximumHorizontalCacheLength != value)
                {
                    ValidateCacheLength(value);
                    _maximumHorizontalCacheLength = value;
                }
            }
        }

        public double VerticalCacheLength
        {
            get => _maximumVerticalCacheLength;
            set
            {
                if (_maximumVerticalCacheLength != value)
                {
                    ValidateCacheLength(value);
                    _maximumVerticalCacheLength = value;
                }
            }
        }

        public Rect GetLayoutVisibleWindow()
        {
            var visibleWindow = _visibleWindow;

            if (_makeAnchorElement != null)
            {
                visibleWindow = visibleWindow.WithX(0).WithY(0);
            }

            return visibleWindow;
        }

        public Rect GetLayoutRealizationWindow()
        {
            var visibleWindow = GetLayoutVisibleWindow();
            if (HasScroller && !_managingViewportDisabled)
            {
                // Compute buffer immediately on the fly so initial layout pass pre-realizes
                // the full buffer before user even starts scrolling (eliminates 1st-4th scroll tick latency).
                double horizBuffer = _maximumHorizontalCacheLength * visibleWindow.Width / 2.0;
                double vertBuffer = _maximumVerticalCacheLength * visibleWindow.Height / 2.0;

                return new Rect(
                    visibleWindow.X - horizBuffer,
                    visibleWindow.Y - vertBuffer,
                    visibleWindow.Width + horizBuffer * 2.0,
                    visibleWindow.Height + vertBuffer * 2.0);
            }

            return visibleWindow;
        }

        public void SetLayoutExtent(Rect extent)
        {
            _layoutExtent = extent;
            // Native Avalonia 12 manages ScrollViewer extent directly from MeasureOverride return value.
            // Redundant scroller InvalidateArrange() calls and WinUI layout shifts are eliminated.
        }

        public Point GetOrigin() => _layoutExtent.TopLeft;

        public void OnLayoutChanged(bool isVirtualizing)
        {
            _managingViewportDisabled = !isVirtualizing;
            _layoutExtent = default;

            if (_managingViewportDisabled && _effectiveViewportChangedSubscribed)
            {
                _owner.EffectiveViewportChanged -= OnEffectiveViewportChanged;
                _effectiveViewportChangedSubscribed = false;
            }
            else if (!_managingViewportDisabled && !_effectiveViewportChangedSubscribed)
            {
                _owner.EffectiveViewportChanged += OnEffectiveViewportChanged;
                _effectiveViewportChangedSubscribed = true;
            }
        }

        public void OnElementPrepared(VirtualizationInfo virtInfo)
        {
            // WinUI registers the element as an anchor candidate here, but I feel that's in error:
            // at this point the element has not yet been positioned by the arrange pass so it will
            // have its previous position, meaning that when the arrange pass moves it into its new
            // position, an incorrect scroll anchoring will occur. Instead signal that it's not yet
            // registered as a scroll anchor candidate.
            virtInfo.IsRegisteredAsAnchorCandidate = false;
        }

        public void OnElementCleared(Control element, VirtualizationInfo virtInfo)
        {
            _scroller?.UnregisterAnchorCandidate(element);
            virtInfo.IsRegisteredAsAnchorCandidate = false;
        }

        public void OnOwnerMeasuring()
        {
            // This is because of a bug that causes effective viewport to not
            // fire if you register during arrange.
            // Bug 17411076: EffectiveViewport: registering for effective viewport in arrange should invalidate viewport
            EnsureScroller();
        }

        public void OnMakeAnchor(Control? anchor)
        {
            if (_makeAnchorElement != anchor)
            {
                _makeAnchorElement = anchor;
            }
        }

        public void OnBringIntoViewRequested(RequestBringIntoViewEventArgs args)
        {
            if (!_managingViewportDisabled)
            {
                // During the time between a bring into view request and the element coming into view we do not
                // want the anchor provider to pick some anchor and jump to it. Instead we want to anchor on the
                // element that is being brought into view. We can do this by making just that element as a potential
                // anchor candidate and ensure no other element of this repeater is an anchor candidate.
                // Once the layout pass is done and we render the frame, the element will be in frame and we can
                // switch back to letting the anchor provider pick a suitable anchor.

                // get the targetChild - i.e the immediate child of this repeater that is being brought into view.
                // Note that the element being brought into view could be a descendant.
                var targetChild = GetImmediateChildOfRepeater((Control)args.TargetObject!);

                if (targetChild is null)
                {
                    return;
                }

                // Make sure that only the target child can be the anchor during the bring into view operation.
                if (_scroller is object)
                {
                    foreach (var child in _owner.Children)
                    {
                        var info = ItemsRepeater.GetVirtualizationInfo(child);

                        if (child != targetChild && info.IsRegisteredAsAnchorCandidate)
                        {
                            _scroller.UnregisterAnchorCandidate(child);
                            info.IsRegisteredAsAnchorCandidate = false;
                        }
                    }
                }

                // Register action to go back to how things were before where any child can be the anchor. Here,
                // WinUI uses CompositionTarget.Rendering but we don't currently have that, so post an action to
                // run *after* rendering has completed (priority needs to be lower than Render as Transformed
                // bounds must have been set in order for OnEffectiveViewportChanged to trigger).
                if (!_isBringIntoViewInProgress)
                {
                    _isBringIntoViewInProgress = true;
                    Dispatcher.UIThread.Post(OnCompositionTargetRendering, DispatcherPriority.Loaded);
                }
            }
        }

        public void RegisterScrollAnchorCandidate(Control element, VirtualizationInfo virtInfo)
        {
            if (!virtInfo.IsRegisteredAsAnchorCandidate)
            {
                _scroller?.RegisterAnchorCandidate(element);
                virtInfo.IsRegisteredAsAnchorCandidate = true;
            }
        }

        private Control? GetImmediateChildOfRepeater(Control descendant)
        {
            var targetChild = descendant;
            var parent = (Control?)descendant.GetVisualParent();
            while (parent != null && parent != _owner)
            {
                targetChild = parent;
                parent = (Control?)parent.GetVisualParent();
            }

            if (parent == null)
            {
                return null;
            }

            return targetChild;
        }

        private void OnCompositionTargetRendering()
        {
            _isBringIntoViewInProgress = false;
            _makeAnchorElement = null;

            // Undo the anchor deregistrations done by OnBringIntoViewRequested.
            if (_scroller is object)
            {
                foreach (var child in _owner.Children)
                {
                    var info = ItemsRepeater.GetVirtualizationInfo(child);

                    // The item brought into view is still registered - don't register it more than once.
                    if (info.IsRealized && info.IsHeldByLayout && !info.IsRegisteredAsAnchorCandidate)
                    {
                        _scroller.RegisterAnchorCandidate(child);
                        info.IsRegisteredAsAnchorCandidate = true;
                    }
                }
            }

            // HACK: Invalidate measure now that the anchor has been removed so that a layout can be
            // done with a proper realization rect. This is a hack not present upstream to try to fix
            // https://github.com/microsoft/microsoft-ui-xaml/issues/1422
            TryInvalidateMeasure();
        }

        public void ResetScrollers()
        {
            if (_scroller is object)
            {
                foreach (var child in _owner.Children)
                {
                    var info = ItemsRepeater.GetVirtualizationInfo(child);

                    if (info.IsRegisteredAsAnchorCandidate)
                    {
                        _scroller.UnregisterAnchorCandidate(child);
                        info.IsRegisteredAsAnchorCandidate = false;
                    }
                }

                _scroller = null;
            }

            _owner.EffectiveViewportChanged -= OnEffectiveViewportChanged;
            _effectiveViewportChangedSubscribed = false;
            _ensuredScroller = false;
        }

        private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
        {
            Logger.TryGet(LogEventLevel.Verbose, "Repeater")?.Log(this, "{LayoutId}: EffectiveViewportChanged event callback", _owner.Layout?.LayoutId);
            UpdateViewport(e.EffectiveViewport);

            if (_visibleWindow.Width == 0 && _visibleWindow.Height == 0)
            {
                // We got cleared.
                _layoutExtent = default;
            }
        }

        private void EnsureScroller()
        {
            if (!_ensuredScroller)
            {
                ResetScrollers();

                var parent = _owner.GetVisualParent();
                while (parent != null)
                {
                    if (parent is IScrollAnchorProvider scroller)
                    {
                        _scroller = scroller;
                        break;
                    }

                    parent = parent.GetVisualParent();
                }

                if (!_managingViewportDisabled)
                {
                    _owner.EffectiveViewportChanged += OnEffectiveViewportChanged;
                    _effectiveViewportChangedSubscribed = true;
                }

                _ensuredScroller = true;
            }
        }

        private void UpdateViewport(Rect viewport)
        {
            var currentVisibleWindow = viewport;
            var previousVisibleWindow = _visibleWindow;

            Logger.TryGet(LogEventLevel.Verbose, "Repeater")?.Log(this, "{LayoutId}: Effective Viewport: ({Before})->({After})",
                _owner.Layout?.LayoutId,
                previousVisibleWindow,
                viewport);

            if (-currentVisibleWindow.X <= ItemsRepeater.ClearedElementsArrangePosition.X &&
                -currentVisibleWindow.Y <= ItemsRepeater.ClearedElementsArrangePosition.Y)
            {
                Logger.TryGet(LogEventLevel.Verbose, "Repeater")?.Log(this, "{LayoutId}: Viewport is invalid. visible window cleared", _owner.Layout?.LayoutId);
                // We got cleared.
                _visibleWindow = default;
            }
            else
            {
                _visibleWindow = currentVisibleWindow;
            }

            if (_visibleWindow != previousVisibleWindow)
            {
                Logger.TryGet(LogEventLevel.Verbose, "Repeater")?.Log(this, "{LayoutId}: Used Viewport: ({Before})->({After})",
                    _owner.Layout?.LayoutId,
                    previousVisibleWindow,
                    currentVisibleWindow);
                TryInvalidateMeasure();
            }
        }

        private static void ValidateCacheLength(double cacheLength)
        {
            if (cacheLength < 0.0 || double.IsInfinity(cacheLength) || double.IsNaN(cacheLength))
            {
                throw new ArgumentException("The maximum cache length must be equal or superior to zero.");
            }
        }

        private void TryInvalidateMeasure()
        {
            // Don't invalidate measure if we have an invalid window.
            if (_visibleWindow.Width != 0 || _visibleWindow.Height != 0)
            {
                // We invalidate measure instead of just invalidating arrange because
                // we don't invalidate measure in UpdateViewport if the view is changing to
                // avoid layout cycles.
                Logger.TryGet(LogEventLevel.Verbose, "Repeater")?.Log(this, "{LayoutId}: Invalidating measure due to viewport change", _owner.Layout?.LayoutId);
                _owner.InvalidateMeasure();
            }
        }

        private class ScrollerInfo
        {
            public ScrollerInfo(ScrollViewer scroller)
            {
                Scroller = scroller;
            }

            public ScrollViewer Scroller { get; }
        }
    };
}
