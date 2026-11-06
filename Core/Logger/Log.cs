using System.Runtime.CompilerServices;

namespace LMP.Core.Logger;

/// <summary>
/// Global static access point for the logger.
/// Thread-safe, supports calls prior to Initialize (will be ignored).
/// </summary>
public static class Log
{
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
    public static string? CurrentLogFile => _processor?.LogFilePath;
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
        if (_isInitialized) return;

        var logsPath = logDirectory ?? GetDefaultLogDirectory();

        _processor = new AsyncLogProcessor(logsPath);
        _isInitialized = true;

        Info($"=== LOGGER INITIALIZED === Path: {logsPath}");
    }

    private static string GetDefaultLogDirectory()
    {
        try { return G.Folder.Logs; }
        catch
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LMP", "Logs");
        }
    }

    /// <summary>
    /// Performs graceful shutdown. Flushes all buffered messages.
    /// Must be called in the finally block of Main.
    /// </summary>
    public static void Shutdown()
    {
        if (!_isInitialized || _processor == null) return;
        Info("=== LOGGER SHUTDOWN ===");
        _processor.Dispose();
        _processor = null;
        _isInitialized = false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Enqueue(LogLevel level, string message, Exception? ex = null)
    {
        if (!_isInitialized || _processor == null || level < MinLevel) return;
        _processor.Enqueue(new LogMessage(level, message, ex));
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

    // InterpolatedStringHandler overloads — zero-alloc when the log level is disabled

    /// <summary>Trace with zero-alloc interpolation.</summary>
    public static void Trace(TraceInterpolatedStringHandler handler)
    {
        if (handler.IsEnabled) Enqueue(LogLevel.Trace, handler.ToStringAndClear());
    }

    /// <summary>Debug with zero-alloc interpolation.</summary>
    public static void Debug(DebugInterpolatedStringHandler handler)
    {
        if (handler.IsEnabled) Enqueue(LogLevel.Debug, handler.ToStringAndClear());
    }

    /// <summary>Info with zero-alloc interpolation.</summary>
    public static void Info(InfoInterpolatedStringHandler handler)
    {
        if (handler.IsEnabled) Enqueue(LogLevel.Info, handler.ToStringAndClear());
    }

    /// <summary>Warn with zero-alloc interpolation.</summary>
    public static void Warn(WarnInterpolatedStringHandler handler)
    {
        if (handler.IsEnabled) Enqueue(LogLevel.Warning, handler.ToStringAndClear());
    }

    /// <summary>Error with zero-alloc interpolation.</summary>
    public static void Error(ErrorInterpolatedStringHandler handler, Exception? ex = null)
    {
        if (handler.IsEnabled) Enqueue(LogLevel.Error, handler.ToStringAndClear(), ex);
    }

    /// <summary>Fatal with zero-alloc interpolation.</summary>
    public static void Fatal(FatalInterpolatedStringHandler handler, Exception? ex = null)
    {
        if (handler.IsEnabled) Enqueue(LogLevel.Fatal, handler.ToStringAndClear(), ex);
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
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T)"/>
        public void AppendFormatted<T>(T value) => _impl.AppendFormatted(value);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,string?)"/>
        public void AppendFormatted<T>(T value, string? format) => _impl.AppendFormatted(value, format);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int)"/>
        public void AppendFormatted<T>(T value, int alignment) => _impl.AppendFormatted(value, alignment);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int,string?)"/>
        public void AppendFormatted<T>(T value, int alignment, string? format) => _impl.AppendFormatted(value, alignment, format);
        internal string ToStringAndClear() => _impl.ToStringAndClear();
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
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T)"/>
        public void AppendFormatted<T>(T value) => _impl.AppendFormatted(value);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,string?)"/>
        public void AppendFormatted<T>(T value, string? format) => _impl.AppendFormatted(value, format);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int)"/>
        public void AppendFormatted<T>(T value, int alignment) => _impl.AppendFormatted(value, alignment);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int,string?)"/>
        public void AppendFormatted<T>(T value, int alignment, string? format) => _impl.AppendFormatted(value, alignment, format);
        internal string ToStringAndClear() => _impl.ToStringAndClear();
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
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T)"/>
        public void AppendFormatted<T>(T value) => _impl.AppendFormatted(value);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,string?)"/>
        public void AppendFormatted<T>(T value, string? format) => _impl.AppendFormatted(value, format);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int)"/>
        public void AppendFormatted<T>(T value, int alignment) => _impl.AppendFormatted(value, alignment);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int,string?)"/>
        public void AppendFormatted<T>(T value, int alignment, string? format) => _impl.AppendFormatted(value, alignment, format);
        internal string ToStringAndClear() => _impl.ToStringAndClear();
    }

    /// <inheritdoc cref="LogInterpolatedStringHandlerBase"/>
    [InterpolatedStringHandler]
    public ref struct WarnInterpolatedStringHandler
    {
        private LogInterpolatedStringHandlerBase _impl;
        internal readonly bool IsEnabled;

        public WarnInterpolatedStringHandler(int literalLength, int formattedCount, out bool isEnabled)
        {
            _impl = new LogInterpolatedStringHandlerBase(literalLength, formattedCount, LogLevel.Warning, out isEnabled);
            IsEnabled = isEnabled;
        }

        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendLiteral"/>
        public void AppendLiteral(string s) => _impl.AppendLiteral(s);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T)"/>
        public void AppendFormatted<T>(T value) => _impl.AppendFormatted(value);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,string?)"/>
        public void AppendFormatted<T>(T value, string? format) => _impl.AppendFormatted(value, format);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int)"/>
        public void AppendFormatted<T>(T value, int alignment) => _impl.AppendFormatted(value, alignment);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int,string?)"/>
        public void AppendFormatted<T>(T value, int alignment, string? format) => _impl.AppendFormatted(value, alignment, format);
        internal string ToStringAndClear() => _impl.ToStringAndClear();
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
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T)"/>
        public void AppendFormatted<T>(T value) => _impl.AppendFormatted(value);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,string?)"/>
        public void AppendFormatted<T>(T value, string? format) => _impl.AppendFormatted(value, format);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int)"/>
        public void AppendFormatted<T>(T value, int alignment) => _impl.AppendFormatted(value, alignment);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int,string?)"/>
        public void AppendFormatted<T>(T value, int alignment, string? format) => _impl.AppendFormatted(value, alignment, format);
        internal string ToStringAndClear() => _impl.ToStringAndClear();
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
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T)"/>
        public void AppendFormatted<T>(T value) => _impl.AppendFormatted(value);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,string?)"/>
        public void AppendFormatted<T>(T value, string? format) => _impl.AppendFormatted(value, format);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int)"/>
        public void AppendFormatted<T>(T value, int alignment) => _impl.AppendFormatted(value, alignment);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int,string?)"/>
        public void AppendFormatted<T>(T value, int alignment, string? format) => _impl.AppendFormatted(value, alignment, format);
        internal string ToStringAndClear() => _impl.ToStringAndClear();
    }

    /// <summary>
    /// Shared implementation of the interpolated string builder for all log levels.
    /// </summary>
    public ref struct LogInterpolatedStringHandlerBase
    {
        private DefaultInterpolatedStringHandler _inner;
        private readonly bool _enabled;

        internal LogInterpolatedStringHandlerBase(
            int literalLength,
            int formattedCount,
            LogLevel level,
            out bool isEnabled)
        {
            _enabled = isEnabled = IsLevelEnabled(level);
            _inner = _enabled
                ? new DefaultInterpolatedStringHandler(literalLength, formattedCount)
                : default;
        }

        /// <summary>Appends a string literal. No-op if the level is disabled.</summary>
        public void AppendLiteral(string s)
        {
            if (_enabled) _inner.AppendLiteral(s);
        }

        /// <summary>Appends a formattable value. No-op if the level is disabled.</summary>
        public void AppendFormatted<T>(T value)
        {
            if (_enabled) _inner.AppendFormatted(value);
        }

        /// <summary>Appends a formattable value with a format string. No-op if the level is disabled.</summary>
        public void AppendFormatted<T>(T value, string? format)
        {
            if (_enabled) _inner.AppendFormatted(value, format);
        }

        /// <summary>
        /// Appends a formattable value with alignment: <c>{x,-45}</c>, <c>{x,15}</c>.
        /// No-op if the level is disabled.
        /// </summary>
        public void AppendFormatted<T>(T value, int alignment)
        {
            if (_enabled) _inner.AppendFormatted(value, alignment);
        }

        /// <summary>
        /// Appends a formattable value with alignment and a format string: <c>{x,15:F2}</c>.
        /// No-op if the level is disabled.
        /// </summary>
        public void AppendFormatted<T>(T value, int alignment, string? format)
        {
            if (_enabled) _inner.AppendFormatted(value, alignment, format);
        }

        internal string ToStringAndClear() =>
            _enabled ? _inner.ToStringAndClear() : string.Empty;
    }
}