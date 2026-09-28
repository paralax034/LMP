using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.UI.Features.Shell;

public partial class MainWindow
{
    private const uint WS_CAPTION = 0x00C00000;

    private Button? _minimizeButton;
    private Button? _maximizeButton;
    private Button? _closeButton;
    private Border? _dragArea;

    private void InitializeChrome()
    {
        _minimizeButton = this.FindControl<Button>("MinimizeButton");
        _maximizeButton = this.FindControl<Button>("MaximizeButton");
        _closeButton = this.FindControl<Button>("CloseButton");
        _dragArea = this.FindControl<Border>("DragArea");

        _minimizeButton?.Click += (_, _) => HandleMinimizeClick();
        _maximizeButton?.Click += (_, _) => ToggleMaximize();
        _closeButton?.Click += (_, _) => Close();

        var titleBar = this.FindControl<Grid>("TitleBar");
        titleBar?.PointerPressed += OnTitleBarPointerPressed;
        _dragArea?.DoubleTapped += (_, _) => ToggleMaximize();

        ConfigureWin32WindowStyles();
    }

    private void ConfigureWin32WindowStyles()
    {
        if (OperatingSystem.IsWindows())
        {
            Win32Properties.AddWindowStylesCallback(this, static (style, exStyle) =>
                (style | WS_CAPTION, exStyle));
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
}