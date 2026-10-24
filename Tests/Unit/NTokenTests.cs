using System.Diagnostics;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Core.Youtube.Bridge.NToken;
using LMP.Tests.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Tests.Unit;

/// <summary>
/// Интеграционные тесты и бенчмарки подсистемы дешифрации N-Token.
/// Проверяют взаимодействие с QuickJS runtime, кэширование, идемпотентность и AST Solver.
/// </summary>
public static class NTokenTests
{
    private static string TestToken => TestConfig.Get().NToken.TestToken;

    // ==========================================
    //          LIVE INTEGRATION TESTS
    // ==========================================

    [TestMethod(TestCategory.Integration, "N-Token Live Decryption",
        Group = TestGroups.Cipher, Order = 1, RequiresNetwork = true, TimeoutSeconds = 60)]
    public static async Task TestLiveDecryptionAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var decryptor = services.GetRequiredService<NTokenDecryptor>();
        var token = TestToken;

        var sw = Stopwatch.StartNew();
        var result = await decryptor.DecryptAsync(token, ct).ConfigureAwait(false);
        sw.Stop();

        TestAssert.NotNullOrEmpty(result, "Decryption returned empty string");
        TestAssert.False(string.Equals(result, token, StringComparison.Ordinal), "Decryption returned unchanged token");
        TestAssert.False(result.Contains("undefined", StringComparison.Ordinal), "Result contains 'undefined'");
        TestAssert.False(result.Contains("null", StringComparison.Ordinal), "Result contains 'null'");
        TestAssert.True(result.Length >= 5, $"Result suspiciously short: '{result}'");
        TestAssert.True(result.Length <= 60, $"Result suspiciously long: {result.Length} chars");

        for (int i = 0; i < result.Length; i++)
        {
            char c = result[i];
            TestAssert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_', $"Result contains invalid character '{c}'");
        }

        Log.Info($"[NToken] Decrypted in {sw.ElapsedMilliseconds}ms: '{token}' -> '{result}'");
    }

    [TestMethod(TestCategory.Integration, "N-Token Memory Cache Hit (<5ms)",
        Group = TestGroups.Cipher, Order = 2, RequiresNetwork = true, TimeoutSeconds = 30)]
    public static async Task TestCacheHitAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var decryptor = services.GetRequiredService<NTokenDecryptor>();
        var token = TestToken;

        var first = await decryptor.DecryptAsync(token, ct).ConfigureAwait(false);
        TestAssert.False(string.Equals(first, token, StringComparison.Ordinal), "Warmup decryption failed");

        var sw = Stopwatch.StartNew();
        var second = await decryptor.DecryptAsync(token, ct).ConfigureAwait(false);
        sw.Stop();

        TestAssert.Equal(first, second, "Cache returned inconsistent result");
        TestAssert.True(sw.ElapsedMilliseconds < 5, $"Cache hit too slow: {sw.ElapsedMilliseconds}ms (expected <5ms)");

        Log.Info($"[NToken] Cache hit verified in {sw.ElapsedMilliseconds}ms");
    }

    [TestMethod(TestCategory.Integration, "N-Token Idempotency Guard",
        Group = TestGroups.Cipher, Order = 3, RequiresNetwork = true, TimeoutSeconds = 30)]
    public static async Task TestIdempotencyAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var decryptor = services.GetRequiredService<NTokenDecryptor>();
        var token = TestToken;

        var decrypted = await decryptor.DecryptAsync(token, ct).ConfigureAwait(false);
        TestAssert.False(string.Equals(decrypted, token, StringComparison.Ordinal), "Warmup decryption failed");

        var sw = Stopwatch.StartNew();
        var again = await decryptor.DecryptAsync(decrypted, ct).ConfigureAwait(false);
        sw.Stop();

        TestAssert.Equal(decrypted, again, "Idempotency guard returned modified value");
        TestAssert.True(sw.ElapsedMilliseconds < 5, $"Idempotency check too slow: {sw.ElapsedMilliseconds}ms");

        Log.Info($"[NToken] Idempotency guard OK in {sw.ElapsedMilliseconds}ms");
    }

    [TestMethod(TestCategory.Integration, "AST Solver & QuickJS Persistent Context",
        Group = TestGroups.Cipher, Order = 4, RequiresNetwork = false, TimeoutSeconds = 60)]
    public static async Task TestAstSolverWithPersistentContextAsync(CancellationToken ct = default)
    {
        var config = TestConfig.Get().PlayerVersions;
        PlayerContext? context = null;
        string? usedVersion = null;

        for (int i = 0; i < config.Versions.Length; i++)
        {
            var version = config.Versions[i];
            context = PlayerContext.LoadFromCacheNoExpiry(version);
            if (context is not null)
            {
                usedVersion = version;
                break;
            }
        }

        if (context is null)
        {
            TestAssert.Skip($"No cached player versions found in {G.Folder.NTokenCache}");
        }

        Log.Info($"[AST] Testing player version: {usedVersion} ({context!.BaseJs.Length / 1024}KB)");

        var script = context.GetOrPrepareScript(() => YoutubeAstSolver.PreprocessPlayer(context.BaseJs));
        TestAssert.True(script.Contains("_result.n", StringComparison.Ordinal), "Preprocessed script missing _result.n");
        TestAssert.True(script.Contains("_result.sig", StringComparison.Ordinal), "Preprocessed script missing _result.sig");

        var fixedManager = new FixedPlayerContextManager(context);
        using var jsService = new JsDecryptionService(fixedManager);

        await jsService.EnsureInitializedAsync(ct).ConfigureAwait(false);

        var testToken = TestToken;
        var decryptedN = await jsService.CallAsync("n", testToken).ConfigureAwait(false);

        TestAssert.NotNullOrEmpty(decryptedN, "N-token decryption returned null/empty");
        TestAssert.False(string.Equals(decryptedN, testToken, StringComparison.Ordinal), "N-token unchanged after decryption");
        TestAssert.False(decryptedN.Contains("undefined", StringComparison.Ordinal), "Result contains 'undefined'");

        var testSig = TestConfig.Get().SigCipher.TestSignature;
        var decryptedSig = await jsService.CallAsync("sig", testSig).ConfigureAwait(false);

        TestAssert.NotNullOrEmpty(decryptedSig, "Sig decryption returned null/empty");
        TestAssert.False(string.Equals(decryptedSig, testSig, StringComparison.Ordinal), "Sig unchanged after decryption");

        Log.Info($"[AST] ✓ Player version {usedVersion} verified successfully");
    }

    [TestMethod(TestCategory.Integration, "Cached Player Versions Compatibility",
        Group = TestGroups.Cipher, Order = 5, RequiresNetwork = false, TimeoutSeconds = 120)]
    public static async Task TestPlayerVersionCompatibilityAsync(CancellationToken ct = default)
    {
        var config = TestConfig.Get().PlayerVersions;

        if (config.Versions.Length == 0)
        {
            TestAssert.Skip("No player versions configured in test-config.json");
        }

        int passed = 0;

        for (int i = 0; i < config.Versions.Length; i++)
        {
            var version = config.Versions[i];
            var context = PlayerContext.LoadFromCacheNoExpiry(version);
            if (context is null)
            {
                Log.Warn($"  ⊘ Skipped: no cached base.js for {version}");
                continue;
            }

            var fixedManager = new FixedPlayerContextManager(context);
            using var jsService = new JsDecryptionService(fixedManager);

            await jsService.EnsureInitializedAsync(ct).ConfigureAwait(false);

            var nResult = await jsService.CallAsync("n", TestToken).ConfigureAwait(false);
            TestAssert.NotNullOrEmpty(nResult, $"Version {version}: N-token returned empty");
            TestAssert.False(string.Equals(nResult, TestToken, StringComparison.Ordinal), $"Version {version}: N-token unchanged");

            if (config.NTokenTestCases.TryGetValue(version, out var cases) && cases.Length > 0)
            {
                for (int j = 0; j < cases.Length; j++)
                {
                    var tc = cases[j];
                    var result = await jsService.CallAsync("n", tc.Encrypted).ConfigureAwait(false);
                    TestAssert.Equal(tc.Expected, result, $"Version {version}: Case {tc.Description ?? tc.Encrypted} failed");
                }
            }

            passed++;
        }

        if (passed == 0)
        {
            TestAssert.Skip("None of the configured player versions could be loaded from local cache");
        }

        Log.Info($"[NToken] Compatibility: {passed} player version(s) passed");
    }

    // ==========================================
    //                BENCHMARK
    // ==========================================

    [TestMethod(TestCategory.Benchmark, "N-Token QuickJS Persistent Context vs Cache",
        Group = TestGroups.Cipher, Order = 10, RequiresNetwork = true, TimeoutSeconds = 120)]
    public static async Task BenchmarkAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var decryptor = services.GetRequiredService<NTokenDecryptor>();
        var jsService = services.GetRequiredService<JsDecryptionService>();

        await decryptor.DecryptAsync(TestToken, ct).ConfigureAwait(false);

        var iterations = TestConfig.Get().NToken.BenchmarkIterations;
        var tokens = new string[iterations];
        for (int i = 0; i < iterations; i++)
            tokens[i] = $"BenchTkn{i:D5}AA__"[..15];

        // 1. Cache Miss (прямой вызов QuickJS через persistent context)
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < tokens.Length; i++)
            await jsService.CallAsync("n", tokens[i]).ConfigureAwait(false);
        sw.Stop();

        var avgMiss = (double)sw.ElapsedMilliseconds / iterations;
        Log.Info($"[Benchmark] Cache miss (QuickJS): {avgMiss:F3}ms avg ({iterations} runs)");

        // 2. Cache Hit (через NTokenDecryptor)
        for (int i = 0; i < tokens.Length; i++)
            await decryptor.DecryptAsync(tokens[i], ct).ConfigureAwait(false);

        sw.Restart();
        for (int i = 0; i < tokens.Length; i++)
            await decryptor.DecryptAsync(tokens[i], ct).ConfigureAwait(false);
        sw.Stop();

        var avgHit = (double)sw.ElapsedMilliseconds / iterations;
        Log.Info($"[Benchmark] Cache hit: {avgHit:F3}ms avg");

        TestAssert.True(avgHit < 1.0, $"Cache hit too slow: {avgHit:F3}ms (expected <1ms)");
        TestAssert.True(avgMiss < 5.0, $"QuickJS call too slow: {avgMiss:F3}ms (expected <5ms)");
    }
}

internal sealed class FixedPlayerContextManager : PlayerContextManager
{
    private readonly PlayerContext _context;

    public FixedPlayerContextManager(PlayerContext context) : base(null!) =>
        _context = context;

    public override Task<PlayerContext> GetOrLoadAsync(CancellationToken ct = default) =>
        Task.FromResult(_context);
}