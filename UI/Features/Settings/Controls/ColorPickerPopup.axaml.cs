using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace LMP.UI.Features.Settings.Controls;

/// <summary>
/// Popup-контент для выбора цвета с RGB-слайдерами и HEX-вводом.
/// </summary>
public partial class ColorPickerPopup : UserControl, INotifyPropertyChanged
{
    #region INotifyPropertyChanged

    public new event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    #endregion

    #region Events

    public event EventHandler<Color>? ColorConfirmed;
    public event EventHandler? ColorCancelled;

    #endregion

    #region Styled Properties

    public static readonly StyledProperty<Color> SelectedColorProperty =
        AvaloniaProperty.Register<ColorPickerPopup, Color>(nameof(SelectedColor), Colors.White);

    public Color SelectedColor
    {
        get => GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    #endregion

    #region Bindable Properties

    private byte _redValue;
    public byte RedValue
    {
        get => _redValue;
        set
        {
            if (_redValue == value) return;
            _redValue = value;
            OnPropertyChanged();
            if (!_isUpdating) UpdateColorFromRgb();
        }
    }

    private byte _greenValue;
    public byte GreenValue
    {
        get => _greenValue;
        set
        {
            if (_greenValue == value) return;
            _greenValue = value;
            OnPropertyChanged();
            if (!_isUpdating) UpdateColorFromRgb();
        }
    }

    private byte _blueValue;
    public byte BlueValue
    {
        get => _blueValue;
        set
        {
            if (_blueValue == value) return;
            _blueValue = value;
            OnPropertyChanged();
            if (!_isUpdating) UpdateColorFromRgb();
        }
    }

    private string _hexInput = "#FFFFFF";
    public string HexInput
    {
        get => _hexInput;
        set
        {
            if (_hexInput == value) return;
            _hexInput = value;
            OnPropertyChanged();
            if (!_isUpdating) UpdateColorFromHex();
        }
    }

    public IReadOnlyList<ColorPreset> PresetColors => CachedPresets;

    #endregion

    private static readonly IReadOnlyList<ColorPreset> CachedPresets = GeneratePresets();
    private bool _isUpdating;

    public ColorPickerPopup()
    {
        InitializeComponent();

        SelectedColorProperty.Changed.AddClassHandler<ColorPickerPopup>(static (sender, _) =>
        {
            sender.SyncFromSelectedColor();
        });
    }

    private void SyncFromSelectedColor()
    {
        if (_isUpdating) return;
        _isUpdating = true;

        var color = SelectedColor;
        _redValue = color.R;
        _greenValue = color.G;
        _blueValue = color.B;
        _hexInput = color.ToString();

        OnPropertyChanged(nameof(RedValue));
        OnPropertyChanged(nameof(GreenValue));
        OnPropertyChanged(nameof(BlueValue));
        OnPropertyChanged(nameof(HexInput));

        _isUpdating = false;
    }

    private void UpdateColorFromRgb()
    {
        _isUpdating = true;

        SelectedColor = Color.FromRgb(_redValue, _greenValue, _blueValue);
        _hexInput = SelectedColor.ToString();
        OnPropertyChanged(nameof(HexInput));

        _isUpdating = false;
    }

    private void UpdateColorFromHex()
    {
        if (string.IsNullOrWhiteSpace(_hexInput)) return;

        if (TryParseHex(_hexInput, out var color))
        {
            _isUpdating = true;

            SelectedColor = color;
            _redValue = color.R;
            _greenValue = color.G;
            _blueValue = color.B;

            OnPropertyChanged(nameof(RedValue));
            OnPropertyChanged(nameof(GreenValue));
            OnPropertyChanged(nameof(BlueValue));

            _isUpdating = false;
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
            if (!hex.StartsWith('#'))
                hex = "#" + hex;

            if (!OnlyHEXRegex().IsMatch(hex))
                return false;

            color = Color.Parse(hex);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static List<ColorPreset> GeneratePresets()
    {
        return
        [
            // Purples
            new("Paralax Purple", "#8A2BE2"),
            new("Violet", "#9B59B6"),
            new("Deep Purple", "#673AB7"),
            new("Lavender", "#B39DDB"),

            // Blues
            new("Electric Blue", "#00B4D8"),
            new("Sky Blue", "#5B9BD5"),
            new("Navy", "#1E3A5F"),
            new("Cyan", "#00BCD4"),

            // Greens
            new("Spotify Green", "#1DB954"),
            new("Emerald", "#2ECC71"),
            new("Teal", "#009688"),
            new("Mint", "#4ECB71"),

            // Warm
            new("Sunset Orange", "#FF6B35"),
            new("Coral", "#FF6B6B"),
            new("Gold", "#FFB86C"),
            new("Pink", "#FF69B4"),

            // Neutrals
            new("White", "#FFFFFF"),
            new("Light Gray", "#B3B3B3"),
            new("Dark Gray", "#404040"),
            new("Pure Black", "#000000"),

            // Backgrounds
            new("Deep Dark", "#0F0B15"),
            new("Spotify Black", "#121212"),
            new("Ocean Dark", "#001219"),
        ];
    }

    [GeneratedRegex(@"^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$")]
    private static partial Regex OnlyHEXRegex();

    #endregion
}

/// <summary>
/// Пресет цвета для быстрого выбора с неизменяемой кэшированной кистью.
/// </summary>
public sealed class ColorPreset
{
    public string Name { get; }
    public string Hex { get; }
    public Color Color { get; }
    public IBrush Brush { get; }

    public ColorPreset(string name, string hex)
    {
        Name = name;
        Hex = hex;
        Color = Color.Parse(hex);
        Brush = new ImmutableSolidColorBrush(Color);
    }
}