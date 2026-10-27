using System.Collections.ObjectModel;
using Avalonia.Threading;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Normalization;
using LMP.UI.Features.Player;

namespace LMP.UI.Features.Settings.ViewModels;

public sealed partial class AudioSettingsViewModel : ViewModelBase
{
    private readonly LibraryService _library;
    private readonly AudioEngine _audio;
    private readonly DialogService _dialog;
    private readonly YoutubeProvider _youtube;

    private DispatcherTimer? _normLufsDebounceTimer;
    private DispatcherTimer? _normGainDebounceTimer;
    private DispatcherTimer? _eqBandsDebounceTimer;
    private bool _isLoading;
    private bool _isRevertingNormalization;

    [ObservableProperty] public partial IReadOnlyList<LocalizedItem<AudioQualityPreference>> QualityOptions { get; private set; } = [];
    [ObservableProperty] public partial LocalizedItem<AudioQualityPreference>? SelectedQualityItem { get; set; }

    [ObservableProperty] public partial int MaxVolumeLimit { get; set; }
    [ObservableProperty] public partial bool RememberTrackFormat { get; set; }
    [ObservableProperty] public partial bool VolumeBoostEnabled { get; set; }
    [ObservableProperty] public partial bool AudioNormalizationEnabled { get; set; }
    [ObservableProperty] public partial float NormalizationTargetLufs { get; set; }
    [ObservableProperty] public partial float NormalizationMaxGain { get; set; }

    [ObservableProperty] public partial IReadOnlyList<LocalizedItem<VolumeCurveType>> VolumeCurveOptions { get; private set; } = [];
    [ObservableProperty] public partial LocalizedItem<VolumeCurveType>? SelectedVolumeCurve { get; set; }

    [ObservableProperty] public partial IReadOnlyList<LocalizedItem<NormalizationMode>> NormalizationModeOptions { get; private set; } = [];
    [ObservableProperty] public partial LocalizedItem<NormalizationMode>? SelectedNormalizationMode { get; set; }

    [ObservableProperty] public partial bool EqualizerEnabled { get; set; }
    [ObservableProperty] public partial bool ForceGlobalEqualizer { get; set; }
    [ObservableProperty] public partial bool UseSimple5BandMode { get; set; } = true;
    [ObservableProperty] public partial float EqualizerPreampDb { get; set; }
    [ObservableProperty] public partial float[] EqualizerBandGains { get; private set; } = new float[10];
    [ObservableProperty] public partial string ActiveEqualizerPresetTitle { get; private set; } = "";
    [ObservableProperty] public partial string NewPresetNameInput { get; set; } = "";
    [ObservableProperty] public partial bool ShowPresetRightClickHint { get; private set; }

    public string BandModeBadgeText => UseSimple5BandMode ? "5" : "10";

    [ObservableProperty] public partial bool CanResetEqualizer { get; private set; }

    public string ForceGlobalEqualizerTooltip => ForceGlobalEqualizer
        ? SL["Eq_ForceGlobal_Active_Tip"]
        : SL["Eq_ForceGlobal_Inactive_Tip"];

    public ObservableCollection<EqualizerPresetChipItem> EqualizerPresetsList { get; } = [];

    public IAsyncRelayCommand ShowNormalizationInfoCommand { get; }
    public IRelayCommand ResetEqualizerCommand { get; }
    public IRelayCommand ToggleEqualizerModeCommand { get; }
    public IRelayCommand ToggleForceGlobalEqualizerCommand { get; }
    public IRelayCommand<EqualizerPresetChipItem> SelectEqualizerPresetCommand { get; }
    public IRelayCommand<EqualizerPresetChipItem> CommitRenamePresetCommand { get; }
    public IRelayCommand<EqualizerPresetChipItem> DeleteEqualizerPresetCommand { get; }
    public IRelayCommand SaveInlinePresetCommand { get; }

    public AudioSettingsViewModel(
        LibraryService library,
        AudioEngine audio,
        DialogService dialog,
        YoutubeProvider youtube)
    {
        _library = library;
        _audio = audio;
        _dialog = dialog;
        _youtube = youtube;

        ShowNormalizationInfoCommand = new AsyncRelayCommand(ShowNormalizationInfoAsync);
        ResetEqualizerCommand = new RelayCommand(() =>
         {
             EqualizerPreampDb = 0f;
             SelectEqualizerPresetById(EqualizerPresets.FlatId);
         });
        ToggleEqualizerModeCommand = new RelayCommand(() => UseSimple5BandMode = !UseSimple5BandMode);
        ToggleForceGlobalEqualizerCommand = new RelayCommand(() => ForceGlobalEqualizer = !ForceGlobalEqualizer);
        SelectEqualizerPresetCommand = new RelayCommand<EqualizerPresetChipItem>(p =>
         {
             if (p == null) return;
             if (p.IsSelected && !p.IsBuiltIn)
             {
                 for (int i = 0; i < EqualizerPresetsList.Count; i++)
                     EqualizerPresetsList[i].IsRenaming = ReferenceEquals(EqualizerPresetsList[i], p);
                 p.EditTitle = p.Title;
                 return;
             }
             for (int i = 0; i < EqualizerPresetsList.Count; i++)
                 EqualizerPresetsList[i].IsRenaming = false;
             SelectEqualizerPresetById(p.Id);
         });
        CommitRenamePresetCommand = new RelayCommand<EqualizerPresetChipItem>(CommitRenamePreset);
        DeleteEqualizerPresetCommand = new RelayCommand<EqualizerPresetChipItem>(DeleteCustomPreset);
        SaveInlinePresetCommand = new RelayCommand(SaveInlinePreset);

        RefreshLists();
        LoadSettings();
    }

    public void RefreshLists()
    {
        _isLoading = true;
        try
        {
            var currentCurve = SelectedVolumeCurve?.Value ?? _library.Settings.Audio.VolumeCurve;
            VolumeCurveOptions = LocalizedItem.CreateList<VolumeCurveType>("VolumeCurve_");
            SelectedVolumeCurve = VolumeCurveOptions.FindByValue(currentCurve, 1);

            var currentNormMode = SelectedNormalizationMode?.Value ?? _library.Settings.Audio.NormalizationMode;
            NormalizationModeOptions = LocalizedItem.CreateList<NormalizationMode>("NormMode_");
            SelectedNormalizationMode = NormalizationModeOptions.FindByValue(currentNormMode, 0);

            var currentQuality = SelectedQualityItem?.Value ?? _library.Settings.QualityPreference;
            QualityOptions = LocalizedItem.CreateList<AudioQualityPreference>("AudioQuality_");
            SelectedQualityItem = QualityOptions.FindByValue(currentQuality, 0);
        }
        finally
        {
            _isLoading = false;
        }
    }

    public void LoadSettings()
    {
        _isLoading = true;
        try
        {
            var s = _library.Settings;
            MaxVolumeLimit = s.MaxVolumeLimit;
            RememberTrackFormat = s.RememberTrackFormat;

            VolumeBoostEnabled = s.Audio.VolumeBoostEnabled;
            AudioNormalizationEnabled = s.Audio.NormalizationEnabled;
            NormalizationTargetLufs = s.Audio.NormalizationTargetLufs;
            NormalizationMaxGain = s.Audio.NormalizationMaxGain;

            SelectedNormalizationMode = NormalizationModeOptions.FindByValue(s.Audio.NormalizationMode, 0);
            SelectedVolumeCurve = VolumeCurveOptions.FindByValue(s.Audio.VolumeCurve, 1);
            SelectedQualityItem = QualityOptions.FindByValue(s.QualityPreference, 0);

            EqualizerEnabled = s.Audio.EqualizerEnabled;
            ForceGlobalEqualizer = s.Audio.ForceGlobalEqualizer;
            UseSimple5BandMode = s.Audio.UseSimple5BandMode;
            EqualizerPreampDb = s.Audio.EqualizerPreampDb;
            OnPropertyChanged(nameof(BandModeBadgeText));
            OnPropertyChanged(nameof(ForceGlobalEqualizerTooltip));

            int expectedCount = EqualizerPresets.BuiltInIds.Length + s.Audio.UserEqualizerPresets.Count;
            if (EqualizerPresetsList.Count != expectedCount)
                RebuildEqualizerPresets();

            SyncEqualizerBandsFromSettings();
        }
        finally
        {
            _isLoading = false;
        }
    }

    partial void OnMaxVolumeLimitChanged(int value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.MaxVolumeLimit = value);
        _audio.OnMaxVolumeLimitChanged(value);
    }

    partial void OnRememberTrackFormatChanged(bool value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.RememberTrackFormat = value);
    }

    partial void OnVolumeBoostEnabledChanged(bool value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.Audio.VolumeBoostEnabled = value);
        _audio.UpdateAudioSettings();
    }

    partial void OnAudioNormalizationEnabledChanged(bool value)
    {
        if (_isLoading || _isRevertingNormalization) return;

        if (!value)
        {
            Dispatcher.UIThread.Post(async () =>
            {
                var confirmed = await _dialog.ConfirmAsync(
                    SL["Settings_NormalizationDisable_Title"],
                    SL["Settings_NormalizationDisable_Message"],
                    SL["Common_Disable"],
                    SL["Common_Cancel"]);

                if (!confirmed)
                {
                    _isRevertingNormalization = true;
                    AudioNormalizationEnabled = true;
                    _isRevertingNormalization = false;
                    return;
                }

                _library.UpdateSettings(s => s.Audio.NormalizationEnabled = false);
                _audio.UpdateAudioSettings();
            });
            return;
        }

        _library.UpdateSettings(s => s.Audio.NormalizationEnabled = true);
        _audio.UpdateAudioSettings();
    }

    partial void OnNormalizationTargetLufsChanged(float value)
    {
        if (_isLoading) return;
        _normLufsDebounceTimer?.Stop();
        _normLufsDebounceTimer ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(300),
            DispatcherPriority.Normal,
            (_, _) =>
            {
                _normLufsDebounceTimer?.Stop();
                _library.UpdateSettings(s => s.Audio.NormalizationTargetLufs = NormalizationTargetLufs);
                _audio.UpdateAudioSettings();
            });
        _normLufsDebounceTimer.Start();
    }

    partial void OnNormalizationMaxGainChanged(float value)
    {
        if (_isLoading) return;
        _normGainDebounceTimer?.Stop();
        _normGainDebounceTimer ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(300),
            DispatcherPriority.Normal,
            (_, _) =>
            {
                _normGainDebounceTimer?.Stop();
                _library.UpdateSettings(s => s.Audio.NormalizationMaxGain = NormalizationMaxGain);
                _audio.UpdateAudioSettings();
            });
        _normGainDebounceTimer.Start();
    }

    partial void OnSelectedNormalizationModeChanged(LocalizedItem<NormalizationMode>? value)
    {
        if (_isLoading || value is null) return;
        _library.UpdateSettings(s => s.Audio.NormalizationMode = value.Value);
        _audio.UpdateAudioSettings();
    }

    partial void OnSelectedVolumeCurveChanged(LocalizedItem<VolumeCurveType>? value)
    {
        if (_isLoading || value is null) return;
        _library.UpdateSettings(s => s.Audio.VolumeCurve = value.Value);
        _audio.UpdateAudioSettings();
    }

    partial void OnSelectedQualityItemChanged(LocalizedItem<AudioQualityPreference>? value)
    {
        if (_isLoading || value is null) return;
        _library.UpdateSettings(s => s.QualityPreference = value.Value);
        _youtube.ClearCache();
    }

    partial void OnEqualizerEnabledChanged(bool value)
    {
        if (_isLoading) return;
        _eqBandsDebounceTimer?.Stop();
        _library.UpdateSettings(s => s.Audio.EqualizerEnabled = value);
        _audio.UpdateEqualizerSettings();
    }

    partial void OnForceGlobalEqualizerChanged(bool value)
    {
        OnPropertyChanged(nameof(ForceGlobalEqualizerTooltip));
        if (_isLoading) return;
        _eqBandsDebounceTimer?.Stop();
        _library.UpdateSettings(s => s.Audio.ForceGlobalEqualizer = value);
        _audio.UpdateEqualizerSettings();
    }

    partial void OnUseSimple5BandModeChanged(bool value)
    {
        OnPropertyChanged(nameof(BandModeBadgeText));
        if (_isLoading) return;
        _library.UpdateSettings(s => s.Audio.UseSimple5BandMode = value);
    }

    partial void OnEqualizerPreampDbChanged(float value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.Audio.EqualizerPreampDb = value);
        RefreshCanReset(EqualizerBandGains);
        _audio.UpdateEqualizerSettings();
    }

    public void ApplyCustomBandsFromUi(float[] bands10)
    {
        if (_isLoading) return;

        var audio = _library.Settings.Audio;
        bool presetChanged = !string.Equals(audio.GlobalEqualizerPresetId, EqualizerPresets.CustomId, StringComparison.Ordinal);
        audio.EqualizerEnabled = true;
        audio.CustomEqualizerBands = bands10;
        audio.GlobalEqualizerPresetId = EqualizerPresets.CustomId;

        if (presetChanged && _audio.CurrentTrack is { EqualizerPresetId.Length: > 0 } currentTrack && !ForceGlobalEqualizer)
            _ = _library.SetTrackEqualizerPresetAsync(currentTrack, EqualizerPresets.CustomId);

        _isLoading = true;
        try
        {
            EqualizerEnabled = true;
            if (presetChanged)
                UpdateSelectedChip(EqualizerPresets.CustomId);
            RefreshCanReset(bands10);
        }
        finally
        {
            _isLoading = false;
        }

        _audio.UpdateEqualizerSettings();

        _eqBandsDebounceTimer?.Stop();
        _eqBandsDebounceTimer ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(350),
            DispatcherPriority.Background,
            (_, _) =>
            {
                _eqBandsDebounceTimer?.Stop();
                _isLoading = true;
                try
                {
                    EqualizerBandGains = _library.Settings.Audio.CustomEqualizerBands;
                }
                finally
                {
                    _isLoading = false;
                }

                _library.UpdateSettings(s =>
                {
                    s.Audio.EqualizerEnabled = _library.Settings.Audio.EqualizerEnabled;
                    s.Audio.CustomEqualizerBands = _library.Settings.Audio.CustomEqualizerBands;
                    s.Audio.GlobalEqualizerPresetId = _library.Settings.Audio.GlobalEqualizerPresetId;
                });
            });
        _eqBandsDebounceTimer.Start();
    }

    private void SelectEqualizerPresetById(string presetId)
    {
        _eqBandsDebounceTimer?.Stop();
        _library.UpdateSettings(s =>
        {
            s.Audio.GlobalEqualizerPresetId = presetId;
            if (!string.Equals(presetId, EqualizerPresets.FlatId, StringComparison.Ordinal))
                s.Audio.EqualizerEnabled = true;
        });

        if (_audio.CurrentTrack is { EqualizerPresetId.Length: > 0 } currentTrack && !ForceGlobalEqualizer)
        {
            string? nextTrackPreset = string.Equals(presetId, EqualizerPresets.FlatId, StringComparison.Ordinal)
                ? null
                : presetId;
            _ = _library.SetTrackEqualizerPresetAsync(currentTrack, nextTrackPreset);
        }

        _isLoading = true;
        try
        {
            EqualizerEnabled = _library.Settings.Audio.EqualizerEnabled;
            SyncEqualizerBandsFromSettings();
        }
        finally
        {
            _isLoading = false;
        }

        _audio.UpdateEqualizerSettings();
    }

    private void SyncEqualizerBandsFromSettings()
    {
        var bands = new float[EqualizerProcessor.BandCount];
        string activeId = EqualizerPresets.ResolveEffectiveBands(
            _library.Settings.Audio, track: _audio.CurrentTrack, bands);
        EqualizerBandGains = bands;
        UpdateSelectedChip(activeId);
        RefreshCanReset(bands);
    }

    private void RefreshCanReset(ReadOnlySpan<float> bands)
    {
        bool hasNonZero = MathF.Abs(EqualizerPreampDb) > 0.05f;
        for (int i = 0; i < bands.Length && !hasNonZero; i++)
        {
            if (MathF.Abs(bands[i]) > 0.05f)
            {
                hasNonZero = true;
                break;
            }
        }
        CanResetEqualizer = hasNonZero;
    }

    private void RebuildEqualizerPresets()
    {
        EqualizerPresetsList.Clear();
        var builtIn = EqualizerPresets.BuiltInIds;
        for (int i = 0; i < builtIn.Length; i++)
        {
            string id = builtIn[i];
            string key = EqualizerPresets.GetLocalizationKey(id);
            EqualizerPresetsList.Add(new EqualizerPresetChipItem(id, SL[key], isBuiltIn: true));
        }

        var userPresets = _library.Settings.Audio.UserEqualizerPresets;
        for (int i = 0; i < userPresets.Count; i++)
        {
            var up = userPresets[i];
            EqualizerPresetsList.Add(new EqualizerPresetChipItem(up.Id, up.Name, isBuiltIn: false));
        }
    }

    private void UpdateSelectedChip(string activeId)
    {
        string activeTitle = SL["Eq_Preset_Custom"];
        for (int i = 0; i < EqualizerPresetsList.Count; i++)
        {
            var item = EqualizerPresetsList[i];
            bool match = string.Equals(item.Id, activeId, StringComparison.Ordinal);
            item.IsSelected = match;
            item.IsGlobalPreset = match;
            item.IsTrackPreset = false;
            if (match)
                activeTitle = item.Title;
        }
        ActiveEqualizerPresetTitle = activeTitle;
    }

    private void SaveInlinePreset()
    {
        string name = NewPresetNameInput.Trim();
        if (string.IsNullOrEmpty(name)) return;

        _eqBandsDebounceTimer?.Stop();
        string id = "user_" + Guid.NewGuid().ToString("N")[..8];
        var bandsCopy = new float[EqualizerProcessor.BandCount];
        for (int i = 0; i < EqualizerBandGains.Length && i < bandsCopy.Length; i++)
            bandsCopy[i] = EqualizerBandGains[i];

        _library.UpdateSettings(s =>
        {
            s.Audio.UserEqualizerPresets.Add(new EqualizerPreset
            {
                Id = id,
                Name = name,
                IsBuiltIn = false,
                Bands = bandsCopy
            });
            s.Audio.GlobalEqualizerPresetId = id;
            s.Audio.EqualizerEnabled = true;
        });

        NewPresetNameInput = "";
        ShowPresetRightClickHint = true;
        EqualizerEnabled = true;
        EqualizerPresetsList.Add(new EqualizerPresetChipItem(id, name, isBuiltIn: false));
        UpdateSelectedChip(id);
        _audio.UpdateEqualizerSettings();
    }

    private void DeleteCustomPreset(EqualizerPresetChipItem? item)
    {
        if (item == null || item.IsBuiltIn) return;

        _eqBandsDebounceTimer?.Stop();
        _library.UpdateSettings(s =>
        {
            s.Audio.UserEqualizerPresets.RemoveAll(p => string.Equals(p.Id, item.Id, StringComparison.Ordinal));
            if (string.Equals(s.Audio.GlobalEqualizerPresetId, item.Id, StringComparison.Ordinal))
                s.Audio.GlobalEqualizerPresetId = EqualizerPresets.FlatId;
        });

        if (_audio.CurrentTrack is { } currentTrack && string.Equals(currentTrack.EqualizerPresetId, item.Id, StringComparison.Ordinal))
            _ = _library.SetTrackEqualizerPresetAsync(currentTrack, null);

        EqualizerPresetsList.Remove(item);
        SyncEqualizerBandsFromSettings();
        _audio.UpdateEqualizerSettings();
    }

    private void CommitRenamePreset(EqualizerPresetChipItem? item)
    {
        if (item == null || item.IsBuiltIn) return;
        item.IsRenaming = false;

        string newName = item.EditTitle.Trim();
        if (string.IsNullOrEmpty(newName) || string.Equals(newName, item.Title, StringComparison.Ordinal))
            return;

        item.Title = newName;
        _library.UpdateSettings(s =>
        {
            for (int i = 0; i < s.Audio.UserEqualizerPresets.Count; i++)
            {
                if (string.Equals(s.Audio.UserEqualizerPresets[i].Id, item.Id, StringComparison.Ordinal))
                {
                    s.Audio.UserEqualizerPresets[i].Name = newName;
                    break;
                }
            }
        });

        if (item.IsSelected)
            ActiveEqualizerPresetTitle = newName;
    }

    private async Task ShowNormalizationInfoAsync()
    {
        await _dialog.ShowInfoAsync(
            SL["Settings_NormalizationInfo_Title"],
            SL["Settings_NormalizationInfo_Body"],
            SL["Common_GotIt"]);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _normLufsDebounceTimer?.Stop();
            _normGainDebounceTimer?.Stop();
            _eqBandsDebounceTimer?.Stop();
        }
        base.Dispose(disposing);
    }
}