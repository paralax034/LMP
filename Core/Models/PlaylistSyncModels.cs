using System.Runtime.CompilerServices;

namespace LMP.Core.Models;

/// <summary>
/// Стратегия разрешения конфликтов при двусторонней синхронизации треков.
/// </summary>
public enum PlaylistSyncStrategy
{
    Merge = 0,
    ReplaceLocal = 1,
    ReplaceCloud = 2
}

/// <summary>
/// Набор параметров и флагов, выбранных для применения синхронизации.
/// </summary>
public sealed class PlaylistSyncOptions
{
    public PlaylistSyncStrategy Strategy { get; init; } = PlaylistSyncStrategy.Merge;
    public bool SyncTracks { get; init; } = true;
    public bool SyncName { get; init; } = true;
    public bool SyncDescription { get; init; } = true;
    public bool SyncThumbnail { get; init; }
}

/// <summary>
/// Утилита детерминированного сопоставления обложек плейлиста без ложных срабатываний на токены YouTube.
/// </summary>
public static class ThumbnailComparer
{
    private const string YtEmptyStatePlaceholder = "playlist-empty-state";

    /// <summary>
    /// Нормализует строковый URL обложки, отсекая динамические query-параметры.
    /// </summary>
    /// <param name="url">Исходный URL обложки.</param>
    /// <returns>Нормализованный URL без параметров запроса либо <c>null</c>.</returns>
    public static string? Normalize(string? url)
    {
        var span = NormalizeToSpan(url);
        if (span.IsEmpty) return null;
        if (url is not null && span.Length == url.Length) return url;
        return span.ToString();
    }

    /// <summary>
    /// Zero-allocation нормализация URL обложки через сегмент памяти.
    /// </summary>
    /// <param name="url">Исходный URL обложки.</param>
    /// <returns>Сегмент символов без query-параметров либо пустой сегмент.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<char> NormalizeToSpan(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return default;

        var span = url.AsSpan().Trim();
        if (span.Contains(YtEmptyStatePlaceholder.AsSpan(), StringComparison.OrdinalIgnoreCase))
            return default;

        if (span.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var qIdx = span.IndexOf('?');
            if (qIdx >= 0)
                span = span[..qIdx];

            // Google CDN (googleusercontent.com / ggpht.com) кодирует размер через суффикс '=s1200' или '=w544-h544'
            if (span.Contains("googleusercontent.com".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                span.Contains("ggpht.com".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                int lastSlash = span.LastIndexOf('/');
                if (lastSlash >= 0)
                {
                    int eqOffset = span[(lastSlash + 1)..].IndexOf('=');
                    if (eqOffset > 0)
                        span = span[..(lastSlash + 1 + eqOffset)];
                }
            }

            return span;
        }

        return span;
    }

    /// <summary>
    /// Проверяет, размещена ли обложка на внутренних CDN-серверах YouTube / Google.
    /// Такие обложки управляются сервером и не загружаются повторно через Scotty протокол.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsYoutubeHosted(ReadOnlySpan<char> url)
    {
        if (url.IsEmpty) return false;
        return url.Contains("ytimg.com".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
               url.Contains("ggpht.com".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
               url.Contains("googleusercontent.com".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
               url.Contains("gstatic.com".AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Проверяет, размещена ли обложка на внутренних CDN-серверах YouTube / Google.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsYoutubeHosted(string? url) =>
        !string.IsNullOrWhiteSpace(url) && IsYoutubeHosted(url.AsSpan());

    /// <summary>
    /// Проверяет эквивалентность локальной и облачной обложки без аллокаций в куче.
    /// </summary>
    /// <param name="localUrl">Локальный путь или URL обложки.</param>
    /// <param name="cloudUrl">Облачный URL обложки YouTube.</param>
    /// <param name="youtubeId">Идентификатор плейлиста для проверки серверных коллажей.</param>
    /// <returns><c>true</c>, если обложки эквивалентны; иначе — <c>false</c>.</returns>
    public static bool AreEquivalent(string? localUrl, string? cloudUrl, string? youtubeId)
    {
        var normLocal = NormalizeToSpan(localUrl);
        var normCloud = NormalizeToSpan(cloudUrl);

        if (normLocal.IsEmpty && normCloud.IsEmpty)
            return true;

        if (normLocal.IsEmpty || normCloud.IsEmpty)
            return false;

        if (normLocal.Equals(normCloud, StringComparison.OrdinalIgnoreCase))
            return true;

        // Если обе ссылки принадлежат CDN YouTube/Google (например, i.ytimg.com и yt3.googleusercontent.com),
        // обе являются серверными представлениями одной облачной обложки и не требуют Scotty-выгрузки
        if (IsYoutubeHosted(normLocal) && IsYoutubeHosted(normCloud))
            return true;

        if (!string.IsNullOrEmpty(youtubeId) && youtubeId.Length <= 128)
        {
            Span<char> collageBuffer = stackalloc char[youtubeId.Length + 8];
            int written = 0;
            "/pl_c/".AsSpan().CopyTo(collageBuffer);
            written += 6;
            youtubeId.AsSpan().CopyTo(collageBuffer[written..]);
            written += youtubeId.Length;
            collageBuffer[written++] = '/';

            var collageSpan = collageBuffer[..written];
            if (normLocal.Contains(collageSpan, StringComparison.OrdinalIgnoreCase) &&
                normCloud.Contains(collageSpan, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Снимок различий между локальным состоянием плейлиста и облаком YouTube.
/// </summary>
public sealed class PlaylistSyncPreview
{
    public required string LocalName { get; init; }
    public required string CloudName { get; init; }
    public string? LocalDescription { get; init; }
    public string? CloudDescription { get; init; }
    public string? LocalThumbnailUrl { get; init; }
    public string? CloudThumbnailUrl { get; init; }
    public int LocalOnlyTrackCount { get; init; }
    public int CloudOnlyTrackCount { get; init; }
    public int CommonTrackCount { get; init; }
    public string? YoutubePlaylistId { get; init; }

    /// <summary>
    /// Полный снимок данных YouTube Music, использованный для построения диффа.
    /// Исключает повторные сетевые запросы и устраняет утечки памяти в singleton-сервисах.
    /// </summary>
    public Youtube.Music.FullPlaylistSyncData? CachedCloudData { get; init; }

    public bool NameDiffers => !string.Equals(LocalName, CloudName, StringComparison.Ordinal);
    public bool DescriptionDiffers => !string.Equals(LocalDescription ?? string.Empty, CloudDescription ?? string.Empty, StringComparison.Ordinal);
    public bool ThumbnailDiffers => !ThumbnailComparer.AreEquivalent(LocalThumbnailUrl, CloudThumbnailUrl, YoutubePlaylistId);
    public bool TracksDiffer => LocalOnlyTrackCount > 0 || CloudOnlyTrackCount > 0;
    public bool HasAnyDifference => NameDiffers || DescriptionDiffers || ThumbnailDiffers || TracksDiffer;
}

/// <summary>
/// Результат выполнения операции синхронизации плейлиста.
/// </summary>
public sealed class PlaylistSyncResult
{
    public bool Success { get; init; }
    public bool MetadataChanged { get; init; }
    public int TracksAddedLocally { get; init; }
    public int TracksAddedToCloud { get; init; }
    public int TracksRemovedLocally { get; init; }
    public int TracksRemovedFromCloud { get; init; }
    public string? ErrorMessage { get; init; }

    public string Summary => Success
        ? $"MetadataChanged={MetadataChanged}, +Local={TracksAddedLocally}, +Cloud={TracksAddedToCloud}, -Local={TracksRemovedLocally}, -Cloud={TracksRemovedFromCloud}"
        : $"Failed: {ErrorMessage}";

    public static PlaylistSyncResult Fail(string error) => new() { Success = false, ErrorMessage = error };
}