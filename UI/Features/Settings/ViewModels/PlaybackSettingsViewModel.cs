namespace LMP.UI.Features.Settings.ViewModels;

public sealed partial class PlaybackSettingsViewModel : ViewModelBase
{
    private readonly LibraryService _library;
    private bool _isLoading;

    [ObservableProperty] public partial bool AutoPlayOnPaste { get; set; }
    [ObservableProperty] public partial IReadOnlyList<LocalizedItem<PlaybackErrorBehavior>> ErrorBehaviorOptions { get; private set; } = [];
    [ObservableProperty] public partial LocalizedItem<PlaybackErrorBehavior>? SelectedErrorBehavior { get; set; }
    [ObservableProperty] public partial bool PlayErrorSound { get; set; }
    [ObservableProperty] public partial bool SkipNTokenTracks { get; set; }
    [ObservableProperty] public partial IReadOnlyList<LocalizedItem<NTokenNotificationMode>> NTokenNotificationOptions { get; private set; } = [];
    [ObservableProperty] public partial LocalizedItem<NTokenNotificationMode>? SelectedNTokenNotification { get; set; }
    [ObservableProperty] public partial IReadOnlyList<LocalizedItem<PlaybackFailureBehavior>> PlaybackFailureOptions { get; private set; } = [];
    [ObservableProperty] public partial LocalizedItem<PlaybackFailureBehavior>? SelectedPlaybackFailure { get; set; }
    [ObservableProperty] public partial bool IsPlaybackFailureActionEnabled { get; private set; } = true;

    public PlaybackSettingsViewModel(LibraryService library)
    {
        _library = library;

        RefreshLists();
        LoadSettings();
    }

    public void RefreshLists()
    {
        _isLoading = true;
        try
        {
            var currentErrorBehavior = SelectedErrorBehavior?.Value ?? _library.Settings.Audio.CriticalErrorBehavior;
            ErrorBehaviorOptions = LocalizedItem.CreateList<PlaybackErrorBehavior>("Settings_ErrorBehavior_");
            SelectedErrorBehavior = ErrorBehaviorOptions.FindByValue(currentErrorBehavior, 1);

            var currentNTokenMode = SelectedNTokenNotification?.Value ?? _library.Settings.Audio.NTokenNotificationMode;
            NTokenNotificationOptions = LocalizedItem.CreateList<NTokenNotificationMode>("Settings_NTokenMode_");
            SelectedNTokenNotification = NTokenNotificationOptions.FindByValue(currentNTokenMode, 2);

            var currentFailureMode = SelectedPlaybackFailure?.Value ?? _library.Settings.Audio.PlaybackFailureBehavior;
            PlaybackFailureOptions = LocalizedItem.CreateList<PlaybackFailureBehavior>("Settings_PlaybackFailure_");
            SelectedPlaybackFailure = PlaybackFailureOptions.FindByValue(currentFailureMode, 1);

            UpdatePlaybackFailureActionAvailability();
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
            AutoPlayOnPaste = s.AutoPlayOnUrlPaste;
            PlayErrorSound = s.Audio.PlayErrorSound;
            SelectedErrorBehavior = ErrorBehaviorOptions.FindByValue(s.Audio.CriticalErrorBehavior, 1);
            SkipNTokenTracks = s.Audio.SkipNTokenTracks;
            SelectedNTokenNotification = NTokenNotificationOptions.FindByValue(s.Audio.NTokenNotificationMode, 2);
            SelectedPlaybackFailure = PlaybackFailureOptions.FindByValue(s.Audio.PlaybackFailureBehavior, 1);

            UpdatePlaybackFailureActionAvailability();
        }
        finally
        {
            _isLoading = false;
        }
    }

    partial void OnAutoPlayOnPasteChanged(bool value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.AutoPlayOnUrlPaste = value);
    }

    partial void OnSelectedErrorBehaviorChanged(LocalizedItem<PlaybackErrorBehavior>? value)
    {
        if (_isLoading || value is null) return;
        _library.UpdateSettings(s => s.Audio.CriticalErrorBehavior = value.Value);
        UpdatePlaybackFailureActionAvailability();
    }

    partial void OnPlayErrorSoundChanged(bool value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.Audio.PlayErrorSound = value);
    }

    partial void OnSkipNTokenTracksChanged(bool value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.Audio.SkipNTokenTracks = value);
    }

    partial void OnSelectedNTokenNotificationChanged(LocalizedItem<NTokenNotificationMode>? value)
    {
        if (_isLoading || value is null) return;
        _library.UpdateSettings(s => s.Audio.NTokenNotificationMode = value.Value);
    }

    partial void OnSelectedPlaybackFailureChanged(LocalizedItem<PlaybackFailureBehavior>? value)
    {
        if (_isLoading || value is null) return;
        _library.UpdateSettings(s => s.Audio.PlaybackFailureBehavior = value.Value);
    }

    private void UpdatePlaybackFailureActionAvailability()
    {
        var behavior = SelectedErrorBehavior?.Value ?? _library.Settings.Audio.CriticalErrorBehavior;
        IsPlaybackFailureActionEnabled = PlaybackErrorBehaviorMatrix.UsesPlaybackFailureBehavior(behavior);
    }
}
