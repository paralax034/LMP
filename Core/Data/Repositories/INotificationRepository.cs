namespace LMP.Core.Data.Repositories;

/// <summary>
/// Репозиторий для персистентного хранения уведомлений.
/// </summary>
public interface INotificationRepository
{
    /// <summary>
    /// Получить последние уведомления.
    /// </summary>
    /// <param name="limit">Максимальное количество возвращаемых записей.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Список доменных моделей уведомлений.</returns>
    Task<List<Notification>> GetRecentAsync(int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// Добавить уведомление.
    /// </summary>
    /// <param name="notification">Экземпляр доменной модели уведомления.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Задача, представляющая асинхронную операцию записи.</returns>
    Task AddAsync(Notification notification, CancellationToken ct = default);

    /// <summary>
    /// Пометить все как прочитанные.
    /// </summary>
    Task MarkAllAsReadAsync(CancellationToken ct = default);

    /// <summary>
    /// Удалить все уведомления.
    /// </summary>
    Task ClearAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Удалить старые уведомления, оставив не более <paramref name="keepCount"/>.
    /// </summary>
    Task PruneAsync(int keepCount = 100, CancellationToken ct = default);

    /// <summary>
    /// Удаляет уведомления, созданные до указанной даты.
    /// Используется авто-очисткой <see cref="NotificationService"/>.
    /// </summary>
    Task DeleteOlderThanAsync(DateTime threshold, CancellationToken ct = default);
}