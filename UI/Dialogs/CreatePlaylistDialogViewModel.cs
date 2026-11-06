namespace LMP.UI.Dialogs;

/// <summary>
/// ViewModel for the new playlist creation dialog.
/// </summary>
public sealed partial class CreatePlaylistDialogViewModel : ViewModelBase
{
    /// <summary>
    /// Nested playlist parameters editor.
    /// </summary>
    public PlaylistEditorViewModel Editor { get; }

    /// <summary>
    /// Callback to close the dialog with a result.
    /// </summary>
    public Action<CreatePlaylistResult?>? OnResult { get; set; }

    /// <summary>
    /// Command to confirm playlist creation.
    /// </summary>
    public IAsyncRelayCommand ConfirmCommand { get; }

    /// <summary>
    /// Command to cancel creation.
    /// </summary>
    public IRelayCommand CancelCommand { get; }

    /// <summary>
    /// Initializes a new playlist creation dialog.
    /// </summary>
    /// <param name="isAuthenticated">Whether the user is authenticated.</param>
    public CreatePlaylistDialogViewModel(bool isAuthenticated = false)
    {
        Editor = PlaylistEditorViewModel.ForCreate(isAuthenticated: isAuthenticated);

        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, () => Editor.CanSave);

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

    private async Task ConfirmAsync(CancellationToken ct)
    {
        if (Editor.SelectedCoverMode == CoverMode.FromTracks && Editor.CoverPicker != null)
        {
            var mosaicPath = await Editor.CoverPicker.PersistMosaicAsync(ct).ConfigureAwait(true);
            if (!string.IsNullOrEmpty(mosaicPath))
            {
                Editor.ThumbnailUrl = mosaicPath;
            }
        }

        var form = Editor.ToResult();
        var result = new CreatePlaylistResult(
            Name: form.Name,
            Description: form.Description,
            ThumbnailUrl: form.ThumbnailUrl,
            CustomColor: form.CustomColor,
            ComputedColor: form.ComputedColor,
            SyncToCloud: Editor.IsSyncedToCloud);

        OnResult?.Invoke(result);
    }
}

/// <summary>
/// Complete playlist creation result containing all form metadata.
/// </summary>
/// <param name="Name">Name of the playlist being created.</param>
/// <param name="Description">Text description of the playlist.</param>
/// <param name="ThumbnailUrl">Local path or network URL of the cover art.</param>
/// <param name="CustomColor">Custom color in HEX format.</param>
/// <param name="ComputedColor">Automatically calculated dominant cover color.</param>
/// <param name="SyncToCloud">Flag indicating immediate sync to YouTube Music.</param>
public sealed record CreatePlaylistResult(
    string Name,
    string? Description,
    string? ThumbnailUrl,
    string? CustomColor,
    string? ComputedColor,
    bool SyncToCloud);