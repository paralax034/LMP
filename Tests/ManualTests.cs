using System.Diagnostics;
using LMP.Tests.Framework;

namespace LMP.Tests;

/// <summary>
/// Точка входа для запуска тестов и диагностических зондов из кода приложения.
/// </summary>
public static class ManualTests
{
    /// <summary>
    /// Запускает все автоматические тесты (Unit, Integration, Benchmark, E2E).
    /// Деструктивные и ручные действия (<see cref="TestCategory.Action"/>) исключены.
    /// </summary>
    public static async Task RunAllAsync()
    {
        var sw = Stopwatch.StartNew();

        Console.WriteLine("\n" + new string('=', 70));
        Console.WriteLine("  LMP AUTOMATED TEST SUITE");
        Console.WriteLine(new string('=', 70) + "\n");

        var runner = new TestRunner(AppEntry.Services);
        int passed = 0, failed = 0, skipped = 0;

        runner.TestCompleted += (descriptor, result) =>
        {
            var icon = result.State switch
            {
                TestRunState.Passed => "✓",
                TestRunState.Failed => "✗",
                TestRunState.Skipped => "⊘",
                _ => "?"
            };

            Console.WriteLine(
                $"  {icon} [{descriptor.Group}] {descriptor.DisplayName} ({result.DurationFormatted})" +
                (result.ErrorMessage is not null ? $"\n    → {result.ErrorMessage}" : ""));

            switch (result.State)
            {
                case TestRunState.Passed: Interlocked.Increment(ref passed); break;
                case TestRunState.Failed: Interlocked.Increment(ref failed); break;
                case TestRunState.Skipped: Interlocked.Increment(ref skipped); break;
            }
        };

        foreach (var (category, tests) in TestDiscovery.GetGrouped())
        {
            if (category == TestCategory.Action)
                continue;

            var label = category switch
            {
                TestCategory.Unit => "▶ UNIT TESTS",
                TestCategory.Integration => "▶ INTEGRATION TESTS (Network / Services)",
                TestCategory.Benchmark => "▶ BENCHMARKS",
                TestCategory.Diagnostic => "▶ DIAGNOSTIC PROBES",
                _ => $"▶ {category}"
            };

            Console.WriteLine($"\n{label}\n");
            await runner.RunBatchAsync(tests).ConfigureAwait(false);
        }

        sw.Stop();

        Console.WriteLine("\n" + new string('=', 70));
        Console.WriteLine($"  RESULTS: {passed} passed, {failed} failed, {skipped} skipped ({sw.Elapsed.TotalSeconds:F1}s)");
        Console.WriteLine(new string('=', 70) + "\n");
    }

    public static Task TestNTokenAsync() =>
        Unit.NTokenTests.TestLiveDecryptionAsync(AppEntry.Services);

    public static Task TestSigCipherAsync() =>
        Unit.SigCipherTests.TestLiveDecryptionAsync(AppEntry.Services);

    public static Task TestAstSolverAsync() =>
        Unit.NTokenTests.TestAstSolverWithPersistentContextAsync();

    public static Task TestFullPipelineAsync(string videoId = "dQw4w9WgXcQ") =>
        Integration.StreamPipelineTests.TestFullPipelineInternalAsync(AppEntry.Services, videoId);

    public static Task TestPoTokenAsync() =>
        Integration.PoTokenTests.TestFullPipelineAsync();

    public static Task TestPoTokenSnapshotAsync() =>
        Integration.PoTokenTests.TestQuickJsSnapshotAsync();

    public static Task DiagnoseCdnAsync() =>
        Integration.CdnDiagnosticTests.TestLiveFullChainAsync(AppEntry.Services);

    public static Task AuditZapretConfigAsync() =>
        Integration.CdnDiagnosticTests.TestZapretConfigAuditAsync(AppEntry.Services);

    public static Task TestProblemCdnAsync() =>
        Integration.CdnDiagnosticTests.TestProblemIpDirectAsync(AppEntry.Services);

    public static Task DiagnoseCdnMultiManifestAsync() =>
        Integration.CdnDiagnosticTests.TestMultiManifestMediaProbeAsync(AppEntry.Services);

    public static Task DiagnoseCdnNetworkTransitionAsync() =>
        Integration.CdnDiagnosticTests.TestNetworkTransitionAsync(AppEntry.Services);

    public static Task DiagnoseCdnActiveStreamAsync() =>
        Integration.CdnDiagnosticTests.TestActiveStreamProbeAsync();

    public static Task DiagnoseDnsAsync() =>
        Integration.CdnDiagnosticTests.TestDnsResolutionAsync();

    public static Task TestAlternativeApiAsync() =>
        Integration.CdnDiagnosticTests.TestAlternativeApiEndpointAsync();

    public static async Task TestStreamFreshnessAsync()
    {
        await Unit.StreamFreshnessTests.TestUrlExExpireParsingAsync().ConfigureAwait(false);
        await Unit.StreamFreshnessTests.TestRefreshWithUnchangedNTokenAndValidExpireAsync().ConfigureAwait(false);
        await Unit.StreamFreshnessTests.TestProactiveStreamFreshnessOnResumeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Запускает изолированно только группу тестов и бенчмарков аудио-тракта (<see cref="TestGroups.Audio"/>).
    /// </summary>
    public static async Task RunAudioSuiteAsync(CancellationToken ct = default)
    {
        var all = TestDiscovery.GetAllOrdered();
        var audioTests = new List<TestDescriptor>(32);

        for (int i = 0; i < all.Count; i++)
        {
            var t = all[i];
            if (t.Category != TestCategory.Action && string.Equals(t.Group, TestGroups.Audio, StringComparison.Ordinal))
                audioTests.Add(t);
        }

        var runner = new TestRunner(AppEntry.Services);
        var results = await runner.RunBatchAsync(audioTests, ct).ConfigureAwait(false);

        int passed = 0, failed = 0;
        for (int i = 0; i < results.Count; i++)
        {
            if (results[i].State == TestRunState.Passed) passed++;
            else if (results[i].State == TestRunState.Failed) failed++;
        }

        Log.Info($"[AudioSuite] Completed {audioTests.Count} audio tests: {passed} passed, {failed} failed.");
    }

    public static Task TestAudioAllocationsAsync() => TestAllHotPathZeroAllocAsync();

    public static async Task TestAllHotPathZeroAllocAsync()
    {
        await Unit.HotPathAllocationTests.TestGainProcessorSimdZeroAllocAsync().ConfigureAwait(false);
        await Unit.HotPathAllocationTests.TestGainProcessorRampZeroAllocAsync().ConfigureAwait(false);
        await Unit.HotPathAllocationTests.TestGainProcessorUnityBypassZeroAllocAsync().ConfigureAwait(false);
        await Unit.HotPathAllocationTests.TestSharedPlaybackStateInterpolationAsync().ConfigureAwait(false);
        await Unit.HotPathAllocationTests.TestRingBufferWriteZeroAllocAsync().ConfigureAwait(false);
        await Unit.HotPathAllocationTests.TestRingBufferReadZeroAllocAsync().ConfigureAwait(false);
        await Unit.HotPathAllocationTests.TestRingBufferPropertiesZeroAllocAsync().ConfigureAwait(false);
        await Unit.HotPathAllocationTests.TestAudioCallbackSimulationZeroAllocAsync().ConfigureAwait(false);
        await Unit.HotPathAllocationTests.TestKWeightingFilterZeroAllocAsync().ConfigureAwait(false);
        await Unit.HotPathAllocationTests.TestSharedPlaybackStateZeroAllocAsync().ConfigureAwait(false);
        await Unit.HotPathAllocationTests.TestWebMParserLacingZeroAllocAsync().ConfigureAwait(false);
        await Unit.HotPathAllocationTests.TestMp4ParserAndDecoderSimdZeroAllocAsync().ConfigureAwait(false);
        await Unit.HotPathAllocationTests.TestOpusDecoderZeroAllocAsync().ConfigureAwait(false);
        await Unit.HotPathAllocationTests.TestAsyncCachingReadStreamRamHitZeroAllocAsync().ConfigureAwait(false);
        await Unit.HotPathAllocationTests.TestGetBufferedRangesAndCacheDirtyZeroAllocAsync().ConfigureAwait(false);
        await Unit.HotPathAllocationTests.TestEndToEndPipelineStepZeroAllocAsync().ConfigureAwait(false);
        await Unit.HotPathAllocationTests.TestAdaptiveDecoderDelayAndTimerSuspendAsync().ConfigureAwait(false);
        await Unit.HotPathAllocationTests.TestNetworkManagerAndDelayNoThrowZeroAllocAsync().ConfigureAwait(false);
        await Unit.HotPathAllocationTests.TestThreadContextSwitchesDiagnosticAsync().ConfigureAwait(false);
    }

    public static async Task BenchmarkAudioHotPathAsync()
    {
        await Unit.AudioHotPathBenchmarks.TestAudioHotPathBenchmarkAsync().ConfigureAwait(false);
        await Unit.AudioHotPathBenchmarks.TestRingBufferContentionStressAsync().ConfigureAwait(false);
    }

    public static Task DiagnosePlaylistOrderAsync(string? playlistUrl = null) =>
        Integration.PlaylistOrderTests.DiagnosePlaylistOrderAsync(AppEntry.Services, playlistUrl);

    public static async Task TestMemoryPackAsync()
    {
        await Unit.MemoryPackTests.TestAtomicFileBinaryBakRecoveryAsync().ConfigureAwait(false);
        await Unit.MemoryPackTests.TestAudioCacheBinaryRoundtripAndAllocationAsync().ConfigureAwait(false);
        await Unit.MemoryPackTests.TestSessionCacheBinaryRoundtripAsync().ConfigureAwait(false);
        await Unit.MemoryPackTests.TestSettingsRepositoryBinaryBlobAndLegacyMigrationAsync(AppEntry.Services).ConfigureAwait(false);
    }

    public static Task GenerateTestNotificationsAsync(int count = 100) =>
        Integration.NotificationTests.TestGenerateNotificationsAsync(AppEntry.Services, count);

    public static async Task TestUpdateSystemAsync()
    {
        await Unit.UpdateTests.TestCommitCountParsingAsync().ConfigureAwait(false);
        await Unit.UpdateTests.TestRollingDevReleaseExtractionAsync().ConfigureAwait(false);
        await Unit.UpdateTests.TestReleaseDtoDeserializationAsync().ConfigureAwait(false);
        await Unit.UpdateTests.TestCleanupPassAsync().ConfigureAwait(false);
        await Unit.UpdateTests.TestFrameworkGroupsAndFilterPerfAsync().ConfigureAwait(false);
    }

    public static Task ClearNotificationsAsync() =>
        Integration.NotificationTests.TestClearAllNotificationsAsync(AppEntry.Services);
}