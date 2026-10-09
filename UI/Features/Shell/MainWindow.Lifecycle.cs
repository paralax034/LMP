using Avalonia;
using Avalonia.Controls;

namespace LMP.UI.Features.Shell;

public partial class MainWindow
{
    #region Constants

    /// <summary>Задержка перед Soft Suspend при потере фокуса (мс).</summary>
    private const int DeactivateSuspendDelayMs = 500;

    /// <summary>Минимальный интервал между GC-очистками (мс).</summary>
    private const int MinCleanupIntervalMs = 30_000;

    #endregion

    #region Fields — Suspend Timers

    private CancellationTokenSource? _cleanupCts;
    private CancellationTokenSource? _deactivateCts;
    private DateTime _lastCleanupTime = DateTime.MinValue;

    #endregion

    private void InitializeLifecycle()
    {
        PropertyChanged += OnWindowPropertyChanged;
        Deactivated += OnWindowDeactivated;
        Activated += OnWindowActivated;
    }

    private void CleanupLifecycle()
    {
        CancelDeactivateSuspend();
        CancelCleanup();

        PropertyChanged -= OnWindowPropertyChanged;
        Deactivated -= OnWindowDeactivated;
        Activated -= OnWindowActivated;
    }

    /// <summary>
    /// Определяет текущий уровень приостановки на основе состояния окна.
    /// </summary>
    private SuspendLevel DetermineSuspendLevel()
    {
        if (_isInTray) return SuspendLevel.Hard;
        if (_isMinimized || _isDeactivated) return SuspendLevel.Soft;
        return SuspendLevel.None;
    }

    /// <summary>
    /// Применяет текущий уровень suspend ко всем VM через <see cref="ViewModelBase.BroadcastSuspendLevel"/>.
    /// Планирует GC-очистку для Hard/Soft режимов.
    /// </summary>
    private void ApplySuspendLevel()
    {
        var level = DetermineSuspendLevel();
        bool forceOptimize = level == SuspendLevel.Hard || ShouldOptimizeWhenInactive();

        ViewModelBase.BroadcastSuspendLevel(level, forceOptimize);

        if (level != SuspendLevel.None && forceOptimize)
        {
            var cleanupDelay = level == SuspendLevel.Hard
                ? TimeSpan.FromSeconds(5)
                : TimeSpan.FromMinutes(2);
            ScheduleCleanup(cleanupDelay);
        }
        else
        {
            CancelCleanup();
        }
    }

    private bool ShouldOptimizeWhenInactive()
    {
        try { return _library?.Settings.OptimizeWhenInactive ?? true; }
        catch { return true; }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty)
            HandleWindowStateChanged((WindowState)e.NewValue!);
    }

    /// <summary>
    /// Обрабатывает смену состояния окна.
    /// MinimizeToTray: если настройка включена, перехватывает Minimized → прячет в трей.
    /// </summary>
    private void HandleWindowStateChanged(WindowState state)
    {
        UpdateMaximizeRestoreIcons(state);

        if (state == WindowState.Minimized)
        {
            if (ShouldMinimizeToTray())
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    WindowState = WindowState.Normal;
                    MinimizeToTray();
                });
                return;
            }

            _isMinimized = true;
            _isDeactivated = false;
            ApplySuspendLevel();
        }
        else if (_isMinimized)
        {
            _isMinimized = false;
            ApplySuspendLevel();
        }
    }

    /// <summary>
    /// Потеря фокуса. Запускает debounce-таймер на <see cref="DeactivateSuspendDelayMs"/>.
    /// </summary>
    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (_isInTray || _isMinimized || _isRestoringFromTray) return;

        CancelDeactivateSuspend();
        _deactivateCts = new CancellationTokenSource();
        var token = _deactivateCts.Token;

        _ = Task.Run(async () =>
        {
            bool completedNormal = await token.DelayNoThrowAsync(
                TimeSpan.FromMilliseconds(DeactivateSuspendDelayMs),
                continueOnCapturedContext: false);
            if (!completedNormal) return;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!IsActive && !_isInTray && !_isMinimized && !_isRestoringFromTray)
                {
                    _isDeactivated = true;
                    ApplySuspendLevel();
                    Log.Debug("[Window] Deactivated → Soft Suspend");
                }
            });
        });
    }

    /// <summary>
    /// Возвращение фокуса. Отменяет pending suspend, снимает mouse hook,
    /// сбрасывает <see cref="_isRestoringFromTray"/> guard.
    /// </summary>
    private void OnWindowActivated(object? sender, EventArgs e)
    {
        CancelDeactivateSuspend();

        _isRestoringFromTray = false;

        if (_isInTray) return;

        if (OperatingSystem.IsWindows())
        {
            _trayManager?.ForceUninstallHook();
        }

        if (_isDeactivated)
        {
            _isDeactivated = false;
            ApplySuspendLevel();
            Log.Debug("[Window] Activated → Suspend lifted");
        }
    }

    private void CancelDeactivateSuspend()
    {
        if (_deactivateCts is null) return;

        _deactivateCts.Cancel();
        _deactivateCts.Dispose();
        _deactivateCts = null;
    }

    private void ScheduleCleanup(TimeSpan delay)
    {
        CancelCleanup();
        _cleanupCts = new CancellationTokenSource();
        var token = _cleanupCts.Token;

        _ = Task.Run(async () =>
        {
            bool completedNormal = await token.DelayNoThrowAsync(delay, continueOnCapturedContext: false);
            if (!completedNormal) return;

            var now = DateTime.UtcNow;
            if ((now - _lastCleanupTime).TotalMilliseconds < MinCleanupIntervalMs)
                return;

            _lastCleanupTime = now;
            MemoryCleanupHelper.PerformCleanup(aggressive: false);
        });
    }

    private void CancelCleanup()
    {
        if (_cleanupCts is null) return;

        _cleanupCts.Cancel();
        _cleanupCts.Dispose();
        _cleanupCts = null;
    }
}