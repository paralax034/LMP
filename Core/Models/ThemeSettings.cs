using System.Text.Json.Serialization;

namespace LMP.Core.Models;

/// <summary>
/// Application theme settings.
/// All colors are stored in HEX format (#RRGGBB or #AARRGGBB).
/// </summary>
public sealed class ThemeSettings : IEquatable<ThemeSettings>
{
    /// <summary>Display name of the theme</summary>
    public string Name { get; set; } = "Paralax Purple";

    // BACKGROUNDS

    /// <summary>Main window background</summary>
    public string BgPrimary { get; set; } = "#0F0B15";

    /// <summary>Card and sidebar background</summary>
    public string BgSecondary { get; set; } = "#1A1625";

    /// <summary>Dialog and menu background</summary>
    public string BgElevated { get; set; } = "#252033";

    /// <summary>Separators and borders</summary>
    public string BgHighlight { get; set; } = "#322A45";

    /// <summary>Hover state</summary>
    public string BgHover { get; set; } = "#3E3456";

    // SKELETON / LOADING - Loading colors

    /// <summary>Light skeleton</summary>
    public string BgSkeleton { get; set; } = "#2A2438";

    /// <summary>Dark skeleton</summary>
    public string BgSkeletonDeep { get; set; } = "#15121C";

    /// <summary>Overlay (semi-transparent)</summary>
    public string BgOverlay { get; set; } = "#CC0A080F";

    // ACCENT - Brand accent colors

    /// <summary>Primary accent color (buttons, links)</summary>
    public string AccentColor { get; set; } = "#8A2BE2";

    /// <summary>Accent on hover</summary>
    public string AccentHover { get; set; } = "#A560F0";

    // SEMANTIC - System colors

    /// <summary>Error color</summary>
    public string SystemError { get; set; } = "#FF5555";

    /// <summary>Error background</summary>
    public string SystemErrorBg { get; set; } = "#331010";

    /// <summary>Info color</summary>
    public string SystemInfoBlue { get; set; } = "#8BE9FD";

    /// <summary>Warning color</summary>
    public string SystemWarnOrange { get; set; } = "#FFB86C";

    // TEXT - Text colors

    /// <summary>Primary text</summary>
    public string TextPrimary { get; set; } = "#F8F8F2";

    /// <summary>Secondary text (subtitles)</summary>
    public string TextSecondary { get; set; } = "#BFB6D3";

    /// <summary>Muted text</summary>
    public string TextMuted { get; set; } = "#6272A4";

    /// <summary>Dark text (on light background)</summary>
    public string TextDark { get; set; } = "#0F0B15";

    // SERIALIZATION

    [JsonIgnore]
    public bool IsBuiltIn { get; init; }

    public override string ToString() => Name;

    /// <summary>
    /// Determines equality of two themes by their name.
    /// </summary>
    /// <param name="other">Theme instance to compare.</param>
    /// <returns><c>true</c> if theme names match, case-insensitively.</returns>
    public bool Equals(ThemeSettings? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ThemeSettings);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Name);
}