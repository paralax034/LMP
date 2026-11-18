using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace LMP.Core.Logger;

/// <summary>
/// Global static access point for the logger.
/// Thread-safe, zero-allocation on both disabled and interpolated UTF-8 paths,
/// and safe before <see cref="Initialize"/> or during <see cref="Shutdown"/>.
/// </summary>
public static class Log
{
    private static readonly Lock _lifecycleLock = new();
    private static AsyncLogProcessor? _processor;
    private static volatile bool _isInitialized;

    /// <summary>
    /// Minimum logging level. Messages below this level
    /// are discarded before string creation — interpolation is not evaluated.
    /// Default: Debug in Debug builds, Info in Release.
    /// </summary>
#if DEBUG
    public static LogLevel MinLevel { get; set; } = LogLevel.Debug;
#else
    public static LogLevel MinLevel { get; set; } = LogLevel.Info;
#endif

#if DEBUG
    private static volatile bool _consoleOutputEnabled = true;

    /// <summary>
    /// Mirrors logs to the console (Debug build only). Headless test runs disable it
    /// so the runner output is not drowned in diagnostics; the log file is always written.
    /// </summary>
    public static bool ConsoleOutputEnabled
    {
        get => _consoleOutputEnabled;
        set => _consoleOutputEnabled = value;
    }

    /// <summary>Full path to the current run log file, or <c>null</c> prior to initialization.</summary>
    public static string? CurrentLogFile => Volatile.Read(ref _processor)?.LogFilePath;
#endif

    /// <summary>
    /// Checks if a log level is active. Used by InterpolatedStringHandlers
    /// for short-circuit evaluation of interpolated strings.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsLevelEnabled(LogLevel level) =>
        _isInitialized && level >= MinLevel;

    /// <summary>
    /// Initializes the logging system.
    /// Must be called at the start of Main, AFTER creating folders (G.Folder.Create).
    /// </summary>
    public static void Initialize(string? logDirectory = null)
    {
        string logsPath;
        lock (_lifecycleLock)
        {
            if (_isInitialized)
            {
                return;
            }

            logsPath = logDirectory ?? GetDefaultLogDirectory();
            _processor = new AsyncLogProcessor(logsPath);
            _isInitialized = true;
        }

        Info($"=== LOGGER INITIALIZED === Path: {logsPath}");
    }

    private static string GetDefaultLogDirectory()
    {
        try
        {
            return G.Folder.Logs;
        }
        catch
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LMP", "Logs");
        }
    }

    /// <summary>
    /// Synchronously flushes all buffered log messages to disk without shutting down the logger.
    /// </summary>
    public static void Flush(int timeoutMs = 2000)
    {
        Volatile.Read(ref _processor)?.Flush(timeoutMs);
    }

    /// <summary>
    /// Performs graceful shutdown. Flushes all buffered messages and closes the active file.
    /// Safe to invoke concurrently or multiple times.
    /// </summary>
    public static void Shutdown()
    {
        AsyncLogProcessor? processorToDispose;
        lock (_lifecycleLock)
        {
            if (!_isInitialized || _processor is null)
            {
                return;
            }

            _processor.EnqueueString(LogLevel.Info, "=== LOGGER SHUTDOWN ===");
            _isInitialized = false;
            processorToDispose = _processor;
            _processor = null;
        }

        processorToDispose.Dispose();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Enqueue(LogLevel level, string message, Exception? ex = null)
    {
        if (!_isInitialized || level < MinLevel)
        {
            return;
        }

        var processor = Volatile.Read(ref _processor);
        if (processor is null)
        {
            return;
        }

        processor.EnqueueString(level, message, ex);

        if (level == LogLevel.Fatal)
        {
            processor.Flush(1000);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EnqueueUtf8(LogLevel level, ReadOnlySpan<byte> utf8Payload, Exception? ex = null)
    {
        if (!_isInitialized || level < MinLevel)
        {
            return;
        }

        var processor = Volatile.Read(ref _processor);
        if (processor is null)
        {
            return;
        }

        processor.EnqueueUtf8(level, utf8Payload, ex);

        if (level == LogLevel.Fatal)
        {
            processor.Flush(1000);
        }
    }

    // String overloads — for literals and pre-constructed strings

    /// <summary>Most detailed level. Used for execution flow tracing.</summary>
    public static void Trace(string message) => Enqueue(LogLevel.Trace, message);

    /// <summary>Debugging information.</summary>
    public static void Debug(string message) => Enqueue(LogLevel.Debug, message);

    /// <summary>Informational messages. Main level.</summary>
    public static void Info(string message) => Enqueue(LogLevel.Info, message);

    /// <summary>Warnings.</summary>
    public static void Warn(string message) => Enqueue(LogLevel.Warning, message);

    /// <summary>Errors.</summary>
    public static void Error(string message, Exception? ex = null) => Enqueue(LogLevel.Error, message, ex);

    /// <summary>Fatal errors.</summary>
    public static void Fatal(string message, Exception? ex = null) => Enqueue(LogLevel.Fatal, message, ex);

    // InterpolatedStringHandler overloads — zero-alloc UTF-8 path when enabled, 0-cost when disabled

    /// <summary>Trace with zero-alloc UTF-8 interpolation.</summary>
    public static void Trace(TraceInterpolatedStringHandler handler)
    {
        try
        {
            if (handler.IsEnabled)
            {
                EnqueueUtf8(LogLevel.Trace, handler.WrittenSpan);
            }
        }
        finally
        {
            handler.Dispose();
        }
    }

    /// <summary>Debug with zero-alloc UTF-8 interpolation.</summary>
    public static void Debug(DebugInterpolatedStringHandler handler)
    {
        try
        {
            if (handler.IsEnabled)
            {
                EnqueueUtf8(LogLevel.Debug, handler.WrittenSpan);
            }
        }
        finally
        {
            handler.Dispose();
        }
    }

    /// <summary>Info with zero-alloc UTF-8 interpolation.</summary>
    public static void Info(InfoInterpolatedStringHandler handler)
    {
        try
        {
            if (handler.IsEnabled)
            {
                EnqueueUtf8(LogLevel.Info, handler.WrittenSpan);
            }
        }
        finally
        {
            handler.Dispose();
        }
    }

    /// <summary>Warn with zero-alloc UTF-8 interpolation.</summary>
    public static void Warn(WarnInterpolatedStringHandler handler)
    {
        try
        {
            if (handler.IsEnabled)
            {
                EnqueueUtf8(LogLevel.Warning, handler.WrittenSpan);
            }
        }
        finally
        {
            handler.Dispose();
        }
    }

    /// <summary>Error with zero-alloc UTF-8 interpolation.</summary>
    public static void Error(ErrorInterpolatedStringHandler handler, Exception? ex = null)
    {
        try
        {
            if (handler.IsEnabled)
            {
                EnqueueUtf8(LogLevel.Error, handler.WrittenSpan, ex);
            }
        }
        finally
        {
            handler.Dispose();
        }
    }

    /// <summary>Fatal with zero-alloc UTF-8 interpolation.</summary>
    public static void Fatal(FatalInterpolatedStringHandler handler, Exception? ex = null)
    {
        try
        {
            if (handler.IsEnabled)
            {
                EnqueueUtf8(LogLevel.Fatal, handler.WrittenSpan, ex);
            }
        }
        finally
        {
            handler.Dispose();
        }
    }

    /// <summary>Overload for objects (like Console.WriteLine).</summary>
    public static void Info(object? obj) => Info(obj?.ToString() ?? "null");

    /// <summary>Overload for objects.</summary>
    public static void Debug(object? obj) => Debug(obj?.ToString() ?? "null");

    // InterpolatedStringHandler per level

    /// <inheritdoc cref="LogInterpolatedStringHandlerBase"/>
    [InterpolatedStringHandler]
    public ref struct TraceInterpolatedStringHandler
    {
        private LogInterpolatedStringHandlerBase _impl;
        internal readonly bool IsEnabled;

        public TraceInterpolatedStringHandler(int literalLength, int formattedCount, out bool isEnabled)
        {
            _impl = new LogInterpolatedStringHandlerBase(literalLength, formattedCount, LogLevel.Trace, out isEnabled);
            IsEnabled = isEnabled;
        }

        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendLiteral"/>
        public void AppendLiteral(string s) => _impl.AppendLiteral(s);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted(string?)"/>
        public void AppendFormatted(string? value) => _impl.AppendFormatted(value);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted(string?,int)"/>
        public void AppendFormatted(string? value, int alignment) => _impl.AppendFormatted(value, alignment);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted(ReadOnlySpan{char})"/>
        public void AppendFormatted(scoped ReadOnlySpan<char> value) => _impl.AppendFormatted(value);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted(ReadOnlySpan{char},int)"/>
        public void AppendFormatted(scoped ReadOnlySpan<char> value, int alignment) => _impl.AppendFormatted(value, alignment);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T)"/>
        public void AppendFormatted<T>(T value) => _impl.AppendFormatted(value);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,string?)"/>
        public void AppendFormatted<T>(T value, string? format) => _impl.AppendFormatted(value, format);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int)"/>
        public void AppendFormatted<T>(T value, int alignment) => _impl.AppendFormatted(value, alignment);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int,string?)"/>
        public void AppendFormatted<T>(T value, int alignment, string? format) =>
            _impl.AppendFormatted(value, alignment, format);

        internal ReadOnlySpan<byte> WrittenSpan => _impl.WrittenSpan;
        internal void Dispose() => _impl.Dispose();
    }

    /// <inheritdoc cref="LogInterpolatedStringHandlerBase"/>
    [InterpolatedStringHandler]
    public ref struct DebugInterpolatedStringHandler
    {
        private LogInterpolatedStringHandlerBase _impl;
        internal readonly bool IsEnabled;

        public DebugInterpolatedStringHandler(int literalLength, int formattedCount, out bool isEnabled)
        {
            _impl = new LogInterpolatedStringHandlerBase(literalLength, formattedCount, LogLevel.Debug, out isEnabled);
            IsEnabled = isEnabled;
        }

        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendLiteral"/>
        public void AppendLiteral(string s) => _impl.AppendLiteral(s);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted(string?)"/>
        public void AppendFormatted(string? value) => _impl.AppendFormatted(value);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted(string?,int)"/>
        public void AppendFormatted(string? value, int alignment) => _impl.AppendFormatted(value, alignment);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted(ReadOnlySpan{char})"/>
        public void AppendFormatted(scoped ReadOnlySpan<char> value) => _impl.AppendFormatted(value);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted(ReadOnlySpan{char},int)"/>
        public void AppendFormatted(scoped ReadOnlySpan<char> value, int alignment) => _impl.AppendFormatted(value, alignment);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T)"/>
        public void AppendFormatted<T>(T value) => _impl.AppendFormatted(value);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,string?)"/>
        public void AppendFormatted<T>(T value, string? format) => _impl.AppendFormatted(value, format);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int)"/>
        public void AppendFormatted<T>(T value, int alignment) => _impl.AppendFormatted(value, alignment);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int,string?)"/>
        public void AppendFormatted<T>(T value, int alignment, string? format) =>
            _impl.AppendFormatted(value, alignment, format);

        internal ReadOnlySpan<byte> WrittenSpan => _impl.WrittenSpan;
        internal void Dispose() => _impl.Dispose();
    }

    /// <inheritdoc cref="LogInterpolatedStringHandlerBase"/>
    [InterpolatedStringHandler]
    public ref struct InfoInterpolatedStringHandler
    {
        private LogInterpolatedStringHandlerBase _impl;
        internal readonly bool IsEnabled;

        public InfoInterpolatedStringHandler(int literalLength, int formattedCount, out bool isEnabled)
        {
            _impl = new LogInterpolatedStringHandlerBase(literalLength, formattedCount, LogLevel.Info, out isEnabled);
            IsEnabled = isEnabled;
        }

        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendLiteral"/>
        public void AppendLiteral(string s) => _impl.AppendLiteral(s);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted(string?)"/>
        public void AppendFormatted(string? value) => _impl.AppendFormatted(value);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted(string?,int)"/>
        public void AppendFormatted(string? value, int alignment) => _impl.AppendFormatted(value, alignment);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted(ReadOnlySpan{char})"/>
        public void AppendFormatted(scoped ReadOnlySpan<char> value) => _impl.AppendFormatted(value);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted(ReadOnlySpan{char},int)"/>
        public void AppendFormatted(scoped ReadOnlySpan<char> value, int alignment) => _impl.AppendFormatted(value, alignment);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T)"/>
        public void AppendFormatted<T>(T value) => _impl.AppendFormatted(value);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,string?)"/>
        public void AppendFormatted<T>(T value, string? format) => _impl.AppendFormatted(value, format);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int)"/>
        public void AppendFormatted<T>(T value, int alignment) => _impl.AppendFormatted(value, alignment);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int,string?)"/>
        public void AppendFormatted<T>(T value, int alignment, string? format) =>
            _impl.AppendFormatted(value, alignment, format);

        internal ReadOnlySpan<byte> WrittenSpan => _impl.WrittenSpan;
        internal void Dispose() => _impl.Dispose();
    }

    /// <inheritdoc cref="LogInterpolatedStringHandlerBase"/>
    [InterpolatedStringHandler]
    public ref struct WarnInterpolatedStringHandler
    {
        private LogInterpolatedStringHandlerBase _impl;
        internal readonly bool IsEnabled;

        public WarnInterpolatedStringHandler(int literalLength, int formattedCount, out bool isEnabled)
        {
            _impl = new LogInterpolatedStringHandlerBase(
                literalLength, formattedCount, LogLevel.Warning, out isEnabled);
            IsEnabled = isEnabled;
        }

        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendLiteral"/>
        public void AppendLiteral(string s) => _impl.AppendLiteral(s);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted(string?)"/>
        public void AppendFormatted(string? value) => _impl.AppendFormatted(value);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted(string?,int)"/>
        public void AppendFormatted(string? value, int alignment) => _impl.AppendFormatted(value, alignment);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted(ReadOnlySpan{char})"/>
        public void AppendFormatted(scoped ReadOnlySpan<char> value) => _impl.AppendFormatted(value);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted(ReadOnlySpan{char},int)"/>
        public void AppendFormatted(scoped ReadOnlySpan<char> value, int alignment) => _impl.AppendFormatted(value, alignment);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T)"/>
        public void AppendFormatted<T>(T value) => _impl.AppendFormatted(value);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,string?)"/>
        public void AppendFormatted<T>(T value, string? format) => _impl.AppendFormatted(value, format);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int)"/>
        public void AppendFormatted<T>(T value, int alignment) => _impl.AppendFormatted(value, alignment);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int,string?)"/>
        public void AppendFormatted<T>(T value, int alignment, string? format) =>
            _impl.AppendFormatted(value, alignment, format);

        internal ReadOnlySpan<byte> WrittenSpan => _impl.WrittenSpan;
        internal void Dispose() => _impl.Dispose();
    }

    /// <inheritdoc cref="LogInterpolatedStringHandlerBase"/>
    [InterpolatedStringHandler]
    public ref struct ErrorInterpolatedStringHandler
    {
        private LogInterpolatedStringHandlerBase _impl;
        internal readonly bool IsEnabled;

        public ErrorInterpolatedStringHandler(int literalLength, int formattedCount, out bool isEnabled)
        {
            _impl = new LogInterpolatedStringHandlerBase(literalLength, formattedCount, LogLevel.Error, out isEnabled);
            IsEnabled = isEnabled;
        }

        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendLiteral"/>
        public void AppendLiteral(string s) => _impl.AppendLiteral(s);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted(string?)"/>
        public void AppendFormatted(string? value) => _impl.AppendFormatted(value);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted(string?,int)"/>
        public void AppendFormatted(string? value, int alignment) => _impl.AppendFormatted(value, alignment);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted(ReadOnlySpan{char})"/>
        public void AppendFormatted(scoped ReadOnlySpan<char> value) => _impl.AppendFormatted(value);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted(ReadOnlySpan{char},int)"/>
        public void AppendFormatted(scoped ReadOnlySpan<char> value, int alignment) => _impl.AppendFormatted(value, alignment);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T)"/>
        public void AppendFormatted<T>(T value) => _impl.AppendFormatted(value);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,string?)"/>
        public void AppendFormatted<T>(T value, string? format) => _impl.AppendFormatted(value, format);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int)"/>
        public void AppendFormatted<T>(T value, int alignment) => _impl.AppendFormatted(value, alignment);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int,string?)"/>
        public void AppendFormatted<T>(T value, int alignment, string? format) =>
            _impl.AppendFormatted(value, alignment, format);

        internal ReadOnlySpan<byte> WrittenSpan => _impl.WrittenSpan;
        internal void Dispose() => _impl.Dispose();
    }

    /// <inheritdoc cref="LogInterpolatedStringHandlerBase"/>
    [InterpolatedStringHandler]
    public ref struct FatalInterpolatedStringHandler
    {
        private LogInterpolatedStringHandlerBase _impl;
        internal readonly bool IsEnabled;

        public FatalInterpolatedStringHandler(int literalLength, int formattedCount, out bool isEnabled)
        {
            _impl = new LogInterpolatedStringHandlerBase(literalLength, formattedCount, LogLevel.Fatal, out isEnabled);
            IsEnabled = isEnabled;
        }

        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendLiteral"/>
        public void AppendLiteral(string s) => _impl.AppendLiteral(s);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted(string?)"/>
        public void AppendFormatted(string? value) => _impl.AppendFormatted(value);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted(string?,int)"/>
        public void AppendFormatted(string? value, int alignment) => _impl.AppendFormatted(value, alignment);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted(ReadOnlySpan{char})"/>
        public void AppendFormatted(scoped ReadOnlySpan<char> value) => _impl.AppendFormatted(value);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted(ReadOnlySpan{char},int)"/>
        public void AppendFormatted(scoped ReadOnlySpan<char> value, int alignment) => _impl.AppendFormatted(value, alignment);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T)"/>
        public void AppendFormatted<T>(T value) => _impl.AppendFormatted(value);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,string?)"/>
        public void AppendFormatted<T>(T value, string? format) => _impl.AppendFormatted(value, format);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int)"/>
        public void AppendFormatted<T>(T value, int alignment) => _impl.AppendFormatted(value, alignment);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int,string?)"/>
        public void AppendFormatted<T>(T value, int alignment, string? format) =>
            _impl.AppendFormatted(value, alignment, format);

        internal ReadOnlySpan<byte> WrittenSpan => _impl.WrittenSpan;
        internal void Dispose() => _impl.Dispose();
    }

    /// <summary>
    /// Shared zero-allocation UTF-8 interpolated string builder for all log levels.
    /// Uses a fast per-thread 1 KB buffer and spills to <see cref="ArrayPool{T}"/> only for large payloads.
    /// </summary>
    public ref struct LogInterpolatedStringHandlerBase
    {
        private const int ThreadBufferSize = 1024;

        [ThreadStatic]
        private static byte[]? t_threadBuffer;

        private byte[]? _buffer;
        private int _pos;
        private readonly bool _enabled;
        private bool _fromPool;

        internal LogInterpolatedStringHandlerBase(
            int literalLength,
            int formattedCount,
            LogLevel level,
            out bool isEnabled)
            : this(literalLength, formattedCount, IsLevelEnabled(level), out isEnabled)
        {
        }

        internal LogInterpolatedStringHandlerBase(
            int literalLength,
            int formattedCount,
            bool forceEnabled,
            out bool isEnabled)
        {
            _enabled = isEnabled = forceEnabled;
            _pos = 0;

            if (!forceEnabled)
            {
                _buffer = null;
                _fromPool = false;
                return;
            }

            int estimatedCap = literalLength * 2 + formattedCount * 24;
            if (estimatedCap <= ThreadBufferSize)
            {
                byte[]? tlsBuf = t_threadBuffer;
                if (tlsBuf is not null)
                {
                    t_threadBuffer = null;
                    _buffer = tlsBuf;
                    _fromPool = false;
                    return;
                }

                _buffer = new byte[ThreadBufferSize];
                _fromPool = false;
                return;
            }

            _buffer = ArrayPool<byte>.Shared.Rent(Math.Min(estimatedCap, 4096));
            _fromPool = true;
        }

        internal readonly ReadOnlySpan<byte> WrittenSpan =>
            _buffer is null ? ReadOnlySpan<byte>.Empty : _buffer.AsSpan(0, _pos);

        /// <summary>Appends a string literal. No-op if the level is disabled.</summary>
        public void AppendLiteral(string s)
        {
            if (_enabled && !string.IsNullOrEmpty(s))
            {
                AppendUtf16Span(s.AsSpan());
            }
        }

        /// <summary>Appends a string value. No-op if the level is disabled.</summary>
        public void AppendFormatted(string? value)
        {
            if (_enabled && !string.IsNullOrEmpty(value))
            {
                AppendUtf16Span(value.AsSpan());
            }
        }

        /// <summary>Appends an aligned string value. No-op if the level is disabled.</summary>
        public void AppendFormatted(string? value, int alignment)
        {
            if (_enabled)
            {
                AppendAlignedChars(value.AsSpan(), alignment);
            }
        }

        /// <summary>Appends a character span directly as UTF-8. No-op if the level is disabled.</summary>
        public void AppendFormatted(scoped ReadOnlySpan<char> value)
        {
            if (_enabled && !value.IsEmpty)
            {
                AppendUtf16Span(value);
            }
        }

        /// <summary>Appends an aligned character span directly as UTF-8. No-op if the level is disabled.</summary>
        public void AppendFormatted(scoped ReadOnlySpan<char> value, int alignment)
        {
            if (_enabled)
            {
                AppendAlignedChars(value, alignment);
            }
        }

        /// <summary>Appends a formattable value directly as UTF-8. No-op if the level is disabled.</summary>
        public void AppendFormatted<T>(T value)
        {
            if (_enabled)
            {
                AppendValueCore(value, []);
            }
        }

        /// <summary>Appends a formattable value with a format string. No-op if the level is disabled.</summary>
        public void AppendFormatted<T>(T value, string? format)
        {
            if (_enabled)
            {
                AppendValueCore(value, format.AsSpan());
            }
        }

        /// <summary>Appends a formattable value with alignment. No-op if the level is disabled.</summary>
        public void AppendFormatted<T>(T value, int alignment)
        {
            if (_enabled)
            {
                AppendFormatted(value, alignment, null);
            }
        }

        /// <summary>Appends a formattable value with alignment and a format string. No-op if the level is disabled.</summary>
        public void AppendFormatted<T>(T value, int alignment, string? format)
        {
            if (!_enabled)
            {
                return;
            }

            if (alignment == 0)
            {
                AppendValueCore(value, format.AsSpan());
                return;
            }

            if (typeof(T) == typeof(string))
            {
                AppendAlignedChars(Unsafe.As<T, string?>(ref value).AsSpan(), alignment);
                return;
            }

            int start = _pos;
            AppendValueCore(value, format.AsSpan());
            int writtenBytes = _pos - start;
            ApplyByteAlignment(start, writtenBytes, alignment);
        }

        internal void Dispose()
        {
            byte[]? buf = _buffer;
            if (buf is null)
            {
                return;
            }

            _buffer = null;
            if (!_fromPool)
            {
                t_threadBuffer ??= buf;
            }
            else
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        private void AppendValueCore<T>(T value, scoped ReadOnlySpan<char> format)
        {
            if (typeof(T) == typeof(string))
            {
                string? str = Unsafe.As<T, string?>(ref value);
                if (!string.IsNullOrEmpty(str))
                {
                    AppendUtf16Span(str.AsSpan());
                }

                return;
            }

            if (typeof(T) == typeof(int))
            {
                FormatUtf8Formattable(Unsafe.As<T, int>(ref value), format);
                return;
            }

            if (typeof(T) == typeof(long))
            {
                FormatUtf8Formattable(Unsafe.As<T, long>(ref value), format);
                return;
            }

            if (typeof(T) == typeof(uint))
            {
                FormatUtf8Formattable(Unsafe.As<T, uint>(ref value), format);
                return;
            }

            if (typeof(T) == typeof(ulong))
            {
                FormatUtf8Formattable(Unsafe.As<T, ulong>(ref value), format);
                return;
            }

            if (typeof(T) == typeof(double))
            {
                FormatUtf8Formattable(Unsafe.As<T, double>(ref value), format);
                return;
            }

            if (typeof(T) == typeof(float))
            {
                FormatUtf8Formattable(Unsafe.As<T, float>(ref value), format);
                return;
            }

            if (typeof(T) == typeof(bool))
            {
                AppendUtf8Bytes(Unsafe.As<T, bool>(ref value) ? "True"u8 : "False"u8);
                return;
            }

            if (typeof(T) == typeof(byte))
            {
                FormatUtf8Formattable(Unsafe.As<T, byte>(ref value), format);
                return;
            }

            if (typeof(T) == typeof(sbyte))
            {
                FormatUtf8Formattable(Unsafe.As<T, sbyte>(ref value), format);
                return;
            }

            if (typeof(T) == typeof(short))
            {
                FormatUtf8Formattable(Unsafe.As<T, short>(ref value), format);
                return;
            }

            if (typeof(T) == typeof(ushort))
            {
                FormatUtf8Formattable(Unsafe.As<T, ushort>(ref value), format);
                return;
            }

            if (typeof(T) == typeof(decimal))
            {
                FormatUtf8Formattable(Unsafe.As<T, decimal>(ref value), format);
                return;
            }

            if (typeof(T) == typeof(char))
            {
                Span<char> ch = stackalloc char[1];
                ch[0] = Unsafe.As<T, char>(ref value);
                AppendUtf16Span(ch);
                return;
            }

            if (typeof(T) == typeof(TimeSpan))
            {
                FormatUtf8Formattable(Unsafe.As<T, TimeSpan>(ref value), format);
                return;
            }

            if (typeof(T) == typeof(DateTime))
            {
                FormatUtf8Formattable(Unsafe.As<T, DateTime>(ref value), format);
                return;
            }

            if (typeof(T) == typeof(DateTimeOffset))
            {
                FormatUtf8Formattable(Unsafe.As<T, DateTimeOffset>(ref value), format);
                return;
            }

            if (typeof(T) == typeof(DateOnly))
            {
                FormatUtf8Formattable(Unsafe.As<T, DateOnly>(ref value), format);
                return;
            }

            if (typeof(T) == typeof(TimeOnly))
            {
                FormatUtf8Formattable(Unsafe.As<T, TimeOnly>(ref value), format);
                return;
            }

            if (typeof(T) == typeof(Guid))
            {
                FormatUtf8Formattable(Unsafe.As<T, Guid>(ref value), format);
                return;
            }

            if (typeof(T) == typeof(nint))
            {
                FormatUtf8Formattable(Unsafe.As<T, nint>(ref value), format);
                return;
            }

            if (typeof(T) == typeof(nuint))
            {
                FormatUtf8Formattable(Unsafe.As<T, nuint>(ref value), format);
                return;
            }

            if (default(T) is null && value is null)
            {
                return;
            }

            if (value is IUtf8SpanFormattable utf8Formattable)
            {
                int bytesWritten;
                while (!utf8Formattable.TryFormat(
                    _buffer.AsSpan(_pos), out bytesWritten, format, CultureInfo.InvariantCulture))
                {
                    Grow(Math.Max(64, _buffer!.Length));
                }

                _pos += bytesWritten;
                return;
            }

            if (value is ISpanFormattable spanFormattable)
            {
                Span<char> charBuf = stackalloc char[128];
                if (spanFormattable.TryFormat(charBuf, out int charsWritten, format, CultureInfo.InvariantCulture))
                {
                    AppendUtf16Span(charBuf[..charsWritten]);
                    return;
                }

                char[] rentedChars = ArrayPool<char>.Shared.Rent(512);
                try
                {
                    while (!spanFormattable.TryFormat(
                        rentedChars, out charsWritten, format, CultureInfo.InvariantCulture))
                    {
                        int nextLen = rentedChars.Length * 2;
                        ArrayPool<char>.Shared.Return(rentedChars);
                        rentedChars = ArrayPool<char>.Shared.Rent(nextLen);
                    }

                    AppendUtf16Span(rentedChars.AsSpan(0, charsWritten));
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(rentedChars);
                }

                return;
            }

            string? fallback = value?.ToString();
            if (!string.IsNullOrEmpty(fallback))
            {
                AppendUtf16Span(fallback.AsSpan());
            }
        }

        private void FormatUtf8Formattable<TFormattable>(TFormattable value, scoped ReadOnlySpan<char> format)
            where TFormattable : IUtf8SpanFormattable
        {
            int bytesWritten;
            while (!value.TryFormat(
                _buffer.AsSpan(_pos), out bytesWritten, format, CultureInfo.InvariantCulture))
            {
                Grow(Math.Max(64, _buffer!.Length));
            }

            _pos += bytesWritten;
        }

        private void AppendAlignedChars(scoped ReadOnlySpan<char> chars, int alignment)
        {
            int width = Math.Abs(alignment);
            int pad = width - chars.Length;

            if (pad <= 0)
            {
                if (!chars.IsEmpty)
                {
                    AppendUtf16Span(chars);
                }

                return;
            }

            if (alignment > 0)
            {
                AppendSpaces(pad);
                if (!chars.IsEmpty)
                {
                    AppendUtf16Span(chars);
                }
            }
            else
            {
                if (!chars.IsEmpty)
                {
                    AppendUtf16Span(chars);
                }

                AppendSpaces(pad);
            }
        }

        private void ApplyByteAlignment(int start, int length, int alignment)
        {
            int width = Math.Abs(alignment);
            int pad = width - length;
            if (pad <= 0)
            {
                return;
            }

            EnsureCapacity(pad);
            if (alignment > 0)
            {
                _buffer.AsSpan(start, length).CopyTo(_buffer.AsSpan(start + pad, length));
                _buffer.AsSpan(start, pad).Fill((byte)' ');
            }
            else
            {
                _buffer.AsSpan(_pos, pad).Fill((byte)' ');
            }

            _pos += pad;
        }

        private void AppendSpaces(int count)
        {
            EnsureCapacity(count);
            _buffer.AsSpan(_pos, count).Fill((byte)' ');
            _pos += count;
        }

        private void AppendUtf8Bytes(ReadOnlySpan<byte> utf8)
        {
            EnsureCapacity(utf8.Length);
            utf8.CopyTo(_buffer.AsSpan(_pos));
            _pos += utf8.Length;
        }

        private void AppendUtf16Span(scoped ReadOnlySpan<char> chars)
        {
            int maxBytes = Encoding.UTF8.GetMaxByteCount(chars.Length);
            EnsureCapacity(maxBytes);
            int written = Encoding.UTF8.GetBytes(chars, _buffer.AsSpan(_pos));
            _pos += written;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureCapacity(int additionalBytes)
        {
            if (_buffer!.Length - _pos < additionalBytes)
            {
                Grow(additionalBytes);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Grow(int additionalBytes)
        {
            byte[] oldBuf = _buffer!;
            int required = _pos + additionalBytes;
            int nextSize = Math.Max(oldBuf.Length * 2, required);
            byte[] newBuf = ArrayPool<byte>.Shared.Rent(nextSize);
            oldBuf.AsSpan(0, _pos).CopyTo(newBuf);
            _buffer = newBuf;

            if (_fromPool)
            {
                ArrayPool<byte>.Shared.Return(oldBuf);
            }
            else
            {
                t_threadBuffer ??= oldBuf;
                _fromPool = true;
            }
        }
    }
}