using System.Text;
using System.Threading.Channels;

namespace LMP.Core.Logger;

/// <summary>
/// Asynchronous log processor with buffering and batch writing.
/// Uses a bounded channel for back-pressure under heavy load.
/// </summary>
public sealed class AsyncLogProcessor : IDisposable, IAsyncDisposable
{
    private readonly Channel<LogMessage> _channel;
    private readonly Task _workerTask;
    private readonly CancellationTokenSource _cts;
    private readonly string _logDirectory;
    private readonly string _currentLogFile;

    /// <summary>
    /// Maximum queue size. When full, oldest messages are dropped.
    /// 5000 messages ≈ 500KB in memory at an average size of 100 bytes.
    /// </summary>
    private const int MAX_BUFFER_SIZE = 5000;
    
    /// <summary>
    /// Batch size for writing. Reduces the number of syscalls.
    /// </summary>
    private const int BATCH_SIZE = 50;

    /// <summary>
    /// Creates a log processor with the specified storage directory.
    /// </summary>
    /// <param name="logDirectory">
    /// Full path to the log directory. The directory will be created if it does not exist.
    /// If null, falls back to the application directory.
    /// </param>
    public AsyncLogProcessor(string? logDirectory = null)
    {
        _logDirectory = logDirectory 
            ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        
        if (!Directory.Exists(_logDirectory))
        {
            Directory.CreateDirectory(_logDirectory);
        }

        string fileName = $"log_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt";
        _currentLogFile = Path.Combine(_logDirectory, fileName);

        var options = new BoundedChannelOptions(MAX_BUFFER_SIZE)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        };
        _channel = Channel.CreateBounded<LogMessage>(options);

        _cts = new CancellationTokenSource();
        
        _workerTask = Task.Factory.StartNew(
            ProcessQueueAsync, 
            _cts.Token,
            TaskCreationOptions.LongRunning, 
            TaskScheduler.Default
        ).Unwrap();
    }

#if DEBUG
    /// <summary>Full path to the current run's log file.</summary>
    public string LogFilePath => _currentLogFile;
#endif

    /// <summary>
    /// Enqueues a message. Non-blocking call.
    /// When the queue overflows, oldest messages are dropped.
    /// </summary>
    public void Enqueue(LogMessage message)
    {
        _channel.Writer.TryWrite(message);
    }

    /// <summary>
    /// Main worker loop. Reads a batch of messages and writes them to the file.
    /// </summary>
    private async Task ProcessQueueAsync()
    {
        await using var fs = new FileStream(
            _currentLogFile, 
            FileMode.Append, 
            FileAccess.Write, 
            FileShare.Read,
            bufferSize: 4096, 
            useAsync: true
        );
        await using var sw = new StreamWriter(fs, Encoding.UTF8);

        var buffer = new List<LogMessage>(BATCH_SIZE);
        
        // Single reusable StringBuilder for assembling the entire log line (zero-alloc in steady state)
        var sb = new StringBuilder(512);

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                if (await _channel.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
                {
                    while (buffer.Count < BATCH_SIZE && _channel.Reader.TryRead(out var msg))
                    {
                        buffer.Add(msg);
                    }

                    for (int i = 0; i < buffer.Count; i++)
                    {
                        var log = buffer[i];
                        sb.Clear();
                        FormatLog(sb, log);

#if DEBUG
                        // In Debug, output to console with colors unless mirroring is disabled by a headless test runner
                        if (Log.ConsoleOutputEnabled)
                        {
                            WriteToConsole(log, sb.ToString());
                        }
#endif
                        // Writing StringBuilder directly to StreamWriter without intermediate ToString() allocation
                        await sw.WriteAsync(sb).ConfigureAwait(false);
                    }

                    await sw.FlushAsync().ConfigureAwait(false);
                    buffer.Clear();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"LOGGER FAILURE: {ex.Message}");
            }
        }

        // Final drain — write everything remaining in the queue
        while (_channel.Reader.TryRead(out var msg))
        {
            sb.Clear();
            FormatLog(sb, msg);
#if DEBUG
            if (Log.ConsoleOutputEnabled)
            {
                WriteToConsole(msg, sb.ToString());
            }
#endif
            await sw.WriteAsync(sb).ConfigureAwait(false);
        }
        
        await sw.FlushAsync().ConfigureAwait(false);
    }

#if DEBUG
    /// <summary>
    /// Colored console output for Debug builds.
    /// </summary>
    private static void WriteToConsole(LogMessage log, string formattedLine)
    {
        var originalColor = Console.ForegroundColor;

        Console.ForegroundColor = log.Level switch
        {
            LogLevel.Trace => ConsoleColor.DarkGray,
            LogLevel.Debug => ConsoleColor.Gray,
            LogLevel.Info => ConsoleColor.White,
            LogLevel.Warning => ConsoleColor.DarkYellow,
            LogLevel.Error => ConsoleColor.Red,
            LogLevel.Fatal => ConsoleColor.Magenta,
            _ => ConsoleColor.White
        };

        Console.Write(formattedLine);
        Console.ForegroundColor = originalColor;
    }
#endif

    /// <summary>
    /// Formats a log message into a StringBuilder without allocations.
    /// </summary>
    private static void FormatLog(StringBuilder sb, LogMessage log)
    {
        sb.Append('[');
        
        // Zero-alloc stack-based date formatting
        Span<char> timeBuffer = stackalloc char[12];
        if (log.Timestamp.ToLocalTime().TryFormat(timeBuffer, out int charsWritten, "HH:mm:ss.fff"))
        {
            sb.Append(timeBuffer[..charsWritten]);
        }
        else
        {
            sb.Append(log.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff"));
        }

        sb.Append("] [")
          .Append(GetLevelString(log.Level))
          .Append("] ")
          .Append(log.Message);

        if (log.Exception != null)
        {
            sb.AppendLine()
              .Append("   >>> EXCEPTION: ")
              .Append(log.Exception.ToString());
        }

        sb.AppendLine();
    }

    /// <summary>
    /// Short level strings for compact log output.
    /// </summary>
    private static string GetLevelString(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Fatal => "FTL",
        _ => "UNK"
    };

    /// <summary>
    /// Asynchronous resource disposal. Waits for all pending messages to be written.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _channel.Writer.Complete();
        _cts.Cancel();
        
        try 
        { 
            await _workerTask.ConfigureAwait(false); 
        } 
        catch (OperationCanceledException) 
        { 
            // Expected on cancellation
        }
        
        _cts.Dispose();
    }

    /// <summary>
    /// Synchronous disposal. Blocks until writing completes without deadlock risk.
    /// </summary>
    public void Dispose()
    {
        _channel.Writer.Complete();
        _cts.Cancel();

        try
        {
            _workerTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        _cts.Dispose();
    }
}