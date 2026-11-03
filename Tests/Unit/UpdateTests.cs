using System.Text.Json;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Модульные тесты подсистемы автообновления и целостности метаданных сборок.
/// Проверяют разбор git-тегов, номеров коммитов, SourceGen DTO и фильтрации тестов.
/// </summary>
public static class UpdateTests
{
    [TestMethod(TestCategory.Unit, "Parse Commit Count from Git Tag Formats", Group = TestGroups.System, Order = 1)]
    public static Task TestCommitCountParsingAsync()
    {
        TestAssert.Equal(185, UpdateService.ParseCommitCountFromTag("dev-185"));
        TestAssert.Equal(172, UpdateService.ParseCommitCountFromTag("v0.0.172"));
        TestAssert.Equal(172, UpdateService.ParseCommitCountFromTag("#172"));
        TestAssert.Equal(172, UpdateService.ParseCommitCountFromTag("172"));
        TestAssert.Equal(172, UpdateService.ParseCommitCountFromTag("0.0.172"));
        TestAssert.Equal(172, UpdateService.ParseCommitCountFromTag("0.0.172+abc1234"));
        TestAssert.Equal(0, UpdateService.ParseCommitCountFromTag("invalid-tag"));
        TestAssert.Equal(0, UpdateService.ParseCommitCountFromTag(string.Empty));

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Extract Commit Count from Rolling Release", Group = TestGroups.System, Order = 2)]
    public static Task TestRollingDevReleaseExtractionAsync()
    {
        var release = new GitHubReleaseDto
        {
            TagName = "dev",
            Name = "LMP Dev Build (Latest)",
            Prerelease = true,
            Body = """
            # 🎵 LMP Dev Build (Latest)
            | Parameter | Value |
            |---|---|
            | Latest Version | `190-7f2a1b4` |
            | Commit Number | `190` |
            | Snapshot Tag | [`dev-190`](https://github.com/paralax034/LMP/releases/tag/dev-190) |
            """,
            Assets =
            [
                new GitHubAssetDto
                {
                    Name = "LMP-Release-latest.zip",
                    Size = 19000000,
                    BrowserDownloadUrl = "https://github.com/paralax034/LMP/releases/download/dev/LMP-Release-latest.zip"
                }
            ]
        };

        int extractedCommit = UpdateService.ExtractCommitCountFromRelease(release);
        TestAssert.Equal(190, extractedCommit);

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "GitHubReleaseDto SourceGen Deserialization", Group = TestGroups.System, Order = 3)]
    public static Task TestReleaseDtoDeserializationAsync()
    {
        const string json = """
        {
          "tag_name": "dev",
          "name": "LMP Dev Build (Latest)",
          "prerelease": true,
          "body": "| Commit Number | `205` |",
          "published_at": "2026-10-08T12:00:00Z",
          "assets": [
            {
              "name": "LMP-Release-latest.zip",
              "size": 18456000,
              "browser_download_url": "https://github.com/paralax034/LMP/releases/download/dev/LMP-Release-latest.zip"
            }
          ]
        }
        """;

        var dto = TestAssert.NotNull(JsonSerializer.Deserialize(json, AppJsonContext.Default.GitHubReleaseDto));

        TestAssert.Equal("dev", dto.TagName);
        TestAssert.Equal("LMP Dev Build (Latest)", dto.Name);
        TestAssert.True(dto.Prerelease);
        TestAssert.Equal(1, dto.Assets.Count);
        TestAssert.Equal("LMP-Release-latest.zip", dto.Assets[0].Name);
        TestAssert.Equal(18456000L, dto.Assets[0].Size);

        int commit = UpdateService.ExtractCommitCountFromRelease(dto);
        TestAssert.Equal(205, commit);

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Prune Pending .old Update Binaries", Group = TestGroups.System, Order = 4)]
    public static Task TestCleanupPassAsync()
    {
        // Изолированная песочница: ни каталог exe, ни реальный %LOCALAPPDATA%/LMP/Update не затрагиваются.
        string root = Path.Combine(Path.GetTempPath(), $"LMP_UpdateCleanupTest_{Guid.NewGuid():N}");
        string baseDir = Path.Combine(root, "app");
        string updateDir = Path.Combine(root, "Update");

        try
        {
            Directory.CreateDirectory(baseDir);
            Directory.CreateDirectory(updateDir);

            string oldFile = Path.Combine(baseDir, "LMP.exe.old");
            string liveFile = Path.Combine(baseDir, "LMP.exe");
            File.WriteAllText(oldFile, "cleanup-test-payload");
            File.WriteAllText(liveFile, "live-binary");
            File.WriteAllText(Path.Combine(updateDir, "payload.zip"), "staged-update");

            UpdateService.CleanupPendingOldFiles(baseDir, updateDir);

            TestAssert.False(File.Exists(oldFile), "CleanupPendingOldFiles failed to prune the old file");
            TestAssert.True(File.Exists(liveFile), "CleanupPendingOldFiles must not touch live binaries");
            TestAssert.False(Directory.Exists(updateDir), "CleanupPendingOldFiles must prune the update staging directory");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "TestRunner Canonical Groups & Filter Perf", Group = TestGroups.System, Order = 5)]
    public static Task TestFrameworkGroupsAndFilterPerfAsync()
    {
        var groups = TestDiscovery.GetAllGroups();
        TestAssert.True(groups.Count > 0, "Expected discovered test groups");

        foreach (var g in groups)
            TestAssert.Equal(TestGroups.Normalize(g), g);

        var vm = new UI.Features.Debug.TestRunnerViewModel();
        var initialCollectionRef = vm.FilteredTests;
        int totalCount = vm.AllTests.Count;
        TestAssert.True(totalCount > 0);

        vm.SearchFilter = "Audio";
        TestAssert.True(ReferenceEquals(initialCollectionRef, vm.FilteredTests));
        TestAssert.True(vm.FilteredTests.Count <= totalCount);

        vm.SelectedGroup = TestGroups.System;
        TestAssert.True(ReferenceEquals(initialCollectionRef, vm.FilteredTests));

        vm.SearchFilter = "";
        vm.SelectedGroup = null;
        TestAssert.Equal(totalCount, vm.FilteredTests.Count);

        return Task.CompletedTask;
    }
}