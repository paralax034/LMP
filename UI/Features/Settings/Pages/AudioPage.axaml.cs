using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LMP.UI.Controls;
using LMP.UI.Features.Settings.ViewModels;

namespace LMP.UI.Features.Settings.Pages;

public partial class AudioPage : UserControl
{
    public AudioPage()
    {
        InitializeComponent();
        SettingsEqCurveControl.BandsChanged += bands =>
        {
            if (DataContext is AudioSettingsViewModel vm)
                vm.Equalizer.ApplyCustomBandsFromUi(bands);
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is AudioSettingsViewModel vm)
        {
            vm.LoadSettings();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SettingsPresetsDropDown.ClosePopup();
        if (DataContext is AudioSettingsViewModel vm)
        {
            vm.Equalizer.FlushPendingSave();
        }
        base.OnDetachedFromVisualTree(e);
    }

    private void OnEqHeaderButtonClicked(object? sender, RoutedEventArgs e) =>
        EqualizerPresetsDropDown.RefreshHeaderButtonToolTip(sender);
}