using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace LMP.UI.Helpers;

/// <summary>
/// Centralized application memory management.
///
/// <para><b>Three modes:</b></para>
/// <list type="bullet">
///   <item><b>Soft</b>  — GC Gen1, non-blocking (during active playback)</item>
///   <item><b>Normal</b> — GC Gen2 Optimized, non-blocking</item>
///   <item><b>Aggressive</b> — GC Gen2 + LOH compaction + TrimWorkingSet + Skia purge</item>
/// </list>
///
/// <para><b>Auto-cleanup:</b> started via <see cref="StartAutoCleanup"/>,
/// interval is retrieved from <see cref="AppSettings.Memory"/>.
/// Stopped via <see cref="StopAutoCleanup"/> or <see cref="Dispose"/>.</para>
/// </summary>
public static class MemoryCleanupHelper
{
    private static PeriodicTimer? _autoCleanupTimer;
    private static CancellationTokenSource? _autoCleanupCts;
    private static readonly Lock _timerLock = new();

    #region Public API

    /// <summary>
    /// Performs memory cleanup on a background thread.
    /// Automatically selects the mode: soft during playback, normal/aggressive otherwise.
    /// </summary>
    public static void PerformCleanup(bool aggressive = false)
    {
        _ = Task.Run(() => RunCleanupAsync(aggressive));
    }

    /// <summary>
    /// Starts the background auto-cleanup timer.
    /// Call after initializing LibraryService.
    /// A repeated call stops the previous timer first.
    /// </summary>
    public static void StartAutoCleanup()
    {
        StopAutoCleanup();

        LibraryService? library = null;
        try
        {
            library = AppEntry.Services.GetRequiredService<LibraryService>();
        }
        catch
        {
            return;
        }

        var settings = library.Settings.Memory;
        if (!settings.AutoCleanupEnabled || settings.AutoCleanupIntervalMinutes <= 0)
        {
            Log.Info("[Memory] Auto-cleanup disabled by settings");
            return;
        }

        lock (_timerLock)
        {
            _autoCleanupCts = new CancellationTokenSource();
            var token = _autoCleanupCts.Token;
            var interval = TimeSpan.FromMinutes(settings.AutoCleanupIntervalMinutes);

            _ = Task.Run(() => RunAutoCleanupLoopAsync(interval, token), token);
            Log.Info($"[Memory] Auto-cleanup started: every {settings.AutoCleanupIntervalMinutes}min");
        }
    }

    /// <summary>
    /// Stops the background auto-cleanup timer.
    /// </summary>
    public static void StopAutoCleanup()
    {
        lock (_timerLock)
        {
            if (_autoCleanupCts is null) return;

            _autoCleanupCts.Cancel();
            _autoCleanupCts.Dispose();
            _autoCleanupCts = null;
            _autoCleanupTimer?.Dispose();
            _autoCleanupTimer = null;

            Log.Info("[Memory] Auto-cleanup stopped");
        }
    }

    /// <summary>
    /// Restarts auto-cleanup with new settings.
    /// Call when settings change in SettingsViewModel.
    /// </summary>
    public static void RestartAutoCleanup() => StartAutoCleanup();

    /// <summary>
    /// Final cleanup on application shutdown.
    /// </summary>
    public static void Dispose()
    {
        StopAutoCleanup();
    }

    #endregion

    #region Implementation

    private static async Task RunAutoCleanupLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        _autoCleanupTimer = new PeriodicTimer(interval);

        try
        {
            // First run — immediately at startup (cleaning up splash screen artifacts)
            await RunCleanupAsync(aggressive: false);

            while (await _autoCleanupTimer.WaitForNextTickAsync(ct))
            {
                if (ct.IsCancellationRequested) break;

                // Check current settings (user may have modified them)
                bool enabled = false;
                try
                {
                    var lib = AppEntry.Services.GetRequiredService<LibraryService>();
                    enabled = lib.Settings.Memory.AutoCleanupEnabled;
                }
                catch { }

                if (enabled)
                    await RunCleanupAsync(aggressive: false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"[Memory] Auto-cleanup loop error: {ex.Message}");
        }
    }

    private static async Task RunCleanupAsync(bool aggressive)
    {
        try
        {
            bool isPlaying = false;
            int pressureThreshold = 0;

            try
            {
                var lib = AppEntry.Services.GetRequiredService<LibraryService>();
                pressureThreshold = lib.Settings.Memory.PressureThresholdMb;

                var pc = AppEntry.Services.GetRequiredService<PlayerControlService>();
                isPlaying = pc.IsPlaying || pc.IsLoading;
            }
            catch { }

            var gcInfo = GC.GetGCMemoryInfo();
            long committedMb = gcInfo.TotalCommittedBytes / (1024 * 1024);
            long liveMb = GC.GetTotalMemory(false) / (1024 * 1024);

            // Memory pressure check: force aggressive ONLY when audio is NOT playing,
            // to never trigger blocking STW GC and TrimWorkingSet while WASAPI is running.
            if (!aggressive && !isPlaying && pressureThreshold > 0)
            {
                if (committedMb > pressureThreshold || liveMb > pressureThreshold)
                {
                    Log.Info($"[Memory] Pressure detected in idle state (Committed: {committedMb}MB, Live: {liveMb}MB > {pressureThreshold}MB), forcing aggressive");
                    aggressive = true;
                }
            }

            // During active playback, do not evict live covers from ImageCacheService,
            // but always clear dead references in TrackRegistry and Skia path cache (SKPath).
            CleanupAppCaches(aggressive && !isPlaying, clearImages: !isPlaying || aggressive);

            bool hasHeapBloat = (committedMb - liveMb) >= 24 || committedMb > 64;

            if (isPlaying)
            {
                if (hasHeapBloat || aggressive)
                {
                    MemoryHelpers.CompactManagedHeapNow(nonBlockingForAudio: true);
                }
                else
                {
                    GC.Collect(1, GCCollectionMode.Optimized, blocking: false);
                    Log.Info("[Memory] Soft cleanup (playback active)");
                }
            }
            else if (aggressive)
            {
                MemoryHelpers.CompactManagedHeapNow(nonBlockingForAudio: false);
                MemoryHelpers.TrimWorkingSet();

                var afterCommittedMb = GC.GetGCMemoryInfo().TotalCommittedBytes / (1024 * 1024);
                Log.Info($"[Memory] Aggressive cleanup done: {afterCommittedMb}MB committed remaining");
            }
            else
            {
                if (hasHeapBloat)
                {
                    MemoryHelpers.CompactManagedHeapNow(nonBlockingForAudio: false);
                }
                else
                {
                    GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
                    Log.Info("[Memory] Normal cleanup done");
                }
            }

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Log.Error($"[Memory] Cleanup error: {ex.Message}");
        }
    }

    /// <summary>
    /// Clears all internal application caches + Skia non-GPU caches.
    /// When aggressive=true, also attempts to purge the GPU texture cache
    /// via Avalonia Compositor (from the UI thread only).
    /// </summary>
    private static void CleanupAppCaches(bool aggressive = false, bool clearImages = true)
    {
        // ImageCache RAM & DominantColor cache
        if (clearImages)
        {
            try
            {
                AppEntry.Services.GetRequiredService<ImageCacheService>().ClearMemoryCache();
                AppEntry.Services.GetService<DominantColorService>()?.ClearCache();
            }
            catch (Exception ex)
            {
                Log.Warn($"[Memory] ImageCache cleanup error: {ex.Message}");
            }
        }

        // TrackRegistry dead references
        try
        {
            AppEntry.Services.GetRequiredService<TrackRegistry>().CleanupDeadReferences();
        }
        catch (Exception ex)
        {
            Log.Warn($"[Memory] TrackRegistry cleanup error: {ex.Message}");
        }

        // Skia non-GPU caches (fonts, path effects, images)
        try
        {
            SKGraphics.PurgeAllCaches();
            Log.Debug("[Memory] Skia non-GPU caches purged");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Memory] Skia purge error: {ex.Message}");
        }

        // GPU texture cache — aggressive mode only and UI thread only.
        if (aggressive)
        {
            try
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        var mainWindow = Avalonia.Application.Current?.ApplicationLifetime
                            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                                ? desktop.MainWindow : null;

                        if (mainWindow != null)
                        {
                            var visual = Avalonia.Rendering.Composition.ElementComposition.GetElementVisual(mainWindow);

                            if (visual?.Compositor is Avalonia.Rendering.Composition.Compositor compositor)
                            {
                                compositor.RequestCompositionUpdate(() =>
                                {
                                    SKGraphics.PurgeAllCaches();
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[Memory] GPU cache purge error: {ex.Message}");
                    }
                }, Avalonia.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                Log.Warn($"[Memory] GPU purge dispatch error: {ex.Message}");
            }
        }
    }

    #endregion
}