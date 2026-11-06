using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace LMP.UI.Controls;

public partial class TrackListControl
{
    /// <summary>
    /// Provides ultra-smooth interpolation for ScrollViewer scrolling.
    /// Supports both mouse wheel scrolling (with accumulation) and smooth "catch-up"
    /// when clicking on the scrollbar track or pressing keys (PageUp/PageDown, arrows).
    /// Automatically disables during manual thumb dragging to prevent lag.
    /// </summary>
    private sealed class SnapScrollHelper : IDisposable
    {
        #region Constants

        private const double
            ScrollStep = ItemHeight * 2; // Exactly 2 tracks (124px) per mouse notch without cumulative drift

        private const double Smoothness = 16.0;
        private const double Epsilon = 0.5;

        #endregion

        #region Fields

        private readonly ScrollViewer _sv;
        private ScrollBar? _verticalScrollBar;
        private TopLevel? _topLevel;

        private double _targetY;
        private double _currentY;
        private bool _isAnimating;
        private bool _isUpdatingOffset;
        private bool _isDraggingScrollbar;
        private bool _disposed;
        private long _lastTickTimestamp;

        #endregion

        /// <summary>
        /// Initializes a new instance of the smooth scroll helper.
        /// </summary>
        /// <param name="sv">Target ScrollViewer.</param>
        public SnapScrollHelper(ScrollViewer sv)
        {
            _sv = sv;
            _currentY = sv.Offset.Y;
            _targetY = _currentY;
            _topLevel = TopLevel.GetTopLevel(sv);

            sv.AddHandler(
                PointerWheelChangedEvent,
                OnWheel,
                RoutingStrategies.Tunnel,
                handledEventsToo: false);

            sv.ScrollChanged += OnScrollChanged;

            Dispatcher.UIThread.Post(InitializeScrollBar, DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Starts smooth animation to the specified Y coordinate.
        /// </summary>
        public void AnimateTo(double targetY)
        {
            double maxScroll = GetMaxScrollY();
            _targetY = Math.Clamp(targetY, 0, maxScroll);
            StartAnimation();
        }

        /// <summary>
        /// Instantly stops scroll interpolation.
        /// </summary>
        public void CancelAnimation()
        {
            StopAnimation();
            _targetY = _sv.Offset.Y;
            _currentY = _sv.Offset.Y;
        }

        /// <summary>
        /// Performs an instant jump to the specified coordinate without triggering smoothing or position rollback.
        /// </summary>
        /// <param name="targetY">Target scroll Y coordinate.</param>
        public void JumpTo(double targetY)
        {
            StopAnimation();
            double maxScroll = GetMaxScrollY();
            double clamped = Math.Clamp(targetY, 0, maxScroll);

            _currentY = clamped;
            _targetY = clamped;

            ApplyOffset(clamped);
        }

        private void InitializeScrollBar()
        {
            if (_disposed) return;

            _verticalScrollBar = _sv.GetTemplateDescendants().OfType<ScrollBar>()
                .FirstOrDefault(x => x.Name == "PART_VerticalScrollBar");
            _verticalScrollBar ??= _sv.FindDescendantOfType<ScrollBar>();

            if (_verticalScrollBar != null)
            {
                _verticalScrollBar.Scroll += OnScrollBarScroll;

                _verticalScrollBar.AddHandler(
                    PointerReleasedEvent,
                    OnScrollBarPointerReleased,
                    RoutingStrategies.Bubble,
                    handledEventsToo: true);
            }
        }

        private void OnScrollBarScroll(object? sender, ScrollEventArgs e)
        {
            if (e.ScrollEventType == ScrollEventType.ThumbTrack)
            {
                _isDraggingScrollbar = true;
                _currentY = e.NewValue;
                _targetY = e.NewValue;
            }
            else
            {
                _isDraggingScrollbar = false;
            }
        }

        private void OnScrollBarPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            _isDraggingScrollbar = false;
        }

        private void OnWheel(object? sender, PointerWheelEventArgs e)
        {
            if (e.Delta.Y == 0 || _isDraggingScrollbar) return;

            e.Handled = true;

            double maxScroll = GetMaxScrollY();
            if (maxScroll <= 0) return;

            double direction = -Math.Sign(e.Delta.Y);
            double desiredY = _targetY + direction * ScrollStep;

            // Align the target coordinate to the track slot grid to prevent subpixel error accumulation
            double snappedY = Math.Round(desiredY / ItemHeight) * ItemHeight;
            _targetY = Math.Clamp(snappedY, 0, maxScroll);

            StartAnimation();
        }

        private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (_isUpdatingOffset || _disposed) return;

            double nativeY = _sv.Offset.Y;

            if (_isDraggingScrollbar)
            {
                _currentY = nativeY;
                _targetY = nativeY;
                return;
            }

            if (Math.Abs(nativeY - _currentY) > Epsilon)
            {
                double maxScroll = GetMaxScrollY();

                _currentY = Math.Min(_currentY, maxScroll);
                _targetY = Math.Clamp(nativeY, 0, maxScroll);

                ApplyOffset(_currentY);
                StartAnimation();
            }
        }

        private void OnAnimationFrame(TimeSpan elapsed)
        {
            if (!_isAnimating || _disposed || _isDraggingScrollbar) return;

            long currentTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            double dt = (double)(currentTimestamp - _lastTickTimestamp) / System.Diagnostics.Stopwatch.Frequency;
            _lastTickTimestamp = currentTimestamp;

            if (dt > 0.05) dt = 0.05;
            if (dt <= 0.0) return;

            double maxScroll = GetMaxScrollY();
            _targetY = Math.Clamp(_targetY, 0, maxScroll);

            if (Math.Abs(_targetY - _currentY) < Epsilon)
            {
                _currentY = _targetY;
                ApplyOffset(_currentY);
                StopAnimation();
                return;
            }

            double factor = 1.0 - Math.Exp(-Smoothness * dt);
            _currentY += (_targetY - _currentY) * factor;

            ApplyOffset(_currentY);

            (_topLevel ??= TopLevel.GetTopLevel(_sv))?.RequestAnimationFrame(OnAnimationFrame);
        }

        private void StartAnimation()
        {
            if (_isAnimating || _isDraggingScrollbar) return;

            _isAnimating = true;
            _lastTickTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

            (_topLevel ??= TopLevel.GetTopLevel(_sv))?.RequestAnimationFrame(OnAnimationFrame);
        }

        private void StopAnimation()
        {
            _isAnimating = false;
        }

        private double GetMaxScrollY()
        {
            return Math.Max(0, _sv.Extent.Height - _sv.Viewport.Height);
        }

        private void ApplyOffset(double y)
        {
            _isUpdatingOffset = true;
            _sv.Offset = new Vector(_sv.Offset.X, y);
            _isUpdatingOffset = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _isAnimating = false;
            _isDraggingScrollbar = false;
            _topLevel = null;

            _sv.RemoveHandler(PointerWheelChangedEvent, OnWheel);
            _sv.ScrollChanged -= OnScrollChanged;

            if (_verticalScrollBar != null)
            {
                _verticalScrollBar.Scroll -= OnScrollBarScroll;
                _verticalScrollBar.RemoveHandler(PointerReleasedEvent, OnScrollBarPointerReleased);
                _verticalScrollBar = null;
            }
        }
    }
}
