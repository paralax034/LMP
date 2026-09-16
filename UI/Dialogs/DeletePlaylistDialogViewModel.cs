namespace LMP.UI.Dialogs;

public sealed partial class DeletePlaylistDialogViewModel : ViewModelBase
{
    public string PlaylistName { get; }

    [ObservableProperty]
    public partial bool IsLocalOnly { get; set; } = true;

    [ObservableProperty]
    public partial bool IsDeleteEverywhere { get; set; }
    public bool CanDeleteFromCloud { get; }

    /// <summary>
    /// Callback для закрытия диалога с результатом.
    /// </summary>
    public Action<DeletePlaylistResult?>? OnResult { get; set; }

    public IRelayCommand ConfirmCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public DeletePlaylistDialogViewModel(Playlist playlist, bool isAuthenticated)
    {
        PlaylistName = playlist.Name;
        CanDeleteFromCloud = playlist.IsFromAccount && isAuthenticated;

        ConfirmCommand = new RelayCommand(() =>
        {
            var result = new DeletePlaylistResult(
                DeleteLocally: true,
                DeleteFromCloud: IsDeleteEverywhere && CanDeleteFromCloud);
            OnResult?.Invoke(result);
        });

        CancelCommand = new RelayCommand(() =>
        {
            OnResult?.Invoke(null);
        });
    }
}

public sealed record DeletePlaylistResult(bool DeleteLocally, bool DeleteFromCloud);