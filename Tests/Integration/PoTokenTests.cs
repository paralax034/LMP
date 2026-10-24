using System.Diagnostics;
using LMP.Core.Audio.Http;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Core.Youtube.Bridge.PoToken;
using LMP.Tests.Framework;

namespace LMP.Tests.Integration;

/// <summary>
/// Интеграционные тесты генерации BotGuard PoToken.
/// Основные тесты с проверками включены в автоматический прогон,
/// а пошаговые дамперы вынесены в категорию Diagnostic.
/// </summary>
public static class PoTokenTests
{
    // ==========================================
    //        AUTOMATED INTEGRATION TESTS
    // ==========================================

    [TestMethod(TestCategory.Integration, "BotGuard PoToken Mint & Length Invariant",
        Group = TestGroups.Cipher, Order = 1, RequiresNetwork = true, TimeoutSeconds = 90)]
    public static async Task TestMintTokenLengthAsync()
    {
        using var service = new BotGuardService(SharedHttpClient.Instance);
        const string videoId = "dQw4w9WgXcQ";

        var sw = Stopwatch.StartNew();
        var token = await service.MintForVideoAsync(videoId).ConfigureAwait(false);
        sw.Stop();

        TestAssert.NotNullOrEmpty(token, "MintForVideoAsync returned null/empty token");
        TestAssert.True(token.Length >= 80, $"PoToken too short: {token.Length} chars (expected >=80)");
        TestAssert.True(token.Length <= 300, $"PoToken too long: {token.Length} chars (expected <=300)");
        TestAssert.False(token.Contains('+') || token.Contains('/'), "Token contains standard base64 instead of base64url");

        Log.Info($"[PoToken] ✓ Content-bound token minted: {token.Length} chars in {sw.ElapsedMilliseconds}ms");
    }

    [TestMethod(TestCategory.Integration, "PoToken Full Pipeline Resolution",
        Group = TestGroups.Cipher, Order = 2, RequiresNetwork = true, TimeoutSeconds = 90)]
    public static async Task TestFullPipelineAsync()
    {
        using var service = new BotGuardService(SharedHttpClient.Instance);
        var visitorId = LMP.Core.Youtube.Utils.YoutubeClientUtils.VisitorData;

        var sw = Stopwatch.StartNew();
        var token = await service.GenerateAsync(visitorId).ConfigureAwait(false);
        sw.Stop();

        TestAssert.NotNullOrEmpty(token, "Full pipeline returned null/empty PoToken");
        TestAssert.True(token.Length >= 100, $"PoToken suspiciously short: {token.Length} chars");
        TestAssert.False(token.Contains('+') || token.Contains('/') || token.Contains('='), "PoToken must be Base64Url encoded");

        Log.Info($"[PoToken] ✓ Full pipeline verified in {sw.ElapsedMilliseconds}ms");
    }

    // ==========================================
    //           DIAGNOSTIC STEP PROBES
    // ==========================================

    [TestMethod(TestCategory.Diagnostic, "BotGuard Step 1 — WAA Challenge Fetch",
        Group = TestGroups.Cipher, Order = 10, RequiresNetwork = true, TimeoutSeconds = 30)]
    public static async Task TestWaaChallengeAsync()
    {
        var sw = Stopwatch.StartNew();
        var challenge = await WaaClient.FetchChallengeAsync(SharedHttpClient.Instance).ConfigureAwait(false);
        sw.Stop();

        TestAssert.NotNull(challenge, "FetchChallengeAsync returned null");
        TestAssert.NotNullOrEmpty(challenge.Program);
        TestAssert.NotNullOrEmpty(challenge.GlobalName);
        TestAssert.True(challenge.HasScript, "Challenge missing both ScriptUrl and InlineScript");

        Log.Info($"[PoToken] Challenge resolved in {sw.ElapsedMilliseconds}ms (GlobalName='{challenge.GlobalName}')");
    }

    [TestMethod(TestCategory.Diagnostic, "BotGuard Step 2 — bg.js Script Download",
        Group = TestGroups.Cipher, Order = 11, RequiresNetwork = true, TimeoutSeconds = 30)]
    public static async Task TestBgJsDownloadAsync()
    {
        var challenge = await WaaClient.FetchChallengeAsync(SharedHttpClient.Instance).ConfigureAwait(false);
        TestAssert.NotNull(challenge);

        var script = await ResolveBgScriptAsync(challenge).ConfigureAwait(false);
        TestAssert.NotNullOrEmpty(script);
        TestAssert.True(script.Length > 10_000, $"bg.js suspiciously small: {script.Length} bytes");

        Log.Info($"[PoToken] bg.js downloaded: {script.Length / 1024}KB");
    }

    [TestMethod(TestCategory.Diagnostic, "BotGuard Step 3 — QuickJS Snapshot Execution",
        Group = TestGroups.Cipher, Order = 12, RequiresNetwork = true, TimeoutSeconds = 60)]
    public static async Task TestQuickJsSnapshotAsync()
    {
        var challenge = await WaaClient.FetchChallengeAsync(SharedHttpClient.Instance).ConfigureAwait(false);
        TestAssert.NotNull(challenge);

        var bgScript = await ResolveBgScriptAsync(challenge).ConfigureAwait(false);
        var handle = QuickJsNative.CreateContext("void 0;");
        TestAssert.True(handle != IntPtr.Zero, "Failed to create QuickJS context");

        try
        {
            var bgEvalRes = QuickJsNative.Eval(handle, bgScript);
            TestAssert.False(bgEvalRes?.StartsWith("__err:", StringComparison.Ordinal) == true, $"bg.js load failed: {bgEvalRes}");

            var bootstrapOk = QuickJsNative.InitBotGuardBootstrap(handle, challenge.GlobalName, challenge.Program);
            TestAssert.True(bootstrapOk, "Native InitBotGuardBootstrap failed");

            var snapshot = await RunAsyncJsFunctionAsync(handle, "_bg_doSnapshot()").ConfigureAwait(false);
            TestAssert.NotNullOrEmpty(snapshot, "Snapshot execution returned empty");

            Log.Info($"[PoToken] Snapshot success! Length={snapshot.Length}");
        }
        finally
        {
            QuickJsNative.DestroyContext(handle);
        }
    }

    [TestMethod(TestCategory.Diagnostic, "BotGuard Step 4 — IntegrityToken Minting",
        Group = TestGroups.Cipher, Order = 13, RequiresNetwork = true, TimeoutSeconds = 60)]
    public static async Task TestIntegrityTokenAsync()
    {
        var challenge = await WaaClient.FetchChallengeAsync(SharedHttpClient.Instance).ConfigureAwait(false);
        TestAssert.NotNull(challenge);

        var bgScript = await ResolveBgScriptAsync(challenge).ConfigureAwait(false);
        var handle = QuickJsNative.CreateContext("void 0;");
        TestAssert.True(handle != IntPtr.Zero);

        try
        {
            QuickJsNative.Eval(handle, bgScript);
            QuickJsNative.InitBotGuardBootstrap(handle, challenge.GlobalName, challenge.Program);

            var snapshot = await RunAsyncJsFunctionAsync(handle, "_bg_doSnapshot()").ConfigureAwait(false);
            TestAssert.NotNullOrEmpty(snapshot);

            var sw = Stopwatch.StartNew();
            var integrityData = await WaaClient.GenerateIntegrityTokenAsync(SharedHttpClient.Instance, snapshot).ConfigureAwait(false);
            sw.Stop();

            TestAssert.NotNull(integrityData);
            TestAssert.NotNullOrEmpty(integrityData.IntegrityToken);

            Log.Info($"[PoToken] IntegrityToken minted in {sw.ElapsedMilliseconds}ms (TTL={integrityData.EstimatedTtlSecs}s)");
        }
        finally
        {
            QuickJsNative.DestroyContext(handle);
        }
    }

    private static async Task<string> ResolveBgScriptAsync(BotGuardChallenge challenge)
    {
        if (!string.IsNullOrEmpty(challenge.InlineScript))
            return challenge.InlineScript;

        if (string.IsNullOrEmpty(challenge.ScriptUrl))
            throw new InvalidOperationException("Challenge has neither InlineScript nor ScriptUrl");

        return await SharedHttpClient.Instance.GetStringAsync(challenge.ScriptUrl).ConfigureAwait(false);
    }

    private static async Task<string?> RunAsyncJsFunctionAsync(IntPtr handle, string jsExpression, int timeoutMs = 10_000)
    {
        var evalCode =
            "globalThis._test_result = null; " +
            "globalThis._test_error  = null; " +
            "globalThis._test_done   = false; " +
            $"Promise.resolve({jsExpression}).then(" +
            "  function(r) { globalThis._test_result = (r == null) ? '' : String(r); globalThis._test_done = true; }," +
            "  function(e) { globalThis._test_error  = (e && e.message) ? e.message : String(e); globalThis._test_done = true; }" +
            ");";

        var startResult = QuickJsNative.Eval(handle, evalCode);
        if (startResult is not null && startResult.StartsWith("__err:", StringComparison.Ordinal))
            return null;

        var pumped = await Task.Run(() => QuickJsNative.PumpEventLoop(handle, timeoutMs)).ConfigureAwait(false);
        if (pumped < 0) return null;

        var done = QuickJsNative.Eval(handle, "globalThis._test_done ? 'yes' : 'no'");
        if (done != "yes") return null;

        var error = QuickJsNative.Eval(handle, "globalThis._test_error");
        if (!string.IsNullOrEmpty(error)) return null;

        return QuickJsNative.Eval(handle, "globalThis._test_result");
    }
}