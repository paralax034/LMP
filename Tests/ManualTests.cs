using System.Diagnostics;
using LMP.Tests.Framework;

namespace LMP.Tests;

/// <summary>
/// Точка входа для запуска тестов из кода (F10 в Debug режиме).
/// </summary>
public static class ManualTests
{
    /// <summary>
    /// Запускает все обнаруженные тесты: Unit → Integration → Benchmark.
    /// </summary>
    public static async Task RunAllAsync()
    {
        var sw = Stopwatch.StartNew();

        Console.WriteLine("\n" + new string('=', 70));
        Console.WriteLine("  LMP TEST SUITE");
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
                $"  {icon} {descriptor.DisplayName} ({result.DurationFormatted})" +
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
            var label = category switch
            {
                TestCategory.Unit => "▶ UNIT TESTS",
                TestCategory.Integration => "▶ INTEGRATION TESTS (network required)",
                TestCategory.Benchmark => "▶ BENCHMARKS",
                _ => $"▶ {category}"
            };
            Console.WriteLine($"\n{label}\n");
            await runner.RunBatchAsync(tests);
        }

        sw.Stop();

        Console.WriteLine("\n" + new string('=', 70));
        Console.WriteLine($"  RESULTS: {passed} passed, {failed} failed, {skipped} skipped " +
                          $"({sw.Elapsed.TotalSeconds:F1}s)");
        Console.WriteLine(new string('=', 70) + "\n");
    }

    /// <summary>Быстрый запуск только NToken integration теста.</summary>
    public static Task TestNTokenAsync() =>
        Unit.NTokenTests.TestLiveDecryptionAsync(AppEntry.Services);

    /// <summary>Быстрый запуск только SigCipher integration теста.</summary>
    public static Task TestSigCipherAsync() =>
        Unit.SigCipherTests.TestLiveDecryptionAsync(AppEntry.Services);

    /// <summary>Быстрый запуск AST Solver теста (offline, нужен кэш плеера).</summary>
    public static Task TestAstSolverAsync() =>
        Unit.NTokenTests.TestAstSolverWithPersistentContextAsync();

    /// <summary>Полный pipeline тест (сеть + дешифрация + стриминг).</summary>
    public static Task TestFullPipelineAsync(string videoId = "dQw4w9WgXcQ") =>
        Integration.StreamPipelineTests.TestFullPipelineInternalAsync(AppEntry.Services, videoId);

    /// <summary>Пошаговая диагностика PoToken pipeline (запускает все 5 шагов).</summary>
    public static Task TestPoTokenAsync() =>
        Integration.PoTokenTests.TestFullPipelineAsync();

    /// <summary>Только шаг 3 — QuickJS Snapshot (самый важный, выявляет async issues).</summary>
    public static Task TestPoTokenSnapshotAsync() =>
        Integration.PoTokenTests.TestQuickJsSnapshotAsync();

    /// <summary>Диагностика CDN: TCP, path discrimination, zapret coverage.</summary>
    public static Task DiagnoseCdnAsync() =>
        Integration.CdnDiagnosticTests.TestLiveFullChainAsync(AppEntry.Services);

    /// <summary>Проверка конфигурации zapret без сети.</summary>
    public static Task AuditZapretConfigAsync() =>
        Integration.CdnDiagnosticTests.TestZapretConfigAuditAsync(AppEntry.Services);

    /// <summary>Тест проблемного CDN узла 74.125.104.73.</summary>
    public static Task TestProblemCdnAsync() =>
        Integration.CdnDiagnosticTests.TestProblemIpDirectAsync(AppEntry.Services);

    /// <summary>Multi-manifest CDN discovery: какие хосты YouTube отдаёт и какие заблокированы.</summary>
    public static Task DiagnoseCdnMultiManifestAsync() =>
        Integration.CdnDiagnosticTests.TestMultiManifestMediaProbeAsync(AppEntry.Services);

    /// <summary>Сравнение CDN доступности между сетями. Запустить дважды (до/после переключения).</summary>
    public static Task DiagnoseCdnNetworkTransitionAsync() =>
        Integration.CdnDiagnosticTests.TestNetworkTransitionAsync(AppEntry.Services);

    /// <summary>Проверка media path для недавно использованных стримов.</summary>
    public static Task DiagnoseCdnActiveStreamAsync() =>
        Integration.CdnDiagnosticTests.TestActiveStreamProbeAsync();

    /// <summary>DNS-диагностика: резолвятся ли youtube.com и CDN хосты.</summary>
    public static Task DiagnoseDnsAsync() =>
        Integration.CdnDiagnosticTests.TestDnsResolutionAsync();

    /// <summary>Проверка альтернативного API endpoint через googleapis.com (bypass ТСПУ без Zapret).</summary>
    public static Task TestAlternativeApiAsync() =>
        Integration.CdnDiagnosticTests.TestAlternativeApiEndpointAsync();

    /// <summary>Быстрый запуск тестов времени жизни URL и упреждающего refresh.</summary>
    public static async Task TestStreamFreshnessAsync()
    {
        await Unit.StreamFreshnessTests.TestUrlExExpireParsingAsync();
        await Unit.StreamFreshnessTests.TestRefreshWithUnchangedNTokenAndValidExpireAsync();
        await Unit.StreamFreshnessTests.TestProactiveStreamFreshnessOnResumeAsync();
    }

    /// <summary>Запуск всех audio hot-path allocation тестов.</summary>
    public static async Task TestAudioAllocationsAsync()
    {
        await Unit.HotPathAllocationTests.TestGainProcessorSimdZeroAllocAsync();
        await Unit.HotPathAllocationTests.TestGainProcessorRampZeroAllocAsync();
        await Unit.HotPathAllocationTests.TestGainProcessorUnityBypassZeroAllocAsync();
        await Unit.HotPathAllocationTests.TestRingBufferWriteZeroAllocAsync();
        await Unit.HotPathAllocationTests.TestRingBufferReadZeroAllocAsync();
        await Unit.HotPathAllocationTests.TestRingBufferPropertiesZeroAllocAsync();
        await Unit.HotPathAllocationTests.TestAudioCallbackSimulationZeroAllocAsync();
    }

    /// <summary>Запуск audio бенчмарков (throughput + latency + alloc check).</summary>
    public static async Task BenchmarkAudioHotPathAsync()
    {
        await Unit.AudioHotPathBenchmarks.TestAudioHotPathBenchmarkAsync();
        await Unit.AudioHotPathBenchmarks.TestRingBufferContentionStressAsync();
    }

    /// <summary>
    /// Запуск комплексной диагностики последовательности треков в плейлистах и лайках.
    /// Позволяет выявить рассинхронизацию между SQLite, L1-кэшем и внешним YouTube API.
    /// </summary>
    /// <param name="playlistUrl">URL плейлиста YouTube или null для проверки локального Liked плейлиста.</param>
    /// <returns>Асинхронная задача выполнения диагностики.</returns>
    public static Task DiagnosePlaylistOrderAsync(string? playlistUrl = null) =>
        Integration.PlaylistOrderTests.DiagnosePlaylistOrderAsync(AppEntry.Services, playlistUrl);

    /// <summary>
    /// Запуск тестов сериализации, миграции и замеров аллокаций MemoryPack.
    /// </summary>
    public static async Task TestMemoryPackAsync()
    {
        await Unit.MemoryPackTests.TestAtomicFileBinaryBakRecoveryAsync();
        await Unit.MemoryPackTests.TestAudioCacheBinaryRoundtripAndAllocationAsync();
        await Unit.MemoryPackTests.TestSessionCacheBinaryRoundtripAsync();
        await Unit.MemoryPackTests.TestSettingsRepositoryBinaryBlobAndLegacyMigrationAsync(AppEntry.Services);
    }
}