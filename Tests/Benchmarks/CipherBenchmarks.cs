using System.Diagnostics;
using LMP.Core.Youtube.Bridge.NToken;
using LMP.Tests.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Tests.Benchmarks;

/// <summary>
/// Throughput benchmarks for QuickJS persistent context vs in-memory decryption cache.
/// </summary>
public static class CipherBenchmarks
{
    [TestMethod(TestCategory.Benchmark, "N-Token QuickJS Persistent Context vs Cache",
        Group = TestGroups.Cipher, Order = 10, RequiresNetwork = true, TimeoutSeconds = 120)]
    public static async Task BenchmarkNTokenCacheVsQuickJsAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var decryptor = services.GetRequiredService<NTokenDecryptor>();
        var testToken = TestConfig.Get().NToken.TestToken;
        await decryptor.DecryptAsync(testToken, ct).ConfigureAwait(false);

        var iterations = TestConfig.Get().NToken.BenchmarkIterations;
        var tokens = new string[iterations];
        long nonce = Stopwatch.GetTimestamp() & 0xFFFF;
        for (int i = 0; i < iterations; i++)
            tokens[i] = $"B{nonce:X4}{i:D5}AA___"[..15];

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < tokens.Length; i++)
            await decryptor.DecryptAsync(tokens[i], ct).ConfigureAwait(false);
        sw.Stop();

        var avgMiss = sw.Elapsed.TotalMilliseconds / iterations;
        Log.Info($"[Benchmark] Cache miss (QuickJS): {avgMiss:F3}ms avg ({iterations} runs)");

        sw.Restart();
        for (int i = 0; i < tokens.Length; i++)
            await decryptor.DecryptAsync(tokens[i], ct).ConfigureAwait(false);
        sw.Stop();

        var avgHit = sw.Elapsed.TotalMilliseconds / iterations;
        Log.Info($"[Benchmark] Cache hit: {avgHit:F3}ms avg");

        TestAssert.True(avgHit < 1.0, $"Cache hit too slow: {avgHit:F3}ms (expected <1ms)");
        TestAssert.True(avgMiss < 10.0, $"QuickJS call too slow: {avgMiss:F3}ms (expected <10ms)");
    }
}