using LMP.Core.Services;
using LMP.Tests.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Tests.Integration;

public static class NotificationTests
{
    [TestMethod(TestCategory.Integration, "Notifications: Generate 100 Stress Test Notifications",
        Group = TestGroups.Notifications, Order = 1, RequiresNetwork = false, TimeoutSeconds = 10)]
    public static async Task TestGenerate100NotificationsAsync(IServiceProvider services)
    {
        await TestGenerateNotificationsAsync(services, count: 100);
    }

    [TestMethod(TestCategory.Integration, "Notifications: Clear All Notifications",
        Group = TestGroups.Notifications, Order = 2, RequiresNetwork = false, TimeoutSeconds = 5)]
    public static Task TestClearAllNotificationsAsync(IServiceProvider services)
    {
        var notificationService = services.GetRequiredService<NotificationService>();
        notificationService.ClearAll();
        Log.Info("[NotificationTests] Successfully cleared all notifications.");
        return Task.CompletedTask;
    }

    public static async Task TestGenerateNotificationsAsync(IServiceProvider services, int count = 100)
    {
        var notificationService = services.GetRequiredService<NotificationService>();
        await notificationService.SeedDebugNotificationsAsync(count, clearExisting: true);
        Log.Info($"[NotificationTests] Populated {count} test notifications into NotificationService.");
    }
}