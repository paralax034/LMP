namespace LMP.UI.Features.Settings.ViewModels;

public sealed partial class WindowBehaviorSettingsViewModel : ViewModelBase
{
    private readonly LibraryService _library;
    private bool _isLoading;

    [ObservableProperty] public partial IReadOnlyList<LocalizedItem<CloseAction>> CloseActionOptions { get; private set; } = [];
    [ObservableProperty] public partial LocalizedItem<CloseAction>? SelectedCloseAction { get; set; }
    [ObservableProperty] public partial bool MinimizeToTray { get; set; }

    public WindowBehaviorSettingsViewModel(LibraryService library)
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
            var currentCloseAction = SelectedCloseAction?.Value ?? _library.Settings.CloseAction;
            CloseActionOptions = LocalizedItem.CreateList<CloseAction>("CloseAction_");
            SelectedCloseAction = CloseActionOptions.FindByValue(currentCloseAction, 2);
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
            SelectedCloseAction = CloseActionOptions.FindByValue(s.CloseAction, 2);
            MinimizeToTray = s.MinimizeToTray;
        }
        finally
        {
            _isLoading = false;
        }
    }

    partial void OnSelectedCloseActionChanged(LocalizedItem<CloseAction>? value)
    {
        if (_isLoading || value is null) return;
        _library.UpdateSettings(s => s.CloseAction = value.Value);
    }

    partial void OnMinimizeToTrayChanged(bool value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.MinimizeToTray = value);
    }
}
