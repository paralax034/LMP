using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Threading;

namespace LMP.Core.Diagnostics;

/// <summary>
/// Высокопроизводительный низкоуровневый рекордер телеметрии процесса в стиле System Informer.
/// Использует source-generated <see cref="LibraryImportAttribute"/>, разрешает реальные Win32 стартовые адреса
/// и имена потоков, выводит чистый ASCII-лог без спецсимволов и локализационного мусора.
/// </summary>
public sealed unsafe partial class ProcessFlightRecorder : IDisposable
{
    private const int PollIntervalMs = 1_000;
    private const int HeartbeatEveryTicks = 15;

    // Пороги срабатывания детектора аномалий (Spike Detector)
    private const double SpikeCpuPercentThreshold = 15.0;
    private const double SpikeUiLatencyMsThreshold = 35.0;
    private const double SpikeAllocMbPerSecThreshold = 12.0;
    private const uint SpikeContextSwitchesDeltaThreshold = 6_000;
    private const uint SpikeHardFaultsDeltaThreshold = 5;
    private const double SpikeIoDeltaBytesThreshold = 8.0 * 1024 * 1024;
    private const ulong MinThreadCyclesDeltaToPrint = 1_500_000;
    private const int MaxTopThreadsToPrint = 10;

    private static readonly Lock _syncLock = new();
    private static ProcessFlightRecorder? _activeInstance;

    private readonly Thread _recorderThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _uiPingEvent = new(false);
    private readonly string _telemetryFilePath;
    private readonly uint _currentPid;
    private readonly uint _mainUiThreadId;
    private readonly int _processorCount;
    private volatile bool _disposed;

    // Кэш состояния предыдущего тика для расчёта дельт (Zero-Alloc)
    private const int MaxTrackedThreads = 256;
    private readonly uint[] _prevThreadIds = new uint[MaxTrackedThreads];
    private readonly ulong[] _prevThreadCycles = new ulong[MaxTrackedThreads];
    private readonly uint[] _prevThreadCtxSwitches = new uint[MaxTrackedThreads];
    private readonly long[] _prevThreadKernelTime = new long[MaxTrackedThreads];
    private readonly long[] _prevThreadUserTime = new long[MaxTrackedThreads];
    private int _prevThreadCount;

    private readonly ThreadDeltaEntry[] _threadSortBuffer = new ThreadDeltaEntry[MaxTrackedThreads];

    // Кэш загруженных модулей (DLL/EXE) для мгновенного маппинга адреса в Module+0xOffset
    private const int MaxCachedModules = 160;
    private readonly nuint[] _moduleBases = new nuint[MaxCachedModules];
    private readonly uint[] _moduleSizes = new uint[MaxCachedModules];
    private readonly string[] _moduleNames = new string[MaxCachedModules];
    private int _moduleCount;
    private long _lastModuleRefreshTick;

    // Кэш разрешенных имен и стартовых адресов потоков (TID -> строка "Module+0xOffset (ThreadName)")
    private readonly uint[] _threadIdentityTids = new uint[MaxTrackedThreads];
    private readonly string[] _threadIdentityLabels = new string[MaxTrackedThreads];
    private int _threadIdentityCount;

    // Предыдущие метрики уровня процесса
    private long _prevSampleTimestamp;
    private long _prevKernelTime100Ns;
    private long _prevUserTime100Ns;
    private ulong _prevTotalCycles;
    private ulong _prevTotalCtxSwitches;
    private uint _prevPageFaults;
    private uint _prevHardFaults;
    private long _prevPrivateBytes;
    private ulong _prevReadOps;
    private ulong _prevWriteOps;
    private ulong _prevOtherOps;
    private ulong _prevReadBytes;
    private ulong _prevWriteBytes;
    private ulong _prevOtherBytes;
    private long _prevGcAllocatedBytes;
    private int _prevGen0;
    private int _prevGen1;
    private int _prevGen2;
    private bool _hasBaseline;

    private long _uiPingStartTimestamp;
    private long _uiPingEndTimestamp;

    /// <summary>
    /// Глобальный флаг активности рекордера телеметрии.
    /// </summary>
    public static bool IsEnabled { get; private set; }

    /// <summary>
    /// Путь к текущему файлу детальной телеметрии.
    /// </summary>
    public string TelemetryFilePath => _telemetryFilePath;

    /// <summary>
    /// Включает или выключает глобальный экземпляр рекордера телеметрии.
    /// </summary>
    public static void SetEnabled(bool enabled, string? logDirectory = null)
    {
        lock (_syncLock)
        {
            if (enabled == IsEnabled && (enabled ? _activeInstance is not null : _activeInstance is null))
                return;

            IsEnabled = enabled;

            if (enabled)
            {
                _activeInstance?.Dispose();
                _activeInstance = new ProcessFlightRecorder(logDirectory);
                _activeInstance.Start();
            }
            else
            {
                _activeInstance?.Dispose();
                _activeInstance = null;
            }
        }
    }

    /// <summary>
    /// Останавливает глобальный экземпляр при завершении приложения.
    /// </summary>
    public static void ShutdownGlobal() => SetEnabled(false);

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="ProcessFlightRecorder"/>.
    /// </summary>
    public ProcessFlightRecorder(string? dumpDirectory = null)
    {
        var dir = dumpDirectory ?? G.Folder.Logs;
        Directory.CreateDirectory(dir);

        _telemetryFilePath = Path.Combine(dir, $"telemetry_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");
        _currentPid = (uint)Environment.ProcessId;
        _mainUiThreadId = OperatingSystem.IsWindows() ? GetCurrentThreadId() : 0;
        _processorCount = Math.Max(1, Environment.ProcessorCount);

        _recorderThread = new Thread(RecorderLoop)
        {
            Name = "LMP-FlightRecorder",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
    }

    /// <summary>
    /// Запускает фоновый поток сбора телеметрии.
    /// </summary>
    public void Start()
    {
        if (_disposed || _recorderThread.IsAlive) return;
        IsEnabled = true;
        _recorderThread.Start();
        Log.Info($"[Telemetry] Flight Recorder started -> {_telemetryFilePath}");
    }

    private void RecorderLoop()
    {
        var token = _cts.Token;
        byte[]? queryBuffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        var sb = new StringBuilder(4096);

        try
        {
            using var fs = new FileStream(
                _telemetryFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 8192,
                useAsync: false);
            using var writer = new StreamWriter(fs, Encoding.UTF8);

            WriteHeader(writer, sb);

            int tickCounter = 0;

            while (!token.IsCancellationRequested && !_disposed)
            {
                if (token.WaitHandle.WaitOne(PollIntervalMs))
                    break;

                tickCounter++;
                double uiLatencyMs = MeasureUiPingLatencyMs();

                if (OperatingSystem.IsWindows())
                {
                    if (!SampleWindowsProcessAndThreads(ref queryBuffer, uiLatencyMs, tickCounter, sb, writer))
                    {
                        SampleCrossPlatformFallback(uiLatencyMs, tickCounter, sb, writer);
                    }
                }
                else
                {
                    SampleCrossPlatformFallback(uiLatencyMs, tickCounter, sb, writer);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"[Telemetry] Recorder loop stopped: {ex.Message}");
        }
        finally
        {
            if (queryBuffer is not null)
                ArrayPool<byte>.Shared.Return(queryBuffer);
        }
    }

    private double MeasureUiPingLatencyMs()
    {
        if (!_hasBaseline)
            return 0.0;

        try
        {
            _uiPingEvent.Reset();
            Volatile.Write(ref _uiPingStartTimestamp, Stopwatch.GetTimestamp());
            Volatile.Write(ref _uiPingEndTimestamp, 0);

            Dispatcher.UIThread.Post(static state =>
            {
                var self = (ProcessFlightRecorder)state!;
                Volatile.Write(ref self._uiPingEndTimestamp, Stopwatch.GetTimestamp());
                try
                {
                    self._uiPingEvent.Set();
                }
                catch (ObjectDisposedException)
                {
                }
            }, this, DispatcherPriority.Send);

            if (_uiPingEvent.Wait(400))
            {
                long end = Volatile.Read(ref _uiPingEndTimestamp);
                long start = Volatile.Read(ref _uiPingStartTimestamp);
                if (end >= start)
                    return Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;
            }
            else
            {
                return 400.0;
            }
        }
        catch
        {
        }

        return 0.0;
    }

    private void WriteHeader(StreamWriter writer, StringBuilder sb)
    {
        sb.Clear();
        sb.AppendLine("# ===============================================================================================");
        sb.Append("# LMP Process Flight Recorder | Build: ")
          .Append(G.Build.FullVersionString)
          .Append(" | PID: ").Append(_currentPid)
          .Append(" | UI_TID: ").Append(_mainUiThreadId)
          .Append(" | Cores: ").Append(_processorCount)
          .AppendLine();
        sb.AppendLine("# Mode: Adaptive (HEARTBEAT every 15s + SPIKE snapshot with Top Active Threads)");
        sb.AppendLine("# Format: Plain ASCII, InvariantCulture (dot decimals, underscore thousands)");
        sb.AppendLine("# ===============================================================================================");
        writer.Write(sb);
        writer.Flush();
    }

    private bool SampleWindowsProcessAndThreads(
        ref byte[] buffer,
        double uiLatencyMs,
        int tickCounter,
        StringBuilder sb,
        StreamWriter writer)
    {
        const int SystemProcessInformationClass = 5;
        const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;

        while (true)
        {
            fixed (byte* pBuf = buffer)
            {
                uint status = NtQuerySystemInformation(
                    SystemProcessInformationClass,
                    pBuf,
                    (uint)buffer.Length,
                    out uint returnLength);

                if (status == STATUS_INFO_LENGTH_MISMATCH)
                {
                    int newSize = Math.Max(buffer.Length * 2, (int)returnLength + 65536);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = ArrayPool<byte>.Shared.Rent(newSize);
                    continue;
                }

                if (status != 0)
                    return false;

                long nowTimestamp = Stopwatch.GetTimestamp();
                byte* ptr = pBuf;

                while (true)
                {
                    var proc = (SYSTEM_PROCESS_INFORMATION*)ptr;
                    if ((uint)proc->UniqueProcessId == _currentPid)
                    {
                        ProcessMatchedSnapshot(proc, nowTimestamp, uiLatencyMs, tickCounter, sb, writer);
                        return true;
                    }

                    if (proc->NextEntryOffset == 0)
                        break;

                    ptr += proc->NextEntryOffset;
                }

                return false;
            }
        }
    }

    private void ProcessMatchedSnapshot(
        SYSTEM_PROCESS_INFORMATION* proc,
        long nowTimestamp,
        double uiLatencyMs,
        int tickCounter,
        StringBuilder sb,
        StreamWriter writer)
    {
        IntPtr hProcess = GetCurrentProcess();

        QueryProcessCycleTime(hProcess, out ulong totalProcessCycles);

        uint gdiHandles = GetGuiResources(hProcess, 0);
        uint userHandles = GetGuiResources(hProcess, 1);

        uint hardFaults = 0;
        VM_COUNTERS_EX2 vmEx2 = default;
        if (NtQueryInformationProcess(
                hProcess,
                ProcessVmCountersClass,
                &vmEx2,
                (uint)sizeof(VM_COUNTERS_EX2),
                out _) == 0)
        {
            hardFaults = vmEx2.HardFaultCount;
        }

        long gcAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
        var gcInfo = GC.GetGCMemoryInfo();
        long gcHeapBytes = gcInfo.HeapSizeBytes;
        int gen0 = GC.CollectionCount(0);
        int gen1 = GC.CollectionCount(1);
        int gen2 = GC.CollectionCount(2);

        int threadCount = (int)Math.Min(proc->NumberOfThreads, (uint)MaxTrackedThreads);
        var threadsPtr = (SYSTEM_THREAD_INFORMATION*)((byte*)proc + sizeof(SYSTEM_PROCESS_INFORMATION));

        ulong totalCtxSwitches = 0;
        int activeDeltaThreadsCount = 0;

        // Временный стек-буфер под текущие циклы потоков, чтобы не вызывать OpenThread дважды за тик
        ulong* currentThreadCycles = stackalloc ulong[threadCount];

        for (int i = 0; i < threadCount; i++)
        {
            ref SYSTEM_THREAD_INFORMATION t = ref threadsPtr[i];
            uint tid = (uint)t.ClientId.UniqueThread;

            ulong threadCycles = 0;
            IntPtr hThread = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, false, tid);
            if (hThread != IntPtr.Zero)
            {
                QueryThreadCycleTime(hThread, out threadCycles);
                CloseHandle(hThread);
            }

            currentThreadCycles[i] = threadCycles;
            totalCtxSwitches += t.ContextSwitches;

            if (_hasBaseline)
            {
                int prevIdx = FindPreviousThreadIndex(tid);
                ulong cyclesDelta = prevIdx >= 0 && threadCycles >= _prevThreadCycles[prevIdx]
                    ? threadCycles - _prevThreadCycles[prevIdx]
                    : threadCycles;

                uint ctxDelta = prevIdx >= 0 && t.ContextSwitches >= _prevThreadCtxSwitches[prevIdx]
                    ? t.ContextSwitches - _prevThreadCtxSwitches[prevIdx]
                    : t.ContextSwitches;

                long kDelta = prevIdx >= 0 ? t.KernelTime - _prevThreadKernelTime[prevIdx] : t.KernelTime;
                long uDelta = prevIdx >= 0 ? t.UserTime - _prevThreadUserTime[prevIdx] : t.UserTime;

                if (cyclesDelta >= MinThreadCyclesDeltaToPrint || ctxDelta >= 250)
                {
                    _threadSortBuffer[activeDeltaThreadsCount++] = new ThreadDeltaEntry
                    {
                        ThreadId = tid,
                        TotalCycles = threadCycles,
                        CyclesDelta = cyclesDelta,
                        TotalCtxSwitches = t.ContextSwitches,
                        CtxSwitchesDelta = ctxDelta,
                        KernelPlusUserDelta100Ns = Math.Max(0, kDelta + uDelta),
                        FallbackStartAddress = (nuint)t.StartAddress,
                        BasePriority = t.BasePriority,
                        State = t.ThreadState,
                        WaitReason = t.WaitReason
                    };
                }
            }
        }

        // Обновляем baseline потоков без повторных системных вызовов
        _prevThreadCount = threadCount;
        for (int i = 0; i < threadCount; i++)
        {
            ref SYSTEM_THREAD_INFORMATION t = ref threadsPtr[i];
            _prevThreadIds[i] = (uint)t.ClientId.UniqueThread;
            _prevThreadCycles[i] = currentThreadCycles[i];
            _prevThreadCtxSwitches[i] = t.ContextSwitches;
            _prevThreadKernelTime[i] = t.KernelTime;
            _prevThreadUserTime[i] = t.UserTime;
        }

        if (!_hasBaseline)
        {
            SaveProcessBaseline(
                proc, nowTimestamp, totalProcessCycles, totalCtxSwitches,
                hardFaults, gcAllocatedBytes, gen0, gen1, gen2);
            return;
        }

        double elapsedSec = Math.Max(0.001, Stopwatch.GetElapsedTime(_prevSampleTimestamp, nowTimestamp).TotalSeconds);
        double elapsed100Ns = elapsedSec * 10_000_000.0;

        long kernelDelta100Ns = Math.Max(0, proc->KernelTime - _prevKernelTime100Ns);
        long userDelta100Ns = Math.Max(0, proc->UserTime - _prevUserTime100Ns);
        long totalCpuDelta100Ns = kernelDelta100Ns + userDelta100Ns;

        double cpuTotalPercent = (totalCpuDelta100Ns / (elapsed100Ns * _processorCount)) * 100.0;
        double cpuUserPercent = (userDelta100Ns / (elapsed100Ns * _processorCount)) * 100.0;
        double cpuKernelPercent = (kernelDelta100Ns / (elapsed100Ns * _processorCount)) * 100.0;

        ulong cyclesDeltaProc = totalProcessCycles >= _prevTotalCycles
            ? totalProcessCycles - _prevTotalCycles
            : 0;
        ulong ctxSwitchesDeltaProc = totalCtxSwitches >= _prevTotalCtxSwitches
            ? totalCtxSwitches - _prevTotalCtxSwitches
            : 0;

        uint pageFaultsDelta = proc->PageFaultCount >= _prevPageFaults
            ? proc->PageFaultCount - _prevPageFaults
            : 0;
        uint hardFaultsDelta = hardFaults >= _prevHardFaults
            ? hardFaults - _prevHardFaults
            : 0;

        long privateBytes = (long)proc->PrivatePageCount;
        long privateBytesDelta = privateBytes - _prevPrivateBytes;

        ulong readOpsDelta = proc->ReadOperationCount - _prevReadOps;
        ulong writeOpsDelta = proc->WriteOperationCount - _prevWriteOps;
        ulong otherOpsDelta = proc->OtherOperationCount - _prevOtherOps;

        ulong readBytesDelta = proc->ReadTransferCount - _prevReadBytes;
        ulong writeBytesDelta = proc->WriteTransferCount - _prevWriteBytes;
        ulong otherBytesDelta = proc->OtherTransferCount - _prevOtherBytes;

        long allocDeltaBytes = Math.Max(0, gcAllocatedBytes - _prevGcAllocatedBytes);
        double allocMbPerSec = (allocDeltaBytes / (1024.0 * 1024.0)) / elapsedSec;

        int gen0Delta = gen0 - _prevGen0;
        int gen1Delta = gen1 - _prevGen1;
        int gen2Delta = gen2 - _prevGen2;

        bool isSpike =
            cpuTotalPercent >= SpikeCpuPercentThreshold ||
            uiLatencyMs >= SpikeUiLatencyMsThreshold ||
            allocMbPerSec >= SpikeAllocMbPerSecThreshold ||
            ctxSwitchesDeltaProc >= SpikeContextSwitchesDeltaThreshold ||
            hardFaultsDelta >= SpikeHardFaultsDeltaThreshold ||
            (readBytesDelta + writeBytesDelta) >= SpikeIoDeltaBytesThreshold;

        bool isHeartbeat = (tickCounter % HeartbeatEveryTicks) == 0;

        if (isSpike || isHeartbeat)
        {
            SortActiveThreads(activeDeltaThreadsCount);

            if (isSpike)
            {
                EnsureModulesCached(hProcess, nowTimestamp);

                uint topTid = activeDeltaThreadsCount > 0 ? _threadSortBuffer[0].ThreadId : 0;
                ulong topTidCycles = activeDeltaThreadsCount > 0 ? _threadSortBuffer[0].CyclesDelta : 0;
                string topMod = activeDeltaThreadsCount > 0
                    ? ResolveThreadIdentity(topTid, _threadSortBuffer[0].FallbackStartAddress)
                    : "n/a";

                Log.Warn(string.Create(CultureInfo.InvariantCulture,
                    $"[PerfSpike] CPU:{cpuTotalPercent:F2}% (U:{cpuUserPercent:F2}% K:{cpuKernelPercent:F2}% dCyc:{FormatShortCount(cyclesDeltaProc)}) | " +
                    $"UI:{uiLatencyMs:F1}ms | Alloc:{allocMbPerSec:F1}MB/s (Priv:{privateBytes / (1024.0 * 1024.0):F1}MB WS:{proc->WorkingSetSize / (1024.0 * 1024.0):F1}MB) | " +
                    $"dCtxSw:{ctxSwitchesDeltaProc} | dIO R:{readBytesDelta / 1024}KB W:{writeBytesDelta / 1024}KB | " +
                    $"TopThr:#{topTid}[{topMod}] dCyc:{FormatShortCount(topTidCycles)}"));
            }

            sb.Clear();
            AppendSummaryLine(
                sb, isSpike ? "SPIKE" : "HEARTBEAT",
                cpuTotalPercent, cpuUserPercent, cpuKernelPercent,
                cyclesDeltaProc, ctxSwitchesDeltaProc,
                privateBytes, privateBytesDelta, (long)proc->WorkingSetSize,
                gcHeapBytes, allocMbPerSec, gen0Delta, gen1Delta, gen2Delta,
                pageFaultsDelta, hardFaultsDelta,
                readBytesDelta, writeBytesDelta,
                proc->HandleCount, gdiHandles, userHandles,
                (int)proc->NumberOfThreads, uiLatencyMs);

            if (isSpike)
            {
                AppendSpikeDetailsAscii(
                    sb, proc, elapsed100Ns,
                    cpuTotalPercent, cpuUserPercent, cpuKernelPercent,
                    totalProcessCycles, cyclesDeltaProc,
                    kernelDelta100Ns, userDelta100Ns,
                    totalCtxSwitches, ctxSwitchesDeltaProc,
                    privateBytes, privateBytesDelta,
                    hardFaults, hardFaultsDelta, pageFaultsDelta,
                    readOpsDelta, writeOpsDelta, otherOpsDelta,
                    readBytesDelta, writeBytesDelta, otherBytesDelta,
                    gdiHandles, userHandles,
                    gcHeapBytes, allocMbPerSec,
                    activeDeltaThreadsCount);
            }

            writer.Write(sb);
            writer.Flush();
        }

        SaveProcessBaseline(
            proc, nowTimestamp, totalProcessCycles, totalCtxSwitches,
            hardFaults, gcAllocatedBytes, gen0, gen1, gen2);
    }

    private static void AppendSummaryLine(
        StringBuilder sb,
        string tag,
        double cpuTotal, double cpuUser, double cpuKernel,
        ulong cyclesDelta, ulong ctxSwDelta,
        long privateBytes, long privateDelta, long workingSetBytes,
        long gcHeapBytes, double allocMbPerSec, int g0Delta, int g1Delta, int g2Delta,
        uint pfDelta, uint hfDelta,
        ulong readBytesDelta, ulong writeBytesDelta,
        uint handles, uint gdiHandles, uint userHandles,
        int threads, double uiMs)
    {
        var inv = CultureInfo.InvariantCulture;
        Span<char> timeBuf = stackalloc char[12];
        if (DateTime.Now.TryFormat(timeBuf, out int written, "HH:mm:ss.fff", inv))
            sb.Append('[').Append(timeBuf[..written]).Append("] ");
        else
            sb.Append('[').Append(DateTime.Now.ToString("HH:mm:ss.fff", inv)).Append("] ");

        sb.Append(inv, $"[{tag}] ")
          .Append(inv, $"CPU:{cpuTotal:F2}%(U:{cpuUser:F2}%/K:{cpuKernel:F2}%) | ")
          .Append(inv, $"dCyc:{FormatShortCount(cyclesDelta)} | ")
          .Append(inv, $"dCtxSw:{ctxSwDelta} | ")
          .Append(inv, $"Priv:{privateBytes / (1024.0 * 1024.0):F2}MB({(privateDelta >= 0 ? "+" : "")}{privateDelta / 1024.0:F0}KB) | ")
          .Append(inv, $"WS:{workingSetBytes / (1024.0 * 1024.0):F2}MB | ")
          .Append(inv, $"GC:{gcHeapBytes / (1024.0 * 1024.0):F2}MB(Alloc:{allocMbPerSec:F2}MB/s dG:{g0Delta}/{g1Delta}/{g2Delta}) | ")
          .Append(inv, $"dPF:{pfDelta}(dHF:{hfDelta}) | ")
          .Append(inv, $"dIO R:{readBytesDelta / 1024}KB W:{writeBytesDelta / 1024}KB | ")
          .Append(inv, $"Hnd:{handles}(G:{gdiHandles}/U:{userHandles}) | ")
          .Append(inv, $"Thr:{threads} | ")
          .Append(inv, $"UI:{uiMs:F1}ms")
          .AppendLine();
    }

    private void AppendSpikeDetailsAscii(
        StringBuilder sb,
        SYSTEM_PROCESS_INFORMATION* proc,
        double elapsed100Ns,
        double cpuTotal, double cpuUser, double cpuKernel,
        ulong totalCycles, ulong cyclesDelta,
        long kernelDelta100Ns, long userDelta100Ns,
        ulong totalCtxSw, ulong ctxSwDelta,
        long privateBytes, long privateDelta,
        uint hardFaults, uint hardFaultsDelta, uint pageFaultsDelta,
        ulong readOpsDelta, ulong writeOpsDelta, ulong otherOpsDelta,
        ulong readBytesDelta, ulong writeBytesDelta, ulong otherBytesDelta,
        uint gdiHandles, uint userHandles,
        long gcHeapBytes, double allocMbPerSec,
        int activeThreadsCount)
    {
        var inv = CultureInfo.InvariantCulture;

        sb.AppendLine("  [Process Statistics]");
        sb.Append(inv, $"    CPU           : {cpuTotal:F2}% (User: {cpuUser:F2}%, Kernel: {cpuKernel:F2}%, Relative: {cpuTotal * _processorCount:F2}%)").AppendLine();
        sb.Append(inv, $"    Cycles        : {FormatGrouped(totalCycles)} (dCyc: +{FormatGrouped(cyclesDelta)}) | CtxSwitches: {FormatGrouped(totalCtxSw)} (dCtxSw: +{FormatGrouped(ctxSwDelta)})").AppendLine();
        sb.Append(inv, $"    Times         : Kernel={TimeSpan.FromTicks(proc->KernelTime):hh\\:mm\\:ss\\.fff} (+{kernelDelta100Ns / 10_000.0:F2}ms) | User={TimeSpan.FromTicks(proc->UserTime):hh\\:mm\\:ss\\.fff} (+{userDelta100Ns / 10_000.0:F2}ms)").AppendLine();
        sb.Append(inv, $"    Memory        : Priv={FormatBytes((ulong)privateBytes)} ({(privateDelta >= 0 ? "+" : "-")}{FormatBytes((ulong)Math.Abs(privateDelta))}, Peak={FormatBytes(proc->PeakPagefileUsage)}) | WS={FormatBytes(proc->WorkingSetSize)} (Peak={FormatBytes(proc->PeakWorkingSetSize)}, PrivWS={FormatBytes((ulong)proc->WorkingSetPrivateSize)})").AppendLine();
        sb.Append(inv, $"    Faults & Pool : PageFaults={FormatGrouped(proc->PageFaultCount)} (+{pageFaultsDelta}) | HardFaults={hardFaults} (+{hardFaultsDelta}) | PagedPool={FormatBytes(proc->QuotaPagedPoolUsage)} | NonPagedPool={FormatBytes(proc->QuotaNonPagedPoolUsage)}").AppendLine();
        sb.Append(inv, $"    I/O Transfers : Reads={proc->ReadOperationCount} (+{readOpsDelta}, {FormatBytes(proc->ReadTransferCount)}, +{FormatBytes(readBytesDelta)}) | Writes={proc->WriteOperationCount} (+{writeOpsDelta}, {FormatBytes(proc->WriteTransferCount)}, +{FormatBytes(writeBytesDelta)}) | Other={proc->OtherOperationCount} (+{otherOpsDelta}, +{FormatBytes(otherBytesDelta)})").AppendLine();
        sb.Append(inv, $"    Handles & CLR : Total={proc->HandleCount}, GDI={gdiHandles}, USER={userHandles} | GC_Heap={FormatBytes((ulong)gcHeapBytes)}, AllocRate={allocMbPerSec:F2} MB/s").AppendLine();

        int printCount = Math.Min(activeThreadsCount, MaxTopThreadsToPrint);
        sb.Append(inv, $"  [Active Threads: Top {printCount} of {proc->NumberOfThreads} (TID | CPU% | Cycles | dCycles | StartModule_And_Name | Priority | CtxSw | dCtxSw | State)]").AppendLine();

        for (int i = 0; i < printCount; i++)
        {
            ref ThreadDeltaEntry t = ref _threadSortBuffer[i];
            double threadCpu = (t.KernelPlusUserDelta100Ns / (elapsed100Ns * _processorCount)) * 100.0;
            string identity = ResolveThreadIdentity(t.ThreadId, t.FallbackStartAddress);

            sb.Append(inv, $"    {t.ThreadId,-6} | {threadCpu,5:F2}% | {FormatGrouped(t.TotalCycles),16} | +{FormatGrouped(t.CyclesDelta),13} | {identity,-42} | {FormatThreadPriority(t.BasePriority),-13} | {FormatGrouped(t.TotalCtxSwitches),9} | +{FormatGrouped(t.CtxSwitchesDelta),6} | {FormatThreadState(t.State, t.WaitReason)}")
              .AppendLine();
        }
    }

    private void SampleCrossPlatformFallback(
        double uiLatencyMs,
        int tickCounter,
        StringBuilder sb,
        StreamWriter writer)
    {
        var inv = CultureInfo.InvariantCulture;
        long now = Stopwatch.GetTimestamp();
        long gcAlloc = GC.GetTotalAllocatedBytes(precise: false);
        long ws = Environment.WorkingSet;

        if (!_hasBaseline)
        {
            _prevSampleTimestamp = now;
            _prevGcAllocatedBytes = gcAlloc;
            _hasBaseline = true;
            return;
        }

        double elapsedSec = Math.Max(0.001, Stopwatch.GetElapsedTime(_prevSampleTimestamp, now).TotalSeconds);
        double allocMbPerSec = ((gcAlloc - _prevGcAllocatedBytes) / (1024.0 * 1024.0)) / elapsedSec;
        bool isSpike = uiLatencyMs >= SpikeUiLatencyMsThreshold || allocMbPerSec >= SpikeAllocMbPerSecThreshold;

        if (isSpike || (tickCounter % HeartbeatEveryTicks) == 0)
        {
            sb.Clear();
            sb.Append(inv, $"[{DateTime.Now.ToString("HH:mm:ss.fff", inv)}] [{(isSpike ? "SPIKE" : "HEARTBEAT")}] WS:{ws / (1024.0 * 1024.0):F1}MB | Alloc:{allocMbPerSec:F2}MB/s | UI:{uiLatencyMs:F1}ms")
              .AppendLine();
            writer.Write(sb);
            writer.Flush();
        }

        _prevSampleTimestamp = now;
        _prevGcAllocatedBytes = gcAlloc;
    }

    private void SaveProcessBaseline(
        SYSTEM_PROCESS_INFORMATION* proc,
        long nowTimestamp,
        ulong totalCycles,
        ulong totalCtxSwitches,
        uint hardFaults,
        long gcAllocatedBytes,
        int gen0,
        int gen1,
        int gen2)
    {
        _prevSampleTimestamp = nowTimestamp;
        _prevKernelTime100Ns = proc->KernelTime;
        _prevUserTime100Ns = proc->UserTime;
        _prevTotalCycles = totalCycles;
        _prevTotalCtxSwitches = totalCtxSwitches;
        _prevPageFaults = proc->PageFaultCount;
        _prevHardFaults = hardFaults;
        _prevPrivateBytes = (long)proc->PrivatePageCount;
        _prevReadOps = proc->ReadOperationCount;
        _prevWriteOps = proc->WriteOperationCount;
        _prevOtherOps = proc->OtherOperationCount;
        _prevReadBytes = proc->ReadTransferCount;
        _prevWriteBytes = proc->WriteTransferCount;
        _prevOtherBytes = proc->OtherTransferCount;
        _prevGcAllocatedBytes = gcAllocatedBytes;
        _prevGen0 = gen0;
        _prevGen1 = gen1;
        _prevGen2 = gen2;
        _hasBaseline = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindPreviousThreadIndex(uint tid)
    {
        for (int i = 0; i < _prevThreadCount; i++)
        {
            if (_prevThreadIds[i] == tid)
                return i;
        }
        return -1;
    }

    private void SortActiveThreads(int count)
    {
        for (int i = 1; i < count; i++)
        {
            ThreadDeltaEntry key = _threadSortBuffer[i];
            int j = i - 1;
            while (j >= 0 && _threadSortBuffer[j].CyclesDelta < key.CyclesDelta)
            {
                _threadSortBuffer[j + 1] = _threadSortBuffer[j];
                j--;
            }
            _threadSortBuffer[j + 1] = key;
        }
    }

    private void EnsureModulesCached(IntPtr hProcess, long nowTimestamp)
    {
        if (_moduleCount > 0 && Stopwatch.GetElapsedTime(_lastModuleRefreshTick, nowTimestamp).TotalSeconds < 30)
            return;

        _lastModuleRefreshTick = nowTimestamp;
        IntPtr* hMods = stackalloc IntPtr[MaxCachedModules];
        if (!EnumProcessModules(hProcess, hMods, (uint)(MaxCachedModules * sizeof(IntPtr)), out uint cbNeeded))
            return;

        int count = Math.Min(MaxCachedModules, (int)(cbNeeded / (uint)sizeof(IntPtr)));
        char* nameBuf = stackalloc char[128];
        int valid = 0;

        for (int i = 0; i < count; i++)
        {
            if (GetModuleInformation(hProcess, hMods[i], out MODULEINFO modInfo, (uint)sizeof(MODULEINFO)))
            {
                uint len = GetModuleBaseNameW(hProcess, hMods[i], nameBuf, 128);
                string name = len > 0 ? new string(nameBuf, 0, (int)len) : "unknown.dll";
                _moduleBases[valid] = (nuint)modInfo.lpBaseOfDll;
                _moduleSizes[valid] = modInfo.SizeOfImage;
                _moduleNames[valid] = name;
                valid++;
            }
        }

        _moduleCount = valid;
    }

    /// <summary>
    /// Разрешает реальный Win32 стартовый адрес потока через <c>NtQueryInformationThread(ThreadQuerySetWin32StartAddress)</c>
    /// и имя потока через <c>GetThreadDescription</c> (как делает System Informer). Кэширует результат по TID.
    /// </summary>
    private string ResolveThreadIdentity(uint tid, nuint fallbackAddress)
    {
        for (int i = 0; i < _threadIdentityCount; i++)
        {
            if (_threadIdentityTids[i] == tid)
                return _threadIdentityLabels[i];
        }

        nuint win32StartAddr = fallbackAddress;
        string? threadName = tid == _mainUiThreadId ? "Avalonia UI" : null;

        IntPtr hThread = OpenThread(THREAD_QUERY_INFORMATION | THREAD_QUERY_LIMITED_INFORMATION, false, tid);
        if (hThread == IntPtr.Zero)
            hThread = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, false, tid);

        if (hThread != IntPtr.Zero)
        {
            nuint queriedAddr = 0;
            if (NtQueryInformationThread(
                    hThread,
                    ThreadQuerySetWin32StartAddress,
                    &queriedAddr,
                    (uint)sizeof(nuint),
                    out _) == 0 && queriedAddr != 0)
            {
                win32StartAddr = queriedAddr;
            }

            if (threadName is null && GetThreadDescription(hThread, out IntPtr pDesc) >= 0 && pDesc != IntPtr.Zero)
            {
                string? desc = Marshal.PtrToStringUni(pDesc);
                LocalFree(pDesc);
                if (!string.IsNullOrWhiteSpace(desc))
                    threadName = desc;
            }

            CloseHandle(hThread);
        }

        string moduleStr = ResolveModuleOffset(win32StartAddr);
        string label = string.IsNullOrEmpty(threadName)
            ? moduleStr
            : $"{moduleStr} [{threadName}]";

        if (_threadIdentityCount < MaxTrackedThreads)
        {
            _threadIdentityTids[_threadIdentityCount] = tid;
            _threadIdentityLabels[_threadIdentityCount] = label;
            _threadIdentityCount++;
        }

        return label;
    }

    private string ResolveModuleOffset(nuint address)
    {
        if (address == 0) return "0x0";

        for (int i = 0; i < _moduleCount; i++)
        {
            nuint baseAddr = _moduleBases[i];
            uint size = _moduleSizes[i];
            if (address >= baseAddr && address < baseAddr + size)
            {
                nuint offset = address - baseAddr;
                return string.Create(CultureInfo.InvariantCulture, $"{_moduleNames[i]}+0x{offset:x}");
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $"0x{address:x}");
    }

    /// <summary>
    /// Форматирует большое целое число с разделителем '_' (например, 3_029_007_875) без пробелов и локалей.
    /// </summary>
    private static string FormatGrouped(ulong value)
    {
        Span<char> raw = stackalloc char[24];
        if (!value.TryFormat(raw, out int len, default, CultureInfo.InvariantCulture))
            return value.ToString(CultureInfo.InvariantCulture);

        if (len <= 3)
            return new string(raw[..len]);

        int underscores = (len - 1) / 3;
        int totalLen = len + underscores;
        Span<char> dst = stackalloc char[32];

        int srcIdx = len - 1;
        int dstIdx = totalLen - 1;
        int groupCount = 0;

        while (srcIdx >= 0)
        {
            dst[dstIdx--] = raw[srcIdx--];
            groupCount++;
            if (groupCount == 3 && srcIdx >= 0)
            {
                dst[dstIdx--] = '_';
                groupCount = 0;
            }
        }

        return new string(dst[..totalLen]);
    }

    private static string FormatShortCount(ulong value) => value switch
    {
        >= 1_000_000_000 => string.Create(CultureInfo.InvariantCulture, $"{value / 1_000_000_000.0:F2}G"),
        >= 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{value / 1_000_000.0:F2}M"),
        >= 1_000 => string.Create(CultureInfo.InvariantCulture, $"{value / 1_000.0:F1}K"),
        _ => value.ToString(CultureInfo.InvariantCulture)
    };

    private static string FormatBytes(ulong bytes) => bytes switch
    {
        >= 1024UL * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0 * 1024.0):F2}GB"),
        >= 1024UL * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):F2}MB"),
        >= 1024UL => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:F1}KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes}B")
    };

    private static string FormatThreadPriority(int basePriority) => basePriority switch
    {
        >= 15 => "TimeCritical",
        >= 10 => "Highest",
        9 => "AboveNormal",
        8 => "Normal",
        7 => "BelowNormal",
        <= 6 => "Lowest"
    };

    private static string FormatThreadState(uint state, uint waitReason) => state switch
    {
        2 => "Running",
        5 => waitReason switch
        {
            0 => "Wait:Executive",
            5 => "Wait:DelayExecution",
            6 => "Wait:UserRequest",
            9 => "Wait:EventPairLow",
            13 => "Wait:LpcReceive",
            15 => "Wait:IoCompletion",
            37 => "Wait:WrQueue",
            _ => string.Create(CultureInfo.InvariantCulture, $"Wait:{waitReason}")
        },
        1 => "Ready",
        _ => string.Create(CultureInfo.InvariantCulture, $"State:{state}")
    };

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        if (_recorderThread.IsAlive && Thread.CurrentThread != _recorderThread)
        {
            _recorderThread.Join(400);
        }

        _uiPingEvent.Dispose();
        _cts.Dispose();
    }

    private struct ThreadDeltaEntry
    {
        public uint ThreadId;
        public ulong TotalCycles;
        public ulong CyclesDelta;
        public uint TotalCtxSwitches;
        public uint CtxSwitchesDelta;
        public long KernelPlusUserDelta100Ns;
        public nuint FallbackStartAddress;
        public int BasePriority;
        public uint State;
        public uint WaitReason;
    }

    #region Win32 & Native NTDLL Structures / LibraryImport (100% Native AOT)

    private const uint THREAD_QUERY_INFORMATION = 0x0040;
    private const uint THREAD_QUERY_LIMITED_INFORMATION = 0x0800;
    private const int ProcessVmCountersClass = 3;
    private const int ThreadQuerySetWin32StartAddress = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CLIENT_ID
    {
        public IntPtr UniqueProcess;
        public IntPtr UniqueThread;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESS_INFORMATION
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public UNICODE_STRING ImageName;
        public int BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
        public uint HandleCount;
        public uint SessionId;
        public nuint UniqueProcessKey;
        public nuint PeakVirtualSize;
        public nuint VirtualSize;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivatePageCount;
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_THREAD_INFORMATION
    {
        public long KernelTime;
        public long UserTime;
        public long CreateTime;
        public uint WaitTime;
        public IntPtr StartAddress;
        public CLIENT_ID ClientId;
        public int Priority;
        public int BasePriority;
        public uint ContextSwitches;
        public uint ThreadState;
        public uint WaitReason;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VM_COUNTERS_EX2
    {
        public nuint PeakVirtualSize;
        public nuint VirtualSize;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
        public nuint PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
        public uint HardFaultCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MODULEINFO
    {
        public IntPtr lpBaseOfDll;
        public uint SizeOfImage;
        public IntPtr EntryPoint;
    }

    [LibraryImport("ntdll.dll")]
    private static partial uint NtQuerySystemInformation(
        int systemInformationClass,
        void* systemInformation,
        uint systemInformationLength,
        out uint returnLength);

    [LibraryImport("ntdll.dll")]
    private static partial uint NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        void* processInformation,
        uint processInformationLength,
        out uint returnLength);

    [LibraryImport("ntdll.dll")]
    private static partial uint NtQueryInformationThread(
        IntPtr threadHandle,
        int threadInformationClass,
        void* threadInformation,
        uint threadInformationLength,
        out uint returnLength);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenThread(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwThreadId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryProcessCycleTime(IntPtr processHandle, out ulong cycleTime);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryThreadCycleTime(IntPtr threadHandle, out ulong cycleTime);

    [LibraryImport("kernel32.dll")]
    private static partial int GetThreadDescription(IntPtr hThread, out IntPtr ppszThreadDescription);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr hMem);

    [LibraryImport("user32.dll")]
    private static partial uint GetGuiResources(IntPtr hProcess, uint uiFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "K32EnumProcessModules")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumProcessModules(IntPtr hProcess, IntPtr* lphModule, uint cb, out uint lpcbNeeded);

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetModuleInformation")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetModuleInformation(IntPtr hProcess, IntPtr hModule, out MODULEINFO lpmodinfo, uint cb);

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetModuleBaseNameW")]
    private static partial uint GetModuleBaseNameW(IntPtr hProcess, IntPtr hModule, char* lpBaseName, uint nSize);

    #endregion
}