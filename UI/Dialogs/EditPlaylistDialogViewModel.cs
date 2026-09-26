using Microsoft.Extensions.DependencyInjection;

namespace LMP.UI.Dialogs;

/// <summary>
/// ViewModel диалога редактирования плейлиста.
/// Выполняет отложенную сборку мозаики без требования промежуточного применения пользователем.
/// </summary>
public sealed class EditPlaylistDialogViewModel : ViewModelBase
{
    /// <summary>
    /// Дочерняя модель редактора параметров плейлиста.
    /// </summary>
    public PlaylistEditorViewModel Editor { get; }

    /// <summary>
    /// Callback для закрытия диалога с результатом.
    /// </summary>
    public Action<EditPlaylistResult?>? OnResult { get; set; }

    /// <summary>
    /// Команда сохранения изменений плейлиста.
    /// </summary>
    public IAsyncRelayCommand SaveCommand { get; }

    /// <summary>
    /// Команда отмены редактирования и закрытия диалога.
    /// </summary>
    public IRelayCommand CancelCommand { get; }

    /// <summary>
    /// Инициализирует модель диалога редактирования плейлиста.
    /// </summary>
    /// <param name="playlist">Редактируемый плейлист.</param>
    /// <param name="isAuthenticated">Флаг авторизации сессии в YouTube.</param>
    /// <param name="playlistTracks">Список треков плейлиста для мозаики обложек.</param>
    /// <param name="networkManager">Менеджер сетевых запросов.</param>
    /// <param name="dominantColorService">Служба палитры цветов.</param>
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
    /// Асинхронно сохраняет состояние редактора, компилируя мозаику в файл при необходимости.
    /// </summary>
    /// <param name="ct">Токен отмены операции.</param>
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