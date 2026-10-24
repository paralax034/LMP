using System.Diagnostics;
using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Interfaces;
using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

/// <summary>
/// Бенчмарки скорости предварительного EBU R128 сканирования для Opus и AAC файлов.
/// При отсутствии полностью закэшированных треков тест пропускается через <see cref="TestAssert.Skip"/>.
/// </summary>
public static class PreScanBenchmarks
{
    private const int WarmupRuns = 1;
    private const int MeasureRuns = 2;
    private const float TargetLufs = -14f;
    private const float MaxGain = 3.0f;

    [TestMethod(TestCategory.Benchmark, "EBU R128 Isolated Pre-Scan Timing",
        Group = TestGroups.Audio, Order = 1, TimeoutSeconds = 60)]
    public static async Task BenchmarkIsolatedPreScanAsync()
    {
        var candidates = CollectCandidates(AudioSourceFactory.GlobalCache);
        if (candidates.Count == 0)
        {
            TestAssert.Skip("No fully cached tracks found in AudioCache. Play tracks to enable pre-scan benchmarks.");
        }

        for (int i = 0; i < candidates.Count; i++)
        {
            var (entry, filePath) = candidates[i];

            for (int w = 0; w < WarmupRuns; w++)
                await RunTimedScanAsync(filePath, entry.Codec).ConfigureAwait(false);

            var times = new long[MeasureRuns];
            for (int m = 0; m < MeasureRuns; m++)
                times[m] = await RunTimedScanAsync(filePath, entry.Codec).ConfigureAwait(false);

            Log.Info($"[Benchmark] PreScan {entry.Codec} ({entry.Bitrate}kbps): avg={times.Average():F0}ms");
        }
    }

    [TestMethod(TestCategory.Benchmark, "EBU R128 Pre-Scan 30s vs 60s Scope",
        Group = TestGroups.Audio, Order = 2, TimeoutSeconds = 60)]
    public static async Task BenchmarkScopeComparisonAsync()
    {
        var candidates = CollectCandidates(AudioSourceFactory.GlobalCache)
            .FindAll(c => c.Entry.Codec == AudioCodec.Opus);

        if (candidates.Count == 0)
        {
            TestAssert.Skip("No Opus tracks found in cache — skipping scope comparison");
        }

        var (entry, filePath) = candidates[0];

        long ms30 = await RunTimedScanWithScopeAsync(filePath, entry.Codec, scanSeconds: 30f).ConfigureAwait(false);
        long ms60 = await RunTimedScanWithScopeAsync(filePath, entry.Codec, scanSeconds: 60f).ConfigureAwait(false);

        double ratio = ms60 > 0 ? (double)ms30 / ms60 : 1.0;
        Log.Info($"[Benchmark] Scope: 30s={ms30}ms, 60s={ms60}ms, ratio={ratio:F2}x");
    }

    private static List<(AudioCacheEntry Entry, string FilePath)> CollectCandidates(AudioCacheManager? cacheManager)
    {
        if (cacheManager is null) return [];

        var result = new List<(AudioCacheEntry, string)>(4);

        foreach (var entry in cacheManager.GetAllCompleteEntries())
        {
            var path = cacheManager.GetCachePath(entry.CacheKey);
            if (File.Exists(path))
            {
                result.Add((entry, path));
                if (result.Count >= 2) break;
            }
        }

        return result;
    }

    private static Task<long> RunTimedScanAsync(string filePath, AudioCodec codec) =>
        RunTimedScanWithScopeAsync(filePath, codec, scanSeconds: 30f);

    private static async Task<long> RunTimedScanWithScopeAsync(string filePath, AudioCodec codec, float scanSeconds)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var sw = Stopwatch.StartNew();

        try
        {
            await IsolatedPreScanHelper.RunAsync(
                filePath,
                codec,
                TargetLufs,
                MaxGain,
                scanSeconds,
                cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"[Benchmark] PreScan error: {ex.Message}");
            throw;
        }

        sw.Stop();
        return sw.ElapsedMilliseconds;
    }
}