using System.Net;
using LMP.Core.Data;
using LMP.Core.Data.Repositories;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Music;
using LMP.Tests.Framework;
using Microsoft.Data.Sqlite;

namespace LMP.Tests.Unit;

/// <summary>
/// Tests for the cloud mutation queue: in-flight operation coalescing, hydration merging,
/// failure classification, YouTube response alignment, persistence pump, and targeted SQL updates.
/// </summary>
public static class CloudOutboxTests
{
    private const string Owner = "test_owner";

    #region Likes

    [TestMethod(TestCategory.Unit, "Outbox: Like Toggle-Back During Flight", Group = TestGroups.Playlists, Order = 20)]
    public static Task TestLikeToggleBackDuringFlightAsync()
    {
        var state = NewState("__likes__");

        CloudOutboxCoalescer.ApplyLike(state, "yt_a", targetLiked: true, baselineLiked: false, isInFlight: false);

        // Like is already in-flight: reverting to the original value must not annihilate the record
        bool annihilated = CloudOutboxCoalescer.ApplyLike(state, "yt_a", targetLiked: false, baselineLiked: true, isInFlight: true);
        TestAssert.False(annihilated);
        TestAssert.True(CloudOutboxCoalescer.TryGetLikeTarget(state, "yt_a", out bool target));
        TestAssert.False(target);

        // In-flight "true" succeeded: record remains with base true and target false
        CloudOutboxCoalescer.CommitLike(state, "yt_a", syncedLiked: true);
        TestAssert.Equal(1, state.PendingLikes.Count);
        TestAssert.True(state.PendingLikes[0].BaselineLiked);
        TestAssert.False(state.PendingLikes[0].TargetLiked);

        CloudOutboxCoalescer.CommitLike(state, "yt_a", syncedLiked: false);
        TestAssert.True(state.IsEmpty);
        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Outbox: Like Annihilation Outside Flight", Group = TestGroups.Playlists, Order = 21)]
    public static Task TestLikeAnnihilationAsync()
    {
        var state = NewState("__likes__");

        CloudOutboxCoalescer.ApplyLike(state, "yt_a", targetLiked: true, baselineLiked: false, isInFlight: false);
        bool annihilated = CloudOutboxCoalescer.ApplyLike(state, "yt_a", targetLiked: false, baselineLiked: true, isInFlight: false);
        TestAssert.True(annihilated);
        TestAssert.True(state.IsEmpty);

        // Intent matching the base value does not create a record
        CloudOutboxCoalescer.ApplyLike(state, "yt_b", targetLiked: true, baselineLiked: true, isInFlight: false);
        TestAssert.True(state.IsEmpty);
        return Task.CompletedTask;
    }

    #endregion

    #region Tracks In Flight

    [TestMethod(TestCategory.Unit, "Outbox: Remove During In-Flight Add (Success)", Group = TestGroups.Playlists, Order = 22)]
    public static Task TestRemoveDuringInFlightAddSuccessAsync()
    {
        var state = NewState("pl_a");
        state.YoutubePlaylistId = "PL_A";
        CloudOutboxCoalescer.ApplyAddTracks(state, ["yt_a", "yt_b"], inFlightRemoves: null);
        var sentAdds = new HashSet<string>(state.PendingAddTrackIds, StringComparer.Ordinal);

        // User deleted the track while add is in-flight: deletion is preserved as a compensating mutation
        CloudOutboxCoalescer.ApplyRemoveTracks(state, [("yt_a", null)], sentAdds);
        TestAssert.Equal(2, state.PendingAddTrackIds.Count);
        TestAssert.Equal(1, state.PendingRemoveTracks.Count);

        CloudOutboxCoalescer.CommitAdds(state, ["yt_a", "yt_b"], [("yt_a", "SV_A"), ("yt_b", "SV_B")]);
        TestAssert.Equal(0, state.PendingAddTrackIds.Count);
        TestAssert.Equal("SV_A", state.PendingRemoveTracks[0].SetVideoId);
        TestAssert.False(CloudOutboxCoalescer.ResolveInFlightCompensations(state, sentAdds, new HashSet<string>(StringComparer.Ordinal)));
        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Outbox: Remove During In-Flight Add (Failure)", Group = TestGroups.Playlists, Order = 23)]
    public static Task TestRemoveDuringInFlightAddFailureAsync()
    {
        var state = NewState("pl_a");
        state.YoutubePlaylistId = "PL_A";
        CloudOutboxCoalescer.ApplyAddTracks(state, ["yt_a"], inFlightRemoves: null);
        var sentAdds = new HashSet<string>(state.PendingAddTrackIds, StringComparer.Ordinal);

        CloudOutboxCoalescer.ApplyRemoveTracks(state, [("yt_a", null)], sentAdds);

        // Add failed: final intent is "no track", retry of add is not sent
        bool changed = CloudOutboxCoalescer.ResolveInFlightCompensations(state, sentAdds, new HashSet<string>(StringComparer.Ordinal));
        TestAssert.True(changed);
        TestAssert.Equal(0, state.PendingAddTrackIds.Count);
        TestAssert.Equal(1, state.PendingRemoveTracks.Count);
        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Outbox: Re-Add During In-Flight Remove", Group = TestGroups.Playlists, Order = 24)]
    public static Task TestReAddDuringInFlightRemoveAsync()
    {
        var state = NewState("pl_a");
        state.YoutubePlaylistId = "PL_A";
        CloudOutboxCoalescer.ApplyRemoveTracks(state, [("yt_a", "SV_A")], inFlightAdds: null);
        var sentRemoves = new HashSet<string>(StringComparer.Ordinal) { "yt_a" };

        // Deletion in-flight: re-adding does not annihilate it, but queues as a compensating mutation
        var restored = CloudOutboxCoalescer.ApplyAddTracks(state, ["yt_a"], sentRemoves);
        TestAssert.Null(restored);
        TestAssert.Equal(1, state.PendingAddTrackIds.Count);
        TestAssert.Equal(1, state.PendingRemoveTracks.Count);

        // Deletion failed: track remains in the cloud, both operations are cleared
        bool changed = CloudOutboxCoalescer.ResolveInFlightCompensations(state, new HashSet<string>(StringComparer.Ordinal), sentRemoves);
        TestAssert.True(changed);
        TestAssert.True(state.IsEmpty);
        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Outbox: Re-Add Annihilates Pending Remove", Group = TestGroups.Playlists, Order = 25)]
    public static Task TestReAddAnnihilatesPendingRemoveAsync()
    {
        var state = NewState("pl_a");
        state.YoutubePlaylistId = "PL_A";
        CloudOutboxCoalescer.ApplyRemoveTracks(state, [("yt_a", "SV_A")], inFlightAdds: null);

        var restored = CloudOutboxCoalescer.ApplyAddTracks(state, ["yt_a"], inFlightRemoves: null);
        TestAssert.NotNull(restored);
        TestAssert.Equal(1, restored.Count);
        TestAssert.Equal("SV_A", restored[0].SetVideoId);
        TestAssert.True(state.IsEmpty);
        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Outbox: Pending Create Accepts Edits", Group = TestGroups.Playlists, Order = 26)]
    public static Task TestPendingCreateAcceptsEditsAsync()
    {
        var state = NewState("pl_new");
        state.PendingCreatePlaylist = true;

        CloudOutboxCoalescer.ApplyAddTracks(state, ["yt_a", "yt_b", "yt_c"], inFlightRemoves: null);
        CloudOutboxCoalescer.ApplyRemoveTracks(state, [("yt_b", null)], inFlightAdds: null);
        CloudOutboxCoalescer.ApplyReorder(state, ["yt_a", "yt_c"], ["yt_c", "yt_a"], isTargetInFlight: false);

        // Prior to playlist creation, reordering applies directly to the order of adds without move operations
        TestAssert.Equal(2, state.PendingAddTrackIds.Count);
        TestAssert.Equal("yt_c", state.PendingAddTrackIds[0]);
        TestAssert.Equal("yt_a", state.PendingAddTrackIds[1]);
        TestAssert.Equal(0, state.PendingRemoveTracks.Count);
        TestAssert.False(state.HasPendingReorder);
        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Outbox: Create Carries Metadata", Group = TestGroups.Playlists, Order = 44)]
    public static Task TestCreateCarriesMetadataAsync()
    {
        var state = NewState("pl_new");
        state.PendingCreatePlaylist = true;
        state.HasPendingName = true;
        state.TargetName = "Draft";
        state.HasPendingDescription = true;
        state.TargetDescription = "draft desc";

        // While creation is not yet in-flight, title and description changes are merged into the creation request itself
        TestAssert.True(CloudOutboxCoalescer.IsUnsentCreate(state, isTargetInFlight: false));
        TestAssert.False(CloudOutboxCoalescer.IsUnsentCreate(state, isTargetInFlight: true));
        CloudOutboxCoalescer.DropMetadataAbsorbedByCreate(state);
        TestAssert.False(state.HasPendingName);
        TestAssert.False(state.HasPendingDescription);
        TestAssert.False(state.IsEmpty);

        // Edits during Phase 0: changes matching the sent payload are cleared, newer changes remain with updated base
        state.HasPendingName = true;
        state.TargetName = "Sent";
        state.HasPendingDescription = true;
        state.TargetDescription = "Newer";
        CloudOutboxCoalescer.CommitMetadataSentWithCreate(state, "Sent", "Old desc");
        TestAssert.False(state.HasPendingName);
        TestAssert.Null(state.TargetName);
        TestAssert.True(state.HasPendingDescription);
        TestAssert.Equal("Old desc", state.BaselineDescription);
        TestAssert.Equal("Newer", state.TargetDescription);

        state.YoutubePlaylistId = "PL_created";
        TestAssert.False(CloudOutboxCoalescer.IsUnsentCreate(state, isTargetInFlight: false));
        return Task.CompletedTask;
    }

    #endregion

    #region Hydration Merge

    [TestMethod(TestCategory.Unit, "Outbox: Hydration Merge (Likes)", Group = TestGroups.Playlists, Order = 27)]
    public static Task TestHydrationMergeLikesAsync()
    {
        var restored = NewState("__likes__");
        CloudOutboxCoalescer.ApplyLike(restored, "yt_a", targetLiked: true, baselineLiked: false, isInFlight: false);

        // Prior to hydration, the user unliked yt_a and liked yt_b
        var live = NewState("__likes__");
        CloudOutboxCoalescer.ApplyLike(live, "yt_a", targetLiked: false, baselineLiked: true, isInFlight: false);
        CloudOutboxCoalescer.ApplyLike(live, "yt_b", targetLiked: true, baselineLiked: false, isInFlight: false);

        var merged = CloudOutboxCoalescer.MergeRestored(live, restored, out _);
        TestAssert.Equal(1, merged.PendingLikes.Count);
        TestAssert.Equal("yt_b", merged.PendingLikes[0].TrackId);
        TestAssert.True(merged.PendingLikes[0].TargetLiked);
        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Outbox: Hydration Merge (Playlist)", Group = TestGroups.Playlists, Order = 28)]
    public static Task TestHydrationMergePlaylistAsync()
    {
        var restored = NewState("pl_a");
        restored.YoutubePlaylistId = "PL_A";
        restored.PendingAddTrackIds.Add("yt_a");
        restored.PendingRemoveTracks.Add(new CloudTrackRemoveEntry { TrackId = "yt_b", SetVideoId = "SV_B" });

        var live = NewState("pl_a");
        live.PendingAddTrackIds.Add("yt_b");
        live.PendingAddTrackIds.Add("yt_c");
        live.PendingRemoveTracks.Add(new CloudTrackRemoveEntry { TrackId = "yt_a" });

        var merged = CloudOutboxCoalescer.MergeRestored(live, restored, out var restoredSetVideoIds);

        TestAssert.Equal("PL_A", merged.YoutubePlaylistId);
        TestAssert.Equal(1, merged.PendingAddTrackIds.Count);
        TestAssert.Equal("yt_c", merged.PendingAddTrackIds[0]);
        TestAssert.Equal(0, merged.PendingRemoveTracks.Count);
        TestAssert.NotNull(restoredSetVideoIds);
        TestAssert.Equal("SV_B", restoredSetVideoIds[0].SetVideoId);
        return Task.CompletedTask;
    }

    #endregion

    #region Failure Policy

    [TestMethod(TestCategory.Unit, "Outbox: Failure Classification", Group = TestGroups.Playlists, Order = 29)]
    public static Task TestFailureClassificationAsync()
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

        // HttpClient timeout arrives as TaskCanceledException with an uncancelled token
        TestAssert.Equal(CloudOutboxFailureKind.Transient, CloudOutboxCoalescer.ClassifyFailure(new TaskCanceledException(), ct));
        TestAssert.Equal(CloudOutboxFailureKind.Permanent, CloudOutboxCoalescer.ClassifyFailure(new FormatException("bad payload"), ct));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        TestAssert.Equal(CloudOutboxFailureKind.Canceled, CloudOutboxCoalescer.ClassifyFailure(new OperationCanceledException(cts.Token), cts.Token));
        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Outbox: Retry Backoff Is Monotonic", Group = TestGroups.Playlists, Order = 30)]
    public static Task TestRetryBackoffAsync()
    {
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

        return Task.CompletedTask;
    }

    #endregion

    #region SetVideoId Mapping

    [TestMethod(TestCategory.Unit, "Outbox: Placeholder SetVideoId Is Never Stored", Group = TestGroups.Playlists, Order = 31)]
    public static Task TestPlaceholderMappingAsync()
    {
        var mappings = CloudOutboxCoalescer.MapAddedSetVideoIds(
            ["yt_a", "yt_b", "yt_c"],
            ["SV_A", CloudOutboxCoalescer.PlaceholderSetVideoId, null]);

        TestAssert.Equal(1, mappings.Count);
        TestAssert.Equal("yt_a", mappings[0].TrackId);
        TestAssert.Equal("SV_A", mappings[0].SetVideoId);

        // Response length mismatch: index-based matching is forbidden
        TestAssert.Equal(0, CloudOutboxCoalescer.MapAddedSetVideoIds(["yt_a", "yt_b"], ["SV_A"]).Count);
        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Outbox: Edit Result Alignment", Group = TestGroups.Playlists, Order = 32)]
    public static Task TestEditResultAlignmentAsync()
    {
        // Server omitted the middle track: index shifting must not assign SV_C to track v2
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

    #endregion

    #region Persistence Pump

    [TestMethod(TestCategory.Unit, "Outbox: Persistence Pump Coalesces Writes", Group = TestGroups.Playlists, Order = 33, TimeoutSeconds = 15)]
    public static async Task TestPersistencePumpCoalescingAsync()
    {
        var writes = new List<IReadOnlyList<CloudOutboxRowChange>>();
        var gate = new Lock();
        int version = 0;

        var pump = new CloudOutboxPersistencePump(
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
            pump.MarkDirty(Owner, "pl_a");
        }

        TestAssert.True(await pump.DrainAsync(5_000).ConfigureAwait(false), "Pump did not drain in time");
        TestAssert.True(pump.BatchesWritten <= 2, $"Expected <= 2 batches, got {pump.BatchesWritten}");

        IReadOnlyList<CloudOutboxRowChange> last;
        lock (gate)
            last = writes[^1];

        TestAssert.Equal(1, last.Count);
        TestAssert.NotNull(last[0].State);
        TestAssert.Equal("yt_100", last[0].State!.PendingAddTrackIds[0]);
    }

    [TestMethod(TestCategory.Unit, "Outbox: Persistence Pump Waits For Hydration", Group = TestGroups.Playlists, Order = 34, TimeoutSeconds = 15)]
    public static async Task TestPersistencePumpPausedAsync()
    {
        int written = 0;
        var pump = new CloudOutboxPersistencePump(
            (_, _) => null,
            (changes, _) =>
            {
                Interlocked.Add(ref written, changes.Count);
                return Task.CompletedTask;
            },
            coalesceWindowMs: 0,
            startPaused: true);

        pump.MarkDirty(Owner, "pl_a");
        await Task.Delay(100).ConfigureAwait(false);

        TestAssert.Equal(0, Volatile.Read(ref written));
        TestAssert.False(await pump.DrainAsync(100).ConfigureAwait(false));

        pump.Resume();
        TestAssert.True(await pump.DrainAsync(5_000).ConfigureAwait(false));
        TestAssert.Equal(1, Volatile.Read(ref written));
        TestAssert.Equal(0, pump.PendingCount);
    }

    [TestMethod(TestCategory.Unit, "Outbox: Persistence Pump Retries Failed Batch", Group = TestGroups.Playlists, Order = 35, TimeoutSeconds = 15)]
    public static async Task TestPersistencePumpRetryAsync()
    {
        int calls = 0;
        var pump = new CloudOutboxPersistencePump(
            (_, targetId) => NewState(targetId),
            (_, _) => Interlocked.Increment(ref calls) == 1
                ? Task.FromException(new IOException("database is locked"))
                : Task.CompletedTask,
            coalesceWindowMs: 0);

        pump.MarkDirty(Owner, "pl_a");

        TestAssert.True(await pump.DrainAsync(5_000).ConfigureAwait(false));
        TestAssert.Equal(2, Volatile.Read(ref calls));
        TestAssert.Equal(1, pump.BatchesWritten);
    }

    #endregion

    #region SQLite

    [TestMethod(TestCategory.Unit, "Outbox: SQLite Store Roundtrip", Group = TestGroups.Playlists, Order = 36, TimeoutSeconds = 20)]
    public static async Task TestOutboxStoreRoundtripAsync()
    {
        var path = CreateTempDbPath();
        ISqliteConnectionFactory factory = CreateFactory(path);
        try
        {
            await CreateSchemaAsync(factory).ConfigureAwait(false);
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

            // Corrupted row is skipped without interrupting hydration
            await InsertBrokenOutboxRowAsync(factory).ConfigureAwait(false);

            var rows = await store.LoadAllAsync().ConfigureAwait(false);
            TestAssert.Equal(1, rows.Count);
            TestAssert.Equal("pl_b", rows[0].TargetId);
            TestAssert.Equal("SV_2", rows[0].PendingRemoveTracks[0].SetVideoId);
            TestAssert.Equal(200L, rows[0].UpdatedAtUtcTicks);
            TestAssert.Equal(2, rows[0].RetryCount);
        }
        finally
        {
            CleanupTempDb(factory, path);
        }
    }

    [TestMethod(TestCategory.Unit, "Outbox: UpdateCloudLinkState Is Targeted", Group = TestGroups.Playlists, Order = 37, TimeoutSeconds = 20)]
    public static async Task TestUpdateCloudLinkStateAsync()
    {
        var path = CreateTempDbPath();
        ISqliteConnectionFactory factory = CreateFactory(path);
        try
        {
            await CreateSchemaAsync(factory).ConfigureAwait(false);
            var repo = new PlaylistRepository(factory);

            // Row was deleted during creation: update does not resurrect it
            TestAssert.False(await repo.UpdateCloudLinkStateAsync("pl_missing", "PL_X", null, false).ConfigureAwait(false));

            await repo.UpsertAsync(new Playlist
            {
                Id = "pl_sync",
                Name = "Original",
                OwnerId = Owner,
                SyncMode = PlaylistSyncMode.TwoWaySync,
                Ownership = PlaylistOwnership.Unknown
            }).ConfigureAwait(false);

            // Concurrent renaming must not be overwritten by binding commitment
            await repo.RenameAsync("pl_sync", "Renamed").ConfigureAwait(false);
            TestAssert.True(await repo.UpdateCloudLinkStateAsync("pl_sync", "PL_NEW", null, false).ConfigureAwait(false));

            var linked = await repo.GetByIdAsync("pl_sync", Owner).ConfigureAwait(false);
            TestAssert.NotNull(linked);
            TestAssert.Equal("PL_NEW", linked.YoutubeId);
            TestAssert.Equal("Renamed", linked.Name);
            TestAssert.Equal(PlaylistOwnership.Mine, linked.Ownership);

            // Binding to another cloud playlist is not overwritten
            TestAssert.False(await repo.UpdateCloudLinkStateAsync("pl_sync", "PL_OTHER", null, false).ConfigureAwait(false));

            await repo.UpsertAsync(new Playlist
            {
                Id = "pl_local",
                Name = "Local",
                OwnerId = Owner,
                SyncMode = PlaylistSyncMode.LocalOnly
            }).ConfigureAwait(false);

            // Playlist unbound from cloud during creation: result is false, caller will delete the cloud copy
            TestAssert.False(await repo.UpdateCloudLinkStateAsync("pl_local", "PL_Y", null, false).ConfigureAwait(false));
        }
        finally
        {
            CleanupTempDb(factory, path);
        }
    }

    #endregion

    #region Localization

    [TestMethod(TestCategory.Unit, "Outbox: Summary Plurals Are Localized", Group = TestGroups.Playlists, Order = 38)]
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

    #endregion

    #region Cloud Rebase

    [TestMethod(TestCategory.Unit, "Outbox: Rejected Edit Detection", Group = TestGroups.Playlists, Order = 39)]
    public static Task TestRejectedEditDetectionAsync()
    {
        TestAssert.True(CloudOutboxCoalescer.IsRejectedEdit(Http(HttpStatusCode.BadRequest)));
        TestAssert.True(CloudOutboxCoalescer.IsRejectedEdit(
            new YoutubeExplodeException("Playlist operation failed with status: STATUS_FAILED")));

        // Transport and auth failures are not edit rejections and do not trigger rebasing
        TestAssert.False(CloudOutboxCoalescer.IsRejectedEdit(Http(HttpStatusCode.ServiceUnavailable)));
        TestAssert.False(CloudOutboxCoalescer.IsRejectedEdit(Http(HttpStatusCode.Unauthorized)));
        TestAssert.False(CloudOutboxCoalescer.IsRejectedEdit(new HttpRequestException("socket reset")));
        TestAssert.False(CloudOutboxCoalescer.IsRejectedEdit(new TimeoutException()));
        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Outbox: Removal Rebase On Cloud State", Group = TestGroups.Playlists, Order = 40)]
    public static Task TestRemovalRebaseAsync()
    {
        List<(string TrackId, string SetVideoId)> cloud =
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

        var rebased = CloudOutboxCoalescer.SelectRemovalsStillInCloud(removals, cloud);

        // SV_B deleted on another device: not sent; duplicate SV_A not repeated; yt_c resolved by occurrences
        TestAssert.Equal(3, rebased.Count);
        TestAssert.Equal("SV_A", rebased[0].SetVideoId);
        TestAssert.Equal("SV_C", rebased[1].SetVideoId);
        TestAssert.Equal("SV_C2", rebased[2].SetVideoId);
        TestAssert.Equal(0, CloudOutboxCoalescer.SelectRemovalsStillInCloud(removals, []).Count);
        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Outbox: Reorder Rebase Skips Deleted", Group = TestGroups.Playlists, Order = 41)]
    public static Task TestReorderRebaseAsync()
    {
        // Locally: d moved to start. In cloud: b already deleted, e added, a duplicated
        List<(string TrackId, string SetVideoId)> cloud =
        [
            ("yt_a", "SV_A"), ("yt_c", "SV_C"), ("yt_d", "SV_D"), ("yt_e", "SV_E"), ("yt_a", "SV_A2")
        ];

        var (current, desired) = CloudOutboxCoalescer.BuildRebasedReorder(cloud, ["yt_d", "yt_a", "yt_b", "yt_c"]);
        TestAssert.Equal(4, current.Count);
        TestAssert.Equal(3, desired.Count);
        TestAssert.Equal("SV_D", desired[0]);
        TestAssert.Equal("SV_A", desired[1]);
        TestAssert.Equal("SV_C", desired[2]);

        var moves = PlaylistSyncService.BuildMinimalReorderMoves(current, desired);
        TestAssert.True(moves.Count > 0);

        var simulated = new List<string>(cloud.Count);
        for (int i = 0; i < cloud.Count; i++)
            simulated.Add(cloud[i].SetVideoId);

        for (int i = 0; i < moves.Count; i++)
        {
            var (setVideoId, predecessor, successor) = moves[i];

            // No move operation references a SetVideoId deleted in the cloud (otherwise YouTube returns 400)
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

        return Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "Outbox: Cloud-Deleted Tracks Selection", Group = TestGroups.Playlists, Order = 42)]
    public static Task TestCloudDeletedSelectionAsync()
    {
        List<(string TrackId, string? SetVideoId)> local =
        [
            ("yt_a", "SV_A"), ("yt_b", "SV_B"), ("yt_new", null),
            ("yt_ph", CloudOutboxCoalescer.PlaceholderSetVideoId), ("yt_c", "SV_C")
        ];
        var cloud = new HashSet<string>(StringComparer.Ordinal) { "yt_a", "yt_c", "yt_x" };

        // Only cloud-bound tracks are considered deleted; new ones and placeholders are not yet uploaded
        var deleted = CloudOutboxCoalescer.SelectCloudDeletedTrackIds(local, cloud);
        TestAssert.NotNull(deleted);
        TestAssert.Equal(1, deleted.Count);
        TestAssert.True(deleted.Contains("yt_b"));

        // Empty cloud response must not wipe out local playlist
        var emptyCloud = new HashSet<string>(StringComparer.Ordinal);
        TestAssert.Null(CloudOutboxCoalescer.SelectCloudDeletedTrackIds(local, emptyCloud));

        List<(string TrackId, string? SetVideoId)> many = [];
        for (int i = 0; i < 8; i++)
            many.Add(($"yt_{i}", $"SV_{i}"));

        var partialCloud = new HashSet<string>(StringComparer.Ordinal) { "yt_0", "yt_1" };
        TestAssert.Null(CloudOutboxCoalescer.SelectCloudDeletedTrackIds(many, partialCloud));

        var nothingLinked = CloudOutboxCoalescer.SelectCloudDeletedTrackIds([("yt_new", null)], emptyCloud);
        TestAssert.NotNull(nothingLinked);
        TestAssert.Equal(0, nothingLinked.Count);
        return Task.CompletedTask;
    }

    #endregion

    #region Task Card

    [TestMethod(TestCategory.Unit, "Outbox: Task Card Countdown", Group = TestGroups.Playlists, Order = 43)]
    public static Task TestTaskCardCountdownAsync()
    {
        var card = new Notification
        {
            IsTask = true,
            IsTaskActive = true,
            IsTaskProgressHidden = true,
            TaskDeadlineUtc = DateTime.UtcNow.AddSeconds(64.5)
        };

        // Seconds are rounded up to prevent lingering at "0:00" prior to actual sending
        TestAssert.Equal("1:05", card.TimeAgo);
        TestAssert.True(card.HasTaskCountdown);
        TestAssert.False(card.ShowTaskProgress);
        TestAssert.False(card.ShowTaskFooter);

        card.TaskDeadlineUtc = DateTime.UtcNow.AddSeconds(3599.5);
        TestAssert.Equal("1:00:00", card.TimeAgo);

        // Overdue deadline (worker waiting for network/auth) must not freeze at "0:00"
        card.TaskDeadlineUtc = DateTime.UtcNow.AddSeconds(-1);
        TestAssert.Equal(LocalizationService.Instance["CloudTask_Overdue"], card.TimeAgo);

        // Explicit task status takes precedence over countdown, and no timer is needed for it
        card.TaskStatusText = "sending";
        TestAssert.Equal("sending", card.TimeAgo);
        TestAssert.False(card.HasTaskCountdown);

        card.TaskActionCommand = new RelayCommand(static () => { });
        card.TaskActionKey = "CloudTask_ForceSend";
        TestAssert.True(card.ShowTaskFooter);
        return Task.CompletedTask;
    }

    #endregion

    #region Helpers

    private static CloudPlaylistOutboxState NewState(string targetId) => new()
    {
        TargetId = targetId,
        OwnerId = Owner
    };

    private static HttpRequestException Http(HttpStatusCode status) => new($"HTTP {(int)status}", null, status);

    // Delegate hides concrete type: tests operate via contract, mirroring DI in AppEntry (CA1859)
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
            // Temp file will be deleted by the OS: test must not fail due to antivirus locks
        }
    }

    #endregion
}
