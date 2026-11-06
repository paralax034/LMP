using Avalonia.Data.Converters;
using Avalonia.Media;
using System.Globalization;
using Avalonia.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia;

namespace LMP.UI.Converters;

/// <summary>
/// Converts bool → StreamGeometry for PathIcon in virtualized lists.
/// Prevents SVG path re-parsing when recycling ItemsRepeater containers.
/// 
/// <para>ConverterParameter format: "TrueKey|FalseKey" 
/// (keys from Icons.axaml without the "Icon." prefix).</para>
/// 
/// <para>Example: ConverterParameter='Pause|Play' → Icon.Pause / Icon.Play</para>
/// </summary>
public sealed class BoolToGeometryConverter : IValueConverter
{
    public static readonly BoolToGeometryConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool boolVal || parameter is not string param)
            return null;

        var span = param.AsSpan();
        int pipe = span.IndexOf('|');
        if (pipe < 0) return null;

        var key = boolVal
            ? $"Icon.{span[..pipe]}"
            : $"Icon.{span[(pipe + 1)..]}";

        return Application.Current?.Resources.TryGetResource(key, null, out var resource) == true
            ? resource
            : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class TimeSpanToStringConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is TimeSpan ts)
            return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
        return "0:00";
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public sealed class BoolToOpacityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (value is bool b && b) ? 1.0 : 0.5;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool invert = parameter?.ToString() == "invert";
        bool isNull = value is null || (value is string s && string.IsNullOrEmpty(s));
        return invert ? isNull : !isNull;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b ? !b : value;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b ? !b : value;
}

/// <summary>
/// Converts volume → geometry icon resource key for PathIcon.
/// Returns a string formatted as "Icon.*" for Application.Resources lookup.
/// </summary>
public sealed class VolumeToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            float vol when vol == 0f => "Icon.VolumeMute",
            float vol when vol < 0.33f => "Icon.VolumeLow",
            float vol when vol < 0.66f => "Icon.VolumeMedium",
            _ => "Icon.VolumeHigh"
        };

        return Application.Current?.Resources.TryGetResource(key, null, out var res) == true
            ? res
            : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts bool to one of two values based on the "TrueValue|FalseValue" parameter.
/// Supports: IBrush (by resource key), string.
/// MaterialIconKind branch removed — use BoolToGeometryConverter for PathIcon.
/// </summary>
public sealed class BoolToSelectorConverter : IValueConverter
{
    private static readonly Dictionary<string, (string TrueVal, string FalseVal)> _pairCache = [];

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool b || parameter is not string key) return null;

        if (!_pairCache.TryGetValue(key, out var pair))
        {
            var sep = key.IndexOf('|');
            pair = sep >= 0 ? (key[..sep], key[(sep + 1)..]) : (key, key);
            _pairCache[key] = pair;
        }

        var chosen = b ? pair.TrueVal : pair.FalseVal;

        // IBrush by resource key
        if (Application.Current?.TryFindResource(chosen, out var res) == true)
        {
            if (res is IBrush brush) return brush;
            if (res is Color color) return new SolidColorBrush(color);
        }

        // Attempt to parse as color
        try { return SolidColorBrush.Parse(chosen); }
        catch { return chosen; }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public sealed class ToUpperConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string str)
        {
            return str.ToUpper(culture);
        }
        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public sealed class BitmapAssetValueConverter : IValueConverter
{
    public static readonly BitmapAssetValueConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string uriStr && !string.IsNullOrEmpty(uriStr))
        {
            // 1. HTTP/HTTPS — skip (handled by AsyncImageLoader)
            if (uriStr.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return null;

            try
            {
                // 2. avares:// — Avalonia resources
                if (uriStr.StartsWith("avares://"))
                {
                    var uri = new Uri(uriStr);
                    if (AssetLoader.Exists(uri))
                    {
                        using var stream = AssetLoader.Open(uri);
                        return new Bitmap(stream);
                    }
                    return null;
                }

                // 3. file:// URI — convert to local path
                if (uriStr.StartsWith(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
                {
                    if (Uri.TryCreate(uriStr, UriKind.Absolute, out var fileUri))
                    {
                        uriStr = fileUri.LocalPath;
                    }
                }

                // 4. Local file path
                if (Path.IsPathRooted(uriStr) && File.Exists(uriStr))
                {
                    // IMPORTANT: Use FileStream with FileShare.Read to prevent file locking
                    using var stream = new FileStream(uriStr, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return new Bitmap(stream);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[BitmapConverter] Failed to load image '{uriStr}': {ex.Message}");
                return null;
            }
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public sealed class StringStartsWithConverter : IValueConverter
{
    public static readonly StringStartsWithConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && parameter is string prefix)
        {
            bool negate = false;
            if (prefix.StartsWith('!'))
            {
                negate = true;
                prefix = prefix[1..];
            }

            bool result = s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            return negate ? !result : result;
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public sealed class WebUrlConverter : IValueConverter
{
    public static readonly WebUrlConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string url && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public sealed class ProgressToWidthConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count >= 2 &&
            values[0] is double progress &&
            values[1] is double containerWidth)
        {
            return Math.Max(0, containerWidth * progress);
        }
        return 0.0;
    }
}

public sealed class GreaterThanConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Checks whether value is greater than parameter
        if (value is double d && double.TryParse(parameter?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double threshold))
        {
            return d > threshold;
        }
        if (value is int i && int.TryParse(parameter?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out int iThreshold))
        {
            return i > iThreshold;
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Compares an Enum value with a parameter. Returns true if they match.
/// Used for RadioButton GroupBinding.
/// </summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return false;

        string checkValue = value.ToString() ?? "";
        string targetValue = parameter.ToString() ?? "";

        return checkValue.Equals(targetValue, StringComparison.InvariantCultureIgnoreCase);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // OneWay binding is usually sufficient for RadioButton + Command, 
        // but if TwoWay is required, Enum.Parse can be returned here
        return value is bool b && b ? parameter : BindingOperations.DoNothing;
    }
}

/// <summary>
/// Converts bool to FontWeight (true = Bold, false = Normal)
/// </summary>
public sealed class BoolToFontWeightConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b)
            return FontWeight.Bold;
        return FontWeight.Normal;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts NotificationSeverity to Color from the current theme.
/// Error → SystemError, Warning → SystemWarnOrange, Info → SystemInfoBlue, Success → Accent.
/// </summary>
public sealed class SeverityToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var severity = value switch
        {
            NotificationSeverity s => s,
            int i => (NotificationSeverity)i,
            _ => NotificationSeverity.Info
        };

        var resourceKey = severity switch
        {
            NotificationSeverity.Error => "SystemError",
            NotificationSeverity.Warning => "SystemWarnOrange",
            NotificationSeverity.Info => "SystemInfoBlue",
            NotificationSeverity.Success => "Accent",
            _ => "TextMuted"
        };

        if (Application.Current?.Resources.TryGetResource(resourceKey, null, out var resource) == true)
        {
            if (resource is Color color) return color;
            if (resource is SolidColorBrush brush) return brush.Color;
        }

        // Fallback
        return severity switch
        {
            NotificationSeverity.Error => Color.Parse("#FF5555"),
            NotificationSeverity.Warning => Color.Parse("#FFB86C"),
            NotificationSeverity.Info => Color.Parse("#8BE9FD"),
            NotificationSeverity.Success => Color.Parse("#50C878"),
            _ => Color.Parse("#6272A4")
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}