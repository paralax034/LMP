using System.Diagnostics;
using LMP.Core.Audio.Http;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Core.Youtube.Bridge.PoToken;
using LMP.Tests.Framework;

namespace LMP.Tests.Integration;

/// <summary>
/// Integration tests and step-by-step diagnostic trace for BotGuard PoToken generation.
/// </summary>
public static class PoTokenTests
{
    [TestMethod(TestCategory.Integration, "BotGuard PoToken Mint & Length Invariant",
        Group = TestGroups.Cipher, Order = 1, RequiresNetwork = true, TimeoutSeconds = 90)]
    public static async Task TestMintTokenLengthAsync(CancellationToken ct = default)
    {
        using var service = new BotGuardService(SharedHttpClient.Instance);
        const string videoId = "dQw4w9WgXcQ";

        var sw = Stopwatch.StartNew();
        var token = await service.MintForVideoAsync(videoId, ct).ConfigureAwait(false);
        sw.Stop();

        TestAssert.NotNullOrEmpty(token, "MintForVideoAsync returned null/empty token");
        TestAssert.True(token.Length >= 80, $"PoToken too short: {token.Length} chars (expected >=80)");
        TestAssert.True(token.Length <= 300, $"PoToken too long: {token.Length} chars (expected <=300)");
        TestAssert.False(token.Contains('+') || token.Contains('/'), "Token contains standard base64 instead of base64url");

        Log.Info($"[PoToken] ✓ Content-bound token minted: {token.Length} chars in {sw.ElapsedMilliseconds}ms");
    }

    [TestMethod(TestCategory.Integration, "PoToken Full Pipeline Resolution",
        Group = TestGroups.Cipher, Order = 2, RequiresNetwork = true, TimeoutSeconds = 90)]
    public static async Task TestFullPipelineAsync(CancellationToken ct = default)
    {
        using var service = new BotGuardService(SharedHttpClient.Instance);
        var visitorId = Core.Youtube.Utils.YoutubeClientUtils.VisitorData;

        var sw = Stopwatch.StartNew();
        var token = await service.GenerateAsync(visitorId, ct).ConfigureAwait(false);
        sw.Stop();

        TestAssert.NotNullOrEmpty(token, "Full pipeline returned null/empty PoToken");
        TestAssert.True(token.Length >= 100, $"PoToken suspiciously short: {token.Length} chars");
        TestAssert.False(token.Contains('+') || token.Contains('/') || token.Contains('='), "PoToken must be Base64Url encoded");

        Log.Info($"[PoToken] ✓ Full pipeline verified in {sw.ElapsedMilliseconds}ms");
    }

    [TestMethod(TestCategory.Diagnostic, "BotGuard End-to-End Stage Trace (WAA -> bg.js -> QuickJS -> Integrity)",
        Group = TestGroups.Cipher, Order = 10, RequiresNetwork = true, TimeoutSeconds = 90)]
    public static async Task TestBotGuardStepTraceAsync(CancellationToken ct = default)
    {
        var http = SharedHttpClient.Instance;

        var sw = Stopwatch.StartNew();
        var challenge = await WaaClient.FetchChallengeAsync(http, ct: ct).ConfigureAwait(false);
        TestAssert.NotNull(challenge, "Step 1 (WAA Challenge) returned null");
        TestAssert.NotNullOrEmpty(challenge.Program);
        TestAssert.NotNullOrEmpty(challenge.GlobalName);
        TestAssert.True(challenge.HasScript, "Challenge missing both ScriptUrl and InlineScript");
        Log.Info($"[PoToken] Step 1 WAA Challenge: {sw.ElapsedMilliseconds}ms (GlobalName='{challenge.GlobalName}')");

        sw.Restart();
        var bgScript = await ResolveBgScriptAsync(http, challenge, ct).ConfigureAwait(false);
        TestAssert.NotNullOrEmpty(bgScript);
        TestAssert.True(bgScript.Length > 10_000, $"Step 2 (bg.js) suspiciously small: {bgScript.Length} bytes");
        Log.Info($"[PoToken] Step 2 bg.js download: {bgScript.Length / 1024}KB in {sw.ElapsedMilliseconds}ms");

        var handle = QuickJsNative.CreateContext("void 0;");
        TestAssert.True(handle != IntPtr.Zero, "Failed to create QuickJS context");

        try
        {
            sw.Restart();
            var bgEvalRes = QuickJsNative.Eval(handle, bgScript);
            TestAssert.False(bgEvalRes?.StartsWith("__err:", StringComparison.Ordinal) == true, $"bg.js load failed: {bgEvalRes}");

            var bootstrapOk = QuickJsNative.InitBotGuardBootstrap(handle, challenge.GlobalName, challenge.Program);
            TestAssert.True(bootstrapOk, "Native InitBotGuardBootstrap failed");

            var snapshot = await RunAsyncJsFunctionAsync(handle, "_bg_doSnapshot()").ConfigureAwait(false);
            TestAssert.NotNullOrEmpty(snapshot, "Step 3 (QuickJS Snapshot) returned empty");
            Log.Info($"[PoToken] Step 3 QuickJS Snapshot: {snapshot.Length} chars in {sw.ElapsedMilliseconds}ms");

            sw.Restart();
            var integrityData = await WaaClient.GenerateIntegrityTokenAsync(http, snapshot, ct).ConfigureAwait(false);
            TestAssert.NotNull(integrityData, "Step 4 (IntegrityToken) returned null");
            TestAssert.NotNullOrEmpty(integrityData.IntegrityToken);
            Log.Info($"[PoToken] Step 4 IntegrityToken: {sw.ElapsedMilliseconds}ms (TTL={integrityData.EstimatedTtlSecs}s)");
        }
        finally
        {
            QuickJsNative.DestroyContext(handle);
        }
    }

    private static async Task<string> ResolveBgScriptAsync(HttpClient http, BotGuardChallenge challenge, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(challenge.InlineScript))
            return challenge.InlineScript;

        if (string.IsNullOrEmpty(challenge.ScriptUrl))
            throw new InvalidOperationException("Challenge has neither InlineScript nor ScriptUrl");

        return await http.GetStringAsync(challenge.ScriptUrl, ct).ConfigureAwait(false);
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