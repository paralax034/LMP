using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.UI.Features.Shell;

/// <summary>
/// Главное окно приложения.
///
/// <para><b>Lifecycle:</b> управляет видимостью окна (Normal / Minimized / Tray),
/// уровнем приостановки (<see cref="SuspendLevel"/>) и системными ресурсами.</para>
///
/// <para><b>Suspend архитектура (три уровня):</b></para>
/// <list type="bullet">
///   <item><b>None</b> — окно активно, все подписки работают</item>
///   <item><b>Soft</b> — окно свёрнуто в taskbar или потеряло фокус (500мс debounce)</item>
///   <item><b>Hard</b> — окно свёрнуто в tray, максимальная экономия ресурсов</item>
/// </list>
///
/// <para><b>Tray:</b> иконка ВСЕГДА видна в системном трее.
/// На Windows — нативный <see cref="TrayManager"/> с перехватом WM_MOUSEWHEEL.
/// На других платформах — стандартный Avalonia <see cref="TrayIcon"/>.</para>
/// </summary>
public partial class MainWindow : Window
{
    #region Constants

    /// <summary>Задержка перед Soft Suspend при потере фокуса (мс).</summary>
    private const int DeactivateSuspendDelayMs = 500;

    /// <summary>Минимальный интервал между GC-очистками (мс).</summary>
    private const int MinCleanupIntervalMs = 30_000;

    /// <summary>Задержка восстановления tooltip после показа громкости (мс).</summary>
    private const int TooltipRestoreDelayMs = 3000;

    /// <summary>
    /// Минимальный интервал между toggle-ами окна из трея (мс).
    /// На Windows debounce в <see cref="TrayManager.TryInvokeToggle"/>.
    /// На non-Windows — в <see cref="ToggleTrayWindow"/>.
    /// </summary>
    public const int ToggleCooldownMs = 1000;

    /// <summary>Длительность отображения Copy Hint (мс), считывается напрямую из синглтона.</summary>
    private static int CopyHintDurationMs => CopyHintService.Instance.DisplayDurationMs;

    /// <summary>Длительность fade-out Copy Hint (мс).</summary>
    private const int CopyHintFadeDurationMs = 150;

    #endregion

    #region Fields — Window Chrome

    private Button? _minimizeButton;
    private Button? _maximizeButton;
    private Button? _closeButton;
    private Border? _dragArea;
    private Grid? _rootGrid;

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
    private LibraryService? _library;

    #endregion

    #region Fields — Suspend Timers

    private CancellationTokenSource? _cleanupCts;
    private CancellationTokenSource? _deactivateCts;
    private DateTime _lastCleanupTime = DateTime.MinValue;

    #endregion

    #region Fields — Tray

    /// <summary>Нативный менеджер трея Windows (lazy mouse hook для скролла громкости).</summary>
    private TrayManager? _trayManager;

    /// <summary>Таймер восстановления tooltip после показа громкости (Windows debounce).</summary>
    private Avalonia.Threading.DispatcherTimer? _tooltipRestoreTimer;

    /// <summary>Стандартный менеджер трея от Avalonia для Linux/macOS.</summary>
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _playPauseItem;
    private NativeMenuItem? _nextItem;
    private NativeMenuItem? _prevItem;
    private NativeMenuItem? _repeatItem;
    private NativeMenuItem? _showItem;
    private NativeMenuItem? _queueItem;
    private NativeMenuItem? _cleanMemItem;
    private NativeMenuItem? _exitItem;

    /// <summary>Timestamp последнего toggle (non-Windows debounce).</summary>
    private long _lastToggleTime;

    #endregion

    #region Fields — Copy Hint

    private readonly Canvas? _copyHintCanvas;
    private readonly Border? _copyHintOverlay;
    private readonly TextBlock? _copyHintText;
    private readonly PathIcon? _copyHintIcon;
    private CancellationTokenSource? _copyHintCts;

    #endregion

    #region Constructor & Initialization

    public MainWindow()
    {
        InitializeComponent();

        _copyHintCanvas = this.FindControl<Canvas>("CopyHintCanvas");
        _copyHintOverlay = this.FindControl<Border>("CopyHintOverlay");
        _copyHintText = this.FindControl<TextBlock>("CopyHintText");
        _copyHintIcon = this.FindControl<PathIcon>("CopyHintIcon");
        _rootGrid = this.FindControl<Grid>("RootGrid");

        if (_rootGrid != null)
            MousePositionHelper.Attach(_rootGrid);

        PropertyChanged += OnWindowPropertyChanged;
        Deactivated += OnWindowDeactivated;
        Activated += OnWindowActivated;

        CopyHintService.Instance.HintRequested += OnCopyHintRequested;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _minimizeButton = this.FindControl<Button>("MinimizeButton");
        _maximizeButton = this.FindControl<Button>("MaximizeButton");
        _closeButton = this.FindControl<Button>("CloseButton");
        _dragArea = this.FindControl<Border>("DragArea");
        _rootGrid = this.FindControl<Grid>("RootGrid");

        _minimizeButton?.Click += (_, _) => HandleMinimizeClick();
        _maximizeButton?.Click += (_, _) => ToggleMaximize();
        _closeButton?.Click += (_, _) => Close();

        var titleBar = this.FindControl<Grid>("TitleBar");
        titleBar?.PointerPressed += OnTitleBarPointerPressed;
        _dragArea?.DoubleTapped += (_, _) => ToggleMaximize();

        try { _library = AppEntry.Services.GetRequiredService<LibraryService>(); }
        catch (Exception ex) { Log.Warn($"[Window] LibraryService not available: {ex.Message}"); }

        SetupTrayIcon();
    }

    #endregion

    #region Suspend Management

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

    #endregion

    #region Window State (Minimize / Maximize)

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

    private void HandleMinimizeClick()
    {
        if (ShouldMinimizeToTray())
            MinimizeToTray();
        else
            WindowState = WindowState.Minimized;
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    /// <summary>
    /// Обновляет иконки Maximize/Restore в title bar.
    /// </summary>
    private void UpdateMaximizeRestoreIcons(WindowState state)
    {
        var maximizeIcon = this.FindControl<Border>("MaximizeIcon");
        var restoreIcon = this.FindControl<Grid>("RestoreIcon");

        maximizeIcon?.IsVisible = state != WindowState.Maximized;
        restoreIcon?.IsVisible = state == WindowState.Maximized;
    }

    /// <summary>
    /// Проверяет пользовательскую настройку MinimizeToTray.
    /// </summary>
    private bool ShouldMinimizeToTray()
    {
        try { return _library?.Settings.MinimizeToTray == true; }
        catch { return false; }
    }

    #endregion

    #region Window Focus (Activate / Deactivate)

    /// <summary>
    /// Потеря фокуса. Запускает debounce-таймер на <see cref="DeactivateSuspendDelayMs"/>.
    ///
    /// <remarks><see cref="_isRestoringFromTray"/> guard блокирует deactivation
    /// во время restore из трея — на Windows <c>Show()</c> + <c>Activate()</c>
    /// часто вызывают Deactivated до получения foreground focus.</remarks>
    /// </summary>
    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (_isInTray || _isMinimized || _isRestoringFromTray) return;

        CancelDeactivateSuspend();
        _deactivateCts = new CancellationTokenSource();
        var token = _deactivateCts.Token;

        _ = Task.Delay(DeactivateSuspendDelayMs, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!IsActive && !_isInTray && !_isMinimized && !_isRestoringFromTray)
                {
                    _isDeactivated = true;
                    ApplySuspendLevel();
                    Log.Debug("[Window] Deactivated → Soft Suspend");
                }
            });
        }, TaskScheduler.Default);
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

    #endregion

    #region Tray — Setup

    /// <summary>
    /// Инициализирует иконку трея. На Windows — нативный TrayManager,
    /// на других платформах — Avalonia TrayIcon. Иконка показывается сразу.
    /// </summary>
    private void SetupTrayIcon()
    {
        try
        {
            _playerControl = AppEntry.Services.GetRequiredService<PlayerControlService>();

            if (OperatingSystem.IsWindows())
            {
                SetupWindowsTray();
            }
            else
            {
                SetupAvaloniaTray();
            }

            SubscribeToPlayerControl();
            Log.Info("[Tray] Configured");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Tray] Setup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Создаёт нативный Windows TrayManager с mouse hook для скролла громкости.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private void SetupWindowsTray()
    {
        _trayManager = new TrayManager(
            _playerControl!,
            onToggleWindow: () => Avalonia.Threading.Dispatcher.UIThread.Post(ToggleTrayWindow),
            onExit: () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _forceClose = true;
                Close();
            }),
            onOpenQueue: () => Avalonia.Threading.Dispatcher.UIThread.Post(OnTrayGoToQueue),
            onVolumeChanged: vol =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => HandleTrayVolumeChanged(vol)),
            isWindowVisible: () => IsVisible && !_isInTray && WindowState != WindowState.Minimized);

        _trayManager.SetIcon(LoadWindowsIcon());
        _trayManager.Show();
        _trayManager.UpdateTooltipFromPlayerState();

        Log.Debug("[Tray] Windows TrayManager created");
    }

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static partial IntPtr LoadImage(
        IntPtr hInst, string name, uint type, int cx, int cy, uint load);

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW")]
    [SupportedOSPlatform("windows")]
    private static partial IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;

    /// <summary>
    /// Загружает иконку из avares:// ресурсов для Shell_NotifyIcon.
    /// Fallback: IDI_APPLICATION (стандартная системная иконка).
    /// </summary>
    /// <returns>Дескриптор нативной иконки Windows (<see cref="IntPtr"/>).</returns>
    [SupportedOSPlatform("windows")]
    private static IntPtr LoadWindowsIcon()
    {
        const string iconPath = "avares://LMP/Assets/app.ico";

        try
        {
            var uri = new Uri(iconPath);
            if (Avalonia.Platform.AssetLoader.Exists(uri))
            {
                string tempPath = Path.Combine(Path.GetTempPath(), "lmp_tray_icon.ico");

                using (var source = Avalonia.Platform.AssetLoader.Open(uri))
                using (var fs = File.Create(tempPath))
                {
                    source.CopyTo(fs);
                }

                IntPtr hIcon = LoadImage(IntPtr.Zero, tempPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE);

                try { File.Delete(tempPath); } catch { /* best effort */ }

                if (hIcon != IntPtr.Zero) return hIcon;
            }
        }
        catch { }

        return LoadIconW(IntPtr.Zero, 32512); // IDI_APPLICATION
    }

    /// <summary>
    /// Настраивает Avalonia TrayIcon для Linux/macOS с контекстным меню.
    /// </summary>
    private void SetupAvaloniaTray()
    {
        var icons = TrayIcon.GetIcons(Application.Current!);
        if (icons is not { Count: > 0 })
        {
            Log.Warn("[Tray] No TrayIcon defined in App.axaml");
            return;
        }

        _trayIcon = icons[0];
        LoadTrayIconImage();
        _trayIcon.IsVisible = true;
        _trayIcon.Clicked += (_, _) =>
        {
            ToggleTrayWindow();
            UpdateShowHideItemText();
        };

        BuildAvaloniaTrayMenu();
        UpdateTrayTooltip();

        LocalizationService.Instance.LanguageChanged += OnTrayLanguageChanged;
    }

    /// <summary>
    /// Собирает нативное контекстное меню трея (non-Windows).
    /// </summary>
    private void BuildAvaloniaTrayMenu()
    {
        if (_trayIcon is null) return;

        var L = LocalizationService.Instance;
        var menu = new NativeMenu();

        _showItem = new NativeMenuItem(FormatShowHideText());
        _showItem.Click += (_, _) => { ToggleTrayWindow(); UpdateShowHideItemText(); };
        menu.Add(_showItem);

        menu.Add(new NativeMenuItemSeparator());

        _playPauseItem = new NativeMenuItem($"►  {L["Tray_Play"] ?? "Play"}") { IsEnabled = false };
        _playPauseItem.Click += (_, _) => _ = _playerControl?.PlayPauseAsync();
        menu.Add(_playPauseItem);

        _nextItem = new NativeMenuItem($"»  {L["Tray_Next"] ?? "Next"}") { IsEnabled = false };
        _nextItem.Click += (_, _) => _ = _playerControl?.NextAsync();
        menu.Add(_nextItem);

        _prevItem = new NativeMenuItem($"«  {L["Tray_Previous"] ?? "Previous"}") { IsEnabled = false };
        _prevItem.Click += (_, _) => _ = _playerControl?.PreviousAsync();
        menu.Add(_prevItem);

        _repeatItem = new NativeMenuItem($"↻  {L["Tray_Repeat"] ?? "Repeat"}") { IsEnabled = false };
        _repeatItem.Click += (_, _) => _playerControl?.ToggleRepeat();
        menu.Add(_repeatItem);

        menu.Add(new NativeMenuItemSeparator());

        _queueItem = new NativeMenuItem($"≡  {L["Tray_Queue"] ?? "Queue"}");
        _queueItem.Click += (_, _) => OnTrayGoToQueue();
        menu.Add(_queueItem);

        _cleanMemItem = new NativeMenuItem($"⟳  {L["Tray_ClearMemory"] ?? "Clear Memory"}");
        _cleanMemItem.Click += (_, _) => OnTrayClearMemory();
        menu.Add(_cleanMemItem);

        menu.Add(new NativeMenuItemSeparator());

        _exitItem = new NativeMenuItem($"×  {L["Tray_Exit"] ?? "Exit"}");
        _exitItem.Click += (_, _) => { _forceClose = true; Close(); };
        menu.Add(_exitItem);

        _trayIcon.Menu = menu;
    }

    /// <summary>
    /// Загружает иконку для Avalonia TrayIcon из ресурсов приложения.
    /// </summary>
    private void LoadTrayIconImage()
    {
        if (_trayIcon is null) return;

        const string iconPath = "avares://LMP/Assets/app.ico";

        try
        {
            var uri = new Uri(iconPath);
            if (Avalonia.Platform.AssetLoader.Exists(uri))
            {
                using var stream = Avalonia.Platform.AssetLoader.Open(uri);
                _trayIcon.Icon = new WindowIcon(stream);
                return;
            }
        }
        catch { /* try next */ }

        if (Icon != null) _trayIcon.Icon = Icon;
    }

    #endregion

    #region Tray — Show / Restore

    /// <summary>
    /// Toggle видимости окна по ЛКМ на иконке трея.
    /// Debounce: на Windows — в <see cref="TrayManager.TryInvokeToggle"/>,
    /// на non-Windows — здесь через <see cref="_lastToggleTime"/>.
    /// </summary>
    private void ToggleTrayWindow()
    {
        if (!OperatingSystem.IsWindows())
        {
            long now = Environment.TickCount64;
            if (now - Volatile.Read(ref _lastToggleTime) < ToggleCooldownMs)
            {
                Log.Debug("[Window] Toggle throttled");
                return;
            }
            Volatile.Write(ref _lastToggleTime, now);
        }

        if (_isInTray || !IsVisible || WindowState == WindowState.Minimized)
            RestoreFromTray();
        else
            MinimizeToTray();
    }

    /// <summary>
    /// Сворачивает окно в трей (Hard Suspend).
    /// Иконка уже видна — просто прячем окно.
    /// </summary>
    private void MinimizeToTray()
    {
        Hide();

        _isInTray = true;
        _isMinimized = false;
        _isDeactivated = false;
        CancelDeactivateSuspend();
        ApplySuspendLevel();

        if (!OperatingSystem.IsWindows())
        {
            UpdateShowHideItemText();
        }

        Log.Info("[Window] Minimized to tray");
    }

    /// <summary>
    /// Восстанавливает окно из трея.
    /// </summary>
    private void RestoreFromTray()
    {
        _isRestoringFromTray = true;
        CancelDeactivateSuspend();

        Show();
        WindowState = WindowState.Normal;
        Activate();

        _isInTray = false;
        _isMinimized = false;
        _isDeactivated = false;

        _playerControl?.ForceSync();
        ApplySuspendLevel();

        _ = ClearRestoreGuardFallbackAsync();

        if (!OperatingSystem.IsWindows())
        {
            UpdateShowHideItemText();
        }

        Log.Info("[Window] Restored from tray");
    }

    /// <summary>
    /// Fallback сброс restore guard.
    /// </summary>
    private async Task ClearRestoreGuardFallbackAsync()
    {
        await Task.Delay(1500);

        if (_isRestoringFromTray)
        {
            _isRestoringFromTray = false;
            Log.Debug("[Window] Restore guard cleared by fallback");
        }
    }

    #endregion

    #region Tray — Player Subscriptions

    /// <summary>
    /// Подписки на PlayerControlService для обновления трея.
    /// </summary>
    private void SubscribeToPlayerControl()
    {
        if (_playerControl is null) return;

        if (OperatingSystem.IsWindows())
        {
            SubscribeWindowsPlayerControl();
        }
        else
        {
            SubscribeNonWindowsPlayerControl();
        }

        _playerControl.ResumeRequested += HandleExternalResumeRequest;
    }

    private void UnsubscribeFromPlayerControl()
    {
        if (_playerControl is null) return;

        if (OperatingSystem.IsWindows())
        {
            UnsubscribeWindowsPlayerControl();
        }
        else
        {
            UnsubscribeNonWindowsPlayerControl();
        }

        _playerControl.ResumeRequested -= HandleExternalResumeRequest;
    }

    [SupportedOSPlatform("windows")]
    private void SubscribeWindowsPlayerControl()
    {
        _playerControl!.CurrentTrackChanged += OnWindowsTrackChanged;
        _playerControl.IsPlayingChanged += OnWindowsPlayingChanged;
    }

    [SupportedOSPlatform("windows")]
    private void UnsubscribeWindowsPlayerControl()
    {
        _playerControl!.CurrentTrackChanged -= OnWindowsTrackChanged;
        _playerControl.IsPlayingChanged -= OnWindowsPlayingChanged;
    }

    [SupportedOSPlatform("windows")]
    private void OnWindowsTrackChanged(TrackInfo? _)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateWindowsTooltip);
    }

    [SupportedOSPlatform("windows")]
    private void OnWindowsPlayingChanged(bool _)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateWindowsTooltip);
    }

    [SupportedOSPlatform("windows")]
    private void UpdateWindowsTooltip()
    {
        _trayManager?.UpdateTooltipFromPlayerState();
    }

    private void SubscribeNonWindowsPlayerControl()
    {
        _playerControl!.IsPlayingChanged += OnNonWindowsPlayingChanged;
        _playerControl.CurrentTrackChanged += OnNonWindowsTrackChanged;
        _playerControl.RepeatModeChanged += OnNonWindowsRepeatModeChanged;
        _playerControl.VolumeChanged += OnNonWindowsVolumeChanged;
    }

    private void UnsubscribeNonWindowsPlayerControl()
    {
        if (_playerControl is null) return;

        _playerControl.IsPlayingChanged -= OnNonWindowsPlayingChanged;
        _playerControl.CurrentTrackChanged -= OnNonWindowsTrackChanged;
        _playerControl.RepeatModeChanged -= OnNonWindowsRepeatModeChanged;
        _playerControl.VolumeChanged -= OnNonWindowsVolumeChanged;
    }

    private void OnNonWindowsPlayingChanged(bool isPlaying)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            UpdatePlayPauseMenuText(isPlaying);
            UpdateTrayTooltip();
        });
    }

    private void OnNonWindowsTrackChanged(TrackInfo? track)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            UpdatePlaybackItemsEnabled(track != null);
            UpdateTrayTooltip();
        });
    }

    private void OnNonWindowsRepeatModeChanged(RepeatMode _)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateRepeatMenuText);
    }

    private void OnNonWindowsVolumeChanged(int _)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateTrayTooltip);
    }

    /// <summary>
    /// Обрабатывает внешний запрос на resume (например из PlayerBarView при hover на Volume).
    /// </summary>
    private void HandleExternalResumeRequest()
    {
        if (_isInTray)
        {
            RestoreFromTray();
            return;
        }

        if (ViewModelBase.CurrentSuspendLevel != SuspendLevel.None)
        {
            _isDeactivated = false;
            _isMinimized = false;
            ApplySuspendLevel();
        }
    }

    #endregion

    #region Tray — Menu Actions

    /// <summary>
    /// Восстанавливает окно и навигирует на страницу очереди.
    /// </summary>
    private void OnTrayGoToQueue()
    {
        RestoreFromTray();

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is MainWindowViewModel vm)
                vm.NavigateCommand.Execute("Queue");
        });
    }

    /// <summary>
    /// Выполняет очистку памяти по команде из меню трея.
    /// </summary>
    private static void OnTrayClearMemory()
    {
        MemoryCleanupHelper.PerformCleanup(aggressive: true);
    }

    /// <summary>
    /// Обрабатывает изменение громкости через скролл на иконке трея (Windows).
    /// </summary>
    private void HandleTrayVolumeChanged(int newVolume)
    {
        if (OperatingSystem.IsWindows())
        {
            UpdateWindowsVolumeTooltip();
        }

        Log.Debug($"[Tray] Volume: {newVolume}%");
    }

    [SupportedOSPlatform("windows")]
    private void UpdateWindowsVolumeTooltip()
    {
        if (_trayManager is null) return;

        _trayManager.UpdateTooltipWithVolumeAccent();

        _tooltipRestoreTimer ??= CreateTooltipRestoreTimer();
        _tooltipRestoreTimer.Stop();
        _tooltipRestoreTimer.Start();
    }

    [SupportedOSPlatform("windows")]
    private Avalonia.Threading.DispatcherTimer CreateTooltipRestoreTimer()
    {
        var timer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(TooltipRestoreDelayMs)
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _trayManager?.UpdateTooltipFromPlayerState();
        };

        return timer;
    }

    #endregion

    #region Tray — Platform Helpers (non-Windows)

    private string FormatShowHideText()
    {
        var L = LocalizationService.Instance;
        bool isVisible = IsVisible && !_isInTray && WindowState != WindowState.Minimized;

        return isVisible
            ? $"●  {L["Tray_Hide"] ?? "Hide"}"
            : $"●  {L["Tray_Show"] ?? "Show"}";
    }

    private void UpdateShowHideItemText()
    {
        _showItem?.Header = FormatShowHideText();
    }

    private void UpdateTrayTooltip()
    {
        if (_trayIcon is null) return;
        int volume = _playerControl?.CurrentVolume ?? 0;
        _trayIcon.ToolTipText = TrayTooltipHelper.Format(_playerControl?.CurrentTrack, volume);
    }

    private void UpdatePlaybackItemsEnabled(bool enabled)
    {
        _playPauseItem?.IsEnabled = enabled;
        _nextItem?.IsEnabled = enabled;
        _prevItem?.IsEnabled = enabled;
        _repeatItem?.IsEnabled = enabled;
    }

    private void UpdatePlayPauseMenuText(bool isPlaying)
    {
        if (_playPauseItem is null) return;
        var L = LocalizationService.Instance;

        _playPauseItem.Header = isPlaying
            ? $"‖  {L["Tray_Pause"] ?? "Pause"}"
            : $"►  {L["Tray_Play"] ?? "Play"}";
    }

    private void UpdateRepeatMenuText()
    {
        if (_repeatItem is null || _playerControl is null) return;
        var L = LocalizationService.Instance;

        _repeatItem.Header = _playerControl.RepeatMode switch
        {
            RepeatMode.All => $"↻• {L["Tray_RepeatAll"] ?? "All"}",
            RepeatMode.One => $"↺• {L["Tray_RepeatOne"] ?? "One"}",
            _ => $"↻  {L["Tray_Repeat"] ?? "Repeat"}"
        };
    }

    private void OnTrayLanguageChanged(object? sender, string e)
    {
        var L = LocalizationService.Instance;

        UpdateShowHideItemText();
        _nextItem?.Header = $"»  {L["Tray_Next"] ?? "Next"}";
        _prevItem?.Header = $"«  {L["Tray_Previous"] ?? "Previous"}";
        _queueItem?.Header = $"≡  {L["Tray_Queue"] ?? "Queue"}";
        _cleanMemItem?.Header = $"⟳  {L["Tray_ClearMemory"] ?? "Clear Memory"}";
        _exitItem?.Header = $"×  {L["Tray_Exit"] ?? "Exit"}";

        if (_playerControl != null)
        {
            UpdatePlayPauseMenuText(_playerControl.IsPlaying);
            UpdateRepeatMenuText();
        }

        UpdateTrayTooltip();
    }

    #endregion

    #region Close Handling

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_forceClose)
        {
            base.OnClosing(e);
            return;
        }

        var closeAction = _library?.Settings.CloseAction ?? CloseAction.Exit;

        switch (closeAction)
        {
            case CloseAction.MinimizeToTray:
                e.Cancel = true;
                MinimizeToTray();
                break;

            case CloseAction.Ask:
                e.Cancel = true;
                await HandleAskCloseAsync();
                break;

            case CloseAction.Exit:
            default:
                base.OnClosing(e);
                break;
        }
    }

    private async Task HandleAskCloseAsync()
    {
        var dialog = AppEntry.Services.GetRequiredService<DialogService>();
        var result = await dialog.ShowCloseActionDialogAsync();

        if (result is null) return;

        if (result.IsChecked)
            _library?.UpdateSettings(s => s.CloseAction = result.Value);

        if (result.Value == CloseAction.MinimizeToTray)
            MinimizeToTray();
        else
        {
            _forceClose = true;
            Close();
        }
    }

    #endregion

    #region Memory Cleanup

    private void ScheduleCleanup(TimeSpan delay)
    {
        CancelCleanup();
        _cleanupCts = new CancellationTokenSource();
        var token = _cleanupCts.Token;

        _ = Task.Run(async () =>
        {
            bool completedNormal = await DelayNoThrowAsync(delay, token);
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

    private static async Task<bool> DelayNoThrowAsync(TimeSpan delay, CancellationToken token)
    {
        if (token.IsCancellationRequested)
            return false;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using (token.Register(static state =>
               {
                   ((TaskCompletionSource<bool>)state!).TrySetResult(false);
               }, tcs))
        {
            var delayTask = Task.Delay(delay, CancellationToken.None);
            var completedTask = await Task.WhenAny(delayTask, tcs.Task).ConfigureAwait(false);
            return completedTask == delayTask;
        }
    }

    #endregion

    #region Title Bar & Drag

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button) return;

        if (e.Source is Visual visual)
        {
            for (var parent = visual; parent != null; parent = parent.GetVisualParent())
            {
                if (parent is Button) return;
            }
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    #endregion

    #region Copy Hint Overlay

    private void OnCopyHintRequested(string text, CopyHintKind kind, Point? cursorPosition)
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => OnCopyHintRequested(text, kind, cursorPosition));
            return;
        }

        ShowCopyHint(text, kind, cursorPosition);
    }

    private async void ShowCopyHint(string text, CopyHintKind kind, Point? cursorPosition)
    {
        if (_copyHintOverlay is null || _copyHintText is null || _copyHintCanvas is null)
            return;

        _copyHintCts?.Cancel();
        _copyHintCts?.Dispose();
        var cts = new CancellationTokenSource();
        _copyHintCts = cts;

        try
        {
            ApplyCopyHintStyle(kind);
            _copyHintText.Text = text;

            _copyHintOverlay.IsVisible = true;
            _copyHintOverlay.Opacity = 0;

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                () => { }, Avalonia.Threading.DispatcherPriority.Render);

            PositionCopyHint(cursorPosition);
            _copyHintOverlay.Opacity = 1;

            await Task.Delay(CopyHintDurationMs, cts.Token);

            _copyHintOverlay.Opacity = 0;
            await Task.Delay(CopyHintFadeDurationMs, cts.Token);

            _copyHintOverlay.IsVisible = false;
        }
        catch (OperationCanceledException) { }
    }

    private void PositionCopyHint(Point? cursorPosition)
    {
        if (_copyHintCanvas is null || _copyHintOverlay is null) return;

        double hintW = _copyHintOverlay.DesiredSize.Width;
        double hintH = _copyHintOverlay.DesiredSize.Height;
        double canvasW = _copyHintCanvas.Bounds.Width;
        double canvasH = _copyHintCanvas.Bounds.Height;

        const double margin = 8.0;

        if (cursorPosition is not { } cursor)
        {
            Canvas.SetLeft(_copyHintOverlay, Math.Max(margin, (canvasW - hintW) * 0.5));
            Canvas.SetTop(_copyHintOverlay, Math.Max(margin, canvasH - hintH - 40));
            return;
        }

        double x = cursor.X - hintW / 2.0;
        double y = cursor.Y - hintH - 12;

        if (y < margin)
            y = cursor.Y + 24;

        x = Math.Clamp(x, margin, canvasW - hintW - margin);
        y = Math.Clamp(y, margin, canvasH - hintH - margin);

        Canvas.SetLeft(_copyHintOverlay, x);
        Canvas.SetTop(_copyHintOverlay, y);
    }

    private void ApplyCopyHintStyle(CopyHintKind kind)
    {
        if (_copyHintIcon is null || _copyHintOverlay is null) return;

        var (iconKey, brushKey) = kind switch
        {
            CopyHintKind.Warning => ("Icon.InformationOutline", "SystemWarnOrangeBrush"),
            CopyHintKind.Error => ("Icon.Close", "SystemErrorBrush"),
            _ => ("Icon.CheckCircle", "AccentBrush")
        };

        var theme = Application.Current?.ActualThemeVariant;

        if (Application.Current?.Resources.TryGetResource(iconKey, theme, out var geo) == true
            && geo is StreamGeometry geometry)
            _copyHintIcon.Data = geometry;

        if (Application.Current?.Resources.TryGetResource(brushKey, theme, out var res) == true
            && res is IBrush brush)
        {
            _copyHintIcon.Foreground = brush;
            _copyHintOverlay.BorderBrush = brush;
        }
    }

    #endregion

    #region Cleanup

    protected override void OnClosed(EventArgs e)
    {
        CancelDeactivateSuspend();
        CancelCleanup();

        UnsubscribeFromPlayerControl();

        if (OperatingSystem.IsWindows())
        {
            _tooltipRestoreTimer?.Stop();
            _tooltipRestoreTimer = null;
            _trayManager?.Dispose();
        }
        else
        {
            _trayIcon?.IsVisible = false;
            LocalizationService.Instance.LanguageChanged -= OnTrayLanguageChanged;
        }

        PropertyChanged -= OnWindowPropertyChanged;
        Deactivated -= OnWindowDeactivated;
        Activated -= OnWindowActivated;

        CopyHintService.Instance.HintRequested -= OnCopyHintRequested;
        _copyHintCts?.Cancel();
        _copyHintCts?.Dispose();

        if (_copyHintOverlay != null)
        {
            _copyHintOverlay.IsVisible = false;
            _copyHintOverlay.Opacity = 0;
        }

        base.OnClosed(e);
    }

    #endregion
}