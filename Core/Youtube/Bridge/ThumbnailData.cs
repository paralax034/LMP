using System.Text.Json;

namespace LMP.Core.Youtube.Bridge;

/// <summary>
/// Represents lightweight thumbnail metadata (URL and resolution).
/// Implemented as a readonly record struct to avoid allocations in the managed heap.
/// </summary>
/// <param name="Url">Direct URL of the thumbnail image.</param>
/// <param name="Width">Image width in pixels.</param>
/// <param name="Height">Image height in pixels.</param>
internal readonly record struct ThumbnailData(string? Url, int? Width, int? Height)
{
    /// <summary>
    /// Extracts thumbnail data from an InnerTube API JSON element.
    /// </summary>
    /// <param name="content">JSON element of the thumbnail object.</param>
    public ThumbnailData(JsonElement content)
        : this(
            content.GetPropertyOrNull("url"u8)?.GetStringOrNull(),
            content.GetPropertyOrNull("width"u8)?.GetInt32OrNull(),
            content.GetPropertyOrNull("height"u8)?.GetInt32OrNull())
    {
    }
}