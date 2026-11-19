using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Unicode;

namespace LMP.Core.Logger;

/// <summary>
/// High-performance asynchronous UTF-8 log processor backed by a pre-allocated ring buffer,
/// a dedicated OS background thread, consecutive duplicate collapsing, and file retention rotation.
/// </summary>
public sealed partial class AsyncLogProcessor : IDisposable, IAsyncDisposable
{
    /// <summary>Default number of log files retained in the log directory.</summary>
    public const int DefaultMaxRetainedFiles = 15;

    /// <summary>Default maximum size in bytes per log file before rotating.</summary>
    public const long DefaultMaxFileSizeBytes = 20L * 1024 * 1024;

    private const int MaxSlotCount = 5000;
    private const int ByteRingCapacity = 512 * 1024;
    private const int MaxRingPayloadBytes = 32 * 1024;
    private const int BatchMaxSlots = 512;
    private const int WriteBufferSize = 64 * 1024;
    private const int MaxDedupPayloadBytes = 4096;
    private const int MaxRepeatCollapseCount = 1000;
    private const int RepeatFlushIntervalMs = 2000;
    private const int CoalesceWindowMs = 25;
    private const int MaxPrefixBytes = 40;

    private static readonly long RepeatMaxHoldTicks = TimeSpan.FromSeconds(5).Ticks;
    private static readonly byte[] NewLineBytes = Encoding.UTF8.GetBytes(Environment.NewLine);

    private readonly string _logDirectory;
    private readonly int _maxRetainedFiles;
    private readonly long _maxFileSizeBytes;

    private readonly Lock _queueLock = new();
    private readonly ManualResetEventSlim _wakeSignal = new(false);
    private readonly ManualResetEventSlim _urgentSignal = new(false);
    private readonly ManualResetEventSlim _flushEvent = new(false);
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    private readonly LogSlot[] _slots = new LogSlot[MaxSlotCount];
    private readonly byte[] _byteRing = new byte[ByteRingCapacity];

    private int _slotHead;
    private int _slotTail;
    private int _slotCount;
    private int _byteTail;
    private int _byteUsed;

    private long _enqueuedSeq;
    private long _flushedSeq;
    private long _flushRequestedSeq;
    private bool _flushRequestPending;
    private bool _workerAsleep;
    private bool _stopRequested;
    private int _disposeState;

    private volatile string _currentLogFile;
    private FileStream _fileStream;
    private long _bytesWrittenInCurrentFile;

    // Worker-thread-only batch, output, and deduplication buffers
    private readonly LogSlot[] _batchSlots = new LogSlot[BatchMaxSlots];
    private readonly int[] _batchOffsets = new int[BatchMaxSlots];
    private readonly byte[] _batchBytes = new byte[WriteBufferSize];
    private readonly byte[] _writeBuffer = new byte[WriteBufferSize];
    private int _writePos;
    private long _processedSeq;

    private readonly byte[] _lastPayload = new byte[MaxDedupPayloadBytes];
    private readonly byte[] _literalScratch = new byte[MaxDedupPayloadBytes];
    private int _lastPayloadLen = -1;
    private LogLevel _lastLevel;
    private Type? _lastExType;
    private string? _lastExMessage;
    private int _lastThreadId;
    private long _firstRepeatUtcTicks;
    private long _lastRepeatUtcTicks;
    private int _repeatCount;
    private bool _repeatMultiThread;

#if DEBUG
    private readonly Stream _consoleStream;
    private readonly byte[] _consoleBuffer = new byte[WriteBufferSize + 64];
    private readonly bool _useAnsiConsole;
    private int _fileFlushCount;
#endif

    private readonly Thread _workerThread;

    /// <summary>Full path to the active log file.</summary>
    public string LogFilePath => _currentLogFile;

#if DEBUG
    internal bool SuppressConsoleOutput { get; set; }
#endif

    /// <summary>
    /// Creates a log processor storing log files in <paramref name="logDirectory"/> with default rotation limits.
    /// </summary>
    /// <param name="logDirectory">
    /// Full path to the log directory. Created if missing. Falls back to the application directory when null.
    /// </param>
    public AsyncLogProcessor(string? logDirectory = null)
        : this(logDirectory, DefaultMaxRetainedFiles, DefaultMaxFileSizeBytes)
    {
    }

    /// <summary>
    /// Creates a log processor with explicit retention and file size rotation thresholds.
    /// </summary>
    /// <param name="logDirectory">Full path to the log directory, or null for the default folder.</param>
    /// <param name="maxRetainedFiles">Maximum number of <c>log_*.txt</c> files kept in the directory.</param>
    /// <param name="maxFileSizeBytes">Maximum size per log file in bytes before rotating to a new file.</param>
    public AsyncLogProcessor(string? logDirectory, int maxRetainedFiles, long maxFileSizeBytes)
    {
        _logDirectory = logDirectory
            ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        _maxRetainedFiles = Math.Max(1, maxRetainedFiles);
        _maxFileSizeBytes = Math.Max(256, maxFileSizeBytes);

        if (!Directory.Exists(_logDirectory))
        {
            Directory.CreateDirectory(_logDirectory);
        }

        (_currentLogFile, _fileStream) = CreateUniqueLogFileStream(_logDirectory);
        PruneOldLogFiles();

#if DEBUG
        _consoleStream = Console.OpenStandardOutput();
        _useAnsiConsole = !Console.IsOutputRedirected && TryEnableAnsiConsole();
#endif

        _workerThread = new Thread(WorkerMain)
        {
            Name = "LMP.Logger",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };
        _workerThread.Start();
    }

    /// <summary>
    /// Enqueues a legacy <see cref="LogMessage"/> without blocking the caller.
    /// When the queue overflows, the oldest messages are dropped.
    /// </summary>
    public void Enqueue(LogMessage message)
    {
        EnqueueSlot(
            message.Timestamp.ToUniversalTime().Ticks,
            message.Level,
            message.ThreadId,
            [],
            message.Message ?? string.Empty,
            message.Exception);
    }

    /// <summary>
    /// Enqueues a pre-encoded UTF-8 message payload directly into the internal ring buffer with zero heap allocations.
    /// </summary>
    internal void EnqueueUtf8(LogLevel level, ReadOnlySpan<byte> utf8Payload, Exception? exception = null)
    {
        long utcTicks = DateTime.UtcNow.Ticks;
        int threadId = Environment.CurrentManagedThreadId;

        if (utf8Payload.Length > MaxRingPayloadBytes)
        {
            string text = Encoding.UTF8.GetString(utf8Payload);
            EnqueueSlot(utcTicks, level, threadId, [], text, exception);
            return;
        }

        EnqueueSlot(utcTicks, level, threadId, utf8Payload, null, exception);
    }

    /// <summary>
    /// Enqueues a pre-existing string message without copying or transcoding on the caller thread.
    /// </summary>
    internal void EnqueueString(LogLevel level, string message, Exception? exception = null)
    {
        EnqueueSlot(
            DateTime.UtcNow.Ticks,
            level,
            Environment.CurrentManagedThreadId,
            [],
            message,
            exception);
    }

    /// <summary>
    /// Synchronously waits until all previously enqueued messages (and any pending repeat summary) are flushed to disk.
    /// </summary>
    /// <param name="timeoutMs">Maximum time to wait in milliseconds.</param>
    public void Flush(int timeoutMs = 2000)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }

        if (!_flushGate.Wait(timeoutMs))
        {
            return;
        }

        try
        {
            long targetSeq;
            bool wakeWorker = false;
            lock (_queueLock)
            {
                if (_stopRequested)
                {
                    return;
                }

                targetSeq = _enqueuedSeq;
                if (targetSeq > _flushRequestedSeq)
                {
                    _flushRequestedSeq = targetSeq;
                }

                _flushRequestPending = true;

                if (_workerAsleep)
                {
                    _workerAsleep = false;
                    wakeWorker = true;
                }
            }

            _urgentSignal.Set();
            if (wakeWorker)
            {
                _wakeSignal.Set();
            }

            long deadline = timeoutMs == Timeout.Infinite
                ? long.MaxValue
                : Environment.TickCount64 + Math.Max(0, timeoutMs);

            while (true)
            {
                lock (_queueLock)
                {
                    if (_stopRequested || (_flushedSeq >= targetSeq && !_flushRequestPending))
                    {
                        return;
                    }

                    _flushEvent.Reset();
                }

                int remaining = timeoutMs == Timeout.Infinite
                    ? Timeout.Infinite
                    : (int)Math.Max(0, deadline - Environment.TickCount64);

                if (remaining == 0)
                {
                    return;
                }

                _flushEvent.Wait(remaining);
            }
        }
        finally
        {
            try
            {
                _flushGate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void EnqueueSlot(
        long utcTicks,
        LogLevel level,
        int threadId,
        ReadOnlySpan<byte> utf8Payload,
        string? literalMessage,
        Exception? exception)
    {
        bool copyToRing = literalMessage is null && utf8Payload.Length > 0;
        bool wakeWorker = false;
        bool wakeUrgent = false;

        lock (_queueLock)
        {
            if (_stopRequested)
            {
                return;
            }

            if (copyToRing)
            {
                while (_slotCount > 0 && (_slotCount >= MaxSlotCount || (_byteRing.Length - _byteUsed) < utf8Payload.Length))
                {
                    DropOldestLocked();
                }
            }
            else if (_slotCount >= MaxSlotCount)
            {
                DropOldestLocked();
            }

            _enqueuedSeq++;
            int offset = 0;

            if (copyToRing)
            {
                offset = _byteTail;
                int firstPart = Math.Min(utf8Payload.Length, _byteRing.Length - offset);
                utf8Payload[..firstPart].CopyTo(_byteRing.AsSpan(offset, firstPart));

                if (firstPart < utf8Payload.Length)
                {
                    int secondPart = utf8Payload.Length - firstPart;
                    utf8Payload.Slice(firstPart, secondPart).CopyTo(_byteRing.AsSpan(0, secondPart));
                }

                _byteTail = (_byteTail + utf8Payload.Length) % _byteRing.Length;
                _byteUsed += utf8Payload.Length;
            }

            _slots[_slotTail] = new LogSlot
            {
                TimestampUtcTicks = utcTicks,
                Level = level,
                ThreadId = threadId,
                ByteOffset = offset,
                ByteLength = copyToRing ? utf8Payload.Length : 0,
                HasBytes = copyToRing,
                LiteralMessage = literalMessage,
                Exception = exception
            };
            _slotTail = (_slotTail + 1) % _slots.Length;
            _slotCount++;

            if (_workerAsleep)
            {
                _workerAsleep = false;
                wakeWorker = true;
            }

            if (_slotCount >= BatchMaxSlots)
            {
                wakeUrgent = true;
            }
        }

        if (wakeUrgent)
        {
            _urgentSignal.Set();
        }

        if (wakeWorker)
        {
            _wakeSignal.Set();
        }
    }

    private void DropOldestLocked()
    {
        if (_slotCount == 0)
        {
            return;
        }

        ref LogSlot oldest = ref _slots[_slotHead];
        if (oldest.HasBytes)
        {
            _byteUsed -= oldest.ByteLength;
        }

        oldest.LiteralMessage = null;
        oldest.Exception = null;
        _slotHead = (_slotHead + 1) % _slots.Length;
        _slotCount--;
    }

    private void WorkerMain()
    {
        try
        {
            while (true)
            {
                int batchCount = DequeueBatch(
                    out long batchEndSeq,
                    out bool stopNow,
                    out bool flushRequested);

                if (batchCount > 0)
                {
                    ProcessBatch(batchCount);
                    _processedSeq = batchEndSeq;

                    if (flushRequested && _repeatCount > 0)
                    {
                        EmitRepeatSummary(_lastRepeatUtcTicks);
                        _repeatCount = 0;
                        _repeatMultiThread = false;
                    }

                    continue;
                }

                if (_repeatCount > 0)
                {
                    long nowTicks = DateTime.UtcNow.Ticks;
                    if (flushRequested || stopNow || (nowTicks - _firstRepeatUtcTicks) >= RepeatMaxHoldTicks)
                    {
                        EmitRepeatSummary(_lastRepeatUtcTicks);
                        _repeatCount = 0;
                        _repeatMultiThread = false;
                    }
                }

                FlushWriteBufferToFile();

                if (flushRequested)
                {
                    _fileStream.Flush();
                }

                bool hasRepeatPending = _repeatCount > 0;
                lock (_queueLock)
                {
                    if (flushRequested)
                    {
                        if (_processedSeq > _flushedSeq)
                        {
                            _flushedSeq = _processedSeq;
                        }

                        if (_flushedSeq >= _flushRequestedSeq)
                        {
                            _flushRequestPending = false;
                        }

                        _flushEvent.Set();
                    }

                    if (stopNow)
                    {
                        break;
                    }

                    if (_slotCount > 0 || _stopRequested || _flushRequestPending)
                    {
                        continue;
                    }

                    _workerAsleep = true;
                    _wakeSignal.Reset();
                    _urgentSignal.Reset();
                }

                if (hasRepeatPending)
                {
                    _wakeSignal.Wait(RepeatFlushIntervalMs);
                }
                else
                {
                    _wakeSignal.Wait();
                }

                _urgentSignal.Wait(CoalesceWindowMs);
            }
        }
        catch (Exception ex)
        {
            try
            {
                Console.Error.WriteLine($"LOGGER FAILURE: {ex.Message}");
            }
            catch
            {
            }
        }
        finally
        {
            try
            {
                if (_repeatCount > 0)
                {
                    EmitRepeatSummary(_lastRepeatUtcTicks);
                    _repeatCount = 0;
                }

                FlushWriteBufferToFile();
                _fileStream.Flush();
                _fileStream.Dispose();
            }
            catch
            {
            }

            lock (_queueLock)
            {
                _flushedSeq = _enqueuedSeq;
                _flushRequestPending = false;
                _flushEvent.Set();
            }
        }
    }

    private int DequeueBatch(out long batchEndSeq, out bool stopNow, out bool flushRequested)
    {
        int count = 0;
        int bytesPos = 0;

        lock (_queueLock)
        {
            while (count < BatchMaxSlots && _slotCount > 0)
            {
                ref LogSlot slot = ref _slots[_slotHead];
                if (slot.HasBytes && (bytesPos + slot.ByteLength) > _batchBytes.Length)
                {
                    break;
                }

                _batchSlots[count] = slot;
                if (slot.HasBytes)
                {
                    _batchOffsets[count] = bytesPos;
                    int firstPart = Math.Min(slot.ByteLength, _byteRing.Length - slot.ByteOffset);
                    _byteRing.AsSpan(slot.ByteOffset, firstPart).CopyTo(_batchBytes.AsSpan(bytesPos, firstPart));

                    if (firstPart < slot.ByteLength)
                    {
                        int secondPart = slot.ByteLength - firstPart;
                        _byteRing.AsSpan(0, secondPart).CopyTo(_batchBytes.AsSpan(bytesPos + firstPart, secondPart));
                    }

                    bytesPos += slot.ByteLength;
                    _byteUsed -= slot.ByteLength;
                }
                else
                {
                    _batchOffsets[count] = 0;
                }

                slot.LiteralMessage = null;
                slot.Exception = null;
                _slotHead = (_slotHead + 1) % _slots.Length;
                _slotCount--;
                count++;
            }

            batchEndSeq = _enqueuedSeq - _slotCount;
            stopNow = _stopRequested && _slotCount == 0;
            flushRequested = _flushRequestPending && _slotCount == 0;
        }

        return count;
    }

    private void ProcessBatch(int count)
    {
        for (int i = 0; i < count; i++)
        {
            ref readonly LogSlot slot = ref _batchSlots[i];
            ReadOnlySpan<byte> payloadSpan = [];
            string? literal = slot.LiteralMessage;

            if (slot.HasBytes)
            {
                payloadSpan = _batchBytes.AsSpan(_batchOffsets[i], slot.ByteLength);
            }
            else if (literal is not null)
            {
                int maxBytes = literal.Length <= MaxDedupPayloadBytes
                    ? Encoding.UTF8.GetMaxByteCount(literal.Length)
                    : int.MaxValue;

                if (maxBytes <= _literalScratch.Length)
                {
                    int encoded = Encoding.UTF8.GetBytes(literal.AsSpan(), _literalScratch);
                    payloadSpan = _literalScratch.AsSpan(0, encoded);
                    literal = null;
                }
            }

            Type? exType = slot.Exception?.GetType();
            string? exMsg = slot.Exception?.Message;

            if (literal is null &&
                _lastPayloadLen >= 0 &&
                slot.Level == _lastLevel &&
                payloadSpan.Length <= MaxDedupPayloadBytes &&
                payloadSpan.Length == _lastPayloadLen &&
                exType == _lastExType &&
                string.Equals(exMsg, _lastExMessage, StringComparison.Ordinal) &&
                payloadSpan.SequenceEqual(_lastPayload.AsSpan(0, _lastPayloadLen)))
            {
                _repeatCount++;
                _lastRepeatUtcTicks = slot.TimestampUtcTicks;
                if (slot.ThreadId != _lastThreadId)
                {
                    _repeatMultiThread = true;
                }

                if (_repeatCount >= MaxRepeatCollapseCount ||
                    (slot.TimestampUtcTicks - _firstRepeatUtcTicks) >= RepeatMaxHoldTicks)
                {
                    EmitRepeatSummary(_lastRepeatUtcTicks);
                    _repeatCount = 0;
                    _firstRepeatUtcTicks = slot.TimestampUtcTicks;
                    _repeatMultiThread = false;
                }

                continue;
            }

            if (_repeatCount > 0)
            {
                EmitRepeatSummary(_lastRepeatUtcTicks);
                _repeatCount = 0;
                _repeatMultiThread = false;
            }

            WriteFormattedEntry(slot.TimestampUtcTicks, slot.Level, slot.ThreadId, payloadSpan, literal, slot.Exception);

            if (literal is null && payloadSpan.Length <= MaxDedupPayloadBytes)
            {
                payloadSpan.CopyTo(_lastPayload);
                _lastPayloadLen = payloadSpan.Length;
                _lastLevel = slot.Level;
                _lastExType = exType;
                _lastExMessage = exMsg;
                _lastThreadId = slot.ThreadId;
                _firstRepeatUtcTicks = slot.TimestampUtcTicks;
                _lastRepeatUtcTicks = slot.TimestampUtcTicks;
            }
            else
            {
                _lastPayloadLen = -1;
            }
        }
    }

    private void EmitRepeatSummary(long timestampUtcTicks)
    {
        double elapsedSec = Math.Max(0.0, (_lastRepeatUtcTicks - _firstRepeatUtcTicks) / (double)TimeSpan.TicksPerSecond);
        int threadId = _repeatMultiThread ? -1 : _lastThreadId;

        Span<byte> msgBuf = stackalloc byte[64];
        int pos = 0;
        "  \u21b3 repeated "u8.CopyTo(msgBuf);
        pos += "  \u21b3 repeated "u8.Length;

        if (Utf8Formatter.TryFormat(_repeatCount, msgBuf[pos..], out int cntLen))
        {
            pos += cntLen;
        }

        "x ("u8.CopyTo(msgBuf[pos..]);
        pos += 3;

        if (Utf8Formatter.TryFormat(elapsedSec, msgBuf[pos..], out int secLen, new StandardFormat('F', 1)))
        {
            pos += secLen;
        }

        "s)"u8.CopyTo(msgBuf[pos..]);
        pos += 2;

        WriteFormattedEntry(timestampUtcTicks, _lastLevel, threadId, msgBuf[..pos], null, null);
    }

    private void WriteFormattedEntry(
        long utcTicks,
        LogLevel level,
        int threadId,
        ReadOnlySpan<byte> utf8Payload,
        string? giantLiteral,
        Exception? exception)
    {
        RotateFileIfNeeded();
        EnsureWriteCapacity(MaxPrefixBytes);

#if DEBUG
        int lineStart = _writePos;
        int flushesBefore = _fileFlushCount;
#endif

        WritePrefix(utcTicks, level, threadId);

        if (giantLiteral is not null)
        {
            WriteUtf16Chunked(giantLiteral.AsSpan());
        }
        else if (!utf8Payload.IsEmpty)
        {
            WriteBytesChunked(utf8Payload);
        }

        if (exception is not null)
        {
            WriteBytesChunked(NewLineBytes);
            WriteBytesChunked("   >>> EXCEPTION: "u8);
            WriteUtf16Chunked(exception.ToString().AsSpan());
        }

        WriteBytesChunked(NewLineBytes);

#if DEBUG
        if (Log.ConsoleOutputEnabled &&
            !SuppressConsoleOutput &&
            giantLiteral is null &&
            _fileFlushCount == flushesBefore &&
            _writePos > lineStart)
        {
            WriteLineToConsole(level, _writeBuffer.AsSpan(lineStart, _writePos - lineStart));
        }
#endif
    }

    private void WritePrefix(long utcTicks, LogLevel level, int threadId)
    {
        Span<byte> dst = _writeBuffer.AsSpan(_writePos);
        int pos = 0;

        var localTime = new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime();
        dst[pos++] = (byte)'[';
        WriteTwoDigits(dst, ref pos, localTime.Hour);
        dst[pos++] = (byte)':';
        WriteTwoDigits(dst, ref pos, localTime.Minute);
        dst[pos++] = (byte)':';
        WriteTwoDigits(dst, ref pos, localTime.Second);
        dst[pos++] = (byte)'.';
        WriteThreeDigits(dst, ref pos, localTime.Millisecond);

        "] ["u8.CopyTo(dst[pos..]);
        pos += 3;

        GetLevelBytes(level).CopyTo(dst[pos..]);
        pos += 3;

        "] [T"u8.CopyTo(dst[pos..]);
        pos += 4;

        if (threadId < 0)
        {
            dst[pos++] = (byte)'*';
            dst[pos++] = (byte)'*';
        }
        else if (threadId < 10)
        {
            dst[pos++] = (byte)'0';
            dst[pos++] = (byte)('0' + threadId);
        }
        else if (Utf8Formatter.TryFormat(threadId, dst[pos..], out int tidLen))
        {
            pos += tidLen;
        }

        dst[pos++] = (byte)']';
        dst[pos++] = (byte)' ';

        _writePos += pos;
    }

    private void WriteBytesChunked(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            int available = _writeBuffer.Length - _writePos;
            if (available == 0)
            {
                FlushWriteBufferToFile();
                available = _writeBuffer.Length;
            }

            int chunk = Math.Min(available, bytes.Length);
            bytes[..chunk].CopyTo(_writeBuffer.AsSpan(_writePos, chunk));
            _writePos += chunk;
            bytes = bytes[chunk..];
        }
    }

    private void WriteUtf16Chunked(ReadOnlySpan<char> chars)
    {
        while (!chars.IsEmpty)
        {
            int available = _writeBuffer.Length - _writePos;
            if (available < 8)
            {
                FlushWriteBufferToFile();
                available = _writeBuffer.Length;
            }

            var status = Utf8.FromUtf16(
                chars,
                _writeBuffer.AsSpan(_writePos, available),
                out int charsRead,
                out int bytesWritten,
                replaceInvalidSequences: true,
                isFinalBlock: true);

            _writePos += bytesWritten;
            chars = chars[charsRead..];

            if (status == OperationStatus.DestinationTooSmall)
            {
                FlushWriteBufferToFile();
            }
            else if (status != OperationStatus.Done && charsRead == 0)
            {
                break;
            }
        }
    }

    private void EnsureWriteCapacity(int required)
    {
        if (_writeBuffer.Length - _writePos < required)
        {
            FlushWriteBufferToFile();
        }
    }

    private void FlushWriteBufferToFile()
    {
        if (_writePos == 0)
        {
            return;
        }

        _fileStream.Write(_writeBuffer.AsSpan(0, _writePos));
        _bytesWrittenInCurrentFile += _writePos;
        _writePos = 0;

#if DEBUG
        _fileFlushCount++;
#endif
    }

    private void RotateFileIfNeeded()
    {
        if (_bytesWrittenInCurrentFile + _writePos < _maxFileSizeBytes)
        {
            return;
        }

        FlushWriteBufferToFile();
        _fileStream.Flush();
        _fileStream.Dispose();

        (_currentLogFile, _fileStream) = CreateUniqueLogFileStream(_logDirectory);
        _bytesWrittenInCurrentFile = 0;
        PruneOldLogFiles();
    }

    private void PruneOldLogFiles()
    {
        try
        {
            string activePath = _currentLogFile;
            var dirInfo = new DirectoryInfo(_logDirectory);
            if (!dirInfo.Exists)
            {
                return;
            }

            var files = dirInfo.GetFiles("log_*.txt");
            if (files.Length <= _maxRetainedFiles)
            {
                return;
            }

            Array.Sort(files, static (a, b) =>
            {
                int cmp = a.CreationTimeUtc.CompareTo(b.CreationTimeUtc);
                if (cmp != 0)
                {
                    return cmp;
                }

                return StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            });

            int toDelete = files.Length - _maxRetainedFiles;
            for (int i = 0; i < files.Length && toDelete > 0; i++)
            {
                if (string.Equals(files[i].FullName, activePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    files[i].Delete();
                    toDelete--;
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private static (string Path, FileStream Stream) CreateUniqueLogFileStream(string directory)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);

        for (int attempt = 0; attempt < 1000; attempt++)
        {
            string fileName = attempt == 0
                ? $"log_{timestamp}.txt"
                : $"log_{timestamp}_{attempt:D3}.txt";

            string candidate = Path.Combine(directory, fileName);
            try
            {
                var fs = new FileStream(
                    candidate,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.None);
                return (candidate, fs);
            }
            catch (IOException)
            {
            }
        }

        string fallback = Path.Combine(directory, $"log_{timestamp}_{Guid.NewGuid():N}.txt");
        var fallbackStream = new FileStream(
            fallback,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.None);
        return (fallback, fallbackStream);
    }

#if DEBUG
    private void WriteLineToConsole(LogLevel level, ReadOnlySpan<byte> utf8Line)
    {
        try
        {
            if (!_useAnsiConsole)
            {
                _consoleStream.Write(utf8Line);
                return;
            }

            ReadOnlySpan<byte> prefix = GetAnsiColorBytes(level);
            ReadOnlySpan<byte> reset = "\x1b[0m"u8;

            int total = prefix.Length + utf8Line.Length + reset.Length;
            if (total > _consoleBuffer.Length)
            {
                _consoleStream.Write(utf8Line);
                return;
            }

            prefix.CopyTo(_consoleBuffer);
            utf8Line.CopyTo(_consoleBuffer.AsSpan(prefix.Length));
            reset.CopyTo(_consoleBuffer.AsSpan(prefix.Length + utf8Line.Length));
            _consoleStream.Write(_consoleBuffer.AsSpan(0, total));
        }
        catch
        {
        }
    }

    private static ReadOnlySpan<byte> GetAnsiColorBytes(LogLevel level) => level switch
    {
        LogLevel.Trace => "\x1b[90m"u8,
        LogLevel.Debug => "\x1b[37m"u8,
        LogLevel.Info => "\x1b[97m"u8,
        LogLevel.Warning => "\x1b[33m"u8,
        LogLevel.Error => "\x1b[91m"u8,
        LogLevel.Fatal => "\x1b[95m"u8,
        _ => "\x1b[97m"u8
    };

    private static bool TryEnableAnsiConsole()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        return TryEnableWindowsVirtualTerminal();
    }

    [SupportedOSPlatform("windows")]
    private static bool TryEnableWindowsVirtualTerminal()
    {
        const int StdOutputHandle = -11;
        const uint EnableVirtualTerminalProcessing = 0x0004;

        nint handle = GetStdHandle(StdOutputHandle);
        if (handle == nint.Zero || handle == new nint(-1))
        {
            return false;
        }

        if (GetConsoleMode(handle, out uint mode) == 0)
        {
            return false;
        }

        if ((mode & EnableVirtualTerminalProcessing) != 0)
        {
            return true;
        }

        return SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing) != 0;
    }

    [LibraryImport("kernel32.dll")]
    [SupportedOSPlatform("windows")]
    private static partial nint GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll")]
    [SupportedOSPlatform("windows")]
    private static partial int GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [LibraryImport("kernel32.dll")]
    [SupportedOSPlatform("windows")]
    private static partial int SetConsoleMode(nint hConsoleHandle, uint dwMode);
#endif

    private static void WriteTwoDigits(Span<byte> dst, ref int pos, int value)
    {
        dst[pos++] = (byte)('0' + (value / 10));
        dst[pos++] = (byte)('0' + (value % 10));
    }

    private static void WriteThreeDigits(Span<byte> dst, ref int pos, int value)
    {
        dst[pos++] = (byte)('0' + (value / 100));
        dst[pos++] = (byte)('0' + (value / 10 % 10));
        dst[pos++] = (byte)('0' + (value % 10));
    }

    private static ReadOnlySpan<byte> GetLevelBytes(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC"u8,
        LogLevel.Debug => "DBG"u8,
        LogLevel.Info => "INF"u8,
        LogLevel.Warning => "WRN"u8,
        LogLevel.Error => "ERR"u8,
        LogLevel.Fatal => "FTL"u8,
        _ => "UNK"u8
    };

    /// <summary>
    /// Asynchronous resource disposal. Waits for all pending messages to be written.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Synchronous disposal. Drains and flushes all pending logs before closing the file.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        lock (_queueLock)
        {
            _stopRequested = true;
            _workerAsleep = false;
        }

        _urgentSignal.Set();
        _wakeSignal.Set();

        _workerThread.Join();

        _wakeSignal.Dispose();
        _urgentSignal.Dispose();
        _flushEvent.Dispose();
        _flushGate.Dispose();
    }

    private struct LogSlot
    {
        public long TimestampUtcTicks;
        public LogLevel Level;
        public int ThreadId;
        public int ByteOffset;
        public int ByteLength;
        public bool HasBytes;
        public string? LiteralMessage;
        public Exception? Exception;
    }
}