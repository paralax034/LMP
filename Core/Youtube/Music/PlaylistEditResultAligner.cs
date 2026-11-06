namespace LMP.Core.Youtube.Music;

/// <summary>
/// Aligns YouTube response <c>playlistEditResults</c> items with the order of requested <c>videoId</c>s.
/// Prevents index shifting when the server skips a duplicate or unavailable track.
/// </summary>
public static class PlaylistEditResultAligner
{
    /// <summary>
    /// Returns a list of <c>SetVideoId</c>s with the same length and order as <paramref name="requestedVideoIds"/>.
    /// Matching is performed by <c>videoId</c>; positional fallback is applied only on exact response length match.
    /// </summary>
    /// <param name="requestedVideoIds">Raw <c>videoId</c>s in the requested order.</param>
    /// <param name="responseItems"><c>(VideoId, SetVideoId)</c> pairs in the response order.</param>
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