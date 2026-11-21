using LMP.Tests.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Tests.Actions;

/// <summary>
/// Interactive and destructive manual debug actions (<see cref="TestCategory.Action"/>).
/// Excluded from automated Run All execution; intended for manual invocation from the Debug UI.
/// </summary>
public static class DebugActions
{
    [TestMethod(TestCategory.Action, "Generate 100 Stress Notifications",
        Group = TestGroups.System, Order = 1, RequiresNetwork = false, TimeoutSeconds = 10)]
    public static async Task ActionGenerate100NotificationsAsync(IServiceProvider services)
    {
        var notificationService = services.GetRequiredService<NotificationService>();
        await notificationService.SeedDebugNotificationsAsync(count: 100, clearExisting: true).ConfigureAwait(false);
        Log.Info("[DebugActions] Populated 100 test notifications into NotificationService.");
    }

    [TestMethod(TestCategory.Action, "Clear All Notifications",
        Group = TestGroups.System, Order = 2, RequiresNetwork = false, TimeoutSeconds = 5)]
    public static Task ActionClearAllNotificationsAsync(IServiceProvider services)
    {
        var notificationService = services.GetRequiredService<NotificationService>();
        notificationService.ClearAll();
        Log.Info("[DebugActions] Cleared all notifications.");
        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Action, "ImageCache Memory LRU & Disk Stats Verification",
        Group = TestGroups.Storage, Order = 7, RequiresNetwork = false, TimeoutSeconds = 10)]
    public static Task ActionImageCacheLimitsAndStatsAsync(IServiceProvider services)
    {
        var imageCache = services.GetRequiredService<ImageCacheService>();
        var library = services.GetRequiredService<LibraryService>();

        int origMaxItems = library.Settings.Storage.MaxBitmapCacheItems;
        try
        {
            var (memItems, _, diskFiles, diskMb) = imageCache.GetStats();
            TestAssert.True(memItems >= 0, "Memory items count must be >= 0");
            TestAssert.True(diskFiles >= 0, "Disk files count must be >= 0");
            TestAssert.True(diskMb >= 0, "Disk MB must be >= 0");

            library.Settings.Storage.MaxBitmapCacheItems = 1;
            imageCache.EnforceLimits();

            var (enforcedMem, _, _, _) = imageCache.GetStats();
            TestAssert.True(enforcedMem <= 1, "EnforceLimits must evict RAM bitmaps down to MaxBitmapCacheItems");

            imageCache.ClearMemoryCache();
            var (clearedMem, _, _, _) = imageCache.GetStats();
            TestAssert.Equal(0, clearedMem);
        }
        finally
        {
            library.Settings.Storage.MaxBitmapCacheItems = origMaxItems;
        }

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Action, "DownloadService Storage Limit Rejection",
        Group = TestGroups.Storage, Order = 8, RequiresNetwork = false, TimeoutSeconds = 10)]
    public static Task ActionDownloadServiceQuotaCheckAsync(IServiceProvider services)
    {
        var library = services.GetRequiredService<LibraryService>();
        var youtube = services.GetRequiredService<YoutubeProvider>();

        string tempDownloads = Path.Combine(Path.GetTempPath(), "LMP_DownloadQuotaTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDownloads);

        try
        {
            string dummyFile = Path.Combine(tempDownloads, "existing_track.mp3");
            File.WriteAllBytes(dummyFile, new byte[2 * 1024 * 1024]);

            string origPath = library.Settings.DownloadPath;
            int origLimit = library.Settings.Storage.DownloadedTracksLimitMb;

            library.Settings.DownloadPath = tempDownloads;
            library.Settings.Storage.DownloadedTracksLimitMb = 1;

            try
            {
                var downloadService = new DownloadService(youtube, library);
                var track = new TrackInfo { Id = "quota_test_track", Title = "Test Quota Track" };

                bool completedCalled = false;
                bool completedSuccess = true;

                downloadService.OnCompleted += (id, success, _) =>
                {
                    if (id == track.Id)
                    {
                        completedCalled = true;
                        completedSuccess = success;
                    }
                };

                downloadService.StartDownload(track);

                TestAssert.True(completedCalled, "OnCompleted must be invoked when quota is exceeded");
                TestAssert.False(completedSuccess, "Download must fail when quota is exceeded");
                TestAssert.False(downloadService.IsDownloading(track.Id), "Track must not remain in active tasks");
            }
            finally
            {
                library.Settings.DownloadPath = origPath;
                library.Settings.Storage.DownloadedTracksLimitMb = origLimit;
            }
        }
        finally
        {
            try { if (Directory.Exists(tempDownloads)) Directory.Delete(tempDownloads, recursive: true); } catch { }
        }

        return Task.CompletedTask;
    }
}