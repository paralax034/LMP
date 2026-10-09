using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.UI.Controls;

/// <summary>
/// Граничный декоратор с непрерывным гидродинамическим волноводом, где длина волны отображает высоту ноты.
/// </summary>
public sealed class AudioWaveBorder : Decorator
{
    #region Constants

    private const double DefaultBorderThickness = 1.35;
    private const double SelectedBorderThickness = 2.4;
    private const double InsetMargin = 1.0;

    private const double BaseWaveSpeed = 0.70;
    private const double BasePulseSpeed = 1.4;

    private const double CornerBlendMargin = 0.08;
    private const double MaxSafeDisplacement = 4.0;
    private const double MinSpatialSpacing = 0.28;

    private const int MaxActivePackets = 16;
    private const double SampleStepPx = 4.0;
    private const int MinRailSamples = 48;
    private const int MaxRailSamples = 320;

    private const double BezierCircleConstant = 0.5522847498307935;

    #endregion

    #region Wave Packet Struct & Enums

    private enum PacketKind : byte
    {
        KickBullet,
        BassSwell,
        MidRipple,
        HighNeedle
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct WavePacket
    {
        public double Position;
        public double Velocity;
        public double Amplitude;
        public double Width;
        public double Frequency;
        public double PhaseOffset;
        public double HarmonicRatio;
        public double HarmonicPhase;
        public double DecayRate;
        public PacketKind Kind;
        public bool IsActive;
    }

    #endregion

    #region Static Global Configuration

    private static bool _globalUseWave = true;
    private static TrackAnimationSpeed _globalAnimationSpeed = TrackAnimationSpeed.Medium;
    private static bool _isGlobalConfigLoaded;

    public static void ConfigureGlobal(bool useWave, TrackAnimationSpeed speed)
    {
        _globalUseWave = useWave;
        _globalAnimationSpeed = speed;
        _isGlobalConfigLoaded = true;
        GlobalConfigChanged?.Invoke();
    }

    private static event Action? GlobalConfigChanged;

    /// <summary>
    /// Гарантирует применение сохраненных настроек при холодном старте приложения.
    /// </summary>
    private static void EnsureGlobalConfigLoaded()
    {
        if (_isGlobalConfigLoaded) return;

        try
        {
            var library = AppEntry.Services.GetService<LibraryService>();
            if (library?.Settings != null)
            {
                _globalUseWave = library.Settings.UseWaveAnimation;
                _globalAnimationSpeed = library.Settings.TrackAnimationSpeed;
                _isGlobalConfigLoaded = true;
            }
        }
        catch
        {
            // Защита при работе в дизайнере Avalonia
        }
    }

    #endregion

    #region Fields

    private readonly Action<TimeSpan> _frameCallback;
    private readonly EventHandler<AvaloniaPropertyChangedEventArgs> _windowPropertyChangedHandler;
    private readonly Action _globalConfigChangedHandler;
    private readonly Action<SuspendLevel> _suspendLevelChangedHandler;
    private readonly EventHandler _windowActivatedHandler;

    private readonly WavePacket[] _packets = new WavePacket[MaxActivePackets];
    private ulong _rngState;

    private Point[] _railPoints = new Point[MaxRailSamples];

    private double _smoothedBeatEnv;
    private double _smoothedLowEnv;
    private double _smoothedMidEnv;
    private double _smoothedHighEnv;

    private double _cooldownBeat;
    private double _cooldownLow;
    private double _cooldownMid;
    private double _cooldownHigh;

    private double _pulsePhase;
    private long _lastFrameTimestamp;
    private bool _isFrameLoopActive;
    private TopLevel? _attachedTopLevel;
    private Window? _subscribedWindow;

    private IBrush? _lastResolvedBrush;
    private IPen? _cachedPen;
    private double _lastRenderedThickness;
    private byte _lastRenderedAlpha;

    #endregion

    #region Styled Properties

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<AudioWaveBorder, bool>(nameof(IsActive));

    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<AudioWaveBorder, bool>(nameof(IsPlaying));

    public static readonly StyledProperty<bool> IsSelectedProperty =
        AvaloniaProperty.Register<AudioWaveBorder, bool>(nameof(IsSelected));

    public static readonly StyledProperty<bool?> UseWaveAnimationProperty =
        AvaloniaProperty.Register<AudioWaveBorder, bool?>(nameof(UseWaveAnimation));

    public static readonly StyledProperty<TrackAnimationSpeed?> AnimationSpeedProperty =
        AvaloniaProperty.Register<AudioWaveBorder, TrackAnimationSpeed?>(nameof(AnimationSpeed));

    public static readonly StyledProperty<IBrush?> WaveBrushProperty =
        AvaloniaProperty.Register<AudioWaveBorder, IBrush?>(nameof(WaveBrush));

    public static readonly StyledProperty<double> WaveThicknessProperty =
        AvaloniaProperty.Register<AudioWaveBorder, double>(nameof(WaveThickness), DefaultBorderThickness);

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<AudioWaveBorder, CornerRadius>(nameof(CornerRadius), new CornerRadius(8));

    public static readonly StyledProperty<int?> SeedProperty =
        AvaloniaProperty.Register<AudioWaveBorder, int?>(nameof(Seed));

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public bool IsPlaying
    {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public bool IsSelected
    {
        get => GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    public bool? UseWaveAnimation
    {
        get => GetValue(UseWaveAnimationProperty);
        set => SetValue(UseWaveAnimationProperty, value);
    }

    public TrackAnimationSpeed? AnimationSpeed
    {
        get => GetValue(AnimationSpeedProperty);
        set => SetValue(AnimationSpeedProperty, value);
    }

    public IBrush? WaveBrush
    {
        get => GetValue(WaveBrushProperty);
        set => SetValue(WaveBrushProperty, value);
    }

    public double WaveThickness
    {
        get => GetValue(WaveThicknessProperty);
        set => SetValue(WaveThicknessProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public int? Seed
    {
        get => GetValue(SeedProperty);
        set => SetValue(SeedProperty, value);
    }

    #endregion

    #region Constructor & Static Init

    static AudioWaveBorder()
    {
        AffectsRender<AudioWaveBorder>(
            WaveBrushProperty,
            WaveThicknessProperty,
            CornerRadiusProperty,
            IsSelectedProperty,
            UseWaveAnimationProperty,
            AnimationSpeedProperty);

        IsActiveProperty.Changed.AddClassHandler<AudioWaveBorder>((x, _) => x.OnActiveChanged());
        IsPlayingProperty.Changed.AddClassHandler<AudioWaveBorder>((x, _) => x.EvaluateAnimationState());
        IsSelectedProperty.Changed.AddClassHandler<AudioWaveBorder>((x, _) => x.InvalidatePens());
    }

    public AudioWaveBorder()
    {
        _frameCallback = OnAnimationFrame;
        _windowPropertyChangedHandler = OnWindowPropertyChanged;
        _globalConfigChangedHandler = OnGlobalConfigChanged;
        _suspendLevelChangedHandler = OnSuspendLevelChanged;
        _windowActivatedHandler = OnWindowActivated;

        _rngState = (ulong)Stopwatch.GetTimestamp() ^ 0x9E3779B97F4A7C15UL;
    }

    #endregion

    #region Fast PRNG & Seed Helper

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private double NextRandom(double min, double max)
    {
        _rngState ^= _rngState >> 12;
        _rngState ^= _rngState << 25;
        _rngState ^= _rngState >> 27;
        ulong result = _rngState * 0x2545F4914F6CDD1DUL;
        double norm = (result >> 11) * (1.0 / (1UL << 53));
        return min + ((max - min) * norm);
    }

    #endregion

    #region Lifecycle

    private void OnActiveChanged()
    {
        if (!IsActive)
        {
            _isFrameLoopActive = false;
            ClearAllPackets();
            InvalidateVisual();
            return;
        }

        EvaluateAnimationState();
    }

    private void OnGlobalConfigChanged()
    {
        InvalidatePens();
        EvaluateAnimationState();
        InvalidateVisual();
    }

    private void OnSuspendLevelChanged(SuspendLevel level)
    {
        if (Dispatcher.UIThread.CheckAccess())
            EvaluateAnimationState();
        else
            Dispatcher.UIThread.Post(EvaluateAnimationState);
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        EvaluateAnimationState();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        EnsureGlobalConfigLoaded();

        GlobalConfigChanged += _globalConfigChangedHandler;
        ViewModelBase.SuspendLevelChanged += _suspendLevelChangedHandler;
        _attachedTopLevel = TopLevel.GetTopLevel(this);

        if (_attachedTopLevel is Window w)
        {
            _subscribedWindow = w;
            w.PropertyChanged += _windowPropertyChangedHandler;
            w.Activated += _windowActivatedHandler;
        }

        EvaluateAnimationState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        GlobalConfigChanged -= _globalConfigChangedHandler;
        ViewModelBase.SuspendLevelChanged -= _suspendLevelChangedHandler;
        if (_subscribedWindow != null)
        {
            _subscribedWindow.PropertyChanged -= _windowPropertyChangedHandler;
            _subscribedWindow.Activated -= _windowActivatedHandler;
            _subscribedWindow = null;
        }

        _isFrameLoopActive = false;
        _attachedTopLevel = null;
        ClearAllPackets();

        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == WaveBrushProperty ||
            change.Property == WaveThicknessProperty ||
            change.Property == IsSelectedProperty ||
            change.Property == UseWaveAnimationProperty)
        {
            InvalidatePens();
        }
        else if (change.Property == SeedProperty)
        {
            int? seed = change.GetNewValue<int?>();
            _rngState = seed.HasValue
                ? (ulong)seed.Value * 0x9E3779B97F4A7C15UL
                : (ulong)Stopwatch.GetTimestamp() ^ 0x9E3779B97F4A7C15UL;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child != null)
        {
            Child.Measure(availableSize);
            return Child.DesiredSize;
        }

        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(finalSize));
        return finalSize;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == Visual.IsVisibleProperty)
        {
            EvaluateAnimationState();
        }
    }

    private void ClearAllPackets()
    {
        for (int i = 0; i < MaxActivePackets; i++)
        {
            _packets[i].IsActive = false;
        }
    }

    #endregion

    #region Animation Loop & Dynamics

    private double GetWaveSpeedMultiplier()
    {
        var speed = AnimationSpeed ?? _globalAnimationSpeed;
        return speed switch
        {
            TrackAnimationSpeed.VerySlow => 0.32,
            TrackAnimationSpeed.Slow => 0.60,
            TrackAnimationSpeed.Medium => 1.0,
            TrackAnimationSpeed.Fast => 1.55,
            TrackAnimationSpeed.Epileptic => 2.40,
            _ => 1.0
        };
    }

    private double GetPulseSpeedMultiplier()
    {
        var speed = AnimationSpeed ?? _globalAnimationSpeed;
        return speed switch
        {
            TrackAnimationSpeed.VerySlow => 0.25,
            TrackAnimationSpeed.Slow => 0.50,
            TrackAnimationSpeed.Medium => 1.0,
            TrackAnimationSpeed.Fast => 2.0,
            TrackAnimationSpeed.Epileptic => 5.5,
            _ => 1.0
        };
    }

    private bool EffectiveUseWave => UseWaveAnimation ?? _globalUseWave;

    private bool ShouldAnimate()
    {
        if (_attachedTopLevel == null)
            return false;

        if (_subscribedWindow != null)
        {
            if (!_subscribedWindow.IsVisible || _subscribedWindow.WindowState == WindowState.Minimized)
                return false;
        }

        if (ViewModelBase.CurrentSuspendLevel == SuspendLevel.Hard)
            return false;

        return IsActive && IsPlaying;
    }

    private void EvaluateAnimationState()
    {
        if (_attachedTopLevel == null || !IsActive) return;

        bool active = ShouldAnimate();

        if (active)
        {
            if (!_isFrameLoopActive)
            {
                _isFrameLoopActive = true;
                _lastFrameTimestamp = Stopwatch.GetTimestamp();
                _attachedTopLevel.RequestAnimationFrame(_frameCallback);
            }
            InvalidateVisual();
        }
        else
        {
            bool isHiddenOrSuspended = _attachedTopLevel == null ||
                (_subscribedWindow != null && (!_subscribedWindow.IsVisible || _subscribedWindow.WindowState == WindowState.Minimized)) ||
                ViewModelBase.CurrentSuspendLevel == SuspendLevel.Hard;

            if (isHiddenOrSuspended)
            {
                _isFrameLoopActive = false;
                ClearAllPackets();
            }
            else if (HasActivePackets())
            {
                if (!_isFrameLoopActive)
                {
                    _isFrameLoopActive = true;
                    _lastFrameTimestamp = Stopwatch.GetTimestamp();
                    _attachedTopLevel!.RequestAnimationFrame(_frameCallback);
                }
                return;
            }
            else
            {
                _isFrameLoopActive = false;
            }

            InvalidateVisual();
        }
    }

    private bool HasActivePackets()
    {
        for (int i = 0; i < MaxActivePackets; i++)
        {
            if (_packets[i].IsActive) return true;
        }
        return false;
    }

    private void OnAnimationFrame(TimeSpan elapsed)
    {
        if (!_isFrameLoopActive || _attachedTopLevel == null || !IsActive)
            return;

        long currentTimestamp = Stopwatch.GetTimestamp();
        double dt = Stopwatch.GetElapsedTime(_lastFrameTimestamp, currentTimestamp).TotalSeconds;
        _lastFrameTimestamp = currentTimestamp;

        if (dt <= 0.0) dt = 0.016;
        else if (dt > 0.050) dt = 0.050;

        bool canRun = ShouldAnimate();
        double waveMultiplier = GetWaveSpeedMultiplier();
        double pulseMultiplier = GetPulseSpeedMultiplier();

        if (canRun)
        {
            double curLow = AudioVisualizerBuffer.LowLevel;
            double curMid = AudioVisualizerBuffer.MidLevel;
            double curHigh = AudioVisualizerBuffer.HighLevel;
            double curBeat = AudioVisualizerBuffer.BeatPulse;

            double effBeat = Math.Pow(curBeat, 0.75);
            double effLow = Math.Pow(curLow, 0.75);
            double effMid = Math.Pow(curMid, 0.85);
            double effHigh = Math.Pow(curHigh, 0.85);

            _smoothedBeatEnv += (effBeat - _smoothedBeatEnv) * (1.0 - Math.Exp(-dt / 0.080));
            _smoothedLowEnv += (effLow - _smoothedLowEnv) * (1.0 - Math.Exp(-dt / 0.120));
            _smoothedMidEnv += (effMid - _smoothedMidEnv) * (1.0 - Math.Exp(-dt / 0.100));
            _smoothedHighEnv += (effHigh - _smoothedHighEnv) * (1.0 - Math.Exp(-dt / 0.070));

            _cooldownBeat -= dt;
            _cooldownLow -= dt;
            _cooldownMid -= dt;
            _cooldownHigh -= dt;

            double baseVelocity = BaseWaveSpeed * waveMultiplier;
            double minCooldown = MinSpatialSpacing / Math.Max(0.15, baseVelocity);

            if (effBeat > 0.18 && curBeat > _smoothedBeatEnv && _cooldownBeat <= 0.0)
            {
                SpawnPacket(PacketKind.KickBullet, effBeat * 4.0, baseVelocity);
                _cooldownBeat = minCooldown * 0.95;
            }

            if ((effLow - _smoothedLowEnv) > 0.025 && effLow > 0.12 && _cooldownLow <= 0.0)
            {
                SpawnPacket(PacketKind.BassSwell, effLow * 3.2, baseVelocity);
                _cooldownLow = minCooldown * 1.10;
            }

            if ((effMid - _smoothedMidEnv) > 0.028 && effMid > 0.14 && _cooldownMid <= 0.0)
            {
                SpawnPacket(PacketKind.MidRipple, effMid * 2.5, baseVelocity);
                _cooldownMid = minCooldown * 0.85;
            }

            if ((effHigh - _smoothedHighEnv) > 0.030 && effHigh > 0.14 && _cooldownHigh <= 0.0)
            {
                SpawnPacket(PacketKind.HighNeedle, effHigh * 2.0, baseVelocity);
                _cooldownHigh = minCooldown * 0.70;
            }

            _pulsePhase = (_pulsePhase + (BasePulseSpeed * pulseMultiplier * dt)) % Math.Tau;
        }

        bool anyRemaining = false;
        for (int i = 0; i < MaxActivePackets; i++)
        {
            ref var p = ref _packets[i];
            if (!p.IsActive) continue;

            p.Position += p.Velocity * dt;
            p.Amplitude -= p.DecayRate * dt;

            if (p.Position - (p.Width * 0.5) > 1.05 || p.Amplitude <= 0.04)
            {
                p.IsActive = false;
            }
            else
            {
                anyRemaining = true;
            }
        }

        if (canRun || anyRemaining)
        {
            InvalidateVisual();
            _attachedTopLevel.RequestAnimationFrame(_frameCallback);
        }
        else
        {
            _isFrameLoopActive = false;
            InvalidateVisual();
        }
    }

    private void SpawnPacket(PacketKind kind, double rawAmp, double baseVelocity)
    {
        int targetIndex = -1;
        double minAmp = double.MaxValue;

        for (int i = 0; i < MaxActivePackets; i++)
        {
            if (!_packets[i].IsActive) { targetIndex = i; break; }
            if (_packets[i].Amplitude < minAmp) { minAmp = _packets[i].Amplitude; targetIndex = i; }
        }

        if (targetIndex < 0) return;

        ref var p = ref _packets[targetIndex];
        p.IsActive = true;
        p.Kind = kind;

        switch (kind)
        {
            case PacketKind.KickBullet:
                p.Amplitude = Math.Clamp(rawAmp * NextRandom(0.92, 1.08), 1.8, 4.4);
                p.Width = NextRandom(0.24, 0.32);
                p.Velocity = baseVelocity * NextRandom(0.98, 1.04);
                p.Frequency = 0.0;
                p.PhaseOffset = 0.0;
                p.HarmonicRatio = 0.0;
                p.HarmonicPhase = 0.0;
                p.DecayRate = NextRandom(0.09, 0.12);
                break;

            case PacketKind.BassSwell:
                p.Amplitude = Math.Clamp(rawAmp * NextRandom(0.90, 1.10), 1.4, 3.6);
                p.Width = NextRandom(0.32, 0.42);
                p.Velocity = baseVelocity * NextRandom(0.84, 0.94);
                p.Frequency = NextRandom(2.8, 4.4);
                p.PhaseOffset = NextRandom(-Math.PI, Math.PI);
                p.HarmonicRatio = NextRandom(0.15, 0.30);
                p.HarmonicPhase = NextRandom(0.0, Math.Tau);
                p.DecayRate = NextRandom(0.07, 0.10);
                break;

            case PacketKind.MidRipple:
                p.Amplitude = Math.Clamp(rawAmp * NextRandom(0.88, 1.12), 1.0, 2.9);
                p.Width = NextRandom(0.20, 0.28);
                p.Velocity = baseVelocity * NextRandom(1.00, 1.12);
                p.Frequency = NextRandom(5.2, 7.8);
                p.PhaseOffset = NextRandom(-Math.PI, Math.PI);
                p.HarmonicRatio = NextRandom(0.10, 0.25);
                p.HarmonicPhase = NextRandom(0.0, Math.Tau);
                p.DecayRate = NextRandom(0.10, 0.14);
                break;

            case PacketKind.HighNeedle:
                p.Amplitude = Math.Clamp(rawAmp * NextRandom(0.85, 1.15), 0.8, 2.4);
                p.Width = NextRandom(0.12, 0.18);
                p.Velocity = baseVelocity * NextRandom(1.15, 1.28);
                p.Frequency = NextRandom(8.5, 12.0);
                p.PhaseOffset = NextRandom(-Math.PI, Math.PI);
                p.HarmonicRatio = NextRandom(0.05, 0.20);
                p.HarmonicPhase = NextRandom(0.0, Math.Tau);
                p.DecayRate = NextRandom(0.13, 0.18);
                break;
        }

        p.Position = -(p.Width * 0.5);
    }

    #endregion

    #region Render & Spline Geometry

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (!IsActive) return;

        var bounds = Bounds;
        if (bounds.Width < 32.0 || bounds.Height < 16.0) return;

        double inset = InsetMargin;
        double width = bounds.Width - (2.0 * inset);
        double height = bounds.Height - (2.0 * inset);
        double radius = Math.Clamp(CornerRadius.TopLeft, 2.0, Math.Min(width / 2.0, height / 2.0));

        double horizLen = Math.Max(0.0, width - (2.0 * radius));
        double vertLen = Math.Max(0.0, height - (2.0 * radius));
        if (horizLen <= 0.0 || vertLen <= 0.0) return;

        double left = inset;
        double top = inset;
        double right = inset + width;
        double bottom = inset + height;

        bool useWave = EffectiveUseWave;
        bool isSelected = IsSelected;
        double thickness = isSelected ? SelectedBorderThickness : WaveThickness;

        if (!useWave)
        {
            double sine = (Math.Sin(_pulsePhase) + 1.0) * 0.5;
            double alphaRatio = isSelected
                ? Math.Clamp(0.70 + (0.30 * sine) + (0.15 * _smoothedBeatEnv), 0.70, 1.0)
                : Math.Clamp(0.18 + (0.55 * sine) + (0.20 * _smoothedBeatEnv), 0.15, 1.0);

            EnsurePen(thickness, (byte)Math.Clamp((int)(alphaRatio * 255.0), 0, 255));
            if (_cachedPen == null) return;

            context.DrawRectangle(null, _cachedPen, new RoundedRect(new Rect(left, top, width, height), radius, radius));
            return;
        }

        EnsurePen(thickness, 255);
        if (_cachedPen == null) return;

        int railSamples = Math.Clamp((int)(horizLen / SampleStepPx), MinRailSamples, MaxRailSamples);
        if (_railPoints.Length < railSamples)
            _railPoints = new Point[railSamples];

        double k = radius * BezierCircleConstant;

        var frameGeometry = new StreamGeometry();
        using (var ctx = frameGeometry.Open())
        {
            // 1. ВЕРХНЯЯ ГРАНЬ (Слева направо)
            for (int i = 0; i < railSamples; i++)
            {
                double u = i / (double)(railSamples - 1);
                _railPoints[i] = new Point(left + radius + (u * horizLen), top - CalculateOrganicDisplacement(u));
            }

            ctx.BeginFigure(_railPoints[0], isFilled: false);
            DrawSpline(ctx, _railPoints, railSamples);

            // 2. ВЕРХНИЙ ПРАВЫЙ УГОЛ И ПРАВЫЙ ТОРЕЦ
            ctx.CubicBezierTo(new Point(right - radius + k, top), new Point(right, top + radius - k), new Point(right, top + radius));
            ctx.LineTo(new Point(right, bottom - radius));

            // 3. НИЖНИЙ ПРАВЫЙ УГОЛ
            ctx.CubicBezierTo(new Point(right, bottom - radius + k), new Point(right - radius + k, bottom), new Point(right - radius, bottom));

            // 4. НИЖНЯЯ ГРАНЬ (Справа налево: u идет от 1.0 к 0.0)
            for (int i = 0; i < railSamples; i++)
            {
                double u = 1.0 - (i / (double)(railSamples - 1));
                _railPoints[i] = new Point(left + radius + (u * horizLen), bottom + CalculateOrganicDisplacement(u));
            }
            DrawSpline(ctx, _railPoints, railSamples);

            // 5. НИЖНИЙ ЛЕВЫЙ УГОЛ И ЛЕВЫЙ ТОРЕЦ
            ctx.CubicBezierTo(new Point(left + radius - k, bottom), new Point(left, bottom - radius + k), new Point(left, bottom - radius));
            ctx.LineTo(new Point(left, top + radius));

            // 6. ВЕРХНИЙ ЛЕВЫЙ УГОЛ (Замыкание контура)
            ctx.CubicBezierTo(new Point(left, top + radius - k), new Point(left + radius - k, top), new Point(left + radius, top));

            ctx.EndFigure(isClosed: true);
        }

        context.DrawGeometry(null, _cachedPen, frameGeometry);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DrawSpline(StreamGeometryContext ctx, Point[] points, int count)
    {
        const double SplineTension = 1.0 / 6.0;

        for (int i = 0; i < count - 1; i++)
        {
            Point p0 = points[i];
            Point p1 = points[i + 1];
            Point pPrev = i > 0 ? points[i - 1] : new Point((2.0 * p0.X) - p1.X, p0.Y);
            Point pNext = i < count - 2 ? points[i + 2] : new Point((2.0 * p1.X) - p0.X, p1.Y);

            Point c1 = new Point(p0.X + ((p1.X - pPrev.X) * SplineTension), p0.Y + ((p1.Y - pPrev.Y) * SplineTension));
            Point c2 = new Point(p1.X - ((pNext.X - p0.X) * SplineTension), p1.Y - ((pNext.Y - p0.Y) * SplineTension));

            ctx.CubicBezierTo(c1, c2, p1);
        }
    }

    /// <summary>
    /// Вычисляет гладкое органическое смещение волны с фазовой привязкой и софт-лимитером.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private double CalculateOrganicDisplacement(double u)
    {
        double rawDisp = 0.0;

        for (int i = 0; i < MaxActivePackets; i++)
        {
            ref readonly var p = ref _packets[i];
            if (!p.IsActive) continue;

            double dist = u - p.Position;
            double halfWidth = p.Width * 0.5;
            double absDist = Math.Abs(dist);
            if (absDist >= halfWidth) continue;

            double norm = absDist / halfWidth;
            double envelope = Math.Cos(Math.PI * 0.5 * norm);
            envelope *= envelope;

            double birthProgress = Math.Clamp((p.Position + halfWidth) / (p.Width * 0.8), 0.0, 1.0);
            double birthFade = birthProgress * birthProgress * (3.0 - (2.0 * birthProgress));

            double exitProgress = Math.Clamp((1.02 - p.Position) / (p.Width * 0.8), 0.0, 1.0);
            double exitFade = exitProgress * exitProgress * (3.0 - (2.0 * exitProgress));

            double packetValue;
            if (p.Frequency <= 0.01)
            {
                packetValue = envelope * p.Amplitude * birthFade * exitFade;
            }
            else
            {
                double phase = (Math.Tau * p.Frequency * dist) + p.PhaseOffset;
                double carrier = Math.Cos(phase) + (p.HarmonicRatio * Math.Cos((2.0 * phase) + p.HarmonicPhase));
                packetValue = envelope * p.Amplitude * carrier * birthFade * exitFade;
            }

            rawDisp += packetValue;
        }

        double safeDisp = SoftLimit(rawDisp, MaxSafeDisplacement);
        double edgeTaper = ComputeSmootherCornerBlend(u, CornerBlendMargin);
        return safeDisp * edgeTaper;
    }

    /// <summary>
    /// Алгебраический софт-лимитер с непрерывной производной для исключения вылета за пределы Bounds.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double SoftLimit(double x, double max)
    {
        double ratio = x / max;
        return x / Math.Sqrt(1.0 + (ratio * ratio));
    }

    /// <summary>
    /// Квинтиковый SmootherStep: первая и вторая производные в точках сопряжения равны строго нулю.
    /// Полностью устраняет высокочастотный дребезг касательных в алгоритме Катмулла-Рома.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ComputeSmootherCornerBlend(double u, double margin)
    {
        if (u < margin)
        {
            double t = u / margin;
            return t * t * t * (t * ((t * 6.0) - 15.0) + 10.0);
        }

        if (u > (1.0 - margin))
        {
            double t = (1.0 - u) / margin;
            return t * t * t * (t * ((t * 6.0) - 15.0) + 10.0);
        }

        return 1.0;
    }

    private void EnsurePen(double thickness, byte alpha)
    {
        var brush = ResolveWaveBrush();

        if (brush != _lastResolvedBrush ||
            _cachedPen == null ||
            Math.Abs(_lastRenderedThickness - thickness) > 0.001 ||
            _lastRenderedAlpha != alpha)
        {
            _lastResolvedBrush = brush;
            _lastRenderedThickness = thickness;
            _lastRenderedAlpha = alpha;

            if (brush is ISolidColorBrush scb)
            {
                var col = scb.Color;
                if (alpha < 255)
                    col = Color.FromArgb((byte)(col.A * alpha / 255), col.R, col.G, col.B);

                _cachedPen = new ImmutablePen(
                    col.ToUInt32(),
                    thickness,
                    lineCap: PenLineCap.Round,
                    lineJoin: PenLineJoin.Round);
            }
            else if (brush is IImmutableBrush ib)
            {
                _cachedPen = new ImmutablePen(
                    ib,
                    thickness,
                    lineCap: PenLineCap.Round,
                    lineJoin: PenLineJoin.Round);
            }
            else
            {
                _cachedPen = new Pen(
                    brush,
                    thickness,
                    lineCap: PenLineCap.Round,
                    lineJoin: PenLineJoin.Round);
            }
        }
    }

    private IBrush ResolveWaveBrush()
    {
        if (WaveBrush != null)
            return WaveBrush;

        if (this.TryFindResource("AccentBrush", out var res) && res is IBrush brush)
            return brush;

        return Brushes.DodgerBlue;
    }

    private void InvalidatePens()
    {
        _lastResolvedBrush = null;
        _cachedPen = null;
    }

    #endregion
}