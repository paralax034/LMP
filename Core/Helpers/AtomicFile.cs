using System.Text;

namespace LMP.Core.Helpers;

/// <summary>
/// Provides fault-tolerant atomic file read and write operations with protection against data corruption
/// during process crashes or power outages (Torn Write Protection).
/// </summary>
public static class AtomicFile
{
    private const int DefaultBufferSize = 4096;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    #region Write Synchronous

    /// <summary>
    /// Atomically writes string content to a file using UTF-8 encoding.
    /// </summary>
    /// <param name="targetPath">Target file path.</param>
    /// <param name="content">String data to write.</param>
    /// <param name="createBackup">Flag indicating whether to create a backup copy (<c>.bak</c>) before overwriting.</param>
    /// <param name="encoding">Text encoding (defaults to UTF-8 without BOM).</param>
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
    /// Atomically writes a byte array to the target file with a forced buffer flush to disk.
    /// </summary>
    /// <param name="targetPath">Target file path.</param>
    /// <param name="bytes">Binary data.</param>
    /// <param name="createBackup">Flag indicating whether to create a backup copy (<c>.bak</c>).</param>
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
    /// Reads binary data from the target file. If it is corrupted, zero-sized, or missing,
    /// automatically reads the backup copy (<c>.bak</c>) and restores the main file.
    /// </summary>
    /// <param name="targetPath">Path to the target file.</param>
    /// <param name="recoveredFromBackup">Returns <c>true</c> if data was successfully recovered from <c>.bak</c>.</param>
    /// <returns>An array of read bytes, or <c>null</c> if neither the main file nor the backup could be read.</returns>
    /// <remarks>
    /// Provides hardware-level protection against data corruption (Torn Write Protection) for binary MemoryPack structures.
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
    /// Asynchronously and atomically writes string content to a file.
    /// </summary>
    /// <param name="targetPath">Target file path.</param>
    /// <param name="content">String data to write.</param>
    /// <param name="createBackup">Flag indicating whether to create a backup copy (<c>.bak</c>).</param>
    /// <param name="encoding">Text encoding (defaults to UTF-8 without BOM).</param>
    /// <param name="ct">Cancellation token for the operation.</param>
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
    /// Asynchronously and atomically writes binary data to a file.
    /// </summary>
    /// <param name="targetPath">Target file path.</param>
    /// <param name="bytes">Binary data to write.</param>
    /// <param name="createBackup">Flag indicating whether to create a backup copy (<c>.bak</c>).</param>
    /// <param name="ct">Cancellation token for the operation.</param>
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
    /// Asynchronously reads binary data from the target file with automatic recovery from <c>.bak</c> on failure.
    /// </summary>
    /// <param name="targetPath">Path to the target file.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>A tuple containing the read byte array and a flag indicating whether recovery from backup occurred.</returns>
    /// <remarks>
    /// Provides hardware-level protection against data corruption (Torn Write Protection) for binary MemoryPack structures.
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
    /// Reads text from the target file. If it is corrupted, zero-sized, or missing,
    /// automatically reads the backup copy (<c>.bak</c>) and restores the main file.
    /// </summary>
    /// <param name="targetPath">Path to the file.</param>
    /// <param name="recoveredFromBackup">Returns <c>true</c> if data was recovered from <c>.bak</c>.</param>
    /// <param name="encoding">Text encoding.</param>
    /// <returns>File content, or <c>null</c> if neither the main file nor the backup could be read.</returns>
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
    /// Asynchronously reads text from a file with automatic recovery from <c>.bak</c> on failure.
    /// </summary>
    /// <param name="targetPath">Path to the file.</param>
    /// <param name="encoding">Text encoding.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A tuple containing the read content and a flag indicating recovery from backup.</returns>
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