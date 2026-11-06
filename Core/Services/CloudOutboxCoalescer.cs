using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using LMP.Core.Youtube.Exceptions;

namespace LMP.Core.Services;

/// <summary>
/// Cloud mutation delivery failure class that determines the retry policy in the Outbox queue.
/// </summary>
public enum CloudOutboxFailureKind : byte
{
    /// <summary>Operation canceled by token owner (application exit, user cancellation).</summary>
    Canceled = 0,

    /// <summary>Transient network failure, HttpClient timeout, cooldown, 5xx/429, SQLite busy.</summary>
    Transient = 1,

    /// <summary>YouTube session invalid (401, LoginRequired): queue parked until re-authentication.</summary>
    AuthRequired = 2,

    /// <summary>Cloud resource does not exist (404, playlist unavailable).</summary>
    NotFound = 3,

    /// <summary>No permission to modify cloud resource (403).</summary>
    Forbidden = 4,

    /// <summary>Non-retryable request error (400, malformed response).</summary>
    Permanent = 5
}

/// <summary>
/// Pure (I/O-free) state machine for Outbox cloud mutation coalescence.
/// All methods mutate the passed state and must be called under state owner lock.
/// Accounts for in-flight operations to prevent annihilation from destroying
/// user intent already sent to the cloud by the current flush.
/// </summary>
public static class CloudOutboxCoalescer
{
    /// <summary>YouTube placeholder replacing real <c>SetVideoId</c>, which must not be persisted in DB.</summary>
    public const string PlaceholderSetVideoId = "to_be_updated_by_client";

    private const int LinearScanThreshold = 8;

    /// <summary>
    /// Validates that <c>SetVideoId</c> is suitable for addressing a cloud playlist item.
    /// </summary>
    public static bool IsUsableSetVideoId([NotNullWhen(true)] string? setVideoId) =>
        !string.IsNullOrEmpty(setVideoId) &&
        !setVideoId.Equals(PlaceholderSetVideoId, StringComparison.OrdinalIgnoreCase);

    #region Likes

    /// <summary>
    /// Applies a "Like" intent to the likes bucket.
    /// While track like is being sent (<paramref name="isInFlight"/>), annihilation is forbidden:
    /// new target value is stored and will be sent by the next flush.
    /// </summary>
    /// <returns><see langword="true"/> if entry was annihilated (reverted to baseline state).</returns>
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
    /// Commits successful like delivery. If user changed target while in-flight,
    /// entry is retained with new baseline set to confirmed cloud state.
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
    /// Removes like entry without committing (non-retryable delivery failure).
    /// </summary>
    public static void DropLike(CloudPlaylistOutboxState state, string trackId)
    {
        int idx = IndexOfLike(state.PendingLikes, trackId);
        if (idx >= 0)
            state.PendingLikes.RemoveAt(idx);
    }

    /// <summary>
    /// Returns current track like target value from live state.
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
    /// Applies batch track addition. Pending removal of the same track is annihilated
    /// unless currently in-flight; otherwise removal is retained, and addition is queued as compensation.
    /// </summary>
    /// <returns>Restorable <c>(TrackId, SetVideoId)</c> pairs of annihilated removals or <see langword="null"/>.</returns>
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
    /// Applies batch track removal. Pending addition is annihilated unless currently
    /// in-flight; otherwise addition is retained, and removal is queued as compensation (without intent loss).
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
    /// Applies track reordering. For a playlist not yet created in cloud (not in-flight),
    /// reordering applies directly to pending addition order without separate move operations.
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
    /// Playlist creation not sent yet and not in-flight: Phase 0 will pull latest title,
    /// description, and track order from local state, so separate edits are unnecessary.
    /// </summary>
    public static bool IsUnsentCreate(CloudPlaylistOutboxState state, bool isTargetInFlight) =>
        state.PendingCreatePlaylist && string.IsNullOrEmpty(state.YoutubePlaylistId) && !isTargetInFlight;

    /// <summary>
    /// Strips separate title and description edits that will be included in the playlist creation request.
    /// </summary>
    public static void DropMetadataAbsorbedByCreate(CloudPlaylistOutboxState state)
    {
        ClearPendingName(state);
        ClearPendingDescription(state);
    }

    /// <summary>
    /// Commits title and description sent in creation request. An edit made during
    /// Phase 0 that differs from the sent value remains queued with a new baseline.
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
    /// Transitions state to terminal cloud playlist deletion, discarding other mutations.
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
    /// Commits successful addition: removes sent tracks from queue and appends returned
    /// <c>SetVideoId</c> values to compensating removals queued while in-flight.
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
    /// Normalizes conflicting "add + remove" pairs for the same track after flush completion:
    /// add in-flight + remove → result "track absent"; remove in-flight + add → result "track present".
    /// </summary>
    /// <returns><see langword="true"/> if state changed.</returns>
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
    /// Maps sent tracks to returned <c>SetVideoId</c> values (response already aligned with request),
    /// discarding empty values and the <see cref="PlaceholderSetVideoId"/> placeholder.
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
    /// Determines whether YouTube rejected the edit itself (HTTP 400 or <c>STATUS_FAILED</c>) rather than transport.
    /// Typical cause is stale <c>SetVideoId</c> values after playlist was modified on another device.
    /// </summary>
    public static bool IsRejectedEdit(Exception ex) =>
        ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.BadRequest } ||
        ex.GetType() == typeof(YoutubeExplodeException);

    /// <summary>
    /// Rebases removals against current cloud state: entry with a real <c>SetVideoId</c> no longer
    /// in cloud is considered completed; entry without a suitable <c>SetVideoId</c> resolves to
    /// first available occurrence of the same track. Reusing the same <c>SetVideoId</c> is prevented.
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
    /// Builds a pair of <c>SetVideoId</c> sequences for reordering over current cloud state:
    /// current = first track occurrences in cloud, target = target order tracks present in cloud.
    /// Tracks removed in cloud and cloud duplicates do not participate in reordering.
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
    /// Selects local tracks removed in cloud on another device: track has a real
    /// <c>SetVideoId</c> (was linked to cloud), but is absent in cloud composition.
    /// Tracks without suitable <c>SetVideoId</c> are considered pending upload and excluded.
    /// </summary>
    /// <returns>
    /// Track identifiers or <see langword="null"/> if safety trigger fired: cloud is empty
    /// or candidate count exceeds <c>max(3, localCount/4)</c> (likely partial cloud response).
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
    /// Merges live state accumulated prior to hydration completion with older state from SQLite.
    /// Live mutations are replayed on top of restored ones (baseline values taken from DB).
    /// </summary>
    /// <param name="live">Live (newer) state.</param>
    /// <param name="restored">State restored from DB (older); mutated and returned.</param>
    /// <param name="restoredSetVideoIds">Annihilated removal <c>SetVideoId</c> pairs to restore in DB.</param>
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
    /// Classifies cloud operation exception for retry policy selection.
    /// HttpClient timeout (<see cref="OperationCanceledException"/> with un-canceled token) is treated as transient.
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
    /// Calculates exponential retry delay: <c>base·2^(n−1)</c> capped at <paramref name="maxMs"/>.
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
    /// Element-wise compares two sequences of identifiers (ordinal, allocation-free).
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
