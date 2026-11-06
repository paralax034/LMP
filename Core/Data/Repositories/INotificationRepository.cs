namespace LMP.Core.Data.Repositories;

/// <summary>
/// Repository for persistent notification storage.
/// </summary>
public interface INotificationRepository
{
    /// <summary>
    /// Gets the latest notifications.
    /// </summary>
    /// <param name="limit">The maximum number of records to return.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>A list of notification domain models.</returns>
    Task<List<Notification>> GetRecentAsync(int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// Adds a notification.
    /// </summary>
    /// <param name="notification">The notification domain model instance.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>A task representing the asynchronous write operation.</returns>
    Task AddAsync(Notification notification, CancellationToken ct = default);

    /// <summary>
    /// Marks all notifications as read.
    /// </summary>
    Task MarkAllAsReadAsync(CancellationToken ct = default);

    /// <summary>
    /// Deletes all notifications.
    /// </summary>
    Task ClearAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Deletes old notifications, retaining at most <paramref name="keepCount"/>.
    /// </summary>
    Task PruneAsync(int keepCount = 100, CancellationToken ct = default);

    /// <summary>
    /// Deletes notifications created before the specified date.
    /// Used by <see cref="NotificationService"/> auto-cleanup.
    /// </summary>
    Task DeleteOlderThanAsync(DateTime threshold, CancellationToken ct = default);
}