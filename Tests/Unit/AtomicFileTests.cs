using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Модульные тесты механизмов атомарной записи и восстановления файлов AtomicFile.
/// Проверяют текстовый и бинарный roundtrip, ротацию версий N-1 (.bak) и auto-heal при сбоях.
/// </summary>
public static class AtomicFileTests
{
    [TestMethod(TestCategory.Unit, "Sync Text Atomic Roundtrip", Group = TestGroups.Storage, Order = 1)]
    public static Task TestSyncRoundtripAsync()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var path = Path.Combine(tempDir, "test_sync.json");
            const string payload = "{\"test\": 12345, \"utf8\": \"тест\"}";

            AtomicFile.WriteText(path, payload, createBackup: true);

            var read = AtomicFile.ReadTextWithFallback(path, out bool recovered);

            TestAssert.Equal(payload, read, "Payload mismatch on primary file read");
            TestAssert.False(recovered, "Expected recovered == false on normal read");
            TestAssert.True(File.Exists(path), "Target file does not exist on disk");
        }
        finally
        {
            CleanupDirectory(tempDir);
        }

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Async Text Atomic Roundtrip", Group = TestGroups.Storage, Order = 2)]
    public static async Task TestAsyncRoundtripAsync()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var path = Path.Combine(tempDir, "test_async.json");
            const string payload = "{\"async\": true, \"symbols\": \"♫♪\"}";

            await AtomicFile.WriteTextAsync(path, payload, createBackup: true).ConfigureAwait(false);

            var (read, recovered) = await AtomicFile.ReadTextWithFallbackAsync(path).ConfigureAwait(false);

            TestAssert.Equal(payload, read, "Async payload mismatch");
            TestAssert.False(recovered, "Expected recovered == false on async read");
        }
        finally
        {
            CleanupDirectory(tempDir);
        }
    }

    [TestMethod(TestCategory.Unit, "Backup (.bak) Rotation on Overwrite", Group = TestGroups.Storage, Order = 3)]
    public static Task TestBackupRotationAsync()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var path = Path.Combine(tempDir, "backup_test.json");
            var backupPath = string.Concat(path, ".bak");
            const string v1 = "Version 1 content";
            const string v2 = "Version 2 content";

            AtomicFile.WriteText(path, v1, createBackup: false);
            TestAssert.False(File.Exists(backupPath), "Backup file should not exist on first write");

            AtomicFile.WriteText(path, v2, createBackup: true);

            TestAssert.True(File.Exists(backupPath), "Backup file was not created on second write");
            TestAssert.Equal(v2, File.ReadAllText(path));
            TestAssert.Equal(v1, File.ReadAllText(backupPath));
        }
        finally
        {
            CleanupDirectory(tempDir);
        }

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Text Auto-Heal from Corrupted Primary", Group = TestGroups.Storage, Order = 4)]
    public static Task TestTextCorruptionRecoveryAsync()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var path = Path.Combine(tempDir, "corrupt_test.json");
            const string validPayload = "{\"valid\": true}";

            AtomicFile.WriteText(path, validPayload, createBackup: false);
            AtomicFile.WriteText(path, validPayload, createBackup: true);

            // Имитируем сбой: обнуляем основной файл (0 байт)
            File.WriteAllText(path, string.Empty);

            var result = AtomicFile.ReadTextWithFallback(path, out bool recovered);

            TestAssert.Equal(validPayload, result, "Failed to recover text payload from backup");
            TestAssert.True(recovered, "Expected recovered flag to be true");
            TestAssert.True(new FileInfo(path).Length > 0, "Auto-heal failed to repair primary file");
            TestAssert.Equal(validPayload, File.ReadAllText(path));
        }
        finally
        {
            CleanupDirectory(tempDir);
        }

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Binary Auto-Heal from Backup on Zero-Length File", Group = TestGroups.Storage, Order = 5)]
    public static Task TestBinaryBakRecoveryAndAutoHealAsync()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var path = Path.Combine(tempDir, "binary_atomic.bin");
            var backupPath = string.Concat(path, ".bak");

            byte[] payloadV1 = [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02];
            byte[] payloadV2 = [0xAA, 0xBB, 0xCC, 0xDD];

            AtomicFile.WriteBytes(path, payloadV1, createBackup: false);
            AtomicFile.WriteBytes(path, payloadV2, createBackup: true);

            // Имитируем повреждение целевого файла до 0 байт
            File.WriteAllBytes(path, []);

            var recoveredBytes = AtomicFile.ReadBytesWithFallback(path, out bool recovered);

            TestAssert.True(recovered, "Expected recovered == true on binary fallback");
            TestAssert.NotNull(recoveredBytes);
            TestAssert.True(recoveredBytes.AsSpan().SequenceEqual(payloadV1), "Recovered payload mismatch");

            // Проверяем факт починки основного файла
            var healedBytes = File.ReadAllBytes(path);
            TestAssert.True(healedBytes.AsSpan().SequenceEqual(payloadV1), "Primary file was not auto-healed");
        }
        finally
        {
            CleanupDirectory(tempDir);
        }

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Temp File Cleanup on Cancellation", Group = TestGroups.Storage, Order = 6)]
    public static async Task TestCancellationCleansTempAsync()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var path = Path.Combine(tempDir, "cancelled.json");
            var tempPath = string.Concat(path, ".tmp");

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            bool threw = false;
            try
            {
                await AtomicFile.WriteTextAsync(path, "cancelled content", createBackup: false, ct: cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                threw = true;
            }

            TestAssert.True(threw, "OperationCanceledException was not thrown");
            TestAssert.False(File.Exists(tempPath), "Temp file was not cleaned up on cancellation");
            TestAssert.False(File.Exists(path), "Target file should not exist on cancellation");
        }
        finally
        {
            CleanupDirectory(tempDir);
        }
    }

    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LMP_AtomicFileTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CleanupDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch { }
    }
}