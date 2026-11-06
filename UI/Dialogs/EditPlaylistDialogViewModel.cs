using Microsoft.Extensions.DependencyInjection;

namespace LMP.UI.Dialogs;

/// <summary>
/// ViewModel for the playlist editing dialog.
/// Performs deferred mosaic construction without requiring intermediate user application.
/// </summary>
public sealed class EditPlaylistDialogViewModel : ViewModelBase
{
    /// <summary>
    /// Child model for the playlist parameter editor.
    /// </summary>
    public PlaylistEditorViewModel Editor { get; }

    /// <summary>
    /// Callback to close the dialog with a result.
    /// </summary>
    public Action<EditPlaylistResult?>? OnResult { get; set; }

    /// <summary>
    /// Command to save playlist changes.
    /// </summary>
    public IAsyncRelayCommand SaveCommand { get; }

    /// <summary>
    /// Command to cancel editing and close the dialog.
    /// </summary>
    public IRelayCommand CancelCommand { get; }

    /// <summary>
    /// Initializes a new instance of the playlist editing dialog model.
    /// </summary>
    /// <param name="playlist">The playlist being edited.</param>
    /// <param name="isAuthenticated">Flag indicating if the YouTube session is authenticated.</param>
    /// <param name="playlistTracks">List of playlist tracks for the cover mosaic.</param>
    /// <param name="networkManager">Network request manager.</param>
    /// <param name="dominantColorService">Color palette service.</param>
    public EditPlaylistDialogViewModel(
        Playlist playlist,
        bool isAuthenticated,
        IReadOnlyList<TrackInfo>? playlistTracks = null,
        NetworkManager? networkManager = null,
        DominantColorService? dominantColorService = null)
    {
        networkManager ??= AppEntry.Services.GetService<NetworkManager>();
        dominantColorService ??= AppEntry.Services.GetService<DominantColorService>();

        Editor = PlaylistEditorViewModel.ForEdit(
            playlist,
            isAuthenticated,
            playlistTracks,
            networkManager,
            dominantColorService);

        Editor.OnCreateCopy = () =>
        {
            var result = Editor.ToResult();
            result.ShouldCreateCopy = true;
            OnResult?.Invoke(result);
        };

        SaveCommand = new AsyncRelayCommand(SaveAsync, () => Editor.CanSave);

        CancelCommand = new RelayCommand(() =>
        {
            OnResult?.Invoke(null);
        });

        Editor.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is nameof(PlaylistEditorViewModel.HasErrors) or nameof(PlaylistEditorViewModel.CanSave))
            {
                SaveCommand.NotifyCanExecuteChanged();
            }
        };
    }

    /// <summary>
    /// Asynchronously saves the editor state, compiling the mosaic to a file if necessary.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    private async Task SaveAsync(CancellationToken ct)
    {
        if (Editor.SelectedCoverMode == CoverMode.FromTracks && Editor.CoverPicker != null)
        {
            var mosaicPath = await Editor.CoverPicker.PersistMosaicAsync(ct).ConfigureAwait(true);
            if (!string.IsNullOrEmpty(mosaicPath))
            {
                Editor.ThumbnailUrl = mosaicPath;
            }
        }

        var result = Editor.ToResult();
        if (Editor.SyncStateChanged)
            result.SyncToCloud = Editor.IsSyncedToCloud;

        OnResult?.Invoke(result);
    }
}