using System.Globalization;
using System.Text;
using LMP.Core.Logger;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Unit tests for the UTF-8 logging pipeline: zero heap allocations on disabled and enabled paths,
/// consecutive duplicate collapsing, file retention rotation, and concurrent flush/dispose safety.
/// </summary>
public static class LoggerTests
{
    [TestMethod(TestCategory.Unit, "Logger UTF-8 Interpolation & Enqueue (0 Alloc)", Group = TestGroups.System, Order = 50)]
    public static Task TestLoggerZeroAllocUtf8PathAsync()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using var processor = new AsyncLogProcessor(tempDir);
#if DEBUG
            processor.SuppressConsoleOutput = true;
#endif

            // 1. Disabled level short-circuit: 0 B
            LogLevel prevMin = Log.MinLevel;
            try
            {
                Log.MinLevel = LogLevel.Info;
                int n = 42;
                double d = 12.345;
                TestAssert.ZeroAlloc(() =>
                {
                    Log.Trace($"Disabled trace n={n}, d={d:F2}, text={"sample"}");
                    Log.Debug($"Disabled debug n={n}, d={d:F2}, text={"sample"}");
                }, warmup: 10, iterations: 1000, context: "Log.Trace/Debug disabled short-circuit");
            }
            finally
            {
                Log.MinLevel = prevMin;
            }

            // 2. Enabled UTF-8 handler + ring-buffer enqueue: 0 B on caller thread
            processor.Flush();
            int seq = 0;
            TimeSpan span = TimeSpan.FromMilliseconds(125);
            TestAssert.ZeroAlloc(() =>
            {
                int current = seq++;
                ReadOnlySpan<char> slice = "opus-stream";
                var handler = new Log.LogInterpolatedStringHandlerBase(
                    literalLength: 28,
                    formattedCount: 4,
                    forceEnabled: true,
                    out bool isEnabled);

                try
                {
                    if (isEnabled)
                    {
                        handler.AppendLiteral("Item=");
                        handler.AppendFormatted(current, -6);
                        handler.AppendLiteral(" dur=");
                        handler.AppendFormatted(span);
                        handler.AppendLiteral(" codec=");
                        handler.AppendFormatted(slice, -12);
                        handler.AppendLiteral(" rate=");
                        handler.AppendFormatted(48.0, 6, "F1");
                        processor.EnqueueUtf8(LogLevel.Info, handler.WrittenSpan);
                    }
                }
                finally
                {
                    handler.Dispose();
                }
            }, warmup: 50, iterations: 200, context: "Logger UTF-8 enabled path");

            // 3. Spill from 1 KB thread buffer to ArrayPool preserves payload intact
            string longPayload = new('Z', 1500);
            var spillHandler = new Log.LogInterpolatedStringHandlerBase(
                literalLength: 6,
                formattedCount: 1,
                forceEnabled: true,
                out bool spillEnabled);

            try
            {
                TestAssert.True(spillEnabled);
                spillHandler.AppendLiteral("LONG=");
                spillHandler.AppendFormatted(longPayload);
                processor.EnqueueUtf8(LogLevel.Info, spillHandler.WrittenSpan);
            }
            finally
            {
                spillHandler.Dispose();
            }

            processor.Flush();
            string written = ReadAllTextShared(processor.LogFilePath);
            TestAssert.True(written.Contains(longPayload, StringComparison.Ordinal),
                "Spill from 1KB thread buffer to ArrayPool must preserve entire payload");
        }
        finally
        {
            CleanupDirectory(tempDir);
        }

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Logger Consecutive Deduplication Preserves Chronology", Group = TestGroups.System, Order = 51)]
    public static Task TestConsecutiveDeduplicationAsync()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string logPath;
            using (var processor = new AsyncLogProcessor(tempDir))
            {
#if DEBUG
                processor.SuppressConsoleOutput = true;
#endif
                logPath = processor.LogFilePath;

                // Two identical messages at different timestamps -> collapsed with repeated 1x
                processor.EnqueueUtf8(LogLevel.Error, "Socket timeout on CDN node"u8);
                Thread.Sleep(5);
                processor.EnqueueUtf8(LogLevel.Error, "Socket timeout on CDN node"u8);

                // Intervening message B -> breaks consecutive run, then A again -> A is NOT merged across B
                processor.EnqueueUtf8(LogLevel.Warning, "Switching fallback host"u8);
                processor.EnqueueUtf8(LogLevel.Error, "Socket timeout on CDN node"u8);

                // Trailing repeats flushed explicitly
                processor.EnqueueUtf8(LogLevel.Error, "Socket timeout on CDN node"u8);
                processor.EnqueueUtf8(LogLevel.Error, "Socket timeout on CDN node"u8);
                processor.Flush();
            }

            string[] lines = File.ReadAllLines(logPath, Encoding.UTF8);
            TestAssert.Equal(5, lines.Length, $"Expected 5 lines (A, rep 1x, B, A, rep 2x), got {lines.Length}");
            TestAssert.True(
                lines[0].Contains("[ERR] [T", StringComparison.Ordinal) &&
                lines[0].Contains("Socket timeout on CDN node", StringComparison.Ordinal));
            TestAssert.True(lines[1].Contains("repeated 1x", StringComparison.Ordinal),
                $"Expected repeated 1x summary, got '{lines[1]}'");
            TestAssert.True(
                lines[2].Contains("[WRN] [T", StringComparison.Ordinal) &&
                lines[2].Contains("Switching fallback host", StringComparison.Ordinal));
            TestAssert.True(lines[3].Contains("Socket timeout on CDN node", StringComparison.Ordinal));
            TestAssert.True(lines[4].Contains("repeated 2x", StringComparison.Ordinal),
                $"Expected trailing repeated 2x summary on Flush, got '{lines[4]}'");
        }
        finally
        {
            CleanupDirectory(tempDir);
        }

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Logger Size Rotation & Retention Pruning", Group = TestGroups.System, Order = 52)]
    public static Task TestSizeRotationAndRetentionPruningAsync()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            // Seed 4 old log files to verify startup retention pruning
            for (int i = 0; i < 4; i++)
            {
                string oldFile = Path.Combine(tempDir, $"log_2025-01-0{i + 1}_00-00-00.txt");
                File.WriteAllText(oldFile, "old");
                File.SetCreationTimeUtc(oldFile, DateTime.UtcNow.AddDays(-10 + i));
            }

            using (var processor = new AsyncLogProcessor(tempDir, maxRetainedFiles: 3, maxFileSizeBytes: 512))
            {
#if DEBUG
                processor.SuppressConsoleOutput = true;
#endif
                for (int i = 0; i < 40; i++)
                {
                    processor.EnqueueString(
                        LogLevel.Info,
                        $"Rotation payload line #{i.ToString("D3", CultureInfo.InvariantCulture)} — padding to trigger rotation");
                }

                processor.Flush();
            }

            string[] files = Directory.GetFiles(tempDir, "log_*.txt");
            TestAssert.True(files.Length <= 3, $"Expected at most 3 retained log files, found {files.Length}");
            TestAssert.True(files.Length >= 2, $"Expected size rotation to produce >= 2 files, found {files.Length}");
        }
        finally
        {
            CleanupDirectory(tempDir);
        }

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Logger Concurrent Producers & Dispose Safety", Group = TestGroups.System, Order = 53)]
    public static async Task TestConcurrentLoggingAndFlushDisposeAsync()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string logPath;
            using (var processor = new AsyncLogProcessor(tempDir))
            {
#if DEBUG
                processor.SuppressConsoleOutput = true;
#endif
                logPath = processor.LogFilePath;

                Task[] writers = new Task[4];
                for (int w = 0; w < writers.Length; w++)
                {
                    int workerId = w;
                    writers[w] = Task.Run(() =>
                    {
                        for (int i = 0; i < 100; i++)
                        {
                            processor.EnqueueString(LogLevel.Info, $"worker={workerId} seq={i} кириллица");
                        }
                    });
                }

                await Task.WhenAll(writers).ConfigureAwait(false);
                processor.Flush();
            }

            string content = File.ReadAllText(logPath, Encoding.UTF8);
            TestAssert.True(content.Contains("worker=0 seq=99 кириллица", StringComparison.Ordinal));
            TestAssert.True(content.Contains("worker=3 seq=99 кириллица", StringComparison.Ordinal));
        }
        finally
        {
            CleanupDirectory(tempDir);
        }
    }

    private static string ReadAllTextShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string CreateTempDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "LMP_LoggerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CleanupDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
        }
    }
}