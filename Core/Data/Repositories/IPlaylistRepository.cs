namespace LMP.Core.Data.Repositories;

/// <summary>
/// Интерфейс репозитория для управления плейлистами и их связями с музыкальными треками.
/// Обеспечивает строгую изоляцию списков воспроизведения на уровне аккаунтов.
/// </summary>
public interface IPlaylistRepository
{
    /// <summary>
    /// Получает плейлист по его уникальному идентификатору с автоматической поддержкой виртуализации системного плейлиста "Liked".
    /// </summary>
    /// <param name="id">Уникальный идентификатор плейлиста.</param>
    /// <param name="ownerId">Идентификатор активного аккаунта владельца.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Модель плейлиста <see cref="Playlist"/> или <c>null</c>, если он не найден.</returns>
    Task<Playlist?> GetByIdAsync(string id, string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Возвращает список всех плейлистов текущего пользователя (включая виртуальный плейлист "Liked").
    /// </summary>
    /// <param name="ownerId">Идентификатор активного аккаунта владельца.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Список доступных плейлистов.</returns>
    Task<List<Playlist>> GetAllAsync(string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Получает упорядоченный список идентификаторов треков, входящих в указанный плейлист.
    /// </summary>
    /// <param name="playlistId">Идентификатор плейлиста.</param>
    /// <param name="ownerId">Идентификатор активного аккаунта владельца.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Список идентификаторов треков.</returns>
    Task<List<string>> GetTrackIdsAsync(string playlistId, string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Возвращает общее количество треков в конкретном плейлисте.
    /// </summary>
    /// <param name="playlistId">Идентификатор плейлиста.</param>
    /// <param name="ownerId">Идентификатор активного аккаунта владельца.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Количество треков в плейлисте.</returns>
    Task<int> GetTrackCountAsync(string playlistId, string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Добавляет или обновляет метаданные плейлиста в базе данных.
    /// </summary>
    /// <param name="playlist">Модель плейлиста для сохранения.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    Task UpsertAsync(Playlist playlist, CancellationToken ct = default);

    /// <summary>
    /// Удаляет плейлист по его уникальному идентификатору.
    /// </summary>
    /// <param name="id">Идентификатор удаляемого плейлиста.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    Task DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Изменяет название локального плейлиста.
    /// </summary>
    /// <param name="id">Идентификатор плейлиста.</param>
    /// <param name="newName">Новое отображаемое имя.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    Task RenameAsync(string id, string newName, CancellationToken ct = default);

    /// <summary>
    /// Добавляет трек в плейлист на указанную или автоматическую (в конец) позицию.
    /// </summary>
    /// <param name="playlistId">Идентификатор плейлиста.</param>
    /// <param name="trackId">Идентификатор добавляемого трека.</param>
    /// <param name="ownerId">Идентификатор активного аккаунта владельца.</param>
    /// <param name="position">Порядковый индекс в плейлисте. Если не указан, трек помещается в конец.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    Task AddTrackAsync(string playlistId, string trackId, string ownerId, int? position = null, CancellationToken ct = default);

    /// <summary>
    /// Пакетно добавляет коллекцию треков в плейлист в рамках единой транзакции базы данных.
    /// </summary>
    /// <param name="playlistId">Идентификатор плейлиста.</param>
    /// <param name="trackIds">Коллекция идентификаторов треков.</param>
    /// <param name="ownerId">Идентификатор активного аккаунта владельца.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Количество успешно связанных записей.</returns>
    Task<int> AddTracksAsync(string playlistId, IEnumerable<string> trackIds, string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Безопасно удаляет трек из плейлиста с автоматическим сдвигом позиций остальных треков.
    /// </summary>
    /// <param name="playlistId">Идентификатор плейлиста.</param>
    /// <param name="trackId">Идентификатор удаляемого трека.</param>
    /// <param name="ownerId">Идентификатор активного аккаунта владельца.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    Task RemoveTrackAsync(string playlistId, string trackId, string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Пакетно удаляет коллекцию треков из плейлиста в рамках единой транзакции базы данных.
    /// </summary>
    /// <param name="playlistId">Идентификатор плейлиста.</param>
    /// <param name="trackIds">Коллекция идентификаторов удаляемых треков.</param>
    /// <param name="ownerId">Идентификатор активного аккаунта владельца.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Количество успешно удаленных записей.</returns>
    Task<int> RemoveTracksAsync(string playlistId, IEnumerable<string> trackIds, string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Изменяет порядок воспроизведения треков внутри плейлиста.
    /// </summary>
    /// <param name="playlistId">Идентификатор плейлиста.</param>
    /// <param name="oldIndex">Текущий индекс трека.</param>
    /// <param name="newIndex">Целевой индекс трека.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    Task MoveTrackAsync(string playlistId, int oldIndex, int newIndex, CancellationToken ct = default);

    /// <summary>
    /// Перепривязывает осиротевшие плейлисты (с пустым или гостевым <c>OwnerId</c>)
    /// к указанному аутентифицированному владельцу.
    /// <para>
    /// Вызывается из <see cref="LibraryService"/> при каждой смене аккаунта на аутентифицированного пользователя.
    /// Операция идемпотентна: при отсутствии сирот выполняет no-op.
    /// </para>
    /// </summary>
    /// <param name="newOwnerId">Идентификатор нового владельца (GaiaId аутентифицированного аккаунта).</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Количество перепривязанных плейлистов.</returns>
    Task<int> AdoptOrphanPlaylistsAsync(string newOwnerId, CancellationToken ct = default);

    /// <summary>
    /// Проверяет, содержится ли конкретный трек в указанном плейлисте.
    /// </summary>
    /// <param name="playlistId">Идентификатор плейлиста.</param>
    /// <param name="trackId">Идентификатор трека.</param>
    /// <param name="ownerId">Идентификатор активного аккаунта владельца.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns><c>true</c>, если трек связан с плейлистом; иначе — <c>false</c>.</returns>
    Task<bool> ContainsTrackAsync(string playlistId, string trackId, string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Возвращает список всех плейлистов текущего пользователя, в которые включен данный трек.
    /// </summary>
    /// <param name="trackId">Идентификатор проверяемого трека.</param>
    /// <param name="ownerId">Идентификатор активного аккаунта владельца.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Набор идентификаторов плейлистов.</returns>
    Task<HashSet<string>> GetPlaylistsForTrackAsync(string trackId, string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Пакетно извлекает вхождение набора треков во все плейлисты пользователя за один запрос.
    /// </summary>
    /// <param name="trackIds">Коллекция идентификаторов треков.</param>
    /// <param name="ownerId">Идентификатор активного аккаунта владельца.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Словарь, где ключ — ID трека, а значение — набор ID плейлистов.</returns>
    Task<Dictionary<string, HashSet<string>>> GetPlaylistsForTracksAsync(IEnumerable<string> trackIds, string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Возвращает все плейлисты пользователя с количеством треков в них.
    /// </summary>
    /// <param name="ownerId">Идентификатор активного аккаунта владельца.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Список кортежей, содержащих модель плейлиста и общее количество треков в нем.</returns>
    Task<List<(Playlist Playlist, int TrackCount)>> GetAllWithCountsAsync(string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Возвращает суммарную продолжительность всех треков в конкретном плейлисте в тиках (Ticks).
    /// </summary>
    /// <param name="playlistId">Идентификатор плейлиста.</param>
    /// <param name="ownerId">Идентификатор активного аккаунта владельца.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Суммарное время воспроизведения в тиках.</returns>
    Task<long> GetTotalDurationTicksAsync(string playlistId, string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Возвращает общую продолжительность всех уникальных треков во всей библиотеке пользователя.
    /// Решает проблему N+1 запросов при обновлении статистики боковой панели.
    /// </summary>
    /// <param name="ownerId">Идентификатор активного аккаунта владельца.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Суммарное время всей библиотеки в тиках.</returns>
    Task<long> GetTotalLibraryDurationAsync(string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Возвращает YouTube-идентификатор связи (setVideoId) для трека в облачном плейлисте.
    /// Необходим для корректного удаления треков из облачных плейлистов через YouTube Music API.
    /// </summary>
    /// <param name="playlistId">Идентификатор плейлиста.</param>
    /// <param name="trackId">Идентификатор трека.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Уникальный setVideoId связи или <c>null</c>, если он отсутствует.</returns>
    Task<string?> GetSetVideoIdAsync(string playlistId, string trackId, CancellationToken ct = default);

    /// <summary>
    /// Обновляет YouTube-идентификатор связи (setVideoId) для трека в облачном плейлисте.
    /// </summary>
    /// <param name="playlistId">Идентификатор плейлиста.</param>
    /// <param name="trackId">Идентификатор трека.</param>
    /// <param name="setVideoId">Новый setVideoId, полученный от YouTube API.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    Task UpdateSetVideoIdAsync(string playlistId, string trackId, string setVideoId, CancellationToken ct = default);

    /// <summary>
    /// Выполняет пакетное обновление соответствий setVideoId для группы треков в плейлисте.
    /// </summary>
    /// <param name="playlistId">Идентификатор плейлиста.</param>
    /// <param name="mappings">Коллекция пар: Идентификатор трека -> setVideoId.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    Task UpdateSetVideoIdsAsync(string playlistId, IReadOnlyList<(string TrackId, string SetVideoId)> mappings, CancellationToken ct = default);

    /// <summary>
    /// Извлекает идентификаторы треков плейлиста с поддержкой SQL-лимитов (LIMIT/OFFSET) для пагинации.
    /// </summary>
    /// <param name="playlistId">Идентификатор плейлиста.</param>
    /// <param name="ownerId">Идентификатор активного аккаунта владельца.</param>
    /// <param name="limit">Максимальное количество возвращаемых идентификаторов.</param>
    /// <param name="offset">Смещение от начала выборки.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Страница идентификаторов треков плейлиста.</returns>
    Task<List<string>> GetTrackIdsAsync(string playlistId, string ownerId, int limit, int offset = 0, CancellationToken ct = default);

    /// <summary>
    /// Пакетно извлекает идентификаторы треков для всех плейлистов пользователя за единый SQL-запрос.
    /// Исключает проблему N+1 запросов при инициализации обратного индекса связей.
    /// </summary>
    /// <param name="ownerId">Идентификатор активного аккаунта владельца.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Словарь, где ключ — ID плейлиста, а значение — набор ID входящих в него треков.</returns>
    Task<Dictionary<string, HashSet<string>>> GetAllPlaylistTrackIdsAsync(string ownerId, CancellationToken ct = default);
}