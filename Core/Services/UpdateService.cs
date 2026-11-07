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
/// Result of the update check operation.
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
/// Service for checking, downloading, and seamlessly applying application updates.
/// All network operations are performed through the centralized <see cref="NetworkManager"/>.
/// </summary>
public sealed partial class UpdateService : IDisposable
{
    [GeneratedRegex(@"\|\s*Commit Number\s*\|\s*`?(\d+)`?", RegexOptions.IgnoreCase)]
    private static partial Regex CommitNumberRegex();

    [GeneratedRegex(@"dev-(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SnapshotTagRegex();

    private readonly LibraryService _library;
    private readonly NetworkManager _networkManager;
    private readonly Lock _syncLock = new();

    private Task<UpdateCheckResult>? _inFlightCheckTask;
    private bool _disposed;

    public event Action? StateChanged;

    public UpdateCheckResult? LastCheckResult { get; private set; }
    public GitHubAssetDto? PendingAsset { get; private set; }
    public int DownloadedCommitCount { get; private set; }

    public bool IsChecking { get; private set; }
    public bool IsDownloading { get; private set; }
    public bool IsUpdateAvailable { get; private set; }
    public bool IsUpdateReadyToInstall { get; private set; }
    public double DownloadProgress { get; private set; }

#if DEBUG
    /// <summary>
    /// Emulated local commit number for debugging updates in Debug builds.
    /// </summary>
    public static int DebugSimulatedCommitCount { get; set; }
#endif

    public UpdateService(LibraryService library, NetworkManager networkManager)
    {
        _library = library;
        _networkManager = networkManager;

        CheckExistingDownloadedPackage();
        Log.Debug($"[UpdateService] Initialized via NetworkManager. Target repo: '{G.RepoSlug}', Current commit: #{G.Build.CommitCount}");
    }

    private void CheckExistingDownloadedPackage()
    {
        try
        {
            if (File.Exists(G.FilePath.UpdatePayloadZip))
            {
                var fileInfo = new FileInfo(G.FilePath.UpdatePayloadZip);
                if (fileInfo.Length > 1024 * 1024)
                {
                    IsUpdateReadyToInstall = true;
                    IsUpdateAvailable = false;
                    Log.Info($"[UpdateService] Found ready-to-install update package on disk ({fileInfo.Length} bytes).");
                }
                else
                {
                    PurgeDownloadedPayload();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[UpdateService] Failed to inspect existing update payload: {ex.Message}");
        }
    }

    /// <summary>
    /// Fetches GitHub repository metadata (stars, forks, issues) via the centralized network stack.
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
    /// Checks for updates via the GitHub Releases API with support for the 'dev' rolling release and snapshots.
    /// Guarantees deduplication of concurrent network requests and an isolated timeout.
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        bool manual = false,
        bool ignoreCooldown = false,
        CancellationToken ct = default)
    {
        Task<UpdateCheckResult>? taskToAwait;
        bool isInitiator = false;

        lock (_syncLock)
        {
            if (_inFlightCheckTask != null && !_inFlightCheckTask.IsCompleted)
            {
                Log.Info("[UpdateService] Update check already in progress. Reusing in-flight task.");
                taskToAwait = _inFlightCheckTask;
            }
            else
            {
                var settings = _library.Settings.Updates;
                if (!manual)
                {
                    if (!settings.AutoCheckUpdates)
                    {
                        Log.Debug("[UpdateService] Auto-check is disabled in settings. Skipping.");
                        return LastCheckResult ?? new UpdateCheckResult(true, false, 0, string.Empty, null, null, null);
                    }

                    if (!ignoreCooldown && settings.LastUpdateCheckUtc.HasValue)
                    {
                        var elapsed = DateTime.UtcNow - settings.LastUpdateCheckUtc.Value;
                        if (elapsed < TimeSpan.FromHours(settings.UpdateCheckIntervalHours))
                        {
                            Log.Info($"[UpdateService] Update check skipped by cooldown: {elapsed.TotalHours:F1}h elapsed < {settings.UpdateCheckIntervalHours}h interval.");
                            return LastCheckResult ?? new UpdateCheckResult(true, false, 0, string.Empty, null, null, null);
                        }
                    }
                }

                IsChecking = true;
                isInitiator = true;
                taskToAwait = ExecuteCheckForUpdatesInternalAsync(manual, ct);
                _inFlightCheckTask = taskToAwait;
            }
        }

        if (isInitiator)
            NotifyStateChanged();

        try
        {
            return await taskToAwait.ConfigureAwait(false);
        }
        finally
        {
            if (isInitiator)
            {
                lock (_syncLock)
                {
                    IsChecking = false;
                    _inFlightCheckTask = null;
                }
                NotifyStateChanged();
            }
        }
    }

    private async Task<UpdateCheckResult> ExecuteCheckForUpdatesInternalAsync(bool manual, CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            var settings = _library.Settings.Updates;
            Log.Info($"[UpdateService] Starting update check (manual={manual}, repo='{G.RepoSlug}')...");

            string url = $"https://api.github.com/repos/{G.RepoSlug}/releases?per_page=10";
            using var request = CreateGitHubRequest(HttpMethod.Get, url);
            using var response = await _networkManager.UpdateClient.SendAsync(request, linkedCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorMsg = $"GitHub API error: {(int)response.StatusCode} ({response.ReasonPhrase})";
                Log.Warn($"[UpdateService] {errorMsg}");
                var failResult = new UpdateCheckResult(false, false, 0, string.Empty, null, null, errorMsg);
                ApplyCheckResult(failResult);
                return failResult;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
            var releases = await JsonSerializer.DeserializeAsync(
                stream,
                AppJsonContext.Default.ListGitHubReleaseDto,
                linkedCts.Token).ConfigureAwait(false);

            if (releases is null || releases.Count == 0)
            {
                Log.Warn("[UpdateService] No releases found in repository response.");
                var noReleasesResult = new UpdateCheckResult(false, false, 0, string.Empty, null, null, "No releases found.");
                ApplyCheckResult(noReleasesResult);
                return noReleasesResult;
            }

            var resolved = ResolveCandidateRelease(releases);
            if (resolved is null)
            {
                Log.Warn("[UpdateService] Could not resolve any valid release candidate from GitHub.");
                var unresResult = new UpdateCheckResult(false, false, 0, string.Empty, null, null, "Unable to resolve release.");
                ApplyCheckResult(unresResult);
                return unresResult;
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

            string? error = null;
            if (remoteCommit > currentCommit && zipAsset == null)
            {
                error = LocalizationService.Instance["Settings_UpdateNoAssets"];
                Log.Warn($"[UpdateService] Remote build #{remoteCommit} is newer, but no .zip asset was found in release '{candidateRelease.TagName}'.");
            }
            else if (hasUpdate)
            {
                Log.Info($"[UpdateService] Update available: '{versionName}' (remote #{remoteCommit} > local #{currentCommit}).");
            }
            else
            {
                Log.Info($"[UpdateService] Up to date (local #{currentCommit}, remote #{remoteCommit} '{versionName}').");
            }

            var result = new UpdateCheckResult(
                IsSuccess: true,
                HasUpdate: hasUpdate,
                RemoteCommitCount: remoteCommit,
                VersionName: versionName,
                ReleaseNotes: candidateRelease.Body,
                Asset: zipAsset,
                ErrorMessage: error);

            ApplyCheckResult(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            Log.Warn("[UpdateService] Update check timed out or was cancelled.");
            var cancelResult = new UpdateCheckResult(false, false, 0, string.Empty, null, null, "Request timed out.");
            ApplyCheckResult(cancelResult);
            return cancelResult;
        }
        catch (Exception ex)
        {
            Log.Error($"[UpdateService] Update check exception: {ex.Message}");
            var exResult = new UpdateCheckResult(false, false, 0, string.Empty, null, null, ex.Message);
            ApplyCheckResult(exResult);
            return exResult;
        }
    }

    private void ApplyCheckResult(UpdateCheckResult result)
    {
        lock (_syncLock)
        {
            LastCheckResult = result;

            if (result.IsSuccess && result.HasUpdate && result.Asset != null)
            {
                if (IsUpdateReadyToInstall && result.RemoteCommitCount > DownloadedCommitCount)
                {
                    Log.Info($"[UpdateService] Newer update #{result.RemoteCommitCount} available while older #{DownloadedCommitCount} was downloaded. Invalidating stale package.");
                    PurgeDownloadedPayload();
                    IsUpdateReadyToInstall = false;
                }

                PendingAsset = result.Asset;
                IsUpdateAvailable = !IsUpdateReadyToInstall;
            }
            else if (result.IsSuccess && !result.HasUpdate)
            {
                IsUpdateAvailable = false;
            }
        }
    }

    /// <summary>
    /// Streams the update archive download via the network stack with automatic redirect support in <see cref="NetworkManager.UpdateClient"/>.
    /// </summary>
    public async Task<string> DownloadUpdateAsync(
        GitHubAssetDto asset,
        IProgress<double>? progress,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asset);

        lock (_syncLock)
        {
            if (IsDownloading)
                throw new InvalidOperationException("Download already in progress.");

            IsDownloading = true;
            DownloadProgress = 0;
            NotifyStateChanged();
        }

        Log.Info($"[UpdateService] Initiating download: '{asset.Name}' ({asset.Size / (1024 * 1024.0):F2} MB) from '{asset.BrowserDownloadUrl}'");

        try
        {
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

            while ((bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                totalRead += bytesRead;

                if (totalBytes > 0)
                {
                    double percentage = Math.Clamp((double)totalRead / totalBytes * 100.0, 0, 100);
                    DownloadProgress = percentage;
                    progress?.Report(percentage);
                    NotifyStateChanged();
                }
            }

            lock (_syncLock)
            {
                DownloadedCommitCount = ParseCommitCountFromTag(asset.Name);
                IsUpdateAvailable = false;
                IsUpdateReadyToInstall = true;
                DownloadProgress = 100;
            }

            Log.Info($"[UpdateService] Download complete. Successfully saved payload ({totalRead} bytes) to '{G.FilePath.UpdatePayloadZip}'");
            return G.FilePath.UpdatePayloadZip;
        }
        finally
        {
            lock (_syncLock)
            {
                IsDownloading = false;
                NotifyStateChanged();
            }
        }
    }

    /// <summary>
    /// Extracts the archive, atomically replaces files, and launches the new process.
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
    /// Cleans up stale backups (.old) and temporary directories on startup.
    /// Uses a short retry loop to compensate for asynchronous handle release by the Windows NTFS kernel.
    /// </summary>
    public static void CleanupPendingOldFiles() =>
        CleanupPendingOldFiles(AppContext.BaseDirectory, G.Folder.Update);

    /// <summary>
    /// Cleans up <c>.old</c> binaries in <paramref name="baseDir"/> and the extraction directory <paramref name="updateDir"/>.
    /// Explicit paths allow tests to run in a temporary folder without touching the app directory or the actual <c>%LOCALAPPDATA%/LMP</c>.
    /// </summary>
    /// <param name="baseDir">Executable directory containing <c>.old</c> copies.</param>
    /// <param name="updateDir">Temporary directory of the downloaded update.</param>
    public static void CleanupPendingOldFiles(string baseDir, string updateDir)
    {
        try
        {
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

            if (Directory.Exists(updateDir))
            {
                try
                {
                    Directory.Delete(updateDir, recursive: true);
                    Log.Debug($"[UpdateService] Pruned temporary update directory '{updateDir}'");
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[UpdateService] Startup cleanup pass encountered an error: {ex.Message}");
        }
    }

    /// <summary>
    /// Permanently deletes the downloaded local update archive and extracted files
    /// if a newer release appears in the repository before applying the previous one.
    /// </summary>
    public static void PurgeDownloadedPayload()
    {
        try
        {
            if (File.Exists(G.FilePath.UpdatePayloadZip))
            {
                File.Delete(G.FilePath.UpdatePayloadZip);
                Log.Info($"[UpdateService] Stale update payload purged: {G.FilePath.UpdatePayloadZip}");
            }

            if (Directory.Exists(G.Folder.UpdateExtracted))
            {
                Directory.Delete(G.Folder.UpdateExtracted, recursive: true);
                Log.Info($"[UpdateService] Stale extracted directory pruned: {G.Folder.UpdateExtracted}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[UpdateService] Failed to purge stale update files: {ex.Message}");
        }
    }

    private static HttpRequestMessage CreateGitHubRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("LMP-Client", G.Build.Version));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
        return request;
    }

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

    private void NotifyStateChanged()
    {
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Log.Debug("[UpdateService] Disposed.");
    }
}