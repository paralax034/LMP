using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using SkiaSharp;

namespace LMP.UI.Controls;

/// <summary>
/// Render-thread 4-bar equalizer playback indicator driven by <see cref="CompositionCustomVisualHandler"/>.
/// Eliminates UI-thread XAML KeyFrame animations and per-frame property invalidations.
/// </summary>
public sealed class PlayingBarsIndicator : Control
{
    private const int BarCount = 4;
    private const float BarWidth = 4f;
    private const float BarSpacing = 4f;
    private const float BarMaxHeight = 24f;
    private const float BarCornerRadius = 2f;
    private const float IdleScaleY = 0.21f;
    private const double MinFrameIntervalSeconds = 0.0155;
    private const double MaxFrameDeltaSeconds = 0.050;

    private static readonly object MsgStop = new();
    private static readonly object MsgSyncState = new();

    private struct IndicatorSnapshot
    {
        public bool IsPlaying;
        public bool CanAnimate;
        public SKColor Color;
    }

    private sealed class PlayingBarsHandler : CompositionCustomVisualHandler
    {
        private static readonly float[] MinScales = [0.25f, 0.25f, 0.33f, 0.25f];
        private static readonly float[] MaxScales = [0.92f, 1.00f, 0.83f, 0.75f];
        private static readonly float[] HalfPeriods = [0.50f, 0.70f, 0.60f, 0.55f];
        private static readonly float[] InitialPhases = [0f, MathF.PI, 0f, MathF.PI];

        [ThreadStatic] private static SKPaint? t_paint;

        private readonly PlayingBarsIndicator _owner;
        private IndicatorSnapshot _state;
        private float _activeMix;
        private float _pulsePhase;
        private bool _running;
        private bool _frameScheduled;
        private TimeSpan? _lastFrameTime;

        public PlayingBarsHandler(PlayingBarsIndicator owner) => _owner = owner;

        public override Rect GetRenderBounds()
        {
            double w = EffectiveSize.X;
            double h = EffectiveSize.Y;
            return w > 0.0 && h > 0.0 ? new Rect(0, 0, w, h) : default;
        }

        public override void OnMessage(object message)
        {
            if (ReferenceEquals(message, MsgStop))
            {
                _running = false;
                return;
            }

            if (!ReferenceEquals(message, MsgSyncState))
                return;

            lock (_owner._stateLock)
            {
                _state = _owner._snapshot;
                _owner._syncQueued = false;
            }

            if (!_state.CanAnimate)
            {
                _running = false;
                _activeMix = 0f;
                Invalidate();
                return;
            }

            Invalidate();

            if (_state.IsPlaying || _activeMix > 0.005f)
            {
                if (!_running)
                {
                    _running = true;
                    _lastFrameTime = null;
                    ScheduleNextFrame(forceRegister: true);
                }
            }
            else
            {
                _running = false;
            }
        }

        public override void OnAnimationFrameUpdate()
        {
            _frameScheduled = false;

            if (!_running || !_state.CanAnimate)
            {
                _running = false;
                return;
            }

            var now = CompositionNow;
            if (!_lastFrameTime.HasValue)
                _lastFrameTime = now - TimeSpan.FromMilliseconds(16.6);

            double dt = (now - _lastFrameTime.Value).TotalSeconds;
            if (dt < MinFrameIntervalSeconds)
            {
                ScheduleNextFrame();
                return;
            }

            _lastFrameTime = now;
            if (dt > MaxFrameDeltaSeconds)
                dt = MaxFrameDeltaSeconds;

            float fDt = (float)dt;
            if (_state.IsPlaying)
            {
                _activeMix = MathF.Min(1f, _activeMix + (fDt * 6.5f));
                _pulsePhase = (_pulsePhase + (fDt * (MathF.Tau / 3.6f))) % MathF.Tau;
            }
            else
            {
                _activeMix = MathF.Max(0f, _activeMix - (fDt * 7.0f));
            }

            Invalidate();

            if (_state.IsPlaying || _activeMix > 0.005f)
                ScheduleNextFrame();
            else
                _running = false;
        }

        public override void OnRender(ImmediateDrawingContext drawingContext)
        {
            double w = EffectiveSize.X;
            double h = EffectiveSize.Y;
            if (w <= 0.0 || h <= 0.0)
                return;

            var leaseFeature = drawingContext.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature == null)
                return;

            using var lease = leaseFeature.Lease();
            float opacity = (float)Math.Clamp(lease.CurrentOpacity, 0.0, 1.0);
            if (opacity <= 0.003f)
                return;

            if (_state.IsPlaying && _activeMix > 0.01f)
            {
                float breath = 0.80f + (0.20f * MathF.Cos(_pulsePhase));
                opacity *= 1f - _activeMix + (_activeMix * breath);
            }

            byte alpha = (byte)Math.Clamp((int)MathF.Round(_state.Color.Alpha * opacity), 0, 255);
            if (alpha == 0)
                return;

            var paint = t_paint ??= new SKPaint();
            paint.Reset();
            paint.IsAntialias = true;
            paint.Style = SKPaintStyle.Fill;
            paint.Color = new SKColor(_state.Color.Red, _state.Color.Green, _state.Color.Blue, alpha);

            float totalW = (BarCount * BarWidth) + ((BarCount - 1) * BarSpacing);
            float startX = ((float)w - totalW) * 0.5f;
            float centerY = (float)h * 0.5f;
            float timeSec = _state.IsPlaying || _activeMix > 0.005f
                ? (float)CompositionNow.TotalSeconds
                : 0f;

            var canvas = lease.SkCanvas;
            for (int i = 0; i < BarCount; i++)
            {
                float scaleY = IdleScaleY;
                if (_activeMix > 0.001f)
                {
                    float t = 0.5f - (0.5f * MathF.Cos((timeSec * MathF.PI / HalfPeriods[i]) + InitialPhases[i]));
                    float animScale = MinScales[i] + ((MaxScales[i] - MinScales[i]) * t);
                    scaleY = IdleScaleY + ((animScale - IdleScaleY) * _activeMix);
                }

                float barH = BarMaxHeight * scaleY;
                float x = startX + (i * (BarWidth + BarSpacing));
                float y = centerY - (barH * 0.5f);
                canvas.DrawRoundRect(x, y, BarWidth, barH, BarCornerRadius, BarCornerRadius, paint);
            }

            paint.Reset();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ScheduleNextFrame(bool forceRegister = false)
        {
            if (forceRegister || !_frameScheduled)
            {
                _frameScheduled = true;
                RegisterForNextAnimationFrameUpdate();
            }
        }
    }

    private readonly CompositionSilenceMonitor _silenceMonitor;
    private readonly EventHandler<AvaloniaPropertyChangedEventArgs> _brushChangedHandler;
    private readonly Lock _stateLock = new();

    private CompositionCustomVisual? _customVisual;
    private SolidColorBrush? _subscribedBrush;
    private IndicatorSnapshot _snapshot;
    private bool _syncQueued;
    private bool _isAttached;

    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<PlayingBarsIndicator, bool>(nameof(IsPlaying));

    public static readonly StyledProperty<IBrush?> BarBrushProperty =
        AvaloniaProperty.Register<PlayingBarsIndicator, IBrush?>(nameof(BarBrush));

    public bool IsPlaying
    {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public IBrush? BarBrush
    {
        get => GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public PlayingBarsIndicator()
    {
        IsHitTestVisible = false;
        _brushChangedHandler = OnBrushPropertyChanged;
        _silenceMonitor = new CompositionSilenceMonitor(
            this,
            shouldAnimate: static () => true,
            onStateReevaluated: PushStateToHandler,
            onResumeFromSilence: static () => { },
            shouldPollSilence: static () => false);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        _silenceMonitor.Attach();
        UpdateBrushSubscription(BarBrush);
        EnsureVisual(new Vector(Bounds.Width, Bounds.Height));
        PushStateToHandler();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        _silenceMonitor.Detach();
        UpdateBrushSubscription(null);

        if (_customVisual != null)
        {
            _customVisual.SendHandlerMessage(MsgStop);
            ElementComposition.SetElementChildVisual(this, null);
            _customVisual = null;
        }

        lock (_stateLock)
        {
            _syncQueued = false;
        }

        base.OnDetachedFromVisualTree(e);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = new Vector(finalSize.Width, finalSize.Height);
        bool created = EnsureVisual(size);
        if (_customVisual != null && _customVisual.Size != size)
        {
            _customVisual.Size = size;
            created = true;
        }

        if (created)
            PushStateToHandler();

        return finalSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == BarBrushProperty)
        {
            if (_isAttached)
                UpdateBrushSubscription(change.GetNewValue<IBrush?>());
            PushStateToHandler();
        }
        else if (change.Property == IsPlayingProperty)
        {
            PushStateToHandler();
        }
    }

    private void OnBrushPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == SolidColorBrush.ColorProperty)
            PushStateToHandler();
    }

    private void UpdateBrushSubscription(IBrush? brush)
    {
        var next = brush as SolidColorBrush;
        if (ReferenceEquals(_subscribedBrush, next))
            return;

        if (_subscribedBrush != null)
            _subscribedBrush.PropertyChanged -= _brushChangedHandler;

        _subscribedBrush = next;
        if (_subscribedBrush != null)
            _subscribedBrush.PropertyChanged += _brushChangedHandler;
    }

    private bool EnsureVisual(Vector size)
    {
        if (!_isAttached || _customVisual != null)
            return false;

        if (ElementComposition.GetElementVisual(this)?.Compositor is not { } compositor)
            return false;

        lock (_stateLock)
        {
            _syncQueued = false;
        }

        _customVisual = compositor.CreateCustomVisual(new PlayingBarsHandler(this));
        _customVisual.Size = size;
        ElementComposition.SetElementChildVisual(this, _customVisual);
        return true;
    }

    private void PushStateToHandler()
    {
        if (!_isAttached)
            return;

        EnsureVisual(new Vector(Bounds.Width, Bounds.Height));
        if (_customVisual == null)
            return;

        bool shouldSend = false;
        var color = ResolveColor();
        bool canAnimate = _silenceMonitor.CanAnimateNow;

        lock (_stateLock)
        {
            _snapshot = new IndicatorSnapshot
            {
                IsPlaying = IsPlaying,
                CanAnimate = canAnimate,
                Color = color
            };

            if (!_syncQueued)
            {
                _syncQueued = true;
                shouldSend = true;
            }
        }

        if (shouldSend)
            _customVisual.SendHandlerMessage(MsgSyncState);
    }

    private SKColor ResolveColor()
    {
        if (BarBrush is ISolidColorBrush solid)
        {
            var c = solid.Color;
            return new SKColor(c.R, c.G, c.B, c.A);
        }

        if (this.TryFindResource("TextSecondaryBrush", out var res) && res is ISolidColorBrush fallback)
        {
            var c = fallback.Color;
            return new SKColor(c.R, c.G, c.B, c.A);
        }

        return new SKColor(179, 179, 179, 255);
    }
}