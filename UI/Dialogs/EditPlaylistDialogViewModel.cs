namespace LMP.UI.Dialogs;

/// <summary>
/// ViewModel диалога редактирования плейлиста.
/// Оборачивает <see cref="PlaylistEditorViewModel"/> и обрабатывает
/// три возможных исхода: сохранение, создание копии, отмена.
/// </summary>
public sealed class EditPlaylistDialogViewModel : ViewModelBase
{
    public PlaylistEditorViewModel Editor { get; }

    /// <summary>
    /// Callback для закрытия диалога с результатом.
    /// </summary>
    public Action<EditPlaylistResult?>? OnResult { get; set; }

    public IRelayCommand SaveCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public EditPlaylistDialogViewModel(
        Playlist playlist,
        bool isAuthenticated,
        IReadOnlyList<TrackInfo>? playlistTracks = null)
    {
        Editor = PlaylistEditorViewModel.ForEdit(playlist, isAuthenticated, playlistTracks);

        // Провязка callback создания копии.
        // Собираем текущие данные редактора и возвращаем результат с флагом ShouldCreateCopy.
        // PlaylistEditService создаст новый локальный плейлист вместо редактирования оригинала.
        Editor.OnCreateCopy = () =>
        {
            var result = Editor.ToResult();
            result.ShouldCreateCopy = true;
            OnResult?.Invoke(result);
        };

        SaveCommand = new RelayCommand(() =>
        {
            var result = Editor.ToResult();

            if (Editor.SyncStateChanged)
                result.SyncToCloud = Editor.IsSyncedToCloud;

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
                SaveCommand.NotifyCanExecuteChanged();
            }
        };
    }
}