using Avalonia.Threading;
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
        NormGain = 1 << 2
    }

    private readonly LibraryService _library;
    private readonly AudioEngine _audio;
    private readonly DialogService _dialog;
    private readonly YoutubeProvider _youtube;
    private readonly EventHandler<string> _languageChangedHandler;

    private DispatcherTimer? _debounceTimer;
    private PendingAudioChanges _pendingChanges;
    private bool _isLoading;
    private bool _isRevertingNormalization;

    /// <summary>
    /// Shared equalizer state and preset management ViewModel.
    /// </summary>
    public EqualizerScopeViewModel Equalizer { get; }

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
        Equalizer = new EqualizerScopeViewModel(audio, library);

        ShowNormalizationInfoCommand = new AsyncRelayCommand(ShowNormalizationInfoAsync);

        _languageChangedHandler = OnLanguageChanged;
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;

        RefreshLists();
        LoadSettings();
    }

    private void OnLanguageChanged(object? sender, string newLang)
    {
        if (Dispatcher.UIThread.CheckAccess())
            RefreshLists();
        else
            Dispatcher.UIThread.Post(RefreshLists);
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

        Equalizer.SyncState();
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

        Equalizer.SyncState();
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
            LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;
            Equalizer.Dispose();
            FlushPendingChanges();
        }
        base.Dispose(disposing);
    }
}