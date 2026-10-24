using System.Diagnostics;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Core.Youtube.Bridge.SigCipher;
using LMP.Tests.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Tests.Unit;

/// <summary>
/// Модульные и интеграционные тесты подсистемы расшифровки подписей (Signature Cipher).
/// </summary>
public static class SigCipherTests
{
    private const string MockPlayerScript = "var sts = 20650; signatureTimestamp: 20650;";
    private const string ExpectedSts = "20650";

    [TestMethod(TestCategory.Unit, "Extract SignatureTimestamp (STS) from Player JS",
        Group = TestGroups.Cipher, Order = 1)]
    public static Task TestStsExtractionAsync()
    {
        var sts = YoutubeAstSolver.ExtractSts(MockPlayerScript);
        TestAssert.Equal(ExpectedSts, sts, "STS extraction mismatch");
        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Integration, "Signature Cipher Live Decryption",
        Group = TestGroups.Cipher, Order = 2, RequiresNetwork = true, TimeoutSeconds = 60)]
    public static async Task TestLiveDecryptionAsync(IServiceProvider services)
    {
        var decryptor = services.GetRequiredService<SigCipherDecryptor>();
        var testSig = TestConfig.Get().SigCipher.TestSignature;

        var sw = Stopwatch.StartNew();
        var result = await decryptor.DecipherAsync(testSig).ConfigureAwait(false);
        sw.Stop();

        TestAssert.NotNullOrEmpty(result, "Decryption returned empty string");
        TestAssert.False(string.Equals(result, testSig, StringComparison.Ordinal), "Decryption returned unchanged signature");
        TestAssert.True(result.Length > 50, $"Result length suspicious: {result.Length}");

        var inputChars = new HashSet<char>(testSig);
        for (int i = 0; i < result.Length; i++)
            TestAssert.True(inputChars.Contains(result[i]), $"Result contains unexpected character '{result[i]}'");

        int removedCount = testSig.Length - result.Length;
        TestAssert.True(removedCount is >= 0 and <= 10, $"Removed chars count out of bounds: {removedCount}");

        // Проверка кэша
        sw.Restart();
        var cached = await decryptor.DecipherAsync(testSig).ConfigureAwait(false);
        sw.Stop();

        TestAssert.Equal(result, cached, "Cache returned inconsistent result");
        TestAssert.True(sw.ElapsedMilliseconds < 5, $"Cache hit too slow: {sw.ElapsedMilliseconds}ms");

        Log.Info($"[SigCipher] Live decipher verified in {sw.ElapsedMilliseconds}ms (removed {removedCount} chars)");
    }

    [TestMethod(TestCategory.Integration, "Concurrent N-Token & Signature Decryption",
        Group = TestGroups.Cipher, Order = 3, RequiresNetwork = true, TimeoutSeconds = 60)]
    public static async Task TestConcurrentNAndSigAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var jsService = services.GetRequiredService<JsDecryptionService>();
        await jsService.EnsureInitializedAsync(ct).ConfigureAwait(false);

        var nToken = TestConfig.Get().NToken.TestToken;
        var sigToken = TestConfig.Get().SigCipher.TestSignature;

        var sw = Stopwatch.StartNew();
        var nTask = jsService.CallAsync("n", nToken).AsTask();
        var sigTask = jsService.CallAsync("sig", sigToken).AsTask();

        await Task.WhenAll(nTask, sigTask).ConfigureAwait(false);
        sw.Stop();

        var nResult = nTask.Result;
        var sigResult = sigTask.Result;

        TestAssert.True(sigResult != sigToken || nResult != nToken,
            "Both n and sig returned unchanged — QuickJS execution failed");

        Log.Info($"[SigCipher] Concurrent calls completed in {sw.ElapsedMilliseconds}ms");
    }
}