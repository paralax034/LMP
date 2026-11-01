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

    /// <summary>
    /// Число локальных треков, уже связанных с облаком (есть <c>SetVideoId</c>), но удалённых из облачного
    /// плейлиста на другом устройстве. Слияние удаляет их локально вместо повторной выгрузки.
    /// </summary>
    public int CloudRemovedTrackCount { get; init; }

    public string? YoutubePlaylistId { get; init; }

    /// <summary>
    /// Полный снимок данных YouTube Music, использованный для построения диффа.
    /// Исключает повторные сетевые запросы и устраняет утечки памяти в singleton-сервисах.
    /// </summary>
    public Youtube.Music.FullPlaylistSyncData? CachedCloudData { get; init; }

    public bool NameDiffers => !string.Equals(LocalName, CloudName, StringComparison.Ordinal);
    public bool DescriptionDiffers => !string.Equals(LocalDescription ?? string.Empty, CloudDescription ?? string.Empty, StringComparison.Ordinal);
    public bool ThumbnailDiffers => !ThumbnailComparer.AreEquivalent(LocalThumbnailUrl, CloudThumbnailUrl, YoutubePlaylistId);
    public bool TracksDiffer => LocalOnlyTrackCount > 0 || CloudOnlyTrackCount > 0 || CloudRemovedTrackCount > 0;
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

/// <summary>
/// Запись об удаляемом из облачного плейлиста треке с сохранённым связующим идентификатором <c>SetVideoId</c>.
/// </summary>
public sealed class CloudTrackRemoveEntry
{
    public required string TrackId { get; init; }
    public string? SetVideoId { get; set; }
}

/// <summary>
/// Запись о мутации статуса «Мне нравится» для трека с сохранением базового состояния для аннигиляции пар.
/// </summary>
public sealed class CloudLikeMutationEntry
{
    public required string TrackId { get; init; }
    public bool BaselineLiked { get; init; }
    public bool TargetLiked { get; set; }
}

/// <summary>
/// Коалесцированное состояние ожидающих облачных мутаций для одного плейлиста или системной корзины лайков.
/// Сериализуется через <see cref="AppJsonContext"/> в таблицу <c>CloudMutationOutbox</c>.
/// </summary>
public sealed class CloudPlaylistOutboxState
{
    /// <summary>Идентификатор локального плейлиста или <c>__likes__</c> для системной очереди лайков.</summary>
    public required string TargetId { get; init; }

    /// <summary>Идентификатор владельца локальной сессии (<c>OwnerId</c>).</summary>
    public required string OwnerId { get; init; }

    /// <summary>Отображаемое имя плейлиста для карточки задачи в центре уведомлений.</summary>
    public string? PlaylistDisplayName { get; set; }

    /// <summary>Облачный идентификатор плейлиста на YouTube.</summary>
    public string? YoutubePlaylistId { get; set; }

    /// <summary>Флаг необходимости создания нового плейлиста в YouTube на Фазе 0 (оффлайн-оптимизм).</summary>
    public bool PendingCreatePlaylist { get; set; }

    /// <summary>Флаг необходимости удаления плейлиста из YouTube (терминальная операция).</summary>
    public bool PendingDeletePlaylist { get; set; }

    /// <summary>Треки, ожидающие пакетного добавления в облачный плейлист (Фаза 2).</summary>
    public List<string> PendingAddTrackIds { get; set; } = [];

    /// <summary>Треки, ожидающие пакетного удаления из облачного плейлиста (Фаза 1).</summary>
    public List<CloudTrackRemoveEntry> PendingRemoveTracks { get; set; } = [];

    /// <summary>Флаг наличия ожидающей синхронизации порядка треков (Фаза 3).</summary>
    public bool HasPendingReorder { get; set; }

    /// <summary>Исходный порядок треков до начала серии локальных перетаскиваний (для аннигиляции и LIS-сжатия).</summary>
    public List<string>? BaselineTrackOrder { get; set; }

    /// <summary>Целевой локальный порядок треков после завершения перетаскиваний.</summary>
    public List<string>? TargetTrackOrder { get; set; }

    /// <summary>Флаг наличия ожидающего переименования плейлиста (Last-Write-Wins).</summary>
    public bool HasPendingName { get; set; }

    /// <summary>Базовое имя плейлиста до начала серии переименований.</summary>
    public string? BaselineName { get; set; }

    /// <summary>Целевое имя плейлиста для отправки в YouTube.</summary>
    public string? TargetName { get; set; }

    /// <summary>Флаг наличия ожидающего изменения описания плейлиста (Last-Write-Wins).</summary>
    public bool HasPendingDescription { get; set; }

    /// <summary>Базовое описание плейлиста до начала редактирования.</summary>
    public string? BaselineDescription { get; set; }

    /// <summary>Целевое описание плейлиста для отправки в YouTube.</summary>
    public string? TargetDescription { get; set; }

    /// <summary>Флаг наличия ожидающей выгрузки пользовательской обложки через Scotty (Last-Write-Wins).</summary>
    public bool HasPendingThumbnail { get; set; }

    /// <summary>Целевой путь или URL обложки для выгрузки через Scotty.</summary>
    public string? TargetThumbnailUrl { get; set; }

    /// <summary>Коалесцированные мутации лайков (используется при <c>TargetId == "__likes__"</c>).</summary>
    public List<CloudLikeMutationEntry> PendingLikes { get; set; } = [];

    /// <summary>Количество неудачных сетевых попыток сброса состояния.</summary>
    public int RetryCount { get; set; }

    /// <summary>Метка времени первой постановки в текущее окно коалесценции в UTC-тиках.</summary>
    public long FirstEnqueuedAtUtcTicks { get; set; }

    /// <summary>Метка времени последнего изменения в UTC-тиках.</summary>
    public long UpdatedAtUtcTicks { get; set; }

    /// <summary>
    /// Возвращает <see langword="true"/>, если все накопленные мутации были взаимно аннигилированы или выполнены.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty =>
        !PendingCreatePlaylist &&
        !PendingDeletePlaylist &&
        PendingAddTrackIds.Count == 0 &&
        PendingRemoveTracks.Count == 0 &&
        !HasPendingReorder &&
        !HasPendingName &&
        !HasPendingDescription &&
        !HasPendingThumbnail &&
        PendingLikes.Count == 0;
}
