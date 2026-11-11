using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace LMP.UI.Controls;

/// <summary>
/// Reusable equalizer preset selector dropdown with hover-open/close delay,
/// inline preset creation/renaming, and right-click user preset deletion.
/// </summary>
public partial class EqualizerPresetsDropDown : UserControl
{
    public static readonly StyledProperty<bool> CompactProperty =
        AvaloniaProperty.Register<EqualizerPresetsDropDown, bool>(nameof(Compact));

    /// <summary>
    /// Gets or sets whether the control uses compact sizing for the PlayerBar flyout.
    /// </summary>
    public bool Compact
    {
        get => GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    private DispatcherTimer? _presetsHoverCloseTimer;
    private bool _isPointerInTrigger;
    private bool _isPointerInPopup;
    private EqualizerScopeViewModel? _subscribedVm;

    /// <summary>
    /// Initializes a new instance of the <see cref="EqualizerPresetsDropDown"/> class.
    /// </summary>
    public EqualizerPresetsDropDown()
    {
        InitializeComponent();
        ApplyCompactMetrics(Compact);
    }

    /// <summary>
    /// Forces an immediate tooltip refresh on an equalizer header icon button after click.
    /// </summary>
    public static void RefreshHeaderButtonToolTip(object? sender)
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

    /// <summary>
    /// Closes the preset popup and cancels any in-progress inline preset rename.
    /// </summary>
    public void ClosePopup()
    {
        _presetsHoverCloseTimer?.Stop();
        _isPointerInTrigger = false;
        _isPointerInPopup = false;
        PresetsPopup.IsOpen = false;
        _subscribedVm?.CancelPresetRename();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CompactProperty)
        {
            ApplyCompactMetrics(Compact);
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        BindViewModel(DataContext as EqualizerScopeViewModel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        BindViewModel(DataContext as EqualizerScopeViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ClosePopup();
        BindViewModel(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void BindViewModel(EqualizerScopeViewModel? vm)
    {
        if (ReferenceEquals(_subscribedVm, vm))
            return;

        if (_subscribedVm != null)
            _subscribedVm.PresetInputFocusRequested -= FocusNewPresetTextBox;

        _subscribedVm = vm;

        if (_subscribedVm != null)
            _subscribedVm.PresetInputFocusRequested += FocusNewPresetTextBox;
    }

    private void ApplyCompactMetrics(bool compact)
    {
        PresetsTriggerButton.Padding = compact ? new Thickness(9, 4) : new Thickness(10, 5);
        ActivePresetTitleText.FontSize = compact ? 11.0 : 12.0;
        ActivePresetTitleText.MaxWidth = compact ? 120.0 : 140.0;
        ChevronIcon.Width = compact ? 10.0 : 11.0;
        ChevronIcon.Height = compact ? 10.0 : 11.0;
        PresetsPopup.ShouldUseOverlayLayer = !compact;
    }

    private void FocusNewPresetTextBox()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (PresetsPopup.IsOpen)
            {
                NewPresetTextBox.Focus();
                NewPresetTextBox.SelectAll();
            }
        }, DispatcherPriority.Input);
    }

    private void OnPresetsTriggerButtonClicked(object? sender, RoutedEventArgs e)
    {
        _presetsHoverCloseTimer?.Stop();
        ToolTip.SetIsOpen(PresetsTriggerButton, false);
        PresetsPopup.IsOpen = !PresetsPopup.IsOpen;
    }

    private void OnPresetsTriggerPointerEntered(object? sender, PointerEventArgs e)
    {
        _isPointerInTrigger = true;
        _presetsHoverCloseTimer?.Stop();
        ToolTip.SetIsOpen(PresetsTriggerButton, false);
        PresetsPopup.IsOpen = true;
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
                    PresetsPopup.IsOpen = false;
                    _subscribedVm?.CancelPresetRename();
                }
            });
        _presetsHoverCloseTimer.Stop();
        _presetsHoverCloseTimer.Start();
    }

    private void OnNewPresetTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (_subscribedVm == null) return;

        if (e.Key == Key.Enter)
        {
            _subscribedVm.SaveInlineEqualizerPresetCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _subscribedVm.CancelPresetRename();
            PresetsTriggerButton.Focus();
            e.Handled = true;
        }
    }

    private void OnPresetItemClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: EqualizerPresetChipItem chip })
        {
            _subscribedVm?.SelectEqualizerPresetCommand.Execute(chip);
        }
    }

    private void OnPresetItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Right
            && sender is Control { DataContext: EqualizerPresetChipItem chip }
            && !chip.IsBuiltIn
            && _subscribedVm != null)
        {
            _isPointerInPopup = true;
            _presetsHoverCloseTimer?.Stop();
            _subscribedVm.DeleteEqualizerPresetCommand.Execute(chip);
            e.Handled = true;
        }
    }
}