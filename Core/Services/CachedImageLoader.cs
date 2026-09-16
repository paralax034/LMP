using AsyncImageLoader;
using Avalonia.Media.Imaging;

namespace LMP.Core.Services;

/// <summary>
/// Адаптер для AsyncImageLoader с автоматическим управлением refCount.
/// 
/// <para><b>ВАЖНО:</b> принимает только HTTP/HTTPS URL.
/// Локальные пути и невалидные строки отклоняются для предотвращения
/// мусорных сетевых запросов (например, hex-хеши парсящиеся как URI).</para>
/// </summary>
public sealed class CachedImageLoader : IAsyncImageLoader
{
    private readonly ImageCacheService _cache;
    private readonly ImageQuality _defaultQuality;

    /// <summary>
    /// Инициализирует адаптер загрузчика изображений с указанным качеством по умолчанию.
    /// </summary>
    /// <param name="cache">Служба кэширования изображений.</param>
    /// <param name="defaultQuality">Качество декодирования растровых изображений.</param>
    public CachedImageLoader(ImageCacheService cache, ImageQuality defaultQuality = ImageQuality.Low)
    {
        _cache = cache;
        _defaultQuality = defaultQuality;
    }

    /// <summary>
    /// Асинхронно предоставляет растровое изображение для указанного URL.
    /// </summary>
    /// <param name="url">Веб-адрес изображения.</param>
    /// <returns>Экземпляр <see cref="Bitmap"/> или <see langword="null"/> в случае ошибки или отмены.</returns>
    public async Task<Bitmap?> ProvideImageAsync(string url)
    {
        if (string.IsNullOrEmpty(url))
            return null;

        // Строгая проверка: только http:// и https://
        if (!url.StartsWith(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            // GetImageAsync уже вызывает AddRef() внутри
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
        // Ничего не делаем - ImageCacheService управляет своим lifecycle
        GC.SuppressFinalize(this);
    }
}