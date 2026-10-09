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
    /// Обеспечивает ультра-плавную интерполяцию прокрутки ScrollViewer.
    /// Поддерживает как прокрутку колесиком (с накоплением), так и плавное «догоняние»
    /// при кликах по треку скроллбара и нажатиях клавиш (PageUp/PageDown, стрелки).
    /// Автоматически отключается при ручном перетаскивании ползунка мыши, исключая лаги.
    /// </summary>
    private sealed class SnapScrollHelper : IDisposable
    {
        #region Constants

        private const double
            ScrollStep = ItemHeight * 2; // Ровно 2 трека (124px) за щелчок мыши без накопительного сдвига

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
        /// Инициализирует новый экземпляр помощника плавной прокрутки.
        /// </summary>
        /// <param name="sv">Целевой ScrollViewer.</param>
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
        /// Запускает плавную анимацию до заданной координаты Y.
        /// </summary>
        public void AnimateTo(double targetY)
        {
            double maxScroll = GetMaxScrollY();
            _targetY = Math.Clamp(targetY, 0, maxScroll);
            StartAnimation();
        }

        /// <summary>
        /// Мгновенно останавливает интерполяцию скролла.
        /// </summary>
        public void CancelAnimation()
        {
            StopAnimation();
            _targetY = _sv.Offset.Y;
            _currentY = _sv.Offset.Y;
        }

        /// <summary>
        /// Выполняет мгновенный переход к заданной координате без запуска сглаживания и без отката позиции.
        /// </summary>
        /// <param name="targetY">Целевая Y-координата скролла.</param>
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

            // Выравниваем целевую координату по сетке слотов треков, исключая субпиксельное накопление ошибки
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
