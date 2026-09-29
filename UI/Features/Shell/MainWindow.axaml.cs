using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.UI.Features.Shell;

/// <summary>
/// Главное окно приложения.
/// </summary>
public partial class MainWindow : Window
{
    #region Constants

    /// <summary>
    /// Минимальный интервал между toggle-ами окна из трея (мс).
    /// На Windows debounce в <see cref="TrayManager.TryInvokeToggle"/>.
    /// На non-Windows — в <see cref="ToggleTrayWindow"/>.
    /// </summary>
    public const int ToggleCooldownMs = 1000;

    #endregion

    #region Fields — Window State

    /// <summary>Окно свёрнуто в системный трей (Hard Suspend).</summary>
    private volatile bool _isInTray;

    /// <summary>Окно свёрнуто в панель задач (Soft Suspend).</summary>
    private volatile bool _isMinimized;

    /// <summary>Окно потеряло фокус (Soft Suspend после debounce).</summary>
    private volatile bool _isDeactivated;

    /// <summary>
    /// Guard: окно восстанавливается из трея.
    ///
    /// <para>Предотвращает re-suspend от Deactivated race condition на Windows:
    /// <c>Show()</c> + <c>Activate()</c> может вызвать Deactivated до того
    /// как окно получит foreground focus. Без guard'а это приводило к
    /// resume → 500мс → re-suspend.</para>
    ///
    /// <para>Сбрасывается в <see cref="OnWindowActivated"/> (нормальный путь)
    /// или через fallback таймер 1.5с (если фокус не пришёл).</para>
    /// </summary>
    private volatile bool _isRestoringFromTray;

    /// <summary>Принудительное закрытие (минуя диалог подтверждения).</summary>
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