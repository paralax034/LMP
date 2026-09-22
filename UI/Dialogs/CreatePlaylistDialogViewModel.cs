namespace LMP.UI.Dialogs;

/// <summary>
/// ViewModel диалога создания нового плейлиста.
/// </summary>
public sealed partial class CreatePlaylistDialogViewModel : ViewModelBase
{
    /// <summary>
    /// Вложенный редактор параметров плейлиста.
    /// </summary>
    public PlaylistEditorViewModel Editor { get; }

    /// <summary>
    /// Callback для закрытия диалога с результатом.
    /// </summary>
    public Action<CreatePlaylistResult?>? OnResult { get; set; }

    /// <summary>
    /// Команда подтверждения создания плейлиста.
    /// </summary>
    public IAsyncRelayCommand ConfirmCommand { get; }

    /// <summary>
    /// Команда отмены создания.
    /// </summary>
    public IRelayCommand CancelCommand { get; }

    /// <summary>
    /// Инициализирует новый диалог создания плейлиста.
    /// </summary>
    /// <param name="isAuthenticated">Авторизован ли пользователь.</param>
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
/// Полный результат создания плейлиста со всеми метаданными из формы.
/// </summary>
/// <param name="Name">Название создаваемого плейлиста.</param>
/// <param name="Description">Текстовое описание плейлиста.</param>
/// <param name="ThumbnailUrl">Локальный путь или сетевой URL обложки.</param>
/// <param name="CustomColor">Пользовательский цвет в формате HEX.</param>
/// <param name="ComputedColor">Автоматически вычисленный доминантный цвет обложки.</param>
/// <param name="SyncToCloud">Флаг немедленной привязки к YouTube Music.</param>
public sealed record CreatePlaylistResult(
    string Name,
    string? Description,
    string? ThumbnailUrl,
    string? CustomColor,
    string? ComputedColor,
    bool SyncToCloud);