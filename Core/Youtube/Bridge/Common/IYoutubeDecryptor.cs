namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Base interface for YouTube decipherers.
/// </summary>
public interface IYoutubeDecryptor
{
    /// <summary>Invalidates the cache (when player version changes).</summary>
    void InvalidateCache();

    /// <summary>Asynchronously flushes the cache to disk.</summary>
    Task FlushCacheAsync();
}