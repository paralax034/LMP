using System.Text.Json;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

public static class UpdateTests
{
    [TestMethod(TestCategory.Unit, "Update: Parse commit count from git tag formats",
        Group = "Update", Order = 1, RequiresNetwork = false, TimeoutSeconds = 3)]
    public static Task TestCommitCountParsingAsync()
    {
        AssertEqual(185, UpdateService.ParseCommitCountFromTag("dev-185"));
        AssertEqual(172, UpdateService.ParseCommitCountFromTag("v0.0.172"));
        AssertEqual(172, UpdateService.ParseCommitCountFromTag("#172"));
        AssertEqual(172, UpdateService.ParseCommitCountFromTag("172"));
        AssertEqual(172, UpdateService.ParseCommitCountFromTag("0.0.172"));
        AssertEqual(172, UpdateService.ParseCommitCountFromTag("0.0.172+abc1234"));
        AssertEqual(0, UpdateService.ParseCommitCountFromTag("invalid-tag"));
        AssertEqual(0, UpdateService.ParseCommitCountFromTag(string.Empty));

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Update: Extract commit count from rolling 'dev' release body",
        Group = "Update", Order = 2, RequiresNetwork = false, TimeoutSeconds = 3)]
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
        AssertEqual(190, extractedCommit);

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Update: GitHubReleaseDto deserialization under Source Generator",
        Group = "Update", Order = 3, RequiresNetwork = false, TimeoutSeconds = 3)]
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

        var dto = JsonSerializer.Deserialize(json, AppJsonContext.Default.GitHubReleaseDto);
        if (dto == null)
            throw new InvalidOperationException("Failed to deserialize GitHubReleaseDto");

        AssertEqual("dev", dto.TagName);
        AssertEqual("LMP Dev Build (Latest)", dto.Name);
        AssertEqual(true, dto.Prerelease);
        AssertEqual(1, dto.Assets.Count);
        AssertEqual("LMP-Release-latest.zip", dto.Assets[0].Name);
        AssertEqual(18456000L, dto.Assets[0].Size);

        int commit = UpdateService.ExtractCommitCountFromRelease(dto);
        AssertEqual(205, commit);

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Update: Old binaries file cleanup pass",
        Group = "Update", Order = 4, RequiresNetwork = false, TimeoutSeconds = 5)]
    public static Task TestCleanupPassAsync()
    {
        string dummyOldFile = Path.Combine(AppContext.BaseDirectory, $"temp_test_{Guid.NewGuid():N}.old");
        File.WriteAllText(dummyOldFile, "cleanup-test-payload");

        if (!File.Exists(dummyOldFile))
            throw new InvalidOperationException("Failed to prepare test old file.");

        UpdateService.CleanupPendingOldFiles();

        if (File.Exists(dummyOldFile))
        {
            try { File.Delete(dummyOldFile); } catch { }
            throw new InvalidOperationException("CleanupPendingOldFiles failed to prune the old file.");
        }

        return Task.CompletedTask;
    }

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Assertion failed. Expected: '{expected}', Actual: '{actual}'");
    }
}