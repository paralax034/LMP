using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace LMP.Core.Services;

/// <summary>
/// Результат проверки наличия обновлений.
/// </summary>
public sealed record UpdateCheckResult(
    bool IsSuccess,
    bool HasUpdate,
    int RemoteCommitCount,
    string VersionName,
    string? ReleaseNotes,
    GitHubAssetDto? Asset,
    string? ErrorMessage);

/// <summary>
/// Сервис проверки, загрузки и бесшовного применения обновлений приложения.
/// Все сетевые операции выполняются через централизованный <see cref="NetworkManager"/>.
/// </summary>
public sealed partial class UpdateService : IDisposable
{
    [GeneratedRegex(@"\|\s*Commit Number\s*\|\s*`?(\d+)`?", RegexOptions.IgnoreCase)]
    private static partial Regex CommitNumberRegex();

    [GeneratedRegex(@"dev-(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SnapshotTagRegex();

    private readonly LibraryService _library;
    private readonly NetworkManager _networkManager;
    private bool _disposed;

#if DEBUG
    /// <summary>
    /// Эмуляция локального номера коммита для отладки обновления в Debug-сборке.
    /// </summary>
    public static int DebugSimulatedCommitCount { get; set; }
#endif

    public UpdateService(LibraryService library, NetworkManager networkManager)
    {
        _library = library;
        _networkManager = networkManager;

        Log.Debug($"[UpdateService] Initialized via NetworkManager. Target repo: '{G.RepoSlug}', Current commit: #{G.Build.CommitCount}");
    }

    /// <summary>
    /// Получает метаданные репозитория GitHub (звёзды, форки, задачи) через централизованный сетевой стек.
    /// </summary>
    public async Task<GitHubRepoInfoDto?> GetRepositoryInfoAsync(CancellationToken ct = default)
    {
        try
        {
            string url = $"https://api.github.com/repos/{G.RepoSlug}";
            using var request = CreateGitHubRequest(HttpMethod.Get, url);
            using var response = await _networkManager.UpdateClient.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync(
                stream,
                AppJsonContext.Default.GitHubRepoInfoDto,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug($"[UpdateService] Failed to load repo info: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Выполняет проверку обновлений через GitHub Releases API с поддержкой rolling-релиза 'dev' и снимков.
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdatesAsync(bool manual = false, CancellationToken ct = default)
    {
        try
        {
            var settings = _library.Settings.Updates;
            Log.Info($"[UpdateService] Starting update check (manual={manual}, autoCheck={settings.AutoCheckUpdates}, repo='{G.RepoSlug}')...");

            if (!manual)
            {
                if (!settings.AutoCheckUpdates)
                {
                    Log.Debug("[UpdateService] Auto-check is disabled in settings. Skipping.");
                    return new UpdateCheckResult(true, false, 0, string.Empty, null, null, null);
                }

                if (settings.LastUpdateCheckUtc.HasValue)
                {
                    var elapsed = DateTime.UtcNow - settings.LastUpdateCheckUtc.Value;
                    if (elapsed < TimeSpan.FromHours(settings.UpdateCheckIntervalHours))
                    {
                        Log.Info($"[UpdateService] Update check skipped by cooldown: {elapsed.TotalHours:F1}h elapsed < {settings.UpdateCheckIntervalHours}h interval.");
                        return new UpdateCheckResult(true, false, 0, string.Empty, null, null, null);
                    }
                }
            }

            string url = $"https://api.github.com/repos/{G.RepoSlug}/releases?per_page=10";
            Log.Debug($"[UpdateService] Sending GET request to {url} via NetworkManager.UpdateClient");

            using var request = CreateGitHubRequest(HttpMethod.Get, url);
            using var response = await _networkManager.UpdateClient.SendAsync(request, ct).ConfigureAwait(false);
            Log.Debug($"[UpdateService] GitHub HTTP response: {(int)response.StatusCode} {response.ReasonPhrase}");

            if (!response.IsSuccessStatusCode)
            {
                var errorMsg = $"GitHub API error: {(int)response.StatusCode} ({response.ReasonPhrase})";
                Log.Warn($"[UpdateService] {errorMsg}");
                return new UpdateCheckResult(false, false, 0, string.Empty, null, null, errorMsg);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var releases = await JsonSerializer.DeserializeAsync(
                stream,
                AppJsonContext.Default.ListGitHubReleaseDto,
                ct).ConfigureAwait(false);

            if (releases is null || releases.Count == 0)
            {
                Log.Warn("[UpdateService] No releases found in repository response.");
                return new UpdateCheckResult(false, false, 0, string.Empty, null, null, "No releases found.");
            }

            Log.Debug($"[UpdateService] Received {releases.Count} releases from GitHub API. Resolving latest build...");

            var resolved = ResolveCandidateRelease(releases);
            if (resolved is null)
            {
                Log.Warn("[UpdateService] Could not resolve any valid release candidate from GitHub.");
                return new UpdateCheckResult(false, false, 0, string.Empty, null, null, "Unable to resolve release.");
            }

            var (candidateRelease, remoteCommit) = resolved.Value;
            int currentCommit = G.Build.CommitCount;

#if DEBUG
            if (DebugSimulatedCommitCount > 0)
            {
                Log.Info($"[UpdateService] [DEBUG OVERRIDE] Simulating local commit #{DebugSimulatedCommitCount} instead of real #{currentCommit}");
                currentCommit = DebugSimulatedCommitCount;
            }
#endif

            _library.UpdateSettings(s => s.Updates.LastUpdateCheckUtc = DateTime.UtcNow);

            GitHubAssetDto? zipAsset = candidateRelease.Assets.FirstOrDefault(a =>
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

            bool hasUpdate = remoteCommit > currentCommit && zipAsset != null;
            string versionName = string.IsNullOrWhiteSpace(candidateRelease.Name)
                ? candidateRelease.TagName
                : candidateRelease.Name;

            Log.Info($"[UpdateService] Check finished. Local: #{currentCommit}, Remote: #{remoteCommit} ('{versionName}'). Update available: {hasUpdate}. Asset: '{zipAsset?.Name ?? "(none)"}'");

            string? error = null;
            if (remoteCommit > currentCommit && zipAsset == null)
            {
                error = LocalizationService.Instance["Settings_UpdateNoAssets"];
                Log.Warn($"[UpdateService] Remote build #{remoteCommit} is newer, but no .zip asset was found in release '{candidateRelease.TagName}'.");
            }

            return new UpdateCheckResult(
                IsSuccess: true,
                HasUpdate: hasUpdate,
                RemoteCommitCount: remoteCommit,
                VersionName: versionName,
                ReleaseNotes: candidateRelease.Body,
                Asset: zipAsset,
                ErrorMessage: error);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log.Debug("[UpdateService] Update check was cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"[UpdateService] Update check exception: {ex.Message}");
            return new UpdateCheckResult(false, false, 0, string.Empty, null, null, ex.Message);
        }
    }

    /// <summary>
    /// Потоково загружает архив обновления через сетевой стек с автоматической поддержкой редиректов в <see cref="NetworkManager.UpdateClient"/>.
    /// </summary>
    public async Task<string> DownloadUpdateAsync(
        GitHubAssetDto asset,
        IProgress<double>? progress,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asset);

        Log.Info($"[UpdateService] Initiating download: '{asset.Name}' ({asset.Size / (1024 * 1024.0):F2} MB) from '{asset.BrowserDownloadUrl}'");

        if (Directory.Exists(G.Folder.Update))
        {
            Log.Debug($"[UpdateService] Cleaning existing update directory: {G.Folder.Update}");
            Directory.Delete(G.Folder.Update, recursive: true);
        }

        Directory.CreateDirectory(G.Folder.Update);

        using var request = CreateGitHubRequest(HttpMethod.Get, asset.BrowserDownloadUrl);
        using var response = await _networkManager.UpdateClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        long totalBytes = response.Content.Headers.ContentLength ?? asset.Size;
        Log.Debug($"[UpdateService] Server confirmed stream size: {totalBytes} bytes. Writing to '{G.FilePath.UpdatePayloadZip}'");

        await using var sourceStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fileStream = new FileStream(
            G.FilePath.UpdatePayloadZip,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;
        int lastReportedPercent = -1;

        while ((bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
            totalRead += bytesRead;

            if (totalBytes > 0)
            {
                double percentage = Math.Clamp((double)totalRead / totalBytes * 100.0, 0, 100);
                int currentPercentInt = (int)percentage;

                if (currentPercentInt >= lastReportedPercent + 25)
                {
                    lastReportedPercent = currentPercentInt;
                    Log.Debug($"[UpdateService] Download progress: {percentage:F0}% ({totalRead / (1024 * 1024.0):F1} MB / {totalBytes / (1024 * 1024.0):F1} MB)");
                }

                progress?.Report(percentage);
            }
        }

        Log.Info($"[UpdateService] Download complete. Successfully saved payload ({totalRead} bytes) to '{G.FilePath.UpdatePayloadZip}'");
        return G.FilePath.UpdatePayloadZip;
    }

    /// <summary>
    /// Выполняет распаковку, атомарную замену файлов и запуск нового процесса.
    /// </summary>
    public void ApplyUpdateAndRestart()
    {
        Log.Info($"[UpdateService] Starting in-place update application from '{G.FilePath.UpdatePayloadZip}'...");

        if (!File.Exists(G.FilePath.UpdatePayloadZip))
        {
            Log.Error($"[UpdateService] Payload archive not found at '{G.FilePath.UpdatePayloadZip}'");
            throw new FileNotFoundException("Update payload archive not found.", G.FilePath.UpdatePayloadZip);
        }

        if (Directory.Exists(G.Folder.UpdateExtracted))
            Directory.Delete(G.Folder.UpdateExtracted, recursive: true);

        Directory.CreateDirectory(G.Folder.UpdateExtracted);
        Log.Debug($"[UpdateService] Extracting zip package into '{G.Folder.UpdateExtracted}'...");
        ZipFile.ExtractToDirectory(G.FilePath.UpdatePayloadZip, G.Folder.UpdateExtracted, overwriteFiles: true);

        string baseDir = AppContext.BaseDirectory;
        string? currentExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentExe))
            currentExe = Path.Combine(baseDir, "LMP.exe");

        var sourceFiles = Directory.GetFiles(G.Folder.UpdateExtracted, "*", SearchOption.AllDirectories);
        Log.Info($"[UpdateService] Extracted {sourceFiles.Length} files. Performing in-place swap in '{baseDir}'...");

        var movedFiles = new List<(string Target, string Backup)>(sourceFiles.Length);

        try
        {
            foreach (var file in sourceFiles)
            {
                string relativePath = Path.GetRelativePath(G.Folder.UpdateExtracted, file);
                string targetPath = Path.Combine(baseDir, relativePath);
                string? targetDir = Path.GetDirectoryName(targetPath);

                if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);

                if (File.Exists(targetPath))
                {
                    string backupPath = targetPath + ".old";
                    File.Move(targetPath, backupPath, overwrite: true);
                    movedFiles.Add((targetPath, backupPath));
                    Log.Debug($"[UpdateService] Renamed occupied file: '{relativePath}' -> '{relativePath}.old'");
                }

                File.Move(file, targetPath, overwrite: true);
            }
            Log.Info($"[UpdateService] All {sourceFiles.Length} binaries successfully replaced.");
        }
        catch (Exception ex)
        {
            Log.Error($"[UpdateService] File swap failed with exception: {ex.Message}. Initiating rollback of {movedFiles.Count} files...");
            foreach (var (target, backup) in movedFiles)
            {
                try
                {
                    if (File.Exists(backup))
                        File.Move(backup, target, overwrite: true);
                }
                catch (Exception rollbackEx)
                {
                    Log.Error($"[UpdateService] Rollback failed for '{target}': {rollbackEx.Message}");
                }
            }
            throw;
        }

        int currentPid = Environment.ProcessId;
        Log.Info($"[UpdateService] Launching updated executable '{currentExe}' with arguments '--wait-pid={currentPid}'...");

        var startInfo = new ProcessStartInfo
        {
            FileName = currentExe,
            Arguments = $"--wait-pid={currentPid}",
            WorkingDirectory = baseDir,
            UseShellExecute = false
        };

        Process.Start(startInfo);
        Log.Info("[UpdateService] New process spawned. Requesting desktop application shutdown...");

        Dispatcher.UIThread.Post(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
            else
            {
                Environment.Exit(0);
            }
        });
    }

    /// <summary>
    /// Очищает устаревшие резервные копии (.old) и временные каталоги при старте.
    /// Использует короткий retry-цикл для компенсации асинхронного освобождения дескрипторов ядром Windows NTFS.
    /// </summary>
    public static void CleanupPendingOldFiles()
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            var oldFiles = Directory.GetFiles(baseDir, "*.old", SearchOption.TopDirectoryOnly);
            if (oldFiles.Length == 0) return;

            Log.Info($"[UpdateService] Found {oldFiles.Length} obsolete .old binaries. Cleaning up...");
            foreach (var oldFile in oldFiles)
            {
                bool deleted = false;
                for (int attempt = 1; attempt <= 4; attempt++)
                {
                    try
                    {
                        File.Delete(oldFile);
                        Log.Debug($"[UpdateService] Pruned obsolete binary: '{Path.GetFileName(oldFile)}' (attempt {attempt})");
                        deleted = true;
                        break;
                    }
                    catch (IOException)
                    {
                        if (attempt < 4) Thread.Sleep(100 * attempt);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        if (attempt < 4) Thread.Sleep(100 * attempt);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug($"[UpdateService] Could not prune file '{Path.GetFileName(oldFile)}': {ex.Message}");
                        break;
                    }
                }

                if (!deleted)
                    Log.Debug($"[UpdateService] File '{Path.GetFileName(oldFile)}' still locked by OS driver. Will be pruned on next launch.");
            }

            if (Directory.Exists(G.Folder.Update))
            {
                try
                {
                    Directory.Delete(G.Folder.Update, recursive: true);
                    Log.Debug($"[UpdateService] Pruned temporary update directory '{G.Folder.Update}'");
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[UpdateService] Startup cleanup pass encountered an error: {ex.Message}");
        }
    }

    private static HttpRequestMessage CreateGitHubRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("LMP-Client", G.Build.Version));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
        return request;
    }

    /// <summary>
    /// Разрешает наиболее релевантный релиз (среди плавающего 'dev' и снимков 'dev-<commit>').
    /// </summary>
    private static (GitHubReleaseDto Release, int CommitCount)? ResolveCandidateRelease(List<GitHubReleaseDto> releases)
    {
        GitHubReleaseDto? bestRelease = null;
        int highestCommit = 0;

        foreach (var release in releases)
        {
            int commit = ExtractCommitCountFromRelease(release);
            if (commit > highestCommit)
            {
                highestCommit = commit;
                bestRelease = release;
            }
        }

        if (bestRelease is not null)
            return (bestRelease, highestCommit);

        return null;
    }

    /// <summary>
    /// Извлекает числовой номер коммита из релиза (анализирует тег, заголовок и markdown-тело).
    /// </summary>
    public static int ExtractCommitCountFromRelease(GitHubReleaseDto release)
    {
        int fromTag = ParseCommitCountFromTag(release.TagName);
        if (fromTag > 0) return fromTag;

        if (!string.IsNullOrEmpty(release.Body))
        {
            var match = CommitNumberRegex().Match(release.Body);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int fromBody))
                return fromBody;

            var snapshotMatch = SnapshotTagRegex().Match(release.Body);
            if (snapshotMatch.Success && int.TryParse(snapshotMatch.Groups[1].Value, out int fromSnapshot))
                return fromSnapshot;
        }

        if (!string.IsNullOrEmpty(release.Name))
        {
            int fromTitle = ParseCommitCountFromTag(release.Name);
            if (fromTitle > 0) return fromTitle;
        }

        return 0;
    }

    /// <summary>
    /// Извлекает числовой номер коммита/билда из строки тега GitHub.
    /// </summary>
    public static int ParseCommitCountFromTag(ReadOnlySpan<char> tag)
    {
        tag = tag.Trim();
        if (tag.IsEmpty) return 0;

        if (tag.StartsWith("dev-", StringComparison.OrdinalIgnoreCase))
            tag = tag[4..];
        else if (tag.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            tag = tag[1..];
        else if (tag.StartsWith("#"))
            tag = tag[1..];

        int lastDot = tag.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < tag.Length - 1)
            tag = tag[(lastDot + 1)..];

        int plusIndex = tag.IndexOf('+');
        if (plusIndex >= 0)
            tag = tag[..plusIndex];

        int dashIndex = tag.IndexOf('-');
        if (dashIndex >= 0)
            tag = tag[..dashIndex];

        if (int.TryParse(tag, out int commitCount))
            return commitCount;

        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Log.Debug("[UpdateService] Disposed.");
    }
}