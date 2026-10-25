using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using LMP.UI.Features.Player;
using LMP.UI.Features.Settings.ViewModels;

namespace LMP.UI.Features.Settings.Pages;

public partial class AudioPage : UserControl
{
    private DispatcherTimer? _presetsHoverCloseTimer;
    private bool _isPointerInTrigger;
    private bool _isPointerInPopup;

    public AudioPage()
    {
        InitializeComponent();
        SettingsEqCurveControl.BandsChanged += bands =>
        {
            if (DataContext is AudioSettingsViewModel vm)
                vm.ApplyCustomBandsFromUi(bands);
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is AudioSettingsViewModel vm)
            vm.LoadSettings();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _presetsHoverCloseTimer?.Stop();
        SettingsPresetsPopup.IsOpen = false;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnEqHeaderButtonClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control button)
        {
            ToolTip.SetIsOpen(button, false);
            Dispatcher.UIThread.Post(() =>
            {
                if (button.IsPointerOver)
                    ToolTip.SetIsOpen(button, true);
            }, DispatcherPriority.Input);
        }
    }

    private void OnPresetsTriggerPointerEntered(object? sender, PointerEventArgs e)
    {
        _isPointerInTrigger = true;
        _presetsHoverCloseTimer?.Stop();
        ToolTip.SetIsOpen(SettingsPresetsButton, false);
        SettingsPresetsPopup.IsOpen = true;
    }

    private void OnPresetsTriggerPointerExited(object? sender, PointerEventArgs e)
    {
        _isPointerInTrigger = false;
        SchedulePopupClose();
    }

    private void OnPresetsPopupPointerEntered(object? sender, PointerEventArgs e)
    {
        _isPointerInPopup = true;
        _presetsHoverCloseTimer?.Stop();
    }

    private void OnPresetsPopupPointerExited(object? sender, PointerEventArgs e)
    {
        _isPointerInPopup = false;
        SchedulePopupClose();
    }

    private void SchedulePopupClose()
    {
        _presetsHoverCloseTimer ??= new DispatcherTimer(
                TimeSpan.FromMilliseconds(220),
                DispatcherPriority.Normal,
                (_, _) =>
                {
                    _presetsHoverCloseTimer?.Stop();
                    if (!_isPointerInTrigger && !_isPointerInPopup)
                    {
                        SettingsPresetsPopup.IsOpen = false;
                    }
                });
        _presetsHoverCloseTimer.Stop();
        _presetsHoverCloseTimer.Start();
    }

    private void OnNewPresetTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is AudioSettingsViewModel vm)
        {
            vm.SaveInlinePresetCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnPresetItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Right
            && sender is Control { DataContext: EqualizerPresetChipItem chip }
            && !chip.IsBuiltIn
            && DataContext is AudioSettingsViewModel vm)
        {
            _isPointerInPopup = true;
            _presetsHoverCloseTimer?.Stop();
            vm.DeleteEqualizerPresetCommand.Execute(chip);
            e.Handled = true;
        }
    }

    private void OnRenamePresetTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Control { DataContext: EqualizerPresetChipItem chip }) return;

        if (e.Key == Key.Enter && DataContext is AudioSettingsViewModel vm)
        {
            vm.CommitRenamePresetCommand.Execute(chip);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            chip.IsRenaming = false;
            e.Handled = true;
        }
    }

    private void OnRenamePresetTextBoxLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { DataContext: EqualizerPresetChipItem { IsRenaming: true } chip }
            && DataContext is AudioSettingsViewModel vm)
        {
            vm.CommitRenamePresetCommand.Execute(chip);
        }
    }
}