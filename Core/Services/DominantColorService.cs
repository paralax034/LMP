using System.Collections.Concurrent;
using Avalonia.Media;
using SkiaSharp;

namespace LMP.Core.Services;

/// <summary>
/// Extracts the dominant color from cover art for gradient backgrounds.
/// Algorithm: load → resize 50x50 → filter by L → weight by S → average.
/// Supports HTTP/HTTPS URLs and local files.
/// </summary>
public sealed class DominantColorService
{
    private readonly NetworkManager _networkManager;
    private readonly ImageCacheService _imageCache;
    private readonly ConcurrentDictionary<string, Color> _cache = new(StringComparer.Ordinal);
    private const int MaxCacheEntries = 256;

    private const float MinLuminance = 0.1f;
    private const float MaxLuminance = 0.9f;
    private const float MinSaturation = 0.15f;
    private const int SampleSize = 50;

    public DominantColorService(NetworkManager networkManager, ImageCacheService imageCache)
    {
        _networkManager = networkManager;
        _imageCache = imageCache;
    }

    /// <summary>
    /// Gets the dominant color from an image.
    /// Cached in memory by URL.
    /// Supports HTTP/HTTPS URLs and local files.
    /// </summary>
    public async Task<Color?> GetDominantColorAsync(string? imageUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(imageUrl))
            return null;

        if (_cache.TryGetValue(imageUrl, out var cached))
            return cached;

        try
        {
            var color = await ExtractColorAsync(imageUrl, ct).ConfigureAwait(false);

            if (color.HasValue)
            {
                if (_cache.Count >= MaxCacheEntries)
                    _cache.Clear();

                _cache[imageUrl] = color.Value;
                Log.Debug($"[DominantColor] Extracted: R={color.Value.R} G={color.Value.G} B={color.Value.B} from {imageUrl[..Math.Min(60, imageUrl.Length)]}");
            }

            return color;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Debug($"[DominantColor] Failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Loads an image and extracts the dominant color without intermediate decoding into Avalonia.Bitmap.
    /// Uses a unified FNV-1a disk cache with <see cref="ImageCacheService"/>.
    /// </summary>
    private async Task<Color?> ExtractColorAsync(string imageUrl, CancellationToken ct)
    {
        string? targetFilePath = null;
        byte[]? fallbackData = null;

        if (IsLocalPath(imageUrl))
        {
            var localPath = ResolveLocalPath(imageUrl);
            if (localPath != null && File.Exists(localPath))
            {
                targetFilePath = localPath;
                Log.Debug($"[DominantColor] Using local file: {localPath[..Math.Min(60, localPath.Length)]}");
            }
            else
            {
                Log.Warn($"[DominantColor] Local file not found: {imageUrl[..Math.Min(60, imageUrl.Length)]}");
                return null;
            }
        }
        else
        {
            // Use the unified FNV-1a disk cache path from ImageCacheService (zero redundant network downloads and zero Avalonia.Bitmap allocations)
            var diskPath = ImageCacheService.GetDiskCachePath(imageUrl);

            if (!File.Exists(diskPath))
            {
                await _imageCache.EnsureDiskFileAsync(imageUrl, ct).ConfigureAwait(false);
            }

            if (File.Exists(diskPath))
            {
                targetFilePath = diskPath;
            }
            else
            {
                try
                {
                    fallbackData = await _networkManager.ImageClient.GetByteArrayAsync(imageUrl, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warn($"[DominantColor] HTTP download failed: {ex.Message}");
                    return null;
                }
            }
        }

        if (targetFilePath == null && (fallbackData == null || fallbackData.Length == 0))
            return null;

        return await Task.Run(() =>
        {
            if (ct.IsCancellationRequested) return null;

            using var codec = targetFilePath != null
                ? SKCodec.Create(targetFilePath)
                : SKCodec.Create(new SKMemoryStream(fallbackData));

            if (codec == null)
            {
                Log.Debug("[DominantColor] SKCodec.Create returned null");
                return null;
            }

            // Hardware sub-sampling when decoding JPEG/WebP directly into the minimum size (saves native buffer allocations)
            var desiredScale = (float)SampleSize / Math.Max(1, Math.Max(codec.Info.Width, codec.Info.Height));
            var scaledSize = codec.GetScaledDimensions(desiredScale);
            var decodeInfo = new SKImageInfo(
                Math.Max(1, scaledSize.Width),
                Math.Max(1, scaledSize.Height),
                SKColorType.Rgba8888,
                SKAlphaType.Premul);

            using var decodedBitmap = new SKBitmap(decodeInfo);
            var result = codec.GetPixels(decodeInfo, decodedBitmap.GetPixels());
            if (result is not SKCodecResult.Success and not SKCodecResult.IncompleteInput)
                return null;

            if (decodedBitmap.Width <= SampleSize && decodedBitmap.Height <= SampleSize)
                return (Color?)AnalyzePixels(decodedBitmap);

            using var resized = decodedBitmap.Resize(
                new SKImageInfo(SampleSize, SampleSize, SKColorType.Rgba8888, SKAlphaType.Premul),
                new SKSamplingOptions(SKFilterMode.Linear));

            return resized != null ? (Color?)AnalyzePixels(resized) : null;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks whether the URL is a local path.
    /// </summary>
    private static bool IsLocalPath(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        // file:// URI
        if (url.StartsWith(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
            return true;

        // Absolute Windows path (C:\...) or Unix path (/home/...)
        if (Path.IsPathRooted(url))
            return true;

        return false;
    }

    /// <summary>
    /// Converts a file:// URI or absolute path into a local file path.
    /// </summary>
    private static string? ResolveLocalPath(string url)
    {
        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var fileUri))
                return fileUri.LocalPath;
            return null;
        }

        if (Path.IsPathRooted(url))
            return url;

        return null;
    }

    /// <summary>
    /// Analyzes pixels via direct buffer access (without allocating an SKColor[] array on the heap via bitmap.Pixels).
    /// </summary>
    private static unsafe Color AnalyzePixels(SKBitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        int count = width * height;
        if (count <= 0)
            return Colors.Gray;

        nint pixelsPtr = bitmap.GetPixels();
        if (pixelsPtr == 0)
            return Colors.Gray;

        double totalR = 0, totalG = 0, totalB = 0;
        double totalWeight = 0;

        byte* ptr = (byte*)pixelsPtr;
        bool isBgra = bitmap.ColorType == SKColorType.Bgra8888;

        for (int i = 0; i < count; i++)
        {
            byte c0 = ptr[i * 4];
            byte c1 = ptr[i * 4 + 1];
            byte c2 = ptr[i * 4 + 2];
            byte aByte = ptr[i * 4 + 3];

            if (aByte < 128) continue;

            float r = (isBgra ? c2 : c0) / 255f;
            float g = c1 / 255f;
            float b = (isBgra ? c0 : c2) / 255f;

            RgbToHsl(r, g, b, out _, out float s, out float l);

            if (l < MinLuminance || l > MaxLuminance)
                continue;

            // Weight = saturation. Desaturated grays have low weight
            float weight = s < MinSaturation ? s * 0.1f : s;

            totalR += r * weight;
            totalG += g * weight;
            totalB += b * weight;
            totalWeight += weight;
        }

        if (totalWeight < 0.001)
        {
            // All pixels were filtered out — return medium dark gray
            return Color.FromRgb(60, 60, 80);
        }

        byte avgR = (byte)Math.Clamp(totalR / totalWeight * 255, 0, 255);
        byte avgG = (byte)Math.Clamp(totalG / totalWeight * 255, 0, 255);
        byte avgB = (byte)Math.Clamp(totalB / totalWeight * 255, 0, 255);

        return Color.FromRgb(avgR, avgG, avgB);
    }

    private static void RgbToHsl(float r, float g, float b, out float h, out float s, out float l)
    {
        float max = Math.Max(r, Math.Max(g, b));
        float min = Math.Min(r, Math.Min(g, b));
        float delta = max - min;

        h = 0;
        l = (max + min) / 2f;

        if (delta < 0.001f)
        {
            s = 0;
            return;
        }

        s = l > 0.5f ? delta / (2f - max - min) : delta / (max + min);

        if (max == r)
            h = ((g - b) / delta + (g < b ? 6f : 0f)) / 6f;
        else if (max == g)
            h = ((b - r) / delta + 2f) / 6f;
        else
            h = ((r - g) / delta + 4f) / 6f;
    }

    /// <summary>
    /// Creates a 3-stop gradient for the header background.
    /// </summary>
    public static LinearGradientBrush CreateHeaderGradient(Color dominant)
    {
        return new LinearGradientBrush
        {
            StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
            EndPoint = new Avalonia.RelativePoint(0, 1, Avalonia.RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(200, dominant.R, dominant.G, dominant.B), 0.0),
                new GradientStop(Color.FromArgb(80, dominant.R, dominant.G, dominant.B), 0.5),
                new GradientStop(Color.FromArgb(0, dominant.R, dominant.G, dominant.B), 1.0)
            }
        };
    }

    public void ClearCache() => _cache.Clear();
}