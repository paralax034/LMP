using LMP.Core.Data.Repositories;
using LMP.Tests.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Tests.Integration;

/// <summary>
/// Tests and debugging tools for the notification subsystem.
/// Destructive operations are categorized as <see cref="TestCategory.Action"/>
/// and isolated from automated tests with assertions (<see cref="TestCategory.Integration"/>).
/// </summary>
public static class NotificationTests
{
    // ==========================================
    //            DEBUG ACTIONS (MANUAL)
    // ==========================================

    /// <summary>
    /// Interactive action: generates 100 test notifications in the UI to stress-test the list.
    /// Excluded from automated Run All execution.
    /// </summary>
    [TestMethod(TestCategory.Action, "Generate 100 Stress Notifications",
        Group = TestGroups.System, Order = 1, RequiresNetwork = false, TimeoutSeconds = 10)]
    public static async Task ActionGenerate100NotificationsAsync(IServiceProvider services)
    {
        await TestGenerateNotificationsAsync(services, count: 100).ConfigureAwait(false);
    }

    /// <summary>
    /// Interactive action: completely clears the user's notification list.
    /// Excluded from automated Run All execution.
    /// </summary>
    [TestMethod(TestCategory.Action, "Clear All Notifications",
        Group = TestGroups.System, Order = 2, RequiresNetwork = false, TimeoutSeconds = 5)]
    public static Task ActionClearAllNotificationsAsync(IServiceProvider services)
    {
        return TestClearAllNotificationsAsync(services);
    }

    /// <summary>
    /// Public invocation point for generating notifications in manual scenarios and <c>ManualTests</c>.
    /// </summary>
    public static async Task TestGenerateNotificationsAsync(IServiceProvider services, int count = 100)
    {
        var notificationService = services.GetRequiredService<NotificationService>();
        await notificationService.SeedDebugNotificationsAsync(count, clearExisting: true).ConfigureAwait(false);
        Log.Info($"[NotificationTests] Populated {count} test notifications into NotificationService.");
    }

    /// <summary>
    /// Public invocation point for clearing all notifications for <c>ManualTests</c>.
    /// </summary>
    public static Task TestClearAllNotificationsAsync(IServiceProvider services)
    {
        var notificationService = services.GetRequiredService<NotificationService>();
        notificationService.ClearAll();
        Log.Info("[NotificationTests] Cleared all notifications.");
        return Task.CompletedTask;
    }

    // ==========================================
    //        AUTOMATED TESTS WITH ASSERTS
    // ==========================================

    /// <summary>
    /// Automated test: verifies creation of an isolated <see cref="Notification"/> object,
    /// persistence in <see cref="INotificationRepository"/>, and correct retrieval via query.
    /// </summary>
    [TestMethod(TestCategory.Integration, "Notification Creation & Storage Integrity",
        Group = TestGroups.System, Order = 10, RequiresNetwork = false, TimeoutSeconds = 5)]
    public static async Task TestNotificationCreationAndStorageIntegrityAsync(IServiceProvider services)
    {
        var repository = services.GetRequiredService<INotificationRepository>();
        var testId = Guid.NewGuid();

        var item = new Notification
        {
            Id = testId,
            Timestamp = DateTime.UtcNow,
            TitleKey = "Dialog_Warning_Title",
            MessageKey = "Auth_ProfileLoadError_Message",
            Severity = NotificationSeverity.Warning,
            IsRead = false
        };

        await repository.AddAsync(item).ConfigureAwait(false);

        var recent = await repository.GetRecentAsync(limit: 50).ConfigureAwait(false);
        var loaded = recent.Find(n => n.Id == testId);

        TestAssert.NotNull(loaded, "Failed to retrieve persisted notification from repository");
        TestAssert.Equal(testId, loaded.Id);
        TestAssert.Equal(NotificationSeverity.Warning, loaded.Severity);
        TestAssert.Equal("Dialog_Warning_Title", loaded.TitleKey);
        TestAssert.Equal("Auth_ProfileLoadError_Message", loaded.MessageKey);
        TestAssert.False(loaded.IsRead, "Expected notification to be unread initially");
    }
}