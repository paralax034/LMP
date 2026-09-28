using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.UI.Features.Shell;

public partial class MainWindow
{
    private const int TooltipRestoreDelayMs = 3000;

    private TrayManager? _trayManager;
    private Avalonia.Threading.DispatcherTimer? _tooltipRestoreTimer;

    private TrayIcon? _trayIcon;
    private NativeMenuItem? _playPauseItem;
    private NativeMenuItem? _nextItem;
    private NativeMenuItem? _prevItem;
    private NativeMenuItem? _repeatItem;
    private NativeMenuItem? _showItem;
    private NativeMenuItem? _queueItem;
    private NativeMenuItem? _cleanMemItem;
    private NativeMenuItem? _exitItem;

    private long _lastToggleTime;

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

                try { File.Delete(tempPath); } catch { }

                if (hIcon != IntPtr.Zero) return hIcon;
            }
        }
        catch { }

        return LoadIconW(IntPtr.Zero, 32512);
    }

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
        catch { }

        if (Icon != null) _trayIcon.Icon = Icon;
    }

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

    private void MinimizeToTray()
    {
        _isInTray = true;
        _isMinimized = false;
        _isDeactivated = false;
        CancelDeactivateSuspend();
        ApplySuspendLevel();

        Hide();

        if (!OperatingSystem.IsWindows())
        {
            UpdateShowHideItemText();
        }

        Log.Info("[Window] Minimized to tray");
    }

    private void RestoreFromTray()
    {
        _isInTray = false;
        _isMinimized = false;
        _isDeactivated = false;
        _isRestoringFromTray = true;
        CancelDeactivateSuspend();
        ApplySuspendLevel();

        WindowState = WindowState.Normal;
        Show();
        Activate();

        _playerControl?.ForceSync();

        _ = ClearRestoreGuardFallbackAsync();

        if (!OperatingSystem.IsWindows())
        {
            UpdateShowHideItemText();
        }

        Log.Info("[Window] Restored from tray");
    }

    private async Task ClearRestoreGuardFallbackAsync()
    {
        await Task.Delay(1500);

        if (_isRestoringFromTray)
        {
            _isRestoringFromTray = false;
            Log.Debug("[Window] Restore guard cleared by fallback");
        }
    }

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

    private void OnTrayGoToQueue()
    {
        RestoreFromTray();

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is MainWindowViewModel vm)
                vm.NavigateCommand.Execute("Queue");
        });
    }

    private static void OnTrayClearMemory()
    {
        MemoryCleanupHelper.PerformCleanup(aggressive: true);
    }

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

    private void CleanupTray()
    {
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
    }
}