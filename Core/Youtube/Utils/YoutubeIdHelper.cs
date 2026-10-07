using System.Runtime.CompilerServices;

namespace LMP.Core.Youtube.Utils;

/// <summary>
/// Высокопроизводительные утилиты для обработки идентификаторов YouTube и метаданных медиа-потоков.
/// Минимизируют аллокации памяти в куче за счет эффективной работы со Span.
/// </summary>
public static class YoutubeIdHelper
{
    public const string VideoIdPrefix = "yt_";
    public const string PlaylistIdPrefix = "yt_pl_";

    private const string SystemLikedVideosId = "LL";
    private const string SystemMusicLikedId = "LM";
    private const string SystemWatchLaterId = "WL";

    private const string BrowsePrefix = "VL";
    private const string BrowseLikedVideosId = "VLLL";
    private const string BrowseMusicLikedId = "VLLM";
    private const string BrowseWatchLaterId = "VLWL";

    /// <summary>
    /// Быстро извлекает чистый YouTube ID без префиксов "yt_" или "yt_pl_" в виде Span без аллокаций.
    /// </summary>
    /// <param name="id">Исходный идентификатор.</param>
    /// <returns>Очищенный сегмент символов.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<char> ExtractRawIdSpan(ReadOnlySpan<char> id)
    {
        var trimmed = id.Trim();
        if (trimmed.StartsWith(PlaylistIdPrefix.AsSpan(), StringComparison.Ordinal))
            return trimmed[PlaylistIdPrefix.Length..];
        if (trimmed.StartsWith(VideoIdPrefix.AsSpan(), StringComparison.Ordinal))
            return trimmed[VideoIdPrefix.Length..];
        return trimmed;
    }

    /// <summary>
    /// Извлекает чистый YouTube ID как строку. 
    /// Если префиксы отсутствуют, возвращает исходную строку без выделения новой памяти.
    /// </summary>
    /// <param name="id">Исходный идентификатор.</param>
    /// <returns>Очищенная строка идентификатора.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string ExtractRawId(string id)
    {
        if (string.IsNullOrEmpty(id))
            return string.Empty;

        if (id.StartsWith(PlaylistIdPrefix, StringComparison.Ordinal))
            return id[PlaylistIdPrefix.Length..];
        if (id.StartsWith(VideoIdPrefix, StringComparison.Ordinal))
            return id[VideoIdPrefix.Length..];

        var span = id.AsSpan();
        var trimmed = span.Trim();
        if (trimmed.Length == span.Length)
            return id;

        if (trimmed.StartsWith(PlaylistIdPrefix.AsSpan(), StringComparison.Ordinal))
            return new string(trimmed[PlaylistIdPrefix.Length..]);
        if (trimmed.StartsWith(VideoIdPrefix.AsSpan(), StringComparison.Ordinal))
            return new string(trimmed[VideoIdPrefix.Length..]);

        return new string(trimmed);
    }

    /// <summary>
    /// Централизованно преобразует идентификатор плейлиста в канонический browseId InnerTube API.
    /// Корректно нормализует системные разделы (LL -> VLLL, LM -> VLLM, WL -> VLWL) и добавляет префикс VL.
    /// </summary>
    /// <param name="playlistId">Идентификатор плейлиста.</param>
    /// <returns>Канонический идентификатор для browse-запроса.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string NormalizePlaylistBrowseId(string playlistId)
    {
        if (string.IsNullOrEmpty(playlistId))
            return string.Empty;

        var rawId = ExtractRawId(playlistId);

        if (string.Equals(rawId, SystemLikedVideosId, StringComparison.Ordinal))
            return BrowseLikedVideosId;
        if (string.Equals(rawId, SystemMusicLikedId, StringComparison.Ordinal))
            return BrowseMusicLikedId;
        if (string.Equals(rawId, SystemWatchLaterId, StringComparison.Ordinal))
            return BrowseWatchLaterId;

        if (rawId.StartsWith(BrowsePrefix, StringComparison.Ordinal))
            return rawId;

        return string.Concat(BrowsePrefix, rawId);
    }

    /// <summary>
    /// Безопасно сопоставляет строковое представление контейнера с перечислением <see cref="AudioFormat"/>.
    /// </summary>
    /// <param name="container">Имя контейнера.</param>
    /// <returns>Соответствующий формат аудио.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AudioFormat MapContainerToFormat(string? container)
    {
        if (string.IsNullOrWhiteSpace(container))
            return AudioFormat.Unknown;

        var span = container.AsSpan().Trim();

        if (span.Equals("webm", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.WebM;
        if (span.Equals("mp4", StringComparison.OrdinalIgnoreCase) || span.Equals("m4a", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.Mp4;
        if (span.Equals("ogg", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.Ogg;
        if (span.Equals("m3u8", StringComparison.OrdinalIgnoreCase) || span.Equals("hls", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.Hls;

        return Enum.TryParse<AudioFormat>(container, true, out var format) ? format : AudioFormat.Unknown;
    }
}