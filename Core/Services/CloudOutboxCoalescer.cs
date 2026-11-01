using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using LMP.Core.Youtube.Exceptions;

namespace LMP.Core.Services;

/// <summary>
/// Класс сбоя отправки облачной мутации, определяющий политику повтора в очереди Outbox.
/// </summary>
public enum CloudOutboxFailureKind : byte
{
    /// <summary>Операция отменена владельцем токена (закрытие приложения, отмена пользователем).</summary>
    Canceled = 0,

    /// <summary>Временный сбой сети, таймаут HttpClient, кулдаун, 5xx/429, занятость SQLite.</summary>
    Transient = 1,

    /// <summary>Сессия YouTube недействительна (401, LoginRequired): очередь паркуется до смены авторизации.</summary>
    AuthRequired = 2,

    /// <summary>Облачный ресурс не существует (404, плейлист недоступен).</summary>
    NotFound = 3,

    /// <summary>Нет прав на изменение облачного ресурса (403).</summary>
    Forbidden = 4,

    /// <summary>Неустранимая ошибка запроса (400, некорректный ответ).</summary>
    Permanent = 5
}

/// <summary>
/// Чистая (без I/O) машина состояний коалесценции облачных мутаций Outbox.
/// Все методы мутируют переданное состояние и должны вызываться под блокировкой владельца состояния.
/// Учитывает операции «в полёте», чтобы аннигиляция не уничтожала намерение пользователя,
/// уже отправленное в облако текущим флашем.
/// </summary>
public static class CloudOutboxCoalescer
{
    /// <summary>Плейсхолдер YouTube вместо реального <c>SetVideoId</c>, который нельзя сохранять в БД.</summary>
    public const string PlaceholderSetVideoId = "to_be_updated_by_client";

    private const int LinearScanThreshold = 8;

    /// <summary>
    /// Проверяет, что <c>SetVideoId</c> пригоден для адресации элемента облачного плейлиста.
    /// </summary>
    public static bool IsUsableSetVideoId([NotNullWhen(true)] string? setVideoId) =>
        !string.IsNullOrEmpty(setVideoId) &&
        !setVideoId.Equals(PlaceholderSetVideoId, StringComparison.OrdinalIgnoreCase);

    #region Likes

    /// <summary>
    /// Применяет намерение «Мне нравится» к корзине лайков.
    /// Пока лайк трека отправляется (<paramref name="isInFlight"/>), аннигиляция запрещена:
    /// новое целевое значение сохраняется и будет отправлено следующим флашем.
    /// </summary>
    /// <returns><see langword="true"/>, если запись аннигилирована (возврат к базовому состоянию).</returns>
    public static bool ApplyLike(
        CloudPlaylistOutboxState state,
        string trackId,
        bool targetLiked,
        bool? baselineLiked,
        bool isInFlight)
    {
        int idx = IndexOfLike(state.PendingLikes, trackId);
        if (idx >= 0)
        {
            var entry = state.PendingLikes[idx];
            if (!isInFlight && entry.BaselineLiked == targetLiked)
            {
                state.PendingLikes.RemoveAt(idx);
                return true;
            }

            entry.TargetLiked = targetLiked;
            return false;
        }

        bool initialBaseline = isInFlight ? !targetLiked : baselineLiked ?? !targetLiked;
        if (initialBaseline != targetLiked)
        {
            state.PendingLikes.Add(new CloudLikeMutationEntry
            {
                TrackId = trackId,
                BaselineLiked = initialBaseline,
                TargetLiked = targetLiked
            });
        }

        return false;
    }

    /// <summary>
    /// Фиксирует успешную отправку лайка. Если пользователь успел изменить цель во время отправки,
    /// запись сохраняется с новым базовым значением, равным подтверждённому облачному состоянию.
    /// </summary>
    public static void CommitLike(CloudPlaylistOutboxState state, string trackId, bool syncedLiked)
    {
        int idx = IndexOfLike(state.PendingLikes, trackId);
        if (idx < 0)
            return;

        var entry = state.PendingLikes[idx];
        if (entry.TargetLiked == syncedLiked)
        {
            state.PendingLikes.RemoveAt(idx);
            return;
        }

        state.PendingLikes[idx] = new CloudLikeMutationEntry
        {
            TrackId = entry.TrackId,
            BaselineLiked = syncedLiked,
            TargetLiked = entry.TargetLiked
        };
    }

    /// <summary>
    /// Удаляет запись лайка без фиксации (неустранимая ошибка отправки).
    /// </summary>
    public static void DropLike(CloudPlaylistOutboxState state, string trackId)
    {
        int idx = IndexOfLike(state.PendingLikes, trackId);
        if (idx >= 0)
            state.PendingLikes.RemoveAt(idx);
    }

    /// <summary>
    /// Возвращает актуальное целевое значение лайка трека из живого состояния.
    /// </summary>
    public static bool TryGetLikeTarget(CloudPlaylistOutboxState state, string trackId, out bool targetLiked)
    {
        int idx = IndexOfLike(state.PendingLikes, trackId);
        targetLiked = idx >= 0 && state.PendingLikes[idx].TargetLiked;
        return idx >= 0;
    }

    #endregion

    #region Tracks

    /// <summary>
    /// Применяет пакетное добавление треков. Отложенное удаление того же трека аннигилируется,
    /// если оно не отправляется прямо сейчас; иначе удаление сохраняется, а добавление ставится как компенсация.
    /// </summary>
    /// <returns>Восстанавливаемые пары <c>(TrackId, SetVideoId)</c> аннигилированных удалений либо <see langword="null"/>.</returns>
    public static List<(string TrackId, string SetVideoId)>? ApplyAddTracks(
        CloudPlaylistOutboxState state,
        IReadOnlyList<string> trackIds,
        IReadOnlySet<string>? inFlightRemoves)
    {
        List<(string TrackId, string SetVideoId)>? restored = null;
        HashSet<string>? addIndex = trackIds.Count > LinearScanThreshold
            ? new HashSet<string>(state.PendingAddTrackIds, StringComparer.Ordinal)
            : null;
        HashSet<string>? targetOrderIndex = state.HasPendingReorder && state.TargetTrackOrder != null && trackIds.Count > LinearScanThreshold
            ? new HashSet<string>(state.TargetTrackOrder, StringComparer.Ordinal)
            : null;

        for (int i = 0; i < trackIds.Count; i++)
        {
            var trackId = trackIds[i];
            if (string.IsNullOrEmpty(trackId) || !trackId.StartsWith("yt_", StringComparison.Ordinal))
                continue;

            var restoredSetVideoId = ApplyAddOne(state, trackId, inFlightRemoves, addIndex);
            if (restoredSetVideoId != null)
                (restored ??= []).Add((trackId, restoredSetVideoId));

            if (state.HasPendingReorder && state.TargetTrackOrder != null &&
                (targetOrderIndex != null ? targetOrderIndex.Add(trackId) : !ContainsOrdinal(state.TargetTrackOrder, trackId)))
            {
                state.TargetTrackOrder.Add(trackId);
            }
        }

        return restored;
    }

    /// <summary>
    /// Применяет пакетное удаление треков. Отложенное добавление аннигилируется, если оно не отправляется
    /// прямо сейчас; иначе добавление сохраняется, а удаление ставится как компенсация (без потери намерения).
    /// </summary>
    public static void ApplyRemoveTracks(
        CloudPlaylistOutboxState state,
        IReadOnlyList<(string TrackId, string? SetVideoId)> removedTracks,
        IReadOnlySet<string>? inFlightAdds)
    {
        HashSet<string>? removedSet = removedTracks.Count > LinearScanThreshold
            ? new HashSet<string>(removedTracks.Count, StringComparer.Ordinal)
            : null;

        for (int i = 0; i < removedTracks.Count; i++)
        {
            var (trackId, setVideoId) = removedTracks[i];
            if (string.IsNullOrEmpty(trackId))
                continue;

            ApplyRemoveOne(state, trackId, setVideoId, inFlightAdds);

            if (removedSet != null)
            {
                removedSet.Add(trackId);
            }
            else
            {
                RemoveFirstOrdinal(state.BaselineTrackOrder, trackId);
                RemoveFirstOrdinal(state.TargetTrackOrder, trackId);
            }
        }

        if (removedSet != null)
        {
            RemoveAllInSet(state.BaselineTrackOrder, removedSet);
            RemoveAllInSet(state.TargetTrackOrder, removedSet);
        }

        CollapseIdentityReorder(state);
    }

    /// <summary>
    /// Применяет перестановку треков. Для ещё не созданного в облаке плейлиста (вне полёта)
    /// перестановка применяется прямо к порядку отложенных добавлений без отдельных move-операций.
    /// </summary>
    public static void ApplyReorder(
        CloudPlaylistOutboxState state,
        IReadOnlyList<string> baselineOrder,
        IReadOnlyList<string> newOrder,
        bool isTargetInFlight)
    {
        if (IsUnsentCreate(state, isTargetInFlight))
        {
            ReorderPendingAdds(state.PendingAddTrackIds, newOrder);
            state.HasPendingReorder = false;
            state.BaselineTrackOrder = null;
            state.TargetTrackOrder = null;
            return;
        }

        state.BaselineTrackOrder ??= [.. baselineOrder];
        state.TargetTrackOrder = [.. newOrder];
        state.HasPendingReorder = true;
        CollapseIdentityReorder(state);
    }

    /// <summary>
    /// Создание плейлиста ещё не отправлено и не в полёте: Фаза 0 возьмёт актуальные название,
    /// описание и порядок треков из локального состояния, отдельные правки для них не нужны.
    /// </summary>
    public static bool IsUnsentCreate(CloudPlaylistOutboxState state, bool isTargetInFlight) =>
        state.PendingCreatePlaylist && string.IsNullOrEmpty(state.YoutubePlaylistId) && !isTargetInFlight;

    /// <summary>
    /// Снимает отдельные правки названия и описания, которые уйдут в запрос создания плейлиста.
    /// </summary>
    public static void DropMetadataAbsorbedByCreate(CloudPlaylistOutboxState state)
    {
        ClearPendingName(state);
        ClearPendingDescription(state);
    }

    /// <summary>
    /// Фиксирует название и описание, отправленные в запросе создания. Правка, сделанная во время
    /// Фазы 0 и отличающаяся от отправленного значения, остаётся в очереди с новой базой.
    /// </summary>
    public static void CommitMetadataSentWithCreate(
        CloudPlaylistOutboxState state,
        string sentName,
        string sentDescription)
    {
        if (state.HasPendingName)
        {
            if (string.Equals(state.TargetName, sentName, StringComparison.Ordinal))
                ClearPendingName(state);
            else
                state.BaselineName = sentName;
        }

        if (state.HasPendingDescription)
        {
            if (string.Equals(state.TargetDescription?.Trim(), sentDescription, StringComparison.Ordinal))
                ClearPendingDescription(state);
            else
                state.BaselineDescription = sentDescription;
        }
    }

    /// <summary>
    /// Переводит состояние в терминальное удаление облачного плейлиста, отбрасывая остальные мутации.
    /// </summary>
    public static void ApplyDeletePlaylist(CloudPlaylistOutboxState state)
    {
        state.PendingCreatePlaylist = false;
        state.PendingAddTrackIds.Clear();
        state.PendingRemoveTracks.Clear();
        state.HasPendingReorder = false;
        state.BaselineTrackOrder = null;
        state.TargetTrackOrder = null;
        state.HasPendingName = false;
        state.BaselineName = null;
        state.TargetName = null;
        state.HasPendingDescription = false;
        state.BaselineDescription = null;
        state.TargetDescription = null;
        state.HasPendingThumbnail = false;
        state.TargetThumbnailUrl = null;
        state.PendingDeletePlaylist = true;
    }

    /// <summary>
    /// Фиксирует успешное добавление: удаляет отправленные треки из очереди и дописывает полученные
    /// <c>SetVideoId</c> в компенсирующие удаления, поставленные во время полёта.
    /// </summary>
    public static void CommitAdds(
        CloudPlaylistOutboxState state,
        IReadOnlyList<string> completedAdds,
        IReadOnlyList<(string TrackId, string SetVideoId)> mappings)
    {
        for (int i = 0; i < completedAdds.Count; i++)
            RemoveFirstOrdinal(state.PendingAddTrackIds, completedAdds[i]);

        for (int i = 0; i < mappings.Count; i++)
        {
            int removeIdx = IndexOfRemove(state.PendingRemoveTracks, mappings[i].TrackId);
            if (removeIdx >= 0 && !IsUsableSetVideoId(state.PendingRemoveTracks[removeIdx].SetVideoId))
                state.PendingRemoveTracks[removeIdx].SetVideoId = mappings[i].SetVideoId;
        }
    }

    /// <summary>
    /// Нормализует конфликтующие пары «добавление + удаление» одного трека после завершения флаша:
    /// добавление в полёте + удаление → итог «трека нет»; удаление в полёте + добавление → итог «трек есть».
    /// </summary>
    /// <returns><see langword="true"/>, если состояние изменилось.</returns>
    public static bool ResolveInFlightCompensations(
        CloudPlaylistOutboxState state,
        IReadOnlySet<string> sentAdds,
        IReadOnlySet<string> sentRemoves)
    {
        if (sentAdds.Count == 0 && sentRemoves.Count == 0)
            return false;

        bool changed = false;
        for (int i = state.PendingAddTrackIds.Count - 1; i >= 0; i--)
        {
            var trackId = state.PendingAddTrackIds[i];
            int removeIdx = IndexOfRemove(state.PendingRemoveTracks, trackId);
            if (removeIdx < 0)
                continue;

            if (sentAdds.Contains(trackId))
            {
                state.PendingAddTrackIds.RemoveAt(i);
                changed = true;
            }
            else if (sentRemoves.Contains(trackId))
            {
                state.PendingAddTrackIds.RemoveAt(i);
                state.PendingRemoveTracks.RemoveAt(removeIdx);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Сопоставляет отправленные треки с полученными <c>SetVideoId</c> (ответ уже выровнен по запросу),
    /// отбрасывая пустые значения и плейсхолдер <see cref="PlaceholderSetVideoId"/>.
    /// </summary>
    public static List<(string TrackId, string SetVideoId)> MapAddedSetVideoIds(
        IReadOnlyList<string> sentTrackIds,
        IReadOnlyList<string?> returnedSetVideoIds)
    {
        if (returnedSetVideoIds.Count != sentTrackIds.Count)
            return [];

        var mappings = new List<(string TrackId, string SetVideoId)>(sentTrackIds.Count);
        for (int i = 0; i < sentTrackIds.Count; i++)
        {
            var sid = returnedSetVideoIds[i];
            if (IsUsableSetVideoId(sid))
                mappings.Add((sentTrackIds[i], sid));
        }

        return mappings;
    }

    #endregion

    #region Cloud Rebase

    /// <summary>
    /// Определяет, что YouTube отклонил саму правку (HTTP 400 или <c>STATUS_FAILED</c>), а не транспорт.
    /// Типичная причина — устаревшие <c>SetVideoId</c> после изменения плейлиста на другом устройстве.
    /// </summary>
    public static bool IsRejectedEdit(Exception ex) =>
        ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.BadRequest } ||
        ex.GetType() == typeof(YoutubeExplodeException);

    /// <summary>
    /// Перебазирует удаления на актуальный облачный состав: запись с реальным <c>SetVideoId</c>, которого
    /// уже нет в облаке, считается выполненной; запись без пригодного <c>SetVideoId</c> разрешается
    /// первым свободным вхождением того же трека. Повторное использование одного <c>SetVideoId</c> исключено.
    /// </summary>
    public static List<CloudTrackRemoveEntry> SelectRemovalsStillInCloud(
        IReadOnlyList<CloudTrackRemoveEntry> removals,
        IReadOnlyList<(string TrackId, string SetVideoId)> cloudOrder)
    {
        var result = new List<CloudTrackRemoveEntry>(removals.Count);
        if (removals.Count == 0 || cloudOrder.Count == 0)
            return result;

        var cloudSetIds = new HashSet<string>(cloudOrder.Count, StringComparer.Ordinal);
        for (int i = 0; i < cloudOrder.Count; i++)
            cloudSetIds.Add(cloudOrder[i].SetVideoId);

        var used = new HashSet<string>(removals.Count, StringComparer.Ordinal);
        for (int i = 0; i < removals.Count; i++)
        {
            var entry = removals[i];
            string? resolved = null;

            if (IsUsableSetVideoId(entry.SetVideoId))
            {
                if (cloudSetIds.Contains(entry.SetVideoId))
                    resolved = entry.SetVideoId;
            }
            else
            {
                for (int j = 0; j < cloudOrder.Count; j++)
                {
                    var (trackId, setVideoId) = cloudOrder[j];
                    if (string.Equals(trackId, entry.TrackId, StringComparison.Ordinal) && !used.Contains(setVideoId))
                    {
                        resolved = setVideoId;
                        break;
                    }
                }
            }

            if (resolved != null && used.Add(resolved))
                result.Add(new CloudTrackRemoveEntry { TrackId = entry.TrackId, SetVideoId = resolved });
        }

        return result;
    }

    /// <summary>
    /// Строит пару порядков <c>SetVideoId</c> для перестановки поверх актуального облачного состава:
    /// текущий — первые вхождения треков в облаке, целевой — треки целевого порядка, присутствующие в облаке.
    /// Треки, удалённые в облаке, и облачные дубликаты в перестановке не участвуют.
    /// </summary>
    public static (List<string> CurrentSetIds, List<string> DesiredSetIds) BuildRebasedReorder(
        IReadOnlyList<(string TrackId, string SetVideoId)> cloudOrder,
        IReadOnlyList<string> targetTrackOrder)
    {
        var firstSetIdByTrack = new Dictionary<string, string>(cloudOrder.Count, StringComparer.Ordinal);
        var current = new List<string>(cloudOrder.Count);
        for (int i = 0; i < cloudOrder.Count; i++)
        {
            var (trackId, setVideoId) = cloudOrder[i];
            if (firstSetIdByTrack.TryAdd(trackId, setVideoId))
                current.Add(setVideoId);
        }

        var desired = new List<string>(targetTrackOrder.Count);
        var seen = new HashSet<string>(targetTrackOrder.Count, StringComparer.Ordinal);
        for (int i = 0; i < targetTrackOrder.Count; i++)
        {
            var trackId = targetTrackOrder[i];
            if (firstSetIdByTrack.TryGetValue(trackId, out var setVideoId) && seen.Add(trackId))
                desired.Add(setVideoId);
        }

        return (current, desired);
    }

    /// <summary>
    /// Выбирает локальные треки, удалённые в облаке на другом устройстве: у трека есть реальный
    /// <c>SetVideoId</c> (он уже был связан с облаком), но в облачном составе трека нет.
    /// Треки без пригодного <c>SetVideoId</c> считаются ещё не выгруженными и не выбираются.
    /// </summary>
    /// <returns>
    /// Идентификаторы треков либо <see langword="null"/>, если сработал предохранитель: облако пустое
    /// или кандидатов больше <c>max(3, localCount/4)</c> (вероятен частичный ответ облака).
    /// </returns>
    public static HashSet<string>? SelectCloudDeletedTrackIds(
        IReadOnlyList<(string TrackId, string? SetVideoId)> localTracks,
        IReadOnlySet<string> cloudTrackIds)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < localTracks.Count; i++)
        {
            var (trackId, setVideoId) = localTracks[i];
            if (IsUsableSetVideoId(setVideoId) && !cloudTrackIds.Contains(trackId))
                result.Add(trackId);
        }

        if (result.Count == 0)
            return result;

        if (cloudTrackIds.Count == 0 || result.Count > Math.Max(3, localTracks.Count / 4))
            return null;

        return result;
    }

    #endregion

    #region Hydration Merge

    /// <summary>
    /// Сливает живое состояние, накопленное до завершения гидратации, с более старым состоянием из SQLite.
    /// Живые мутации переигрываются поверх восстановленных (базовые значения берутся из БД).
    /// </summary>
    /// <param name="live">Живое (более новое) состояние.</param>
    /// <param name="restored">Восстановленное из БД (более старое) состояние; мутируется и возвращается.</param>
    /// <param name="restoredSetVideoIds">Пары <c>SetVideoId</c> аннигилированных удалений для восстановления в БД.</param>
    public static CloudPlaylistOutboxState MergeRestored(
        CloudPlaylistOutboxState live,
        CloudPlaylistOutboxState restored,
        out List<(string TrackId, string SetVideoId)>? restoredSetVideoIds)
    {
        restoredSetVideoIds = null;

        for (int i = 0; i < live.PendingLikes.Count; i++)
        {
            var like = live.PendingLikes[i];
            ApplyLike(restored, like.TrackId, like.TargetLiked, like.BaselineLiked, isInFlight: false);
        }

        if (!string.IsNullOrEmpty(live.YoutubePlaylistId))
            restored.YoutubePlaylistId = live.YoutubePlaylistId;
        if (!string.IsNullOrEmpty(live.PlaylistDisplayName))
            restored.PlaylistDisplayName = live.PlaylistDisplayName;

        if (live.PendingDeletePlaylist)
        {
            ApplyDeletePlaylist(restored);
            if (string.IsNullOrEmpty(restored.YoutubePlaylistId))
                restored.PendingDeletePlaylist = false;
        }
        else
        {
            if (live.PendingCreatePlaylist)
            {
                restored.PendingDeletePlaylist = false;
                restored.PendingCreatePlaylist = string.IsNullOrEmpty(restored.YoutubePlaylistId);
            }

            for (int i = 0; i < live.PendingRemoveTracks.Count; i++)
            {
                var entry = live.PendingRemoveTracks[i];
                ApplyRemoveOne(restored, entry.TrackId, entry.SetVideoId, inFlightAdds: null);
                RemoveFirstOrdinal(restored.BaselineTrackOrder, entry.TrackId);
                RemoveFirstOrdinal(restored.TargetTrackOrder, entry.TrackId);
            }

            if (live.PendingAddTrackIds.Count > 0)
                restoredSetVideoIds = ApplyAddTracks(restored, live.PendingAddTrackIds, inFlightRemoves: null);

            if (live.HasPendingReorder && live.TargetTrackOrder != null)
                ApplyReorder(restored, live.BaselineTrackOrder ?? live.TargetTrackOrder, live.TargetTrackOrder, isTargetInFlight: false);
            else
                CollapseIdentityReorder(restored);

            if (live.HasPendingName)
            {
                restored.BaselineName ??= live.BaselineName;
                restored.TargetName = live.TargetName;
                restored.HasPendingName = restored.BaselineName == null ||
                                          !string.Equals(restored.BaselineName, live.TargetName, StringComparison.Ordinal);
                if (!restored.HasPendingName)
                {
                    restored.BaselineName = null;
                    restored.TargetName = null;
                }
            }

            if (live.HasPendingDescription)
            {
                restored.BaselineDescription ??= live.BaselineDescription;
                restored.TargetDescription = live.TargetDescription;
                restored.HasPendingDescription = restored.BaselineDescription == null ||
                                                 !string.Equals(restored.BaselineDescription, live.TargetDescription, StringComparison.Ordinal);
                if (!restored.HasPendingDescription)
                {
                    restored.BaselineDescription = null;
                    restored.TargetDescription = null;
                }
            }

            if (live.HasPendingThumbnail)
            {
                restored.HasPendingThumbnail = true;
                restored.TargetThumbnailUrl = live.TargetThumbnailUrl;
            }
        }

        restored.FirstEnqueuedAtUtcTicks = MinPositive(restored.FirstEnqueuedAtUtcTicks, live.FirstEnqueuedAtUtcTicks);
        restored.UpdatedAtUtcTicks = Math.Max(restored.UpdatedAtUtcTicks, live.UpdatedAtUtcTicks);
        restored.RetryCount = 0;
        return restored;
    }

    #endregion

    #region Failure Policy

    /// <summary>
    /// Классифицирует исключение облачной операции для выбора политики повтора.
    /// Таймаут HttpClient (<see cref="OperationCanceledException"/> при неотменённом токене) считается временным.
    /// </summary>
    public static CloudOutboxFailureKind ClassifyFailure(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return CloudOutboxFailureKind.Canceled;

        if (ex is LoginRequiredException)
            return CloudOutboxFailureKind.AuthRequired;

        if (ex is PlaylistUnavailableException)
            return CloudOutboxFailureKind.NotFound;

        if (ex is HttpRequestException httpEx)
        {
            return httpEx.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => CloudOutboxFailureKind.AuthRequired,
                System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone => CloudOutboxFailureKind.NotFound,
                System.Net.HttpStatusCode.Forbidden => CloudOutboxFailureKind.Forbidden,
                System.Net.HttpStatusCode.BadRequest => CloudOutboxFailureKind.Permanent,
                _ => CloudOutboxFailureKind.Transient
            };
        }

        if (ex is BotDetectionException or YoutubeNetworkException or TimeoutException)
            return CloudOutboxFailureKind.Transient;

        if (ex is OperationCanceledException or DbException or IOException)
            return CloudOutboxFailureKind.Transient;

        return YoutubeNetworkException.TryClassify(ex, ct) != null
            ? CloudOutboxFailureKind.Transient
            : CloudOutboxFailureKind.Permanent;
    }

    /// <summary>
    /// Вычисляет экспоненциальную задержку повтора: <c>base·2^(n−1)</c> с ограничением <paramref name="maxMs"/>.
    /// </summary>
    public static int ComputeRetryBackoffMs(int retryCount, int baseMs, int maxMs)
    {
        if (retryCount <= 0)
            return 0;

        long delay = (long)baseMs << Math.Min(retryCount - 1, 16);
        return (int)Math.Min(delay, maxMs);
    }

    #endregion

    #region Ordinal Helpers

    /// <summary>
    /// Поэлементно сравнивает две последовательности идентификаторов (ordinal, без аллокаций).
    /// </summary>
    public static bool SequenceEqualOrdinal(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count)
            return false;

        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static string? ApplyAddOne(
        CloudPlaylistOutboxState state,
        string trackId,
        IReadOnlySet<string>? inFlightRemoves,
        HashSet<string>? addIndex)
    {
        int removeIdx = IndexOfRemove(state.PendingRemoveTracks, trackId);
        bool removeInFlight = removeIdx >= 0 && inFlightRemoves != null && inFlightRemoves.Contains(trackId);

        if (removeIdx >= 0 && !removeInFlight)
        {
            var removedEntry = state.PendingRemoveTracks[removeIdx];
            state.PendingRemoveTracks.RemoveAt(removeIdx);
            return IsUsableSetVideoId(removedEntry.SetVideoId) ? removedEntry.SetVideoId : null;
        }

        bool isNew = addIndex != null ? addIndex.Add(trackId) : !ContainsOrdinal(state.PendingAddTrackIds, trackId);
        if (isNew)
            state.PendingAddTrackIds.Add(trackId);

        return null;
    }

    private static void ApplyRemoveOne(
        CloudPlaylistOutboxState state,
        string trackId,
        string? setVideoId,
        IReadOnlySet<string>? inFlightAdds)
    {
        int addIdx = IndexOfOrdinal(state.PendingAddTrackIds, trackId);
        bool addInFlight = addIdx >= 0 && inFlightAdds != null && inFlightAdds.Contains(trackId);

        if (addIdx >= 0 && !addInFlight)
        {
            state.PendingAddTrackIds.RemoveAt(addIdx);
            return;
        }

        int existingIdx = IndexOfRemove(state.PendingRemoveTracks, trackId);
        if (existingIdx >= 0)
        {
            if (IsUsableSetVideoId(setVideoId))
                state.PendingRemoveTracks[existingIdx].SetVideoId = setVideoId;
            return;
        }

        state.PendingRemoveTracks.Add(new CloudTrackRemoveEntry
        {
            TrackId = trackId,
            SetVideoId = IsUsableSetVideoId(setVideoId) ? setVideoId : null
        });
    }

    private static void ClearPendingName(CloudPlaylistOutboxState state)
    {
        state.HasPendingName = false;
        state.BaselineName = null;
        state.TargetName = null;
    }

    private static void ClearPendingDescription(CloudPlaylistOutboxState state)
    {
        state.HasPendingDescription = false;
        state.BaselineDescription = null;
        state.TargetDescription = null;
    }

    private static void ReorderPendingAdds(List<string> pendingAdds, IReadOnlyList<string> newOrder)
    {
        if (pendingAdds.Count <= 1)
            return;

        var pending = new HashSet<string>(pendingAdds, StringComparer.Ordinal);
        var reordered = new List<string>(pendingAdds.Count);

        for (int i = 0; i < newOrder.Count; i++)
        {
            if (pending.Remove(newOrder[i]))
                reordered.Add(newOrder[i]);
        }

        for (int i = 0; i < pendingAdds.Count; i++)
        {
            if (pending.Remove(pendingAdds[i]))
                reordered.Add(pendingAdds[i]);
        }

        pendingAdds.Clear();
        pendingAdds.AddRange(reordered);
    }

    private static void CollapseIdentityReorder(CloudPlaylistOutboxState state)
    {
        if (state.HasPendingReorder &&
            state.BaselineTrackOrder != null &&
            state.TargetTrackOrder != null &&
            SequenceEqualOrdinal(state.BaselineTrackOrder, state.TargetTrackOrder))
        {
            state.HasPendingReorder = false;
            state.BaselineTrackOrder = null;
            state.TargetTrackOrder = null;
        }
    }

    private static long MinPositive(long a, long b)
    {
        if (a <= 0) return b;
        if (b <= 0) return a;
        return Math.Min(a, b);
    }

    private static int IndexOfLike(List<CloudLikeMutationEntry> likes, string trackId)
    {
        for (int i = 0; i < likes.Count; i++)
        {
            if (string.Equals(likes[i].TrackId, trackId, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    private static int IndexOfRemove(List<CloudTrackRemoveEntry> removes, string trackId)
    {
        for (int i = 0; i < removes.Count; i++)
        {
            if (string.Equals(removes[i].TrackId, trackId, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    private static bool ContainsOrdinal(List<string> list, string value) => IndexOfOrdinal(list, value) >= 0;

    private static int IndexOfOrdinal(List<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    private static void RemoveFirstOrdinal(List<string>? list, string value)
    {
        if (list == null)
            return;

        int idx = IndexOfOrdinal(list, value);
        if (idx >= 0)
            list.RemoveAt(idx);
    }

    private static void RemoveAllInSet(List<string>? list, HashSet<string> set)
    {
        if (list == null)
            return;

        int write = 0;
        for (int read = 0; read < list.Count; read++)
        {
            if (!set.Contains(list[read]))
                list[write++] = list[read];
        }

        if (write < list.Count)
            list.RemoveRange(write, list.Count - write);
    }

    #endregion
}
