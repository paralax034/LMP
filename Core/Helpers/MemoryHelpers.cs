using System.Runtime;
using System.Runtime.InteropServices;

namespace LMP.Core.Helpers;

/// <summary>
/// Низкоуровневые примитивы управления виртуальной и физической памятью процесса,
/// включая детерминированную компактификацию Gen 2 / LOH с дебаунсом после тяжелых операций.
/// </summary>
public static partial class MemoryHelpers
{
    private static CancellationTokenSource? _postHeavyWorkCts;
    private static readonly Lock _postHeavyWorkLock = new();
    private static long _lastHeavyCompactTicks;

    /// <summary>
    /// Минимальный интервал между компактификациями после тяжелых фаз (тиков Stopwatch).
    /// </summary>
    private const int MinHeavyCompactIntervalSeconds = 5;

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
    private static partial nint _GetCurrentProcess();

    [LibraryImport("kernel32.dll", EntryPoint = "SetProcessWorkingSetSize")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool _SetProcessWorkingSetSize(nint process, nint minimumWorkingSetSize, nint maximumWorkingSetSize);

    /// <summary>
    /// Принудительно сбрасывает неиспользуемые страницы физической памяти (Working Set) на уровне ОС.
    /// Использует 64-битные параметры <see cref="nint"/> (<c>SIZE_T</c>) и псевдо-хэндл текущего процесса без аллокаций в куче.
    /// </summary>
    public static void TrimWorkingSet()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            // Параметры (nint)(-1), (nint)(-1) (SIZE_T_MAX) указывают Windows освободить неактивные страницы Working Set.
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
    /// Выполняет немедленную компактификацию управляемой кучи (Gen 2 + LOH) для возврата ОС
    /// виртуальных страниц (<c>Private: Commit</c>) после парсинга AST или инициализации нативных мостов.
    /// Не сбрасывает Working Set в pagefile, поэтому безопасна при активном аудио-воспроизведении.
    /// </summary>
    public static void CompactManagedHeapNow(bool nonBlockingForAudio = false)
    {
        try
        {
            long beforeCommitted = GC.GetGCMemoryInfo().TotalCommittedBytes;

            if (nonBlockingForAudio)
            {
                // Во время активного аудио-воспроизведения используем фоновый (concurrent) GC Gen 2
                // без Stop-The-World компактификации LOH и без блокировки потока WASAPI (пауза < 1 мс вместо 220 мс).
                GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);
                var bgInfo = GC.GetGCMemoryInfo();
                Log.Info(
                    $"[Memory] Non-blocking background Gen2 GC triggered (playback active): " +
                    $"Committed {beforeCommitted / (1024 * 1024)} MB -> {bgInfo.TotalCommittedBytes / (1024 * 1024)} MB");
                return;
            }

            // В простое (без воспроизведения): одиночный проход с компактификацией LOH
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
    /// Планирует отложенную компактификацию Gen 2 + LOH с дебаунсом после тяжелых аллокаций
    /// (разбор <c>base.js</c>, компиляция AST, генерация <c>PoToken</c>).
    /// Позволяет схлопнуть серию расшифровок в один проход GC без прерывания аудио-потока.
    /// </summary>
    /// <param name="delayMs">Задержка дебаунса в миллисекундах (по умолчанию 2500 мс).</param>
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