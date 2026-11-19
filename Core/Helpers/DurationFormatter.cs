using System.Globalization;

namespace LMP.Core.Helpers;

/// <summary>
/// Centralized, Native AOT-safe duration formatting helpers.
/// Preserves total hours across multi-day durations instead of wrapping at 24 hours.
/// </summary>
public static class DurationFormatter
{
    /// <summary>
    /// Formats a duration as <c>H:mm:ss</c> (when &gt;= 1 hour) or <c>m:ss</c>.
    /// </summary>
    public static string FormatShort(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        int totalHours = (int)duration.TotalHours;
        return totalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{totalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}")
            : string.Create(CultureInfo.InvariantCulture, $"{duration.Minutes}:{duration.Seconds:D2}");
    }

    /// <summary>
    /// Formats a duration as <c>H:mm:ss</c> (when &gt;= 1 hour) or <c>mm:ss</c>.
    /// </summary>
    public static string FormatZeroPadded(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        int totalHours = (int)duration.TotalHours;
        return totalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{totalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}")
            : string.Create(CultureInfo.InvariantCulture, $"{duration.Minutes:D2}:{duration.Seconds:D2}");
    }
}