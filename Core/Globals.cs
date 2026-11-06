using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace LMP;

public static class G
{
    public const string AppId = "LMP";
    public const string AppName = "Lite Music Player";
    public const string GitHubUrl = "https://github.com/paralax034/LMP";

    /// <summary>
    /// GitHub repository slug in "owner/repo" format for GitHub API calls.
    /// </summary>
    public static string RepoSlug
    {
        get
        {
            var url = GitHubUrl.TrimEnd('/');
            const string prefix = "https://github.com/";
            if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return url[prefix.Length..];

            return "paralax034/LMP";
        }
    }
    public const string GithubExtensionAuthUrl = "https://github.com/paralax034/LMP-Auth";

    public static string DisplayGithubUrl => GitHubUrl[8..];
    public static string AuthExtensionDownloadUrl => GithubExtensionAuthUrl + "/archive/refs/heads/main.zip";

    public static class Build
    {
        private static readonly Lazy<BuildInfo> _info = new(LoadBuildInfo);

        public static BuildInfo Info => _info.Value;
        public static string Version => Info.Version;
        public static string GitHash => Info.GitHash;
        public static int CommitCount => Info.CommitCount;
        public static DateTime BuildDate => Info.BuildDate;

        public static bool IsDebug =>
#if DEBUG
            true;
#else
            false;
#endif

        public static int MinSplashTimeMs =>
#if DEBUG
            1000;
#else
            2000;
#endif

        public static string DisplayVersion => Info.DisplayVersion;
        public static string FullVersionString => Info.FullVersionString;

        private static BuildInfo LoadBuildInfo()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version ?? new Version(1, 0, 0);

            var infoVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? version.ToString();

            // Parse Git hash from "1.0.172+abc1234"
            var gitHash = "local";

            if (infoVersion.Contains('+'))
            {
                var hashPart = infoVersion.Split('+')[1].Trim();
                // Take only the first 7 characters
                gitHash = hashPart.Length > 7 ? hashPart[..7] : hashPart;
            }

            // CommitCount = version.Build (the third version component)
            var commitCount = version.Build;

            var buildDate = DateTime.Now;
            try
            {
                // Using Environment.ProcessPath guarantees compatibility with Single-File builds.
                // Returns the path to LMP.exe, from which the actual file creation/publication date is extracted.
                var location = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(location) && File.Exists(location))
                {
                    buildDate = File.GetLastWriteTime(location);
                }
            }
            catch { }

            var displayVersion = IsDebug
                ? $"#{commitCount}-dev"
                : $"#{commitCount}";

            return new BuildInfo
            {
                Version = commitCount.ToString(),
                GitHash = gitHash,
                CommitCount = commitCount,
                BuildDate = buildDate,
                IsDebug = IsDebug,
                DisplayVersion = displayVersion,
                FullVersionString = $"{displayVersion} ({gitHash}) • {buildDate:yyyy-MM-dd}"
            };
        }
    }

    public sealed class BuildInfo
    {
        public required string Version { get; init; }
        public required string GitHash { get; init; }
        public required int CommitCount { get; init; }
        public required DateTime BuildDate { get; init; }
        public required bool IsDebug { get; init; }
        public required string DisplayVersion { get; init; }
        public required string FullVersionString { get; init; }
    }

    public static class SystemInfo
    {
        public static string DetectSystemLanguage()
        {
            try
            {
                var culture = CultureInfo.CurrentUICulture;
                var lang = culture.TwoLetterISOLanguageName.ToLowerInvariant();

                return lang switch
                {
                    "ru" => "ru",
                    "en" => "en",
                    _ => "en"
                };
            }
            catch
            {
                return "en";
            }
        }
    }

    public static class Folder
    {
        public static readonly string Data = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppId);

        public static readonly string Cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppId);

        public static readonly string Logs = Path.Combine(Data, "Logs");
        public static readonly string Downloads = Path.Combine(Data, "Downloads");
        public static readonly string ImageCache = Path.Combine(Cache, "ImageCache");
        public static readonly string SearchCache = Path.Combine(Cache, "SearchCache");
        public static readonly string AudioCache = Path.Combine(Cache, "AudioCache");
        public static readonly string NTokenCache = Path.Combine(Cache, "NToken");
        public static readonly string SigCipherCache = Path.Combine(Cache, "SigCipher");
        public static readonly string Extension = Path.Combine(Cache, "Extension");
        public static readonly string StreamCache = Path.Combine(Cache, "StreamCache");

        /// <summary>
        /// Directory containing test data for different player versions.
        /// Contains JSON files with encrypted/expected pairs for NToken and SigCipher.
        /// </summary>
        public static readonly string TestData = Path.Combine(Cache, "TestData");

        public static readonly string Update = Path.Combine(Cache, "Update");
        public static readonly string UpdateExtracted = Path.Combine(Update, "Extracted");

        public static void Create()
        {
            Directory.CreateDirectory(Data);
            Directory.CreateDirectory(Cache);
            Directory.CreateDirectory(Downloads);
            Directory.CreateDirectory(ImageCache);
            Directory.CreateDirectory(SearchCache);
            Directory.CreateDirectory(AudioCache);
            Directory.CreateDirectory(NTokenCache);
            Directory.CreateDirectory(SigCipherCache);
            Directory.CreateDirectory(Logs);
            Directory.CreateDirectory(TestData);
            Directory.CreateDirectory(StreamCache);
            Directory.CreateDirectory(Update);
        }
    }

    public static class FilePath
    {
        public static readonly string Bootstrap = Path.Combine(Folder.Data, "bootstrap.json");
        public static readonly string Cookie = Path.Combine(Folder.Data, "auth_cookies.txt");
        public static readonly string LegacyDatabase = Path.Combine(Folder.Data, "library.json");
        /// <summary>
        /// Path to the main library database file.
        /// Redirected to the process's temporary directory in debug builds during headless CLI test runs.
        /// </summary>
        public static string Database { get; private set; } = Path.Combine(Folder.Data, "library.db");
        public static readonly string Theme = Path.Combine(Folder.Data, "theme.json");

#if DEBUG
        /// <summary>
        /// Prefix for the isolated test database temporary directory.
        /// </summary>
        public const string TestDatabaseFolderPrefix = "LMP-tests-";

        /// <summary>
        /// Temporary directory of the isolated test database; <c>null</c> if isolation is disabled.
        /// </summary>
        public static string? IsolatedTestDatabaseFolder { get; private set; }

        /// <summary>
        /// Redirects <see cref="Database"/> to a temporary directory <c>%TEMP%/LMP-tests-{pid}</c>.
        /// Must be called strictly before service registration and the first database access.
        /// </summary>
        /// <returns>Full path to the created temporary directory.</returns>
        public static string UseIsolatedTestDatabase()
        {
            var folder = Path.Combine(
                Path.GetTempPath(),
                TestDatabaseFolderPrefix + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);

            Directory.CreateDirectory(folder);

            IsolatedTestDatabaseFolder = folder;
            Database = Path.Combine(folder, "library.db");
            return folder;
        }

        /// <summary>
        /// Resets SQLite connection pools and deletes the isolated test database temporary directory.
        /// </summary>
        /// <remarks>
        /// File system exceptions are rethrown to the caller for logging.
        /// </remarks>
        public static void CleanupIsolatedTestDatabase()
        {
            var folder = IsolatedTestDatabaseFolder;
            if (folder is null)
                return;

            IsolatedTestDatabaseFolder = null;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
#endif

        /// <summary>
        /// Authentication state file (user profile, data cache).
        /// Stored in %APPDATA%/LMP/auth.json
        /// </summary>
        public static readonly string AuthData = Path.Combine(Folder.Data, "auth.json");

        public static readonly string NTokenCache = Path.Combine(Folder.NTokenCache, "tokens.bin");
        public static readonly string SigCipherCache = Path.Combine(Folder.SigCipherCache, "sigcache.bin");

        /// <summary>
        /// Test parameters configuration.
        /// Allows changing videoId, tokens, iterations without recompilation.
        /// Stored in %LOCALAPPDATA%/LMP/test-config.json
        /// </summary>
        public static readonly string TestConfig = Path.Combine(Folder.Cache, "test-config.json");

        public static readonly string TempAuthExtensionZipFile = Path.Combine(Folder.Cache, "LMP-Auth-main.zip");
        public static readonly string UpdatePayloadZip = Path.Combine(Folder.Update, "update_payload.zip");

        public static readonly string CdnHostStats = Path.Combine(Folder.StreamCache, "cdn_hosts.bin");
        public static readonly string SessionCache = Path.Combine(Folder.StreamCache, "session_cache.bin");
    }

    public static class Json
    {
        public static readonly JsonSerializerOptions Beautiful = new()
        {
            WriteIndented = true,
        };

        // public static readonly JsonSerializerOptions Compact = new()
        // {
        //     WriteIndented = false,
        // };
    }
}