using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LMP.UI.Controls;

public partial class FilterBar : UserControl
{
    // FILTER TEXT
    public static readonly StyledProperty<string?> FilterTextProperty =
        AvaloniaProperty.Register<FilterBar, string?>(
            nameof(FilterText),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public string? FilterText
    {
        get => GetValue(FilterTextProperty);
        set => SetValue(FilterTextProperty, value);
    }

    // WATERMARK
    public static readonly StyledProperty<string?> WatermarkProperty =
        AvaloniaProperty.Register<FilterBar, string?>(nameof(Watermark), "Filter...");

    public string? Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    // COMPACT MODE
    public static readonly StyledProperty<bool> CompactProperty =
        AvaloniaProperty.Register<FilterBar, bool>(nameof(Compact), false);

    public bool Compact
    {
        get => GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    /// <summary>
    /// Ключ ресурса иконки из Icons.axaml.
    /// По умолчанию — поиск (Icon.Search).
    /// Смена ключа автоматически обновляет <see cref="IconData"/> через PropertyChanged.
    /// </summary>
    public static readonly StyledProperty<string> IconKeyProperty =
        AvaloniaProperty.Register<FilterBar, string>(nameof(IconKey), "Icon.Search");

    public string IconKey
    {
        get => GetValue(IconKeyProperty);
        set => SetValue(IconKeyProperty, value);
    }

    // COMPUTED (Direct Properties для уведомления UI)

    public static readonly DirectProperty<FilterBar, double> BarHeightProperty =
        AvaloniaProperty.RegisterDirect<FilterBar, double>(nameof(BarHeight), o => o.BarHeight);

    public double BarHeight => Compact ? 32 : 36;

    public static readonly DirectProperty<FilterBar, CornerRadius> BarCornerRadiusProperty =
        AvaloniaProperty.RegisterDirect<FilterBar, CornerRadius>(nameof(BarCornerRadius), o => o.BarCornerRadius);

    public CornerRadius BarCornerRadius => Compact ? new CornerRadius(6) : new CornerRadius(8);

    public static readonly DirectProperty<FilterBar, double> TextFontSizeProperty =
        AvaloniaProperty.RegisterDirect<FilterBar, double>(nameof(TextFontSize), o => o.TextFontSize);

    public double TextFontSize => Compact ? 12 : 13;

    public static readonly DirectProperty<FilterBar, double> IconSizeProperty =
        AvaloniaProperty.RegisterDirect<FilterBar, double>(nameof(IconSize), o => o.IconSize);

    public double IconSize => Compact ? 14 : 16;

    /// <summary>
    /// Геометрия иконки, вычисленная из <see cref="IconKey"/> через Application.Resources.
    /// Zero-alloc: StreamGeometry создаётся один раз при парсинге XAML.
    /// </summary>
    public static readonly DirectProperty<FilterBar, Geometry?> IconDataProperty =
        AvaloniaProperty.RegisterDirect<FilterBar, Geometry?>(nameof(IconData), o => o.IconData);

    public Geometry? IconData => ResolveGeometry(IconKey);

    public FilterBar()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == CompactProperty)
        {
            RaisePropertyChanged(BarHeightProperty, default, BarHeight);
            RaisePropertyChanged(BarCornerRadiusProperty, default, BarCornerRadius);
            RaisePropertyChanged(TextFontSizeProperty, default, TextFontSize);
            RaisePropertyChanged(IconSizeProperty, default, IconSize);
        }
        else if (change.Property == IconKeyProperty)
        {
            RaisePropertyChanged(IconDataProperty, default, IconData);
        }
    }

    /// <summary>
    /// Ищет StreamGeometry в Application.Resources по строковому ключу.
    /// Вызывается только при смене IconKey — не на каждый layout pass.
    /// </summary>
    private static Geometry? ResolveGeometry(string key)
    {
        var app = Application.Current;
        if (app is null) return null;

        return app.Resources.TryGetResource(key, app.ActualThemeVariant, out var res)
            && res is Geometry geo ? geo : null;
    }
}