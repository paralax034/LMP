using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.UI.Features.Shell;

/// <summary>
/// Main application window.
/// </summary>
public partial class MainWindow : Window
{
    #region Constants

    /// <summary>
    /// Minimum interval between window toggles from the tray (ms).
    /// On Windows, debouncing is handled in <see cref="TrayManager.TryInvokeToggle"/>.
    /// On non-Windows — in <see cref="ToggleTrayWindow"/>.
    /// </summary>
    public const int ToggleCooldownMs = 1000;

    #endregion

    #region Fields — Window State

    /// <summary>Window is minimized to the system tray (Hard Suspend).</summary>
    private volatile bool _isInTray;

    /// <summary>Window is minimized to the taskbar (Soft Suspend).</summary>
    private volatile bool _isMinimized;

    /// <summary>Window lost focus (Soft Suspend after debounce).</summary>
    private volatile bool _isDeactivated;

    /// <summary>
    /// Guard: window is being restored from the tray.
    ///
    /// <para>Prevents re-suspend from Deactivated race condition on Windows:
    /// <c>Show()</c> + <c>Activate()</c> can trigger Deactivated before
    /// the window receives foreground focus. Without the guard, this resulted in
    /// resume → 500ms → re-suspend.</para>
    ///
    /// <para>Reset in <see cref="OnWindowActivated"/> (normal path)
    /// or via a 1.5s fallback timer (if focus was not received).</para>
    /// </summary>
    private volatile bool _isRestoringFromTray;

    /// <summary>Forced close (bypassing the confirmation dialog).</summary>
    private bool _forceClose;

    #endregion

    #region Fields — Services

    private PlayerControlService? _playerControl;
    private readonly LibraryService? _library;
    private readonly Grid? _rootGrid;

    #endregion

    #region Constructor & Initialization

    public MainWindow()
    {
        InitializeComponent();

        _rootGrid = this.FindControl<Grid>("RootGrid");
        if (_rootGrid != null)
            MousePositionHelper.Attach(_rootGrid);

        try { _library = AppEntry.Services.GetRequiredService<LibraryService>(); }
        catch (Exception ex) { Log.Warn($"[Window] LibraryService not available: {ex.Message}"); }

        InitializeChrome();
        InitializeLifecycle();
        SetupTrayIcon();
        InitializeCopyHint();

        SingleInstanceGuard.RegisterActivationCallback(() =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(BringToFront);
        });
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    #endregion

    #region Window Activation & Restore

    public void BringToFront()
    {
        if (_isInTray)
        {
            RestoreFromTray();
        }
        else
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;

            if (!IsVisible)
                Show();

            Activate();
        }

        if (OperatingSystem.IsWindows())
        {
            var handle = TryGetPlatformHandle();
            if (handle != null && handle.Handle != IntPtr.Zero)
            {
                SetForegroundWindow(handle.Handle);
            }
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    #endregion

    #region Cleanup

    protected override void OnClosed(EventArgs e)
    {
        SingleInstanceGuard.RegisterActivationCallback(null);

        CleanupLifecycle();
        CleanupTray();
        CleanupCopyHint();

        base.OnClosed(e);
    }

    #endregion
}