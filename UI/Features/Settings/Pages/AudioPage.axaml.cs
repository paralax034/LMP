using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using LMP.UI.Controls;
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

    private AudioSettingsViewModel? _subscribedVm;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is AudioSettingsViewModel vm)
        {
            if (!ReferenceEquals(_subscribedVm, vm))
            {
                if (_subscribedVm != null)
                    _subscribedVm.PresetInputFocusRequested -= FocusNewPresetTextBox;

                _subscribedVm = vm;
                _subscribedVm.PresetInputFocusRequested += FocusNewPresetTextBox;
            }
            vm.LoadSettings();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _presetsHoverCloseTimer?.Stop();
        SettingsPresetsPopup.IsOpen = false;
        if (_subscribedVm != null)
        {
            _subscribedVm.CancelPresetRename();
            _subscribedVm.PresetInputFocusRequested -= FocusNewPresetTextBox;
            _subscribedVm = null;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private void FocusNewPresetTextBox()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (SettingsPresetsPopup.IsOpen)
            {
                NewPresetTextBox.Focus();
                NewPresetTextBox.SelectAll();
            }
        }, DispatcherPriority.Input);
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

    private void OnPresetsTriggerButtonClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _presetsHoverCloseTimer?.Stop();
        ToolTip.SetIsOpen(SettingsPresetsButton, false);
        SettingsPresetsPopup.IsOpen = !SettingsPresetsPopup.IsOpen;
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

    private bool IsPresetsPopupEditing() => NewPresetTextBox.IsFocused;

    private void SchedulePopupClose()
    {
        _presetsHoverCloseTimer ??= new DispatcherTimer(
                TimeSpan.FromMilliseconds(220),
                DispatcherPriority.Normal,
                (_, _) =>
                {
                    _presetsHoverCloseTimer?.Stop();
                    if (!_isPointerInTrigger && !_isPointerInPopup && !IsPresetsPopupEditing())
                    {
                        SettingsPresetsPopup.IsOpen = false;
                        _subscribedVm?.CancelPresetRename();
                    }
                });
        _presetsHoverCloseTimer.Stop();
        _presetsHoverCloseTimer.Start();
    }

    private void OnNewPresetTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is AudioSettingsViewModel vm)
        {
            if (e.Key == Key.Enter)
            {
                vm.SaveInlinePresetCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                vm.CancelPresetRename();
                SettingsPresetsButton.Focus();
                e.Handled = true;
            }
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
}