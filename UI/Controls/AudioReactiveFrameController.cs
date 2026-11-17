using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Threading;

namespace LMP.UI.Controls;

/// <summary>
/// Bounded render-thread-safe pool base for zero-allocation <see cref="ICustomDrawOperation"/> nodes.
/// </summary>
internal abstract class PooledDrawOperation<TSelf> : ICustomDrawOperation
    where TSelf : PooledDrawOperation<TSelf>, new()
{
    private const int MaxPoolSize = 16;
    private static readonly ConcurrentQueue<TSelf> Pool = new();
    private static int _poolCount;
    private int _disposed;

    public abstract Rect Bounds { get; }
    public virtual bool HitTest(Point p) => false;
    public virtual bool Equals(ICustomDrawOperation? other) => false;
    public abstract void Render(ImmediateDrawingContext context);

    protected static TSelf RentCore()
    {
        if (Pool.TryDequeue(out var op))
            Interlocked.Decrement(ref _poolCount);
        else
            op = new TSelf();

        Volatile.Write(ref op._disposed, 0);
        return op;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            if (Interlocked.Increment(ref _poolCount) <= MaxPoolSize)
                Pool.Enqueue((TSelf)this);
            else
                Interlocked.Decrement(ref _poolCount);
        }
    }
}

/// <summary>
/// Shared zero-allocation xorshift64* PRNG for wave packet emitters.
/// </summary>
internal struct FastXorShift64
{
    private ulong _state;

    public FastXorShift64(ulong seed)
    {
        _state = seed != 0 ? seed : 0x9E3779B97F4A7C15UL;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double NextDouble(double min, double max)
    {
        _state ^= _state >> 12;
        _state ^= _state << 25;
        _state ^= _state >> 27;
        ulong result = _state * 0x2545F4914F6CDD1DUL;
        double norm = (result >> 11) * (1.0 / (1UL << 53));
        return min + ((max - min) * norm);
    }
}

/// <summary>
/// Shared 4-band audio envelope smoother and cooldown tracker for audio-reactive UI controls.
/// </summary>
internal struct AudioReactiveSignalState
{
    public double Beat;
    public double Low;
    public double Mid;
    public double High;

    public double CooldownBeat;
    public double CooldownLow;
    public double CooldownMid;
    public double CooldownHigh;

    public void Reset()
    {
        Beat = 0;
        Low = 0;
        Mid = 0;
        High = 0;
        CooldownBeat = 0;
        CooldownLow = 0;
        CooldownMid = 0;
        CooldownHigh = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AdvanceCooldowns(double dt)
    {
        CooldownBeat -= dt;
        CooldownLow -= dt;
        CooldownMid -= dt;
        CooldownHigh -= dt;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Decay(double dt, double timeConstant)
    {
        double decay = Math.Exp(-dt / timeConstant);
        Beat *= decay;
        Low *= decay;
        Mid *= decay;
        High *= decay;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SmoothToward(
        double targetBeat,
        double targetLow,
        double targetMid,
        double targetHigh,
        double dt,
        double tauBeat,
        double tauLow,
        double tauMid,
        double tauHigh)
    {
        Beat += (targetBeat - Beat) * (1.0 - Math.Exp(-dt / tauBeat));
        Low += (targetLow - Low) * (1.0 - Math.Exp(-dt / tauLow));
        Mid += (targetMid - Mid) * (1.0 - Math.Exp(-dt / tauMid));
        High += (targetHigh - High) * (1.0 - Math.Exp(-dt / tauHigh));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasEnergy(double threshold = 0.01) =>
        Beat > threshold || Low > threshold || Mid > threshold || High > threshold;
}

/// <summary>
/// Coordinates page-visibility, suspend-level, and silence-wakeup polling on the UI thread
/// for render-thread <see cref="Avalonia.Rendering.Composition.CompositionCustomVisualHandler"/> controls.
/// Performs zero per-frame work during active playback or paused idle state.
/// </summary>
internal sealed class CompositionSilenceMonitor
{
    private const int IdleWakeIntervalMs = 200;

    private readonly Visual _owner;
    private readonly Func<bool> _shouldAnimate;
    private readonly Func<bool>? _shouldPollSilence;
    private readonly Action _onStateReevaluated;
    private readonly Action _onResumeFromSilence;

    private readonly Action _activePageChangedHandler;
    private readonly Action<SuspendLevel> _suspendLevelChangedHandler;
    private readonly Action _silenceEnteredCallback;
    private readonly EventHandler<AvaloniaPropertyChangedEventArgs> _topLevelPropertyChangedHandler;

    private TopLevel? _attachedTopLevel;
    private DispatcherTimer? _idleWakeTimer;
    private bool _isAttached;
    private bool _isWaitingForAudio;

    public CompositionSilenceMonitor(
        Visual owner,
        Func<bool> shouldAnimate,
        Action onStateReevaluated,
        Action onResumeFromSilence,
        Func<bool>? shouldPollSilence = null)
    {
        _owner = owner;
        _shouldAnimate = shouldAnimate;
        _shouldPollSilence = shouldPollSilence;
        _onStateReevaluated = onStateReevaluated;
        _onResumeFromSilence = onResumeFromSilence;

        _activePageChangedHandler = HandleContextChanged;
        _suspendLevelChangedHandler = OnSuspendLevelChanged;
        _silenceEnteredCallback = HandleSilenceEnteredOnUiThread;
        _topLevelPropertyChangedHandler = OnTopLevelPropertyChanged;
    }

    public bool IsWaitingForAudio => _isWaitingForAudio;

    public bool CanAnimateNow =>
        _isAttached
        && _attachedTopLevel != null
        && _shouldAnimate()
        && PersistentPageHost.CanAnimateVisual(_owner, _attachedTopLevel);

    private bool CanPollSilenceNow =>
        CanAnimateNow && (_shouldPollSilence == null || _shouldPollSilence());

    public void Attach()
    {
        var topLevel = TopLevel.GetTopLevel(_owner);
        if (!ReferenceEquals(_attachedTopLevel, topLevel))
        {
            if (_attachedTopLevel != null)
                _attachedTopLevel.PropertyChanged -= _topLevelPropertyChangedHandler;

            _attachedTopLevel = topLevel;
            if (_attachedTopLevel != null)
                _attachedTopLevel.PropertyChanged += _topLevelPropertyChangedHandler;
        }

        if (!_isAttached)
        {
            PersistentPageHost.ActivePageChanged += _activePageChangedHandler;
            ViewModelBase.SuspendLevelChanged += _suspendLevelChangedHandler;
            _isAttached = true;
        }
    }

    public void Detach()
    {
        if (_isAttached)
        {
            PersistentPageHost.ActivePageChanged -= _activePageChangedHandler;
            ViewModelBase.SuspendLevelChanged -= _suspendLevelChangedHandler;
            _isAttached = false;
        }

        if (_attachedTopLevel != null)
        {
            _attachedTopLevel.PropertyChanged -= _topLevelPropertyChangedHandler;
            _attachedTopLevel = null;
        }

        StopIdleTimer();
    }

    public void StopIdleTimer()
    {
        _isWaitingForAudio = false;
        _idleWakeTimer?.Stop();
    }

    /// <summary>
    /// Called from the render thread when audio silence settles so both UI and render threads can sleep
    /// until <see cref="AudioVisualizerBuffer.HasRecentActivity"/> turns back on.
    /// </summary>
    public void PostSilenceEntered()
    {
        Dispatcher.UIThread.Post(_silenceEnteredCallback, DispatcherPriority.Background);
    }

    private void HandleSilenceEnteredOnUiThread()
    {
        if (!CanPollSilenceNow)
        {
            StopIdleTimer();
            return;
        }

        if (AudioVisualizerBuffer.HasRecentActivity)
        {
            StopIdleTimer();
            _onResumeFromSilence();
            return;
        }

        _isWaitingForAudio = true;
        _idleWakeTimer ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(IdleWakeIntervalMs),
            DispatcherPriority.Background,
            OnIdleWakeTick);
        _idleWakeTimer.Start();
    }

    private void OnIdleWakeTick(object? sender, EventArgs e)
    {
        if (!_isWaitingForAudio || !CanPollSilenceNow)
        {
            StopIdleTimer();
            return;
        }

        if (AudioVisualizerBuffer.HasRecentActivity)
        {
            StopIdleTimer();
            _onResumeFromSilence();
        }
    }

    private void HandleContextChanged()
    {
        if (!CanAnimateNow)
            StopIdleTimer();

        _onStateReevaluated();
    }

    private void OnTopLevelPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Window.WindowStateProperty && e.Property != Visual.IsVisibleProperty)
            return;

        HandleContextChanged();

        if (!CanAnimateNow
            && _attachedTopLevel is Window w
            && w.IsVisible
            && w.WindowState != WindowState.Minimized)
        {
            Dispatcher.UIThread.Post(_activePageChangedHandler, DispatcherPriority.Loaded);
        }
    }

    private void OnSuspendLevelChanged(SuspendLevel _)
    {
        if (Dispatcher.UIThread.CheckAccess())
            HandleContextChanged();
        else
            Dispatcher.UIThread.Post(HandleContextChanged);
    }
}