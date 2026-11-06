using System.Runtime;
using System.Runtime.InteropServices;

namespace LMP.Core.Helpers;

/// <summary>
/// Low-level primitives for process virtual and physical memory management,
/// including deterministic Gen 2 / LOH compaction with debouncing after heavy operations.
/// </summary>
public static partial class MemoryHelpers
{
    private static CancellationTokenSource? _postHeavyWorkCts;
    private static readonly Lock _postHeavyWorkLock = new();
    private static long _lastHeavyCompactTicks;

    /// <summary>
    /// Minimum interval between compactions after heavy phases (Stopwatch ticks).
    /// </summary>
    private const int MinHeavyCompactIntervalSeconds = 5;

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
    private static partial nint _GetCurrentProcess();

    [LibraryImport("kernel32.dll", EntryPoint = "SetProcessWorkingSetSize")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool _SetProcessWorkingSetSize(nint process, nint minimumWorkingSetSize, nint maximumWorkingSetSize);

    /// <summary>
    /// Forces trimming of unused physical memory pages (Working Set) at the OS level.
    /// Uses 64-bit <see cref="nint"/> (<c>SIZE_T</c>) parameters and a pseudo-handle to the current process without heap allocations.
    /// </summary>
    public static void TrimWorkingSet()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            // Parameters (nint)(-1), (nint)(-1) (SIZE_T_MAX) instruct Windows to release inactive Working Set pages.
            nint currentProcessHandle = _GetCurrentProcess();
            _SetProcessWorkingSetSize(currentProcessHandle, -1, -1);

            var gcInfo = GC.GetGCMemoryInfo();
            Log.Info($"[Memory] Working set trimmed. Committed Heap: {gcInfo.TotalCommittedBytes / (1024 * 1024)} MB");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Memory] Failed to trim working set: {ex.Message}");
        }
    }

    /// <summary>
    /// Performs immediate managed heap compaction (Gen 2 + LOH) to return
    /// virtual pages (<c>Private: Commit</c>) to the OS after AST parsing or native bridge initialization.
    /// Does not trim Working Set to pagefile, making it safe during active audio playback.
    /// </summary>
    public static void CompactManagedHeapNow(bool nonBlockingForAudio = false)
    {
        try
        {
            long beforeCommitted = GC.GetGCMemoryInfo().TotalCommittedBytes;

            if (nonBlockingForAudio)
            {
                // During active audio playback, use background (concurrent) Gen 2 GC
                // without Stop-The-World LOH compaction and without blocking the WASAPI thread (pause < 1 ms instead of 220 ms).
                GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);
                var bgInfo = GC.GetGCMemoryInfo();
                Log.Info(
                    $"[Memory] Non-blocking background Gen2 GC triggered (playback active): " +
                    $"Committed {beforeCommitted / (1024 * 1024)} MB -> {bgInfo.TotalCommittedBytes / (1024 * 1024)} MB");
                return;
            }

            // When idle (no playback): a single pass with LOH compaction
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

            var gcInfo = GC.GetGCMemoryInfo();
            long afterCommitted = gcInfo.TotalCommittedBytes;
            long liveBytes = GC.GetTotalMemory(false);
            long savedMb = (beforeCommitted - afterCommitted) / (1024 * 1024);

            Log.Info(
                $"[Memory] Post-heavy LOH/Gen2 compaction complete: " +
                $"Committed {beforeCommitted / (1024 * 1024)} MB -> {afterCommitted / (1024 * 1024)} MB " +
                $"(reclaimed {savedMb} MB) | Live Objects: {liveBytes / (1024 * 1024)} MB, " +
                $"HeapSize: {gcInfo.HeapSizeBytes / (1024 * 1024)} MB");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Memory] Managed heap compaction error: {ex.Message}");
        }
    }

    /// <summary>
    /// Schedules deferred Gen 2 + LOH compaction with debouncing after heavy allocations
    /// (parsing <c>base.js</c>, AST compilation, <c>PoToken</c> generation).
    /// Allows collapsing a series of decryptions into a single GC pass without interrupting the audio stream.
    /// </summary>
    /// <param name="delayMs">Debounce delay in milliseconds (default is 2500 ms).</param>
    public static void SchedulePostHeavyWorkCleanup(int delayMs = 2500)
    {
        CancellationToken token;

        lock (_postHeavyWorkLock)
        {
            if (_postHeavyWorkCts is not null)
            {
                _postHeavyWorkCts.Cancel();
                _postHeavyWorkCts.Dispose();
            }

            _postHeavyWorkCts = new CancellationTokenSource();
            token = _postHeavyWorkCts.Token;
        }

        _ = Task.Run(async () =>
        {
            bool completed = await token.DelayNoThrowAsync(TimeSpan.FromMilliseconds(delayMs), continueOnCapturedContext: false);
            if (!completed) return;

            long nowTicks = Environment.TickCount64;
            long lastTicks = Interlocked.Read(ref _lastHeavyCompactTicks);
            if (nowTicks - lastTicks < MinHeavyCompactIntervalSeconds * 1000L)
                return;

            Interlocked.Exchange(ref _lastHeavyCompactTicks, nowTicks);
            CompactManagedHeapNow();
        });
    }
}