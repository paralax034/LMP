using System.Runtime.CompilerServices;

namespace LMP.Core.Models;

/// <summary>
/// Conflict resolution strategy for two-way track synchronization.
/// </summary>
public enum PlaylistSyncStrategy
{
    Merge = 0,
    ReplaceLocal = 1,
    ReplaceCloud = 2
}

/// <summary>
/// Set of parameters and flags selected for applying synchronization.
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
/// Utility for deterministic playlist cover matching without false positives on YouTube tokens.
/// </summary>
public static class ThumbnailComparer
{
    private const string YtEmptyStatePlaceholder = "playlist-empty-state";

    /// <summary>
    /// Normalizes a string cover URL by stripping dynamic query parameters.
    /// </summary>
    /// <param name="url">The source cover URL.</param>
    /// <returns>The normalized URL without query parameters, or <c>null</c>.</returns>
    public static string? Normalize(string? url)
    {
        var span = NormalizeToSpan(url);
        if (span.IsEmpty) return null;
        if (url is not null && span.Length == url.Length) return url;
        return span.ToString();
    }

    /// <summary>
    /// Zero-allocation normalization of a cover URL using a memory segment.
    /// </summary>
    /// <param name="url">The source cover URL.</param>
    /// <returns>A character segment without query parameters, or an empty segment.</returns>
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

            // Google CDN (googleusercontent.com / ggpht.com) encodes size via '=s1200' or '=w544-h544' suffix
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
    /// Checks whether the cover is hosted on internal YouTube / Google CDN servers.
    /// Such covers are managed by the server and are not re-uploaded via the Scotty protocol.
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
    /// Checks whether the cover is hosted on internal YouTube / Google CDN servers.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsYoutubeHosted(string? url) =>
        !string.IsNullOrWhiteSpace(url) && IsYoutubeHosted(url.AsSpan());

    /// <summary>
    /// Checks equivalence between local and cloud covers without heap allocations.
    /// </summary>
    /// <param name="localUrl">Local path or cover URL.</param>
    /// <param name="cloudUrl">YouTube cloud cover URL.</param>
    /// <param name="youtubeId">Playlist identifier for checking server collages.</param>
    /// <returns><c>true</c> if covers are equivalent; otherwise, <c>false</c>.</returns>
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

        // If both links belong to YouTube/Google CDN (e.g., i.ytimg.com and yt3.googleusercontent.com),
        // both are server representations of the same cloud cover and do not require a Scotty upload
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
/// Snapshot of differences between the local playlist state and YouTube cloud.
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
    /// Number of local tracks already linked to the cloud (having <c>SetVideoId</c>), but deleted from the cloud
    /// playlist on another device. Merging deletes them locally instead of re-uploading.
    /// </summary>
    public int CloudRemovedTrackCount { get; init; }

    public string? YoutubePlaylistId { get; init; }

    /// <summary>
    /// Full YouTube Music data snapshot used to build the diff.
    /// Prevents redundant network requests and eliminates memory leaks in singleton services.
    /// </summary>
    public Youtube.Music.FullPlaylistSyncData? CachedCloudData { get; init; }

    public bool NameDiffers => !string.Equals(LocalName, CloudName, StringComparison.Ordinal);
    public bool DescriptionDiffers => !string.Equals(LocalDescription ?? string.Empty, CloudDescription ?? string.Empty, StringComparison.Ordinal);
    public bool ThumbnailDiffers => !ThumbnailComparer.AreEquivalent(LocalThumbnailUrl, CloudThumbnailUrl, YoutubePlaylistId);
    public bool TracksDiffer => LocalOnlyTrackCount > 0 || CloudOnlyTrackCount > 0 || CloudRemovedTrackCount > 0;
    public bool HasAnyDifference => NameDiffers || DescriptionDiffers || ThumbnailDiffers || TracksDiffer;
}

/// <summary>
/// Result of executing a playlist synchronization operation.
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
/// Record of a track to be removed from the cloud playlist, preserving its binding <c>SetVideoId</c>.
/// </summary>
public sealed class CloudTrackRemoveEntry
{
    public required string TrackId { get; init; }
    public string? SetVideoId { get; set; }
}

/// <summary>
/// Record of a track's "Liked" status mutation, preserving the base state for pair annihilation.
/// </summary>
public sealed class CloudLikeMutationEntry
{
    public required string TrackId { get; init; }
    public bool BaselineLiked { get; init; }
    public bool TargetLiked { get; set; }
}

/// <summary>
/// Coalesced state of pending cloud mutations for a single playlist or system likes inbox.
/// Serialized via <see cref="AppJsonContext"/> into the <c>CloudMutationOutbox</c> table.
/// </summary>
public sealed class CloudPlaylistOutboxState
{
    /// <summary>Identifier of the local playlist or <c>__likes__</c> for the system likes queue.</summary>
    public required string TargetId { get; init; }

    /// <summary>Identifier of the local session owner (<c>OwnerId</c>).</summary>
    public required string OwnerId { get; init; }

    /// <summary>Display name of the playlist for the task card in the notification center.</summary>
    public string? PlaylistDisplayName { get; set; }

    /// <summary>Cloud identifier of the playlist on YouTube.</summary>
    public string? YoutubePlaylistId { get; set; }

    /// <summary>Flag indicating whether a new playlist needs to be created on YouTube in Phase 0 (offline-optimistic).</summary>
    public bool PendingCreatePlaylist { get; set; }

    /// <summary>Flag indicating whether the playlist needs to be deleted from YouTube (terminal operation).</summary>
    public bool PendingDeletePlaylist { get; set; }

    /// <summary>Tracks pending batch addition to the cloud playlist (Phase 2).</summary>
    public List<string> PendingAddTrackIds { get; set; } = [];

    /// <summary>Tracks pending batch removal from the cloud playlist (Phase 1).</summary>
    public List<CloudTrackRemoveEntry> PendingRemoveTracks { get; set; } = [];

    /// <summary>Flag indicating whether track order synchronization is pending (Phase 3).</summary>
    public bool HasPendingReorder { get; set; }

    /// <summary>Original track order prior to a series of local drag-and-drop actions (for annihilation and LIS compression).</summary>
    public List<string>? BaselineTrackOrder { get; set; }

    /// <summary>Target local track order after completing drag-and-drop actions.</summary>
    public List<string>? TargetTrackOrder { get; set; }

    /// <summary>Flag indicating whether a playlist rename is pending (Last-Write-Wins).</summary>
    public bool HasPendingName { get; set; }

    /// <summary>Base playlist name prior to starting a series of renames.</summary>
    public string? BaselineName { get; set; }

    /// <summary>Target playlist name to send to YouTube.</summary>
    public string? TargetName { get; set; }

    /// <summary>Flag indicating whether a playlist description update is pending (Last-Write-Wins).</summary>
    public bool HasPendingDescription { get; set; }

    /// <summary>Base playlist description prior to editing.</summary>
    public string? BaselineDescription { get; set; }

    /// <summary>Target playlist description to send to YouTube.</summary>
    public string? TargetDescription { get; set; }

    /// <summary>Indicates whether a custom cover upload via Scotty is pending (Last-Write-Wins).</summary>
    public bool HasPendingThumbnail { get; set; }

    /// <summary>Target path or URL of the cover for Scotty upload.</summary>
    public string? TargetThumbnailUrl { get; set; }

    /// <summary>Coalesced like mutations (used when <c>TargetId == "__likes__"</c>).</summary>
    public List<CloudLikeMutationEntry> PendingLikes { get; set; } = [];

    /// <summary>Number of failed network state flush attempts.</summary>
    public int RetryCount { get; set; }

    /// <summary>Timestamp of the initial enqueue in the current coalescing window in UTC ticks.</summary>
    public long FirstEnqueuedAtUtcTicks { get; set; }

    /// <summary>Timestamp of the last modification in UTC ticks.</summary>
    public long UpdatedAtUtcTicks { get; set; }

    /// <summary>
    /// Returns <see langword="true"/> if all accumulated mutations have been mutually annihilated or completed.
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
