using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using LMP.Core.Youtube.Exceptions;

namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// P/Invoke wrapper over native QuickJS-NG bridge.
/// </summary>
internal static unsafe partial class QuickJsNative
{
    private const string LibName = "quickjs_bridge";

#if DEBUG
    private static string? _resolvedLibraryPath;
    private static int _bridgeVerified;

    private const int ExpectedBridgeFeatureMask =
        0x0001 | // BROWSER_ENV
        0x0002 | // EVENTS
        0x0004 | // DOM
        0x0008;  // BOM
#endif

    /// <summary>
    /// Bridge ABI version. Used to invalidate bytecode cache on updates.
    /// </summary>
    public static string BridgeAbi { get; } = ExtractAbi();

    static QuickJsNative()
    {
        NativeLibrary.SetDllImportResolver(
            typeof(QuickJsNative).Assembly,
            static (libraryName, assembly, searchPath) =>
            {
                if (libraryName != LibName)
                    return IntPtr.Zero;

                string extension = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".dll" :
                                   RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? ".dylib" : ".so";

                // Look directly next to the app (without "Native" subfolder)
                var localPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    $"{LibName}{extension}");

                // For Debug/Release builds, the file will be located next to the EXE
                if (File.Exists(localPath) && NativeLibrary.TryLoad(localPath, out var handle))
                {
#if DEBUG
                    _resolvedLibraryPath = localPath;
#endif
                    return handle;
                }

                // In Single File Publish, the file does not physically exist in BaseDirectory.
                // Return IntPtr.Zero to allow the standard .NET mechanism
                // to load the library from the bundle extraction cache (%TEMP%).
                return IntPtr.Zero;
            });
    }

    // --- Native P/Invoke imports ---

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial IntPtr qjs_create_context(string script);

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int qjs_init_botguard_bootstrap(
        IntPtr handle,
        string globalName,
        string program);

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial byte* qjs_call_function(IntPtr handle, string functionName, string challenge);

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial byte* qjs_eval(IntPtr handle, string code);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int qjs_pump_event_loop(IntPtr handle, int maxMs);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial IntPtr qjs_get_last_error(IntPtr handle);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void qjs_destroy_context(IntPtr handle);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void qjs_free_string(byte* str);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial IntPtr qjs_get_console_output(IntPtr handle);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void qjs_clear_console_output(IntPtr handle);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial IntPtr qjs_get_bridge_signature();

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int qjs_get_bridge_feature_mask();

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial IntPtr qjs_create_context_ex(
        string script, byte** outBytecode, nuint* outBytecodeLen);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial IntPtr qjs_create_context_from_bytecode(byte* bytecode, nuint len);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void qjs_free_bytecode(byte* buf);

#if DEBUG
    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void qjs_trace_enable(IntPtr handle, int enabled);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int qjs_trace_get_total(IntPtr handle);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int qjs_trace_get_count(IntPtr handle);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial byte* qjs_trace_dump(IntPtr handle, int offset, int count);

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial byte* qjs_trace_dump_filtered(
        IntPtr handle, string substring, int maxCount);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void qjs_trace_reset(IntPtr handle);

#endif

    // --- Public managed API ---

    /// <summary>
    /// Creates a persistent QuickJS context, executing the script exactly once.
    /// </summary>
    public static IntPtr CreateContext(string script)
    {
        try
        {
#if DEBUG
            VerifyExpectedBridgeLoaded();
#endif

            var handle = qjs_create_context(script);
            if (handle == IntPtr.Zero)
                Log.Error("[QuickJsNative] CreateContext failed");

            return handle;
        }
        catch (Exception ex)
        {
            Log.Error($"[QuickJsNative] CreateContext failed: {ex.Message}");
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Creates a QuickJS context from a source script and simultaneously returns compiled bytecode.
    /// Performs parsing and compilation strictly once per JSRuntime instance.
    /// </summary>
    /// <param name="script">JavaScript source code.</param>
    /// <returns>A tuple containing a handle to the native context and a serialized bytecode byte array.</returns>
    public static unsafe (IntPtr Handle, byte[]? Bytecode) CreateContextWithBytecode(string script)
    {
        if (string.IsNullOrEmpty(script)) return (IntPtr.Zero, null);

        byte* bcPtr = null;
        nuint bcLen = 0;
        IntPtr handle = IntPtr.Zero;

        try
        {
#if DEBUG
            VerifyExpectedBridgeLoaded();
#endif
            handle = qjs_create_context_ex(script, &bcPtr, &bcLen);

            byte[]? bytecode = null;
            if (bcPtr != null && bcLen > 0)
            {
                bytecode = new byte[(int)bcLen];
                fixed (byte* dst = bytecode)
                {
                    Buffer.MemoryCopy(bcPtr, dst, bcLen, bcLen);
                }
            }

            return (handle, bytecode);
        }
        catch (Exception ex)
        {
            Log.Error($"[QuickJsNative] CreateContextWithBytecode failed: {ex.Message}");
            if (handle != IntPtr.Zero)
            {
                DestroyContext(handle);
            }
            return (IntPtr.Zero, null);
        }
        finally
        {
            if (bcPtr != null) qjs_free_bytecode(bcPtr);
        }
    }

    /// <summary>
    /// Creates a persistent QuickJS context directly from compiled bytecode.
    /// </summary>
    /// <param name="bytecode">Compiled QuickJS bytecode.</param>
    /// <returns>Handle to the native context or IntPtr.Zero.</returns>
    public static IntPtr CreateContextFromBytecode(byte[] bytecode)
    {
        if (bytecode == null || bytecode.Length == 0) return IntPtr.Zero;

        try
        {
#if DEBUG
            VerifyExpectedBridgeLoaded();
#endif
            fixed (byte* ptr = bytecode)
            {
                var handle = qjs_create_context_from_bytecode(ptr, (nuint)bytecode.Length);
                if (handle == IntPtr.Zero)
                    Log.Error("[QuickJsNative] CreateContextFromBytecode failed");
                return handle;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[QuickJsNative] CreateContextFromBytecode failed: {ex.Message}");
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Initializes native BotGuard bootstrap layer (VM Verification, State, doSnapshot, doMint, activateToken).
    /// </summary>
    /// <param name="handle">Native handle to the QuickJS context.</param>
    /// <param name="globalName">VM global variable name.</param>
    /// <param name="program">Challenge program script.</param>
    /// <returns>True if initialization succeeded.</returns>
    public static bool InitBotGuardBootstrap(IntPtr handle, string globalName, string program)
    {
        if (handle == IntPtr.Zero) return false;

        try
        {
            return qjs_init_botguard_bootstrap(handle, globalName, program) != 0;
        }
        catch (Exception ex)
        {
            Log.Error($"[QuickJsNative] InitBotGuardBootstrap failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Invokes <c>globalThis._result.{functionName}(challenge)</c> via direct JS API.
    /// </summary>
    public static string? CallFunction(IntPtr handle, string functionName, string challenge)
    {
        if (handle == IntPtr.Zero) return null;

        byte* nativeResult = null;
        try
        {
            nativeResult = qjs_call_function(handle, functionName, challenge);
            return MarshalNativeString(nativeResult);
        }
        catch (Exception ex)
        {
            Log.Error($"[QuickJsNative] CallFunction({functionName}) failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (nativeResult != null)
                qjs_free_string(nativeResult);
        }
    }

    /// <summary>
    /// Executes arbitrary JavaScript code in an existing persistent context.
    /// </summary>
    public static string? Eval(IntPtr handle, string code)
    {
        if (handle == IntPtr.Zero) return null;

        byte* nativeResult = null;
        try
        {
            nativeResult = qjs_eval(handle, code);
            return MarshalNativeString(nativeResult);
        }
        catch (Exception ex)
        {
            Log.Error($"[QuickJsNative] Eval failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (nativeResult != null)
                qjs_free_string(nativeResult);
        }
    }

    /// <summary>
    /// Pumps event loop (microtasks + expired timers) up to <paramref name="maxMs"/> ms.
    /// </summary>
    public static int PumpEventLoop(IntPtr handle, int maxMs)
    {
        if (handle == IntPtr.Zero) return -1;

        try
        {
            return qjs_pump_event_loop(handle, maxMs);
        }
        catch (Exception ex)
        {
            Log.Error($"[QuickJsNative] PumpEventLoop failed: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// Returns the last JS error (message + stack trace).
    /// </summary>
    public static string? GetLastError(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return "NULL handle";

        try
        {
            var ptr = qjs_get_last_error(handle);
            if (ptr == IntPtr.Zero) return null;
            return Marshal.PtrToStringUTF8(ptr);
        }
        catch (Exception ex)
        {
            return $"GetLastError itself failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Destroys persistent context and releases all QuickJS resources.
    /// </summary>
    public static void DestroyContext(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;

        try
        {
            qjs_destroy_context(handle);
        }
        catch (Exception ex)
        {
            Log.Warn($"[QuickJsNative] DestroyContext failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns captured console output from the native buffer.
    /// In Release builds, calls to this method are stripped by the compiler via [Conditional("DEBUG")]
    /// on the caller side (DumpDiagnosticConsole).
    /// </summary>
    public static string? GetConsoleOutput(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return null;
        var ptr = qjs_get_console_output(handle);
        return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);
    }

    /// <summary>
    /// Clears captured console output in the native buffer.
    /// </summary>
    public static void ClearConsoleOutput(IntPtr handle)
    {
        if (handle != IntPtr.Zero) qjs_clear_console_output(handle);
    }

#if DEBUG

    /// <summary>
    /// Enables or disables string comparison (<c>===</c>) tracing
    /// in QuickJS. On first enable, allocates ring buffer (~2.7 MB).
    /// </summary>
    public static void TraceEnable(IntPtr handle, bool enabled)
    {
        if (handle != IntPtr.Zero)
            qjs_trace_enable(handle, enabled ? 1 : 0);
    }

    /// <summary>
    /// Absolute count of string comparisons since trace was enabled.
    /// </summary>
    public static int TraceGetTotal(IntPtr handle)
        => handle != IntPtr.Zero ? qjs_trace_get_total(handle) : 0;

    /// <summary>
    /// Number of records available in ring buffer (≤ 16384).
    /// </summary>
    public static int TraceGetCount(IntPtr handle)
        => handle != IntPtr.Zero ? qjs_trace_get_count(handle) : 0;

    /// <summary>
    /// Dumps <paramref name="count"/> trace records starting from <paramref name="offset"/>.
    /// </summary>
    public static string? TraceDump(IntPtr handle, int offset, int count)
    {
        if (handle == IntPtr.Zero) return null;

        byte* ptr = null;
        try
        {
            ptr = qjs_trace_dump(handle, offset, count);
            return MarshalNativeString(ptr);
        }
        finally
        {
            if (ptr != null) qjs_free_string(ptr);
        }
    }

    /// <summary>
    /// Dumps trace records where left or right operand contains <paramref name="substring"/>.
    /// Filtered on the C side — minimal P/Invoke marshaling overhead.
    /// </summary>
    public static string? TraceDumpFiltered(IntPtr handle, string substring, int maxCount)
    {
        if (handle == IntPtr.Zero) return null;

        byte* ptr = null;
        try
        {
            ptr = qjs_trace_dump_filtered(handle, substring, maxCount);
            return MarshalNativeString(ptr);
        }
        finally
        {
            if (ptr != null) qjs_free_string(ptr);
        }
    }

    /// <summary>
    /// Resets ring buffer and trace counters.
    /// </summary>
    public static void TraceReset(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
            qjs_trace_reset(handle);
    }

    /// <summary>
    /// DEBUG-only fail-fast check ensuring the expected quickjs_bridge.dll is loaded,
    /// and not an outdated/foreign copy missing native browser environment.
    /// </summary>
    public static void VerifyExpectedBridgeLoaded()
    {
        if (System.Threading.Volatile.Read(ref _bridgeVerified) != 0)
            return;

        try
        {
            var sigPtr = qjs_get_bridge_signature();
            var signature = sigPtr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(sigPtr);
            var featureMask = qjs_get_bridge_feature_mask();
            var loadedPath = _resolvedLibraryPath ?? "(unknown path, loaded via Single-File abstraction)";

            Log.Info($"[QuickJsNative] Loaded quickjs_bridge.dll from: {loadedPath}");
            Log.Info($"[QuickJsNative] Bridge signature: {signature ?? "null"}");
            Log.Info($"[QuickJsNative] Bridge feature mask: 0x{featureMask:X}");

            bool ok =
                !string.IsNullOrWhiteSpace(signature) &&
                signature.Contains("LMP_QJS_BRIDGE", StringComparison.Ordinal) &&
                signature.Contains("BROWSER_ENV=1", StringComparison.Ordinal) &&
                signature.Contains("EVENTS=1", StringComparison.Ordinal) &&
                signature.Contains("DOM=1", StringComparison.Ordinal) &&
                signature.Contains("BOM=1", StringComparison.Ordinal) &&
                featureMask == ExpectedBridgeFeatureMask;

            if (!ok)
            {
                throw new QuickJsBridgeVerificationException(
                    "Loaded quickjs_bridge.dll is stale or incompatible.\n" +
                    $"Path:      {loadedPath}\n" +
                    $"Signature: {signature ?? "null"}\n" +
                    $"Mask:      0x{featureMask:X}\n" +
                    $"Expected:  0x{ExpectedBridgeFeatureMask:X}\n" +
                    "Most likely the app loaded an old DLL from the application directory.");
            }

            System.Threading.Volatile.Write(ref _bridgeVerified, 1);
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new QuickJsBridgeVerificationException(
                "Loaded quickjs_bridge.dll does not expose bridge build metadata exports.\n" +
                "This almost certainly means an old/stale DLL was loaded instead of the freshly built native bridge.\n" +
                $"Loaded path: {_resolvedLibraryPath ?? "(unknown path)"}",
                ex);
        }
    }

#endif

    // --- Private helpers ---

    private static string ExtractAbi()
    {
        try
        {
            var sigPtr = qjs_get_bridge_signature();
            var signature = sigPtr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(sigPtr);
            if (string.IsNullOrEmpty(signature)) return "1";

            var match = ABIRegex.Match(signature);
            return match.Success ? match.Groups[1].Value : "1";
        }
        catch
        {
            return "1";
        }
    }

    /// <summary>
    /// Marshals a null-terminated UTF-8 string from native memory.
    /// </summary>
    private static string? MarshalNativeString(byte* ptr)
    {
        if (ptr is null)
            return null;

        ReadOnlySpan<byte> utf8 = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(ptr);
        return utf8.IsEmpty ? null : Encoding.UTF8.GetString(utf8);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"ABI=(\d+)")]
    private static partial System.Text.RegularExpressions.Regex ABIRegex { get; }
}