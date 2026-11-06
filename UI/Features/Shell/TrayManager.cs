using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LMP.UI.Features.Shell;

/// <summary>
/// Native Windows system tray manager.
/// 
/// <para><b>Architecture:</b></para>
/// <list type="bullet">
///   <item>Native icon via Shell_NotifyIcon V4 (tooltip, icon, hover events)</item>
///   <item>Lazy Low-Level Mouse Hook on a <b>dedicated thread</b> with its own message pump:
///         installed ONLY on icon hover,
///         removed 2 seconds after the cursor leaves OR when the main window activates</item>
///   <item>Shell_NotifyIconGetRect to determine icon position (Windows 7+)</item>
///   <item>Custom Avalonia Popup instead of a native Win32 context menu</item>
/// </list>
/// 
/// <para><b>Why Dedicated Hook Thread:</b>
/// A WH_MOUSE_LL callback is invoked on the thread that installed the hook via its message pump.
/// If the hook is installed on the Avalonia UI thread, the message pump stalls
/// during layout/render/GC, causing system-wide mouse lag. A dedicated thread with a lightweight
/// GetMessage/DispatchMessage loop is independent of Avalonia, keeping mouse input responsive.
/// See MSDN LowLevelMouseProc: "it should run the hooks on a dedicated thread
/// that passes the work off to a worker thread and then immediately returns."</para>
/// 
/// <para><b>Why Lazy Hook:</b> Even on a dedicated thread, a permanent WH_MOUSE_LL hook
/// processes EVERY mouse event in the system. The lazy hook minimizes active time:
/// WM_MOUSEMOVE from Shell_NotifyIcon V4 → install hook → wheel scroll → 
/// uninstall after 2s of inactivity OR on main window activation.</para>
/// 
/// <para><b>Volume:</b> On mouse wheel scroll, a single method
/// <see cref="PlayerControlService.AdjustVolume"/> is called, while persistence to disk
/// is automatically debounced by the library service without blocking the hook thread.</para>
/// 
/// <para><b>Toggle debounce:</b> NIN_SELECT and WM_LBUTTONDBLCLK can both be fired
/// on a fast click. <see cref="TryInvokeToggle"/> uses a cooldown
/// <see cref="MainWindow.ToggleCooldownMs"/> to prevent double toggling.</para>
/// 
/// <para><b>Tooltip format:</b> <c>{AppName}: {TrackTitle} ({Volume}{VolumeEmoji})</c></para>
/// <para>Formatted via <see cref="TrayTooltipHelper"/> (DRY).</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class TrayManager : IDisposable
{
    #region Constants

    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 1;

    // Mouse message IDs (V4: received as LOWORD(lParam))
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_DESTROY = 0x0002;

    /// <summary>
    /// V4 sends WM_CONTEXTMENU along with WM_RBUTTONUP on right-click.
    /// Process ONLY WM_CONTEXTMENU for RMB to avoid triggering the popup twice.
    /// </summary>
    private const int WM_CONTEXTMENU = 0x007B;

    // NIN_ notifications (V4: received as LOWORD(lParam))
    /// <summary>Cursor entered icon bounding area (Shell_NotifyIcon V4).</summary>
    private const int NIN_POPUPOPEN = 0x0406;
    /// <summary>Cursor left icon bounding area (Shell_NotifyIcon V4).</summary>
    private const int NIN_POPUPCLOSE = 0x0407;

    /// <summary>
    /// V4: User clicked (select) the icon. Preferred over WM_LBUTTONUP.
    /// See MSDN: "for NOTIFYICON_VERSION_4 clients, NIN_SELECT is preferable".
    /// </summary>
    private const int NIN_SELECT = 0x0400;

    // Mouse Hook
    private const int WH_MOUSE_LL = 14;
    private const int HC_ACTION = 0;
    private const int WM_MOUSEWHEEL_MSG = 0x020A;

    // Shell_NotifyIcon
    private const int NIM_ADD = 0x00;
    private const int NIM_MODIFY = 0x01;
    private const int NIM_DELETE = 0x02;
    private const int NIM_SETVERSION = 0x04;

    private const int NIF_MESSAGE = 0x01;
    private const int NIF_ICON = 0x02;
    private const int NIF_TIP = 0x04;

    /// <summary>
    /// Windows Vista+. Instructs the Shell to show the standard tooltip when using NOTIFYICON_VERSION_4.
    /// 
    /// <para><b>CRITICAL:</b> Without this flag under V4, tooltips are suppressed (the shell expects
    /// the application to render its own popup on NIN_POPUPOPEN).
    /// The flag remains effective only until the next Shell_NotifyIcon call —
    /// therefore it MUST be passed on every NIM_MODIFY.</para>
    /// </summary>
    private const int NIF_SHOWTIP = 0x80;

    /// <summary>
    /// V4 is required to receive NIN_POPUPOPEN/NIN_POPUPCLOSE/NIN_SELECT/WM_CONTEXTMENU.
    /// WM_MOUSEWHEEL is intercepted via a lazy Mouse Hook on a dedicated thread.
    /// </summary>
    private const int NOTIFYICON_VERSION_4 = 4;

    /// <summary>Volume adjustment step per mouse wheel scroll click.</summary>
    private const int VolumeStep = 2;

    /// <summary>Minimum interval for updating tooltips on hover (ms).</summary>
    private const int TooltipRefreshIntervalMs = 2000;

    /// <summary>
    /// Auto-removal timeout for the mouse hook after the last hover/wheel event (ms).
    /// 2 seconds is sufficient for comfortable scrolling with pauses.
    /// </summary>
    private const int HookAutoUninstallMs = 2000;

    /// <summary>Lifetime of the tray icon RECT cache (ms).</summary>
    private const int IconRectCacheTtlMs = 5000;

    /// <summary>Custom message to control the hook from the UI thread → hook thread.</summary>
    private const int WM_APP_INSTALL_HOOK = WM_USER + 100;
    private const int WM_APP_UNINSTALL_HOOK = WM_USER + 101;
    private const int WM_APP_SHUTDOWN = WM_USER + 102;

    #endregion

    #region Win32 Imports — Shell & Window

#pragma warning disable SYSLIB1054
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);
#pragma warning restore SYSLIB1054

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial IntPtr DefWindowProcW(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandleW(string? lpModuleName);

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW")]
    private static partial IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT lpPoint);

    /// <summary>
    /// PostThreadMessage — actual DLL export: PostThreadMessageW.
    /// LibraryImport does NOT automatically append the W suffix — EntryPoint is required.
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessage(uint dwThreadId, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    /// <summary>
    /// GetMessage returns int: -1 (error), 0 (WM_QUIT), >0 (regular message).
    /// Marshalling as bool is incorrect: -1 → true results in an infinite loop on error.
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static partial int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(ref MSG lpMsg);

    [LibraryImport("user32.dll")]
    private static partial IntPtr DispatchMessageW(ref MSG lpMsg);

    /// <summary>
    /// PeekMessage — forcibly creates a Win32 message queue on the current thread.
    /// Must be called BEFORE Set() on ManualResetEvent so PostThreadMessage
    /// from the UI thread doesn't fail with ERROR_INVALID_THREAD_ID.
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    private const uint PM_NOREMOVE = 0x0000;

    #endregion

    #region Win32 Imports — Mouse Hook

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static partial IntPtr SetWindowsHookExW(int idHook, IntPtr lpfn, IntPtr hMod, uint dwThreadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(IntPtr hhk);

    [LibraryImport("user32.dll")]
    private static partial IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [LibraryImport("shell32.dll")]
    private static partial int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

    #endregion

    #region Win32 Structs

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public int mouseData;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    #endregion

    #region Fields

    private readonly PlayerControlService _playerControl;
    private readonly Action? _onToggleWindow;
    private readonly Action? _onExit;
    private readonly Action? _onOpenQueue;
    private readonly Action<int>? _onVolumeChanged;

    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private WndProcDelegate? _wndProc;
    private bool _isVisible;
    private bool _disposed;
    private string _tooltip = "Lite Music Player";

    /// <summary>Custom Avalonia window for the tray context menu. Created lazily.</summary>
    private TrayPopupWindow? _popupWindow;

    // Dedicated Hook Thread

    /// <summary>
    /// Dedicated thread for the WH_MOUSE_LL hook.
    /// Runs its own Win32 message loop (GetMessage/DispatchMessage),
    /// fully independent of the Avalonia UI thread.
    /// </summary>
    private Thread? _hookThread;

    /// <summary>Thread ID of the dedicated thread — used for PostThreadMessage.</summary>
    private volatile uint _hookThreadId;

    /// <summary>
    /// Readiness signal for the hook thread (message loop is running).
    /// Install/Uninstall requests via PostThreadMessage are valid only after Set().
    /// </summary>
    private readonly ManualResetEventSlim _hookThreadReady = new(false);

    /// <summary>Low-level mouse hook handle. Access ONLY from the hook thread.</summary>
    private IntPtr _mouseHookHandle;

    /// <summary>
    /// Delegate for the mouse hook callback — MUST keep a reference to prevent GC collection.
    /// </summary>
    private LowLevelMouseProc? _mouseHookDelegate;

    /// <summary>
    /// Timestamp of the last hover/wheel event (Environment.TickCount64).
    /// </summary>
    private long _lastHookActivityTime;

    /// <summary>Timer for auto-uninstalling the hook. Runs on UI thread.</summary>
    private Avalonia.Threading.DispatcherTimer? _hookTimeoutTimer;

    /// <summary>Popup visibility flag — volatile for lock-free reads from the hook thread.</summary>
    private volatile bool _isPopupVisible;

    /// <summary>Flag indicating whether the hook is currently installed. Volatile for reads from the UI thread.</summary>
    private volatile bool _isHookInstalled;

    // Tray icon RECT cache
    private RECT _cachedIconRect;
    private long _iconRectCacheTime;
    private volatile bool _iconRectCacheValid;

    /// <summary>Timestamp of the last tooltip update on hover.</summary>
    private DateTime _lastTooltipRefresh = DateTime.MinValue;

    /// <summary>
    /// Timestamp of the last window toggle (Environment.TickCount64).
    /// Used for debouncing NIN_SELECT / WM_LBUTTONDBLCLK.
    /// </summary>
    private long _lastToggleTime;

    private const string WindowClassName = "LMPTrayWindow";

    /// <summary>
    /// Returns the current visibility state of the main window.
    /// Func instead of bool — captures the actual value at the moment the popup opens.
    /// </summary>
    private readonly Func<bool>? _isWindowVisible;

    #endregion

    #region Constructor

    /// <summary>
    /// Creates a tray manager with a native icon and a custom Avalonia Popup.
    /// Mouse hook is NOT installed immediately — only on icon hover.
    /// The hook operates on a dedicated thread to eliminate mouse lag.
    /// </summary>
    /// <param name="playerControl">Player control service</param>
    /// <param name="onToggleWindow">Toggle show/hide main window (LMB on icon)</param>
    /// <param name="onExit">Exit application</param>
    /// <param name="onOpenQueue">Open queue</param>
    /// <param name="onVolumeChanged">Callback triggered on scroll volume changes</param>
    /// <param name="isWindowVisible">
    /// Function returning the current main window visibility.
    /// Invoked every time the popup opens to get the current Show/Hide button state.
    /// </param>
    public TrayManager(
        PlayerControlService playerControl,
        Action? onToggleWindow = null,
        Action? onExit = null,
        Action? onOpenQueue = null,
        Action<int>? onVolumeChanged = null,
        Func<bool>? isWindowVisible = null)
    {
        _playerControl = playerControl;
        _onToggleWindow = onToggleWindow;
        _onExit = onExit;
        _onOpenQueue = onOpenQueue;
        _onVolumeChanged = onVolumeChanged;
        _isWindowVisible = isWindowVisible;

        _mouseHookDelegate = MouseHookCallback;

        CreateMessageWindow();
        StartHookThread();
    }

    #endregion

    #region Window Creation

    private void CreateMessageWindow()
    {
        _wndProc = WndProc;
        IntPtr hInstance = GetModuleHandleW(null);

        var wndClass = new WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = WindowClassName
        };

        RegisterClassW(ref wndClass);

        _hwnd = CreateWindowExW(
            0, WindowClassName, "LMP Tray",
            0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            Log.Error($"[TrayManager] CreateWindow failed, error={error}");
        }
    }

    #endregion

    #region Icon Management

    /// <summary>
    /// Shows the icon in the system tray.
    /// The mouse hook is NOT installed — it will be installed lazily on hover.
    /// </summary>
    public void Show(IntPtr? iconHandle = null)
    {
        if (_isVisible || _hwnd == IntPtr.Zero) return;

        if (iconHandle.HasValue && iconHandle.Value != IntPtr.Zero)
            _hIcon = iconHandle.Value;

        if (_hIcon == IntPtr.Zero)
            _hIcon = LoadDefaultIcon();

        var nid = CreateNotifyIconData();
        nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        nid.uCallbackMessage = WM_TRAYICON;
        nid.hIcon = _hIcon;
        nid.szTip = TruncateTooltip(_tooltip);

        bool result = Shell_NotifyIconW(NIM_ADD, ref nid);

        if (result)
        {
            nid.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIconW(NIM_SETVERSION, ref nid);

            _isVisible = true;
            Log.Debug("[TrayManager] Icon shown (hook will be installed on hover)");
        }
        else
        {
            int error = Marshal.GetLastWin32Error();
            Log.Warn($"[TrayManager] Shell_NotifyIcon ADD failed, error={error}");
        }
    }

    /// <summary>
    /// Hides the icon from the system tray and uninstalls the hook if it was installed.
    /// </summary>
    public void Hide()
    {
        if (!_isVisible || _hwnd == IntPtr.Zero) return;

        RequestUninstallHook();

        var nid = CreateNotifyIconData();
        Shell_NotifyIconW(NIM_DELETE, ref nid);
        _isVisible = false;
        _iconRectCacheValid = false;

        Log.Debug("[TrayManager] Icon hidden");
    }

    /// <summary>
    /// Updates the tray icon tooltip text.
    /// 
    /// <para><b>NIF_SHOWTIP:</b> Passed on EVERY NIM_MODIFY — otherwise tooltip is reset under V4.</para>
    /// 
    /// <para><b>forceRefresh:</b> When <c>true</c>, sets szTip to empty first, then to the actual string.
    /// Forces the shell to immediately refresh the tooltip window.</para>
    /// </summary>
    public void SetTooltip(string text, bool forceRefresh = false)
    {
        _tooltip = text;

        if (!_isVisible || _hwnd == IntPtr.Zero) return;

        if (forceRefresh)
        {
            var clearNid = CreateNotifyIconData();
            clearNid.uFlags = NIF_TIP | NIF_SHOWTIP;
            clearNid.szTip = "";
            Shell_NotifyIconW(NIM_MODIFY, ref clearNid);
        }

        var nid = CreateNotifyIconData();
        nid.uFlags = NIF_TIP | NIF_SHOWTIP;
        nid.szTip = TruncateTooltip(text);
        Shell_NotifyIconW(NIM_MODIFY, ref nid);
    }

    /// <summary>
    /// Updates the system tray icon.
    /// NIF_SHOWTIP is passed to avoid resetting the tooltip display.
    /// </summary>
    public void SetIcon(IntPtr iconHandle)
    {
        _hIcon = iconHandle;

        if (!_isVisible || _hwnd == IntPtr.Zero) return;

        var nid = CreateNotifyIconData();
        nid.uFlags = NIF_ICON | NIF_SHOWTIP;
        nid.hIcon = iconHandle;

        Shell_NotifyIconW(NIM_MODIFY, ref nid);
    }

    /// <summary>
    /// Updates the tooltip using the current player state.
    /// </summary>
    public void UpdateTooltipFromPlayerState()
    {
        int volume = _playerControl.GetCurrentVolume();
        SetTooltip(TrayTooltipHelper.Format(_playerControl.CurrentTrack, volume));
    }

    /// <summary>
    /// Updates the tooltip with an emphasis on volume (for scroll events).
    /// Uses <c>forceRefresh: true</c> for immediate rendering.
    /// </summary>
    public void UpdateTooltipWithVolumeAccent()
    {
        int volume = _playerControl.GetCurrentVolume();
        SetTooltip(
            TrayTooltipHelper.FormatWithVolumeAccent(_playerControl.CurrentTrack, volume),
            forceRefresh: true);
    }

    /// <summary>
    /// Immediately unhooks and commits volume.
    /// Called from MainWindow on window activation.
    /// </summary>
    public void ForceUninstallHook()
    {
        if (!_isHookInstalled) return;

        RequestUninstallHook();
        Log.Debug("[TrayManager] Hook force-uninstalled (window activated)");
    }

    private NOTIFYICONDATAW CreateNotifyIconData()
    {
        return new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            szTip = "",
            szInfo = "",
            szInfoTitle = ""
        };
    }

    private static IntPtr LoadDefaultIcon()
        => LoadIconW(IntPtr.Zero, 32512);

    private static string TruncateTooltip(string text)
        => text.Length > TrayTooltipHelper.MaxTooltipLength
            ? text[..TrayTooltipHelper.MaxTooltipLength]
            : text;

    #endregion

    #region Icon RECT Cache

    /// <summary>
    /// Updates the cached tray icon RECT via P/Invoke.
    /// </summary>
    private void RefreshIconRectCache()
    {
        var id = new NOTIFYICONIDENTIFIER
        {
            cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = _hwnd,
            uID = 1
        };

        int hr = Shell_NotifyIconGetRect(ref id, out RECT rect);

        if (hr == 0)
        {
            _cachedIconRect = rect;
            _iconRectCacheValid = true;
        }
        else
        {
            _iconRectCacheValid = false;
        }

        Volatile.Write(ref _iconRectCacheTime, Environment.TickCount64);
    }

    /// <summary>
    /// Checks cursor position relative to the cached RECT.
    /// Updates the cache if TTL has expired.
    /// </summary>
    private bool IsCursorOverTrayIconCached(int cursorX, int cursorY)
    {
        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref _iconRectCacheTime) > IconRectCacheTtlMs)
            RefreshIconRectCache();

        if (!_iconRectCacheValid)
            return false;

        return cursorX >= _cachedIconRect.Left && cursorX <= _cachedIconRect.Right
            && cursorY >= _cachedIconRect.Top && cursorY <= _cachedIconRect.Bottom;
    }

    #endregion

    #region Dedicated Hook Thread

    /// <summary>
    /// Starts a dedicated background thread with a Win32 message loop for the mouse hook.
    /// </summary>
    private void StartHookThread()
    {
        _hookThread = new Thread(HookThreadProc)
        {
            Name = "LMP.MouseHookThread",
            IsBackground = true
        };
        _hookThread.Start();

        _hookThreadReady.Wait(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Entry point for the dedicated hook thread.
    /// </summary>
    private void HookThreadProc()
    {
        _hookThreadId = GetCurrentThreadId();

        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);
        _hookThreadReady.Set();

        int result;
        while ((result = GetMessageW(out MSG msg, IntPtr.Zero, 0, 0)) != 0)
        {
            if (result == -1)
            {
                int error = Marshal.GetLastWin32Error();
                Log.Warn($"[TrayManager] HookThread GetMessage error={error}, exiting");
                break;
            }

            switch (msg.message)
            {
                case WM_APP_INSTALL_HOOK:
                    InstallHookOnThread();
                    break;

                case WM_APP_UNINSTALL_HOOK:
                    UninstallHookOnThread();
                    break;

                case WM_APP_SHUTDOWN:
                    UninstallHookOnThread();
                    return;

                default:
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                    break;
            }
        }

        UninstallHookOnThread();
    }

    /// <summary>
    /// Installs the hook on the current (hook) thread.
    /// MUST be called from HookThreadProc on WM_APP_INSTALL_HOOK.
    /// </summary>
    private void InstallHookOnThread()
    {
        if (_mouseHookHandle != IntPtr.Zero) return;
        if (_mouseHookDelegate == null) return;

        IntPtr funcPtr = Marshal.GetFunctionPointerForDelegate(_mouseHookDelegate);
        IntPtr hMod = GetModuleHandleW("user32");

        _mouseHookHandle = SetWindowsHookExW(WH_MOUSE_LL, funcPtr, hMod, 0);

        if (_mouseHookHandle == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            Log.Warn($"[TrayManager] Failed to install mouse hook, error={error}");
            return;
        }

        _isHookInstalled = true;
        RefreshIconRectCache();

        Log.Debug("[TrayManager] Mouse hook installed (lazy, on hover)");
    }

    /// <summary>
    /// Uninstalls the hook on the current (hook) thread.
    /// MUST be called from HookThreadProc.
    /// </summary>
    private void UninstallHookOnThread()
    {
        if (_mouseHookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHookHandle);
            _mouseHookHandle = IntPtr.Zero;
            _isHookInstalled = false;
            Log.Debug("[TrayManager] Mouse hook uninstalled");
        }
    }

    #endregion

    #region Lazy Mouse Hook — UI Thread Side

    /// <summary>
    /// Requests hook installation via PostThreadMessage to the hook thread.
    /// Called from the UI thread (WndProc) when hovering over the icon.
    /// </summary>
    private void RequestInstallHook()
    {
        Volatile.Write(ref _lastHookActivityTime, Environment.TickCount64);

        if (_isHookInstalled) return;

        uint threadId = _hookThreadId;
        if (threadId == 0) return;

        PostThreadMessage(threadId, WM_APP_INSTALL_HOOK, IntPtr.Zero, IntPtr.Zero);
        StartHookTimeoutTimer();
    }

    /// <summary>
    /// Requests mouse hook uninstallation on the dedicated thread.
    /// </summary>
    private void RequestUninstallHook()
    {
        StopHookTimeoutTimer();

        uint threadId = _hookThreadId;
        if (threadId != 0 && _isHookInstalled)
        {
            PostThreadMessage(threadId, WM_APP_UNINSTALL_HOOK, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>
    /// Starts DispatcherTimer to monitor hook timeout.
    /// </summary>
    private void StartHookTimeoutTimer()
    {
        if (_hookTimeoutTimer == null)
        {
            _hookTimeoutTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1000)
            };
            _hookTimeoutTimer.Tick += OnHookTimeoutTick;
        }

        if (!_hookTimeoutTimer.IsEnabled)
            _hookTimeoutTimer.Start();
    }

    private void StopHookTimeoutTimer()
    {
        _hookTimeoutTimer?.Stop();
    }

    /// <summary>
    /// Tick handler: checks if enough time has elapsed since the last activity.
    /// </summary>
    private void OnHookTimeoutTick(object? sender, EventArgs e)
    {
        if (!_isHookInstalled)
        {
            StopHookTimeoutTimer();
            return;
        }

        long elapsed = Environment.TickCount64 - Volatile.Read(ref _lastHookActivityTime);
        if (elapsed >= HookAutoUninstallMs)
        {
            RequestUninstallHook();
        }
    }

    #endregion

    #region Mouse Hook Callback (runs on Hook Thread)

    /// <summary>
    /// Callback low-level mouse hook.
    /// </summary>
    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < HC_ACTION || (int)wParam != WM_MOUSEWHEEL_MSG)
            return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);

        if (!_isVisible || _isPopupVisible)
            return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);

        int cursorX = Marshal.ReadInt32(lParam, 0);
        int cursorY = Marshal.ReadInt32(lParam, 4);

        if (!IsCursorOverTrayIconCached(cursorX, cursorY))
            return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);

        // Wheel event over icon
        Volatile.Write(ref _lastHookActivityTime, Environment.TickCount64);

        int mouseData = Marshal.ReadInt32(lParam, 8);
        short wheelDelta = (short)(mouseData >> 16);
        int volumeDelta = wheelDelta > 0 ? VolumeStep : -VolumeStep;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            int newVolume = _playerControl.AdjustVolume(volumeDelta);
            _onVolumeChanged?.Invoke(newVolume);
        });

        return 1;
    }

    #endregion

    #region Window Procedure & Toggle Debounce

    /// <summary>
    /// Window message handler for Shell_NotifyIcon V4.
    /// </summary>
    private IntPtr WndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam)
    {
        if (uMsg == WM_TRAYICON)
        {
            int eventId = (int)(lParam.ToInt64() & 0xFFFF);

            switch (eventId)
            {
                case NIN_SELECT:
                case WM_LBUTTONDBLCLK:
                    TryInvokeToggle();
                    break;

                case WM_CONTEXTMENU:
                    ShowPopupMenu();
                    break;

                case WM_MOUSEMOVE:
                    RequestInstallHook();

                    var now = DateTime.UtcNow;
                    if ((now - _lastTooltipRefresh).TotalMilliseconds > TooltipRefreshIntervalMs)
                    {
                        _lastTooltipRefresh = now;
                        UpdateTooltipFromPlayerState();
                    }
                    break;

                case NIN_POPUPOPEN:
                    RequestInstallHook();
                    break;

                case NIN_POPUPCLOSE:
                    break;
            }

            return IntPtr.Zero;
        }

        if (uMsg == WM_DESTROY)
        {
            Hide();
            return IntPtr.Zero;
        }

        return DefWindowProcW(hWnd, uMsg, wParam, lParam);
    }

    /// <summary>
    /// Triggers window toggle with debounce protection.
    /// </summary>
    private void TryInvokeToggle()
    {
        long now = Environment.TickCount64;
        long elapsed = now - Volatile.Read(ref _lastToggleTime);

        if (elapsed < MainWindow.ToggleCooldownMs)
        {
            Log.Debug($"[TrayManager] Toggle throttled (elapsed={elapsed}ms < {MainWindow.ToggleCooldownMs}ms)");
            return;
        }

        Volatile.Write(ref _lastToggleTime, now);
        _onToggleWindow?.Invoke();
    }

    #endregion

    #region Popup Menu (Avalonia)

    /// <summary>
    /// Shows a custom Avalonia context menu window.
    /// Passes the current window visibility state for correct Show/Hide text.
    /// </summary>
    private void ShowPopupMenu()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_popupWindow == null)
                {
                    _popupWindow = new TrayPopupWindow();
                    _popupWindow.Initialize(
                        _playerControl,
                        onShowWindow: _onToggleWindow,
                        onExit: _onExit,
                        onOpenQueue: _onOpenQueue,
                        onCleanMemory: () => MemoryCleanupHelper.PerformCleanup(aggressive: true),
                        onVolumeChanged: vol => _onVolumeChanged?.Invoke(vol));

                    _popupWindow.PropertyChanged += (_, e) =>
                    {
                        if (e.Property == Avalonia.Visual.IsVisibleProperty)
                            _isPopupVisible = _popupWindow.IsVisible;
                    };
                }

                bool isVisible = _isWindowVisible?.Invoke() ?? false;
                GetCursorPos(out POINT pt);
                _popupWindow.ShowAt(pt.X, pt.Y, isVisible);
            }
            catch (Exception ex)
            {
                Log.Error($"[TrayManager] Failed to show popup: {ex.Message}");
            }
        });
    }

    #endregion

    #region Dispose

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopHookTimeoutTimer();
        _hookTimeoutTimer = null;

        uint threadId = _hookThreadId;
        if (threadId != 0)
        {
            PostThreadMessage(threadId, WM_APP_SHUTDOWN, IntPtr.Zero, IntPtr.Zero);
            _hookThread?.Join(TimeSpan.FromSeconds(2));
        }
        _hookThreadReady.Dispose();

        Hide();

        try { _popupWindow?.Close(); }
        catch { /* ignore */ }
        _popupWindow = null;

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        _wndProc = null;
        _mouseHookDelegate = null;
        Log.Debug("[TrayManager] Disposed");
    }

    #endregion
}