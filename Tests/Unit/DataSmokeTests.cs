using System.Globalization;
using LMP.Core.Data;
using LMP.Tests.Framework;
using Microsoft.Data.Sqlite;
using SkiaSharp;

namespace LMP.Tests.Unit;

/// <summary>
/// Smoke tests for data infrastructure and native dependencies: SQLite bridge, consistent backup,
/// test database isolation, atomic schema migration rollback, and Skia 3.x compatibility.
/// </summary>
public static class DataSmokeTests
{
    /// <summary>
    /// Minimum SQLite version with <c>VACUUM INTO</c> support (3.27.0).
    /// </summary>
    private const int MinimumSqliteVersion = 3_027_000;

    [TestMethod(TestCategory.Unit, "SQLite Native Bridge (Batteries) & VACUUM INTO Support", Group = TestGroups.Storage, Order = 40)]
    public static async Task TestSqliteNativeBridgeAsync()
    {
        int libVersion = SQLitePCL.raw.sqlite3_libversion_number();
        TestAssert.True(libVersion >= MinimumSqliteVersion,
            $"Native SQLite {libVersion} is older than {MinimumSqliteVersion} required for VACUUM INTO");

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sqlite_version();";
        var reported = await cmd.ExecuteScalarAsync() as string;
        TestAssert.NotNullOrEmpty(reported);
    }

    [TestMethod(TestCategory.Unit, "Consistent Backup Captures Uncheckpointed WAL Pages", Group = TestGroups.Storage, Order = 41)]
    public static async Task TestConsistentBackupIncludesWalAsync()
    {
        string scratch = CreateScratchDirectory();
        try
        {
            string dbPath = Path.Combine(scratch, "source.db");
            string backupPath = Path.Combine(scratch, "source.db.bak");

            // The writer remains open: closing the last connection would execute a checkpoint and truncate -wal.
            await using var writer = CreateScratchConnection(dbPath);
            await writer.OpenAsync();
            await ExecuteAsync(writer, """
                PRAGMA journal_mode = WAL;
                PRAGMA wal_autocheckpoint = 0;
                CREATE TABLE "Probe" ("Value" TEXT NOT NULL);
                INSERT INTO "Probe" ("Value") VALUES ('wal-row');
                """);

            var walInfo = new FileInfo(dbPath + "-wal");
            TestAssert.True(walInfo.Exists && walInfo.Length > 0, "Probe row must reside in the WAL before backup");

            DatabaseExtensions.CreateConsistentBackup(dbPath, backupPath);

            await using var reader = CreateScratchConnection(backupPath);
            await reader.OpenAsync();
            long rows = await ScalarInt64Async(reader, """SELECT count(*) FROM "Probe" WHERE "Value" = 'wal-row';""");
            TestAssert.Equal(1L, rows);
            TestAssert.False(File.Exists(backupPath + ".tmp"), "Temporary backup file must be renamed after success");
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [TestMethod(TestCategory.Unit, "Headless Test Database Isolation", Group = TestGroups.Storage, Order = 42)]
    public static Task TestDatabaseIsolationAsync()
    {
        RequireIsolatedDatabase();

        string dbPath = G.FilePath.Database;
        string? folder = Path.GetDirectoryName(dbPath);
        TestAssert.NotNull(folder);
        TestAssert.Equal($"LMP-tests-{Environment.ProcessId}", Path.GetFileName(folder));
        TestAssert.True(dbPath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase),
            $"Test database must live under the temp directory: {dbPath}");
        TestAssert.True(File.Exists(dbPath), $"Isolated database must be created before tests run: {dbPath}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Precondition for tests operating on the database via production services.
    /// Without an isolated temporary database, the test is skipped so that running from the GUI host
    /// does not leave test rows in the user's library.
    /// </summary>
    internal static void RequireIsolatedDatabase()
    {
        if (AppEntry.IsTestUiMode)
            TestAssert.Skip("UI test mode intentionally works with the real database");

        if (G.FilePath.IsolatedTestDatabaseFolder is null)
            TestAssert.Skip("Host runs on the user database; database tests require the headless test host");
    }

    [TestMethod(TestCategory.Unit, "Schema Migration Rolls Back Atomically On Failure", Group = TestGroups.Storage, Order = 43)]
    public static async Task TestSchemaMigrationRollbackAsync()
    {
        string scratch = CreateScratchDirectory();
        try
        {
            string dbPath = Path.Combine(scratch, "migration.db");
            await using var connection = CreateScratchConnection(dbPath);
            await connection.OpenAsync();

            // View with a table name: CREATE TABLE IF NOT EXISTS skips it, but an index on it fails
            // with "views may not be indexed" after other tables have already been created inside the migration transaction.
            await ExecuteAsync(connection, """CREATE VIEW "Notifications" AS SELECT 1 AS "Id";""");

            SqliteException? failure = null;
            try
            {
                await connection.ApplySchemaMigrationAsync(DatabaseExtensions.CurrentDbVersion);
            }
            catch (SqliteException ex)
            {
                failure = ex;
            }

            TestAssert.NotNull(failure);
            TestAssert.Equal(0, await connection.GetDatabaseVersionAsync());
            TestAssert.Equal(0L, await ScalarInt64Async(connection,
                "SELECT count(*) FROM sqlite_master WHERE type IN ('table', 'index');"));
            TestAssert.True(SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle!) != 0,
                "Failed migration must not leave an open transaction");

            // Check: after resolving the cause, the same database migrates normally.
            await ExecuteAsync(connection, """DROP VIEW "Notifications";""");
            await connection.ApplySchemaMigrationAsync(DatabaseExtensions.CurrentDbVersion);
            TestAssert.Equal(DatabaseExtensions.CurrentDbVersion, await connection.GetDatabaseVersionAsync());
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// Isolation check result is consistent with the host: where an isolated database is not enabled (GUI host),
    /// the check must be skipped rather than failing with a false red.
    /// </summary>
    [TestMethod(TestCategory.Unit, "Test Database Isolation Verdict Matches Host", Group = TestGroups.Storage, Order = 44)]
    public static async Task TestDatabaseIsolationVerdictAsync()
    {
        if (AppEntry.IsTestUiMode)
            TestAssert.Skip("UI test mode intentionally works with the real database");

        bool isolated = G.FilePath.IsolatedTestDatabaseFolder != null;
        Exception? failure = null;
        try
        {
            await TestDatabaseIsolationAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (isolated)
        {
            TestAssert.Null(failure, $"Isolated host must pass the isolation check: {failure?.Message}");
            return;
        }

        TestAssert.True(failure is TestSkippedException,
            $"Host without an isolated database must skip the isolation check, got {failure?.GetType().Name ?? "pass"}: {failure?.Message}");
    }

    [TestMethod(TestCategory.Unit, "SkiaSharp 3.x Native Raster & PNG Roundtrip", Group = TestGroups.System, Order = 40)]
    public static Task TestSkiaSharpRasterRoundtripAsync()
    {
        var managedVersion = typeof(SKBitmap).Assembly.GetName().Version;
        TestAssert.NotNull(managedVersion);
        TestAssert.Equal(3, managedVersion.Major);
        TestAssert.True(SkiaSharpVersion.CheckNativeLibraryCompatible(),
            $"Native libSkiaSharp {SkiaSharpVersion.Native} is incompatible with managed SkiaSharp {managedVersion}");

        var expected = new SKColor(0x20, 0x40, 0x80, 0xFF);
        using var bitmap = new SKBitmap(new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(expected);

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        TestAssert.NotNull(encoded);

        using var decoded = SKBitmap.Decode(encoded);
        TestAssert.NotNull(decoded);
        TestAssert.Equal(expected, decoded.GetPixel(2, 2));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates a unique temporary folder for database files that does not collide with the isolated test DB.
    /// </summary>
    private static string CreateScratchDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "LMP-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Creates an unpooled connection: the file is released immediately upon closing, allowing the folder to be deleted.
    /// </summary>
    private static SqliteConnection CreateScratchConnection(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        return new SqliteConnection(builder.ConnectionString);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarInt64Async(SqliteConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }
}