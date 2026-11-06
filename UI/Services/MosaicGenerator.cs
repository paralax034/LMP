using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace LMP.UI.Services;

/// <summary>
/// Generates a playlist mosaic cover from track artwork.
/// 
/// <para><b>Supported layouts:</b></para>
/// <list type="bullet">
///   <item>1 cover: scaled to full size</item>
///   <item>2 covers: two horizontal halves</item>
///   <item>3 covers: 1 large on the left + 2 small on the right</item>
///   <item>4 covers: 2×2 grid</item>
/// </list>
/// 
/// <para><b>Result:</b> PNG file in AppData/LMP/Covers/,
/// filename = hash of track IDs for idempotency.</para>
/// </summary>
public static class MosaicGenerator
{
    /// <summary>Size of the resulting image (square).</summary>
    private const int OutputSize = 512;

    /// <summary>Gap between mosaic items in pixels.</summary>
    private const int Gap = 2;

    /// <summary>Folder for saving mosaics.</summary>
    private static readonly string CoversFolder = Path.Combine(G.Folder.Data, "Covers");

    /// <summary>
    /// Generates a mosaic from the provided images and saves it as a PNG.
    /// </summary>
    /// <param name="bitmaps">
    /// List of loaded images (1-4 items).
    /// The caller is responsible for disposing them after invocation.
    /// </param>
    /// <param name="trackIds">
    /// Track IDs — used to generate a unique filename.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Full path to the saved PNG file.</returns>
    /// <exception cref="ArgumentException">If the list is empty.</exception>
    public static async Task<string> GenerateAsync(
        IReadOnlyList<Bitmap> bitmaps,
        IReadOnlyList<string> trackIds,
        CancellationToken ct = default)
    {
        if (bitmaps.Count == 0)
            throw new ArgumentException("At least one bitmap is required.", nameof(bitmaps));

        // Ensure the directory exists
        Directory.CreateDirectory(CoversFolder);

        // Generate a unique filename from track IDs
        var fileName = GenerateFileName(trackIds);
        var filePath = Path.Combine(CoversFolder, fileName);

        // If the file already exists (idempotency), return the path
        if (File.Exists(filePath))
            return filePath;

        ct.ThrowIfCancellationRequested();

        // Mosaic rendering — must be executed on the UI thread (Avalonia rendering)
        using var memoryStream = new MemoryStream();
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            using var target = RenderMosaic(bitmaps);
            target.Save(memoryStream, PngBitmapEncoderOptions.Default);
        });

        memoryStream.Position = 0;
        await using (var fileStream = new FileStream(
            filePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true))
        {
            await memoryStream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
        }

        return filePath;
    }

    /// <summary>
    /// Generates a mosaic preview for UI display (without saving to disk).
    /// Must be called on the UI thread.
    /// </summary>
    /// <param name="bitmaps">Loaded covers (1-4).</param>
    /// <returns>Bitmap for binding to Image.Source.</returns>
    public static RenderTargetBitmap GeneratePreview(IReadOnlyList<Bitmap> bitmaps)
    {
        return RenderMosaic(bitmaps);
    }

    /// <summary>
    /// Renders the mosaic into a RenderTargetBitmap.
    /// Shared logic for GenerateAsync and GeneratePreview.
    /// </summary>
    private static RenderTargetBitmap RenderMosaic(IReadOnlyList<Bitmap> bitmaps)
    {
        var target = new RenderTargetBitmap(new PixelSize(OutputSize, OutputSize));

        using (var ctx = target.CreateDrawingContext())
        {
            // Fill the background with a dark color (for the gap)
            ctx.DrawRectangle(
                Brushes.Black,
                null,
                new Rect(0, 0, OutputSize, OutputSize));

            var count = Math.Min(bitmaps.Count, 4);

            switch (count)
            {
                case 1:
                    DrawSingle(ctx, bitmaps[0]);
                    break;
                case 2:
                    DrawTwo(ctx, bitmaps[0], bitmaps[1]);
                    break;
                case 3:
                    DrawThree(ctx, bitmaps[0], bitmaps[1], bitmaps[2]);
                    break;
                case 4:
                    DrawFour(ctx, bitmaps[0], bitmaps[1], bitmaps[2], bitmaps[3]);
                    break;
            }
        }

        return target;
    }

    // ═══ Layouts ═══

    /// <summary>1 cover: scaled to the entire square.</summary>
    private static void DrawSingle(DrawingContext ctx, Bitmap bmp)
    {
        DrawBitmapToRect(ctx, bmp, new Rect(0, 0, OutputSize, OutputSize));
    }

    /// <summary>2 covers: two horizontal halves with a gap.</summary>
    private static void DrawTwo(DrawingContext ctx, Bitmap left, Bitmap right)
    {
        var halfW = (OutputSize - Gap) / 2.0;
        DrawBitmapToRect(ctx, left, new Rect(0, 0, halfW, OutputSize));
        DrawBitmapToRect(ctx, right, new Rect(halfW + Gap, 0, halfW, OutputSize));
    }

    /// <summary>3 covers: 1 large on the left + 2 small stacked on the right.</summary>
    private static void DrawThree(DrawingContext ctx, Bitmap big, Bitmap topRight, Bitmap bottomRight)
    {
        var halfW = (OutputSize - Gap) / 2.0;
        var halfH = (OutputSize - Gap) / 2.0;

        // Large left image — takes full height
        DrawBitmapToRect(ctx, big, new Rect(0, 0, halfW, OutputSize));
        // Top right
        DrawBitmapToRect(ctx, topRight, new Rect(halfW + Gap, 0, halfW, halfH));
        // Bottom right
        DrawBitmapToRect(ctx, bottomRight, new Rect(halfW + Gap, halfH + Gap, halfW, halfH));
    }

    /// <summary>4 covers: 2×2 grid.</summary>
    private static void DrawFour(DrawingContext ctx, Bitmap tl, Bitmap tr, Bitmap bl, Bitmap br)
    {
        var halfW = (OutputSize - Gap) / 2.0;
        var halfH = (OutputSize - Gap) / 2.0;

        DrawBitmapToRect(ctx, tl, new Rect(0, 0, halfW, halfH));
        DrawBitmapToRect(ctx, tr, new Rect(halfW + Gap, 0, halfW, halfH));
        DrawBitmapToRect(ctx, bl, new Rect(0, halfH + Gap, halfW, halfH));
        DrawBitmapToRect(ctx, br, new Rect(halfW + Gap, halfH + Gap, halfW, halfH));
    }

    /// <summary>
    /// Draws a Bitmap into the specified rectangle with cropping (UniformToFill).
    /// Centers the image and crops overflowing parts.
    /// </summary>
    private static void DrawBitmapToRect(DrawingContext ctx, Bitmap bmp, Rect destRect)
    {
        // Calculate source rect for UniformToFill (centered cropping)
        var srcW = bmp.PixelSize.Width;
        var srcH = bmp.PixelSize.Height;
        var destAspect = destRect.Width / destRect.Height;
        var srcAspect = (double)srcW / srcH;

        Rect sourceRect;
        if (srcAspect > destAspect)
        {
            // Image is wider — crop the sides
            var cropW = srcH * destAspect;
            var offsetX = (srcW - cropW) / 2.0;
            sourceRect = new Rect(offsetX, 0, cropW, srcH);
        }
        else
        {
            // Image is taller — crop top/bottom
            var cropH = srcW / destAspect;
            var offsetY = (srcH - cropH) / 2.0;
            sourceRect = new Rect(0, offsetY, srcW, cropH);
        }

        // Public DrawingContext.DrawImage API
        ctx.DrawImage(bmp, sourceRect, destRect);
    }

    /// <summary>
    /// Generates a unique filename from sorted track IDs.
    /// Sorting guarantees the same filename for the same set of tracks
    /// regardless of selection order.
    /// </summary>
    private static string GenerateFileName(IReadOnlyList<string> trackIds)
    {
        var sorted = trackIds.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var combined = string.Join("|", sorted);

        // Simple hash (sufficient for a filename, non-cryptographic)
        var hash = combined.GetHashCode(StringComparison.Ordinal);
        return $"mosaic_{hash:X8}.png";
    }
}