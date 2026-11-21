using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Skia;
using LMP.Core.Audio.Helpers;
using SkiaSharp;

namespace LMP.UI.Controls;

/// <summary>
/// Interactive equalizer chart with a unified live wave:
/// 100% clickable area across the full height of the columns, 0 points, live frequency response of the playing track.
/// </summary>
public sealed class EqualizerCurveControl : Control
{
    private const double MaxDbRange = 12.0;
    private const double PaddingLeft = 18.0;
    private const double PaddingRight = 18.0;
    private const double PaddingTop = 20.0;
    private const double PaddingBottom = 22.0;
    private const double LerpSpeed = 18.0;
    private const double SpectrumLerpUp = 28.0;
    private const double SpectrumLerpDown = 10.0;
    private const double MinFrameIntervalSeconds = 0.0125;

    // Technical frequency labels in SI notation with a Latin "k", matching Hints5BandFreqs: do not translate
    private static readonly string[] Labels10Band = ["31", "62", "125", "250", "500", "1k", "2k", "4k", "8k", "16k"];
    private static readonly string[] Hints10BandKeys =
    [
        "Eq_Hint10_0", "Eq_Hint10_1", "Eq_Hint10_2", "Eq_Hint10_3", "Eq_Hint10_4",
        "Eq_Hint10_5", "Eq_Hint10_6", "Eq_Hint10_7", "Eq_Hint10_8", "Eq_Hint10_9"
    ];

    private static readonly string[] Keys5Band = ["Eq_Band_Bass", "Eq_Band_Warm", "Eq_Band_Vocal", "Eq_Band_Clear", "Eq_Band_Air"];
    private static readonly string[] Hints5BandFreqs = ["31–90 Hz", "125–350 Hz", "500–1.5k Hz", "2k–6k Hz", "8k–16k Hz"];

    #region Styled Properties

    public static readonly StyledProperty<IReadOnlyList<float>?> BandGainsProperty =
        AvaloniaProperty.Register<EqualizerCurveControl, IReadOnlyList<float>?>(nameof(BandGains));

    public static readonly StyledProperty<bool> IsSimple5BandModeProperty =
        AvaloniaProperty.Register<EqualizerCurveControl, bool>(nameof(IsSimple5BandMode), true);

    public static readonly StyledProperty<bool> IsEqEnabledProperty =
        AvaloniaProperty.Register<EqualizerCurveControl, bool>(nameof(IsEqEnabled), true);

    public IReadOnlyList<float>? BandGains
    {
        get => GetValue(BandGainsProperty);
        set => SetValue(BandGainsProperty, value);
    }

    public bool IsSimple5BandMode
    {
        get => GetValue(IsSimple5BandModeProperty);
        set => SetValue(IsSimple5BandModeProperty, value);
    }

    public bool IsEqEnabled
    {
        get => GetValue(IsEqEnabledProperty);
        set => SetValue(IsEqEnabledProperty, value);
    }

    /// <summary>
    /// Occurs when the user changes the bands (passes an array of 10 ISO bands in dB).
    /// </summary>
    public event Action<float[]>? BandsChanged;

    #endregion

    #region Fields

    private readonly float[] _targetIso10 = new float[EqualizerProcessor.BandCount];
    private readonly float[] _displayIso10 = new float[EqualizerProcessor.BandCount];
    private readonly float[] _cachedEqGainDb = new float[EqualizerProcessor.BandCount];
    private readonly float[] _cachedEqGainFactor = [1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f];
    private readonly float[] _spectrum10 = new float[EqualizerProcessor.BandCount];
    private readonly float[] _macro5Working = new float[5];
    private readonly float[] _emitBufferA = new float[EqualizerProcessor.BandCount];
    private readonly float[] _emitBufferB = new float[EqualizerProcessor.BandCount];
    private bool _emitBufferToggle;

    [InlineArray(EqualizerProcessor.BandCount)]
    private struct EqPoints10
    {
        private SKPoint _element0;
    }

    private sealed class EqWaveDrawOp : PooledDrawOperation<EqWaveDrawOp>
    {
        [ThreadStatic] private static SKPath? t_fillPath;
        [ThreadStatic] private static SKPath? t_linePath;
        [ThreadStatic] private static SKPaint? t_paint;
        [ThreadStatic] private static SKShader? t_fillShader;
        [ThreadStatic] private static uint t_shaderAccentKey;
        [ThreadStatic] private static float t_shaderTop;
        [ThreadStatic] private static float t_shaderBottom;
        [ThreadStatic] private static SKColor[]? t_gradColors;
        private static readonly float[] GradStops = [0.0f, 0.5f, 1.0f];

        private Rect _bounds;
        private EqPoints10 _points;
        private float _zeroY;
        private float _plotTop;
        private float _plotBottom;
        private SKColor _accentColor;
        private SKColor _mutedColor;
        private bool _isEqEnabled;

        public override Rect Bounds => _bounds;

        public static EqWaveDrawOp Rent(
            Rect bounds,
            Point[] pts,
            float zeroY,
            float plotTop,
            float plotBottom,
            SKColor accentColor,
            SKColor mutedColor,
            bool isEqEnabled)
        {
            var op = RentCore();
            op._bounds = bounds;
            for (int i = 0; i < EqualizerProcessor.BandCount; i++)
                op._points[i] = new SKPoint((float)pts[i].X, (float)pts[i].Y);
            op._zeroY = zeroY;
            op._plotTop = plotTop;
            op._plotBottom = plotBottom;
            op._accentColor = accentColor;
            op._mutedColor = mutedColor;
            op._isEqEnabled = isEqEnabled;
            return op;
        }

        public override void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature == null) return;

            using var lease = leaseFeature.Lease();
            float opacity = (float)Math.Clamp(lease.CurrentOpacity, 0.0, 1.0);
            if (opacity <= 0.003f) return;

            var fillPath = t_fillPath ??= new SKPath();
            var linePath = t_linePath ??= new SKPath();
            fillPath.Rewind();
            linePath.Rewind();

            ref readonly var first = ref _points[0];
            fillPath.MoveTo(first.X, _zeroY);
            fillPath.LineTo(first.X, first.Y);
            linePath.MoveTo(first.X, first.Y);

            const float tension = 1.0f / 6.0f;
            for (int i = 0; i < EqualizerProcessor.BandCount - 1; i++)
            {
                SKPoint p0 = _points[i];
                SKPoint p1 = _points[i + 1];
                SKPoint pPrev = i > 0 ? _points[i - 1] : p0;
                SKPoint pNext = i < EqualizerProcessor.BandCount - 2 ? _points[i + 2] : p1;

                float c1X = p0.X + ((p1.X - pPrev.X) * tension);
                float c1Y = p0.Y + ((p1.Y - pPrev.Y) * tension);
                float c2X = p1.X - ((pNext.X - p0.X) * tension);
                float c2Y = p1.Y - ((pNext.Y - p0.Y) * tension);

                fillPath.CubicTo(c1X, c1Y, c2X, c2Y, p1.X, p1.Y);
                linePath.CubicTo(c1X, c1Y, c2X, c2Y, p1.X, p1.Y);
            }

            ref readonly var last = ref _points[EqualizerProcessor.BandCount - 1];
            fillPath.LineTo(last.X, _zeroY);
            fillPath.Close();

            var paint = t_paint ??= new SKPaint();
            paint.Reset();
            paint.IsAntialias = true;

            uint accentKey = (uint)_accentColor;
            if (t_fillShader == null
                || t_shaderAccentKey != accentKey
                || MathF.Abs(t_shaderTop - _plotTop) > 0.5f
                || MathF.Abs(t_shaderBottom - _plotBottom) > 0.5f)
            {
                t_fillShader?.Dispose();
                var colors = t_gradColors ??= new SKColor[3];
                colors[0] = new SKColor(_accentColor.Red, _accentColor.Green, _accentColor.Blue, 72);
                colors[1] = new SKColor(_accentColor.Red, _accentColor.Green, _accentColor.Blue, 14);
                colors[2] = new SKColor(_accentColor.Red, _accentColor.Green, _accentColor.Blue, 62);

                t_fillShader = SKShader.CreateLinearGradient(
                    new SKPoint(0f, _plotTop),
                    new SKPoint(0f, _plotBottom),
                    colors,
                    GradStops,
                    SKShaderTileMode.Clamp);
                t_shaderAccentKey = accentKey;
                t_shaderTop = _plotTop;
                t_shaderBottom = _plotBottom;
            }

            var canvas = lease.SkCanvas;
            paint.Style = SKPaintStyle.Fill;
            paint.Shader = t_fillShader;
            paint.Color = new SKColor(255, 255, 255, (byte)(255f * opacity));
            canvas.DrawPath(fillPath, paint);

            paint.Shader = null;
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeCap = SKStrokeCap.Round;
            paint.StrokeJoin = SKStrokeJoin.Round;

            if (_isEqEnabled)
            {
                paint.StrokeWidth = 2.4f;
                paint.Color = new SKColor(_accentColor.Red, _accentColor.Green, _accentColor.Blue,
                    (byte)Math.Clamp((int)MathF.Round(_accentColor.Alpha * opacity), 0, 255));
            }
            else
            {
                paint.StrokeWidth = 1.8f;
                paint.Color = new SKColor(_mutedColor.Red, _mutedColor.Green, _mutedColor.Blue,
                    (byte)Math.Clamp((int)MathF.Round(115f * opacity), 0, 255));
            }

            canvas.DrawPath(linePath, paint);
            paint.Reset();
        }
    }

    private readonly Point[] _wave10Points = new Point[EqualizerProcessor.BandCount];

    private const int DbStepCount = 49; // [-12.0 .. +12.0] in 0.5 dB steps
    private readonly FormattedText?[] _cachedNormalLabels = new FormattedText?[EqualizerProcessor.BandCount];
    private readonly FormattedText?[] _cachedHotLabels = new FormattedText?[EqualizerProcessor.BandCount];
    private readonly FormattedText?[] _cachedHintSubLabels = new FormattedText?[EqualizerProcessor.BandCount];
    private readonly FormattedText?[] _cachedDbValueLabels = new FormattedText?[DbStepCount];
    private bool _labelsDirty = true;
    private string _lastLanguageCode = "";

    private readonly Action<TimeSpan> _rafCallback;
    private readonly CompositionSilenceMonitor _silenceMonitor;
    private TopLevel? _attachedTopLevel;

    private int _hoveredColumnIndex = -1;
    private bool _isDraggingWave;
    private bool _isRafActive;
    private long _lastFrameTs;
    private double _visPulsePhase;
    private float _smoothedMacroEnergy;
    private float _smoothedPercBoost;

    private Color _lastAccentColor;
    private IPen? _zeroLinePen;
    private IPen? _guideLinePen;
    private IPen? _activeGuidePen;
    private IBrush? _labelBrush;
    private IBrush? _activeLabelBrush;

    #endregion

    static EqualizerCurveControl()
    {
        AffectsRender<EqualizerCurveControl>(
            IsSimple5BandModeProperty,
            IsEqEnabledProperty);
    }

    public EqualizerCurveControl()
    {
        _rafCallback = OnAnimationFrame;
        _silenceMonitor = new CompositionSilenceMonitor(
            this,
            shouldAnimate: () => IsVisible,
            onStateReevaluated: OnAnimationContextChanged,
            onResumeFromSilence: EnsureAnimationLoop);

        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attachedTopLevel = TopLevel.GetTopLevel(this);
        _silenceMonitor.Attach();
        SyncTargetFromProperty(snapImmediately: true);
        EnsureAnimationLoop();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _silenceMonitor.Detach();
        _isRafActive = false;
        _isDraggingWave = false;
        _hoveredColumnIndex = -1;
        _attachedTopLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == BandGainsProperty && !_isDraggingWave)
        {
            SyncTargetFromProperty(snapImmediately: false);
            EnsureAnimationLoop();
            InvalidateVisual();
        }
        else if (change.Property == IsSimple5BandModeProperty || change.Property == IsEqEnabledProperty)
        {
            _hoveredColumnIndex = -1;
            _labelsDirty = true;
            EnsureAnimationLoop();
            InvalidateVisual();
        }
        else if (change.Property == IsVisibleProperty)
        {
            if (IsVisible)
            {
                SyncTargetFromProperty(snapImmediately: true);
                EnsureAnimationLoop();
            }
            else
            {
                _isRafActive = false;
                _silenceMonitor.StopIdleTimer();
            }
        }
    }

    private void OnAnimationContextChanged()
    {
        if (_silenceMonitor.CanAnimateNow)
            EnsureAnimationLoop();
        else
            _isRafActive = false;
    }

    private void SyncTargetFromProperty(bool snapImmediately)
    {
        var src = BandGains;
        if (src is { Count: >= EqualizerProcessor.BandCount })
        {
            for (int i = 0; i < EqualizerProcessor.BandCount; i++)
            {
                _targetIso10[i] = Math.Clamp(src[i], -12f, 12f);
                if (snapImmediately)
                    _displayIso10[i] = _targetIso10[i];
            }
        }
        else
        {
            Array.Clear(_targetIso10);
            if (snapImmediately)
                Array.Clear(_displayIso10);
        }
    }

    private void EnsureAnimationLoop()
    {
        if (_isRafActive) return;
        var topLevel = _attachedTopLevel ??= TopLevel.GetTopLevel(this);
        if (topLevel == null || !_silenceMonitor.CanAnimateNow) return;

        _silenceMonitor.StopIdleTimer();
        _isRafActive = true;
        _lastFrameTs = Stopwatch.GetTimestamp() - (Stopwatch.Frequency / 60);
        topLevel.RequestAnimationFrame(_rafCallback);
    }

    private void OnAnimationFrame(TimeSpan ts)
    {
        var topLevel = _attachedTopLevel;
        if (!_isRafActive || topLevel == null || !_silenceMonitor.CanAnimateNow)
        {
            _isRafActive = false;
            return;
        }

        long now = Stopwatch.GetTimestamp();
        double rawDt = Stopwatch.GetElapsedTime(_lastFrameTs, now).TotalSeconds;
        if (rawDt < MinFrameIntervalSeconds)
        {
            topLevel.RequestAnimationFrame(_rafCallback);
            return;
        }

        double dt = Math.Clamp(rawDt, 0.001, 0.050);
        _lastFrameTs = now;

        bool needsRedraw = false;
        float alpha = (float)(1.0 - Math.Exp(-LerpSpeed * dt));
        float specUp = (float)(1.0 - Math.Exp(-SpectrumLerpUp * dt));
        float specDown = (float)(1.0 - Math.Exp(-SpectrumLerpDown * dt));

        for (int i = 0; i < EqualizerProcessor.BandCount; i++)
        {
            float diff = _targetIso10[i] - _displayIso10[i];
            if (MathF.Abs(diff) > 0.005f)
            {
                _displayIso10[i] += diff * alpha;
                needsRedraw = true;
            }
            else if (_displayIso10[i] != _targetIso10[i])
            {
                _displayIso10[i] = _targetIso10[i];
                needsRedraw = true;
            }
        }

        Span<float> rawBands10 = stackalloc float[EqualizerProcessor.BandCount];
        AudioVisualizerBuffer.CopyBands10(rawBands10);
        bool hasSnap = AudioVisualizerBuffer.TryReadSnapshot(out var snap);

        float macroTarget = hasSnap ? snap.MacroEnergy : 0f;
        float percTarget = hasSnap ? MathF.Max(snap.Beat, snap.PercussiveEnergy) : 0f;
        _smoothedMacroEnergy += (macroTarget - _smoothedMacroEnergy) * (macroTarget > _smoothedMacroEnergy ? specUp * 0.45f : specDown * 0.55f);
        _smoothedPercBoost += (percTarget - _smoothedPercBoost) * (percTarget > _smoothedPercBoost ? specUp : specDown);

        bool hasAudioEnergy = false;
        for (int i = 0; i < EqualizerProcessor.BandCount; i++)
        {
            float raw = rawBands10[i];
            float cur = _spectrum10[i];
            float k = raw > cur ? specUp : specDown;
            float next = cur + (raw - cur) * k;
            if (next < 0.002f) next = 0f;

            if (MathF.Abs(next - cur) > 0.0005f)
                needsRedraw = true;

            _spectrum10[i] = next;
            if (next > 0f || raw > 0.002f)
                hasAudioEnergy = true;
        }

        if (hasAudioEnergy)
        {
            _visPulsePhase = (_visPulsePhase + dt * 7.5) % Math.Tau;
            needsRedraw = true;
        }

        if (needsRedraw)
        {
            InvalidateVisual();
            topLevel.RequestAnimationFrame(_rafCallback);
        }
        else if (AudioVisualizerBuffer.HasRecentActivity)
        {
            topLevel.RequestAnimationFrame(_rafCallback);
        }
        else
        {
            _isRafActive = false;
            _silenceMonitor.PostSilenceEntered();
        }
    }

    #region Pointer Interaction (Full-Height Column Hit-Testing)

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        EnsureAnimationLoop();
        int col = FindClosestControlColumn(e.GetPosition(this));
        if (col != _hoveredColumnIndex)
        {
            _hoveredColumnIndex = col;
            InvalidateVisual();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        int col = FindClosestControlColumn(pos);

        if (_isDraggingWave && col >= 0)
        {
            _hoveredColumnIndex = col;
            UpdateColumnGainFromY(col, pos.Y);
            return;
        }

        if (col != _hoveredColumnIndex)
        {
            _hoveredColumnIndex = col;
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEqEnabled) return;

        var point = e.GetCurrentPoint(this);
        var pos = point.Position;
        int col = FindClosestControlColumn(pos);
        if (col < 0) return;

        if (e.ClickCount == 2 || point.Properties.IsRightButtonPressed)
        {
            _hoveredColumnIndex = col;
            SetColumnGain(col, 0f);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            _isDraggingWave = true;
            _hoveredColumnIndex = col;
            e.Pointer.Capture(this);
            UpdateColumnGainFromY(col, pos.Y);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isDraggingWave)
        {
            _isDraggingWave = false;
            e.Pointer.Capture(null);
            InvalidateVisual();
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_isDraggingWave)
        {
            _isDraggingWave = false;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!_isDraggingWave && _hoveredColumnIndex >= 0)
        {
            _hoveredColumnIndex = -1;
            InvalidateVisual();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!IsEqEnabled) return;

        var pos = e.GetPosition(this);
        int col = _hoveredColumnIndex >= 0 ? _hoveredColumnIndex : FindClosestControlColumn(pos);
        if (col < 0) return;

        float currentDb = GetControlColumnGain(col, useDisplay: false);
        float step = e.Delta.Y > 0 ? 0.5f : -0.5f;
        SetColumnGain(col, Math.Clamp(MathF.Round((currentDb + step) * 2f) * 0.5f, -12f, 12f));
        e.Handled = true;
    }

    private void UpdateColumnGainFromY(int colIdx, double y)
    {
        double plotHeight = Math.Max(1.0, Bounds.Height - PaddingTop - PaddingBottom);
        double normY = Math.Clamp((y - PaddingTop) / plotHeight, 0.0, 1.0);
        float db = (float)((0.5 - normY) * 2.0 * MaxDbRange);
        db = MathF.Round(db * 2.0f) * 0.5f;
        SetColumnGain(colIdx, Math.Clamp(db, -12f, 12f));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float GetControlColumnGain(int colIdx, bool useDisplay)
    {
        var source = useDisplay ? _displayIso10 : _targetIso10;
        if (IsSimple5BandMode)
        {
            Span<float> m5 = stackalloc float[5];
            EqualizerPresets.MapIso10ToMacro5(source, m5);
            return m5[Math.Clamp(colIdx, 0, 4)];
        }
        return source[Math.Clamp(colIdx, 0, EqualizerProcessor.BandCount - 1)];
    }

    private void SetColumnGain(int colIdx, float db)
    {
        if (MathF.Abs(GetControlColumnGain(colIdx, useDisplay: false) - db) < 0.01f)
            return;

        if (IsSimple5BandMode)
        {
            EqualizerPresets.MapIso10ToMacro5(_targetIso10, _macro5Working);
            _macro5Working[Math.Clamp(colIdx, 0, 4)] = db;
            EqualizerPresets.MapMacro5ToIso10(_macro5Working, _targetIso10);
        }
        else
        {
            _targetIso10[Math.Clamp(colIdx, 0, EqualizerProcessor.BandCount - 1)] = db;
        }

        EnsureAnimationLoop();
        if (!_isRafActive)
            InvalidateVisual();

        _emitBufferToggle = !_emitBufferToggle;
        var emitBuffer = _emitBufferToggle ? _emitBufferA : _emitBufferB;
        _targetIso10.AsSpan().CopyTo(emitBuffer);
        BandsChanged?.Invoke(emitBuffer);
    }

    private int FindClosestControlColumn(Point pos)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
            return -1;

        if (!_isDraggingWave && (pos.X < 0 || pos.X > Bounds.Width || pos.Y < 0 || pos.Y > Bounds.Height))
            return -1;

        int count = IsSimple5BandMode ? 5 : EqualizerProcessor.BandCount;
        double plotW = Math.Max(1.0, Bounds.Width - PaddingLeft - PaddingRight);
        double clampedX = Math.Clamp(pos.X, 0.0, Bounds.Width);

        int best = 0;
        double minDist = double.MaxValue;

        for (int i = 0; i < count; i++)
        {
            double x = GetControlColumnX(i, count, plotW);
            double dist = Math.Abs(clampedX - x);
            if (dist < minDist)
            {
                minDist = dist;
                best = i;
            }
        }

        return best;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double GetControlColumnX(int index, int count, double plotW)
    {
        double u = count > 1 ? (double)index / (count - 1) : 0.5;
        return PaddingLeft + u * plotW;
    }

    #endregion

    #region Rendering (Single Unified Live Wave + Micro Nodes)

    public override void Render(DrawingContext context)
    {
        if (!_isRafActive && !_silenceMonitor.IsWaitingForAudio && _silenceMonitor.CanAnimateNow)
            EnsureAnimationLoop();

        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w < 40 || h < 40) return;

        // Transparent background over full Bounds to ensure 100% Hit-Test triggering across the entire column height
        context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        EnsureBrushesAndPens();

        double plotLeft = PaddingLeft;
        double plotRight = w - PaddingRight;
        double plotTop = PaddingTop;
        double plotBottom = h - PaddingBottom;
        double plotW = Math.Max(1.0, plotRight - plotLeft);
        double plotH = Math.Max(1.0, plotBottom - plotTop);
        double zeroY = plotTop + plotH * 0.5;

        // 1. Center zero line (0 dB)
        if (_zeroLinePen != null)
            context.DrawLine(_zeroLinePen, new Point(plotLeft, zeroY), new Point(plotRight, zeroY));

        // 2. Unified live 10-band wave: ALWAYS breathes with the music (even when EQ is disabled)
        for (int i = 0; i < EqualizerProcessor.BandCount; i++)
        {
            double u = i / 9.0;
            double x = plotLeft + u * plotW;

            float db = IsEqEnabled ? _displayIso10[i] : 0f;
            float spec = _spectrum10[i];

            double staticY = plotTop + (0.5 - (db / (2.0 * MaxDbRange))) * plotH;

            double liveDisplacement = 0.0;
            if (spec > 0.002f)
            {
                float eqGainFactor;
                if (IsEqEnabled)
                {
                    if (_cachedEqGainDb[i] != db)
                    {
                        _cachedEqGainDb[i] = db;
                        _cachedEqGainFactor[i] = MathF.Pow(10f, db / 26f);
                    }
                    eqGainFactor = _cachedEqGainFactor[i];
                }
                else
                {
                    eqGainFactor = 0.85f;
                }
                double harmonicWave = Math.Sin(_visPulsePhase + i * 0.85) * 0.25 + 0.75;
                double dynamicScale = 0.55 + (0.55 * Math.Clamp(_smoothedMacroEnergy, 0f, 1.35f)) + (0.28 * _smoothedPercBoost);
                liveDisplacement = spec * eqGainFactor * harmonicWave * dynamicScale * (plotH * 0.28);
            }

            double y = Math.Clamp(staticY - liveDisplacement, plotTop - 4.0, plotBottom + 2.0);
            _wave10Points[i] = new Point(x, y);
        }

        // 3. Vertical control lines (5 or 10) with cached FormattedText
        int controlCount = IsSimple5BandMode ? 5 : EqualizerProcessor.BandCount;
        EnsureLabelsCached(controlCount);

        for (int i = 0; i < controlCount; i++)
        {
            double colX = GetControlColumnX(i, controlCount, plotW);
            bool isHot = i == _hoveredColumnIndex;

            var pen = isHot ? _activeGuidePen : _guideLinePen;
            if (pen != null)
                context.DrawLine(pen, new Point(colX, plotTop), new Point(colX, plotBottom));

            var ft = isHot ? _cachedHotLabels[i] : _cachedNormalLabels[i];
            if (ft != null)
                context.DrawText(ft, new Point(colX - ft.Width * 0.5, plotBottom + 5.0));
        }

        // 4. Rendering a unified smooth wave and its gradient via pooled Skia draw op (zero StreamGeometry allocations)
        var eqOp = EqWaveDrawOp.Rent(
            new Rect(plotLeft - 4.0, plotTop - 16.0, plotW + 8.0, plotH + 24.0),
            _wave10Points,
            (float)zeroY,
            (float)plotTop,
            (float)plotBottom,
            new SKColor(_lastAccentColor.R, _lastAccentColor.G, _lastAccentColor.B, _lastAccentColor.A),
            new SKColor(_lastTextMutedColor.R, _lastTextMutedColor.G, _lastTextMutedColor.B, _lastTextMutedColor.A),
            IsEqEnabled);
        context.Custom(eqOp);

        // 5. Neat micro-dots directly on the wave + parameter text on top when hovered
        for (int i = 0; i < controlCount; i++)
        {
            Point nodePt = GetControlNodeWavePoint(i, controlCount);
            bool isHot = i == _hoveredColumnIndex;

            if (isHot)
            {
                if (_activeLabelBrush != null)
                    context.DrawEllipse(_activeLabelBrush, null, nodePt, 4.8, 4.8);
            }
            else
            {
                var dotBrush = IsEqEnabled ? _activeLabelBrush : _labelBrush;
                if (dotBrush != null)
                    context.DrawEllipse(dotBrush, null, nodePt, 2.2, 2.2);
            }
        }

        if (_hoveredColumnIndex >= 0 && _hoveredColumnIndex < controlCount)
        {
            int idx = _hoveredColumnIndex;
            double colX = GetControlColumnX(idx, controlCount, plotW);
            float db = GetControlColumnGain(idx, useDisplay: false);

            int dbIdx = Math.Clamp((int)MathF.Round((db + 12.0f) * 2.0f), 0, DbStepCount - 1);
            var dbFt = GetOrCreateDbValueLabel(dbIdx);
            var subFt = _cachedHintSubLabels[idx];

            if (subFt != null)
            {
                const double gap = 6.0;
                double totalW = dbFt.Width + gap + subFt.Width;
                double tipX = Math.Clamp(colX - totalW * 0.5, 4.0, Math.Max(4.0, w - totalW - 4.0));
                context.DrawText(dbFt, new Point(tipX, 2.0));
                context.DrawText(subFt, new Point(tipX + dbFt.Width + gap, 2.0));
            }
        }
    }

    private FormattedText GetOrCreateDbValueLabel(int dbIdx)
    {
        var cached = _cachedDbValueLabels[dbIdx];
        if (cached != null)
            return cached;

        float dbVal = -12.0f + dbIdx * 0.5f;
        string dbStr = dbVal > 0.05f ? $"+{dbVal:0.0} dB •" : $"{dbVal:0.0} dB •";
        cached = new FormattedText(
            dbStr,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            10.5,
            _activeLabelBrush);
        _cachedDbValueLabels[dbIdx] = cached;
        return cached;
    }

    private void EnsureLabelsCached(int controlCount)
    {
        var l10n = LocalizationService.Instance;
        string lang = l10n.CurrentLanguageCode;
        if (!_labelsDirty && string.Equals(_lastLanguageCode, lang, StringComparison.Ordinal))
            return;

        _lastLanguageCode = lang;
        _labelsDirty = false;
        Array.Clear(_cachedDbValueLabels);
        var typeface = Typeface.Default;

        for (int i = 0; i < controlCount; i++)
        {
            string text = IsSimple5BandMode ? l10n[Keys5Band[i]] : Labels10Band[i];
            _cachedNormalLabels[i] = new FormattedText(
                text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 10.5, _labelBrush);
            _cachedHotLabels[i] = new FormattedText(
                text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 10.5, _activeLabelBrush);

            string hintSub = IsSimple5BandMode ? Hints5BandFreqs[i] : l10n[Hints10BandKeys[i]];
            _cachedHintSubLabels[i] = new FormattedText(
                hintSub, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 10.5, _activeLabelBrush);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Point GetControlNodeWavePoint(int controlIdx, int controlCount)
    {
        if (controlCount == EqualizerProcessor.BandCount)
            return _wave10Points[Math.Clamp(controlIdx, 0, 9)];

        // For 5 bands (u = 0, 0.25, 0.5, 0.75, 1.0), linearly interpolate Y on the 10-band wave
        double exactIso = controlIdx / 4.0 * 9.0;
        int i0 = Math.Clamp((int)exactIso, 0, 8);
        int i1 = i0 + 1;
        double frac = exactIso - i0;

        Point p0 = _wave10Points[i0];
        Point p1 = _wave10Points[i1];
        return new Point(
            p0.X + (p1.X - p0.X) * frac,
            p0.Y + (p1.Y - p0.Y) * frac);
    }

    private Color _lastHighlightColor;
    private Color _lastTextMutedColor;

    private void EnsureBrushesAndPens()
    {
        var accent = ThemeManagerService.GetThemeColor("Accent");
        var highlight = ThemeManagerService.GetThemeColor("BgHighlight");
        var textMuted = ThemeManagerService.GetThemeColor("TextMuted");

        if (accent == _lastAccentColor
            && highlight == _lastHighlightColor
            && textMuted == _lastTextMutedColor
            && _zeroLinePen != null)
        {
            return;
        }

        _lastAccentColor = accent;
        _lastHighlightColor = highlight;
        _lastTextMutedColor = textMuted;

        _zeroLinePen = new ImmutablePen(
            new ImmutableSolidColorBrush(highlight, 0.60),
            1.0);

        _guideLinePen = new ImmutablePen(
            new ImmutableSolidColorBrush(highlight, 0.30),
            1.0);

        _activeGuidePen = new ImmutablePen(
            new ImmutableSolidColorBrush(accent, 0.90),
            1.6);

        _labelBrush = new ImmutableSolidColorBrush(textMuted);
        _activeLabelBrush = new ImmutableSolidColorBrush(accent);
        Array.Clear(_cachedDbValueLabels);
        _labelsDirty = true;
    }

    #endregion
}

/// <summary>
/// Interactive preset item for the equalizer dropdown menu in both the Player Bar and Audio Settings.
/// </summary>
public sealed partial class EqualizerPresetChipItem : ObservableObject
{
    public string Id { get; }
    public bool IsBuiltIn { get; }

    [ObservableProperty] public partial string Title { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial bool IsTrackPreset { get; set; }

    public string? RightClickHint => IsBuiltIn
        ? null
        : LocalizationService.Instance["Eq_Preset_RightClickDelete"];

    public EqualizerPresetChipItem(string id, string title, bool isBuiltIn)
    {
        Id = id;
        Title = title;
        IsBuiltIn = isBuiltIn;
    }

    /// <summary>
    /// Synchronizes an <see cref="System.Collections.ObjectModel.ObservableCollection{EqualizerPresetChipItem}"/>
    /// with built-in and user presets, updating selection and track assignment flags in-place.
    /// </summary>
    public static string SyncPresetsList(
        System.Collections.ObjectModel.ObservableCollection<EqualizerPresetChipItem> targetList,
        AudioSettings audioSettings,
        TrackInfo? currentTrack,
        string activePresetId)
    {
        var sl = LocalizationService.Instance;
        var builtIn = EqualizerPresets.BuiltInIds;
        var userPresets = audioSettings.UserEqualizerPresets;
        int expectedCount = builtIn.Length + userPresets.Count;

        bool needsRebuild = targetList.Count != expectedCount;
        if (!needsRebuild)
        {
            for (int i = 0; i < userPresets.Count; i++)
            {
                var chip = targetList[builtIn.Length + i];
                if (!string.Equals(chip.Id, userPresets[i].Id, StringComparison.Ordinal))
                {
                    needsRebuild = true;
                    break;
                }
            }
        }

        if (needsRebuild)
        {
            targetList.Clear();
            for (int i = 0; i < builtIn.Length; i++)
            {
                string id = builtIn[i];
                string key = EqualizerPresets.GetLocalizationKey(id);
                targetList.Add(new EqualizerPresetChipItem(id, sl[key], isBuiltIn: true));
            }

            for (int i = 0; i < userPresets.Count; i++)
            {
                var up = userPresets[i];
                targetList.Add(new EqualizerPresetChipItem(up.Id, up.Name, isBuiltIn: false));
            }
        }
        else
        {
            for (int i = 0; i < builtIn.Length; i++)
            {
                string id = builtIn[i];
                string key = EqualizerPresets.GetLocalizationKey(id);
                targetList[i].Title = sl[key];
            }

            for (int i = 0; i < userPresets.Count; i++)
            {
                var chip = targetList[builtIn.Length + i];
                if (!string.Equals(chip.Title, userPresets[i].Name, StringComparison.Ordinal))
                    chip.Title = userPresets[i].Name;
            }
        }

        string? trackId = currentTrack?.EqualizerPresetId;
        string activeTitle = sl["Eq_Preset_Custom"];

        for (int i = 0; i < targetList.Count; i++)
        {
            var item = targetList[i];
            bool match = string.Equals(item.Id, activePresetId, StringComparison.Ordinal);
            item.IsSelected = match;
            item.IsTrackPreset = !string.IsNullOrEmpty(trackId)
                              && string.Equals(item.Id, trackId, StringComparison.Ordinal);

            if (match)
                activeTitle = item.Title;
        }

        return activeTitle;
    }

    /// <summary>
    /// Resolves the localized tooltip for the per-track equalizer button.
    /// </summary>
    public static string FormatTrackEqualizerTooltip(
        string? trackPresetId,
        bool isTrackModeHighlighted,
        IList<EqualizerPresetChipItem> presets)
    {
        var sl = LocalizationService.Instance;
        if (!string.IsNullOrEmpty(trackPresetId))
        {
            string presetTitle = "";
            if (EqualizerPresets.IsCustomPresetId(trackPresetId))
            {
                Span<float> tmp = stackalloc float[EqualizerProcessor.BandCount];
                bool isFlatCustom = EqualizerPresets.TryParseCustomTrackBands(trackPresetId, tmp)
                                 && !EqualizerPresets.HasNonZeroBands(tmp);
                presetTitle = isFlatCustom ? sl["Eq_Preset_Flat"] : sl["Eq_Preset_Custom"];
            }
            else
            {
                for (int i = 0; i < presets.Count; i++)
                {
                    if (string.Equals(presets[i].Id, trackPresetId, StringComparison.Ordinal))
                    {
                        presetTitle = presets[i].Title;
                        break;
                    }
                }
                if (string.IsNullOrEmpty(presetTitle) && EqualizerPresets.IsBuiltInPreset(trackPresetId))
                    presetTitle = sl[EqualizerPresets.GetLocalizationKey(trackPresetId)];
            }

            if (!string.IsNullOrEmpty(presetTitle))
                return string.Format(sl["Eq_Track_AssignedWithPreset_Tip"], presetTitle);
        }

        return isTrackModeHighlighted
            ? sl["Eq_Track_Assigned_Tip"]
            : sl["Eq_Track_NotAssigned_Tip"];
    }
}