using Avalonia.Media.Imaging;

namespace LMP.Core.Models;

/// <summary>
/// Wrapper around a cached <see cref="Avalonia.Media.Imaging.Bitmap"/> for byte-size tracking in the LRU cache.
/// Note: Does not implement reference counting; cache eviction only clears its own reference so GC can reclaim
/// the bitmap after visual controls detach.
/// </summary>
public sealed class RefCountedBitmap : IDisposable
{
    private int _disposed;
    private Bitmap? _bitmap;

    public Bitmap? Bitmap => Volatile.Read(ref _bitmap);
    public long EstimatedBytes { get; }
    public DateTime CachedAt { get; }

    public RefCountedBitmap(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        _bitmap = bitmap;

        long pixelCount = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height;
        EstimatedBytes = pixelCount * 4;
        CachedAt = DateTime.UtcNow;
    }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Unlinks the cache reference from the <see cref="Bitmap"/>, allowing the GC to collect it
    /// as soon as no visual control's <c>Image.Source</c> references it anymore.
    /// Does not call <c>Bitmap.Dispose()</c> to avoid breaking active bindings in the Avalonia visual tree.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Volatile.Write(ref _bitmap, null);
    }
}