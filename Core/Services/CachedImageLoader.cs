using AsyncImageLoader;
using Avalonia.Media.Imaging;

namespace LMP.Core.Services;

/// <summary>
/// Adapter for AsyncImageLoader with automatic refCount management.
/// 
/// <para><b>IMPORTANT:</b> Accepts HTTP/HTTPS URLs only.
/// Local paths and invalid strings are rejected to prevent
/// garbage network requests (e.g., hex hashes parsed as URIs).</para>
/// </summary>
public sealed class CachedImageLoader : IAsyncImageLoader
{
    private readonly ImageCacheService _cache;
    private readonly ImageQuality _defaultQuality;

    /// <summary>
    /// Initializes the image loader adapter with the specified default quality.
    /// </summary>
    /// <param name="cache">The image caching service.</param>
    /// <param name="defaultQuality">The bitmap decoding quality.</param>
    public CachedImageLoader(ImageCacheService cache, ImageQuality defaultQuality = ImageQuality.Low)
    {
        _cache = cache;
        _defaultQuality = defaultQuality;
    }

    /// <summary>
    /// Asynchronously provides a bitmap for the specified URL.
    /// </summary>
    /// <param name="url">The web URL of the image.</param>
    /// <returns>A <see cref="Bitmap"/> instance or <see langword="null"/> in case of error or cancellation.</returns>
    public async Task<Bitmap?> ProvideImageAsync(string url)
    {
        if (string.IsNullOrEmpty(url))
            return null;

        // Strict validation: http:// and https:// only
        if (!url.StartsWith(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            // GetImageAsync already calls AddRef() internally
            return await _cache.GetImageAsync(url, _defaultQuality).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Debug($"[CachedImageLoader] Image load failed: {ex.Message}");
            return null;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Do nothing - ImageCacheService manages its own lifecycle
    }
}