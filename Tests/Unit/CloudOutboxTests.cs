using System.Net;
using LMP.Core.Data;
using LMP.Core.Data.Repositories;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Music;
using LMP.Tests.Framework;
using Microsoft.Data.Sqlite;

namespace LMP.Tests.Unit;

/// <summary>
/// Scenario tests for the cloud mutation outbox: in-flight coalescing, hydration merging,
/// failure classification, cloud rebasing, persistence pump, targeted SQLite updates, and task cards.
/// </summary>
public static class CloudOutboxTests
{
    private const string Owner = "test_owner";

    [TestMethod(TestCategory.Unit, "Outbox: Likes Coalescing, Annihilation & In-Flight Toggle-Back", Group = TestGroups.Playlists, Order = 20)]
    public static Task TestLikesCoalescingAndInFlightLifecycleAsync()
    {
        // 1. Toggle-back during in-flight send preserves compensating mutation
        var inFlight = NewState("__likes__");
        CloudOutboxCoalescer.ApplyLike(inFlight, "yt_a", targetLiked: true, baselineLiked: false, isInFlight: false);

        TestAssert.False(CloudOutboxCoalescer.ApplyLike(inFlight, "yt_a", targetLiked: false, baselineLiked: true, isInFlight: true));
        TestAssert.True(CloudOutboxCoalescer.TryGetLikeTarget(inFlight, "yt_a", out bool target) && !target);

        CloudOutboxCoalescer.CommitLike(inFlight, "yt_a", syncedLiked: true);
        TestAssert.Equal(1, inFlight.PendingLikes.Count);
        TestAssert.True(inFlight.PendingLikes[0].BaselineLiked);
        TestAssert.False(inFlight.PendingLikes[0].TargetLiked);

        CloudOutboxCoalescer.CommitLike(inFlight, "yt_a", syncedLiked: false);
        TestAssert.True(inFlight.IsEmpty);

        // 2. Toggle-back outside flight annihilates; no-op intent does not enqueue
        var idle = NewState("__likes__");
        CloudOutboxCoalescer.ApplyLike(idle, "yt_a", targetLiked: true, baselineLiked: false, isInFlight: false);
        TestAssert.True(CloudOutboxCoalescer.ApplyLike(idle, "yt_a", targetLiked: false, baselineLiked: true, isInFlight: false));
        TestAssert.True(idle.IsEmpty);

        CloudOutboxCoalescer.ApplyLike(idle, "yt_b", targetLiked: true, baselineLiked: true, isInFlight: false);
        TestAssert.True(idle.IsEmpty);
        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Outbox: In-Flight Track Compensations, Create Metadata & Reorder", Group = TestGroups.Playlists, Order = 21)]
    public static Task TestInFlightTrackMutationsAndCreateMetadataAsync()
    {
        // 1. Remove during in-flight Add: success -> keeps compensating remove with resolved SetVideoId
        var addSuccess = NewState("pl_a");
        addSuccess.YoutubePlaylistId = "PL_A";
        CloudOutboxCoalescer.ApplyAddTracks(addSuccess, ["yt_a", "yt_b"], inFlightRemoves: null);
        var sentAdds = new HashSet<string>(addSuccess.PendingAddTrackIds, StringComparer.Ordinal);

        CloudOutboxCoalescer.ApplyRemoveTracks(addSuccess, [("yt_a", null)], sentAdds);
        TestAssert.Equal(2, addSuccess.PendingAddTrackIds.Count);
        TestAssert.Equal(1, addSuccess.PendingRemoveTracks.Count);

        CloudOutboxCoalescer.CommitAdds(addSuccess, ["yt_a", "yt_b"], [("yt_a", "SV_A"), ("yt_b", "SV_B")]);
        TestAssert.Equal(0, addSuccess.PendingAddTrackIds.Count);
        TestAssert.Equal("SV_A", addSuccess.PendingRemoveTracks[0].SetVideoId);
        TestAssert.False(CloudOutboxCoalescer.ResolveInFlightCompensations(addSuccess, sentAdds, new HashSet<string>(StringComparer.Ordinal)));

        // 2. Remove during in-flight Add: failure -> clears failed add retry, keeps remove intent
        var addFail = NewState("pl_a");
        addFail.YoutubePlaylistId = "PL_A";
        CloudOutboxCoalescer.ApplyAddTracks(addFail, ["yt_a"], inFlightRemoves: null);
        var sentFailAdds = new HashSet<string>(addFail.PendingAddTrackIds, StringComparer.Ordinal);
        CloudOutboxCoalescer.ApplyRemoveTracks(addFail, [("yt_a", null)], sentFailAdds);
        TestAssert.True(CloudOutboxCoalescer.ResolveInFlightCompensations(addFail, sentFailAdds, new HashSet<string>(StringComparer.Ordinal)));
        TestAssert.Equal(0, addFail.PendingAddTrackIds.Count);
        TestAssert.Equal(1, addFail.PendingRemoveTracks.Count);

        // 3. Re-add during in-flight Remove vs pending Remove outside flight
        var removeInFlight = NewState("pl_a");
        removeInFlight.YoutubePlaylistId = "PL_A";
        CloudOutboxCoalescer.ApplyRemoveTracks(removeInFlight, [("yt_a", "SV_A")], inFlightAdds: null);
        var sentRemoves = new HashSet<string>(StringComparer.Ordinal) { "yt_a" };
        TestAssert.Null(CloudOutboxCoalescer.ApplyAddTracks(removeInFlight, ["yt_a"], sentRemoves));
        TestAssert.Equal(1, removeInFlight.PendingAddTrackIds.Count);
        TestAssert.Equal(1, removeInFlight.PendingRemoveTracks.Count);
        TestAssert.True(CloudOutboxCoalescer.ResolveInFlightCompensations(removeInFlight, new HashSet<string>(StringComparer.Ordinal), sentRemoves));
        TestAssert.True(removeInFlight.IsEmpty);

        var removeIdle = NewState("pl_a");
        removeIdle.YoutubePlaylistId = "PL_A";
        CloudOutboxCoalescer.ApplyRemoveTracks(removeIdle, [("yt_a", "SV_A")], inFlightAdds: null);
        var restored = CloudOutboxCoalescer.ApplyAddTracks(removeIdle, ["yt_a"], inFlightRemoves: null);
        TestAssert.NotNull(restored);
        TestAssert.Equal(1, restored.Count);
        TestAssert.Equal("SV_A", restored[0].SetVideoId);
        TestAssert.True(removeIdle.IsEmpty);

        // 4. Pending Create absorbs edits, reorders, and metadata before Phase 0 commit
        var pendingCreate = NewState("pl_new");
        pendingCreate.PendingCreatePlaylist = true;
        pendingCreate.HasPendingName = true;
        pendingCreate.TargetName = "Draft";
        pendingCreate.HasPendingDescription = true;
        pendingCreate.TargetDescription = "draft desc";

        CloudOutboxCoalescer.ApplyAddTracks(pendingCreate, ["yt_a", "yt_b", "yt_c"], inFlightRemoves: null);
        CloudOutboxCoalescer.ApplyRemoveTracks(pendingCreate, [("yt_b", null)], inFlightAdds: null);
        CloudOutboxCoalescer.ApplyReorder(pendingCreate, ["yt_a", "yt_c"], ["yt_c", "yt_a"], isTargetInFlight: false);

        TestAssert.Equal(2, pendingCreate.PendingAddTrackIds.Count);
        TestAssert.Equal("yt_c", pendingCreate.PendingAddTrackIds[0]);
        TestAssert.Equal("yt_a", pendingCreate.PendingAddTrackIds[1]);
        TestAssert.Equal(0, pendingCreate.PendingRemoveTracks.Count);
        TestAssert.False(pendingCreate.HasPendingReorder);

        TestAssert.True(CloudOutboxCoalescer.IsUnsentCreate(pendingCreate, isTargetInFlight: false));
        TestAssert.False(CloudOutboxCoalescer.IsUnsentCreate(pendingCreate, isTargetInFlight: true));
        CloudOutboxCoalescer.DropMetadataAbsorbedByCreate(pendingCreate);
        TestAssert.False(pendingCreate.HasPendingName);
        TestAssert.False(pendingCreate.HasPendingDescription);
        TestAssert.False(pendingCreate.IsEmpty);

        pendingCreate.HasPendingName = true;
        pendingCreate.TargetName = "Sent";
        pendingCreate.HasPendingDescription = true;
        pendingCreate.TargetDescription = "Newer";
        CloudOutboxCoalescer.CommitMetadataSentWithCreate(pendingCreate, "Sent", "Old desc");
        TestAssert.False(pendingCreate.HasPendingName);
        TestAssert.Null(pendingCreate.TargetName);
        TestAssert.True(pendingCreate.HasPendingDescription);
        TestAssert.Equal("Old desc", pendingCreate.BaselineDescription);
        TestAssert.Equal("Newer", pendingCreate.TargetDescription);

        pendingCreate.YoutubePlaylistId = "PL_created";
        TestAssert.False(CloudOutboxCoalescer.IsUnsentCreate(pendingCreate, isTargetInFlight: false));
        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Outbox: Hydration Merge (Likes & Playlist)", Group = TestGroups.Playlists, Order = 22)]
    public static Task TestHydrationMergeAsync()
    {
        // 1. Likes hydration merge
        var restoredLikes = NewState("__likes__");
        CloudOutboxCoalescer.ApplyLike(restoredLikes, "yt_a", targetLiked: true, baselineLiked: false, isInFlight: false);

        var liveLikes = NewState("__likes__");
        CloudOutboxCoalescer.ApplyLike(liveLikes, "yt_a", targetLiked: false, baselineLiked: true, isInFlight: false);
        CloudOutboxCoalescer.ApplyLike(liveLikes, "yt_b", targetLiked: true, baselineLiked: false, isInFlight: false);

        var mergedLikes = CloudOutboxCoalescer.MergeRestored(liveLikes, restoredLikes, out _);
        TestAssert.Equal(1, mergedLikes.PendingLikes.Count);
        TestAssert.Equal("yt_b", mergedLikes.PendingLikes[0].TrackId);
        TestAssert.True(mergedLikes.PendingLikes[0].TargetLiked);

        // 2. Playlist hydration merge
        var restoredPl = NewState("pl_a");
        restoredPl.YoutubePlaylistId = "PL_A";
        restoredPl.PendingAddTrackIds.Add("yt_a");
        restoredPl.PendingRemoveTracks.Add(new CloudTrackRemoveEntry { TrackId = "yt_b", SetVideoId = "SV_B" });

        var livePl = NewState("pl_a");
        livePl.PendingAddTrackIds.Add("yt_b");
        livePl.PendingAddTrackIds.Add("yt_c");
        livePl.PendingRemoveTracks.Add(new CloudTrackRemoveEntry { TrackId = "yt_a" });

        var mergedPl = CloudOutboxCoalescer.MergeRestored(livePl, restoredPl, out var restoredSetVideoIds);
        TestAssert.Equal("PL_A", mergedPl.YoutubePlaylistId);
        TestAssert.Equal(1, mergedPl.PendingAddTrackIds.Count);
        TestAssert.Equal("yt_c", mergedPl.PendingAddTrackIds[0]);
        TestAssert.Equal(0, mergedPl.PendingRemoveTracks.Count);
        TestAssert.NotNull(restoredSetVideoIds);
        TestAssert.Equal("SV_B", restoredSetVideoIds[0].SetVideoId);
        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Outbox: Failure Classification, Backoff & SetVideoId Alignment", Group = TestGroups.Playlists, Order = 23)]
    public static Task TestFailurePolicyAndEditAlignmentAsync()
    {
        var ct = CancellationToken.None;

        TestAssert.Equal(CloudOutboxFailureKind.AuthRequired, CloudOutboxCoalescer.ClassifyFailure(Http(HttpStatusCode.Unauthorized), ct));
        TestAssert.Equal(CloudOutboxFailureKind.NotFound, CloudOutboxCoalescer.ClassifyFailure(Http(HttpStatusCode.NotFound), ct));
        TestAssert.Equal(CloudOutboxFailureKind.NotFound, CloudOutboxCoalescer.ClassifyFailure(Http(HttpStatusCode.Gone), ct));
        TestAssert.Equal(CloudOutboxFailureKind.Forbidden, CloudOutboxCoalescer.ClassifyFailure(Http(HttpStatusCode.Forbidden), ct));
        TestAssert.Equal(CloudOutboxFailureKind.Permanent, CloudOutboxCoalescer.ClassifyFailure(Http(HttpStatusCode.BadRequest), ct));
        TestAssert.Equal(CloudOutboxFailureKind.Transient, CloudOutboxCoalescer.ClassifyFailure(Http(HttpStatusCode.ServiceUnavailable), ct));
        TestAssert.Equal(CloudOutboxFailureKind.Transient, CloudOutboxCoalescer.ClassifyFailure(new HttpRequestException("socket reset"), ct));
        TestAssert.Equal(CloudOutboxFailureKind.Transient, CloudOutboxCoalescer.ClassifyFailure(new TimeoutException(), ct));
        TestAssert.Equal(CloudOutboxFailureKind.Transient, CloudOutboxCoalescer.ClassifyFailure(new TaskCanceledException(), ct));
        TestAssert.Equal(CloudOutboxFailureKind.Permanent, CloudOutboxCoalescer.ClassifyFailure(new FormatException("bad payload"), ct));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        TestAssert.Equal(CloudOutboxFailureKind.Canceled, CloudOutboxCoalescer.ClassifyFailure(new OperationCanceledException(cts.Token), cts.Token));

        // Monotonic retry backoff
        TestAssert.Equal(0, CloudOutboxCoalescer.ComputeRetryBackoffMs(0, 3_000, 60_000));
        TestAssert.Equal(3_000, CloudOutboxCoalescer.ComputeRetryBackoffMs(1, 3_000, 60_000));
        TestAssert.Equal(6_000, CloudOutboxCoalescer.ComputeRetryBackoffMs(2, 3_000, 60_000));
        TestAssert.Equal(60_000, CloudOutboxCoalescer.ComputeRetryBackoffMs(100, 3_000, 60_000));

        int previous = 0;
        for (int n = 1; n <= 64; n++)
        {
            int current = CloudOutboxCoalescer.ComputeRetryBackoffMs(n, 3_000, 60_000);
            TestAssert.True(current >= previous && current <= 60_000, $"Backoff #{n} = {current} breaks monotonicity");
            previous = current;
        }

        // Placeholder SetVideoId filtering and response length guard
        var mappings = CloudOutboxCoalescer.MapAddedSetVideoIds(
            ["yt_a", "yt_b", "yt_c"],
            ["SV_A", CloudOutboxCoalescer.PlaceholderSetVideoId, null]);
        TestAssert.Equal(1, mappings.Count);
        TestAssert.Equal("yt_a", mappings[0].TrackId);
        TestAssert.Equal("SV_A", mappings[0].SetVideoId);
        TestAssert.Equal(0, CloudOutboxCoalescer.MapAddedSetVideoIds(["yt_a", "yt_b"], ["SV_A"]).Count);

        // Response item alignment across omitted, positional, ambiguous, and duplicate tracks
        var aligned = PlaylistEditResultAligner.Align(["v1", "v2", "v3"], [("v1", "SV_A"), ("v3", "SV_C")]);
        TestAssert.Equal(3, aligned.Count);
        TestAssert.Equal("SV_A", aligned[0]);
        TestAssert.Null(aligned[1]);
        TestAssert.Equal("SV_C", aligned[2]);

        var positional = PlaylistEditResultAligner.Align(["v1", "v2"], [(null, "SV_1"), (null, "SV_2")]);
        TestAssert.Equal("SV_1", positional[0]);
        TestAssert.Equal("SV_2", positional[1]);

        var ambiguous = PlaylistEditResultAligner.Align(["v1", "v2", "v3"], [(null, "SV_1"), (null, "SV_2")]);
        TestAssert.Null(ambiguous[0]);
        TestAssert.Null(ambiguous[2]);

        var duplicates = PlaylistEditResultAligner.Align(["v1", "v1"], [("v1", "SV_X"), ("v1", "SV_Y")]);
        TestAssert.Equal("SV_X", duplicates[0]);
        TestAssert.Equal("SV_Y", duplicates[1]);
        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Outbox: Persistence Pump Coalescing, Hydration Pause & Retry", Group = TestGroups.Playlists, Order = 24, TimeoutSeconds = 15)]
    public static async Task TestPersistencePumpLifecycleAsync()
    {
        // 1. Coalescing burst of 100 dirty signals
        var writes = new List<IReadOnlyList<CloudOutboxRowChange>>();
        var gate = new Lock();
        int version = 0;

        var coalescePump = new CloudOutboxPersistencePump(
            (_, targetId) =>
            {
                var s = NewState(targetId);
                s.PendingAddTrackIds.Add("yt_" + Volatile.Read(ref version));
                return s;
            },
            (changes, _) =>
            {
                lock (gate)
                    writes.Add(changes);
                return Task.CompletedTask;
            },
            coalesceWindowMs: 200);

        for (int i = 1; i <= 100; i++)
        {
            Volatile.Write(ref version, i);
            coalescePump.MarkDirty(Owner, "pl_a");
        }

        TestAssert.True(await coalescePump.DrainAsync(5_000).ConfigureAwait(false), "Pump did not drain in time");
        TestAssert.True(coalescePump.BatchesWritten <= 2, $"Expected <= 2 batches, got {coalescePump.BatchesWritten}");

        IReadOnlyList<CloudOutboxRowChange> last;
        lock (gate)
            last = writes[^1];
        TestAssert.Equal(1, last.Count);
        TestAssert.Equal("yt_100", last[0].State!.PendingAddTrackIds[0]);

        // 2. StartPaused holds writes until Resume()
        int written = 0;
        var pausedPump = new CloudOutboxPersistencePump(
            (_, _) => null,
            (changes, _) =>
            {
                Interlocked.Add(ref written, changes.Count);
                return Task.CompletedTask;
            },
            coalesceWindowMs: 0,
            startPaused: true);

        pausedPump.MarkDirty(Owner, "pl_a");
        await Task.Delay(100).ConfigureAwait(false);
        TestAssert.Equal(0, Volatile.Read(ref written));
        TestAssert.False(await pausedPump.DrainAsync(100).ConfigureAwait(false));

        pausedPump.Resume();
        TestAssert.True(await pausedPump.DrainAsync(5_000).ConfigureAwait(false));
        TestAssert.Equal(1, Volatile.Read(ref written));
        TestAssert.Equal(0, pausedPump.PendingCount);

        // 3. Transient batch write failure retries and succeeds
        int calls = 0;
        var retryPump = new CloudOutboxPersistencePump(
            (_, targetId) => NewState(targetId),
            (_, _) => Interlocked.Increment(ref calls) == 1
                ? Task.FromException(new IOException("database is locked"))
                : Task.CompletedTask,
            coalesceWindowMs: 0);

        retryPump.MarkDirty(Owner, "pl_a");
        TestAssert.True(await retryPump.DrainAsync(5_000).ConfigureAwait(false));
        TestAssert.Equal(2, Volatile.Read(ref calls));
        TestAssert.Equal(1, retryPump.BatchesWritten);
    }

    [TestMethod(TestCategory.Unit, "Outbox: SQLite Store Roundtrip & Targeted CloudLinkState", Group = TestGroups.Playlists, Order = 25, TimeoutSeconds = 20)]
    public static async Task TestSqliteOutboxAndCloudLinkStateAsync()
    {
        var path = CreateTempDbPath();
        ISqliteConnectionFactory factory = CreateFactory(path);
        try
        {
            await CreateSchemaAsync(factory).ConfigureAwait(false);

            // 1. Outbox batch write/delete & corrupted payload tolerance
            var store = new CloudOutboxStore(factory);
            var a = NewState("pl_a");
            a.PendingAddTrackIds.Add("yt_1");
            a.UpdatedAtUtcTicks = 100;

            var b = NewState("pl_b");
            b.PendingRemoveTracks.Add(new CloudTrackRemoveEntry { TrackId = "yt_2", SetVideoId = "SV_2" });
            b.UpdatedAtUtcTicks = 200;
            b.RetryCount = 2;

            await store.WriteBatchAsync(
                [new CloudOutboxRowChange(Owner, "pl_a", a), new CloudOutboxRowChange(Owner, "pl_b", b)]).ConfigureAwait(false);
            await store.WriteBatchAsync([new CloudOutboxRowChange(Owner, "pl_a", null)]).ConfigureAwait(false);
            await InsertBrokenOutboxRowAsync(factory).ConfigureAwait(false);

            var rows = await store.LoadAllAsync().ConfigureAwait(false);
            TestAssert.Equal(1, rows.Count);
            TestAssert.Equal("pl_b", rows[0].TargetId);
            TestAssert.Equal("SV_2", rows[0].PendingRemoveTracks[0].SetVideoId);
            TestAssert.Equal(200L, rows[0].UpdatedAtUtcTicks);
            TestAssert.Equal(2, rows[0].RetryCount);

            // 2. Targeted UpdateCloudLinkStateAsync preserves concurrent renames and guards LocalOnly/missing playlists
            var repo = new PlaylistRepository(factory);
            TestAssert.False(await repo.UpdateCloudLinkStateAsync("pl_missing", "PL_X", null, false).ConfigureAwait(false));

            await repo.UpsertAsync(new Playlist
            {
                Id = "pl_sync",
                Name = "Original",
                OwnerId = Owner,
                SyncMode = PlaylistSyncMode.TwoWaySync,
                Ownership = PlaylistOwnership.Unknown
            }).ConfigureAwait(false);

            await repo.RenameAsync("pl_sync", "Renamed").ConfigureAwait(false);
            TestAssert.True(await repo.UpdateCloudLinkStateAsync("pl_sync", "PL_NEW", null, false).ConfigureAwait(false));

            var linked = await repo.GetByIdAsync("pl_sync", Owner).ConfigureAwait(false);
            TestAssert.NotNull(linked);
            TestAssert.Equal("PL_NEW", linked.YoutubeId);
            TestAssert.Equal("Renamed", linked.Name);
            TestAssert.Equal(PlaylistOwnership.Mine, linked.Ownership);

            TestAssert.False(await repo.UpdateCloudLinkStateAsync("pl_sync", "PL_OTHER", null, false).ConfigureAwait(false));

            await repo.UpsertAsync(new Playlist
            {
                Id = "pl_local",
                Name = "Local",
                OwnerId = Owner,
                SyncMode = PlaylistSyncMode.LocalOnly
            }).ConfigureAwait(false);
            TestAssert.False(await repo.UpdateCloudLinkStateAsync("pl_local", "PL_Y", null, false).ConfigureAwait(false));
        }
        finally
        {
            CleanupTempDb(factory, path);
        }
    }

    [TestMethod(TestCategory.Unit, "Outbox: Cloud Rebase (Rejected Edits, Removals, Reorder & Deleted Guard)", Group = TestGroups.Playlists, Order = 26)]
    public static Task TestCloudRebaseAndDeletedSelectionAsync()
    {
        // 1. Rejected edit detection
        TestAssert.True(CloudOutboxCoalescer.IsRejectedEdit(Http(HttpStatusCode.BadRequest)));
        TestAssert.True(CloudOutboxCoalescer.IsRejectedEdit(
            new YoutubeExplodeException("Playlist operation failed with status: STATUS_FAILED")));
        TestAssert.False(CloudOutboxCoalescer.IsRejectedEdit(Http(HttpStatusCode.ServiceUnavailable)));
        TestAssert.False(CloudOutboxCoalescer.IsRejectedEdit(Http(HttpStatusCode.Unauthorized)));
        TestAssert.False(CloudOutboxCoalescer.IsRejectedEdit(new HttpRequestException("socket reset")));
        TestAssert.False(CloudOutboxCoalescer.IsRejectedEdit(new TimeoutException()));

        // 2. Removal rebase against current cloud state
        List<(string TrackId, string SetVideoId)> removalCloud =
        [
            ("yt_a", "SV_A"), ("yt_c", "SV_C"), ("yt_c", "SV_C2"), ("yt_d", "SV_D")
        ];
        List<CloudTrackRemoveEntry> removals =
        [
            new() { TrackId = "yt_a", SetVideoId = "SV_A" },
            new() { TrackId = "yt_b", SetVideoId = "SV_B" },
            new() { TrackId = "yt_c", SetVideoId = null },
            new() { TrackId = "yt_c", SetVideoId = CloudOutboxCoalescer.PlaceholderSetVideoId },
            new() { TrackId = "yt_a", SetVideoId = "SV_A" }
        ];
        var rebased = CloudOutboxCoalescer.SelectRemovalsStillInCloud(removals, removalCloud);
        TestAssert.Equal(3, rebased.Count);
        TestAssert.Equal("SV_A", rebased[0].SetVideoId);
        TestAssert.Equal("SV_C", rebased[1].SetVideoId);
        TestAssert.Equal("SV_C2", rebased[2].SetVideoId);
        TestAssert.Equal(0, CloudOutboxCoalescer.SelectRemovalsStillInCloud(removals, []).Count);

        // 3. Reorder rebase skips tracks deleted in cloud and converges cleanly
        List<(string TrackId, string SetVideoId)> reorderCloud =
        [
            ("yt_a", "SV_A"), ("yt_c", "SV_C"), ("yt_d", "SV_D"), ("yt_e", "SV_E"), ("yt_a", "SV_A2")
        ];
        var (current, desired) = CloudOutboxCoalescer.BuildRebasedReorder(reorderCloud, ["yt_d", "yt_a", "yt_b", "yt_c"]);
        TestAssert.Equal(4, current.Count);
        TestAssert.Equal(3, desired.Count);

        var moves = PlaylistSyncService.BuildMinimalReorderMoves(current, desired);
        TestAssert.True(moves.Count > 0);

        var simulated = new List<string>(reorderCloud.Count);
        for (int i = 0; i < reorderCloud.Count; i++)
            simulated.Add(reorderCloud[i].SetVideoId);

        for (int i = 0; i < moves.Count; i++)
        {
            var (setVideoId, predecessor, successor) = moves[i];
            TestAssert.True(simulated.Contains(setVideoId), $"Stale move target {setVideoId}");
            simulated.Remove(setVideoId);

            int anchor = predecessor != null ? simulated.IndexOf(predecessor) : simulated.IndexOf(successor!);
            TestAssert.True(anchor >= 0, $"Stale move anchor for {setVideoId}");
            simulated.Insert(predecessor != null ? anchor + 1 : anchor, setVideoId);
        }

        var resultingOrder = simulated.FindAll(desired.Contains);
        TestAssert.Equal(desired.Count, resultingOrder.Count);
        for (int i = 0; i < desired.Count; i++)
            TestAssert.Equal(desired[i], resultingOrder[i]);

        // 4. Cloud-deleted selection guards against partial/empty cloud responses and placeholders
        List<(string TrackId, string? SetVideoId)> local =
        [
            ("yt_a", "SV_A"), ("yt_b", "SV_B"), ("yt_new", null),
            ("yt_ph", CloudOutboxCoalescer.PlaceholderSetVideoId), ("yt_c", "SV_C")
        ];
        var cloud = new HashSet<string>(StringComparer.Ordinal) { "yt_a", "yt_c", "yt_x" };
        var deleted = CloudOutboxCoalescer.SelectCloudDeletedTrackIds(local, cloud);
        TestAssert.NotNull(deleted);
        TestAssert.Equal(1, deleted.Count);
        TestAssert.True(deleted.Contains("yt_b"));

        var emptyCloud = new HashSet<string>(StringComparer.Ordinal);
        TestAssert.Null(CloudOutboxCoalescer.SelectCloudDeletedTrackIds(local, emptyCloud));

        List<(string TrackId, string? SetVideoId)> many = [];
        for (int i = 0; i < 8; i++)
            many.Add(($"yt_{i}", $"SV_{i}"));
        TestAssert.Null(CloudOutboxCoalescer.SelectCloudDeletedTrackIds(many, new HashSet<string>(StringComparer.Ordinal) { "yt_0", "yt_1" }));

        var nothingLinked = CloudOutboxCoalescer.SelectCloudDeletedTrackIds([("yt_new", null)], emptyCloud);
        TestAssert.NotNull(nothingLinked);
        TestAssert.Equal(0, nothingLinked.Count);
        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "LIS Minimal Reorder Compression", Group = TestGroups.Playlists, Order = 6)]
    public static Task TestLisMinimalReorderCompressionAsync()
    {
        // 1. Identical lists -> 0 moves
        var baseList = new List<string> { "sv_0", "sv_1", "sv_2", "sv_3", "sv_4", "sv_5" };
        var sameMoves = PlaylistSyncService.BuildMinimalReorderMoves(baseList, baseList);
        TestAssert.Equal(0, sameMoves.Count, "Identical order must produce 0 moves");

        // 2. Moving the first element to the end of a 6-element list requires exactly 1 LIS move
        var movedFirstToLast = new List<string> { "sv_1", "sv_2", "sv_3", "sv_4", "sv_5", "sv_0" };
        var singleMove = PlaylistSyncService.BuildMinimalReorderMoves(baseList, movedFirstToLast);
        TestAssert.Equal(1, singleMove.Count, "Moving 1st track to end must require exactly 1 LIS move");
        TestAssert.Equal("sv_0", singleMove[0].SetVideoId);
        TestAssert.Equal("sv_5", singleMove[0].Predecessor);

        // 3. Exact convergence of a complex permutation to the target order
        var complexTarget = new List<string> { "sv_4", "sv_1", "sv_0", "sv_5", "sv_2", "sv_3" };
        var complexMoves = PlaylistSyncService.BuildMinimalReorderMoves(baseList, complexTarget);
        TestAssert.True(complexMoves.Count <= 3, $"Expected <= 3 moves via LIS, got {complexMoves.Count}");

        var simulated = new List<string>(baseList);
        for (int i = 0; i < complexMoves.Count; i++)
        {
            var (sid, pred, succ) = complexMoves[i];
            simulated.Remove(sid);
            if (!string.IsNullOrEmpty(pred))
            {
                int pIdx = simulated.IndexOf(pred);
                simulated.Insert(pIdx + 1, sid);
            }
            else if (!string.IsNullOrEmpty(succ))
            {
                int sIdx = simulated.IndexOf(succ);
                simulated.Insert(sIdx, sid);
            }
        }

        for (int i = 0; i < complexTarget.Count; i++)
            TestAssert.Equal(complexTarget[i], simulated[i], $"Simulated order mismatch at index {i}");

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Outbox: Task Card Countdown", Group = TestGroups.Playlists, Order = 27)]
    public static Task TestTaskCardCountdownAsync()
    {
        var card = new Notification
        {
            IsTask = true,
            IsTaskActive = true,
            IsTaskProgressHidden = true,
            TaskDeadlineUtc = DateTime.UtcNow.AddSeconds(64.5)
        };

        TestAssert.Equal("1:05", card.TimeAgo);
        TestAssert.True(card.HasTaskCountdown);
        TestAssert.False(card.ShowTaskProgress);
        TestAssert.False(card.ShowTaskFooter);

        card.TaskDeadlineUtc = DateTime.UtcNow.AddSeconds(3599.5);
        TestAssert.Equal("1:00:00", card.TimeAgo);

        card.TaskDeadlineUtc = DateTime.UtcNow.AddSeconds(-1);
        TestAssert.Equal(LocalizationService.Instance["CloudTask_Overdue"], card.TimeAgo);

        card.TaskStatusText = "sending";
        TestAssert.Equal("sending", card.TimeAgo);
        TestAssert.False(card.HasTaskCountdown);

        card.TaskActionCommand = new RelayCommand(static () => { });
        card.TaskActionKey = "CloudTask_ForceSend";
        TestAssert.True(card.ShowTaskFooter);
        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Outbox: Summary Plurals Are Localized", Group = TestGroups.Playlists, Order = 28)]
    public static Task TestSummaryPluralsAsync()
    {
        var sl = LocalizationService.Instance;
        var one = sl.GetPlural("CloudTask_TracksAdded", 1);
        if (one == "1")
            TestAssert.Skip("Localization is not initialized");

        TestAssert.False(one.Contains('['), $"Missing plural key: {one}");
        TestAssert.False(sl["CloudTask_RetryingAuth"].Contains('['));
        TestAssert.False(sl["CloudTask_RetryingError"].Contains('['));

        if (sl.CurrentLanguageCode == "ru")
        {
            TestAssert.Equal("+1 трек", one);
            TestAssert.Equal("+3 трека", sl.GetPlural("CloudTask_TracksAdded", 3));
            TestAssert.Equal("+5 треков", sl.GetPlural("CloudTask_TracksAdded", 5));
            TestAssert.Equal("+11 треков", sl.GetPlural("CloudTask_TracksAdded", 11));
            TestAssert.Equal("+21 трек", sl.GetPlural("CloudTask_TracksAdded", 21));
            TestAssert.Equal("-2 трека", sl.GetPlural("CloudTask_TracksRemoved", 2));
        }
        else
        {
            TestAssert.Equal("+1 track", one);
            TestAssert.Equal("+3 tracks", sl.GetPlural("CloudTask_TracksAdded", 3));
            TestAssert.Equal("-5 tracks", sl.GetPlural("CloudTask_TracksRemoved", 5));
        }

        return Task.CompletedTask;
    }

    #region Helpers

    private static CloudPlaylistOutboxState NewState(string targetId) => new()
    {
        TargetId = targetId,
        OwnerId = Owner
    };

    private static HttpRequestException Http(HttpStatusCode status) => new($"HTTP {(int)status}", null, status);

    private static readonly Func<string, ISqliteConnectionFactory> CreateFactory =
        static path => new LowMemorySqliteConnectionFactory(path);

    private static string CreateTempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"lmp-tests-{Guid.NewGuid():N}.db");

    private static async Task CreateSchemaAsync(ISqliteConnectionFactory factory)
    {
        using var connection = factory.CreateConnection();
        connection.Open();
        await connection.EnsureTablesCreatedAsync().ConfigureAwait(false);
        await connection.MigrateSchemaAsync().ConfigureAwait(false);
    }

    private static async Task InsertBrokenOutboxRowAsync(ISqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenConnectionAsync(CancellationToken.None).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO CloudMutationOutbox (TargetId, OwnerId, PayloadJson, UpdatedAtTicks, RetryCount)
            VALUES ('pl_broken', 'test_owner', '{not json', 300, 0);
            """;
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static void CleanupTempDb(ISqliteConnectionFactory factory, string path)
    {
        SqliteConnection.ClearAllPools();
        (factory as IDisposable)?.Dispose();

        TryDelete(path);
        TryDelete(path + "-wal");
        TryDelete(path + "-shm");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    #endregion
}