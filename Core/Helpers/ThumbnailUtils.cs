using System.Runtime.CompilerServices;
using System.Text.Json;

namespace LMP.Core.Helpers;

/// <summary>
/// Single point of management and parsing for preview images (thumbnails) without LINQ allocations.
/// </summary>
public static class ThumbnailUtils
{
    /// <summary>
    /// Finds the cover image with the maximum resolution from a list of Thumbnail domain models.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string GetBestUrl(IReadOnlyList<Thumbnail> thumbnails, string? fallbackVideoId = null)
    {
        if (thumbnails.Count == 0)
        {
            return fallbackVideoId != null ? $"https://i.ytimg.com/vi/{fallbackVideoId}/mqdefault.jpg" : "";
        }

        Thumbnail? best = null;
        int maxArea = -1;

        for (int i = 0; i < thumbnails.Count; i++)
        {
            var t = thumbnails[i];
            int area = t.Resolution.Width * t.Resolution.Height;
            if (area > maxArea)
            {
                maxArea = area;
                best = t;
            }
        }

        return best?.Url ?? (fallbackVideoId != null ? $"https://i.ytimg.com/vi/{fallbackVideoId}/mqdefault.jpg" : "");
    }

    /// <summary>
    /// Finds the cover image with the maximum resolution from internal ThumbnailData structures.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static string GetBestUrl(IEnumerable<Youtube.Bridge.ThumbnailData> thumbnails, string? fallbackVideoId = null)
    {
        string? bestUrl = null;
        int bestArea = -1;

        foreach (var t in thumbnails)
        {
            if (t.Url == null) continue;
            int area = (t.Width ?? 0) * (t.Height ?? 0);
            if (area > bestArea)
            {
                bestArea = area;
                bestUrl = t.Url;
            }
        }

        return bestUrl ?? (fallbackVideoId != null ? $"https://i.ytimg.com/vi/{fallbackVideoId}/mqdefault.jpg" : "");
    }

    /// <summary>
    /// High-performance extracts a Thumbnail array directly from JSON without creating intermediate ThumbnailData objects.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Thumbnail[] ExtractThumbnails(JsonElement? thumbsElement)
    {
        if (thumbsElement == null || thumbsElement.Value.ValueKind != JsonValueKind.Array)
            return [];

        var array = thumbsElement.Value;
        int len = array.GetArrayLength();
        if (len == 0) return [];

        var result = new Thumbnail[len];
        for (int i = 0; i < len; i++)
        {
            var item = array[i];
            var url = item.GetPropertyOrNull("url")?.GetStringOrNull() ?? "";
            var width = item.GetPropertyOrNull("width")?.GetInt32OrNull() ?? 0;
            var height = item.GetPropertyOrNull("height")?.GetInt32OrNull() ?? 0;
            result[i] = new Thumbnail(url, new Resolution(width, height));
        }
        return result;
    }
}