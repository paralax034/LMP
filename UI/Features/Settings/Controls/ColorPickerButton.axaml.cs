using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace LMP.UI.Features.Settings.Controls;

/// <summary>
/// Custom color picker button with a lazily instantiated in-tree popup dialog.
/// Avoids upfront instantiation of popup controls during page load and supports
/// frame-throttled live color preview while dragging RGB sliders.
/// </summary>
public partial class ColorPickerButton : UserControl
{
    private const int LivePreviewThrottleMs = 33;

    #region Styled Properties

    /// <summary>
    /// Selected color.
    /// </summary>
    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<ColorPickerButton, Color>(nameof(Color), Colors.White);

    public static readonly StyledProperty<string> HexTextProperty =
        AvaloniaProperty.Register<ColorPickerButton, string>(nameof(HexText), "#FFFFFF");

    public Color Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public string HexText
    {
        get => GetValue(HexTextProperty);
        private set => SetValue(HexTextProperty, value);
    }

    public SolidColorBrush SwatchBrush { get; } = new(Colors.White);

    #endregion

    private ColorPickerPopup? _pickerContent;
    private Color _initialColor;
    private Color _pendingLiveColor;
    private bool _hasPendingLiveColor;
    private bool _isSyncingPopup;
    private DispatcherTimer? _liveThrottleTimer;

    static ColorPickerButton()
    {
        ColorProperty.Changed.AddClassHandler<ColorPickerButton>(static (sender, e) =>
        {
            if (e.NewValue is Color c)
            {
                sender.SwatchBrush.Color = c;
                sender.HexText = ThemeManagerService.FormatHexColor(c);
            }
        });
    }

    public ColorPickerButton()
    {
        InitializeComponent();
        SwatchBrush.Color = Color;
        HexText = ThemeManagerService.FormatHexColor(Color);
        ColorPopup.Closed += OnPopupClosed;
    }

    private void OnButtonClick(object? sender, RoutedEventArgs e)
    {
        _initialColor = Color;
        _isSyncingPopup = true;
        try
        {
            if (_pickerContent is null)
            {
                _pickerContent = new ColorPickerPopup();
                _pickerContent.PropertyChanged += OnPickerPropertyChanged;
                _pickerContent.ColorConfirmed += OnColorConfirmed;
                _pickerContent.ColorCancelled += OnColorCancelled;
                ColorPopup.Child = _pickerContent;
            }

            _pickerContent.SelectedColor = Color;
            ColorPopup.IsOpen = true;
            _pickerContent.SelectedColor = Color;
        }
        finally
        {
            _isSyncingPopup = false;
        }
    }

    private void OnPickerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_isSyncingPopup || !ColorPopup.IsOpen) return;
        if (e.Property == ColorPickerPopup.SelectedColorProperty && e.NewValue is Color liveColor)
        {
            if (liveColor == Color && !_hasPendingLiveColor)
                return;

            SwatchBrush.Color = liveColor;
            HexText = ThemeManagerService.FormatHexColor(liveColor);
            _pendingLiveColor = liveColor;
            _hasPendingLiveColor = true;

            if (_liveThrottleTimer is null)
            {
                _liveThrottleTimer = new DispatcherTimer(
                    TimeSpan.FromMilliseconds(LivePreviewThrottleMs),
                    DispatcherPriority.Background,
                    OnLiveThrottleTick);
            }

            if (!_liveThrottleTimer.IsEnabled)
            {
                _liveThrottleTimer.Start();
            }
        }
    }

    private void OnLiveThrottleTick(object? sender, EventArgs e)
    {
        if (!_hasPendingLiveColor)
        {
            _liveThrottleTimer?.Stop();
            return;
        }

        _hasPendingLiveColor = false;
        _liveThrottleTimer?.Stop();
        Color = _pendingLiveColor;
    }

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        _liveThrottleTimer?.Stop();
        if (_hasPendingLiveColor)
        {
            _hasPendingLiveColor = false;
            Color = _pendingLiveColor;
            _initialColor = _pendingLiveColor;
        }
    }

    private void OnColorConfirmed(object? sender, Color color)
    {
        _liveThrottleTimer?.Stop();
        _hasPendingLiveColor = false;
        SwatchBrush.Color = color;
        HexText = ThemeManagerService.FormatHexColor(color);
        Color = color;
        _initialColor = color;
        ColorPopup.IsOpen = false;
    }

    private void OnColorCancelled(object? sender, EventArgs e)
    {
        _liveThrottleTimer?.Stop();
        _hasPendingLiveColor = false;
        SwatchBrush.Color = _initialColor;
        HexText = ThemeManagerService.FormatHexColor(_initialColor);
        Color = _initialColor;
        ColorPopup.IsOpen = false;
    }
}