using System.Text;

namespace LMP.Core.Helpers;

/// <summary>
/// Обеспечивает отказоустойчивые атомарные операции записи и чтения файлов с защитой от повреждения данных
/// при аварийном завершении процесса или отключении питания (Torn Write Protection).
/// </summary>
public static class AtomicFile
{
    private const int DefaultBufferSize = 4096;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    #region Write Synchronous

    /// <summary>
    /// Атомарно записывает строковый контент в файл в кодировке UTF-8.
    /// </summary>
    /// <param name="targetPath">Целевой путь к файлу.</param>
    /// <param name="content">Строковые данные для записи.</param>
    /// <param name="createBackup">Флаг создания резервной копии (<c>.bak</c>) перед перезаписью.</param>
    /// <param name="encoding">Кодировка текста (по умолчанию UTF-8 без BOM).</param>
    public static void WriteText(
        string targetPath,
        string content,
        bool createBackup = false,
        Encoding? encoding = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(content);

        var enc = encoding ?? Utf8NoBom;
        var bytes = enc.GetBytes(content);
        WriteBytes(targetPath, bytes, createBackup);
    }

    /// <summary>
    /// Атомарно записывает массив байт в целевой файл с принудительным сбросом буфера на диск.
    /// </summary>
    /// <param name="targetPath">Целевой путь к файлу.</param>
    /// <param name="bytes">Бинарные данные.</param>
    /// <param name="createBackup">Флаг создания резервной копии (<c>.bak</c>).</param>
    public static void WriteBytes(
        string targetPath,
        ReadOnlySpan<byte> bytes,
        bool createBackup = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        EnsureDirectoryExists(targetPath);

        var tempPath = string.Concat(targetPath, ".tmp");

        try
        {
            using (var fs = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                DefaultBufferSize,
                FileOptions.WriteThrough))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }

            CommitTempFile(tempPath, targetPath, createBackup);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Считывает бинарные данные из целевого файла. При его повреждении, нулевом размере или отсутствии
    /// автоматически считывает резервную копию (<c>.bak</c>) и восстанавливает основной файл.
    /// </summary>
    /// <param name="targetPath">Путь к целевому файлу.</param>
    /// <param name="recoveredFromBackup">Возвращает <c>true</c>, если данные были успешно восстановлены из <c>.bak</c>.</param>
    /// <returns>Массив прочитанных байт или <c>null</c>, если ни основной файл, ни бэкап прочитать не удалось.</returns>
    /// <remarks>
    /// Обеспечивает аппаратную защиту от повреждения данных (Torn Write Protection) для бинарных MemoryPack-структур.
    /// </remarks>
    public static byte[]? ReadBytesWithFallback(
        string targetPath,
        out bool recoveredFromBackup) =>
        ReadWithFallbackCore(
            targetPath,
            static path => File.ReadAllBytes(path),
            static bytes => bytes.Length > 0,
            out recoveredFromBackup);

    #endregion

    #region Write Asynchronous

    /// <summary>
    /// Асинхронно и атомарно записывает строковый контент в файл.
    /// </summary>
    /// <param name="targetPath">Целевой путь к файлу.</param>
    /// <param name="content">Строковые данные для записи.</param>
    /// <param name="createBackup">Флаг создания резервной копии (<c>.bak</c>).</param>
    /// <param name="encoding">Кодировка текста (по умолчанию UTF-8 без BOM).</param>
    /// <param name="ct">Токен отмены операции.</param>
    public static async Task WriteTextAsync(
        string targetPath,
        string content,
        bool createBackup = false,
        Encoding? encoding = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(content);

        var enc = encoding ?? Utf8NoBom;
        var bytes = enc.GetBytes(content);
        await WriteBytesAsync(targetPath, bytes, createBackup, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Асинхронно и атомарно записывает бинарные данные в файл.
    /// </summary>
    /// <param name="targetPath">Целевой путь к файлу.</param>
    /// <param name="bytes">Бинарные данные для записи.</param>
    /// <param name="createBackup">Флаг создания резервной копии (<c>.bak</c>).</param>
    /// <param name="ct">Токен отмены операции.</param>
    public static async Task WriteBytesAsync(
        string targetPath,
        ReadOnlyMemory<byte> bytes,
        bool createBackup = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        EnsureDirectoryExists(targetPath);

        var tempPath = string.Concat(targetPath, ".tmp");

        try
        {
            await using (var fs = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                DefaultBufferSize,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await fs.WriteAsync(bytes, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }

            CommitTempFile(tempPath, targetPath, createBackup);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Асинхронно считывает бинарные данные из целевого файла с автоматическим восстановлением из <c>.bak</c> при сбое.
    /// </summary>
    /// <param name="targetPath">Путь к целевому файлу.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Кортеж из массива прочитанных байт и признака восстановления из резервной копии.</returns>
    /// <remarks>
    /// Обеспечивает аппаратную защиту от повреждения данных (Torn Write Protection) для бинарных MemoryPack-структур.
    /// </remarks>
    public static Task<(byte[]? Content, bool RecoveredFromBackup)> ReadBytesWithFallbackAsync(
        string targetPath,
        CancellationToken ct = default) =>
        ReadWithFallbackCoreAsync(
            targetPath,
            static (path, token) => File.ReadAllBytesAsync(path, token),
            static bytes => bytes.Length > 0,
            ct);

    #endregion

    #region Read with Fallback Synchronous

    /// <summary>
    /// Считывает текст из целевого файла. При его повреждении, нулевом размере или отсутствии
    /// автоматически считывает резервную копию (<c>.bak</c>) и восстанавливает основной файл.
    /// </summary>
    /// <param name="targetPath">Путь к файлу.</param>
    /// <param name="recoveredFromBackup">Возвращает <c>true</c>, если данные были восстановлены из <c>.bak</c>.</param>
    /// <param name="encoding">Кодировка текста.</param>
    /// <returns>Содержимое файла или <c>null</c>, если ни основной файл, ни бэкап прочитать не удалось.</returns>
    public static string? ReadTextWithFallback(
        string targetPath,
        out bool recoveredFromBackup,
        Encoding? encoding = null)
    {
        var enc = encoding ?? Utf8NoBom;
        return ReadWithFallbackCore(
            targetPath,
            path => File.ReadAllText(path, enc),
            static text => !string.IsNullOrWhiteSpace(text),
            out recoveredFromBackup);
    }

    #endregion

    #region Read with Fallback Asynchronous

    /// <summary>
    /// Асинхронно считывает текст из файла с автоматическим восстановлением из <c>.bak</c> при сбое.
    /// </summary>
    /// <param name="targetPath">Путь к файлу.</param>
    /// <param name="encoding">Кодировка текста.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Кортеж из прочитанного содержимого и флага восстановления из бэкапа.</returns>
    public static Task<(string? Content, bool RecoveredFromBackup)> ReadTextWithFallbackAsync(
        string targetPath,
        Encoding? encoding = null,
        CancellationToken ct = default)
    {
        var enc = encoding ?? Utf8NoBom;
        return ReadWithFallbackCoreAsync(
            targetPath,
            (path, token) => File.ReadAllTextAsync(path, enc, token),
            static text => !string.IsNullOrWhiteSpace(text),
            ct);
    }

    #endregion

    #region Helpers

    private static void CommitTempFile(string tempPath, string targetPath, bool createBackup)
    {
        if (createBackup && File.Exists(targetPath))
        {
            var backupPath = string.Concat(targetPath, ".bak");
            try
            {
                File.Copy(targetPath, backupPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Warn($"[AtomicFile] Failed to create backup copy for '{targetPath}': {ex.Message}");
            }
        }

        File.Move(tempPath, targetPath, overwrite: true);
    }

    private static T? ReadWithFallbackCore<T>(
        string targetPath,
        Func<string, T> reader,
        Func<T, bool> validator,
        out bool recoveredFromBackup) where T : class
    {
        recoveredFromBackup = false;
        if (string.IsNullOrWhiteSpace(targetPath)) return null;

        if (HasNonEmptyFile(targetPath))
        {
            try
            {
                var content = reader(targetPath);
                if (validator(content))
                    return content;
            }
            catch (Exception ex)
            {
                Log.Warn($"[AtomicFile] Primary file read failed for '{targetPath}': {ex.Message}");
            }
        }

        var backupPath = string.Concat(targetPath, ".bak");
        if (HasNonEmptyFile(backupPath))
        {
            try
            {
                var backupContent = reader(backupPath);
                if (validator(backupContent))
                {
                    recoveredFromBackup = true;
                    TryAutoHeal(backupPath, targetPath);
                    Log.Warn($"[AtomicFile] ⚠️ Successfully recovered '{targetPath}' from backup (.bak)");
                    return backupContent;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[AtomicFile] Backup file read failed for '{backupPath}': {ex.Message}");
            }
        }

        return null;
    }

    private static async Task<(T? Content, bool RecoveredFromBackup)> ReadWithFallbackCoreAsync<T>(
        string targetPath,
        Func<string, CancellationToken, Task<T>> readerAsync,
        Func<T, bool> validator,
        CancellationToken ct) where T : class
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return (null, false);

        if (HasNonEmptyFile(targetPath))
        {
            try
            {
                var content = await readerAsync(targetPath, ct).ConfigureAwait(false);
                if (validator(content))
                    return (content, false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Log.Warn($"[AtomicFile] Primary file read failed for '{targetPath}': {ex.Message}");
            }
        }

        var backupPath = string.Concat(targetPath, ".bak");
        if (HasNonEmptyFile(backupPath))
        {
            try
            {
                var backupContent = await readerAsync(backupPath, ct).ConfigureAwait(false);
                if (validator(backupContent))
                {
                    TryAutoHeal(backupPath, targetPath);
                    Log.Warn($"[AtomicFile] ⚠️ Successfully recovered '{targetPath}' from backup (.bak)");
                    return (backupContent, true);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Log.Error($"[AtomicFile] Backup file read failed for '{backupPath}': {ex.Message}");
            }
        }

        return (null, false);
    }

    private static bool HasNonEmptyFile(string path) =>
        File.Exists(path) && new FileInfo(path).Length > 0;

    private static void EnsureDirectoryExists(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    private static void TryAutoHeal(string sourceBackupPath, string targetPath)
    {
        try
        {
            File.Copy(sourceBackupPath, targetPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"[AtomicFile] Auto-heal copy failed for '{targetPath}': {ex.Message}");
        }
    }

    #endregion
}