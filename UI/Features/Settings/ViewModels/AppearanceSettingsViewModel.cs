using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using LMP.UI.Controls;

namespace LMP.UI.Features.Settings.ViewModels;

/// <summary>
/// ViewModel for the Appearance &amp; Theme Editor settings page.
/// Supports built-in and custom user presets, live preview with rollback,
/// WCAG 2.0 contrast analysis, derived token fine-tuning, and JSON clipboard import/export.
/// </summary>
public sealed partial class AppearanceSettingsViewModel : ViewModelBase, IUnsavedChangesGuard
{
    private readonly LibraryService _library;
    private readonly ThemeManagerService _themeManager;
    private readonly NotificationService? _notifications;

    private ThemeSettings _committedTheme = ThemeManagerService.GetDefaultTheme();
    private bool _isLoadingTheme;
    private bool _isLoadingSettings;

    public ObservableCollection<ThemeSettings> ThemePresets { get; } = [];
    public ObservableCollection<ThemePresetChipItem> ThemePresetChips { get; } = [];

    [ObservableProperty] public partial ThemeSettings? SelectedPreset { get; set; }
    [ObservableProperty] public partial string NewPresetNameInput { get; set; } = string.Empty;
    [ObservableProperty] public partial bool LivePreviewEnabled { get; set; } = true;

    // Base palette colors
    [ObservableProperty] public partial Color AccentColor { get; set; }
    [ObservableProperty] public partial Color BgPrimaryColor { get; set; }
    [ObservableProperty] public partial Color BgSecondaryColor { get; set; }
    [ObservableProperty] public partial Color BgElevatedColor { get; set; }
    [ObservableProperty] public partial Color TextPrimaryColor { get; set; }
    [ObservableProperty] public partial Color TextSecondaryColor { get; set; }

    // Derived fine-tunable surface/state tokens
    [ObservableProperty] public partial Color BgHighlightColor { get; set; }
    [ObservableProperty] public partial Color BgHoverColor { get; set; }
    [ObservableProperty] public partial Color BgSkeletonColor { get; set; }
    [ObservableProperty] public partial Color TextMutedColor { get; set; }
    [ObservableProperty] public partial Color BgOverlayColor { get; set; }

    // WCAG 2.0 Contrast diagnostics
    [ObservableProperty] public partial string PrimaryContrastRatioText { get; private set; } = "15.0:1";
    [ObservableProperty] public partial string PrimaryContrastBadgeText { get; private set; } = string.Empty;
    [ObservableProperty] public partial IBrush PrimaryContrastBrush { get; private set; } = Brushes.White;

    [ObservableProperty] public partial string AccentContrastRatioText { get; private set; } = "4.5:1";
    [ObservableProperty] public partial string AccentContrastBadgeText { get; private set; } = string.Empty;
    [ObservableProperty] public partial IBrush AccentContrastBrush { get; private set; } = Brushes.White;
    private readonly SolidColorBrush _accentSampleTextBrush = new(Colors.White);
    private readonly SolidColorBrush _accentSampleBgBrush = new(Colors.Purple);

    public IBrush AccentSampleTextBrush => _accentSampleTextBrush;
    public IBrush AccentSampleBgBrush => _accentSampleBgBrush;

    [ObservableProperty]
    public partial IReadOnlyList<LocalizedItem<TrackAnimationSpeed>> TrackAnimationSpeedOptions { get; private set; } = [];

    [ObservableProperty] public partial LocalizedItem<TrackAnimationSpeed>? SelectedTrackAnimationSpeed { get; set; }
    [ObservableProperty] public partial bool UseWaveAnimation { get; set; }
    [ObservableProperty] public partial bool HasUnsavedThemeChanges { get; set; }

    bool IUnsavedChangesGuard.HasUnsavedChanges => HasUnsavedThemeChanges;
    void IUnsavedChangesGuard.ApplyChanges() => ApplyTheme();
    void IUnsavedChangesGuard.DiscardChanges() => CancelThemeChanges();

    public IRelayCommand ApplyThemeCommand { get; }
    public IRelayCommand CancelThemeChangesCommand { get; }
    public IRelayCommand ResetThemeCommand { get; }
    public IRelayCommand RecalculateDerivedColorsCommand { get; }
    public IRelayCommand CreatePresetCommand { get; }
    public IRelayCommand<ThemePresetChipItem> SelectPresetChipCommand { get; }
    public IRelayCommand<ThemePresetChipItem> BeginRenamePresetCommand { get; }
    public IRelayCommand<ThemePresetChipItem> CommitRenamePresetCommand { get; }
    public IRelayCommand<ThemePresetChipItem> DeletePresetCommand { get; }
    public IAsyncRelayCommand ExportThemeToClipboardCommand { get; }
    public IAsyncRelayCommand ImportThemeFromClipboardCommand { get; }

    public AppearanceSettingsViewModel(
        LibraryService library,
        ThemeManagerService themeManager,
        NotificationService? notifications = null)
    {
        _library = library;
        _themeManager = themeManager;
        _notifications = notifications;

        ApplyThemeCommand = new RelayCommand(ApplyTheme);
        CancelThemeChangesCommand = new RelayCommand(CancelThemeChanges);
        ResetThemeCommand = new RelayCommand(ResetTheme);
        RecalculateDerivedColorsCommand = new RelayCommand(RecalculateDerivedColors);
        CreatePresetCommand = new RelayCommand(CreateNewCustomPreset);
        SelectPresetChipCommand = new RelayCommand<ThemePresetChipItem>(SelectPresetChip);
        BeginRenamePresetCommand = new RelayCommand<ThemePresetChipItem>(BeginRenamePreset);
        CommitRenamePresetCommand = new RelayCommand<ThemePresetChipItem>(CommitRenamePreset);
        DeletePresetCommand = new RelayCommand<ThemePresetChipItem>(DeleteCustomPreset);
        ExportThemeToClipboardCommand = new AsyncRelayCommand(ExportThemeToClipboardAsync);
        ImportThemeFromClipboardCommand = new AsyncRelayCommand(ImportThemeFromClipboardAsync);

        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;

        EnsureActiveCustomThemeRegistered();
        RefreshPresets();
        RefreshLists();
        LoadSettings();
    }

    private void OnLanguageChanged(object? sender, string langCode)
    {
        RefreshLists();
        UpdateContrastMetrics();
        OnPropertyChanged(nameof(L));
    }

    private void EnsureActiveCustomThemeRegistered()
    {
        var active = _themeManager.GetCurrentTheme();
        var builtIns = ThemeManagerService.GetBuiltInPresets();
        for (int i = 0; i < builtIns.Count; i++)
        {
            if (string.Equals(builtIns[i].Name, active.Name, StringComparison.OrdinalIgnoreCase))
                return;
        }

        var userThemes = _library.Settings.UserThemes;
        for (int i = 0; i < userThemes.Count; i++)
        {
            if (string.Equals(userThemes[i].Name, active.Name, StringComparison.OrdinalIgnoreCase))
                return;
        }

        _library.UpdateSettings(s => s.UserThemes.Add(active.Clone(isBuiltIn: false)));
    }

    public void RefreshPresets()
    {
        ThemePresets.Clear();
        ThemePresetChips.Clear();

        var builtIns = ThemeManagerService.GetBuiltInPresets();
        for (int i = 0; i < builtIns.Count; i++)
        {
            var preset = builtIns[i];
            ThemePresets.Add(preset);
            ThemePresetChips.Add(new ThemePresetChipItem(preset));
        }

        var userThemes = _library.Settings.UserThemes;
        for (int i = 0; i < userThemes.Count; i++)
        {
            var custom = userThemes[i].Clone(isBuiltIn: false);
            bool exists = false;
            for (int j = 0; j < ThemePresets.Count; j++)
            {
                if (string.Equals(ThemePresets[j].Name, custom.Name, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
            {
                ThemePresets.Add(custom);
                ThemePresetChips.Add(new ThemePresetChipItem(custom));
            }
        }

        SyncChipSelection(SelectedPreset?.Name ?? _committedTheme.Name);
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

            RefreshPresets();
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
            _committedTheme = _themeManager.GetCurrentTheme().Clone();
            ApplyThemeToColorPickers(_committedTheme);

            ThemeSettings? matchingPreset = null;
            for (int i = 0; i < ThemePresets.Count; i++)
            {
                if (string.Equals(ThemePresets[i].Name, _committedTheme.Name, StringComparison.OrdinalIgnoreCase))
                {
                    matchingPreset = ThemePresets[i];
                    break;
                }
            }

            SelectedPreset = matchingPreset ?? (ThemePresets.Count > 0 ? ThemePresets[0] : null);
            SyncChipSelection(SelectedPreset?.Name);
            HasUnsavedThemeChanges = false;
            UpdateContrastMetrics();
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
            AccentColor = ThemeManagerService.ParseColorSafe(theme.AccentColor);
            BgPrimaryColor = ThemeManagerService.ParseColorSafe(theme.BgPrimary);
            BgSecondaryColor = ThemeManagerService.ParseColorSafe(theme.BgSecondary);
            BgElevatedColor = ThemeManagerService.ParseColorSafe(theme.BgElevated);
            TextPrimaryColor = ThemeManagerService.ParseColorSafe(theme.TextPrimary);
            TextSecondaryColor = ThemeManagerService.ParseColorSafe(theme.TextSecondary);

            BgHighlightColor = ThemeManagerService.ParseColorSafe(
                theme.BgHighlight,
                ThemeManagerService.DeriveBgHighlight(BgSecondaryColor));
            BgHoverColor = ThemeManagerService.ParseColorSafe(
                theme.BgHover,
                ThemeManagerService.DeriveBgHover(BgSecondaryColor));
            BgSkeletonColor = ThemeManagerService.ParseColorSafe(
                theme.BgSkeleton,
                ThemeManagerService.DeriveBgSkeleton(BgSecondaryColor));
            TextMutedColor = ThemeManagerService.ParseColorSafe(
                theme.TextMuted,
                ThemeManagerService.DeriveTextMuted(TextSecondaryColor));
            BgOverlayColor = ThemeManagerService.ParseColorSafe(
                theme.BgOverlay,
                ThemeManagerService.DeriveBgOverlay(BgPrimaryColor));
        }
        finally
        {
            _isLoadingTheme = false;
        }
    }

    private void SyncChipSelection(string? selectedName)
    {
        for (int i = 0; i < ThemePresetChips.Count; i++)
        {
            var chip = ThemePresetChips[i];
            chip.IsSelected = !string.IsNullOrEmpty(selectedName)
                && string.Equals(chip.Name, selectedName, StringComparison.OrdinalIgnoreCase);
            if (!chip.IsSelected)
                chip.IsRenaming = false;
        }
    }

    partial void OnSelectedPresetChanged(ThemeSettings? value)
    {
        if (_isLoadingSettings || _isLoadingTheme || value is null) return;
        NewPresetNameInput = string.Empty;
        SyncChipSelection(value.Name);
        ApplyThemeToColorPickers(value);
        OnEditorThemeModified();
    }

    partial void OnLivePreviewEnabledChanged(bool value)
    {
        if (_isLoadingSettings || _isLoadingTheme) return;

        if (value)
        {
            _themeManager.ApplyTheme(BuildThemeFromEditor(), isLivePreview: true);
        }
        else if (HasUnsavedThemeChanges)
        {
            _themeManager.ApplyTheme(_committedTheme, isLivePreview: true);
        }
    }

    partial void OnAccentColorChanged(Color value) => OnEditorThemeModified();

    partial void OnBgPrimaryColorChanged(Color value)
    {
        if (!_isLoadingSettings && !_isLoadingTheme)
        {
            _isLoadingTheme = true;
            BgOverlayColor = ThemeManagerService.DeriveBgOverlay(value);
            _isLoadingTheme = false;
        }
        OnEditorThemeModified();
    }

    partial void OnBgSecondaryColorChanged(Color value)
    {
        if (!_isLoadingSettings && !_isLoadingTheme)
        {
            _isLoadingTheme = true;
            BgHighlightColor = ThemeManagerService.DeriveBgHighlight(value);
            BgHoverColor = ThemeManagerService.DeriveBgHover(value);
            BgSkeletonColor = ThemeManagerService.DeriveBgSkeleton(value);
            _isLoadingTheme = false;
        }
        OnEditorThemeModified();
    }

    partial void OnBgElevatedColorChanged(Color value) => OnEditorThemeModified();
    partial void OnTextPrimaryColorChanged(Color value) => OnEditorThemeModified();

    partial void OnTextSecondaryColorChanged(Color value)
    {
        if (!_isLoadingSettings && !_isLoadingTheme)
        {
            _isLoadingTheme = true;
            TextMutedColor = ThemeManagerService.DeriveTextMuted(value);
            _isLoadingTheme = false;
        }
        OnEditorThemeModified();
    }

    partial void OnBgHighlightColorChanged(Color value) => OnEditorThemeModified();
    partial void OnBgHoverColorChanged(Color value) => OnEditorThemeModified();
    partial void OnBgSkeletonColorChanged(Color value) => OnEditorThemeModified();
    partial void OnTextMutedColorChanged(Color value) => OnEditorThemeModified();
    partial void OnBgOverlayColorChanged(Color value) => OnEditorThemeModified();

    partial void OnUseWaveAnimationChanged(bool value)
    {
        if (_isLoadingSettings) return;
        _library.UpdateSettings(s => s.UseWaveAnimation = value);
        AudioWaveBorder.ConfigureGlobal(value, SelectedTrackAnimationSpeed?.Value ?? TrackAnimationSpeed.Medium);
        Log.Info($"[Settings] UseWaveAnimation -> {value}");
    }

    partial void OnSelectedTrackAnimationSpeedChanged(LocalizedItem<TrackAnimationSpeed>? value)
    {
        if (_isLoadingSettings || value is null) return;
        _library.UpdateSettings(s => s.TrackAnimationSpeed = value.Value);
        AudioWaveBorder.ConfigureGlobal(UseWaveAnimation, value.Value);
        Log.Info($"[Settings] TrackAnimationSpeed -> {value.Value}");
    }

    private void OnEditorThemeModified()
    {
        if (_isLoadingSettings || _isLoadingTheme)
            return;

        UpdateContrastMetrics();

        var previewTheme = BuildThemeFromEditor();
        HasUnsavedThemeChanges = !previewTheme.Equals(_committedTheme)
                              || !previewTheme.HasSameColors(_committedTheme);

        if (LivePreviewEnabled)
        {
            _themeManager.ApplyTheme(previewTheme, isLivePreview: true);
        }
    }

    private void RecalculateDerivedColors()
    {
        _isLoadingTheme = true;
        try
        {
            BgHighlightColor = ThemeManagerService.DeriveBgHighlight(BgSecondaryColor);
            BgHoverColor = ThemeManagerService.DeriveBgHover(BgSecondaryColor);
            BgSkeletonColor = ThemeManagerService.DeriveBgSkeleton(BgSecondaryColor);
            TextMutedColor = ThemeManagerService.DeriveTextMuted(TextSecondaryColor);
            BgOverlayColor = ThemeManagerService.DeriveBgOverlay(BgPrimaryColor);
        }
        finally
        {
            _isLoadingTheme = false;
        }

        OnEditorThemeModified();
    }

    private void UpdateContrastMetrics()
    {
        double primaryRatio = ThemeManagerService.GetContrastRatio(TextPrimaryColor, BgPrimaryColor);
        var primaryLevel = ThemeManagerService.EvaluateContrastLevel(primaryRatio);
        PrimaryContrastRatioText = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{primaryRatio:F1}:1");
        PrimaryContrastBadgeText = GetContrastBadgeLabel(primaryLevel);
        PrimaryContrastBrush = GetContrastBadgeBrush(primaryLevel);

        var accentText = ThemeManagerService.GetContrastingTextColor(AccentColor);
        double accentRatio = ThemeManagerService.GetContrastRatio(accentText, AccentColor);
        var accentLevel = ThemeManagerService.EvaluateContrastLevel(accentRatio);
        AccentContrastRatioText = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{accentRatio:F1}:1");
        AccentContrastBadgeText = GetContrastBadgeLabel(accentLevel);
        AccentContrastBrush = GetContrastBadgeBrush(accentLevel);
        _accentSampleTextBrush.Color = accentText;
        _accentSampleBgBrush.Color = AccentColor;
    }

    private static string GetContrastBadgeLabel(ThemeManagerService.WcagContrastLevel level) => level switch
    {
        ThemeManagerService.WcagContrastLevel.PassAAA => SL["Theme_Contrast_AAA"],
        ThemeManagerService.WcagContrastLevel.PassAA => SL["Theme_Contrast_AA"],
        ThemeManagerService.WcagContrastLevel.Warning => SL["Theme_Contrast_Warn"],
        _ => SL["Theme_Contrast_Fail"]
    };

    private static readonly ImmutableSolidColorBrush ContrastPassBrush = new(Color.Parse("#2ECC71"));
    private static readonly ImmutableSolidColorBrush ContrastWarnBrush = new(Color.Parse("#FFB86C"));
    private static readonly ImmutableSolidColorBrush ContrastFailBrush = new(Color.Parse("#FF5555"));

    private static ImmutableSolidColorBrush GetContrastBadgeBrush(ThemeManagerService.WcagContrastLevel level) => level switch
    {
        ThemeManagerService.WcagContrastLevel.PassAAA or ThemeManagerService.WcagContrastLevel.PassAA => ContrastPassBrush,
        ThemeManagerService.WcagContrastLevel.Warning => ContrastWarnBrush,
        _ => ContrastFailBrush
    };

    private ThemeSettings BuildThemeFromEditor(string? explicitName = null)
    {
        if (explicitName is null
            && SelectedPreset is { IsBuiltIn: true } builtInPreset
            && IsMatchingPresetColors(builtInPreset))
        {
            return builtInPreset.Clone(isBuiltIn: true);
        }

        string targetName = explicitName
            ?? (SelectedPreset is { IsBuiltIn: false } customPreset
                ? customPreset.Name
                : SL["Theme_Custom"]);

        byte overlayAlpha = BgOverlayColor.A is > 0 and < 255 ? BgOverlayColor.A : (byte)0xCC;
        var overlayColor = Color.FromArgb(overlayAlpha, BgOverlayColor.R, BgOverlayColor.G, BgOverlayColor.B);

        return new ThemeSettings
        {
            Name = targetName,
            IsBuiltIn = false,
            AccentColor = ThemeManagerService.FormatHexColor(AccentColor),
            AccentHover = ThemeManagerService.FormatHexColor(ThemeManagerService.SmartAccentHover(AccentColor)),
            BgPrimary = ThemeManagerService.FormatHexColor(BgPrimaryColor),
            BgSecondary = ThemeManagerService.FormatHexColor(BgSecondaryColor),
            BgElevated = ThemeManagerService.FormatHexColor(BgElevatedColor),
            BgHighlight = ThemeManagerService.FormatHexColor(BgHighlightColor),
            BgHover = ThemeManagerService.FormatHexColor(BgHoverColor),
            BgSkeleton = ThemeManagerService.FormatHexColor(BgSkeletonColor),
            BgSkeletonDeep = ThemeManagerService.FormatHexColor(ThemeManagerService.DeriveBgSkeletonDeep(BgSecondaryColor)),
            BgOverlay = ThemeManagerService.FormatArgbHexColor(overlayColor),
            TextPrimary = ThemeManagerService.FormatHexColor(TextPrimaryColor),
            TextSecondary = ThemeManagerService.FormatHexColor(TextSecondaryColor),
            TextMuted = ThemeManagerService.FormatHexColor(TextMutedColor),
            TextDark = ThemeManagerService.FormatHexColor(BgPrimaryColor),
            SystemError = _committedTheme.SystemError,
            SystemErrorBg = _committedTheme.SystemErrorBg,
            SystemInfoBlue = _committedTheme.SystemInfoBlue,
            SystemWarnOrange = _committedTheme.SystemWarnOrange
        };
    }

    private void SelectPresetChip(ThemePresetChipItem? chip)
    {
        if (chip is null) return;

        if (chip.IsSelected && !chip.IsBuiltIn && !HasUnsavedThemeChanges)
        {
            BeginRenamePreset(chip);
            return;
        }

        CancelAllPresetRenames();
        if (ReferenceEquals(SelectedPreset, chip.Theme) || Equals(SelectedPreset, chip.Theme))
        {
            NewPresetNameInput = string.Empty;
            SyncChipSelection(chip.Name);
            ApplyThemeToColorPickers(chip.Theme);
            OnEditorThemeModified();
            return;
        }

        SelectedPreset = chip.Theme;
    }

    private void CreateNewCustomPreset()
    {
        CancelAllPresetRenames();

        string rawInput = NewPresetNameInput.Trim();
        string uniqueName = GenerateUniqueCustomThemeName(
            string.IsNullOrEmpty(rawInput) ? SL["Theme_Custom"] : rawInput);

        var newTheme = BuildThemeFromEditor(uniqueName);
        NewPresetNameInput = string.Empty;

        SaveOrUpdateCustomPresetInSettings(newTheme);
        CommitAndApplyTheme(newTheme);
    }

    private void BeginRenamePreset(ThemePresetChipItem? chip)
    {
        if (chip is null || chip.IsBuiltIn) return;

        for (int i = 0; i < ThemePresetChips.Count; i++)
        {
            ThemePresetChips[i].IsRenaming = ReferenceEquals(ThemePresetChips[i], chip);
        }

        chip.EditName = chip.Name;
    }

    private void CancelAllPresetRenames()
    {
        for (int i = 0; i < ThemePresetChips.Count; i++)
        {
            ThemePresetChips[i].IsRenaming = false;
        }
    }

    private void CommitRenamePreset(ThemePresetChipItem? chip)
    {
        if (chip is null || chip.IsBuiltIn) return;
        chip.IsRenaming = false;

        string candidate = chip.EditName.Trim();
        if (string.IsNullOrEmpty(candidate) || string.Equals(candidate, chip.Name, StringComparison.Ordinal))
            return;

        if (IsBuiltInThemeName(candidate))
            candidate = GenerateUniqueCustomThemeName(candidate);

        string oldName = chip.Name;
        chip.Name = candidate;
        chip.Theme.Name = candidate;

        _library.UpdateSettings(s =>
        {
            for (int i = 0; i < s.UserThemes.Count; i++)
            {
                if (string.Equals(s.UserThemes[i].Name, oldName, StringComparison.OrdinalIgnoreCase))
                {
                    s.UserThemes[i].Name = candidate;
                    break;
                }
            }
        });

        if (string.Equals(_committedTheme.Name, oldName, StringComparison.OrdinalIgnoreCase))
        {
            _committedTheme.Name = candidate;
            _themeManager.SaveTheme(_committedTheme);
        }
    }

    private void DeleteCustomPreset(ThemePresetChipItem? chip)
    {
        if (chip is null || chip.IsBuiltIn) return;

        bool wasSelected = chip.IsSelected
            || string.Equals(_committedTheme.Name, chip.Name, StringComparison.OrdinalIgnoreCase);

        _library.UpdateSettings(s =>
        {
            s.UserThemes.RemoveAll(t => string.Equals(t.Name, chip.Name, StringComparison.OrdinalIgnoreCase));
        });

        ThemePresets.Remove(chip.Theme);
        ThemePresetChips.Remove(chip);

        if (wasSelected)
        {
            var fallback = ThemeManagerService.GetDefaultTheme();
            CommitAndApplyTheme(fallback);
        }
    }

    private void ApplyTheme()
    {
        CancelAllPresetRenames();

        string? requestedName = null;
        if (!string.IsNullOrWhiteSpace(NewPresetNameInput))
        {
            requestedName = GenerateUniqueCustomThemeName(NewPresetNameInput.Trim());
            NewPresetNameInput = string.Empty;
        }

        var theme = BuildThemeFromEditor(requestedName);
        if (!theme.IsBuiltIn)
        {
            if (IsBuiltInThemeName(theme.Name))
                theme.Name = GenerateUniqueCustomThemeName(SL["Theme_Custom"]);

            SaveOrUpdateCustomPresetInSettings(theme);
        }

        CommitAndApplyTheme(theme);
    }

    private void CommitAndApplyTheme(ThemeSettings theme)
    {
        _committedTheme = theme.Clone();
        _themeManager.SaveTheme(_committedTheme);
        _themeManager.ApplyTheme(_committedTheme);
        HasUnsavedThemeChanges = false;

        Log.Info($"[Settings] Theme applied: '{_committedTheme.Name}' (builtIn={_committedTheme.IsBuiltIn}, accent={_committedTheme.AccentColor}, bg={_committedTheme.BgPrimary})");

        RefreshPresets();

        _isLoadingTheme = true;
        try
        {
            ThemeSettings? match = null;
            for (int i = 0; i < ThemePresets.Count; i++)
            {
                if (string.Equals(ThemePresets[i].Name, _committedTheme.Name, StringComparison.OrdinalIgnoreCase))
                {
                    match = ThemePresets[i];
                    break;
                }
            }

            SelectedPreset = match ?? (ThemePresets.Count > 0 ? ThemePresets[0] : null);
            ApplyThemeToColorPickers(_committedTheme);
            SyncChipSelection(_committedTheme.Name);
            UpdateContrastMetrics();
        }
        finally
        {
            _isLoadingTheme = false;
        }
    }

    private void CancelThemeChanges()
    {
        CancelAllPresetRenames();
        NewPresetNameInput = string.Empty;
        _themeManager.ApplyTheme(_committedTheme);

        _isLoadingTheme = true;
        try
        {
            ApplyThemeToColorPickers(_committedTheme);

            ThemeSettings? match = null;
            for (int i = 0; i < ThemePresets.Count; i++)
            {
                if (string.Equals(ThemePresets[i].Name, _committedTheme.Name, StringComparison.OrdinalIgnoreCase))
                {
                    match = ThemePresets[i];
                    break;
                }
            }

            SelectedPreset = match ?? (ThemePresets.Count > 0 ? ThemePresets[0] : null);
            SyncChipSelection(_committedTheme.Name);
            HasUnsavedThemeChanges = false;
            UpdateContrastMetrics();
        }
        finally
        {
            _isLoadingTheme = false;
        }

        Log.Info($"[Settings] Theme preview changes rolled back to '{_committedTheme.Name}'");
    }

    private void ResetTheme()
    {
        CancelAllPresetRenames();
        NewPresetNameInput = string.Empty;
        _themeManager.ResetToDefault();
        LoadThemeColors();
        Log.Info("[Settings] Theme reset to default");
    }

    private async Task ExportThemeToClipboardAsync()
    {
        var theme = BuildThemeFromEditor();
        string json = ThemeManagerService.ExportThemeJson(theme);
        await Clipboard.SetTextAsync(json);

        if (_notifications is not null)
        {
            await _notifications.ShowToastAsync(
                titleKey: "Common_Copied",
                messageKey: "Theme_ExportedToast",
                severity: NotificationSeverity.Success,
                durationMs: 3000);
        }
    }

    private async Task ImportThemeFromClipboardAsync()
    {
        string? raw = await Clipboard.GetTextAsync();
        if (!ThemeManagerService.TryImportThemeJson(raw, out var imported) || imported is null)
        {
            if (_notifications is not null)
            {
                await _notifications.ShowToastAsync(
                    titleKey: "Dialog_Warning_Title",
                    messageKey: "Theme_ImportInvalidToast",
                    severity: NotificationSeverity.Warning,
                    durationMs: 4000);
            }
            return;
        }

        string importedName = IsBuiltInThemeName(imported.Name)
            ? GenerateUniqueCustomThemeName(imported.Name)
            : imported.Name;

        imported.Name = importedName;
        NewPresetNameInput = importedName;
        ApplyThemeToColorPickers(imported);
        OnEditorThemeModified();

        if (_notifications is not null)
        {
            await _notifications.ShowToastAsync(
                titleKey: "Dialog_Success",
                messageKey: "Theme_ImportedToast",
                severity: NotificationSeverity.Success,
                durationMs: 3000);
        }
    }

    private void SaveOrUpdateCustomPresetInSettings(ThemeSettings customTheme)
    {
        var clone = customTheme.Clone(isBuiltIn: false);
        _library.UpdateSettings(s =>
        {
            for (int i = 0; i < s.UserThemes.Count; i++)
            {
                if (string.Equals(s.UserThemes[i].Name, clone.Name, StringComparison.OrdinalIgnoreCase))
                {
                    s.UserThemes[i] = clone;
                    return;
                }
            }

            s.UserThemes.Add(clone);
        });
    }

    private static bool IsBuiltInThemeName(string name)
    {
        var builtIns = ThemeManagerService.GetBuiltInPresets();
        for (int i = 0; i < builtIns.Count; i++)
        {
            if (string.Equals(builtIns[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private string GenerateUniqueCustomThemeName(string baseName)
    {
        if (!IsBuiltInThemeName(baseName) && !PresetNameExists(baseName))
            return baseName;

        for (int idx = 2; idx < 1000; idx++)
        {
            string candidate = $"{baseName} {idx}";
            if (!IsBuiltInThemeName(candidate) && !PresetNameExists(candidate))
                return candidate;
        }

        return $"{baseName} {Guid.NewGuid().ToString("N")[..4]}";
    }

    private bool PresetNameExists(string name)
    {
        for (int i = 0; i < ThemePresets.Count; i++)
        {
            if (string.Equals(ThemePresets[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private bool IsMatchingPresetColors(ThemeSettings preset)
    {
        byte overlayAlpha = BgOverlayColor.A is > 0 and < 255 ? BgOverlayColor.A : (byte)0xCC;
        var overlayColor = Color.FromArgb(overlayAlpha, BgOverlayColor.R, BgOverlayColor.G, BgOverlayColor.B);

        return AccentColor == ThemeManagerService.ParseColorSafe(preset.AccentColor) &&
               BgPrimaryColor == ThemeManagerService.ParseColorSafe(preset.BgPrimary) &&
               BgSecondaryColor == ThemeManagerService.ParseColorSafe(preset.BgSecondary) &&
               BgElevatedColor == ThemeManagerService.ParseColorSafe(preset.BgElevated) &&
               TextPrimaryColor == ThemeManagerService.ParseColorSafe(preset.TextPrimary) &&
               TextSecondaryColor == ThemeManagerService.ParseColorSafe(preset.TextSecondary) &&
               BgHighlightColor == ThemeManagerService.ParseColorSafe(preset.BgHighlight) &&
               BgHoverColor == ThemeManagerService.ParseColorSafe(preset.BgHover) &&
               BgSkeletonColor == ThemeManagerService.ParseColorSafe(preset.BgSkeleton) &&
               TextMutedColor == ThemeManagerService.ParseColorSafe(preset.TextMuted) &&
               overlayColor == ThemeManagerService.ParseColorSafe(preset.BgOverlay, ThemeManagerService.DeriveBgOverlay(BgPrimaryColor));
    }
}

/// <summary>
/// Interactive chip item representing a built-in or user-saved theme preset.
/// </summary>
public sealed partial class ThemePresetChipItem : ObservableObject
{
    public ThemeSettings Theme { get; }
    public bool IsBuiltIn => Theme.IsBuiltIn;
    public bool IsCustom => !Theme.IsBuiltIn;
    public LocalizationService L => LocalizationService.Instance;

    [ObservableProperty] public partial string Name { get; set; }
    [ObservableProperty] public partial string EditName { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRename))]
    public partial bool IsSelected { get; set; }

    [ObservableProperty] public partial bool IsRenaming { get; set; }

    public IBrush AccentBrush { get; private set; }
    public IBrush BgPrimaryBrush { get; private set; }

    public bool CanRename => IsSelected && !IsBuiltIn;

    public string? ChipHint => IsBuiltIn
        ? Name
        : LocalizationService.Instance["Theme_Chip_Hint"];

    public ThemePresetChipItem(ThemeSettings theme)
    {
        Theme = theme;
        Name = theme.Name;
        EditName = theme.Name;
        AccentBrush = new ImmutableSolidColorBrush(ThemeManagerService.ParseColorSafe(theme.AccentColor));
        BgPrimaryBrush = new ImmutableSolidColorBrush(ThemeManagerService.ParseColorSafe(theme.BgPrimary));
    }
}