using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace LMP.UI.Features.Settings.Controls;

/// <summary>
/// Popup content for color selection with RGB sliders, preset swatches, and HEX input.
/// Uses native <see cref="AvaloniaProperty"/> bindings for zero-reflection compiled binding support.
/// </summary>
public partial class ColorPickerPopup : UserControl
{
    #region Events

    public event EventHandler<Color>? ColorConfirmed;
    public event EventHandler? ColorCancelled;

    #endregion

    #region Styled Properties

    public static readonly StyledProperty<Color> SelectedColorProperty =
        AvaloniaProperty.Register<ColorPickerPopup, Color>(nameof(SelectedColor), Colors.White);

    public static readonly StyledProperty<byte> RedValueProperty =
        AvaloniaProperty.Register<ColorPickerPopup, byte>(nameof(RedValue));

    public static readonly StyledProperty<byte> GreenValueProperty =
        AvaloniaProperty.Register<ColorPickerPopup, byte>(nameof(GreenValue));

    public static readonly StyledProperty<byte> BlueValueProperty =
        AvaloniaProperty.Register<ColorPickerPopup, byte>(nameof(BlueValue));

    public static readonly StyledProperty<string> HexInputProperty =
        AvaloniaProperty.Register<ColorPickerPopup, string>(nameof(HexInput), "#FFFFFF");

    public Color SelectedColor
    {
        get => GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    public byte RedValue
    {
        get => GetValue(RedValueProperty);
        set => SetValue(RedValueProperty, value);
    }

    public byte GreenValue
    {
        get => GetValue(GreenValueProperty);
        set => SetValue(GreenValueProperty, value);
    }

    public byte BlueValue
    {
        get => GetValue(BlueValueProperty);
        set => SetValue(BlueValueProperty, value);
    }

    public string HexInput
    {
        get => GetValue(HexInputProperty);
        set => SetValue(HexInputProperty, value);
    }

    public LocalizationService L => LocalizationService.Instance;

    public SolidColorBrush PreviewBrush { get; } = new(Colors.White);

    public IReadOnlyList<ColorPreset> PresetColors => CachedPresets;

    #endregion

    private static readonly IReadOnlyList<ColorPreset> CachedPresets = GeneratePresets();
    private bool _isUpdating;

    static ColorPickerPopup()
    {
        SelectedColorProperty.Changed.AddClassHandler<ColorPickerPopup>(static (sender, _) =>
        {
            sender.SyncFromSelectedColor();
        });

        RedValueProperty.Changed.AddClassHandler<ColorPickerPopup>(static (sender, _) =>
        {
            sender.UpdateColorFromRgb();
        });

        GreenValueProperty.Changed.AddClassHandler<ColorPickerPopup>(static (sender, _) =>
        {
            sender.UpdateColorFromRgb();
        });

        BlueValueProperty.Changed.AddClassHandler<ColorPickerPopup>(static (sender, _) =>
        {
            sender.UpdateColorFromRgb();
        });

        HexInputProperty.Changed.AddClassHandler<ColorPickerPopup>(static (sender, _) =>
        {
            sender.UpdateColorFromHex();
        });
    }

    public ColorPickerPopup()
    {
        InitializeComponent();
        SyncFromSelectedColor();
    }

    private void SyncFromSelectedColor()
    {
        var color = SelectedColor;
        PreviewBrush.Color = color;

        if (_isUpdating) return;
        _isUpdating = true;
        try
        {
            RedValue = color.R;
            GreenValue = color.G;
            BlueValue = color.B;
            HexInput = ThemeManagerService.FormatHexColor(color);
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private void UpdateColorFromRgb()
    {
        if (_isUpdating) return;
        _isUpdating = true;
        try
        {
            var color = Color.FromRgb(RedValue, GreenValue, BlueValue);
            PreviewBrush.Color = color;
            SelectedColor = color;
            HexInput = ThemeManagerService.FormatHexColor(color);
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private void UpdateColorFromHex()
    {
        if (_isUpdating) return;
        var input = HexInput;
        if (string.IsNullOrWhiteSpace(input)) return;

        if (TryParseHex(input, out var color))
        {
            _isUpdating = true;
            try
            {
                PreviewBrush.Color = color;
                SelectedColor = color;
                RedValue = color.R;
                GreenValue = color.G;
                BlueValue = color.B;
            }
            finally
            {
                _isUpdating = false;
            }
        }
    }

    #region Event Handlers

    private void OnPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Color color })
        {
            SelectedColor = color;
        }
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        ColorConfirmed?.Invoke(this, SelectedColor);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        ColorCancelled?.Invoke(this, EventArgs.Empty);
    }

    #endregion

    #region Helpers

    private static bool TryParseHex(string hex, out Color color)
    {
        color = Colors.White;
        try
        {
            var trimmed = hex.Trim();
            if (!trimmed.StartsWith('#'))
                trimmed = "#" + trimmed;

            if (!OnlyHEXRegex().IsMatch(trimmed))
                return false;

            color = Color.Parse(trimmed);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static List<ColorPreset> GeneratePresets() =>
    [
        new("#8A2BE2"),
        new("#9B59B6"),
        new("#673AB7"),
        new("#B39DDB"),
        new("#00B4D8"),
        new("#5B9BD5"),
        new("#1E3A5F"),
        new("#00BCD4"),
        new("#1DB954"),
        new("#2ECC71"),
        new("#009688"),
        new("#4ECB71"),
        new("#FF6B35"),
        new("#FF6B6B"),
        new("#FFB86C"),
        new("#FF69B4"),
        new("#FFFFFF"),
        new("#B3B3B3"),
        new("#404040"),
        new("#000000"),
        new("#0F0B15"),
        new("#121212"),
        new("#001219"),
        new("#282A36")
    ];

    [GeneratedRegex(@"^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$")]
    private static partial Regex OnlyHEXRegex();

    #endregion
}

/// <summary>
/// Color preset for quick selection with an immutable cached brush.
/// </summary>
public sealed class ColorPreset
{
    public string Hex { get; }
    public Color Color { get; }
    public IBrush Brush { get; }

    public ColorPreset(string hex)
    {
        Hex = hex;
        Color = Color.Parse(hex);
        Brush = new ImmutableSolidColorBrush(Color);
    }
}