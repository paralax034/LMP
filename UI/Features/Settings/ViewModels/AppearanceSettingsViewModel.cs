using System.Collections.ObjectModel;
using Avalonia.Media;
using LMP.UI.Controls;

namespace LMP.UI.Features.Settings.ViewModels;

public sealed partial class AppearanceSettingsViewModel : ViewModelBase
{
    private readonly LibraryService _library;
    private readonly ThemeManagerService _themeManager;
    private bool _isLoadingTheme;
    private bool _isLoadingSettings;

    public ObservableCollection<ThemeSettings> ThemePresets { get; } = [];

    [ObservableProperty] public partial ThemeSettings? SelectedPreset { get; set; }
    [ObservableProperty] public partial Color AccentColor { get; set; }
    [ObservableProperty] public partial Color BgPrimaryColor { get; set; }
    [ObservableProperty] public partial Color BgSecondaryColor { get; set; }
    [ObservableProperty] public partial Color BgElevatedColor { get; set; }
    [ObservableProperty] public partial Color TextPrimaryColor { get; set; }
    [ObservableProperty] public partial Color TextSecondaryColor { get; set; }

    [ObservableProperty] public partial IReadOnlyList<LocalizedItem<TrackAnimationSpeed>> TrackAnimationSpeedOptions { get; private set; } = [];
    [ObservableProperty] public partial LocalizedItem<TrackAnimationSpeed>? SelectedTrackAnimationSpeed { get; set; }
    [ObservableProperty] public partial bool UseWaveAnimation { get; set; }
    [ObservableProperty] public partial bool HasUnsavedThemeChanges { get; set; }

    public IRelayCommand ApplyThemeCommand { get; }
    public IRelayCommand ResetThemeCommand { get; }

    public AppearanceSettingsViewModel(LibraryService library, ThemeManagerService themeManager)
    {
        _library = library;
        _themeManager = themeManager;

        ApplyThemeCommand = new RelayCommand(ApplyTheme);
        ResetThemeCommand = new RelayCommand(ResetTheme);

        RefreshPresets();
        RefreshLists();
        LoadSettings();
    }

    public void RefreshPresets()
    {
        ThemePresets.Clear();
        foreach (var preset in ThemeManagerService.GetBuiltInPresets())
            ThemePresets.Add(preset);

        var saved = _themeManager.GetCurrentTheme();
        var isBuiltIn = ThemePresets.Any(p =>
            string.Equals(p.Name, saved.Name, StringComparison.OrdinalIgnoreCase));

        if (!isBuiltIn && !saved.IsBuiltIn)
            ThemePresets.Add(saved);
    }

    public void RefreshLists()
    {
        _isLoadingSettings = true;
        try
        {
            var currentSpeed = SelectedTrackAnimationSpeed?.Value ?? _library.Settings.TrackAnimationSpeed;
            TrackAnimationSpeedOptions = LocalizedItem.CreateList<TrackAnimationSpeed>("AnimationSpeed_");
            SelectedTrackAnimationSpeed = TrackAnimationSpeedOptions.FindByValue(currentSpeed, 2);
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    public void LoadSettings()
    {
        _isLoadingSettings = true;
        try
        {
            var s = _library.Settings;
            UseWaveAnimation = s.UseWaveAnimation;
            SelectedTrackAnimationSpeed = TrackAnimationSpeedOptions.FindByValue(s.TrackAnimationSpeed, 2);
            AudioWaveBorder.ConfigureGlobal(s.UseWaveAnimation, s.TrackAnimationSpeed);

            LoadThemeColors();
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    private void LoadThemeColors()
    {
        _isLoadingTheme = true;
        try
        {
            var currentTheme = _themeManager.GetCurrentTheme();
            ApplyThemeToColorPickers(currentTheme);

            var matchingPreset = ThemePresets.FirstOrDefault(p =>
                string.Equals(p.Name, currentTheme.Name, StringComparison.OrdinalIgnoreCase));

            SelectedPreset = matchingPreset ?? ThemePresets.FirstOrDefault();
            HasUnsavedThemeChanges = false;
        }
        finally
        {
            _isLoadingTheme = false;
        }
    }

    private void ApplyThemeToColorPickers(ThemeSettings theme)
    {
        _isLoadingTheme = true;
        try
        {
            AccentColor = ParseColorSafe(theme.AccentColor);
            BgPrimaryColor = ParseColorSafe(theme.BgPrimary);
            BgSecondaryColor = ParseColorSafe(theme.BgSecondary);
            BgElevatedColor = ParseColorSafe(theme.BgElevated);
            TextPrimaryColor = ParseColorSafe(theme.TextPrimary);
            TextSecondaryColor = ParseColorSafe(theme.TextSecondary);
        }
        finally
        {
            _isLoadingTheme = false;
        }
    }

    partial void OnSelectedPresetChanged(ThemeSettings? value)
    {
        if (_isLoadingSettings || value is null) return;
        ApplyThemeToColorPickers(value);
        HasUnsavedThemeChanges = true;
    }

    partial void OnAccentColorChanged(Color value) => OnColorPickerChanged();
    partial void OnBgPrimaryColorChanged(Color value) => OnColorPickerChanged();
    partial void OnBgSecondaryColorChanged(Color value) => OnColorPickerChanged();
    partial void OnBgElevatedColorChanged(Color value) => OnColorPickerChanged();
    partial void OnTextPrimaryColorChanged(Color value) => OnColorPickerChanged();
    partial void OnTextSecondaryColorChanged(Color value) => OnColorPickerChanged();

    partial void OnUseWaveAnimationChanged(bool value)
    {
        if (_isLoadingSettings) return;
        _library.UpdateSettings(s => s.UseWaveAnimation = value);
        AudioWaveBorder.ConfigureGlobal(value, SelectedTrackAnimationSpeed?.Value ?? TrackAnimationSpeed.Medium);
    }

    partial void OnSelectedTrackAnimationSpeedChanged(LocalizedItem<TrackAnimationSpeed>? value)
    {
        if (_isLoadingSettings || value is null) return;
        _library.UpdateSettings(s => s.TrackAnimationSpeed = value.Value);
        AudioWaveBorder.ConfigureGlobal(UseWaveAnimation, value.Value);
    }

    private void OnColorPickerChanged()
    {
        if (!_isLoadingSettings && !_isLoadingTheme)
            HasUnsavedThemeChanges = true;
    }

    private void ApplyTheme()
    {
        static string GetRgbHex(Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";

        var current = _themeManager.GetCurrentTheme();

        var theme = new ThemeSettings
        {
            Name = SelectedPreset?.Name ?? SL["Theme_Custom"],
            AccentColor = AccentColor.ToString(),
            AccentHover = SmartAccentHover(AccentColor).ToString(),
            BgPrimary = BgPrimaryColor.ToString(),
            BgSecondary = BgSecondaryColor.ToString(),
            BgElevated = BgElevatedColor.ToString(),
            BgHighlight = LightenColor(BgSecondaryColor, 0.1).ToString(),
            BgHover = LightenColor(BgSecondaryColor, 0.2).ToString(),
            BgSkeleton = LightenColor(BgSecondaryColor, 0.05).ToString(),
            BgSkeletonDeep = DarkenColor(BgSecondaryColor, 0.2).ToString(),
            BgOverlay = $"#CC{GetRgbHex(BgPrimaryColor)}",
            TextPrimary = TextPrimaryColor.ToString(),
            TextSecondary = TextSecondaryColor.ToString(),
            TextMuted = DarkenColor(TextSecondaryColor, 0.3).ToString(),
            TextDark = BgPrimaryColor.ToString(),
            SystemError = current.SystemError,
            SystemErrorBg = current.SystemErrorBg,
            SystemInfoBlue = current.SystemInfoBlue,
            SystemWarnOrange = current.SystemWarnOrange,
        };

        _themeManager.SaveTheme(theme);
        _themeManager.ApplyTheme(theme);
        HasUnsavedThemeChanges = false;

        RefreshPresets();

        _isLoadingTheme = true;
        SelectedPreset = ThemePresets.FirstOrDefault(p =>
            string.Equals(p.Name, theme.Name, StringComparison.OrdinalIgnoreCase))
            ?? ThemePresets.FirstOrDefault();
        _isLoadingTheme = false;
    }

    private void ResetTheme()
    {
        _themeManager.ResetToDefault();
        LoadThemeColors();
        SelectedPreset = ThemePresets.FirstOrDefault();
    }

    private static Color SmartAccentHover(Color accent)
    {
        var brightness = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255.0;
        return brightness > 0.7
            ? DarkenColor(accent, 0.15)
            : LightenColor(accent, 0.15);
    }

    private static Color ParseColorSafe(string hex)
    {
        try { return Color.Parse(hex); }
        catch { return Colors.Magenta; }
    }

    private static Color LightenColor(Color c, double factor) =>
        Color.FromArgb(c.A,
            (byte)Math.Min(255, c.R + (255 - c.R) * factor),
            (byte)Math.Min(255, c.G + (255 - c.G) * factor),
            (byte)Math.Min(255, c.B + (255 - c.B) * factor));

    private static Color DarkenColor(Color c, double factor) =>
        Color.FromArgb(c.A,
            (byte)(c.R * (1 - factor)),
            (byte)(c.G * (1 - factor)),
            (byte)(c.B * (1 - factor)));
}
