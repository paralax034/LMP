using System.Buffers.Binary;
using LMP.Tests.Framework;
using LMP.UI.Features.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Tests.Diagnostics;

/// <summary>
/// Live process diagnostics: heap/UI memory anatomy breakdown and Windows NT per-thread context switch profiler.
/// </summary>
public static partial class SystemDiagnostics
{
    [TestMethod(TestCategory.Diagnostic, "Memory Anatomy & Live Heap Breakdown",
        Group = TestGroups.System, Order = 98, RequiresNetwork = false, TimeoutSeconds = 30)]
    public static Task TestMemoryAnatomyDiagnosticAsync()
    {
        using var proc = System.Diagnostics.Process.GetCurrentProcess();
        long wsBeforeMb = proc.WorkingSet64 / (1024 * 1024);
        long privBeforeMb = proc.PrivateMemorySize64 / (1024 * 1024);

        var gcBefore = GC.GetGCMemoryInfo();
        long liveBeforeMb = GC.GetTotalMemory(false) / (1024 * 1024);
        long committedBeforeMb = gcBefore.TotalCommittedBytes / (1024 * 1024);

        var genSizes = FormatGenerationSizes(gcBefore);

        var services = AppEntry.Services;
        var (imgMemoryItems, imgMemoryMb, _, _) = services.GetService<ImageCacheService>()?.GetStats() ?? (0, 0, 0, 0);
        var audioStats = AudioSourceFactory.GlobalCache?.GetStats() ?? default;
        var registry = services.GetService<TrackRegistry>();
        int pinnedTracks = 0;
        if (registry != null)
        {
            foreach (var _ in registry.GetPinnedTracks())
                pinnedTracks++;
        }

        var mainVm = services.GetService<MainWindowViewModel>();
        int prewarmedCount = mainVm?.PrewarmedPages.Count ?? 0;

        int repeaterCount = 0;
        int totalRealizedContainers = 0;
        int activeRealizedContainers = 0;
        var repeaterDetails = new System.Text.StringBuilder(128);

        if (Avalonia.Application.Current is not null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
            {
                if (Avalonia.Application.Current?.ApplicationLifetime is
                    Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } win })
                {
                    foreach (var rep in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(win).OfType<Avalonia.Controls.ItemsRepeater>())
                    {
                        repeaterCount++;
                        int childCount = rep.Children.Count;
                        totalRealizedContainers += childCount;
                        var parentPage = Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<Avalonia.Controls.UserControl>(rep);
                        repeaterDetails.Append($"{parentPage?.GetType().Name ?? rep.Name}:{childCount} ");

                        foreach (var child in rep.Children)
                        {
                            if (child.Bounds.X > -5000 && child.Bounds.Y > -5000)
                                activeRealizedContainers++;
                        }
                    }
                }
            });
        }

        Log.Info(
            $"[MemAnatomy] Repeaters ({repeaterCount}) Total Children: {totalRealizedContainers} (Active On-Screen/Buffer: {activeRealizedContainers}) -> [{repeaterDetails}]");

        Log.Info(
            $"[MemAnatomy] BEFORE GC -> WorkingSet: {wsBeforeMb} MB | PrivateBytes (Native+Managed): {privBeforeMb} MB | " +
            $"Managed Committed: {committedBeforeMb} MB | Managed Live: {liveBeforeMb} MB | Generations: [{genSizes}]");

        Log.Info(
            $"[MemAnatomy] Caches -> ImageCache RAM: {imgMemoryItems} items (~{imgMemoryMb} MB) | " +
            $"AudioCache Index: {audioStats.TotalEntries} entries | TrackRegistry Pinned: {pinnedTracks} tracks | " +
            $"Shell PrewarmedPages: {prewarmedCount}");

        SkiaSharp.SKGraphics.PurgeAllCaches();
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        proc.Refresh();
        var gcAfter = GC.GetGCMemoryInfo();
        long wsAfterMb = proc.WorkingSet64 / (1024 * 1024);
        long privAfterMb = proc.PrivateMemorySize64 / (1024 * 1024);
        long liveAfterMb = GC.GetTotalMemory(false) / (1024 * 1024);
        long committedAfterMb = gcAfter.TotalCommittedBytes / (1024 * 1024);
        long nativePrivateMb = Math.Max(0, privAfterMb - committedAfterMb);

        Log.Info(
            $"[MemAnatomy] AFTER GC+SkiaPurge -> WorkingSet: {wsAfterMb} MB | PrivateBytes: {privAfterMb} MB " +
            $"(Managed Committed: {committedAfterMb} MB, Unmanaged/Native: ~{nativePrivateMb} MB) | " +
            $"Managed Live: {liveAfterMb} MB | Generations: [{FormatGenerationSizes(gcAfter)}]");

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Diagnostic, "Windows NT Per-Thread Context Switches (20s Live Window)",
        Group = TestGroups.System, Order = 99, RequiresNetwork = false, TimeoutSeconds = 30)]
    public static async Task TestThreadContextSwitchesDiagnosticAsync()
    {
        if (!OperatingSystem.IsWindows())
            return;

        const double windowSec = 20.0;

        await Task.Delay(150).ConfigureAwait(false);
        Log.Info($"[CS-Diagnostic] Measuring kernel context switches and CPU time for {windowSec:F0}s (you can play/seek/equalize tracks now)...");

        var snapBefore = CaptureProcessThreadsSnapshot();
        long startTs = System.Diagnostics.Stopwatch.GetTimestamp();

        await Task.Delay(TimeSpan.FromSeconds(windowSec)).ConfigureAwait(false);

        double actualSec = Math.Max(0.1, System.Diagnostics.Stopwatch.GetElapsedTime(startTs).TotalSeconds);
        var snapAfter = CaptureProcessThreadsSnapshot();

        ulong totalCs = 0;
        double totalCpuMs = 0;
        int newlySpawnedThreads = 0;
        var perThreadDeltas = new List<(int Tid, uint DeltaCs, double CpuMs, uint WaitReason, nint StartAddress, bool IsNew)>(snapAfter.Count);

        foreach (var (tid, after) in snapAfter)
        {
            uint deltaCs;
            long deltaTime100Ns;
            bool isNew;

            if (snapBefore.TryGetValue(tid, out var before))
            {
                deltaCs = after.ContextSwitches >= before.ContextSwitches
                    ? after.ContextSwitches - before.ContextSwitches
                    : after.ContextSwitches;
                deltaTime100Ns = Math.Max(0L, after.KernelTime - before.KernelTime + (after.UserTime - before.UserTime));
                isNew = false;
            }
            else
            {
                deltaCs = after.ContextSwitches;
                deltaTime100Ns = Math.Max(0L, after.KernelTime + after.UserTime);
                isNew = true;
                newlySpawnedThreads++;
            }

            double cpuMs = deltaTime100Ns / 10_000.0;
            totalCs += deltaCs;
            totalCpuMs += cpuMs;

            if (deltaCs > 0 || cpuMs > 0.1)
                perThreadDeltas.Add((tid, deltaCs, cpuMs, after.WaitReason, after.StartAddress, isNew));
        }

        perThreadDeltas.Sort(static (a, b) => b.DeltaCs.CompareTo(a.DeltaCs));

        var modules = CaptureModuleRanges();
        var topBuilder = new System.Text.StringBuilder(320);
        int topCount = Math.Min(6, perThreadDeltas.Count);
        for (int i = 0; i < topCount; i++)
        {
            if (i > 0) topBuilder.Append(", ");
            var (Tid, DeltaCs, CpuMs, WaitReason, StartAddress, IsNew) = perThreadDeltas[i];
            var tName = TryGetWindowsThreadName(Tid) ?? TryFormatModuleOffset(StartAddress, modules);
            string newTag = IsNew ? "+" : "";
            if (!string.IsNullOrEmpty(tName))
                topBuilder.Append($"{newTag}TID {Tid}({tName},w={WaitReason})={DeltaCs / actualSec:F0} CS/s ({CpuMs:F0}ms CPU)");
            else
                topBuilder.Append($"{newTag}TID {Tid}(w={WaitReason})={DeltaCs / actualSec:F0} CS/s ({CpuMs:F0}ms CPU)");
        }

        Log.Info(
            $"[CS-Diagnostic] Threads: {snapAfter.Count} (+{newlySpawnedThreads} new) | " +
            $"Window: {actualSec:F1}s | Total CS: {totalCs:N0} ({totalCs / actualSec:F0} CS/s) | " +
            $"Total CPU: {totalCpuMs:F0}ms ({totalCpuMs / (actualSec * 10.0):F2}% of 1 core) | Top: [{topBuilder}]");
    }

    private static string FormatGenerationSizes(GCMemoryInfo gcInfo)
    {
        var sb = new System.Text.StringBuilder(128);
        for (int g = 0; g < gcInfo.GenerationInfo.Length; g++)
        {
            string genName = g switch
            {
                0 => "Gen0",
                1 => "Gen1",
                2 => "Gen2",
                3 => "LOH",
                4 => "POH",
                _ => $"G{g}"
            };
            long sizeKb = gcInfo.GenerationInfo[g].SizeAfterBytes / 1024;
            if (g > 0) sb.Append(", ");
            sb.Append($"{genName}={sizeKb:N0}KB");
        }
        return sb.ToString();
    }

    private readonly record struct ThreadKernelInfo(uint ContextSwitches, long KernelTime, long UserTime, uint WaitReason, nint StartAddress);

    private static List<(nint BaseAddr, nint EndAddr, string Name)> CaptureModuleRanges()
    {
        var list = new List<(nint BaseAddr, nint EndAddr, string Name)>(64);
        try
        {
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            foreach (System.Diagnostics.ProcessModule mod in proc.Modules)
            {
                nint baseAddr = mod.BaseAddress;
                nint endAddr = baseAddr + mod.ModuleMemorySize;
                list.Add((baseAddr, endAddr, mod.ModuleName));
            }
        }
        catch { }
        return list;
    }

    private static string? TryFormatModuleOffset(nint startAddr, List<(nint BaseAddr, nint EndAddr, string Name)> modules)
    {
        if (startAddr == 0) return null;
        for (int i = 0; i < modules.Count; i++)
        {
            var (baseAddr, endAddr, name) = modules[i];
            if (startAddr >= baseAddr && startAddr < endAddr)
            {
                long offset = startAddr - baseAddr;
                return $"{name}+0x{offset:x}";
            }
        }
        return $"0x{startAddr:x}";
    }

    private static unsafe string? TryGetWindowsThreadName(int tid)
    {
        const uint THREAD_QUERY_LIMITED_INFORMATION = 0x0800;
        nint hThread = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, 0, (uint)tid);
        if (hThread == 0) return null;

        try
        {
            if (GetThreadDescription(hThread, out nint pDesc) >= 0 && pDesc != 0)
            {
                try
                {
                    return System.Runtime.InteropServices.Marshal.PtrToStringUni(pDesc);
                }
                finally
                {
                    LocalFree(pDesc);
                }
            }
        }
        finally
        {
            CloseHandle(hThread);
        }

        return null;
    }

    private static unsafe Dictionary<int, ThreadKernelInfo> CaptureProcessThreadsSnapshot()
    {
        var result = new Dictionary<int, ThreadKernelInfo>(64);
        int currentPid = Environment.ProcessId;

        int bufferSize = 2 * 1024 * 1024;
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(bufferSize);

        try
        {
            int status;
            fixed (byte* pBuffer = buffer)
            {
                status = NtQuerySystemInformation(5, (nint)pBuffer, buffer.Length, out int returnLength);

                if (status != 0 && returnLength > buffer.Length)
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                    buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(returnLength + 65536);
                    fixed (byte* pRetry = buffer)
                    {
                        status = NtQuerySystemInformation(5, (nint)pRetry, buffer.Length, out _);
                    }
                }
            }

            if (status == 0)
            {
                ReadOnlySpan<byte> span = buffer;
                int offset = 0;
                const int SystemProcessInfoSizeX64 = 256;
                const int SystemThreadInfoSizeX64 = 80;

                while (offset + SystemProcessInfoSizeX64 <= span.Length)
                {
                    uint nextEntryOffset = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
                    uint numberOfThreads = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 4, 4));
                    nint uniqueProcessId = (nint)BinaryPrimitives.ReadInt64LittleEndian(span.Slice(offset + 80, 8));

                    if (uniqueProcessId == currentPid)
                    {
                        int threadOffset = offset + SystemProcessInfoSizeX64;
                        for (uint i = 0; i < numberOfThreads && threadOffset + SystemThreadInfoSizeX64 <= span.Length; i++, threadOffset += SystemThreadInfoSizeX64)
                        {
                            long kernelTime = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(threadOffset, 8));
                            long userTime = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(threadOffset + 8, 8));
                            nint startAddress = (nint)BinaryPrimitives.ReadInt64LittleEndian(span.Slice(threadOffset + 24, 8));
                            int tid = (int)BinaryPrimitives.ReadInt64LittleEndian(span.Slice(threadOffset + 48, 8));
                            uint contextSwitches = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(threadOffset + 64, 4));
                            uint waitReason = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(threadOffset + 72, 4));

                            result[tid] = new ThreadKernelInfo(contextSwitches, kernelTime, userTime, waitReason, startAddress);
                        }
                        break;
                    }

                    if (nextEntryOffset == 0) break;
                    offset += (int)nextEntryOffset;
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }

        return result;
    }

    [System.Runtime.InteropServices.LibraryImport("ntdll.dll")]
    private static partial int NtQuerySystemInformation(
        int systemInformationClass, nint systemInformation, int systemInformationLength, out int returnLength);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    private static partial nint OpenThread(uint dwDesiredAccess, int bInheritHandle, uint dwThreadId);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    private static partial int GetThreadDescription(nint hThread, out nint ppszThreadDescription);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint hMem);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(nint hObject);
}