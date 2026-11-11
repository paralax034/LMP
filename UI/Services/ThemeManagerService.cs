using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LMP.UI.Services;

/// <summary>
/// Application theme management service.
/// Responsible for loading, saving, and applying themes.
/// </summary>
public sealed class ThemeManagerService
{
    private ThemeSettings? _cachedTheme;
    private ThemeSettings? _appliedFluentTheme;
    private readonly Dictionary<string, Color> _appliedColors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Color> _committedColors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SolidColorBrush> _brushCache = new(StringComparer.Ordinal);
    private bool _isResourcesInitialized;

    // PUBLIC API

    /// <summary>
    /// Loads and applies the theme during application startup
    /// </summary>
    public void LoadAndApplyThemeOnStartup()
    {
        var theme = LoadThemeFromDisk();
        ApplyTheme(theme);
    }

    /// <summary>
    /// Applies the theme to application resources.
    /// During live preview (<paramref name="isLivePreview"/> = <c>true</c>), mutates existing
    /// <see cref="SolidColorBrush.Color"/> instances in-place without writing to <see cref="ResourceDictionary"/>,
    /// avoiding global <c>ResourcesChanged</c> broadcasts across the visual tree.
    /// </summary>
    /// <param name="theme">Theme settings to apply.</param>
    /// <param name="isLivePreview">When <c>true</c>, performs zero-invalidation in-place brush updates only.</param>
    public void ApplyTheme(ThemeSettings theme, bool isLivePreview = false)
    {
        // GUARD: verify Application readiness
        if (Application.Current?.Resources is not { } resources)
        {
            Log.Warn("Application.Current.Resources not available yet, deferring theme application");
            _cachedTheme = theme; // Save for retry
            return;
        }

        _cachedTheme = theme;

        bool canSkipResourceDictWrites = isLivePreview && _isResourcesInitialized;
        List<KeyValuePair<object, object?>>? pendingBatch = canSkipResourceDictWrites
            ? null
            : new List<KeyValuePair<object, object?>>(64);

        // Backgrounds
        SetColor(resources, pendingBatch, "BgPrimary", theme.BgPrimary);
        SetColor(resources, pendingBatch, "BgSecondary", theme.BgSecondary);
        SetColor(resources, pendingBatch, "BgElevated", theme.BgElevated);
        SetColor(resources, pendingBatch, "BgHighlight", theme.BgHighlight);
        SetColor(resources, pendingBatch, "BgHover", theme.BgHover);
        SetColor(resources, pendingBatch, "BgSkeleton", theme.BgSkeleton);
        SetColor(resources, pendingBatch, "BgSkeletonDeep", theme.BgSkeletonDeep);
        SetColor(resources, pendingBatch, "BgOverlay", theme.BgOverlay);

        // Accent
        SetColor(resources, pendingBatch, "Accent", theme.AccentColor);
        SetColor(resources, pendingBatch, "AccentHover", theme.AccentHover);

        // Semantic
        SetColor(resources, pendingBatch, "SystemError", theme.SystemError);
        SetColor(resources, pendingBatch, "SystemErrorBg", theme.SystemErrorBg);
        SetColor(resources, pendingBatch, "SystemInfoBlue", theme.SystemInfoBlue);
        SetColor(resources, pendingBatch, "SystemWarnOrange", theme.SystemWarnOrange);
        SetColor(resources, pendingBatch, "SystemSuccessGreen", "#2ECC71");

        // Text
        SetColor(resources, pendingBatch, "TextPrimary", theme.TextPrimary);
        SetColor(resources, pendingBatch, "TextSecondary", theme.TextSecondary);
        SetColor(resources, pendingBatch, "TextMuted", theme.TextMuted);
        SetColor(resources, pendingBatch, "TextDark", theme.TextDark);

        // CONTRAST TEXT FOR ACCENT BUTTONS
        // Automatically determine black or white text on an accent background
        _ = TryParseColor(theme.AccentColor, out var accent);
        var accentButtonText = GetContrastingTextColor(accent);
        SetColorDirect(resources, pendingBatch, "AccentButtonText", accentButtonText);

        // AVALONIA FLUENT THEME COMPATIBILITY
        ApplyFluentOverrides(resources, pendingBatch, theme, _appliedFluentTheme);

        if (pendingBatch is { Count: > 0 })
        {
            CommitResourceBatch(resources, pendingBatch);
            _appliedFluentTheme = theme.Clone();
            _isResourcesInitialized = true;
        }

        if (!isLivePreview)
        {
            Log.Info($"Theme '{theme.Name}' applied.");
        }
    }

    /// <summary>
    /// WCAG 2.0 contrast compliance level.
    /// </summary>
    public enum WcagContrastLevel
    {
        /// <summary>Contrast ratio below 3.0:1 (poor readability).</summary>
        Fail,

        /// <summary>Contrast ratio between 3.0:1 and 4.49:1 (acceptable for large text and UI components).</summary>
        Warning,

        /// <summary>Contrast ratio between 4.5:1 and 6.99:1 (WCAG 2.0 AA standard).</summary>
        PassAA,

        /// <summary>Contrast ratio 7.0:1 or higher (WCAG 2.0 AAA enhanced standard).</summary>
        PassAAA
    }

    public static double GetRelativeLuminance(Color color)
    {
        // Linearize sRGB channel according to WCAG 2.0 specification
        static double Linearize(byte channel)
        {
            var s = channel / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linearize(color.R)
             + 0.7152 * Linearize(color.G)
             + 0.0722 * Linearize(color.B);
    }

    /// <summary>
    /// Calculates the WCAG 2.0 contrast ratio between two colors.
    /// Result ranges from 1:1 (identical) to 21:1 (black/white).
    /// </summary>
    /// <param name="color1">First color</param>
    /// <param name="color2">Second color</param>
    /// <returns>Contrast ratio (≥ 1.0)</returns>
    public static double GetContrastRatio(Color color1, Color color2)
    {
        var l1 = GetRelativeLuminance(color1);
        var l2 = GetRelativeLuminance(color2);
        var lighter = Math.Max(l1, l2);
        var darker = Math.Min(l1, l2);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>
    /// Classifies a WCAG 2.0 contrast ratio into a discrete readability tier.
    /// </summary>
    /// <param name="contrastRatio">Contrast ratio (1.0 to 21.0).</param>
    /// <returns>Corresponding <see cref="WcagContrastLevel"/>.</returns>
    public static WcagContrastLevel EvaluateContrastLevel(double contrastRatio)
    {
        if (contrastRatio >= 7.0) return WcagContrastLevel.PassAAA;
        if (contrastRatio >= 4.5) return WcagContrastLevel.PassAA;
        if (contrastRatio >= 3.0) return WcagContrastLevel.Warning;
        return WcagContrastLevel.Fail;
    }

    /// <summary>
    /// Determines the contrasting text color for a given background.
    /// Uses WCAG 2.0 contrast ratio to select between black and white.
    /// Guarantees text readability on any background, including edge cases
    /// (AMOLED Black with white accent, Classic Green with green accent).
    /// </summary>
    /// <param name="background">Background color</param>
    /// <returns>Black or white color providing maximum contrast</returns>
    public static Color GetContrastingTextColor(Color background)
    {
        var white = Color.FromRgb(255, 255, 255);
        var black = Color.FromRgb(0, 0, 0);

        var contrastWithWhite = GetContrastRatio(background, white);
        var contrastWithBlack = GetContrastRatio(background, black);

        // Select the color with higher contrast.
        // In case of a tie, prefer white (better readability on dark themes,
        // which make up the majority of presets).
        return contrastWithBlack > contrastWithWhite ? black : white;
    }

    /// <summary>
    /// Applies overrides for the Avalonia Fluent theme.
    /// Required for standard controls (CheckBox, ToggleSwitch, 
    /// ComboBox, Slider, dialog windows, etc.) to work properly with custom theme colors.
    /// </summary>
    private void ApplyFluentOverrides(
        IResourceDictionary resources,
        List<KeyValuePair<object, object?>>? batch,
        ThemeSettings theme,
        ThemeSettings? previous)
    {
        _ = TryParseColor(theme.AccentColor, out var accent);
        _ = TryParseColor(theme.AccentHover, out var accentHover);
        _ = TryParseColor(theme.BgPrimary, out var bgPrimary);
        _ = TryParseColor(theme.BgSecondary, out var bgSecondary);
        _ = TryParseColor(theme.BgElevated, out var bgElevated);
        _ = TryParseColor(theme.BgHighlight, out var bgHighlight);
        _ = TryParseColor(theme.BgHover, out var bgHover);
        _ = TryParseColor(theme.TextPrimary, out var textPrimary);
        _ = TryParseColor(theme.TextSecondary, out var textSecondary);
        _ = TryParseColor(theme.TextMuted, out var textMuted);
        _ = TryParseColor(theme.TextDark, out var textDark);
        var accentButtonText = GetContrastingTextColor(accent);

        // Always update mutable Fluent brushes in-place (0 dictionary notifications)
        UpdateOrSetBrush(resources, batch, "AccentFillColorDefaultBrush", accent);
        UpdateOrSetBrush(resources, batch, "FocusVisualPrimaryBrush", accent);
        UpdateOrSetBrush(resources, batch, "SystemControlFocusVisualPrimaryBrush", accent, 0.85);
        UpdateOrSetBrush(resources, batch, "FlyoutBorderThemeBrush", bgHighlight);
        UpdateOrSetBrush(resources, batch, "MenuFlyoutPresenterBorderBrush", bgHighlight);

        // Interactive control state brushes (Slider, ToggleSwitch, Expander)
        UpdateOrSetBrush(resources, batch, "ToggleSwitchFillOn", new Color(35, accent.R, accent.G, accent.B));
        UpdateOrSetBrush(resources, batch, "ToggleSwitchFillOnPointerOver", new Color(50, accentHover.R, accentHover.G, accentHover.B));
        UpdateOrSetBrush(resources, batch, "ToggleSwitchFillOnPressed", new Color(65, accentHover.R, accentHover.G, accentHover.B));
        UpdateOrSetBrush(resources, batch, "ToggleSwitchStrokeOn", accent);
        UpdateOrSetBrush(resources, batch, "ToggleSwitchStrokeOnPointerOver", accentHover);
        UpdateOrSetBrush(resources, batch, "ToggleSwitchStrokeOnPressed", accentHover);
        UpdateOrSetBrush(resources, batch, "ToggleSwitchFillOff", Colors.Transparent);
        UpdateOrSetBrush(resources, batch, "ToggleSwitchFillOffPointerOver", Colors.Transparent);
        UpdateOrSetBrush(resources, batch, "ToggleSwitchFillOffPressed", Colors.Transparent);
        UpdateOrSetBrush(resources, batch, "ToggleSwitchStrokeOff", bgHighlight);
        UpdateOrSetBrush(resources, batch, "ToggleSwitchStrokeOffPointerOver", accent);
        UpdateOrSetBrush(resources, batch, "ToggleSwitchStrokeOffPressed", accentHover);
        UpdateOrSetBrush(resources, batch, "ToggleSwitchKnobFillOn", accent);
        UpdateOrSetBrush(resources, batch, "ToggleSwitchKnobFillOnPointerOver", accentHover);
        UpdateOrSetBrush(resources, batch, "ToggleSwitchKnobFillOnPressed", accentHover);
        UpdateOrSetBrush(resources, batch, "ToggleSwitchKnobFillOff", textMuted);
        UpdateOrSetBrush(resources, batch, "ToggleSwitchKnobFillOffPointerOver", textPrimary);
        UpdateOrSetBrush(resources, batch, "ToggleSwitchKnobFillOffPressed", textPrimary);

        UpdateOrSetBrush(resources, batch, "SliderTrackFill", bgHighlight);
        UpdateOrSetBrush(resources, batch, "SliderTrackFillPointerOver", bgHighlight);
        UpdateOrSetBrush(resources, batch, "SliderTrackFillPressed", bgHighlight);
        UpdateOrSetBrush(resources, batch, "SliderTrackValueFill", accent);
        UpdateOrSetBrush(resources, batch, "SliderTrackValueFillPointerOver", accentHover);
        UpdateOrSetBrush(resources, batch, "SliderTrackValueFillPressed", accent);
        UpdateOrSetBrush(resources, batch, "SliderThumbBackground", textPrimary);
        UpdateOrSetBrush(resources, batch, "SliderThumbBackgroundPointerOver", accent);
        UpdateOrSetBrush(resources, batch, "SliderThumbBackgroundPressed", accentHover);
        UpdateOrSetBrush(resources, batch, "SliderOuterThumbBackground", textPrimary);
        UpdateOrSetBrush(resources, batch, "SliderThumbBorderBrush", accent);

        UpdateOrSetBrush(resources, batch, "ExpanderHeaderBackground", new Color(65, bgElevated.R, bgElevated.G, bgElevated.B));
        UpdateOrSetBrush(resources, batch, "ExpanderHeaderBackgroundPointerOver", new Color(105, bgElevated.R, bgElevated.G, bgElevated.B));
        UpdateOrSetBrush(resources, batch, "ExpanderHeaderBackgroundPressed", new Color(135, bgElevated.R, bgElevated.G, bgElevated.B));
        UpdateOrSetBrush(resources, batch, "ExpanderHeaderBorderBrush", bgHighlight);
        UpdateOrSetBrush(resources, batch, "ExpanderHeaderBorderBrushPointerOver", accent);
        UpdateOrSetBrush(resources, batch, "ExpanderHeaderBorderBrushPressed", accentHover);
        UpdateOrSetBrush(resources, batch, "ExpanderContentBackground", new Color(30, bgSecondary.R, bgSecondary.G, bgSecondary.B));
        UpdateOrSetBrush(resources, batch, "ExpanderContentBorderBrush", bgHighlight);
        UpdateOrSetBrush(resources, batch, "ExpanderChevronBackground", Colors.Transparent);
        UpdateOrSetBrush(resources, batch, "ExpanderChevronBackgroundPointerOver", Colors.Transparent);
        UpdateOrSetBrush(resources, batch, "ExpanderChevronBackgroundPressed", Colors.Transparent);
        UpdateOrSetBrush(resources, batch, "ExpanderChevronBorderBrush", Colors.Transparent);
        UpdateOrSetBrush(resources, batch, "ExpanderChevronBorderBrushPointerOver", Colors.Transparent);
        UpdateOrSetBrush(resources, batch, "ExpanderChevronBorderBrushPressed", Colors.Transparent);
        UpdateOrSetBrush(resources, batch, "ExpanderChevronForeground", textSecondary);
        UpdateOrSetBrush(resources, batch, "ExpanderChevronForegroundPointerOver", accent);
        UpdateOrSetBrush(resources, batch, "ExpanderChevronForegroundPressed", accentHover);

        // During live preview, skip raw Color struct writes into ResourceDictionary
        // to avoid triggering global ResourcesChanged across the visual tree.
        if (batch is null)
        {
            return;
        }

        bool isInitial = previous is null || !_isResourcesInitialized;

        bool accentChanged = isInitial || !string.Equals(theme.AccentColor, previous!.AccentColor, StringComparison.OrdinalIgnoreCase);
        bool accentHoverChanged = isInitial || !string.Equals(theme.AccentHover, previous!.AccentHover, StringComparison.OrdinalIgnoreCase);
        bool textPrimaryChanged = isInitial || !string.Equals(theme.TextPrimary, previous!.TextPrimary, StringComparison.OrdinalIgnoreCase);
        bool textSecondaryChanged = isInitial || !string.Equals(theme.TextSecondary, previous!.TextSecondary, StringComparison.OrdinalIgnoreCase);
        bool textMutedChanged = isInitial || !string.Equals(theme.TextMuted, previous!.TextMuted, StringComparison.OrdinalIgnoreCase);
        bool textDarkChanged = isInitial || !string.Equals(theme.TextDark, previous!.TextDark, StringComparison.OrdinalIgnoreCase);
        bool bgPrimaryChanged = isInitial || !string.Equals(theme.BgPrimary, previous!.BgPrimary, StringComparison.OrdinalIgnoreCase);
        bool bgSecondaryChanged = isInitial || !string.Equals(theme.BgSecondary, previous!.BgSecondary, StringComparison.OrdinalIgnoreCase);
        bool bgElevatedChanged = isInitial || !string.Equals(theme.BgElevated, previous!.BgElevated, StringComparison.OrdinalIgnoreCase);
        bool bgHighlightChanged = isInitial || !string.Equals(theme.BgHighlight, previous!.BgHighlight, StringComparison.OrdinalIgnoreCase);
        bool bgHoverChanged = isInitial || !string.Equals(theme.BgHover, previous!.BgHover, StringComparison.OrdinalIgnoreCase);

        if (!isInitial &&
            !accentChanged && !accentHoverChanged &&
            !textPrimaryChanged && !textSecondaryChanged && !textMutedChanged && !textDarkChanged &&
            !bgPrimaryChanged && !bgSecondaryChanged && !bgElevatedChanged && !bgHighlightChanged && !bgHoverChanged)
        {
            return;
        }

        // Colors already parsed above
        var transparent = Colors.Transparent;

        static void AddItem(List<KeyValuePair<object, object?>> list, string k, object? v) =>
            list.Add(new KeyValuePair<object, object?>(k, v));

        if (accentChanged || accentHoverChanged)
        {
            AddItem(batch, "SystemAccentColor", accent);
            AddItem(batch, "SystemAccentColorDark1", accentHover);
            AddItem(batch, "SystemAccentColorDark2", accentHover);
            AddItem(batch, "SystemAccentColorDark3", accentHover);
            AddItem(batch, "SystemAccentColorLight1", accentHover);
            AddItem(batch, "SystemAccentColorLight2", accentHover);
            AddItem(batch, "SystemAccentColorLight3", accentHover);

            AddItem(batch, "AccentFillColorSecondary", accentHover);
            AddItem(batch, "AccentFillColorTertiary", accent);
        }

        if (accentChanged || textSecondaryChanged)
        {
            AddItem(batch, "TextOnAccentFillColorPrimary", accentButtonText);
            AddItem(batch, "TextOnAccentFillColorSecondary", accentButtonText);
            AddItem(batch, "TextOnAccentFillColorDisabled", textSecondary);
            AddItem(batch, "TextOnAccentFillColorSelectedText", accentButtonText);
        }

        if (textPrimaryChanged || textSecondaryChanged || textDarkChanged)
        {
            AddItem(batch, "TextFillColorPrimary", textPrimary);
            AddItem(batch, "TextFillColorSecondary", textSecondary);
            AddItem(batch, "TextFillColorTertiary", textSecondary);
            AddItem(batch, "TextFillColorDisabled", textSecondary);
            AddItem(batch, "TextFillColorInverse", textDark);
        }

        if (bgHighlightChanged || textSecondaryChanged)
        {
            AddItem(batch, "ControlFillColorTertiary", bgHighlight);
            AddItem(batch, "ControlStrokeColorDefault", bgHighlight);
            AddItem(batch, "ControlStrokeColorSecondary", bgHighlight);
            AddItem(batch, "ControlStrongStrokeColorDefault", textSecondary);
            AddItem(batch, "ControlStrongStrokeColorDisabled", bgHighlight);
            AddItem(batch, "AccentFillColorDisabled", bgHighlight);
        }

        if (bgHoverChanged || bgHighlightChanged)
        {
            AddItem(batch, "SubtleFillColorSecondary", bgHover);
            AddItem(batch, "SubtleFillColorTertiary", bgHighlight);
        }

        if (accentChanged || accentHoverChanged || bgHighlightChanged || textSecondaryChanged)
        {
            AddItem(batch, "AccentButtonBackground", accent);
            AddItem(batch, "AccentButtonBackgroundPointerOver", accent);
            AddItem(batch, "AccentButtonBackgroundPressed", accentHover);
            AddItem(batch, "AccentButtonBackgroundDisabled", bgHighlight);

            AddItem(batch, "AccentButtonForeground", accentButtonText);
            AddItem(batch, "AccentButtonForegroundPointerOver", accentButtonText);
            AddItem(batch, "AccentButtonForegroundPressed", accentButtonText);
            AddItem(batch, "AccentButtonForegroundDisabled", textSecondary);

            AddItem(batch, "AccentButtonBorderBrushPointerOver", accentButtonText);
        }

        if (bgElevatedChanged || bgHighlightChanged || accentChanged || accentHoverChanged || textSecondaryChanged)
        {
            AddItem(batch, "CheckBoxCheckBackgroundFillUnchecked", bgElevated);
            AddItem(batch, "CheckBoxCheckBackgroundFillUncheckedPointerOver", bgElevated);
            AddItem(batch, "CheckBoxCheckBackgroundFillUncheckedPressed", bgElevated);
            AddItem(batch, "CheckBoxCheckBackgroundFillUncheckedDisabled", bgHighlight);
            AddItem(batch, "CheckBoxCheckBackgroundFillChecked", accent);
            AddItem(batch, "CheckBoxCheckBackgroundFillCheckedPointerOver", accent);
            AddItem(batch, "CheckBoxCheckBackgroundFillCheckedPressed", accentHover);
            AddItem(batch, "CheckBoxCheckBackgroundFillCheckedDisabled", bgHighlight);

            AddItem(batch, "CheckBoxCheckBackgroundStrokeUnchecked", bgHighlight);
            AddItem(batch, "CheckBoxCheckBackgroundStrokeUncheckedPointerOver", accent);
            AddItem(batch, "CheckBoxCheckBackgroundStrokeUncheckedPressed", accent);
            AddItem(batch, "CheckBoxCheckBackgroundStrokeUncheckedDisabled", bgHighlight);
            AddItem(batch, "CheckBoxCheckBackgroundStrokeChecked", accent);
            AddItem(batch, "CheckBoxCheckBackgroundStrokeCheckedPointerOver", accentHover);
            AddItem(batch, "CheckBoxCheckBackgroundStrokeCheckedPressed", accent);
            AddItem(batch, "CheckBoxCheckBackgroundStrokeCheckedDisabled", bgHighlight);

            AddItem(batch, "CheckBoxCheckGlyphForegroundChecked", accentButtonText);
            AddItem(batch, "CheckBoxCheckGlyphForegroundCheckedPointerOver", accentButtonText);
            AddItem(batch, "CheckBoxCheckGlyphForegroundCheckedPressed", accentButtonText);
            AddItem(batch, "CheckBoxCheckGlyphForegroundCheckedDisabled", textSecondary);
        }

        if (bgHighlightChanged || textSecondaryChanged || textMutedChanged)
        {
            AddItem(batch, "ToggleSwitchFillOffDisabled", bgHighlight);
            AddItem(batch, "ToggleSwitchFillOnDisabled", bgHighlight);
            AddItem(batch, "ToggleSwitchStrokeOffDisabled", bgHighlight);
            AddItem(batch, "ToggleSwitchStrokeOnDisabled", bgHighlight);
            AddItem(batch, "ToggleSwitchKnobFillOffDisabled", textMuted);
            AddItem(batch, "ToggleSwitchKnobFillOnDisabled", textMuted);
            AddItem(batch, "SliderTrackFillDisabled", bgHighlight);
            AddItem(batch, "SliderTrackValueFillDisabled", bgHighlight);
            AddItem(batch, "SliderThumbBackgroundDisabled", textSecondary);
        }

        if (bgPrimaryChanged || bgHighlightChanged || accentChanged || textPrimaryChanged || textSecondaryChanged)
        {
            AddItem(batch, "ComboBoxBorderBrush", bgHighlight);
            AddItem(batch, "ComboBoxBorderBrushPointerOver", accent);
            AddItem(batch, "ComboBoxBorderBrushPressed", accent);
            AddItem(batch, "ComboBoxBorderBrushDisabled", bgHighlight);

            AddItem(batch, "ComboBoxForeground", textPrimary);
            AddItem(batch, "ComboBoxForegroundPointerOver", textPrimary);
            AddItem(batch, "ComboBoxForegroundPressed", textPrimary);
            AddItem(batch, "ComboBoxForegroundDisabled", textSecondary);

            AddItem(batch, "ComboBoxDropDownBackground", bgPrimary);
            AddItem(batch, "ComboBoxDropDownBorderBrush", bgHighlight);

            AddItem(batch, "ButtonForeground", textPrimary);
            AddItem(batch, "ButtonForegroundPointerOver", textPrimary);
            AddItem(batch, "ButtonForegroundPressed", textPrimary);
            AddItem(batch, "ButtonForegroundDisabled", textSecondary);
        }

        if (bgPrimaryChanged || bgSecondaryChanged || bgElevatedChanged || bgHighlightChanged || textPrimaryChanged)
        {
            AddItem(batch, "SolidBackgroundFillColorBase", bgPrimary);
            AddItem(batch, "SolidBackgroundFillColorSecondary", bgSecondary);
            AddItem(batch, "SolidBackgroundFillColorTertiary", bgElevated);
            AddItem(batch, "SolidBackgroundFillColorQuarternary", bgHighlight);

            AddItem(batch, "LayerFillColorDefault", bgElevated);
            AddItem(batch, "LayerFillColorAlt", bgSecondary);
            AddItem(batch, "LayerOnMicaBaseAltFillColorDefault", bgPrimary);

            AddItem(batch, "CardBackgroundFillColorDefault", bgElevated);
            AddItem(batch, "CardBackgroundFillColorSecondary", bgSecondary);
            AddItem(batch, "CardStrokeColorDefault", bgHighlight);

            AddItem(batch, "DividerStrokeColorDefault", bgHighlight);

            AddItem(batch, "FlyoutBackground", bgElevated);
            AddItem(batch, "MenuFlyoutPresenterBackground", bgElevated);
            AddItem(batch, "MenuFlyoutItemForeground", textPrimary);
            AddItem(batch, "MenuFlyoutItemForegroundPointerOver", textPrimary);

            AddItem(batch, "ContentDialogBackground", bgElevated);
            AddItem(batch, "ContentDialogTopOverlay", bgElevated);
            AddItem(batch, "ContentDialogBorderBrush", bgHighlight);
            AddItem(batch, "ContentDialogForeground", textPrimary);
        }

        if (textDarkChanged)
        {
            AddItem(batch, "InfoBadgeForeground", textDark);
            AddItem(batch, "InfoBarErrorSeverityIconBackground", textDark);
            AddItem(batch, "InfoBarWarningSeverityIconBackground", textDark);
            AddItem(batch, "InfoBarSuccessSeverityIconBackground", textDark);
            AddItem(batch, "InfoBarInformationalSeverityIconBackground", textDark);
        }

        if (isInitial)
        {
            AddItem(batch, "SliderPreContentMargin", new GridLength(1, GridUnitType.Star));
            AddItem(batch, "SliderPostContentMargin", new GridLength(1, GridUnitType.Star));
            AddItem(batch, "SliderHorizontalHeight", 24.0);
            AddItem(batch, "SliderHorizontalThumbWidth", 16.0);
            AddItem(batch, "SliderHorizontalThumbHeight", 16.0);
            AddItem(batch, "SliderVerticalThumbWidth", 16.0);
            AddItem(batch, "SliderVerticalThumbHeight", 16.0);
            AddItem(batch, "SliderThumbCornerRadius", new CornerRadius(8));
            AddItem(batch, "ToggleSwitchOnStrokeThickness", new Thickness(1));

            AddItem(batch, "ControlFillColorDefault", transparent);
            AddItem(batch, "ControlFillColorSecondary", transparent);
            AddItem(batch, "ControlFillColorInputActive", transparent);
            AddItem(batch, "ControlFillColorDisabled", transparent);

            AddItem(batch, "SubtleFillColorTransparent", transparent);
            AddItem(batch, "SubtleFillColorDisabled", transparent);

            AddItem(batch, "AccentButtonBorderBrush", transparent);
            AddItem(batch, "AccentButtonBorderBrushPressed", transparent);
            AddItem(batch, "AccentButtonBorderBrushDisabled", transparent);

            AddItem(batch, "CheckBoxCheckGlyphForegroundUnchecked", transparent);
            AddItem(batch, "CheckBoxCheckGlyphForegroundUncheckedPointerOver", transparent);
            AddItem(batch, "CheckBoxCheckGlyphForegroundUncheckedPressed", transparent);
            AddItem(batch, "CheckBoxCheckGlyphForegroundUncheckedDisabled", transparent);

            AddItem(batch, "ToggleSwitchContainerBackground", transparent);
            AddItem(batch, "ToggleSwitchContainerBackgroundPointerOver", transparent);

            AddItem(batch, "ComboBoxBackground", transparent);
            AddItem(batch, "ComboBoxBackgroundPointerOver", transparent);
            AddItem(batch, "ComboBoxBackgroundPressed", transparent);
            AddItem(batch, "ComboBoxBackgroundDisabled", transparent);

            AddItem(batch, "ComboBoxItemBackground", transparent);
            AddItem(batch, "ComboBoxItemBackgroundPointerOver", transparent);
            AddItem(batch, "ComboBoxItemBackgroundPressed", transparent);
            AddItem(batch, "ComboBoxItemBackgroundDisabled", transparent);

            AddItem(batch, "ButtonBackground", transparent);
            AddItem(batch, "ButtonBackgroundPointerOver", transparent);
            AddItem(batch, "ButtonBackgroundPressed", transparent);
            AddItem(batch, "ButtonBackgroundDisabled", transparent);

            AddItem(batch, "ButtonBorderBrush", transparent);
            AddItem(batch, "ButtonBorderBrushPointerOver", transparent);
            AddItem(batch, "ButtonBorderBrushPressed", transparent);
            AddItem(batch, "ButtonBorderBrushDisabled", transparent);

            UpdateOrSetBrush(resources, batch, "FocusVisualSecondaryBrush", transparent);
            UpdateOrSetBrush(resources, batch, "SystemControlFocusVisualSecondaryBrush", transparent);

            AddItem(batch, "MenuFlyoutItemBackground", transparent);
            AddItem(batch, "MenuFlyoutItemBackgroundPointerOver", transparent);
            AddItem(batch, "MenuFlyoutItemBackgroundPressed", transparent);
            AddItem(batch, "MenuFlyoutItemBackgroundDisabled", transparent);
            AddItem(batch, "MenuFlyoutSubItemBackground", transparent);
            AddItem(batch, "MenuFlyoutSubItemBackgroundPointerOver", transparent);
            AddItem(batch, "MenuFlyoutSubItemBackgroundPressed", transparent);
            AddItem(batch, "MenuFlyoutSubItemBackgroundDisabled", transparent);
        }
    }

    /// <summary>
    /// Saves the theme to disk
    /// </summary>
    public void SaveTheme(ThemeSettings theme)
    {
        try
        {
            string json = JsonSerializer.Serialize(theme, AppJsonContext.DefaultPretty.ThemeSettings);
            AtomicFile.WriteText(G.FilePath.Theme, json, createBackup: false);
            _cachedTheme = theme;
            Log.Info($"Theme '{theme.Name}' saved.");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to save theme: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the currently loaded theme
    /// </summary>
    public ThemeSettings GetCurrentTheme()
    {
        return _cachedTheme ?? LoadThemeFromDisk();
    }

    /// <summary>
    /// Returns the default theme (Paralax Purple)
    /// </summary>
    public static ThemeSettings GetDefaultTheme() => new() { IsBuiltIn = true };

    /// <summary>
    /// Resets the theme to default
    /// </summary>
    public void ResetToDefault()
    {
        try
        {
            if (File.Exists(G.FilePath.Theme))
                File.Delete(G.FilePath.Theme);

            var backupPath = string.Concat(G.FilePath.Theme, ".bak");
            if (File.Exists(backupPath))
                File.Delete(backupPath);
        }
        catch { /* Ignore deletion error */ }

        var def = GetDefaultTheme();
        SaveTheme(def);
        ApplyTheme(def);
    }

    /// <summary>
    /// Returns the list of built-in theme presets
    /// </summary>
    public static IReadOnlyList<ThemeSettings> GetBuiltInPresets() =>
    [
        // 1. PARALAX PURPLE (Default)
        new ThemeSettings { IsBuiltIn = true },

        // 2. CLASSIC GREEN (Spotify-like)
        new ThemeSettings
        {
            Name = "Classic Green",
            IsBuiltIn = true,
            BgPrimary = "#121212",
            BgSecondary = "#1E1E1E",
            BgElevated = "#282828",
            BgHighlight = "#404040",
            BgHover = "#505050",
            BgSkeleton = "#282828",
            BgSkeletonDeep = "#1a1a1a",
            BgOverlay = "#CC121212",
            AccentColor = "#1DB954",
            AccentHover = "#20e063",
            TextPrimary = "#FFFFFF",
            TextSecondary = "#B3B3B3",
            TextMuted = "#888888",
            TextDark = "#000000"
        },

        // 3. OCEAN DEEP
        new ThemeSettings
        {
            Name = "Ocean Deep",
            IsBuiltIn = true,
            BgPrimary = "#001219",
            BgSecondary = "#001f2d",
            BgElevated = "#002d42",
            BgHighlight = "#003b57",
            BgHover = "#00506b",
            BgSkeleton = "#002535",
            BgSkeletonDeep = "#000d12",
            BgOverlay = "#CC001219",
            AccentColor = "#00B4D8",
            AccentHover = "#45c9e4",
            TextPrimary = "#E0FBFC",
            TextSecondary = "#98C1D9",
            TextMuted = "#5B8FA8",
            TextDark = "#001219"
        },

        // 4. AMOLED BLACK
        new ThemeSettings
        {
            Name = "AMOLED Black",
            IsBuiltIn = true,
            BgPrimary = "#000000",
            BgSecondary = "#0A0A0A",
            BgElevated = "#141414",
            BgHighlight = "#1F1F1F",
            BgHover = "#2A2A2A",
            BgSkeleton = "#141414",
            BgSkeletonDeep = "#050505",
            BgOverlay = "#CC000000",
            AccentColor = "#FFFFFF",
            AccentHover = "#CCCCCC",
            TextPrimary = "#FFFFFF",
            TextSecondary = "#A0A0A0",
            TextMuted = "#606060",
            TextDark = "#000000"
        },

        // 5. WARM SUNSET
        new ThemeSettings
        {
            Name = "Warm Sunset",
            IsBuiltIn = true,
            BgPrimary = "#1A1210",
            BgSecondary = "#261A16",
            BgElevated = "#33221C",
            BgHighlight = "#4A3228",
            BgHover = "#5C3F32",
            BgSkeleton = "#2A1C18",
            BgSkeletonDeep = "#120E0C",
            BgOverlay = "#CC1A1210",
            AccentColor = "#FF6B35",
            AccentHover = "#FF8C5A",
            TextPrimary = "#FFF5F0",
            TextSecondary = "#D4B5A5",
            TextMuted = "#8B7265",
            TextDark = "#1A1210"
        },

        // 6. DRACULA
        new ThemeSettings
        {
            Name = "Dracula",
            IsBuiltIn = true,
            BgPrimary = "#282a36",
            BgSecondary = "#21222c",
            BgElevated = "#343746",
            BgHighlight = "#44475a",
            BgHover = "#4d5066",
            BgSkeleton = "#343746",
            BgSkeletonDeep = "#1e1f29",
            BgOverlay = "#CC282a36",
            AccentColor = "#bd93f9",
            AccentHover = "#d4b8ff",
            TextPrimary = "#f8f8f2",
            TextSecondary = "#bfbfbf",
            TextMuted = "#6272a4",
            TextDark = "#282a36"
        }
    ];

    // PRIVATE HELPERS

    private ThemeSettings LoadThemeFromDisk()
    {
        try
        {
            var jsonPath = G.FilePath.Theme;
            var json = AtomicFile.ReadTextWithFallback(jsonPath, out bool recovered);
            if (!string.IsNullOrWhiteSpace(json))
            {
                if (recovered)
                    Log.Warn("[ThemeManager] Recovered theme settings from backup (.bak)");

                var theme = JsonSerializer.Deserialize(json, AppJsonContext.Default.ThemeSettings);
                if (theme != null)
                {
                    _cachedTheme = theme;
                    return theme;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load theme: {ex.Message}");
        }

        // Save default on first run
        var def = GetDefaultTheme();
        SaveTheme(def);
        return def;
    }

    private void SetColor(
        IResourceDictionary resources,
        List<KeyValuePair<object, object?>>? batch,
        string key,
        string hex)
    {
        if (!TryParseColor(hex, out var color))
        {
            Log.Error($"Invalid color: {key}={hex}");
            color = Colors.Magenta;
        }

        SetColorDirect(resources, batch, key, color);
    }

    private void SetColorDirect(
        IResourceDictionary resources,
        List<KeyValuePair<object, object?>>? batch,
        string key,
        Color color)
    {
        bool brushColorUnchanged = _appliedColors.TryGetValue(key, out var existingColor) && existingColor == color;
        if (!brushColorUnchanged || !_isResourcesInitialized)
        {
            _appliedColors[key] = color;
            UpdateOrSetBrush(resources, batch, $"{key}Brush", color);

            // Transparent version for animations (alpha = 0, but identical RGB)
            var transparentBrushColor = new Color(0, color.R, color.G, color.B);
            UpdateOrSetBrush(resources, batch, $"{key}TransparentBrush", transparentBrushColor);
        }

        if (batch is not null)
        {
            bool committedUnchanged = _committedColors.TryGetValue(key, out var committedColor)
                                   && committedColor == color
                                   && _isResourcesInitialized;
            if (!committedUnchanged)
            {
                _committedColors[key] = color;
                var transparent = new Color(0, color.R, color.G, color.B);
                batch.Add(new KeyValuePair<object, object?>(key, color));
                batch.Add(new KeyValuePair<object, object?>($"{key}Transparent", transparent));
            }
        }
    }

    private void UpdateOrSetBrush(
        IResourceDictionary resources,
        List<KeyValuePair<object, object?>>? batch,
        string brushKey,
        Color color,
        double opacity = 1.0)
    {
        if (_brushCache.TryGetValue(brushKey, out var cachedBrush))
        {
            if (cachedBrush.Color != color)
            {
                cachedBrush.Color = color;
            }
            return;
        }

        if (resources.TryGetResource(brushKey, null, out var existing)
            && existing is SolidColorBrush mutableBrush)
        {
            _brushCache[brushKey] = mutableBrush;
            if (mutableBrush.Color != color)
            {
                mutableBrush.Color = color;
            }
            return;
        }

        var newBrush = opacity < 1.0
            ? new SolidColorBrush(color) { Opacity = opacity }
            : new SolidColorBrush(color);

        _brushCache[brushKey] = newBrush;
        if (batch is not null)
        {
            batch.Add(new KeyValuePair<object, object?>(brushKey, newBrush));
        }
        else
        {
            resources[brushKey] = newBrush;
        }
    }

    private static void CommitResourceBatch(
        IResourceDictionary resources,
        List<KeyValuePair<object, object?>> batch)
    {
        if (resources is ResourceDictionary concreteDict)
        {
            concreteDict.SetItems(batch);
        }
        else
        {
            for (int i = 0; i < batch.Count; i++)
            {
                resources[batch[i].Key] = batch[i].Value;
            }
        }
    }

    /// <summary>
    /// Reads a color from the resource dictionary of the active Avalonia theme.
    /// If the key is missing, returns gray as a safe fallback.
    /// </summary>
    public static Color GetThemeColor(string key)
    {
        var resources = Application.Current?.Resources;
        if (resources != null)
        {
            if (resources.TryGetResource($"{key}Brush", null, out var brushRes)
                && brushRes is ISolidColorBrush solidBrush)
            {
                return solidBrush.Color;
            }

            if (resources.TryGetResource(key, null, out var res)
                && res is Color color)
            {
                return color;
            }
        }

        return Colors.Gray;
    }

    /// <summary>
    /// Formats a color as a 6-digit RGB HEX string (<c>#RRGGBB</c>).
    /// </summary>
    public static string FormatHexColor(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>
    /// Formats a color as an 8-digit ARGB HEX string (<c>#AARRGGBB</c>).
    /// </summary>
    public static string FormatArgbHexColor(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>
    /// Safely parses a HEX color string, returning <paramref name="fallback"/> on failure.
    /// </summary>
    public static Color ParseColorSafe(string? hex, Color? fallback = null)
    {
        if (!string.IsNullOrWhiteSpace(hex) && TryParseColor(hex, out var parsed))
            return parsed;

        return fallback ?? Colors.Magenta;
    }

    /// <summary>
    /// Lightens a color by interpolating its RGB channels toward white.
    /// </summary>
    public static Color LightenColor(Color c, double factor) =>
        Color.FromArgb(c.A,
            (byte)Math.Clamp((int)Math.Round(c.R + (255 - c.R) * factor), 0, 255),
            (byte)Math.Clamp((int)Math.Round(c.G + (255 - c.G) * factor), 0, 255),
            (byte)Math.Clamp((int)Math.Round(c.B + (255 - c.B) * factor), 0, 255));

    /// <summary>
    /// Darkens a color by scaling its RGB channels toward black.
    /// </summary>
    public static Color DarkenColor(Color c, double factor) =>
        Color.FromArgb(c.A,
            (byte)Math.Clamp((int)Math.Round(c.R * (1.0 - factor)), 0, 255),
            (byte)Math.Clamp((int)Math.Round(c.G * (1.0 - factor)), 0, 255),
            (byte)Math.Clamp((int)Math.Round(c.B * (1.0 - factor)), 0, 255));

    /// <summary>
    /// Computes an adaptive hover accent color based on perceived brightness.
    /// </summary>
    public static Color SmartAccentHover(Color accent)
    {
        var brightness = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255.0;
        return brightness > 0.7
            ? DarkenColor(accent, 0.15)
            : LightenColor(accent, 0.15);
    }

    /// <summary>Derives the 1px highlight/border color from the secondary background.</summary>
    public static Color DeriveBgHighlight(Color bgSecondary) => LightenColor(bgSecondary, 0.10);

    /// <summary>Derives the pointer-over surface color from the secondary background.</summary>
    public static Color DeriveBgHover(Color bgSecondary) => LightenColor(bgSecondary, 0.20);

    /// <summary>Derives the loading skeleton color from the secondary background.</summary>
    public static Color DeriveBgSkeleton(Color bgSecondary) => LightenColor(bgSecondary, 0.05);

    /// <summary>Derives the deep loading skeleton color from the secondary background.</summary>
    public static Color DeriveBgSkeletonDeep(Color bgSecondary) => DarkenColor(bgSecondary, 0.20);

    /// <summary>Derives the semi-transparent modal overlay color from the primary background.</summary>
    public static Color DeriveBgOverlay(Color bgPrimary, byte alpha = 0xCC) =>
        Color.FromArgb(alpha, bgPrimary.R, bgPrimary.G, bgPrimary.B);

    /// <summary>Derives the muted text color from the secondary text color.</summary>
    public static Color DeriveTextMuted(Color textSecondary) => DarkenColor(textSecondary, 0.30);

    /// <summary>
    /// Serializes a <see cref="ThemeSettings"/> instance to formatted JSON for clipboard export.
    /// </summary>
    public static string ExportThemeJson(ThemeSettings theme) =>
        JsonSerializer.Serialize(theme, AppJsonContext.DefaultPretty.ThemeSettings);

    /// <summary>
    /// Attempts to deserialize and validate a <see cref="ThemeSettings"/> instance from clipboard JSON.
    /// </summary>
    public static bool TryImportThemeJson(string? json, out ThemeSettings? theme)
    {
        theme = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            var trimmed = json.Trim();
            if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}'))
                return false;

            var parsed = JsonSerializer.Deserialize(trimmed, AppJsonContext.Default.ThemeSettings);
            if (parsed is null)
                return false;

            if (!TryParseColor(parsed.AccentColor, out var accent) ||
                !TryParseColor(parsed.BgPrimary, out var bgPrimary) ||
                !TryParseColor(parsed.BgSecondary, out var bgSecondary) ||
                !TryParseColor(parsed.BgElevated, out _) ||
                !TryParseColor(parsed.TextPrimary, out _) ||
                !TryParseColor(parsed.TextSecondary, out var textSecondary))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(parsed.Name))
                parsed.Name = LocalizationService.Instance["Theme_Custom"];

            if (!TryParseColor(parsed.AccentHover, out _))
                parsed.AccentHover = FormatHexColor(SmartAccentHover(accent));
            if (!TryParseColor(parsed.BgHighlight, out _))
                parsed.BgHighlight = FormatHexColor(DeriveBgHighlight(bgSecondary));
            if (!TryParseColor(parsed.BgHover, out _))
                parsed.BgHover = FormatHexColor(DeriveBgHover(bgSecondary));
            if (!TryParseColor(parsed.BgSkeleton, out _))
                parsed.BgSkeleton = FormatHexColor(DeriveBgSkeleton(bgSecondary));
            if (!TryParseColor(parsed.BgSkeletonDeep, out _))
                parsed.BgSkeletonDeep = FormatHexColor(DeriveBgSkeletonDeep(bgSecondary));
            if (!TryParseColor(parsed.BgOverlay, out _))
                parsed.BgOverlay = FormatArgbHexColor(DeriveBgOverlay(bgPrimary));
            if (!TryParseColor(parsed.TextMuted, out _))
                parsed.TextMuted = FormatHexColor(DeriveTextMuted(textSecondary));
            if (!TryParseColor(parsed.TextDark, out _))
                parsed.TextDark = FormatHexColor(bgPrimary);

            var def = GetDefaultTheme();
            if (!TryParseColor(parsed.SystemError, out _))
                parsed.SystemError = def.SystemError;
            if (!TryParseColor(parsed.SystemErrorBg, out _))
                parsed.SystemErrorBg = def.SystemErrorBg;
            if (!TryParseColor(parsed.SystemInfoBlue, out _))
                parsed.SystemInfoBlue = def.SystemInfoBlue;
            if (!TryParseColor(parsed.SystemWarnOrange, out _))
                parsed.SystemWarnOrange = def.SystemWarnOrange;

            theme = parsed.Clone(isBuiltIn: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryParseColor(string hex, out Color color)
    {
        color = Colors.Transparent;
        try
        {
            color = Color.Parse(hex);
            return true;
        }
        catch
        {
            return false;
        }
    }
}