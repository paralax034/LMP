using System.Diagnostics;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Core.Youtube.Bridge.NToken;
using LMP.Core.Youtube.Bridge.SigCipher;
using LMP.Tests.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Tests.Integration;

/// <summary>
/// Live and cached-artifact integration tests for YouTube deciphering (N-Token, SigCipher, AST Solver, QuickJS).
/// </summary>
public static class CipherTests
{
    private static string TestToken => TestConfig.Get().NToken.TestToken;

    [TestMethod(TestCategory.Integration, "N-Token Live Decryption",
        Group = TestGroups.Cipher, Order = 1, RequiresNetwork = true, TimeoutSeconds = 60)]
    public static async Task TestNTokenLiveDecryptionAsync(IServiceProvider services, CancellationToken ct = default)
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
    public static async Task TestNTokenCacheHitAsync(IServiceProvider services, CancellationToken ct = default)
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
    public static async Task TestNTokenIdempotencyAsync(IServiceProvider services, CancellationToken ct = default)
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
        var candidates = ResolveCandidatePlayerVersions();
        PlayerContext? context = null;
        string? usedVersion = null;

        for (int i = 0; i < candidates.Length; i++)
        {
            var version = candidates[i];
            context = PlayerContext.LoadFromCacheNoExpiry(version, requireRawBaseJs: true)
                   ?? PlayerContext.LoadFromCacheNoExpiry(version, requireRawBaseJs: false);
            if (context is not null)
            {
                usedVersion = version;
                break;
            }
        }

        if (context is null)
            TestAssert.Skip($"No cached player versions found in {G.Folder.NTokenCache}");

        Log.Info($"[AST] Testing player version: {usedVersion} (raw BaseJs={context!.BaseJs.Length / 1024}KB)");

        var script = !string.IsNullOrEmpty(context.BaseJs)
            ? YoutubeAstSolver.PreprocessPlayer(context.BaseJs)
            : context.GetOrPrepareScript(() => YoutubeAstSolver.PreprocessPlayer(context.BaseJs));

        TestAssert.True(script.Contains("_result.n", StringComparison.Ordinal), "Preprocessed script missing _result.n");
        TestAssert.True(script.Contains("_result.sig", StringComparison.Ordinal), "Preprocessed script missing _result.sig");

        var freshContext = new PlayerContext(usedVersion!, string.Empty, preprocessedJs: script, sts: context.Sts, clientVersion: context.ClientVersion);
        var fixedManager = new FixedPlayerContextManager(freshContext);
        using var jsService = new JsDecryptionService(fixedManager);

        await jsService.EnsureInitializedAsync(ct).ConfigureAwait(false);

        var testToken = TestToken;
        var decryptedN = await jsService.CallAsync("n", testToken, ct).ConfigureAwait(false);

        TestAssert.NotNullOrEmpty(decryptedN, "N-token decryption returned null/empty");
        TestAssert.False(string.Equals(decryptedN, testToken, StringComparison.Ordinal), "N-token unchanged after decryption");
        TestAssert.False(decryptedN.Contains("undefined", StringComparison.Ordinal), "Result contains 'undefined'");

        var testSig = TestConfig.Get().SigCipher.TestSignature;
        var decryptedSig = await jsService.CallAsync("sig", testSig, ct).ConfigureAwait(false);

        TestAssert.NotNullOrEmpty(decryptedSig, "Sig decryption returned null/empty");
        TestAssert.False(string.Equals(decryptedSig, testSig, StringComparison.Ordinal), "Sig unchanged after decryption");

        Log.Info($"[AST] ✓ Player version {usedVersion} verified successfully");
    }

    [TestMethod(TestCategory.Integration, "Cached Player Versions Compatibility",
        Group = TestGroups.Cipher, Order = 5, RequiresNetwork = false, TimeoutSeconds = 120)]
    public static async Task TestPlayerVersionCompatibilityAsync(CancellationToken ct = default)
    {
        var config = TestConfig.Get().PlayerVersions;
        var versions = ResolveCandidatePlayerVersions();

        if (versions.Length == 0)
            TestAssert.Skip($"No player versions found in test-config.json or {G.Folder.NTokenCache}");

        int passed = 0;

        for (int i = 0; i < versions.Length; i++)
        {
            var version = versions[i];
            var context = PlayerContext.LoadFromCacheNoExpiry(version);
            if (context is null)
            {
                Log.Warn($"  ⊘ Skipped: no cached player artifacts for {version}");
                continue;
            }

            var fixedManager = new FixedPlayerContextManager(context);
            using var jsService = new JsDecryptionService(fixedManager);

            await jsService.EnsureInitializedAsync(ct).ConfigureAwait(false);

            var nResult = await jsService.CallAsync("n", TestToken, ct).ConfigureAwait(false);
            TestAssert.NotNullOrEmpty(nResult, $"Version {version}: N-token returned empty");
            TestAssert.False(string.Equals(nResult, TestToken, StringComparison.Ordinal), $"Version {version}: N-token unchanged");

            if (config.NTokenTestCases.TryGetValue(version, out var cases) && cases.Length > 0)
            {
                for (int j = 0; j < cases.Length; j++)
                {
                    var tc = cases[j];
                    var result = await jsService.CallAsync("n", tc.Encrypted, ct).ConfigureAwait(false);
                    TestAssert.Equal(tc.Expected, result, $"Version {version}: Case {tc.Description ?? tc.Encrypted} failed");
                }
            }

            passed++;
        }

        if (passed == 0)
            TestAssert.Skip("None of the candidate player versions could be loaded from local cache");

        Log.Info($"[NToken] Compatibility: {passed} player version(s) passed");
    }

    [TestMethod(TestCategory.Integration, "Signature Cipher Live Decryption",
        Group = TestGroups.Cipher, Order = 6, RequiresNetwork = true, TimeoutSeconds = 60)]
    public static async Task TestSigCipherLiveDecryptionAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var decryptor = services.GetRequiredService<SigCipherDecryptor>();
        var testSig = TestConfig.Get().SigCipher.TestSignature;

        var sw = Stopwatch.StartNew();
        var result = await decryptor.DecipherAsync(testSig, ct).ConfigureAwait(false);
        sw.Stop();

        TestAssert.NotNullOrEmpty(result, "Decryption returned empty string");
        TestAssert.False(string.Equals(result, testSig, StringComparison.Ordinal), "Decryption returned unchanged signature");
        TestAssert.True(result.Length > 50, $"Result length suspicious: {result.Length}");

        var inputChars = new HashSet<char>(testSig);
        for (int i = 0; i < result.Length; i++)
            TestAssert.True(inputChars.Contains(result[i]), $"Result contains unexpected character '{result[i]}'");

        int removedCount = testSig.Length - result.Length;
        TestAssert.True(removedCount is >= 0 and <= 10, $"Removed chars count out of bounds: {removedCount}");

        sw.Restart();
        var cached = await decryptor.DecipherAsync(testSig, ct).ConfigureAwait(false);
        sw.Stop();

        TestAssert.Equal(result, cached, "Cache returned inconsistent result");
        TestAssert.True(sw.ElapsedMilliseconds < 5, $"Cache hit too slow: {sw.ElapsedMilliseconds}ms");

        Log.Info($"[SigCipher] Live decipher verified in {sw.ElapsedMilliseconds}ms (removed {removedCount} chars)");
    }

    [TestMethod(TestCategory.Integration, "Concurrent N-Token & Signature Decryption",
        Group = TestGroups.Cipher, Order = 7, RequiresNetwork = true, TimeoutSeconds = 60)]
    public static async Task TestConcurrentNAndSigAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var jsService = services.GetRequiredService<JsDecryptionService>();
        await jsService.EnsureInitializedAsync(ct).ConfigureAwait(false);

        var nToken = TestToken;
        var sigToken = TestConfig.Get().SigCipher.TestSignature;

        var sw = Stopwatch.StartNew();
        var nTask = jsService.CallAsync("n", nToken, ct).AsTask();
        var sigTask = jsService.CallAsync("sig", sigToken, ct).AsTask();

        await Task.WhenAll(nTask, sigTask).ConfigureAwait(false);
        sw.Stop();

        TestAssert.True(sigTask.Result != sigToken || nTask.Result != nToken,
            "Both n and sig returned unchanged — QuickJS execution failed");

        Log.Info($"[SigCipher] Concurrent calls completed in {sw.ElapsedMilliseconds}ms");
    }

    private static string[] ResolveCandidatePlayerVersions()
    {
        var configured = TestConfig.Get().PlayerVersions.Versions;
        var cached = PlayerContext.GetCachedVersions();

        if (configured.Length == 0) return cached;
        if (cached.Length == 0) return configured;

        var combined = new List<string>(configured.Length + cached.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < cached.Length; i++)
        {
            if (seen.Add(cached[i]))
                combined.Add(cached[i]);
        }

        for (int i = 0; i < configured.Length; i++)
        {
            if (seen.Add(configured[i]))
                combined.Add(configured[i]);
        }

        return [.. combined];
    }

    private sealed class FixedPlayerContextManager : PlayerContextManager
    {
        private readonly PlayerContext _context;

        public FixedPlayerContextManager(PlayerContext context)
            : base(static () => Core.Audio.Http.SharedHttpClient.Instance) =>
            _context = context;

        public override Task<PlayerContext> GetOrLoadAsync(CancellationToken ct = default) =>
            Task.FromResult(_context);
    }
}