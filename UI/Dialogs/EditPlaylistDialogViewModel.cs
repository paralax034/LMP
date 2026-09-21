using Microsoft.Extensions.DependencyInjection;

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

    /// <summary>
    /// Инициализирует новый экземпляр ViewModel диалога редактирования плейлиста.
    /// Обеспечивает гарантированное разрешение сетевых и доменных служб из DI-контейнера при их отсутствии в параметрах.
    /// </summary>
    /// <param name="playlist">Редактируемый плейлист.</param>
    /// <param name="isAuthenticated">Флаг авторизации текущей сессии в YouTube.</param>
    /// <param name="playlistTracks">Список треков плейлиста для мозаики обложек.</param>
    /// <param name="networkManager">Централизованный сетевой менеджер (опционально, разрешается из DI при null).</param>
    /// <param name="dominantColorService">Служба извлечения палитры (опционально, разрешается из DI при null).</param>
    /// <param name="youtube">Провайдер YouTube API (опционально, разрешается из DI при null).</param>
    public EditPlaylistDialogViewModel(
        Playlist playlist,
        bool isAuthenticated,
        IReadOnlyList<TrackInfo>? playlistTracks = null,
        INetworkManager? networkManager = null,
        DominantColorService? dominantColorService = null,
        Lazy<YoutubeProvider>? youtube = null)
    {
        networkManager ??= AppEntry.Services.GetService<INetworkManager>();
        dominantColorService ??= AppEntry.Services.GetService<DominantColorService>();
        youtube ??= AppEntry.Services.GetService<Lazy<YoutubeProvider>>();

        Editor = PlaylistEditorViewModel.ForEdit(
            playlist,
            isAuthenticated,
            playlistTracks,
            networkManager,
            dominantColorService,
            youtube);

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