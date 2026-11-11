using System.Collections.ObjectModel;
using Avalonia.Threading;
using LMP.Core.Audio.Helpers;

namespace LMP.UI.Controls;

/// <summary>
/// Shared ViewModel managing equalizer state, preset list synchronization, inline preset creation/renaming,
/// per-track equalizer binding, and DSP preamp across both the Player Bar Flyout and Audio Settings page.
/// </summary>
public sealed partial class EqualizerScopeViewModel : ViewModelBase
{
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly EventHandler<string> _languageChangedHandler;

    private bool _isSyncing;
    private string? _renamingPresetId;

    [ObservableProperty] public partial bool IsEqualizerEnabled { get; set; }
    [ObservableProperty] public partial bool IsSimple5BandMode { get; set; } = true;
    [ObservableProperty] public partial float EqualizerPreampDb { get; set; }
    [ObservableProperty] public partial float[] EqualizerBandGains { get; private set; } = new float[EqualizerProcessor.BandCount];
    [ObservableProperty] public partial string ActiveEqualizerPresetId { get; private set; } = EqualizerPresets.FlatId;
    [ObservableProperty] public partial string ActiveEqualizerPresetTitle { get; private set; } = "";
    [ObservableProperty] public partial string NewPresetNameInput { get; set; } = "";
    [ObservableProperty] public partial bool ShowPresetRightClickHint { get; private set; }
    [ObservableProperty] public partial bool HasTrack { get; private set; }
    [ObservableProperty] public partial bool IsTrackModeHighlighted { get; private set; }
    [ObservableProperty] public partial bool CanResetEqualizer { get; private set; }

    /// <summary>
    /// Badge text ("5" or "10") indicating the active band mode.
    /// </summary>
    public string BandModeBadgeText => IsSimple5BandMode ? "5" : "10";

    /// <summary>
    /// Localized tooltip for the per-track equalizer mode button.
    /// </summary>
    public string TrackEqualizerTooltip =>
        EqualizerPresetChipItem.FormatTrackEqualizerTooltip(
            _audio.CurrentTrack?.EqualizerPresetId,
            IsTrackModeHighlighted,
            EqualizerPresetsList);

    /// <summary>
    /// Synchronized collection of built-in and user equalizer presets.
    /// </summary>
    public ObservableCollection<EqualizerPresetChipItem> EqualizerPresetsList { get; } = [];

    /// <summary>
    /// Raised when the inline preset text box should receive keyboard focus for renaming.
    /// </summary>
    public event Action? PresetInputFocusRequested;

    public IRelayCommand ResetEqualizerCommand { get; }
    public IRelayCommand ToggleEqualizerModeCommand { get; }
    public IRelayCommand ToggleTrackEqualizerModeCommand { get; }
    public IRelayCommand<EqualizerPresetChipItem> SelectEqualizerPresetCommand { get; }
    public IRelayCommand<EqualizerPresetChipItem> DeleteEqualizerPresetCommand { get; }
    public IRelayCommand SaveInlineEqualizerPresetCommand { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="EqualizerScopeViewModel"/> class.
    /// </summary>
    public EqualizerScopeViewModel(AudioEngine audio, LibraryService library)
    {
        _audio = audio;
        _library = library;

        ResetEqualizerCommand = new RelayCommand(() =>
        {
            if (!CanResetEqualizer) return;
            _audio.ResetEqualizer();
        });

        ToggleEqualizerModeCommand = new RelayCommand(() =>
        {
            IsSimple5BandMode = !IsSimple5BandMode;
        });

        ToggleTrackEqualizerModeCommand = new RelayCommand(() =>
        {
            if (!HasTrack) return;
            _audio.ToggleTrackEqualizerMode();
        });

        SelectEqualizerPresetCommand = new RelayCommand<EqualizerPresetChipItem>(item =>
        {
            if (item == null) return;
            if (item.IsSelected && !item.IsBuiltIn)
            {
                _renamingPresetId = item.Id;
                NewPresetNameInput = item.Title;
                PresetInputFocusRequested?.Invoke();
                return;
            }

            _renamingPresetId = null;
            _audio.SelectEqualizerPreset(item.Id);
        });

        DeleteEqualizerPresetCommand = new RelayCommand<EqualizerPresetChipItem>(item =>
        {
            if (item == null || item.IsBuiltIn) return;
            if (string.Equals(_renamingPresetId, item.Id, StringComparison.Ordinal))
            {
                _renamingPresetId = null;
                NewPresetNameInput = "";
            }
            _audio.DeleteUserEqualizerPreset(item.Id);
        });

        SaveInlineEqualizerPresetCommand = new RelayCommand(() =>
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

        _languageChangedHandler = OnLanguageChanged;
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;
        _audio.OnEqualizerChanged += SyncState;
        _audio.OnTrackChanged += OnAudioTrackChanged;
        _library.OnTrackUpdated += OnLibraryTrackUpdated;

        SyncState();
    }

    private void OnAudioTrackChanged(TrackInfo? _) => SyncStateOnUiThread();

    private void OnLibraryTrackUpdated(TrackInfo updated)
    {
        var current = _audio.CurrentTrack;
        if (current == null || !string.Equals(current.Id, updated.Id, StringComparison.Ordinal))
            return;

        void ApplyTrackPresetUpdate()
        {
            var active = _audio.CurrentTrack;
            if (active == null || !string.Equals(active.Id, updated.Id, StringComparison.Ordinal))
                return;

            if (!string.Equals(active.EqualizerPresetId, updated.EqualizerPresetId, StringComparison.Ordinal))
            {
                active.EqualizerPresetId = updated.EqualizerPresetId;
                SyncState();
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
            ApplyTrackPresetUpdate();
        else
            Dispatcher.UIThread.Post(ApplyTrackPresetUpdate);
    }

    private void OnLanguageChanged(object? sender, string newLang)
    {
        void RefreshOnUi()
        {
            OnPropertyChanged(nameof(L));
            EqualizerPresetsList.Clear();
            SyncState();
        }

        if (Dispatcher.UIThread.CheckAccess())
            RefreshOnUi();
        else
            Dispatcher.UIThread.Post(RefreshOnUi);
    }

    private void SyncStateOnUiThread()
    {
        if (Dispatcher.UIThread.CheckAccess())
            SyncState();
        else
            Dispatcher.UIThread.Post(SyncState);
    }

    /// <summary>
    /// Cancels any active inline preset rename operation and clears the input box.
    /// </summary>
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

    /// <summary>
    /// Synchronizes all equalizer properties and the preset list from <see cref="AudioSettings"/> and the active track.
    /// </summary>
    public void SyncState()
    {
        _isSyncing = true;
        try
        {
            var audioSettings = _library.Settings.Audio;
            var currentTrack = _audio.CurrentTrack;
            if (currentTrack != null)
            {
                var storedTrack = _library.GetTrack(currentTrack.Id);
                if (storedTrack != null && string.IsNullOrEmpty(currentTrack.EqualizerPresetId))
                    currentTrack.EqualizerPresetId = storedTrack.EqualizerPresetId;
            }

            HasTrack = currentTrack != null;
            IsEqualizerEnabled = audioSettings.EqualizerEnabled;
            IsSimple5BandMode = audioSettings.UseSimple5BandMode;
            EqualizerPreampDb = audioSettings.EqualizerPreampDb;

            var bands = new float[EqualizerProcessor.BandCount];
            string resolvedPreset = EqualizerPresets.ResolveEffectiveBands(
                audioSettings,
                currentTrack,
                bands);

            if (_renamingPresetId != null
                && !string.Equals(_renamingPresetId, resolvedPreset, StringComparison.Ordinal))
            {
                CancelPresetRename();
            }

            ActiveEqualizerPresetId = resolvedPreset;
            EqualizerBandGains = bands;
            ActiveEqualizerPresetTitle = EqualizerPresetChipItem.SyncPresetsList(
                EqualizerPresetsList,
                audioSettings,
                currentTrack,
                resolvedPreset);

            IsTrackModeHighlighted = audioSettings.EqualizerEnabled && _audio.IsTrackEqualizerActive;
            CanResetEqualizer = EqualizerPresets.HasNonZeroBands(bands, audioSettings.EqualizerPreampDb);

            OnPropertyChanged(nameof(BandModeBadgeText));
            OnPropertyChanged(nameof(TrackEqualizerTooltip));
        }
        finally
        {
            _isSyncing = false;
        }
    }

    partial void OnIsEqualizerEnabledChanged(bool value)
    {
        if (_isSyncing) return;
        _audio.SetEqualizerEnabled(value);
    }

    partial void OnIsSimple5BandModeChanged(bool value)
    {
        OnPropertyChanged(nameof(BandModeBadgeText));
        if (_isSyncing) return;
        _audio.SetSimple5BandMode(value);
    }

    partial void OnEqualizerPreampDbChanged(float value)
    {
        if (_isSyncing) return;
        CanResetEqualizer = EqualizerPresets.HasNonZeroBands(EqualizerBandGains, value);
        _audio.SetEqualizerPreampDb(value);
    }

    /// <summary>
    /// Applies a custom 10-band curve modified interactively in <see cref="EqualizerCurveControl"/>.
    /// </summary>
    public void ApplyCustomBandsFromUi(float[] newBands10)
    {
        if (_isSyncing || newBands10.Length < EqualizerProcessor.BandCount) return;
        CancelPresetRename();
        newBands10.AsSpan(0, EqualizerProcessor.BandCount).CopyTo(EqualizerBandGains);
        CanResetEqualizer = EqualizerPresets.HasNonZeroBands(
            EqualizerBandGains,
            _library.Settings.Audio.EqualizerPreampDb);
        _audio.ApplyCustomEqualizerBands(newBands10);
    }

    /// <summary>
    /// Flushes any debounced equalizer changes to persistent storage.
    /// </summary>
    public void FlushPendingSave() => _audio.FlushPendingEqualizerSave();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _audio.FlushPendingEqualizerSave();
            LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;
            _audio.OnEqualizerChanged -= SyncState;
            _audio.OnTrackChanged -= OnAudioTrackChanged;
            _library.OnTrackUpdated -= OnLibraryTrackUpdated;
        }

        base.Dispose(disposing);
    }
}