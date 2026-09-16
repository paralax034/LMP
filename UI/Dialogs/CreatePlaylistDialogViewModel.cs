namespace LMP.UI.Dialogs;

public sealed partial class CreatePlaylistDialogViewModel : ViewModelBase
{
    public PlaylistEditorViewModel Editor { get; }

    [ObservableProperty]
    public partial bool SyncToCloud { get; set; }
    public bool ShowSyncToggle { get; }

    /// <summary>
    /// Callback для закрытия диалога с результатом.
    /// </summary>
    public Action<CreatePlaylistResult?>? OnResult { get; set; }

    public IRelayCommand ConfirmCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public CreatePlaylistDialogViewModel(bool isAuthenticated = false)
    {
        Editor = PlaylistEditorViewModel.ForCreate();
        ShowSyncToggle = isAuthenticated;
        SyncToCloud = isAuthenticated;

        ConfirmCommand = new RelayCommand(() =>
        {
            var result = new CreatePlaylistResult(
                Name: Editor.ToResult().Name,
                SyncToCloud: SyncToCloud);
            OnResult?.Invoke(result);
        }, () => Editor.CanSave);

        CancelCommand = new RelayCommand(() =>
        {
            OnResult?.Invoke(null);
        });

        Editor.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is nameof(PlaylistEditorViewModel.HasErrors) or nameof(PlaylistEditorViewModel.CanSave))
            {
                ConfirmCommand.NotifyCanExecuteChanged();
            }
        };
    }
}

public sealed record CreatePlaylistResult(string Name, bool SyncToCloud);