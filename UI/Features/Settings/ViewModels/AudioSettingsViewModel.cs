using Avalonia.Threading;
using LMP.Core.Audio.Normalization;

namespace LMP.UI.Features.Settings.ViewModels;

public sealed partial class AudioSettingsViewModel : ViewModelBase, IDisposable
{
    private readonly LibraryService _library;
    private readonly AudioEngine _audio;
    private readonly DialogService _dialog;
    private readonly YoutubeProvider _youtube;

    private DispatcherTimer? _normLufsDebounceTimer;
    private DispatcherTimer? _normGainDebounceTimer;
    private bool _isLoading;
    private bool _isRevertingNormalization;

    [ObservableProperty] public partial IReadOnlyList<LocalizedItem<AudioQualityPreference>> QualityOptions { get; private set; } = [];
    [ObservableProperty] public partial LocalizedItem<AudioQualityPreference>? SelectedQualityItem { get; set; }

    [ObservableProperty] public partial int MaxVolumeLimit { get; set; }
    [ObservableProperty] public partial float TargetGainDb { get; set; }
    [ObservableProperty] public partial bool RememberTrackFormat { get; set; }
    [ObservableProperty] public partial bool VolumeBoostEnabled { get; set; }
    [ObservableProperty] public partial bool AudioNormalizationEnabled { get; set; }
    [ObservableProperty] public partial float NormalizationTargetLufs { get; set; }
    [ObservableProperty] public partial float NormalizationMaxGain { get; set; }

    [ObservableProperty] public partial IReadOnlyList<LocalizedItem<VolumeCurveType>> VolumeCurveOptions { get; private set; } = [];
    [ObservableProperty] public partial LocalizedItem<VolumeCurveType>? SelectedVolumeCurve { get; set; }

    [ObservableProperty] public partial IReadOnlyList<LocalizedItem<NormalizationMode>> NormalizationModeOptions { get; private set; } = [];
    [ObservableProperty] public partial LocalizedItem<NormalizationMode>? SelectedNormalizationMode { get; set; }

    public IAsyncRelayCommand ShowNormalizationInfoCommand { get; }

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
            TargetGainDb = s.TargetGainDb;
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
    }

    partial void OnMaxVolumeLimitChanged(int value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.MaxVolumeLimit = value);
        _audio.OnMaxVolumeLimitChanged(value);
    }

    partial void OnTargetGainDbChanged(float value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.TargetGainDb = value);
        _audio.UpdateAudioSettings();
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
        _normLufsDebounceTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(300),
            DispatcherPriority.Normal,
            (_, _) =>
            {
                _normLufsDebounceTimer?.Stop();
                _library.UpdateSettings(s => s.Audio.NormalizationTargetLufs = value);
                _audio.UpdateAudioSettings();
            });
        _normLufsDebounceTimer.Start();
    }

    partial void OnNormalizationMaxGainChanged(float value)
    {
        if (_isLoading) return;
        _normGainDebounceTimer?.Stop();
        _normGainDebounceTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(300),
            DispatcherPriority.Normal,
            (_, _) =>
            {
                _normGainDebounceTimer?.Stop();
                _library.UpdateSettings(s => s.Audio.NormalizationMaxGain = value);
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
        }
        base.Dispose(disposing);
    }
}
