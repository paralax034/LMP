using System.Runtime.CompilerServices;

namespace LMP.Core.Youtube.Utils;

/// <summary>
/// High-performance utilities for processing YouTube identifiers and media stream metadata.
/// Minimizes heap allocations through efficient work with Span.
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
    /// Rapidly extracts a clean YouTube ID without "yt_" or "yt_pl_" prefixes as an allocation-free Span.
    /// </summary>
    /// <param name="id">The source identifier.</param>
    /// <returns>The cleaned character segment.</returns>
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
    /// Extracts a clean YouTube ID as a string.
    /// If prefixes are absent, returns the original string without allocating new memory.
    /// </summary>
    /// <param name="id">The source identifier.</param>
    /// <returns>The cleaned identifier string.</returns>
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
    /// Centralally converts a playlist identifier into a canonical InnerTube API browseId.
    /// Correctly normalizes system sections (LL -> VLLL, LM -> VLLM, WL -> VLWL) and appends the VL prefix.
    /// </summary>
    /// <param name="playlistId">The playlist identifier.</param>
    /// <returns>The canonical identifier for a browse request.</returns>
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
    /// Safely maps the string representation of a container to the <see cref="AudioFormat"/> enumeration.
    /// </summary>
    /// <param name="container">The container name.</param>
    /// <returns>The corresponding audio format.</returns>
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