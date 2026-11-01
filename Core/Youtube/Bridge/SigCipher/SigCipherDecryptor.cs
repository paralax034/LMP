using LMP.Core.Youtube.Bridge.Common;

namespace LMP.Core.Youtube.Bridge.SigCipher;

/// <summary>
/// Дешифратор подписей (SigCipher) YouTube.
/// </summary>
public sealed class SigCipherDecryptor : BaseYoutubeDecryptor
{
    /// <summary>
    /// Инициализирует новый экземпляр класса <see cref="SigCipherDecryptor"/>.
    /// </summary>
    public SigCipherDecryptor(JsDecryptionService jsService, string? cacheFilePath = null)
        : base(jsService, cacheFilePath ?? G.FilePath.SigCipherCache, maxMemory: 500, maxDisk: 100)
    {
    }

    /// <summary>
    /// Расшифровывает подпись (signature) YouTube.
    /// </summary>
    public async ValueTask<string> DecipherAsync(string signature, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(signature)) return signature;

        if (DecryptedTokens.ContainsKey(signature))
        {
            Log.Debug($"[SigCipher] Idempotency bypass: already deciphered");
            return signature;
        }

        if (Cache.TryGet(signature, out var cached))
        {
            DecryptedTokens.TryAdd(cached, 0);
            return cached;
        }

        await JsService.EnsureInitializedAsync(ct).ConfigureAwait(false);

        var result = await JsService.CallAsync("sig", signature, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(result) && result != signature)
        {
            Cache.Set(signature, result);
            DecryptedTokens.TryAdd(result, 0);
            return result;
        }

        return signature;
    }

    /// <inheritdoc />
    public override void InvalidateCache()
    {
        base.InvalidateCache();
        Log.Info("[SigCipher] Cache invalidated");
    }
}