using System.Text;
using System.Threading.Channels;

namespace LMP.Core.Logger;

/// <summary>
/// Асинхронный процессор логов с буферизацией и batch-записью.
/// Использует bounded channel для back-pressure при высокой нагрузке.
/// </summary>
public sealed class AsyncLogProcessor : IDisposable, IAsyncDisposable
{
    private readonly Channel<LogMessage> _channel;
    private readonly Task _workerTask;
    private readonly CancellationTokenSource _cts;
    private readonly string _logDirectory;
    private readonly string _currentLogFile;

    /// <summary>
    /// Максимальный размер очереди. При переполнении старые сообщения отбрасываются.
    /// 5000 сообщений ≈ 500KB в памяти при среднем размере 100 байт.
    /// </summary>
    private const int MAX_BUFFER_SIZE = 5000;
    
    /// <summary>
    /// Размер batch для записи. Снижает количество syscall'ов.
    /// </summary>
    private const int BATCH_SIZE = 50;

    /// <summary>
    /// Создаёт процессор логов с указанной папкой для хранения.
    /// </summary>
    /// <param name="logDirectory">
    /// Полный путь к папке логов. Папка будет создана если не существует.
    /// Если null — используется fallback в папку приложения.
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
    /// <summary>Полный путь к файлу лога текущего запуска.</summary>
    public string LogFilePath => _currentLogFile;
#endif

    /// <summary>
    /// Добавляет сообщение в очередь. Неблокирующий вызов.
    /// При переполнении очереди старые сообщения отбрасываются.
    /// </summary>
    public void Enqueue(LogMessage message)
    {
        _channel.Writer.TryWrite(message);
    }

    /// <summary>
    /// Основной worker loop. Читает batch сообщений и пишет в файл.
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
        
        // Единственный переиспользуемый StringBuilder для сборки всей строки лога (Zero-Alloc в steady state)
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
                        // В Debug выводим в консоль с цветами, если зеркалирование не отключено headless-раннером тестов
                        if (Log.ConsoleOutputEnabled)
                        {
                            WriteToConsole(log, sb.ToString());
                        }
#endif
                        // Запись StringBuilder напрямую в StreamWriter без промежуточной аллокации ToString()
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

        // Финальный drain — записываем всё что осталось в очереди
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
    /// Цветной вывод в консоль для Debug-сборок.
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
    /// Форматирует лог-сообщение в StringBuilder без аллокаций.
    /// </summary>
    private static void FormatLog(StringBuilder sb, LogMessage log)
    {
        sb.Append('[');
        
        // Zero-alloc форматирование даты на стеке
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
    /// Короткие строки уровней для компактности лога.
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
    /// Асинхронное освобождение ресурсов. Дожидается записи всех сообщений.
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
            // Ожидаемо при отмене
        }
        
        _cts.Dispose();
    }

    /// <summary>
    /// Синхронное освобождение. Блокирует до завершения записи без риска взаимной блокировки.
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
            // Ожидаемо
        }

        _cts.Dispose();
    }
}