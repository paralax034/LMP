using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.UI.Controls;

/// <summary>
/// Unified attached property for image loading with debounce, quality control,
/// and a unified LRU cache. Fully replaces DebouncedImage and QualityImage.
///
/// <para><b>Problem solved:</b>
/// DebouncedImage → AsyncImageLoader → RamCachedWebImageLoader (unbounded, full-resolution).
/// 574 thumbnails × 280KB = up to 160MB without eviction.
/// SmartImage → ImageCacheService → FNV-1a LRU, MaxMemoryItems=80, DecodeToWidth(120).
/// 80 × 57KB ≈ 4.5MB ceiling.</para>
///
/// <para><b>Debounce:</b>
/// When <see cref="DebounceProperty"/> &gt; 0 — two-phase delay.
/// Phase 1: yield to Background priority (UI completes layout for the current frame).
/// Phase 2: Task.Delay filters out intermediate recyclings.
/// Recycling before delay expires → CTS cancelled → load aborted.</para>
///
/// <para><b>Bitmap lifecycle:</b>
/// HTTP → shared from LRU cache (IsOwned=false, do not dispose — Image.Source holds reference,
/// GC will collect after eviction + Source change).
/// avares:// / local → created for control (IsOwned=true, disposed on Source change).</para>
/// </summary>
public static class SmartImage
{
    #region Constants

    /// <summary>
    /// Default debounce delay for virtualized lists (ms).
    /// 80ms ≈ 5 frames at 60fps — sufficient to filter out
    /// intermediate recyclings during fast scrolling.
    /// </summary>
    public const int DefaultDebounceMs = 80;

    #endregion

    #region Attached Properties

    public static readonly AttachedProperty<string?> SourceProperty =
        AvaloniaProperty.RegisterAttached<Image, string?>("Source", typeof(SmartImage));

    /// <summary>
    /// Decoding quality. Avalonia automatically parses enum from XAML:
    /// <c>ctrl:SmartImage.Quality="Low"</c> → ImageQuality.Low (120px).
    /// </summary>
    public static readonly AttachedProperty<ImageQuality> QualityProperty =
        AvaloniaProperty.RegisterAttached<Image, ImageQuality>("Quality", typeof(SmartImage),
            defaultValue: ImageQuality.Low);

    /// <summary>
    /// Debounce delay in ms. 0 = no debounce (static images outside lists).
    /// Use <see cref="DefaultDebounceMs"/> for ItemsRepeater with recycling.
    /// </summary>
    public static readonly AttachedProperty<int> DebounceProperty =
        AvaloniaProperty.RegisterAttached<Image, int>("Debounce", typeof(SmartImage),
            defaultValue: 0);

    /// <summary>
    /// Internal marker: true if bitmap was created for this control (avares/local).
    /// false = bitmap is from shared LRU cache, do not modify or dispose.
    /// </summary>
    private static readonly AttachedProperty<bool> IsOwnedProperty =
        AvaloniaProperty.RegisterAttached<Image, bool>("IsOwned", typeof(SmartImage));

    #endregion

    #region Pending Operations

    /// <summary>
    /// ConditionalWeakTable does not hold Image from GC —
    /// a recycled control is automatically collected along with its CTS.
    /// </summary>
    private static readonly ConditionalWeakTable<Image, CancellationTokenSource> _pending = [];

    /// <summary>
    /// Cache service statically: GetService&lt;T&gt;() is a dictionary lookup on every invocation.
    /// When scrolling 574 tracks, this results in hundreds of calls without cache. Lazily initialized
    /// after the DI container is ready (first access no earlier than the first load).
    /// </summary>
    private static ImageCacheService? _imageCache;

    #endregion

    static SmartImage()
    {
        SourceProperty.Changed.AddClassHandler<Image>(static (img, _) => BeginLoad(img));
        QualityProperty.Changed.AddClassHandler<Image>(static (img, _) => BeginLoad(img));
    }

    #region Getters / Setters

    public static string? GetSource(Image image) => image.GetValue(SourceProperty);
    public static void SetSource(Image image, string? value) => image.SetValue(SourceProperty, value);

    public static ImageQuality GetQuality(Image image) => image.GetValue(QualityProperty);
    public static void SetQuality(Image image, ImageQuality value) => image.SetValue(QualityProperty, value);

    public static int GetDebounce(Image image) => image.GetValue(DebounceProperty);
    public static void SetDebounce(Image image, int value) => image.SetValue(DebounceProperty, value);

    #endregion

    #region Core Load Logic

    /// <summary>
    /// Initiates image loading for the control. Called when
    /// <see cref="SourceProperty"/> or <see cref="QualityProperty"/> changes.
    /// </summary>
    private static async void BeginLoad(Image image)
    {
        if (GetQuality(image) == 0) return;

        var url = GetSource(image);

        if (string.IsNullOrEmpty(url))
        {
            CancelPending(image);
            DisposeOwned(image);
            image.Source = null;
            return;
        }

        var decodeWidth = (int)GetQuality(image);

        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            _imageCache ??= AppEntry.Services.GetService<ImageCacheService>();
            if (_imageCache != null && _imageCache.TryGetFromMemory(url, decodeWidth, out var memoryBitmap))
            {
                CancelPending(image);
                DisposeOwned(image);
                image.Source = memoryBitmap;
                image.SetValue(IsOwnedProperty, false);
                return;
            }
        }

        CancelPending(image);
        DisposeOwned(image);
        image.Source = null;

        var cts = new CancellationTokenSource();
        _pending.AddOrUpdate(image, cts);

        try
        {
            var debounceMs = GetDebounce(image);

            if (debounceMs > 0)
            {
                if (!await cts.Token.DelayNoThrowAsync(debounceMs))
                    return;
            }

            if (cts.IsCancellationRequested) return;

            if (GetSource(image) != url) return;

            var urlSpan = url.AsSpan();

            if (urlSpan.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                await LoadHttpAsync(image, url, decodeWidth, cts.Token);
            else if (urlSpan.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
                await LoadAvaresAsync(image, url, decodeWidth, cts.Token);
            else
                await LoadLocalAsync(image, url, decodeWidth, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Debug($"[SmartImage] Load failed for '{url}': {ex.Message}");
            DisposeOwned(image);
            image.Source = null;
        }
        finally
        {
            if (_pending.TryGetValue(image, out var current) && ReferenceEquals(current, cts))
                _pending.Remove(image);

            cts.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CancelPending(Image image)
    {
        if (_pending.TryGetValue(image, out var oldCts))
        {
            _pending.Remove(image);
            try { oldCts.Cancel(); } catch { }
            oldCts.Dispose();
        }
    }

    #endregion

    #region Loaders

    /// <summary>
    /// HTTP/HTTPS: bitmap from unified LRU cache ImageCacheService.
    /// Runs on UI thread: caller chain of BeginLoad does not use
    /// ConfigureAwait(false), continuation returns via UI SynchronizationContext.
    /// Shared bitmap — IsOwned=false, do not call Dispose.
    /// </summary>
    private static async Task LoadHttpAsync(Image image, string url, int decodeWidth, CancellationToken ct)
    {
        _imageCache ??= AppEntry.Services.GetService<ImageCacheService>();
        if (_imageCache == null) return;

        var bitmap = await _imageCache.GetImageAsync(url, decodeWidth, ct);

        if (bitmap == null || ct.IsCancellationRequested || GetSource(image) != url) return;

        DisposeOwned(image);
        image.Source = bitmap;
        image.SetValue(IsOwnedProperty, false);
    }

    /// <summary>
    /// avares:// embedded resource: decoded on threadpool, bitmap is owned by control.
    /// Task.Run is offloaded from UI thread: DecodeToWidth on large resources can
    /// take several ms and cause UI jank if called synchronously.
    /// </summary>
    private static async Task LoadAvaresAsync(Image image, string url, int decodeWidth, CancellationToken ct)
    {
        var uri = new Uri(url);
        if (!Avalonia.Platform.AssetLoader.Exists(uri)) return;

        byte[] bytes;
        using (var assetStream = Avalonia.Platform.AssetLoader.Open(uri))
        {
            bytes = new byte[assetStream.Length];
            _ = await assetStream.ReadAsync(bytes, ct);
        }

        if (ct.IsCancellationRequested || GetSource(image) != url) return;

        var bitmap = await Task.Run(() =>
        {
            using var stream = new MemoryStream(bytes);
            return decodeWidth > 0
                ? Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.MediumQuality)
                : new Bitmap(stream);
        }, ct);

        if (ct.IsCancellationRequested || GetSource(image) != url)
        {
            bitmap.Dispose();
            return;
        }

        DisposeOwned(image);
        image.Source = bitmap;
        image.SetValue(IsOwnedProperty, true);
    }

    /// <summary>
    /// Local file: decoded on threadpool, bitmap is owned by control.
    /// FileStream with useAsync:false is more efficient on threadpool (no async I/O overhead).
    /// </summary>
    private static async Task LoadLocalAsync(Image image, string url, int decodeWidth, CancellationToken ct)
    {
        var path = ResolveLocalPath(url);
        if (path == null || !File.Exists(path))
        {
            DisposeOwned(image);
            image.Source = null;
            return;
        }

        var bitmap = await Task.Run(() =>
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, useAsync: false);

            return decodeWidth > 0
                ? Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.MediumQuality)
                : new Bitmap(stream);
        }, ct);

        if (ct.IsCancellationRequested || GetSource(image) != url)
        {
            bitmap.Dispose();
            return;
        }

        DisposeOwned(image);
        image.Source = bitmap;
        image.SetValue(IsOwnedProperty, true);
    }

    #endregion

    #region Cleanup

    /// <summary>
    /// Disposes current bitmap only if it is "owned" (avares/local).
    /// Do not dispose HTTP bitmaps from LRU cache: cache manages lifecycle,
    /// Image.Source holds the last reference until Source changes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DisposeOwned(Image image)
    {
        if (image.Source is Bitmap old && image.GetValue(IsOwnedProperty))
        {
            image.SetValue(IsOwnedProperty, false);
            old.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string? ResolveLocalPath(string url)
    {
        if (url.StartsWith(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.LocalPath : null;

        return Path.IsPathRooted(url) ? url : null;
    }

    #endregion
}