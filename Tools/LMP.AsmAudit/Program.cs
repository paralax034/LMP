using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using Iced.Intel;

namespace LMP.Tools.AsmAudit;

/// <summary>
/// Бинарный линтер машинного кода Native AOT (x86-64 PE + MSF 7.0 PDB).
/// Находит горячие циклы по обратным переходам графа потока управления и проверяет отсутствие
/// инструкций <c>call</c>, тяжёлого деления/корня (<c>div*</c>/<c>sqrt*</c>), штрафов смешивания VEX/Legacy SSE
/// и сбросов регистров на стек (<c>[rsp + disp]</c>).
/// </summary>
public static partial class Program
{
    private const uint SYMOPT_UNDNAME = 0x00000002;
    private const uint SYMOPT_DEFERRED_LOADS = 0x00000004;
    private const uint SYMOPT_EXACT_SYMBOLS = 0x00000400;

    /// <summary>
    /// Точный размер структуры <c>SYMBOL_INFOW</c> без хвостового буфера имени в 64-битном <c>dbghelp.h</c> (88 байт),
    /// смещение поля <c>Name</c> равно 84 байтам.
    /// </summary>
    private const uint SymbolInfoStructSize = 88;
    private const int SymbolInfoNameOffset = 84;
    private const uint MaxSymbolNameChars = 1024;

    private static readonly string[] DefaultHotPathMasks =
    [
        "*EqualizerProcessor*Process*",
        "*AudioSpectrumAnalyzer*ProcessBlock*",
        "*AudioSpectrumAnalyzer*Accumulate*",
        "*GainProcessor*Process*",
        "*TruePeakLimiter*Process*",
        "*GainCrossfader*Advance*",
        "*KWeightingFilter*ProcessBlock*",
        "*SpanMathHelper*MultiplyByConstant*"
    ];

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SYMBOL_INFOW
    {
        public uint SizeOfStruct;
        public uint TypeIndex;
        public fixed ulong Reserved[2];
        public uint Index;
        public uint Size;
        public ulong ModBase;
        public uint Flags;
        public ulong Value;
        public ulong Address;
        public uint Register;
        public uint Scope;
        public uint Tag;
        public uint NameLen;
        public uint MaxNameLen;
    }

    [LibraryImport("dbghelp.dll")]
    private static partial uint SymSetOptions(uint symOptions);

    [LibraryImport("dbghelp.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SymInitializeW(nint hProcess, string? userSearchPath, [MarshalAs(UnmanagedType.Bool)] bool fInvadeProcess);

    [LibraryImport("dbghelp.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial ulong SymLoadModuleExW(
        nint hProcess,
        nint hFile,
        string? imageName,
        string? moduleName,
        ulong baseOfDll,
        uint dllSize,
        nint data,
        uint flags);

    [LibraryImport("dbghelp.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SymEnumSymbolsW(
        nint hProcess,
        ulong baseOfDll,
        string? mask,
        delegate* unmanaged[Stdcall]<SYMBOL_INFOW*, uint, nint, int> enumSymbolsCallback,
        nint userContext);

    [LibraryImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SymFromAddrW(
        nint hProcess,
        ulong address,
        out ulong displacement,
        SYMBOL_INFOW* symbol);

    [LibraryImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SymCleanup(nint hProcess);

    private readonly record struct NativeSymbol(string Name, uint Rva, uint Size);

    private readonly record struct HotLoopRange(
        int LoopId,
        ulong HeaderIp,
        ulong BackEdgeIp,
        int StartIndex,
        int EndIndex,
        bool IsInnerLeafLoop);

    private readonly record struct InstructionAnnotation(
        int LoopId,
        bool IsInnerLoop,
        string? CallTarget,
        bool IsHeavyMath,
        bool IsStackSpill,
        bool IsAvxSseMix);

    private sealed class MethodAuditResult
    {
        public required string SymbolName { get; init; }
        public required uint Rva { get; init; }
        public required uint Size { get; init; }
        public required List<HotLoopRange> Loops { get; init; }
        public required List<string> DisassemblyLines { get; init; }
        public int InnerLoopCalls { get; set; }
        public int OuterLoopMathCalls { get; set; }
        public int HeavyMathInInnerLoop { get; set; }
        public int StackSpillsInInnerLoop { get; set; }
        public int AvxSseTransitions { get; set; }

        public bool HasDefects(int maxSpills) =>
            InnerLoopCalls > 0 ||
            OuterLoopMathCalls > 0 ||
            HeavyMathInInnerLoop > 0 ||
            AvxSseTransitions > 0 ||
            StackSpillsInInnerLoop > maxSpills;
    }

    private static List<NativeSymbol>? _enumCollector;
    private static ulong _loadedModuleBase;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("[ОШИБКА] LMP.AsmAudit требует Windows (dbghelp.dll) для чтения нативных MSF 7.0 PDB.");
            return 1;
        }

        if (args.Length < 2)
        {
            Console.WriteLine("Использование: LMP.AsmAudit <путь_к_LMP.exe> <путь_к_LMP.pdb> [отчёт=AsmAuditReport.md] [--filter=Mask1,Mask2] [--max-spills=0]");
            return 1;
        }

        string exePath = Path.GetFullPath(args[0]);
        string pdbPath = Path.GetFullPath(args[1]);
        string reportPath = "AsmAuditReport.md";
        int maxSpills = 0;
        bool includeAsync = false;
        List<string> customMasks = [];

        for (int i = 2; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.StartsWith("--filter=", StringComparison.OrdinalIgnoreCase))
            {
                string raw = arg[9..];
                foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    customMasks.Add(part.Contains('*') ? part : $"*{part}*");
                }
            }
            else if (arg.StartsWith("--max-spills=", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(arg.AsSpan(13), out int parsed) && parsed >= 0)
                    maxSpills = parsed;
            }
            else if (string.Equals(arg, "--include-async", StringComparison.OrdinalIgnoreCase))
            {
                includeAsync = true;
            }
            else if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                reportPath = Path.GetFullPath(arg);
            }
        }

        if (!File.Exists(exePath))
        {
            Console.Error.WriteLine($"[ОШИБКА] Бинарный файл не найден: {exePath}");
            return 1;
        }

        if (!File.Exists(pdbPath))
        {
            Console.Error.WriteLine($"[ОШИБКА] Нативный PDB-файл не найден: {pdbPath}");
            return 1;
        }

        string[] masks = customMasks.Count > 0 ? customMasks.ToArray() : DefaultHotPathMasks;

        Console.WriteLine("══════════════════════════════════════════════════════════════════════");
        Console.WriteLine(" LMP Native AOT Binary Assembler & Hot-Loop Auditor (v1.1)");
        Console.WriteLine("══════════════════════════════════════════════════════════════════════");
        Console.WriteLine($"  Бинарник : {exePath}");
        Console.WriteLine($"  Символы  : {pdbPath}");
        Console.WriteLine($"  Маски    : {string.Join(", ", masks)}");
        Console.WriteLine($"  Отчёт    : {reportPath}");
        Console.WriteLine();

        var sw = Stopwatch.StartNew();

        byte[] peBytes = File.ReadAllBytes(exePath);
        using var peStream = new MemoryStream(peBytes, writable: false);
        using var peReader = new PEReader(peStream, PEStreamOptions.LeaveOpen);

        ulong imageBase = peReader.PEHeaders.PEHeader?.ImageBase ?? 0x140000000UL;
        uint imageSize = (uint)(peReader.PEHeaders.PEHeader?.SizeOfImage ?? peBytes.Length);

        nint hProcess = (nint)0x4C4D5041;
        _ = SymSetOptions(SYMOPT_UNDNAME | SYMOPT_DEFERRED_LOADS | SYMOPT_EXACT_SYMBOLS);

        string pdbDir = Path.GetDirectoryName(pdbPath) ?? Environment.CurrentDirectory;
        if (!SymInitializeW(hProcess, pdbDir, false))
        {
            Console.Error.WriteLine($"[ОШИБКА] SymInitializeW завершился с кодом {Marshal.GetLastPInvokeError()}");
            return 1;
        }

        try
        {
            ulong modBase = SymLoadModuleExW(hProcess, 0, exePath, null, imageBase, imageSize, 0, 0);
            if (modBase == 0)
            {
                Console.Error.WriteLine($"[ОШИБКА] SymLoadModuleExW завершился с кодом {Marshal.GetLastPInvokeError()}");
                return 1;
            }

            _loadedModuleBase = modBase;
            var discovered = new Dictionary<uint, NativeSymbol>();
            _enumCollector = new List<NativeSymbol>(128);

            unsafe
            {
                for (int m = 0; m < masks.Length; m++)
                {
                    _enumCollector.Clear();
                    _ = SymEnumSymbolsW(hProcess, modBase, masks[m], &OnSymbolEnumerated, 0);

                    for (int i = 0; i < _enumCollector.Count; i++)
                    {
                        var sym = _enumCollector[i];
                        if (ShouldAuditSymbol(sym.Name, includeAsync) && sym.Size > 0)
                        {
                            discovered[sym.Rva] = sym;
                        }
                    }
                }
            }

            if (discovered.Count == 0)
            {
                Console.Error.WriteLine("[ОШИБКА] В PDB не найдено ни одного целевого DSP-символа по заданным маскам.");
                return 1;
            }

            var sortedSymbols = discovered.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
            var results = new List<MethodAuditResult>(sortedSymbols.Count);

            foreach (var sym in sortedSymbols)
            {
                if (!TryMapRvaToOffset(peReader, sym.Rva, sym.Size, peBytes.Length, out int fileOffset))
                    continue;

                ReadOnlySpan<byte> codeSlice = peBytes.AsSpan(fileOffset, (int)sym.Size);
                ulong methodVa = modBase + sym.Rva;
                var audit = AuditMethod(hProcess, sym, methodVa, codeSlice);
                results.Add(audit);
            }

            WriteMarkdownReport(reportPath, exePath, pdbPath, results, maxSpills);
            sw.Stop();

            int failedMethods = 0;
            int totalHotLoops = 0;
            int totalInnerCalls = 0;
            int totalMathCalls = 0;
            int totalHeavyMath = 0;
            int totalSpills = 0;
            int totalAvxSse = 0;

            foreach (var r in results)
            {
                totalHotLoops += r.Loops.Count;
                totalInnerCalls += r.InnerLoopCalls;
                totalMathCalls += r.OuterLoopMathCalls;
                totalHeavyMath += r.HeavyMathInInnerLoop;
                totalSpills += r.StackSpillsInInnerLoop;
                totalAvxSse += r.AvxSseTransitions;

                bool failed = r.HasDefects(maxSpills);
                if (failed) failedMethods++;

                Console.ForegroundColor = failed ? ConsoleColor.Red : ConsoleColor.Green;
                string status = failed ? "[FAIL]" : "[PASS]";
                Console.Write($"  {status} ");
                Console.ResetColor();
                Console.WriteLine(
                    $"{r.SymbolName} (RVA: 0x{r.Rva:X}, {r.Size} B, Loops: {r.Loops.Count}, " +
                    $"LoopCalls: {r.InnerLoopCalls + r.OuterLoopMathCalls}, Div/Sqrt: {r.HeavyMathInInnerLoop}, " +
                    $"Spills: {r.StackSpillsInInnerLoop}, AVX/SSE: {r.AvxSseTransitions})");
            }

            Console.WriteLine("──────────────────────────────────────────────────────────────────────");
            if (failedMethods == 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[УСПЕХ] Проверено {results.Count} методов ({totalHotLoops} горячих циклов) за {sw.ElapsedMilliseconds} мс — 0 дефектов!");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(
                    $"[ДЕФЕКТЫ] В {failedMethods} из {results.Count} методов обнаружены проблемы " +
                    $"(Calls={totalInnerCalls + totalMathCalls}, Div/Sqrt={totalHeavyMath}, Spills={totalSpills}, AVX/SSE={totalAvxSse}).");
            }
            Console.ResetColor();
            Console.WriteLine($"  Подробный Markdown-дамп: {reportPath}");
            Console.WriteLine("──────────────────────────────────────────────────────────────────────");

            return failedMethods == 0 ? 0 : 2;
        }
        finally
        {
            _ = SymCleanup(hProcess);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static unsafe int OnSymbolEnumerated(SYMBOL_INFOW* pSymInfo, uint symbolSize, nint userContext)
    {
        _ = userContext;
        if (pSymInfo == null || pSymInfo->NameLen == 0 || _enumCollector == null)
            return 1;

        uint size = pSymInfo->Size != 0 ? pSymInfo->Size : symbolSize;
        if (size == 0 || pSymInfo->Address <= _loadedModuleBase)
            return 1;

        char* pName = (char*)((byte*)pSymInfo + SymbolInfoNameOffset);
        int len = (int)Math.Min(pSymInfo->NameLen, MaxSymbolNameChars);
        string name = new(pName, 0, len);
        uint rva = (uint)(pSymInfo->Address - _loadedModuleBase);

        _enumCollector.Add(new NativeSymbol(name, rva, size));
        return 1;
    }

    private static bool ShouldAuditSymbol(string name, bool includeAsync)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        // Аудируем только собственные методы сборок LMP / LMP.Core (отсекаем S_P_CoreLib_ и Internal_CompilerGenerated_)
        if (!name.StartsWith("LMP_", StringComparison.Ordinal))
            return false;

        if (!includeAsync &&
            (name.Contains("_d__", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("SlowAsync", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("ParseHeadersAsync", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("ParseInfoAsync", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("ParseTracksAsync", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("ParseTrackEntryAsync", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("ParseAudioSettingsAsync", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("ParseCuesAsync", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("ParseCuePointAsync", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("ParseBlockPayloadAsync", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("ParseLacingAsync", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // Исключаем синтетические funclet-блоки, методы record-структур, инициализацию/сброс/освобождение и холодные Throw-хелперы
        if (name.Contains("funclet", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("cctor", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ctor", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("__Dispose", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("__Reset", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("__RequireResync", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("__Equals", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("__GetHashCode", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("__ToString", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("__PrintMembers", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("____GetFieldHelper", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("__Throw", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("__EnqueueLacedFramesSync", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Configure", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("SnapToTarget", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Publish", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("RequestReset", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ClearPending", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ProcessSamples", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("SyncPublishedParameters", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool IsStrictDspKernelSymbol(string symbolName) =>
        symbolName.Contains("EqualizerProcessor", StringComparison.OrdinalIgnoreCase) ||
        symbolName.Contains("AudioSpectrumAnalyzer", StringComparison.OrdinalIgnoreCase) ||
        symbolName.Contains("GainProcessor", StringComparison.OrdinalIgnoreCase) ||
        symbolName.Contains("TruePeakLimiter", StringComparison.OrdinalIgnoreCase) ||
        symbolName.Contains("GainCrossfader", StringComparison.OrdinalIgnoreCase) ||
        symbolName.Contains("KWeightingFilter", StringComparison.OrdinalIgnoreCase) ||
        symbolName.Contains("BiquadFilterMath", StringComparison.OrdinalIgnoreCase) ||
        symbolName.Contains("SpanMathHelper", StringComparison.OrdinalIgnoreCase) ||
        symbolName.Contains("ConvertBytesToFloat", StringComparison.OrdinalIgnoreCase);

    private static bool TryMapRvaToOffset(PEReader peReader, uint rva, uint size, int fileLength, out int fileOffset)
    {
        foreach (var section in peReader.PEHeaders.SectionHeaders)
        {
            uint secStart = (uint)section.VirtualAddress;
            uint secSize = (uint)Math.Max(section.VirtualSize, section.SizeOfRawData);
            if (rva >= secStart && rva + size <= secStart + secSize)
            {
                long offset = section.PointerToRawData + (rva - secStart);
                if (offset >= 0 && offset + size <= fileLength)
                {
                    fileOffset = (int)offset;
                    return true;
                }
            }
        }

        fileOffset = 0;
        return false;
    }

    private static MethodAuditResult AuditMethod(
        nint hProcess,
        NativeSymbol symbol,
        ulong methodVa,
        ReadOnlySpan<byte> codeBytes)
    {
        var reader = new ByteArrayCodeReader(codeBytes.ToArray());
        var decoder = Iced.Intel.Decoder.Create(64, reader, methodVa);
        var instructions = new List<Instruction>(128);
        ulong endVa = methodVa + symbol.Size;

        while (reader.CanReadByte && decoder.IP < endVa)
        {
            decoder.Decode(out Instruction instr);
            instructions.Add(instr);
        }

        var ipToIndex = new Dictionary<ulong, int>(instructions.Count);
        for (int i = 0; i < instructions.Count; i++)
            ipToIndex[instructions[i].IP] = i;

        bool rbpIsStackFramePointer = IsRbpEstablishedAsFramePointer(instructions);

        // Pass 1: Детектирование горячих циклов по обратным переходам (TargetIP < CurrentIP) с проверкой достижимости без ret/int3
        var rawLoops = new List<(ulong HeaderIp, ulong BackEdgeIp, int StartIdx, int EndIdx)>();
        for (int i = 0; i < instructions.Count; i++)
        {
            ref readonly Instruction instr = ref CollectionsMarshal.AsSpan(instructions)[i];
            if (instr.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch)
            {
                ulong targetIp = instr.NearBranchTarget;
                if (targetIp >= methodVa && targetIp < instr.IP && ipToIndex.TryGetValue(targetIp, out int startIdx))
                {
                    if (CanReachWithoutReturn(instructions, ipToIndex, startIdx, i))
                    {
                        rawLoops.Add((targetIp, instr.IP, startIdx, i));
                    }
                }
            }
        }

        var loops = new List<HotLoopRange>(rawLoops.Count);
        for (int i = 0; i < rawLoops.Count; i++)
        {
            var cur = rawLoops[i];
            bool containsNested = false;
            for (int j = 0; j < rawLoops.Count; j++)
            {
                if (i == j) continue;
                var other = rawLoops[j];
                if (other.StartIdx >= cur.StartIdx && other.EndIdx <= cur.EndIdx &&
                    (other.StartIdx > cur.StartIdx || other.EndIdx < cur.EndIdx))
                {
                    containsNested = true;
                    break;
                }
            }

            loops.Add(new HotLoopRange(
                LoopId: i + 1,
                HeaderIp: cur.HeaderIp,
                BackEdgeIp: cur.BackEdgeIp,
                StartIndex: cur.StartIdx,
                EndIndex: cur.EndIdx,
                IsInnerLeafLoop: !containsNested));
        }

        // Pass 2: Аудит инструкций внутри горячих циклов
        var annotations = new InstructionAnnotation[instructions.Count];
        int innerLoopCalls = 0;
        int outerLoopMathCalls = 0;
        int heavyMathInInner = 0;
        int stackSpillsInInner = 0;
        int avxSseTransitions = 0;
        bool isStrictDsp = IsStrictDspKernelSymbol(symbol.Name);

        foreach (var loop in loops)
        {
            // Игнорируем микро-циклы пролога компилятора по обнулению стекового фрейма (xorps xmm,xmm + movaps [rsp+rax+disp],xmm)
            if (IsPrologZeroInitLoop(instructions, loop.StartIndex, loop.EndIndex))
                continue;

            bool sawVexSimd = false;
            bool sawLegacySseSimd = false;
            bool rbpModifiedInLoop = IsRbpModifiedInRange(instructions, loop.StartIndex, loop.EndIndex);
            bool trackRbpAsStack = rbpIsStackFramePointer && !rbpModifiedInLoop;

            for (int idx = loop.StartIndex; idx <= loop.EndIndex; idx++)
            {
                // Если инструкция принадлежит вложенному циклу, её детально проверяет сам внутренний цикл
                if (!loop.IsInnerLeafLoop && IsInsideAnyInnerLoop(idx, loops))
                    continue;

                // Отсекаем тупиковые блоки выхода (early return / call Throw* + int3), расположенные внутри
                // диапазона адресов, но не имеющие пути возврата к обратной дуге цикла (BackEdgeIp)
                if (!CanReachWithoutReturn(instructions, ipToIndex, idx, loop.EndIndex))
                    continue;

                Instruction instr = instructions[idx];
                string? callTarget = null;
                bool isHeavyMath = false;
                bool isSpill = false;
                bool isAvxMix = false;

                if (instr.FlowControl is FlowControl.Call or FlowControl.IndirectCall)
                {
                    string resolvedTarget = ResolveCallTargetName(hProcess, in instr);
                    if (loop.IsInnerLeafLoop && (isStrictDsp || IsForbiddenRuntimeOrMathCall(resolvedTarget)))
                    {
                        callTarget = resolvedTarget;
                        innerLoopCalls++;
                    }
                    else if (!loop.IsInnerLeafLoop && IsForbiddenRuntimeOrMathCall(resolvedTarget))
                    {
                        callTarget = resolvedTarget;
                        outerLoopMathCalls++;
                    }
                }

                if (loop.IsInnerLeafLoop && IsHeavyDivOrSqrt(instr.Mnemonic))
                {
                    isHeavyMath = true;
                    heavyMathInInner++;
                }

                if (loop.IsInnerLeafLoop && IsStackMemoryAccess(in instr, trackRbpAsStack))
                {
                    isSpill = true;
                    stackSpillsInInner++;
                }

                if (TouchesSimdRegisters(in instr))
                {
                    if (instr.Encoding is EncodingKind.VEX or EncodingKind.EVEX)
                        sawVexSimd = true;
                    else if (instr.Encoding == EncodingKind.Legacy)
                        sawLegacySseSimd = true;

                    if (sawVexSimd && sawLegacySseSimd)
                    {
                        isAvxMix = true;
                        avxSseTransitions++;
                        sawLegacySseSimd = false;
                    }
                }

                annotations[idx] = new InstructionAnnotation(
                    LoopId: loop.LoopId,
                    IsInnerLoop: loop.IsInnerLeafLoop,
                    CallTarget: callTarget,
                    IsHeavyMath: isHeavyMath,
                    IsStackSpill: isSpill,
                    IsAvxSseMix: isAvxMix);
            }
        }

        // Форматирование дизассемблерного листинга с разметкой [HOT LOOP]
        var formatter = new IntelFormatter();
        formatter.Options.UppercaseHex = true;
        formatter.Options.HexPrefix = "0x";
        var output = new StringOutput();
        var disasmLines = new List<string>(instructions.Count + loops.Count * 2);

        for (int i = 0; i < instructions.Count; i++)
        {
            foreach (var loop in loops)
            {
                if (loop.StartIndex == i)
                {
                    string kind = loop.IsInnerLeafLoop ? "INNER HOT LOOP" : "OUTER LOOP";
                    disasmLines.Add($"  ; >>> [{kind} #{loop.LoopId} START @ 0x{loop.HeaderIp:X} .. 0x{loop.BackEdgeIp:X}]");
                }
            }

            Instruction instr = instructions[i];
            output.Reset();
            formatter.Format(in instr, output);
            string text = output.ToStringAndReset();

            var ann = annotations[i];
            string prefix = ann.LoopId > 0
                ? (ann.IsInnerLoop ? $"[L{ann.LoopId}:HOT] " : $"[L{ann.LoopId}:OUT] ")
                : "          ";

            var commentBuilder = new StringBuilder();
            if (ann.CallTarget != null)
                commentBuilder.Append($" ; <-- [!CALL: {ann.CallTarget}]");
            if (ann.IsHeavyMath)
                commentBuilder.Append(" ; <-- [!DIV/SQRT IN HOT LOOP]");
            if (ann.IsStackSpill)
                commentBuilder.Append(" ; <-- [!STACK SPILL]");
            if (ann.IsAvxSseMix)
                commentBuilder.Append(" ; <-- [!AVX-SSE MIX]");

            disasmLines.Add($"{prefix}0x{instr.IP:X12}:  {text}{commentBuilder}");

            foreach (var loop in loops)
            {
                if (loop.EndIndex == i)
                {
                    string kind = loop.IsInnerLeafLoop ? "INNER HOT LOOP" : "OUTER LOOP";
                    disasmLines.Add($"  ; <<< [{kind} #{loop.LoopId} END]");
                }
            }
        }

        return new MethodAuditResult
        {
            SymbolName = symbol.Name,
            Rva = symbol.Rva,
            Size = symbol.Size,
            Loops = loops,
            DisassemblyLines = disasmLines,
            InnerLoopCalls = innerLoopCalls,
            OuterLoopMathCalls = outerLoopMathCalls,
            HeavyMathInInnerLoop = heavyMathInInner,
            StackSpillsInInnerLoop = stackSpillsInInner,
            AvxSseTransitions = avxSseTransitions
        };
    }

    private static bool IsRbpEstablishedAsFramePointer(List<Instruction> instructions)
    {
        int limit = Math.Min(20, instructions.Count);
        for (int i = 0; i < limit; i++)
        {
            Instruction instr = instructions[i];
            if (instr.Mnemonic is Mnemonic.Mov or Mnemonic.Lea &&
                instr.OpCount >= 2 &&
                instr.GetOpKind(0) == OpKind.Register &&
                instr.GetOpRegister(0) == Register.RBP)
            {
                if (instr.GetOpKind(1) == OpKind.Register && instr.GetOpRegister(1) == Register.RSP)
                    return true;
                if (instr.GetOpKind(1) == OpKind.Memory && instr.MemoryBase == Register.RSP)
                    return true;
            }
        }
        return false;
    }

    private static bool IsRbpModifiedInRange(List<Instruction> instructions, int startIdx, int endIdx)
    {
        for (int i = startIdx; i <= endIdx; i++)
        {
            Instruction instr = instructions[i];
            if (instr.OpCount >= 1 &&
                instr.GetOpKind(0) == OpKind.Register &&
                instr.GetOpRegister(0) is Register.RBP or Register.EBP)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsPrologZeroInitLoop(List<Instruction> instructions, int startIdx, int endIdx)
    {
        if (endIdx - startIdx > 6 || startIdx > 24)
            return false;

        for (int i = startIdx; i <= endIdx; i++)
        {
            Instruction instr = instructions[i];
            if (instr.Mnemonic is Mnemonic.Movaps or Mnemonic.Movups or Mnemonic.Add or Mnemonic.Jne)
                continue;
            return false;
        }
        return true;
    }

    private static bool CanReachWithoutReturn(
        List<Instruction> instructions,
        Dictionary<ulong, int> ipToIndex,
        int startIdx,
        int targetIdx)
    {
        int count = targetIdx - startIdx + 1;
        if (count <= 0) return false;

        Span<bool> visited = count <= 256 ? stackalloc bool[count] : new bool[count];
        var queue = new Queue<int>(16);
        visited[0] = true;
        queue.Enqueue(startIdx);

        while (queue.Count > 0)
        {
            int curr = queue.Dequeue();
            if (curr == targetIdx)
                return true;

            Instruction instr = instructions[curr];
            FlowControl fc = instr.FlowControl;

            if (fc is FlowControl.Return or FlowControl.Interrupt or FlowControl.Exception)
                continue;

            if (fc != FlowControl.UnconditionalBranch)
            {
                int next = curr + 1;
                if (next >= startIdx && next <= targetIdx && !visited[next - startIdx])
                {
                    visited[next - startIdx] = true;
                    queue.Enqueue(next);
                }
            }

            if (fc is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch)
            {
                ulong branchTarget = instr.NearBranchTarget;
                if (ipToIndex.TryGetValue(branchTarget, out int branchIdx) &&
                    branchIdx >= startIdx && branchIdx <= targetIdx &&
                    !visited[branchIdx - startIdx])
                {
                    visited[branchIdx - startIdx] = true;
                    queue.Enqueue(branchIdx);
                }
            }
        }

        return false;
    }

    private static bool IsInsideAnyInnerLoop(int instructionIndex, List<HotLoopRange> loops)
    {
        for (int i = 0; i < loops.Count; i++)
        {
            var loop = loops[i];
            if (loop.IsInnerLeafLoop && instructionIndex >= loop.StartIndex && instructionIndex <= loop.EndIndex)
                return true;
        }
        return false;
    }

    private static unsafe string ResolveCallTargetName(nint hProcess, in Instruction instr)
    {
        ulong targetAddr = instr.NearBranchTarget;
        if (targetAddr == 0)
            return "indirect_call";

        int bufferBytes = (int)(SymbolInfoStructSize + MaxSymbolNameChars * sizeof(char));
        byte* pBuf = stackalloc byte[bufferBytes];
        NativeMemory.Clear(pBuf, (nuint)bufferBytes);

        SYMBOL_INFOW* pSym = (SYMBOL_INFOW*)pBuf;
        pSym->SizeOfStruct = SymbolInfoStructSize;
        pSym->MaxNameLen = MaxSymbolNameChars;

        if (SymFromAddrW(hProcess, targetAddr, out ulong disp, pSym) && pSym->NameLen > 0)
        {
            char* pName = (char*)(pBuf + SymbolInfoNameOffset);
            int len = (int)Math.Min(pSym->NameLen, MaxSymbolNameChars);
            string symName = new(pName, 0, len);
            return disp == 0 ? symName : $"{symName}+0x{disp:X}";
        }

        return $"0x{targetAddr:X}";
    }

    private static bool IsForbiddenRuntimeOrMathCall(string? callTarget)
    {
        if (string.IsNullOrEmpty(callTarget))
            return false;

        return callTarget.Contains("fma", StringComparison.OrdinalIgnoreCase) ||
               callTarget.Contains("pow", StringComparison.OrdinalIgnoreCase) ||
               callTarget.Contains("sin", StringComparison.OrdinalIgnoreCase) ||
               callTarget.Contains("cos", StringComparison.OrdinalIgnoreCase) ||
               callTarget.Contains("exp", StringComparison.OrdinalIgnoreCase) ||
               callTarget.Contains("log", StringComparison.OrdinalIgnoreCase) ||
               callTarget.Contains("RhNew", StringComparison.OrdinalIgnoreCase) ||
               callTarget.Contains("Throw", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHeavyDivOrSqrt(Mnemonic mnemonic) => mnemonic switch
    {
        Mnemonic.Divps or Mnemonic.Divpd or Mnemonic.Divss or Mnemonic.Divsd or
        Mnemonic.Vdivps or Mnemonic.Vdivpd or Mnemonic.Vdivss or Mnemonic.Vdivsd or
        Mnemonic.Sqrtps or Mnemonic.Sqrtpd or Mnemonic.Sqrtss or Mnemonic.Sqrtsd or
        Mnemonic.Vsqrtps or Mnemonic.Vsqrtpd or Mnemonic.Vsqrtss or Mnemonic.Vsqrtsd or
        Mnemonic.Div or Mnemonic.Idiv => true,
        _ => false
    };

    private static bool IsStackMemoryAccess(in Instruction instr, bool trackRbpAsStack)
    {
        int opCount = instr.OpCount;
        for (int i = 0; i < opCount; i++)
        {
            if (instr.GetOpKind(i) == OpKind.Memory)
            {
                Register baseReg = instr.MemoryBase;
                if (baseReg is Register.RSP or Register.ESP)
                    return true;
                if (trackRbpAsStack && baseReg is Register.RBP or Register.EBP)
                    return true;
            }
        }
        return false;
    }

    private static bool TouchesSimdRegisters(in Instruction instr)
    {
        int opCount = instr.OpCount;
        for (int i = 0; i < opCount; i++)
        {
            OpKind kind = instr.GetOpKind(i);
            if (kind == OpKind.Register)
            {
                Register reg = instr.GetOpRegister(i);
                if (reg is >= Register.XMM0 and <= Register.ZMM31)
                    return true;
            }
        }
        return false;
    }

    private static void WriteMarkdownReport(
        string reportPath,
        string exePath,
        string pdbPath,
        List<MethodAuditResult> results,
        int maxSpills)
    {
        using var fs = new FileStream(reportPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(fs, Encoding.UTF8);

        writer.WriteLine("# LMP Native AOT Assembler Hot-Path Audit Report");
        writer.WriteLine();
        writer.WriteLine($"- **Бинарный файл:** `{Path.GetFileName(exePath)}`");
        writer.WriteLine($"- **Нативный PDB:** `{Path.GetFileName(pdbPath)}`");
        writer.WriteLine($"- **Дата аудита:** `{DateTime.Now:yyyy-MM-dd HH:mm:ss}`");
        writer.WriteLine($"- **Допуск стековых проливов в цикле:** `{maxSpills}`");
        writer.WriteLine();
        writer.WriteLine("## Сводная таблица DSP-ядер");
        writer.WriteLine();
        writer.WriteLine("| Статус | Метод | RVA | Размер | Циклов | Calls в цикле | Div/Sqrt | Stack Spills | AVX/SSE Mix |");
        writer.WriteLine("| :---: | :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: |");

        foreach (var r in results)
        {
            bool failed = r.HasDefects(maxSpills);
            string badge = failed ? "FAIL" : "PASS";
            int totalCalls = r.InnerLoopCalls + r.OuterLoopMathCalls;
            writer.WriteLine(
                $"| **{badge}** | `{r.SymbolName}` | `0x{r.Rva:X}` | {r.Size} B | {r.Loops.Count} | {totalCalls} | {r.HeavyMathInInnerLoop} | {r.StackSpillsInInnerLoop} | {r.AvxSseTransitions} |");
        }

        writer.WriteLine();
        writer.WriteLine("## Дизассемблерные графы горячих путей");
        writer.WriteLine();

        foreach (var r in results)
        {
            bool failed = r.HasDefects(maxSpills);
            writer.WriteLine($"### {(failed ? "[FAIL]" : "[PASS]")} `{r.SymbolName}` (`RVA: 0x{r.Rva:X}`, `{r.Size} B`)");
            writer.WriteLine();
            writer.WriteLine("```asm");
            foreach (string line in r.DisassemblyLines)
            {
                writer.WriteLine(line);
            }
            writer.WriteLine("```");
            writer.WriteLine();
        }
    }
}