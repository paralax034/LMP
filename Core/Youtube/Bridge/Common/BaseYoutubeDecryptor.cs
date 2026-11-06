using System.Collections.Concurrent;

namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Absolute base class for YouTube decryptors implementing shared two-tier 
/// caching logic and idempotency protection against re-decryption.
/// </summary>
public abstract class BaseYoutubeDecryptor : IYoutubeDecryptor, IDisposable
{
    private bool _disposed;

    /// <summary>
    /// Persistent QuickJS control service.
    /// </summary>
    protected readonly JsDecryptionService JsService;

    /// <summary>
    /// Cache of decrypted values (string -> string) on disk and in memory.
    /// </summary>
    protected readonly DecryptorCache Cache;

    /// <summary>
    /// Local session cache to prevent re-processing of already decrypted tokens.
    /// </summary>
    protected readonly ConcurrentDictionary<string, byte> DecryptedTokens = new(StringComparer.Ordinal);

    /// <summary>
    /// Player context manager.
    /// </summary>
    public PlayerContextManager PlayerManager => JsService.PlayerManager;

    /// <summary>
    /// Indicates whether the internal native QuickJS context is initialized.
    /// </summary>
    public bool IsInitialized => JsService.IsInitialized;

    /// <summary>
    /// Ensures background initialization of the native QuickJS context (from bytecode cache or via AST preprocessing).
    /// </summary>
    public ValueTask EnsureInitializedAsync(CancellationToken ct = default) =>
        JsService.EnsureInitializedAsync(ct);

    /// <summary>
    /// Initializes a new instance of the <see cref="BaseYoutubeDecryptor"/> class.
    /// </summary>
    protected BaseYoutubeDecryptor(JsDecryptionService jsService, string cacheFilePath, int maxMemory, int maxDisk)
    {
        JsService = jsService;
        Cache = new DecryptorCache(cacheFilePath, maxMemory, maxDisk);
    }

    /// <inheritdoc />
    public virtual void InvalidateCache()
    {
        DecryptedTokens.Clear();
        Cache.Clear();
    }

    /// <inheritdoc />
    public Task FlushCacheAsync() => Cache.SaveAsync();

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases unmanaged (and managed) resources.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            DecryptedTokens.Clear();
            try
            {
                FlushCacheAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // Suppress errors during application pool teardown
            }
        }

        _disposed = true;
    }
}