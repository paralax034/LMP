using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.VisualTree;
using LMP.Core.Audio.Helpers;

namespace LMP.UI.Controls;

/// <summary>
/// Интерактивный график эквалайзера с единой живой волной:
/// 100% кликабельная область по всей высоте колонок, 0 точек, живой отклик частот играющего трека.
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
    private const double MinFrameIntervalSeconds = 0.0155; // ~60 FPS cap

    private static readonly string[] Labels10Band = ["31", "62", "125", "250", "500", "1к", "2к", "4к", "8к", "16к"];
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
    /// Событие изменения полос пользователем (передаёт массив из 10 ISO-полос в дБ).
    /// </summary>
    public event Action<float[]>? BandsChanged;

    #endregion

    #region Fields

    private readonly float[] _targetIso10 = new float[EqualizerProcessor.BandCount];
    private readonly float[] _displayIso10 = new float[EqualizerProcessor.BandCount];
    private readonly float[] _spectrum10 = new float[EqualizerProcessor.BandCount];
    private readonly float[] _macro5Working = new float[5];
    private readonly float[] _emitBufferA = new float[EqualizerProcessor.BandCount];
    private readonly float[] _emitBufferB = new float[EqualizerProcessor.BandCount];
    private bool _emitBufferToggle;

    private readonly Point[] _wave10Points = new Point[EqualizerProcessor.BandCount];
    private readonly Point[] _lastRenderedWavePoints = new Point[EqualizerProcessor.BandCount];
    private StreamGeometry? _cachedFillGeometry;
    private StreamGeometry? _cachedLineGeometry;
    private double _lastRenderedZeroY = double.NaN;

    private const int DbStepCount = 49; // [-12.0 .. +12.0] с шагом 0.5 dB
    private readonly FormattedText?[] _cachedNormalLabels = new FormattedText?[EqualizerProcessor.BandCount];
    private readonly FormattedText?[] _cachedHotLabels = new FormattedText?[EqualizerProcessor.BandCount];
    private readonly FormattedText?[] _cachedHintSubLabels = new FormattedText?[EqualizerProcessor.BandCount];
    private readonly FormattedText?[] _cachedDbValueLabels = new FormattedText?[DbStepCount];
    private bool _labelsDirty = true;
    private string _lastLanguageCode = "";

    private readonly Action<TimeSpan> _rafCallback;
    private Avalonia.Threading.DispatcherTimer? _idlePollTimer;

    private int _hoveredColumnIndex = -1;
    private bool _isDraggingWave;
    private bool _isRafActive;
    private long _lastFrameTs;
    private double _visPulsePhase;

    private Color _lastAccentColor;
    private IPen? _curvePen;
    private IPen? _disabledCurvePen;
    private IPen? _zeroLinePen;
    private IPen? _guideLinePen;
    private IPen? _activeGuidePen;
    private IBrush? _fillGradientBrush;
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
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SyncTargetFromProperty(snapImmediately: true);
        EnsureAnimationLoop();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isRafActive = false;
        _isDraggingWave = false;
        _hoveredColumnIndex = -1;
        _idlePollTimer?.Stop();
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
                _idlePollTimer?.Stop();
            }
        }
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
        if (_isRafActive || !PersistentPageHost.CanAnimateVisual(this)) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        _idlePollTimer?.Stop();
        _isRafActive = true;
        _lastFrameTs = Stopwatch.GetTimestamp();
        topLevel.RequestAnimationFrame(_rafCallback);
    }

    private void OnAnimationFrame(TimeSpan ts)
    {
        if (!_isRafActive || !PersistentPageHost.CanAnimateVisual(this) || ViewModelBase.CurrentSuspendLevel != SuspendLevel.None)
        {
            _isRafActive = false;
            return;
        }

        long now = Stopwatch.GetTimestamp();
        double rawDt = Stopwatch.GetElapsedTime(_lastFrameTs, now).TotalSeconds;
        if (rawDt < MinFrameIntervalSeconds)
        {
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_rafCallback);
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
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_rafCallback);
        }
        else if (AudioVisualizerBuffer.HasRecentActivity)
        {
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_rafCallback);
        }
        else
        {
            _isRafActive = false;
            _idlePollTimer ??= new Avalonia.Threading.DispatcherTimer(
                TimeSpan.FromMilliseconds(250),
                Avalonia.Threading.DispatcherPriority.Background,
                (_, _) =>
                {
                    _idlePollTimer?.Stop();
                    if (PersistentPageHost.CanAnimateVisual(this))
                        EnsureAnimationLoop();
                });
            _idlePollTimer.Start();
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
        if (!_isRafActive && PersistentPageHost.CanAnimateVisual(this))
            EnsureAnimationLoop();

        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w < 40 || h < 40) return;

        // Прозрачная подложка на весь Bounds для 100% срабатывания Hit-Test по всей высоте колонок
        context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        EnsureBrushesAndPens();

        double plotLeft = PaddingLeft;
        double plotRight = w - PaddingRight;
        double plotTop = PaddingTop;
        double plotBottom = h - PaddingBottom;
        double plotW = Math.Max(1.0, plotRight - plotLeft);
        double plotH = Math.Max(1.0, plotBottom - plotTop);
        double zeroY = plotTop + plotH * 0.5;

        // 1. Центральная нулевая линия (0 дБ)
        if (_zeroLinePen != null)
            context.DrawLine(_zeroLinePen, new Point(plotLeft, zeroY), new Point(plotRight, zeroY));

        // 2. Единая живая 10-полосная волна: дышит в такт музыке ВСЕГДА (даже когда EQ выключен)
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
                float eqGainFactor = IsEqEnabled ? MathF.Pow(10f, db / 26f) : 0.85f;
                double harmonicWave = Math.Sin(_visPulsePhase + i * 0.85) * 0.35 + 0.65;
                liveDisplacement = spec * eqGainFactor * harmonicWave * (plotH * 0.22);
            }

            double y = Math.Clamp(staticY - liveDisplacement, plotTop - 4.0, plotBottom + 2.0);
            _wave10Points[i] = new Point(x, y);
        }

        // 3. Вертикальные линии управления (5 или 10) с кэшированными FormattedText
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

        // 4. Отрисовка единой гладкой волны и её градиента (пересборка StreamGeometry только при изменении точек)
        EnsureWaveGeometries(zeroY);

        if (_fillGradientBrush != null && _cachedFillGeometry != null)
            context.DrawGeometry(_fillGradientBrush, null, _cachedFillGeometry);

        var mainPen = IsEqEnabled ? _curvePen : _disabledCurvePen;
        if (mainPen != null && _cachedLineGeometry != null)
            context.DrawGeometry(null, mainPen, _cachedLineGeometry);

        // 5. Аккуратные микро-точки прямо на волне + текст параметров сверху при наведении
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
            var dbFt = _cachedDbValueLabels[dbIdx];
            var subFt = _cachedHintSubLabels[idx];

            if (dbFt != null && subFt != null)
            {
                const double gap = 6.0;
                double totalW = dbFt.Width + gap + subFt.Width;
                double tipX = Math.Clamp(colX - totalW * 0.5, 4.0, Math.Max(4.0, w - totalW - 4.0));
                context.DrawText(dbFt, new Point(tipX, 2.0));
                context.DrawText(subFt, new Point(tipX + dbFt.Width + gap, 2.0));
            }
        }
    }

    private void EnsureWaveGeometries(double zeroY)
    {
        bool pointsChanged = _cachedFillGeometry == null
                          || _cachedLineGeometry == null
                          || Math.Abs(_lastRenderedZeroY - zeroY) > 0.35;

        if (!pointsChanged)
        {
            for (int i = 0; i < EqualizerProcessor.BandCount; i++)
            {
                if (Math.Abs(_wave10Points[i].X - _lastRenderedWavePoints[i].X) > 0.35 ||
                    Math.Abs(_wave10Points[i].Y - _lastRenderedWavePoints[i].Y) > 0.35)
                {
                    pointsChanged = true;
                    break;
                }
            }
        }

        if (!pointsChanged)
            return;

        _lastRenderedZeroY = zeroY;
        _wave10Points.AsSpan().CopyTo(_lastRenderedWavePoints);

        var fillGeo = new StreamGeometry();
        using (var ctx = fillGeo.Open())
        {
            ctx.BeginFigure(new Point(_wave10Points[0].X, zeroY), isFilled: true);
            ctx.LineTo(_wave10Points[0]);
            DrawCatmullRomSpline(ctx, _wave10Points, EqualizerProcessor.BandCount);
            ctx.LineTo(new Point(_wave10Points[EqualizerProcessor.BandCount - 1].X, zeroY));
            ctx.EndFigure(isClosed: true);
        }
        _cachedFillGeometry = fillGeo;

        var lineGeo = new StreamGeometry();
        using (var ctx = lineGeo.Open())
        {
            ctx.BeginFigure(_wave10Points[0], isFilled: false);
            DrawCatmullRomSpline(ctx, _wave10Points, EqualizerProcessor.BandCount);
            ctx.EndFigure(isClosed: false);
        }
        _cachedLineGeometry = lineGeo;
    }

    private void EnsureLabelsCached(int controlCount)
    {
        var l10n = LocalizationService.Instance;
        string lang = l10n.CurrentLanguageCode;
        if (!_labelsDirty && string.Equals(_lastLanguageCode, lang, StringComparison.Ordinal))
            return;

        _lastLanguageCode = lang;
        _labelsDirty = false;
        var typeface = Typeface.Default;

        if (_cachedDbValueLabels[0] == null)
        {
            for (int s = 0; s < DbStepCount; s++)
            {
                float dbVal = -12.0f + s * 0.5f;
                string dbStr = dbVal > 0.05f ? $"+{dbVal:0.0} dB •" : $"{dbVal:0.0} dB •";
                _cachedDbValueLabels[s] = new FormattedText(
                    dbStr, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 10.5, _activeLabelBrush);
            }
        }

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

        // Для 5 полос (u = 0, 0.25, 0.5, 0.75, 1.0) линейно интерполируем Y на 10-полосной волне
        double exactIso = (controlIdx / 4.0) * 9.0;
        int i0 = Math.Clamp((int)exactIso, 0, 8);
        int i1 = i0 + 1;
        double frac = exactIso - i0;

        Point p0 = _wave10Points[i0];
        Point p1 = _wave10Points[i1];
        return new Point(
            p0.X + (p1.X - p0.X) * frac,
            p0.Y + (p1.Y - p0.Y) * frac);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DrawCatmullRomSpline(StreamGeometryContext ctx, Point[] pts, int count)
    {
        const double Tension = 1.0 / 6.0;
        for (int i = 0; i < count - 1; i++)
        {
            Point p0 = pts[i];
            Point p1 = pts[i + 1];
            Point pPrev = i > 0 ? pts[i - 1] : p0;
            Point pNext = i < count - 2 ? pts[i + 2] : p1;

            Point c1 = new(p0.X + (p1.X - pPrev.X) * Tension, p0.Y + (p1.Y - pPrev.Y) * Tension);
            Point c2 = new(p1.X - (pNext.X - p0.X) * Tension, p1.Y - (pNext.Y - p0.Y) * Tension);
            ctx.CubicBezierTo(c1, c2, p1);
        }
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
            && _curvePen != null)
        {
            return;
        }

        _lastAccentColor = accent;
        _lastHighlightColor = highlight;
        _lastTextMutedColor = textMuted;

        _curvePen = new ImmutablePen(
            new ImmutableSolidColorBrush(accent),
            2.4,
            lineCap: PenLineCap.Round,
            lineJoin: PenLineJoin.Round);

        _disabledCurvePen = new ImmutablePen(
            new ImmutableSolidColorBrush(textMuted, 0.45),
            1.8,
            lineCap: PenLineCap.Round,
            lineJoin: PenLineJoin.Round);

        _zeroLinePen = new ImmutablePen(
            new ImmutableSolidColorBrush(highlight, 0.60),
            1.0);

        _guideLinePen = new ImmutablePen(
            new ImmutableSolidColorBrush(highlight, 0.30),
            1.0);

        _activeGuidePen = new ImmutablePen(
            new ImmutableSolidColorBrush(accent, 0.90),
            1.6);

        _fillGradientBrush = new ImmutableLinearGradientBrush(
            [
                new ImmutableGradientStop(0.0, Color.FromArgb(72, accent.R, accent.G, accent.B)),
                new ImmutableGradientStop(0.5, Color.FromArgb(14, accent.R, accent.G, accent.B)),
                new ImmutableGradientStop(1.0, Color.FromArgb(62, accent.R, accent.G, accent.B))
            ],
            startPoint: new RelativePoint(0.5, 0.0, RelativeUnit.Relative),
            endPoint: new RelativePoint(0.5, 1.0, RelativeUnit.Relative));

        _labelBrush = new ImmutableSolidColorBrush(textMuted);
        _activeLabelBrush = new ImmutableSolidColorBrush(accent);
        Array.Clear(_cachedDbValueLabels);
        _labelsDirty = true;
    }

    #endregion
}