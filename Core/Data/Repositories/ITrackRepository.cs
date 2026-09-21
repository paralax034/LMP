namespace LMP.Core.Data.Repositories;

/// <summary>
/// Интерфейс репозитория для управления персистентным хранением музыкальных треков.
/// Обеспечивает изоляцию пользовательских данных (лайков, истории) между аккаунтами.
/// </summary>
public interface ITrackRepository
{
    #region Чтение

    /// <summary>
    /// Получает метаданные трека по его уникальному идентификатору.
    /// </summary>
    /// <param name="id">Уникальный идентификатор трека.</param>
    /// <param name="ownerId">Идентификатор владельца (аккаунта) для вычисления состояния лайка.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Возвращает заполненную модель трека <see cref="TrackInfo"/> или <c>null</c>, если трек не найден.</returns>
    Task<TrackInfo?> GetByIdAsync(string id, string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Получает пакет треков по списку их идентификаторов с сохранением исходного порядка.
    /// </summary>
    /// <param name="ids">Коллекция идентификаторов треков.</param>
    /// <param name="ownerId">Идентификатор владельца (аккаунта) для вычисления состояния лайков.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Список найденных треков.</returns>
    Task<List<TrackInfo>> GetByIdsAsync(IEnumerable<string> ids, string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Возвращает полный список треков, зарегистрированных в базе данных.
    /// </summary>
    /// <param name="ownerId">Идентификатор владельца (аккаунта) для вычисления состояния лайков.</param>
    /// <param name="limit">Максимальное количество возвращаемых записей (для пагинации).</param>
    /// <param name="offset">Смещение выборки от начала (для пагинации).</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Список моделей треков.</returns>
    Task<List<TrackInfo>> GetAllAsync(string ownerId, int limit = 10000, int offset = 0, CancellationToken ct = default);

    /// <summary>
    /// Выполняет поиск треков в локальной базе данных по названию или автору с помощью SQL-оператора LIKE.
    /// </summary>
    /// <param name="query">Поисковый запрос.</param>
    /// <param name="ownerId">Идентификатор владельца (аккаунта) для вычисления состояния лайков.</param>
    /// <param name="limit">Максимальное количество возвращаемых результатов.</param>
    /// <param name="offset">Смещение выборки от начала.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Список соответствующих поисковому запросу треков.</returns>
    Task<List<TrackInfo>> SearchAsync(string query, string ownerId, int limit = 50, int offset = 0, CancellationToken ct = default);

    /// <summary>
    /// Возвращает список лайкнутых треков для указанного аккаунта, отсортированных по дате добавления (сначала новые).
    /// </summary>
    /// <param name="ownerId">Идентификатор владельца (аккаунта), чьи лайки необходимо извлечь.</param>
    /// <param name="limit">Максимальное количество возвращаемых записей.</param>
    /// <param name="offset">Смещение выборки от начала.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Список лайкнутых треков.</returns>
    Task<List<TrackInfo>> GetLikedAsync(string ownerId, int limit = 100, int offset = 0, CancellationToken ct = default);

    /// <summary>
    /// Возвращает список полностью загруженных на устройство треков.
    /// </summary>
    /// <param name="ownerId">Идентификатор владельца (аккаунта) для вычисления состояния лайков.</param>
    /// <param name="limit">Максимальное количество возвращаемых записей.</param>
    /// <param name="offset">Смещение выборки от начала.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Список загруженных треков.</returns>
    Task<List<TrackInfo>> GetDownloadedAsync(string ownerId, int limit = 100, int offset = 0, CancellationToken ct = default);

    /// <summary>
    /// Возвращает историю недавно воспроизведенных треков для указанного аккаунта.
    /// </summary>
    /// <param name="ownerId">Идентификатор владельца (аккаунта), чью историю необходимо извлечь.</param>
    /// <param name="limit">Максимальное количество записей в истории.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Список недавно прослушанных треков.</returns>
    Task<List<TrackInfo>> GetRecentlyPlayedAsync(string ownerId, int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// Извлекает локальные файлы треков (начинающиеся с "local_" или имеющие статус загруженных).
    /// </summary>
    /// <param name="ownerId">Идентификатор владельца (аккаунта) для вычисления состояния лайков.</param>
    /// <param name="limit">Максимальное количество возвращаемых записей.</param>
    /// <param name="offset">Смещение выборки от начала.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Список локальных треков.</returns>
    Task<List<TrackInfo>> GetLocalTracksAsync(string ownerId, int limit = 1000, int offset = 0, CancellationToken ct = default);

    /// <summary>
    /// Возвращает общее количество треков в локальной базе данных.
    /// </summary>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Общее число записей.</returns>
    Task<int> CountAsync(CancellationToken ct = default);

    /// <summary>
    /// Возвращает общее количество лайкнутых треков для указанного аккаунта.
    /// </summary>
    /// <param name="ownerId">Идентификатор владельца (аккаунта).</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Число лайкнутых треков.</returns>
    Task<int> CountLikedAsync(string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Возвращает общее количество локальных треков.
    /// </summary>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Число локальных записей.</returns>
    Task<int> CountLocalAsync(CancellationToken ct = default);

    #endregion

    #region Запись

    /// <summary>
    /// Сохраняет или обновляет метаданные трека в локальной базе данных.
    /// </summary>
    /// <param name="track">Модель данных трека.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    Task UpsertAsync(TrackInfo track, CancellationToken ct = default);

    /// <summary>
    /// Массово сохраняет или обновляет коллекцию треков в рамках единой транзакции базы данных.
    /// </summary>
    /// <param name="tracks">Коллекция треков для сохранения.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    Task UpsertBatchAsync(IEnumerable<TrackInfo> tracks, CancellationToken ct = default);

    /// <summary>
    /// Удаляет трек и все связанные с ним связи из базы данных по его идентификатору.
    /// </summary>
    /// <param name="id">Идентификатор удаляемого трека.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    Task DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Устанавливает или снимает флаг лайка для конкретного трека в контексте указанного аккаунта.
    /// </summary>
    /// <param name="id">Уникальный идентификатор трека.</param>
    /// <param name="ownerId">Идентификатор владельца (аккаунта).</param>
    /// <param name="liked"><c>true</c>, если трек отмечен как понравившийся; иначе — <c>false</c>.</param>
    /// <param name="likedAt">Опциональная метка времени добавления отметки. Если не указана, используется текущее время UtcNow.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    Task SetLikedAsync(string id, string ownerId, bool liked, DateTime? likedAt = null, CancellationToken ct = default);

    /// <summary>
    /// Пакетно фиксирует отметки «Мне нравится» для набора треков в рамках единой транзакции базы данных.
    /// </summary>
    /// <param name="trackIds">Упорядоченная коллекция идентификаторов треков (индекс 0 — самый свежий лайк).</param>
    /// <param name="ownerId">Идентификатор владельца (аккаунта).</param>
    /// <param name="preserveOrder">Сохранять ли исходный порядок коллекции через монотонно убывающие временные метки.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    Task SetLikedBatchAsync(IReadOnlyList<string> trackIds, string ownerId, bool preserveOrder = true, CancellationToken ct = default);

    /// <summary>
    /// Обновляет локальный статус загрузки трека и физический путь к файлу на устройстве.
    /// </summary>
    /// <param name="id">Идентификатор трека.</param>
    /// <param name="downloaded">Указывает, загружен ли трек на диск.</param>
    /// <param name="localPath">Полный путь к локальному аудиофайлу.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    Task SetDownloadedAsync(string id, bool downloaded, string? localPath, CancellationToken ct = default);

    /// <summary>
    /// Сохраняет track-level integrated loudness и её источник.
    /// </summary>
    /// <param name="id">Идентификатор трека.</param>
    /// <param name="integratedLufs">Измеренная integrated loudness в LUFS.</param>
    /// <param name="source">Источник значения <c>IntegratedLufs</c>.</param>
    /// <param name="ct">Токен отмены.</param>
    Task SaveNormalizationMetadataAsync(
        string id,
        float integratedLufs,
        int source,
        CancellationToken ct = default);

    #endregion

    #region История

    /// <summary>
    /// Добавляет трек в историю воспроизведения конкретного пользователя, автоматически очищая записи старше 100-й.
    /// </summary>
    /// <param name="trackId">Идентификатор прослушанного трека.</param>
    /// <param name="ownerId">Идентификатор владельца (аккаунта).</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    Task AddToHistoryAsync(string trackId, string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Полностью очищает историю воспроизведения для указанного аккаунта.
    /// </summary>
    /// <param name="ownerId">Идентификатор владельца (аккаунта).</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    Task ClearHistoryAsync(string ownerId, CancellationToken ct = default);

    #endregion
}