using System.Collections;
using System.Collections.Specialized;
using LMP.UI.Features.Shared;

namespace LMP.UI.ViewModels;

/// <summary>
/// Единый контракт высокопроизводительного виртуализированного списка треков.
/// Является Single Source of Truth для порядка элементов, фильтрации и состояния выбора.
/// </summary>
public interface IVirtualTrackList : IList<TrackItemViewModel>, IReadOnlyList<TrackItemViewModel>, IList,
    INotifyCollectionChanged, IDisposable
{
    /// <summary>
    /// Явное сокрытие базовых определений Count для исключения неоднозначности CS0229.
    /// </summary>
    new int Count { get; }

    /// <summary>
    /// Явное сокрытие индексатора для разрешения неоднозначности между IList и IReadOnlyList (CS0121).
    /// </summary>
    new TrackItemViewModel this[int index] { get; set; }

    /// <summary>
    /// Общее количество треков в мастер-индексе (до применения фильтрации).
    /// </summary>
    int TotalCount { get; }

    /// <summary>
    /// Количество треков, удовлетворяющих текущему фильтру.
    /// </summary>
    int FilteredCount { get; }

    /// <summary>
    /// Количество выделенных в данный момент треков.
    /// </summary>
    int SelectedCount { get; }

    /// <summary>
    /// Контекст плейлиста для отображения специфичных действий в меню и строках.
    /// </summary>
    bool IsPlaylistContext { get; set; }

    /// <summary>
    /// Контекст очереди воспроизведения для отображения специфичных действий в меню и строках.
    /// </summary>
    bool IsQueueContext { get; set; }

    /// <summary>
    /// Делегат поставщика снимка выделенных треков для контекстного меню.
    /// </summary>
    Func<IReadOnlyList<TrackInfo>>? SelectionProvider { get; set; }

    /// <summary>
    /// Событие изменения набора выделенных треков для централизованного оповещения UI.
    /// </summary>
    event Action? SelectionChanged;

    /// <summary>
    /// Проверяет, выделен ли элемент с указанным визуальным индексом.
    /// </summary>
    bool IsIndexSelected(int visualIndex);

    /// <summary>
    /// Устанавливает статус выделения для элемента с указанным визуальным индексом.
    /// </summary>
    void SetIndexSelected(int visualIndex, bool isSelected);

    /// <summary>
    /// Инвертирует статус выделения для элемента с указанным визуальным индексом.
    /// </summary>
    void ToggleIndexSelected(int visualIndex);

    /// <summary>
    /// Выделяет все видимые треки.
    /// </summary>
    void SelectAll();

    /// <summary>
    /// Снимает выделение со всех элементов.
    /// </summary>
    void ClearSelection();

    /// <summary>
    /// Выделяет непрерывный диапазон визуальных индексов.
    /// </summary>
    void SelectRange(int fromVisualIndex, int toVisualIndex, bool addToExisting);

    /// <summary>
    /// Возвращает снимок доменных моделей всех выделенных треков.
    /// </summary>
    List<TrackInfo> GetSelectedTracks();

    /// <summary>
    /// Возвращает упорядоченный список выделенных моделей представления для зоны видимости.
    /// </summary>
    List<TrackItemViewModel> GetSelectedViewModelsOrdered();

    /// <summary>
    /// Извлекает доменную модель трека по его визуальному индексу без создания ViewModel.
    /// </summary>
    TrackInfo? GetTrackAt(int visualIndex);

    /// <summary>
    /// Возвращает визуальный индекс трека по его идентификатору за O(1)/O(N) без материализации ViewModel.
    /// </summary>
    int IndexOfTrackId(string? trackId);

    /// <summary>
    /// Возвращает активный экземпляр ViewModel для трека, если он находится в видимом пуле.
    /// </summary>
    TrackItemViewModel? TryGetVm(string trackId);

    /// <summary>
    /// Пакетно инициализирует список коллекцией треков.
    /// </summary>
    void SetTracks(IEnumerable<TrackInfo> tracks, IReadOnlyList<string>? explicitOrder = null,
        bool preserveSelection = false);

    /// <summary>
    /// Добавляет пачку треков в конец индекса.
    /// </summary>
    void AppendTracks(IEnumerable<TrackInfo> newTracks);

    /// <summary>
    /// Применяет поисковый запрос и выполняет векторизованную SIMD-фильтрацию.
    /// </summary>
    void ApplyFilter(string query);

    /// <summary>
    /// Перемещает элемент в мастер-индексе и пересчитывает визуальные позиции.
    /// </summary>
    void MoveTrack(int oldVisualIndex, int newVisualIndex);

    /// <summary>
    /// Удаляет трек по его идентификатору.
    /// </summary>
    bool RemoveTrack(string trackId);

    /// <summary>
    /// Обновляет состояние активности и воспроизведения в O(1).
    /// </summary>
    void UpdatePlaybackState(TrackInfo? currentTrack, bool isPlaying);

    /// <summary>
    /// Обновляет прогресс физической загрузки для отображаемого элемента.
    /// </summary>
    void UpdateDownloadProgress(string trackId, float progress);

    /// <summary>
    /// Фиксирует завершение физической загрузки трека.
    /// </summary>
    void UpdateDownloadCompleted(string trackId, bool ok, string? path);

    /// <summary>
    /// Фиксирует статус локального дискового кэша для элемента.
    /// </summary>
    void UpdateCacheStatus(string trackId, AudioFormat format, int bitrate);
}
