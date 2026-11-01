namespace LMP.Core.Youtube.Music;

/// <summary>
/// Выравнивает элементы <c>playlistEditResults</c> ответа YouTube по порядку запрошенных <c>videoId</c>.
/// Защищает от сдвига индексов, когда сервер пропускает дубликат или недоступный трек.
/// </summary>
public static class PlaylistEditResultAligner
{
    /// <summary>
    /// Возвращает список <c>SetVideoId</c> той же длины и порядка, что и <paramref name="requestedVideoIds"/>.
    /// Сопоставление идёт по <c>videoId</c>; позиционный fallback применяется только при точном совпадении длины ответа.
    /// </summary>
    /// <param name="requestedVideoIds">Сырые <c>videoId</c> в порядке запроса.</param>
    /// <param name="responseItems">Пары <c>(VideoId, SetVideoId)</c> в порядке ответа.</param>
    public static List<string?> Align(
        IReadOnlyList<string> requestedVideoIds,
        IReadOnlyList<(string? VideoId, string? SetVideoId)> responseItems)
    {
        var result = new List<string?>(requestedVideoIds.Count);
        for (int i = 0; i < requestedVideoIds.Count; i++)
            result.Add(null);

        if (responseItems.Count == 0 || requestedVideoIds.Count == 0)
            return result;

        var consumed = new bool[requestedVideoIds.Count];
        bool positionalAllowed = responseItems.Count == requestedVideoIds.Count;

        for (int r = 0; r < responseItems.Count; r++)
        {
            var (videoId, setVideoId) = responseItems[r];
            int target = -1;

            if (!string.IsNullOrEmpty(videoId))
            {
                for (int i = 0; i < requestedVideoIds.Count; i++)
                {
                    if (!consumed[i] && string.Equals(requestedVideoIds[i], videoId, StringComparison.Ordinal))
                    {
                        target = i;
                        break;
                    }
                }
            }
            else if (positionalAllowed && !consumed[r])
            {
                target = r;
            }

            if (target < 0)
                continue;

            consumed[target] = true;
            result[target] = setVideoId;
        }

        return result;
    }
}