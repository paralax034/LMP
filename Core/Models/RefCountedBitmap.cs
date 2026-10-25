using Avalonia.Media.Imaging;

namespace LMP.Core.Models;

/// <summary>
/// Обёртка над разделяемым <see cref="Avalonia.Media.Imaging.Bitmap"/> для учёта памяти в LRU-кэше.
/// </summary>
public sealed class RefCountedBitmap : IDisposable
{
    private int _disposed;
    private Bitmap? _bitmap;

    public Bitmap Bitmap => _bitmap!;
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
    /// Отвязывает ссылку кэша от <see cref="Bitmap"/>, позволяя GC собрать его,
    /// как только ни один визуальный контрол <c>Image.Source</c> больше не ссылается на него.
    /// Не вызывает <c>Bitmap.Dispose()</c>, чтобы не разрушить активные привязки в визуальном дереве Avalonia.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _bitmap = null;
    }
}