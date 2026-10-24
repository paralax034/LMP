using Avalonia;
using Avalonia.Controls;

namespace LMP.UI.Features.Shell;

public partial class MainWindow
{
    #region Constants

    /// <summary>Минимальный интервал между GC-очистками (мс).</summary>
    private const int MinCleanupIntervalMs = 30_000;

    #endregion

    #region Fields — Suspend Timers

    private CancellationTokenSource? _cleanupCts;
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
        if (_isMinimized) return SuspendLevel.Soft;
        // Пока окно открыто и видимо на экране (даже если фокус на соседнем окне или втором мониторе),
        // не усыпляем UI и анимации. Suspend включается только при реальном сворачивании (Minimized / Tray).
        return SuspendLevel.None;
    }

    /// <summary>
    /// Применяет текущий уровень suspend ко всем VM через <see cref="ViewModelBase.BroadcastSuspendLevel"/>.
    /// Планирует GC-очистку для Hard/Soft режимов.
    /// </summary>
    private void ApplySuspendLevel()
    {
        var level = DetermineSuspendLevel();
        bool forceOptimize = level == SuspendLevel.Hard || (_isMinimized && ShouldOptimizeWhenInactive());

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
    /// Потеря фокуса окна. Окно остаётся видимым на экране, поэтому анимации не останавливаются.
    /// </summary>
    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (_isInTray || _isMinimized || _isRestoringFromTray) return;
        _isDeactivated = true;
    }

    /// <summary>
    /// Возвращение фокуса. Снимает mouse hook и сбрасывает <see cref="_isRestoringFromTray"/> guard.
    /// </summary>
    private void OnWindowActivated(object? sender, EventArgs e)
    {
        _isRestoringFromTray = false;

        if (_isInTray) return;

        if (OperatingSystem.IsWindows())
        {
            _trayManager?.ForceUninstallHook();
        }

        if (_isDeactivated)
        {
            _isDeactivated = false;
            Log.Debug("[Window] Activated");
        }
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