using System.Collections.ObjectModel;
using Avalonia.Threading;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Normalization;
using LMP.UI.Controls;

namespace LMP.UI.Features.Settings.ViewModels;

public sealed partial class AudioSettingsViewModel : ViewModelBase
{
    [Flags]
    private enum PendingAudioChanges : uint
    {
        None = 0,
        MaxVolume = 1 << 0,
        NormLufs = 1 << 1,
        NormGain = 1 << 2,
        EqPreamp = 1 << 3
    }

    private readonly LibraryService _library;
    private readonly AudioEngine _audio;
    private readonly DialogService _dialog;
    private readonly YoutubeProvider _youtube;

    private DispatcherTimer? _debounceTimer;
    private PendingAudioChanges _pendingChanges;
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

    private string? _renamingPresetId;

    [ObservableProperty] public partial bool EqualizerEnabled { get; set; }
    [ObservableProperty] public partial bool UseSimple5BandMode { get; set; } = true;
    [ObservableProperty] public partial float EqualizerPreampDb { get; set; }
    [ObservableProperty] public partial float[] EqualizerBandGains { get; private set; } = new float[10];
    [ObservableProperty] public partial string ActiveEqualizerPresetTitle { get; private set; } = "";
    [ObservableProperty] public partial string NewPresetNameInput { get; set; } = "";
    [ObservableProperty] public partial bool ShowPresetRightClickHint { get; private set; }
    [ObservableProperty] public partial bool HasTrack { get; private set; }
    [ObservableProperty] public partial bool IsTrackModeHighlighted { get; private set; }

    public string BandModeBadgeText => UseSimple5BandMode ? "5" : "10";

    [ObservableProperty] public partial bool CanResetEqualizer { get; private set; }

    public string TrackEqualizerTooltip =>
        EqualizerPresetChipItem.FormatTrackEqualizerTooltip(
            _audio.CurrentTrack?.EqualizerPresetId,
            IsTrackModeHighlighted,
            EqualizerPresetsList);

    public ObservableCollection<EqualizerPresetChipItem> EqualizerPresetsList { get; } = [];

    public event Action? PresetInputFocusRequested;

    public IAsyncRelayCommand ShowNormalizationInfoCommand { get; }
    public IRelayCommand ResetEqualizerCommand { get; }
    public IRelayCommand ToggleEqualizerModeCommand { get; }
    public IRelayCommand ToggleTrackEqualizerModeCommand { get; }
    public IRelayCommand<EqualizerPresetChipItem> SelectEqualizerPresetCommand { get; }
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
            if (!CanResetEqualizer) return;
            _audio.ResetEqualizer();
        });
        ToggleEqualizerModeCommand = new RelayCommand(() => UseSimple5BandMode = !UseSimple5BandMode);
        ToggleTrackEqualizerModeCommand = new RelayCommand(() =>
        {
            if (!HasTrack) return;
            _audio.ToggleTrackEqualizerMode();
        });
        SelectEqualizerPresetCommand = new RelayCommand<EqualizerPresetChipItem>(p =>
        {
            if (p == null) return;
            if (p.IsSelected && !p.IsBuiltIn)
            {
                _renamingPresetId = p.Id;
                NewPresetNameInput = p.Title;
                PresetInputFocusRequested?.Invoke();
                return;
            }

            _renamingPresetId = null;
            _audio.SelectEqualizerPreset(p.Id);
        });
        DeleteEqualizerPresetCommand = new RelayCommand<EqualizerPresetChipItem>(p =>
        {
            if (p == null || p.IsBuiltIn) return;
            if (string.Equals(_renamingPresetId, p.Id, StringComparison.Ordinal))
            {
                _renamingPresetId = null;
                NewPresetNameInput = "";
            }
            _audio.DeleteUserEqualizerPreset(p.Id);
        });
        SaveInlinePresetCommand = new RelayCommand(() =>
        {
            if (!string.IsNullOrEmpty(_renamingPresetId))
            {
                string targetId = _renamingPresetId;
                _renamingPresetId = null;
                _audio.RenameUserEqualizerPreset(targetId, NewPresetNameInput);
                NewPresetNameInput = "";
                return;
            }

            if (_audio.SaveUserEqualizerPreset(NewPresetNameInput, EqualizerBandGains) != null)
            {
                NewPresetNameInput = "";
                ShowPresetRightClickHint = true;
            }
        });

        _audio.OnEqualizerChanged += SyncEqualizerStateFromEngine;
        _audio.OnTrackChanged += OnAudioTrackChanged;

        RefreshLists();
        LoadSettings();
    }

    private void OnAudioTrackChanged(TrackInfo? _)
    {
        SyncEqualizerStateFromEngine();
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

            EqualizerPresetsList.Clear();
        }
        finally
        {
            _isLoading = false;
        }

        SyncEqualizerStateFromEngine();
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
        }
        finally
        {
            _isLoading = false;
        }

        SyncEqualizerStateFromEngine();
    }

    public void CancelPresetRename()
    {
        if (_renamingPresetId != null)
        {
            _renamingPresetId = null;
            NewPresetNameInput = "";
        }
    }

    partial void OnNewPresetNameInputChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            _renamingPresetId = null;
    }

    private void SyncEqualizerStateFromEngine()
    {
        _isLoading = true;
        try
        {
            var audioSettings = _library.Settings.Audio;
            var currentTrack = _audio.CurrentTrack;

            HasTrack = currentTrack != null;
            EqualizerEnabled = audioSettings.EqualizerEnabled;
            UseSimple5BandMode = audioSettings.UseSimple5BandMode;
            EqualizerPreampDb = audioSettings.EqualizerPreampDb;

            var bands = new float[EqualizerProcessor.BandCount];
            string activeId = EqualizerPresets.ResolveEffectiveBands(audioSettings, currentTrack, bands);

            if (_renamingPresetId != null
                && !string.Equals(_renamingPresetId, activeId, StringComparison.Ordinal))
            {
                CancelPresetRename();
            }

            EqualizerBandGains = bands;

            ActiveEqualizerPresetTitle = EqualizerPresetChipItem.SyncPresetsList(
                EqualizerPresetsList,
                audioSettings,
                currentTrack,
                activeId);

            IsTrackModeHighlighted = audioSettings.EqualizerEnabled && _audio.IsTrackEqualizerActive;
            CanResetEqualizer = EqualizerPresets.HasNonZeroBands(bands, audioSettings.EqualizerPreampDb);

            OnPropertyChanged(nameof(BandModeBadgeText));
            OnPropertyChanged(nameof(TrackEqualizerTooltip));
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void ScheduleDebounce(PendingAudioChanges flag)
    {
        _pendingChanges |= flag;
        _debounceTimer?.Stop();
        _debounceTimer ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(350),
            DispatcherPriority.Normal,
            (_, _) => FlushPendingChanges());
        _debounceTimer.Start();
    }

    private void FlushPendingChanges()
    {
        _debounceTimer?.Stop();
        var pending = _pendingChanges;
        if (pending == PendingAudioChanges.None) return;
        _pendingChanges = PendingAudioChanges.None;

        if ((pending & (PendingAudioChanges.NormLufs | PendingAudioChanges.NormGain)) != 0)
            _audio.UpdateAudioSettings();

        if ((pending & PendingAudioChanges.MaxVolume) != 0)
            Log.Info($"[Settings] MaxVolumeLimit -> {MaxVolumeLimit}% (gain={_audio.CurrentGain:F4}x)");

        if ((pending & PendingAudioChanges.NormLufs) != 0)
            Log.Info($"[Settings] NormalizationTargetLufs -> {NormalizationTargetLufs:F1} LUFS");

        if ((pending & PendingAudioChanges.NormGain) != 0)
            Log.Info($"[Settings] NormalizationMaxGain -> {NormalizationMaxGain:F1} dB");

        if ((pending & PendingAudioChanges.EqPreamp) != 0)
            Log.Info($"[Settings] EqualizerPreampDb -> {EqualizerPreampDb:+0.0;-0.0;0.0} dB");
    }

    partial void OnMaxVolumeLimitChanged(int value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.MaxVolumeLimit = value);
        _audio.OnMaxVolumeLimitChanged(value);
        ScheduleDebounce(PendingAudioChanges.MaxVolume);
    }

    partial void OnRememberTrackFormatChanged(bool value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.RememberTrackFormat = value);
        Log.Info($"[Settings] RememberTrackFormat -> {value}");
    }

    partial void OnVolumeBoostEnabledChanged(bool value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.Audio.VolumeBoostEnabled = value);
        _audio.UpdateAudioSettings();
        _audio.NotifyAudioSettingsChanged();
        Log.Info($"[Settings] VolumeBoostEnabled -> {value} (gain={_audio.CurrentGain:F4}x)");
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
                Log.Info("[Settings] NormalizationEnabled -> False");
            });
            return;
        }

        _library.UpdateSettings(s => s.Audio.NormalizationEnabled = true);
        _audio.UpdateAudioSettings();
        Log.Info("[Settings] NormalizationEnabled -> True");
    }

    partial void OnNormalizationTargetLufsChanged(float value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.Audio.NormalizationTargetLufs = value);
        ScheduleDebounce(PendingAudioChanges.NormLufs);
    }

    partial void OnNormalizationMaxGainChanged(float value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.Audio.NormalizationMaxGain = value);
        ScheduleDebounce(PendingAudioChanges.NormGain);
    }

    partial void OnSelectedNormalizationModeChanged(LocalizedItem<NormalizationMode>? value)
    {
        if (_isLoading || value is null) return;
        _library.UpdateSettings(s => s.Audio.NormalizationMode = value.Value);
        _audio.UpdateAudioSettings();
        Log.Info($"[Settings] NormalizationMode -> {value.Value}");
    }

    partial void OnSelectedVolumeCurveChanged(LocalizedItem<VolumeCurveType>? value)
    {
        if (_isLoading || value is null) return;
        _library.UpdateSettings(s => s.Audio.VolumeCurve = value.Value);
        _audio.UpdateAudioSettings();
        Log.Info($"[Settings] VolumeCurve -> {value.Value} (gain={_audio.CurrentGain:F4}x)");
    }

    partial void OnSelectedQualityItemChanged(LocalizedItem<AudioQualityPreference>? value)
    {
        if (_isLoading || value is null) return;
        _library.UpdateSettings(s => s.QualityPreference = value.Value);
        _youtube.ClearCache();
        Log.Info($"[Settings] QualityPreference -> {value.Value}");
    }

    partial void OnEqualizerEnabledChanged(bool value)
    {
        if (_isLoading) return;
        _audio.SetEqualizerEnabled(value);
    }

    partial void OnUseSimple5BandModeChanged(bool value)
    {
        OnPropertyChanged(nameof(BandModeBadgeText));
        if (_isLoading) return;
        _audio.SetSimple5BandMode(value);
        Log.Info($"[Settings] EqualizerBandMode -> {(value ? "5-Band" : "10-Band")}");
    }

    partial void OnEqualizerPreampDbChanged(float value)
    {
        if (_isLoading) return;
        CanResetEqualizer = EqualizerPresets.HasNonZeroBands(EqualizerBandGains, value);
        _audio.SetEqualizerPreampDb(value);
        ScheduleDebounce(PendingAudioChanges.EqPreamp);
    }

    public void ApplyCustomBandsFromUi(float[] bands10)
    {
        if (_isLoading || bands10.Length < EqualizerProcessor.BandCount) return;
        CancelPresetRename();
        bands10.AsSpan(0, EqualizerProcessor.BandCount).CopyTo(EqualizerBandGains);
        CanResetEqualizer = EqualizerPresets.HasNonZeroBands(EqualizerBandGains, EqualizerPreampDb);
        _audio.ApplyCustomEqualizerBands(bands10);
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
            _audio.OnEqualizerChanged -= SyncEqualizerStateFromEngine;
            _audio.OnTrackChanged -= OnAudioTrackChanged;
            _audio.FlushPendingEqualizerSave();
            FlushPendingChanges();
        }
        base.Dispose(disposing);
    }
}