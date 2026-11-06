using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using LMP.Core.Data;
using LMP.Core.Data.Repositories;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Music;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Videos;
using Microsoft.Data.Sqlite;

namespace LMP.Core.Services;

/// <summary>
/// Sync service for local playlists and YouTube Music platform, managing the Smart Cloud Mutation Outbox Queue.
/// Encapsulates operation coalescing, LIS-based permutation compression, SQLite persistence, network status awareness, and conflict resolution.
/// </summary>
public sealed class PlaylistSyncService : IDisposable, IAsyncDisposable
{
    /// <summary>Identifier of the system queue bucket for like mutations.</summary>
    public const string LikesOutboxTargetId = "__likes__";

    private const int QuietGuardWindowMs = 3_000;
    private const int PermanentFailureStrikeLimit = 2;
    private const int MaxRetryBackoffMs = 60_000;
    private const int BaseRetryBackoffMs = 3_000;
    private const int AuthParkBackoffMs = 300_000;
    private const int ForbiddenConfirmationAttempts = 2;
    private const int RecentFlushTombstoneTtlMs = 300_000;
    private const int MaxHydrationAttempts = 3;
    private const int ShutdownWorkerWaitMs = 2_000;
    private const int ShutdownPersistDrainMs = 3_000;
    private const int ScottyAudioBufferingWaitStepMs = 500;
    private const int ScottyAudioBufferingMaxWaitMs = 10_000;

    private readonly IPlaylistRepository _playlists;
    private readonly ITrackRepository _tracks;
    private readonly TrackRegistry _registry;
    private readonly YoutubeProvider _youtube;
    private readonly CookieAuthService _auth;
    private readonly YoutubeUserDataService _ytUser;
    private readonly NotificationService _notifications;
    private readonly NetworkManager _networkManager;
    private readonly ISqliteConnectionFactory? _connectionFactory;
    private readonly LibraryService? _library;
    private readonly CloudOutboxStore? _store;
    private readonly CloudOutboxPersistencePump? _persistencePump;

    private readonly Dictionary<(string OwnerId, string TargetId), CloudPlaylistOutboxState> _outboxByKey = [];
    private readonly Dictionary<(string OwnerId, string TargetId), CloudOutboxFailureKind> _lastFailureKind = [];

    // Counter for unrecoverable failures (400) by target: not reset by new edits or phase commits,
    // so a "poisoned" task does not loop indefinitely
    private readonly Dictionary<(string OwnerId, string TargetId), int> _permanentStrikes = [];
    private readonly Dictionary<(string OwnerId, string TrackId), (bool Liked, long ExpiresAtTicks)> _likeTombstones = [];
    private readonly Dictionary<(string OwnerId, string YoutubeId), long> _deleteTombstones = [];
    private readonly Dictionary<string, RelayCommand> _forceCommandCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inFlightAddIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inFlightRemoveIds = new(StringComparer.Ordinal);
    private readonly Lock _outboxLock = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private TaskCompletionSource _wakeUpTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _workerTask;
    private Task? _hydrationTask;

    // In-flight operations (under _outboxLock): current flush target and the like mutation currently being sent
    private string? _inFlightOwnerId;
    private string? _inFlightTargetId;
    private string? _inFlightLikeTrackId;

    private volatile bool _isHydratedFromDb;
    private volatile bool _disposed;
    private int _shutdownStarted;
    private int? _explicitDebounceOverrideMs;

    private static LocalizationService SL => LocalizationService.Instance;

    /// <summary>
    /// Coalescing window before sending the accumulated batch to the cloud (in milliseconds).
    /// Read by default from user setting <see cref="AppSettings.CloudBatchInterval"/>
    /// (10 sec – 3 hrs, 1 min by default).
    /// </summary>
    public int DebounceWindowMs
    {
        get
        {
            if (_explicitDebounceOverrideMs.HasValue)
                return _explicitDebounceOverrideMs.Value;

            int sec = _library != null ? (int)_library.Settings.CloudBatchInterval : 60;
            return Math.Clamp(sec, 1, 10_800) * 1000;
        }
        set => _explicitDebounceOverrideMs = value;
    }

    /// <summary>
    /// Optional predicate checking whether initial audio stream buffering is currently active.
    /// Used to defer heavy Scotty cover uploads during track startup.
    /// </summary>
    public Func<bool>? IsAudioBufferingCheck { get; set; }

    /// <summary>
    /// Event raised when background metadata changes occur, YoutubeId is created, or playlist permissions are downgraded.
    /// </summary>
    public event Action<Playlist>? OnCloudPlaylistStateChanged;

    private string CurrentOwnerId => _auth.State.DisplayId;

    /// <summary>
    /// Initializes a new instance of the playlist synchronization service and background mutation queue.
    /// </summary>
    public PlaylistSyncService(
        IPlaylistRepository playlists,
        ITrackRepository tracks,
        TrackRegistry registry,
        YoutubeProvider youtube,
        CookieAuthService auth,
        YoutubeUserDataService ytUser,
        NotificationService notifications,
        NetworkManager networkManager,
        ISqliteConnectionFactory? connectionFactory = null,
        LibraryService? library = null)
    {
        _playlists = playlists;
        _tracks = tracks;
        _registry = registry;
        _youtube = youtube;
        _auth = auth;
        _ytUser = ytUser;
        _notifications = notifications;
        _networkManager = networkManager;
        _connectionFactory = connectionFactory;
        _library = library;

        if (connectionFactory != null)
        {
            _store = new CloudOutboxStore(connectionFactory);
            // Writing to SQLite is only allowed after hydration to prevent overwriting previous session rows
            _persistencePump = new CloudOutboxPersistencePump(GetPersistSnapshot, _store.WriteBatchAsync, startPaused: true);
        }

        _networkManager.NetworkRebuilt += HandleNetworkOrAuthRestored;
        _auth.OnAuthStateChanged += HandleNetworkOrAuthRestored;

        _workerTask = Task.Run(OutboxWorkerLoopAsync);
    }

    #region Smart Cloud Mutation Outbox — Enqueue & Coalescing API

    /// <summary>
    /// Returns a snapshot of the current coalesced queue state for the specified playlist or likes bucket.
    /// </summary>
    public CloudPlaylistOutboxState? GetPendingStateSnapshot(string targetId, string? ownerId = null)
    {
        var owner = ownerId ?? CurrentOwnerId;
        lock (_outboxLock)
        {
            if (!_outboxByKey.TryGetValue((owner, targetId), out var state) || state.IsEmpty)
                return null;

            return CloneStateUnderLock(state);
        }
    }

    /// <summary>
    /// Queues Phase 0 of cloud playlist creation (offline-optimistic binding without blocking UI).
    /// </summary>
    public void EnqueueCreatePlaylist(Playlist playlist, IReadOnlyList<string>? initialTrackIds = null)
    {
        if (_disposed || !_auth.IsAuthenticated)
            return;

        var ownerId = CurrentOwnerId;
        CloudPlaylistOutboxState snapshot;

        lock (_outboxLock)
        {
            var state = GetOrCreateStateUnderLock(ownerId, playlist.Id, playlist.YoutubeId, playlist.Name);
            state.PendingDeletePlaylist = false;
            state.PendingCreatePlaylist = string.IsNullOrEmpty(playlist.YoutubeId);

            // Title and description will be sent in the creation request itself: separate edits would only double the requests
            bool createCarriesMetadata = CloudOutboxCoalescer.IsUnsentCreate(
                state, IsTargetInFlightUnderLock(ownerId, playlist.Id));
            if (createCarriesMetadata)
            {
                CloudOutboxCoalescer.DropMetadataAbsorbedByCreate(state);
            }
            else if (!string.IsNullOrWhiteSpace(playlist.Description))
            {
                state.HasPendingDescription = true;
                state.TargetDescription = playlist.Description;
            }

            if (!string.IsNullOrWhiteSpace(playlist.ThumbnailUrl) &&
                !ThumbnailComparer.IsYoutubeHosted(playlist.ThumbnailUrl))
            {
                state.HasPendingThumbnail = true;
                state.TargetThumbnailUrl = playlist.ThumbnailUrl;
            }

            if (initialTrackIds is { Count: > 0 })
            {
                for (int i = 0; i < initialTrackIds.Count; i++)
                {
                    var tId = initialTrackIds[i];
                    if (tId.StartsWith("yt_", StringComparison.Ordinal) &&
                        !ContainsOrdinal(state.PendingAddTrackIds, tId))
                    {
                        state.PendingAddTrackIds.Add(tId);
                    }
                }
            }

            TouchTimestampsUnderLock(state);
            snapshot = CloneStateUnderLock(state);
        }

        Log.Debug($"[PlaylistOutbox] Enqueued CreatePlaylist for '{playlist.Name}' ({playlist.Id}), tracks={snapshot.PendingAddTrackIds.Count}, thumb={snapshot.HasPendingThumbnail}");
        PersistOrDeleteAsyncFireAndForget(ownerId, playlist.Id, snapshot, shouldDelete: false);
        SyncTaskNotificationForState(snapshot);
        SignalWorker();
    }

    /// <summary>
    /// Queues deletion of a cloud playlist.
    /// If the playlist has not been created in the cloud yet (<c>PendingCreatePlaylist == true</c> and <c>YoutubePlaylistId == null</c>),
    /// the <c>Create + Delete</c> pair mutually annihilates down to 0 network calls.
    /// </summary>
    public void EnqueueDeletePlaylist(Playlist playlist)
    {
        if (_disposed || !_auth.IsAuthenticated)
            return;

        var ownerId = CurrentOwnerId;
        CloudPlaylistOutboxState? snapshot = null;
        bool annihilated = false;

        lock (_outboxLock)
        {
            _outboxByKey.TryGetValue((ownerId, playlist.Id), out var existing);
            string? knownYoutubeId = !string.IsNullOrEmpty(playlist.YoutubeId)
                ? playlist.YoutubeId
                : existing?.YoutubePlaylistId;

            // While Phase 0 is in-flight, the cloud playlist might appear: Create + Delete annihilation is forbidden,
            // deletion will be performed with the YoutubeId committed by the completed flush.
            if (string.IsNullOrEmpty(knownYoutubeId) && !IsTargetInFlightUnderLock(ownerId, playlist.Id))
            {
                _outboxByKey.Remove((ownerId, playlist.Id));
                annihilated = true;
            }
            else
            {
                var state = GetOrCreateStateUnderLock(ownerId, playlist.Id, knownYoutubeId, playlist.Name);
                CloudOutboxCoalescer.ApplyDeletePlaylist(state);
                TouchTimestampsUnderLock(state);
                snapshot = CloneStateUnderLock(state);
            }
        }

        if (annihilated)
        {
            Log.Debug($"[PlaylistOutbox] Annihilated Create+Delete for unsynced playlist '{playlist.Name}' ({playlist.Id}) -> 0 network requests");
            PersistOrDeleteAsyncFireAndForget(ownerId, playlist.Id, null, shouldDelete: true);
            _notifications.CompleteTaskNotification(GetTaskKey(playlist.Id), removeIfAnnihilated: true);
        }
        else if (snapshot != null)
        {
            Log.Debug($"[PlaylistOutbox] Enqueued DeletePlaylist for '{playlist.Name}' (YT: {snapshot.YoutubePlaylistId ?? "<in-flight create>"})");
            PersistOrDeleteAsyncFireAndForget(ownerId, playlist.Id, snapshot, shouldDelete: false);
            SyncTaskNotificationForState(snapshot);
            SignalWorker();
        }
    }

    /// <summary>
    /// Queues a "Like" mutation with automatic annihilation of <c>Like + Unlike</c> pairs.
    /// </summary>
    public void EnqueueLikeMutation(string trackId, bool targetLiked, bool? baselineLiked = null)
    {
        if (_disposed || !_auth.IsAuthenticated || string.IsNullOrWhiteSpace(trackId))
            return;

        var normId = trackId.StartsWith("yt_", StringComparison.Ordinal) ? trackId : "yt_" + trackId;
        var ownerId = CurrentOwnerId;
        CloudPlaylistOutboxState? snapshotToPersist = null;
        bool shouldDeleteRow = false;
        bool wasAnnihilated;

        lock (_outboxLock)
        {
            var state = GetOrCreateStateUnderLock(ownerId, LikesOutboxTargetId, null, SL["CloudTask_LikesTitle"]);
            bool isInFlight = string.Equals(_inFlightLikeTrackId, normId, StringComparison.Ordinal) &&
                              IsTargetInFlightUnderLock(ownerId, LikesOutboxTargetId);

            wasAnnihilated = CloudOutboxCoalescer.ApplyLike(state, normId, targetLiked, baselineLiked, isInFlight);
            TouchTimestampsUnderLock(state);

            if (state.IsEmpty)
            {
                _outboxByKey.Remove((ownerId, LikesOutboxTargetId));
                shouldDeleteRow = true;
            }
            else
            {
                snapshotToPersist = CloneStateUnderLock(state);
            }
        }

        if (shouldDeleteRow)
        {
            Log.Debug($"[PlaylistOutbox] Like mutation for '{normId}' annihilated (returned to baseline). Pending likes=0");
            PersistOrDeleteAsyncFireAndForget(ownerId, LikesOutboxTargetId, null, shouldDelete: true);
            _notifications.CompleteTaskNotification(GetTaskKey(LikesOutboxTargetId), removeIfAnnihilated: true);
        }
        else if (snapshotToPersist != null)
        {
            Log.Debug($"[PlaylistOutbox] {(wasAnnihilated ? "Annihilated single like" : $"Enqueued Like={targetLiked}")} for '{normId}'. Total pending likes={snapshotToPersist.PendingLikes.Count}");
            PersistOrDeleteAsyncFireAndForget(ownerId, LikesOutboxTargetId, snapshotToPersist, shouldDelete: false);
            SyncTaskNotificationForState(snapshotToPersist);
            SignalWorker();
        }
    }

    /// <summary>
    /// Queues batch addition of tracks to a cloud playlist, annihilating tracks previously deleted within the same window.
    /// </summary>
    public void EnqueueAddTracks(Playlist playlist, IReadOnlyList<string> trackIds)
    {
        if (_disposed || !_auth.IsAuthenticated || !playlist.IsFromAccount || !playlist.IsEditable || trackIds.Count == 0)
            return;

        var ownerId = CurrentOwnerId;
        CloudPlaylistOutboxState? snapshotToPersist = null;
        bool shouldDeleteRow = false;
        List<(string TrackId, string SetVideoId)>? restoredSetVideoIds;

        lock (_outboxLock)
        {
            var state = GetOrCreateStateUnderLock(ownerId, playlist.Id, playlist.YoutubeId, playlist.Name);
            var inFlightRemoves = IsTargetInFlightUnderLock(ownerId, playlist.Id) ? _inFlightRemoveIds : null;
            restoredSetVideoIds = CloudOutboxCoalescer.ApplyAddTracks(state, trackIds, inFlightRemoves);

            TouchTimestampsUnderLock(state);

            if (state.IsEmpty)
            {
                _outboxByKey.Remove((ownerId, playlist.Id));
                shouldDeleteRow = true;
            }
            else
            {
                snapshotToPersist = CloneStateUnderLock(state);
            }
        }

        if (restoredSetVideoIds is { Count: > 0 })
        {
            _ = RestoreSetVideoIdsNoThrowAsync(playlist.Id, restoredSetVideoIds);
        }

        if (shouldDeleteRow)
        {
            Log.Debug($"[PlaylistOutbox] AddTracks for '{playlist.Name}' annihilated pending removes -> 0 network requests");
            PersistOrDeleteAsyncFireAndForget(ownerId, playlist.Id, null, shouldDelete: true);
            _notifications.CompleteTaskNotification(GetTaskKey(playlist.Id), removeIfAnnihilated: true);
        }
        else if (snapshotToPersist != null)
        {
            Log.Debug($"[PlaylistOutbox] Enqueued +{trackIds.Count} track(s) for '{playlist.Name}' (pending adds={snapshotToPersist.PendingAddTrackIds.Count}, removes={snapshotToPersist.PendingRemoveTracks.Count})");
            PersistOrDeleteAsyncFireAndForget(ownerId, playlist.Id, snapshotToPersist, shouldDelete: false);
            SyncTaskNotificationForState(snapshotToPersist);
            SignalWorker();
        }
    }

    private async Task RestoreSetVideoIdsNoThrowAsync(string playlistId, List<(string TrackId, string SetVideoId)> mappings)
    {
        try
        {
            await _playlists.UpdateSetVideoIdsAsync(playlistId, mappings, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug($"[PlaylistOutbox] SetVideoId restore skipped for '{playlistId}': {ex.Message}");
        }
    }

    /// <summary>
    /// Queues batch removal of tracks from a cloud playlist.
    /// Annihilates tracks added in the current coalescing window that have not been sent to YouTube yet.
    /// </summary>
    public void EnqueueRemoveTracks(
        Playlist playlist,
        IReadOnlyList<(string TrackId, string? SetVideoId)> removedTracks)
    {
        if (_disposed || !_auth.IsAuthenticated || !playlist.IsFromAccount || !playlist.IsEditable || removedTracks.Count == 0)
            return;

        var ownerId = CurrentOwnerId;
        CloudPlaylistOutboxState? snapshotToPersist = null;
        bool shouldDeleteRow = false;

        lock (_outboxLock)
        {
            var state = GetOrCreateStateUnderLock(ownerId, playlist.Id, playlist.YoutubeId, playlist.Name);
            var inFlightAdds = IsTargetInFlightUnderLock(ownerId, playlist.Id) ? _inFlightAddIds : null;
            CloudOutboxCoalescer.ApplyRemoveTracks(state, removedTracks, inFlightAdds);

            TouchTimestampsUnderLock(state);

            if (state.IsEmpty)
            {
                _outboxByKey.Remove((ownerId, playlist.Id));
                shouldDeleteRow = true;
            }
            else
            {
                snapshotToPersist = CloneStateUnderLock(state);
            }
        }

        if (shouldDeleteRow)
        {
            Log.Debug($"[PlaylistOutbox] RemoveTracks for '{playlist.Name}' annihilated pending adds -> 0 network requests");
            PersistOrDeleteAsyncFireAndForget(ownerId, playlist.Id, null, shouldDelete: true);
            _notifications.CompleteTaskNotification(GetTaskKey(playlist.Id), removeIfAnnihilated: true);
        }
        else if (snapshotToPersist != null)
        {
            Log.Debug($"[PlaylistOutbox] Enqueued -{removedTracks.Count} track(s) for '{playlist.Name}' (pending removes={snapshotToPersist.PendingRemoveTracks.Count}, adds={snapshotToPersist.PendingAddTrackIds.Count})");
            PersistOrDeleteAsyncFireAndForget(ownerId, playlist.Id, snapshotToPersist, shouldDelete: false);
            SyncTaskNotificationForState(snapshotToPersist);
            SignalWorker();
        }
    }

    /// <summary>
    /// Queues reordering of tracks in a playlist, coalescing a series of drag-and-drops into a single batch.
    /// </summary>
    public void EnqueueReorderTracks(
        Playlist playlist,
        IReadOnlyList<string> baselineOrder,
        IReadOnlyList<string> newOrder)
    {
        if (_disposed || !_auth.IsAuthenticated || !playlist.IsFromAccount || !playlist.IsEditable || newOrder.Count <= 1)
            return;

        var ownerId = CurrentOwnerId;
        CloudPlaylistOutboxState? snapshotToPersist = null;
        bool shouldDeleteRow = false;

        lock (_outboxLock)
        {
            var state = GetOrCreateStateUnderLock(ownerId, playlist.Id, playlist.YoutubeId, playlist.Name);
            CloudOutboxCoalescer.ApplyReorder(state, baselineOrder, newOrder, IsTargetInFlightUnderLock(ownerId, playlist.Id));

            TouchTimestampsUnderLock(state);

            if (state.IsEmpty)
            {
                _outboxByKey.Remove((ownerId, playlist.Id));
                shouldDeleteRow = true;
            }
            else
            {
                snapshotToPersist = CloneStateUnderLock(state);
            }
        }

        if (shouldDeleteRow)
        {
            Log.Debug($"[PlaylistOutbox] Reorder for '{playlist.Name}' returned to baseline order -> annihilated (0 requests)");
            PersistOrDeleteAsyncFireAndForget(ownerId, playlist.Id, null, shouldDelete: true);
            _notifications.CompleteTaskNotification(GetTaskKey(playlist.Id), removeIfAnnihilated: true);
        }
        else if (snapshotToPersist != null)
        {
            Log.Debug($"[PlaylistOutbox] Coalesced track reorder for '{playlist.Name}' (hasReorder={snapshotToPersist.HasPendingReorder}, pendingAdds={snapshotToPersist.PendingAddTrackIds.Count})");
            PersistOrDeleteAsyncFireAndForget(ownerId, playlist.Id, snapshotToPersist, shouldDelete: false);
            SyncTaskNotificationForState(snapshotToPersist);
            SignalWorker();
        }
    }

    /// <summary>
    /// Queues playlist metadata modification (title, description, cover) using Last-Write-Wins (LWW).
    /// </summary>
    public void EnqueueMetadataUpdate(
        Playlist playlist,
        string? newName = null,
        string? baselineName = null,
        string? newDescription = null,
        string? baselineDescription = null,
        string? newThumbnailUrl = null,
        bool thumbnailChanged = false)
    {
        if (_disposed || !_auth.IsAuthenticated || !playlist.IsFromAccount || !playlist.IsEditable)
            return;

        var ownerId = CurrentOwnerId;
        CloudPlaylistOutboxState? snapshotToPersist = null;
        bool shouldDeleteRow = false;

        lock (_outboxLock)
        {
            var state = GetOrCreateStateUnderLock(ownerId, playlist.Id, playlist.YoutubeId, playlist.Name);

            if (newName != null)
            {
                state.PlaylistDisplayName = newName;
                state.BaselineName ??= baselineName;
                if (state.BaselineName != null && string.Equals(state.BaselineName, newName, StringComparison.Ordinal))
                {
                    state.HasPendingName = false;
                    state.BaselineName = null;
                    state.TargetName = null;
                }
                else
                {
                    state.HasPendingName = true;
                    state.TargetName = newName;
                }
            }

            if (newDescription != null)
            {
                state.BaselineDescription ??= baselineDescription;
                if (state.BaselineDescription != null &&
                    string.Equals(state.BaselineDescription, newDescription, StringComparison.Ordinal))
                {
                    state.HasPendingDescription = false;
                    state.BaselineDescription = null;
                    state.TargetDescription = null;
                }
                else
                {
                    state.HasPendingDescription = true;
                    state.TargetDescription = newDescription;
                }
            }

            if (thumbnailChanged)
            {
                if (string.IsNullOrWhiteSpace(newThumbnailUrl) || ThumbnailComparer.IsYoutubeHosted(newThumbnailUrl))
                {
                    state.HasPendingThumbnail = false;
                    state.TargetThumbnailUrl = null;
                }
                else
                {
                    state.HasPendingThumbnail = true;
                    state.TargetThumbnailUrl = newThumbnailUrl;
                }
            }

            // Playlist not created in cloud yet: Phase 0 will read title and description already saved in DB
            if (string.IsNullOrEmpty(playlist.YoutubeId) &&
                CloudOutboxCoalescer.IsUnsentCreate(state, IsTargetInFlightUnderLock(ownerId, playlist.Id)))
            {
                CloudOutboxCoalescer.DropMetadataAbsorbedByCreate(state);
            }

            TouchTimestampsUnderLock(state);

            if (state.IsEmpty)
            {
                _outboxByKey.Remove((ownerId, playlist.Id));
                shouldDeleteRow = true;
            }
            else
            {
                snapshotToPersist = CloneStateUnderLock(state);
            }
        }

        if (shouldDeleteRow)
        {
            Log.Debug($"[PlaylistOutbox] Metadata edit for '{playlist.Name}' annihilated back to baseline");
            PersistOrDeleteAsyncFireAndForget(ownerId, playlist.Id, null, shouldDelete: true);
            _notifications.CompleteTaskNotification(GetTaskKey(playlist.Id), removeIfAnnihilated: true);
        }
        else if (snapshotToPersist != null)
        {
            Log.Debug($"[PlaylistOutbox] Enqueued LWW metadata for '{playlist.Name}' (name={snapshotToPersist.HasPendingName}, desc={snapshotToPersist.HasPendingDescription}, thumb={snapshotToPersist.HasPendingThumbnail})");
            PersistOrDeleteAsyncFireAndForget(ownerId, playlist.Id, snapshotToPersist, shouldDelete: false);
            SyncTaskNotificationForState(snapshotToPersist);
            SignalWorker();
        }
    }

    /// <summary>
    /// Instantly cancels all pending cloud mutations for the specified playlist.
    /// </summary>
    public void DiscardPlaylistOutbox(string playlistId, string? ownerId = null)
    {
        if (string.IsNullOrEmpty(playlistId)) return;
        var owner = ownerId ?? CurrentOwnerId;

        bool removed;
        lock (_outboxLock)
        {
            removed = _outboxByKey.Remove((owner, playlistId));
            _permanentStrikes.Remove((owner, playlistId));
        }

        if (removed || _connectionFactory != null)
        {
            PersistOrDeleteAsyncFireAndForget(owner, playlistId, null, shouldDelete: true);
        }

        _notifications.CompleteTaskNotification(GetTaskKey(playlistId), removeIfAnnihilated: true);
    }

    /// <summary>
    /// Returns a set of cloud <c>YoutubeId</c>s currently pending deletion in the Outbox queue.
    /// Used during account sync to avoid re-importing playlists queued for deletion.
    /// </summary>
    public HashSet<string> GetPendingDeletedYoutubeIds(string? ownerId = null)
    {
        var owner = ownerId ?? CurrentOwnerId;
        var result = new HashSet<string>(StringComparer.Ordinal);

        lock (_outboxLock)
        {
            foreach (var kvp in _outboxByKey)
            {
                if (string.Equals(kvp.Key.OwnerId, owner, StringComparison.Ordinal) &&
                    kvp.Value.PendingDeletePlaylist &&
                    !string.IsNullOrEmpty(kvp.Value.YoutubePlaylistId))
                {
                    result.Add(kvp.Value.YoutubePlaylistId!);
                }
            }

            // Recently deleted playlists: YouTube account list updates with a delay
            PruneExpiredTombstonesUnderLock(DateTime.UtcNow.Ticks);
            foreach (var key in _deleteTombstones.Keys)
            {
                if (string.Equals(key.OwnerId, owner, StringComparison.Ordinal))
                    result.Add(key.YoutubeId);
            }
        }

        return result;
    }

    /// <summary>
    /// Async version of <see cref="GetPendingDeletedYoutubeIds"/> that awaits queue hydration from SQLite,
    /// preventing early account sync from re-importing a playlist whose deletion hasn't been restored yet.
    /// </summary>
    public async Task<HashSet<string>> GetPendingDeletedYoutubeIdsAsync(string? ownerId = null, CancellationToken ct = default)
    {
        await EnsureOutboxHydratedAsync(ct).ConfigureAwait(false);
        return GetPendingDeletedYoutubeIds(ownerId);
    }

    /// <summary>
    /// Cancels a pending cloud playlist deletion in the Outbox by its <c>YoutubeId</c> (<c>Delete + Re-Import</c> annihilation).
    /// </summary>
    public bool CancelPendingDeleteByYoutubeId(string youtubeId, string? ownerId = null)
    {
        if (string.IsNullOrEmpty(youtubeId)) return false;
        var owner = ownerId ?? CurrentOwnerId;
        string? matchedTargetId = null;

        lock (_outboxLock)
        {
            foreach (var kvp in _outboxByKey)
            {
                if (string.Equals(kvp.Key.OwnerId, owner, StringComparison.Ordinal) &&
                    kvp.Value.PendingDeletePlaylist &&
                    string.Equals(kvp.Value.YoutubePlaylistId, youtubeId, StringComparison.Ordinal))
                {
                    matchedTargetId = kvp.Key.TargetId;
                    break;
                }
            }

            if (matchedTargetId != null)
                _outboxByKey.Remove((owner, matchedTargetId));
        }

        if (matchedTargetId != null)
        {
            Log.Info($"[PlaylistOutbox] Cancelled pending cloud delete for re-imported playlist YT ID '{youtubeId}'");
            PersistOrDeleteAsyncFireAndForget(owner, matchedTargetId, null, shouldDelete: true);
            _notifications.CompleteTaskNotification(GetTaskKey(matchedTargetId), removeIfAnnihilated: true);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Forces an immediate flush of all accumulated mutations for a specific playlist or likes.
    /// </summary>
    public async Task FlushPlaylistAsync(string playlistId, CancellationToken ct = default)
    {
        if (_disposed || !_auth.IsAuthenticated || string.IsNullOrEmpty(playlistId))
            return;

        await EnsureOutboxHydratedAsync(ct).ConfigureAwait(false);

        await _flushLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ownerId = CurrentOwnerId;
            CloudPlaylistOutboxState? snapshot;
            lock (_outboxLock)
            {
                if (!_outboxByKey.TryGetValue((ownerId, playlistId), out var state) || state.IsEmpty)
                    return;
                snapshot = CloneStateUnderLock(state);
            }

            await ProcessTargetStateAsync(snapshot, ct).ConfigureAwait(false);
        }
        finally
        {
            _flushLock.Release();
        }
    }

    /// <summary>
    /// Forces an immediate flush of all accumulated like mutations.
    /// </summary>
    public async Task FlushLikesAsync(CancellationToken ct = default) =>
        await FlushPlaylistAsync(LikesOutboxTargetId, ct).ConfigureAwait(false);

    /// <summary>
    /// Forces a flush of all pending cloud mutations for the current user.
    /// </summary>
    public async Task FlushAllAsync(CancellationToken ct = default)
    {
        if (_disposed || !_auth.IsAuthenticated)
            return;

        await EnsureOutboxHydratedAsync(ct).ConfigureAwait(false);

        await _flushLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ownerId = CurrentOwnerId;
            var snapshots = new List<CloudPlaylistOutboxState>();

            lock (_outboxLock)
            {
                foreach (var kvp in _outboxByKey)
                {
                    if (string.Equals(kvp.Key.OwnerId, ownerId, StringComparison.Ordinal) && !kvp.Value.IsEmpty)
                        snapshots.Add(CloneStateUnderLock(kvp.Value));
                }
            }

            for (int i = 0; i < snapshots.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                await ProcessTargetStateAsync(snapshots[i], ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }

    #endregion

    #region Outbox Background Worker & Persistence

    private void HandleNetworkOrAuthRestored()
    {
        if (_disposed) return;

        lock (_outboxLock)
        {
            foreach (var state in _outboxByKey.Values)
            {
                state.RetryCount = 0;
            }

            // Network/auth change unparks AuthRequired: queue is dispatched immediately
            _lastFailureKind.Clear();
        }

        SignalWorker();
    }

    /// <summary>
    /// Applies batch window configuration change: recalculates task card timers
    /// and wakes up worker so the new window takes effect immediately instead of after current wait.
    /// </summary>
    public void OnBatchIntervalChanged()
    {
        if (_disposed) return;

        var ownerId = CurrentOwnerId;
        List<CloudPlaylistOutboxState>? pending = null;
        lock (_outboxLock)
        {
            foreach (var kvp in _outboxByKey)
            {
                if (kvp.Value.IsEmpty ||
                    !string.Equals(kvp.Key.OwnerId, ownerId, StringComparison.Ordinal) ||
                    IsTargetInFlightUnderLock(kvp.Key.OwnerId, kvp.Key.TargetId))
                {
                    continue;
                }

                (pending ??= []).Add(CloneStateUnderLock(kvp.Value));
            }
        }

        if (pending != null)
        {
            // Card deadline is locked on enqueuing: without recalculation the timer would display the old window
            for (int i = 0; i < pending.Count; i++)
                SyncTaskNotificationForState(pending[i], isSendingNow: false);
        }

        SignalWorker();
    }

    private void SignalWorker()
    {
        if (_disposed) return;
        _wakeUpTcs.TrySetResult();
    }

    private async Task OutboxWorkerLoopAsync()
    {
        var ct = _disposeCts.Token;

        // Let the application smoothly finish startup initialization before reading queue from SQLite
        if (!await ct.DelayNoThrowAsync(1200, continueOnCapturedContext: false).ConfigureAwait(false))
            return;

        // Single registration for the entire worker lifetime (avoids leaking CancellationTokenRegistration per iteration)
        var cancelTask = ct.WhenCanceledAsync();
        int consecutiveFaults = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await EnsureOutboxHydratedAsync(ct).ConfigureAwait(false);

                if (!_auth.IsAuthenticated)
                {
                    await WaitForWakeUpOrDelayAsync(5000, ct).ConfigureAwait(false);
                    continue;
                }

                if (VideoController.IsInCooldown)
                {
                    var cooldown = VideoController.GetRemainingCooldown();
                    if (cooldown > TimeSpan.Zero)
                    {
                        await WaitForWakeUpOrDelayAsync((int)Math.Min(cooldown.TotalMilliseconds + 250, 60_000), ct)
                            .ConfigureAwait(false);
                        continue;
                    }
                }

                long nowTicks = DateTime.UtcNow.Ticks;
                long batchWindowTicks = TimeSpan.FromMilliseconds(Math.Max(50, DebounceWindowMs)).Ticks;
                long quietGuardTicks = TimeSpan.FromMilliseconds(Math.Min(QuietGuardWindowMs, Math.Max(50, DebounceWindowMs))).Ticks;
                CloudPlaylistOutboxState? readySnapshot = null;
                int minWaitMs = 5000;
                bool hasAnyForOwner = false;
                var ownerId = CurrentOwnerId;

                lock (_outboxLock)
                {
                    foreach (var kvp in _outboxByKey)
                    {
                        if (!string.Equals(kvp.Key.OwnerId, ownerId, StringComparison.Ordinal))
                            continue;

                        var state = kvp.Value;
                        if (state.IsEmpty)
                            continue;

                        hasAnyForOwner = true;

                        long deadlineTicks;
                        if (state.RetryCount > 0)
                        {
                            int backoffMs = _lastFailureKind.TryGetValue(kvp.Key, out var failureKind) &&
                                            failureKind == CloudOutboxFailureKind.AuthRequired
                                ? AuthParkBackoffMs
                                : CloudOutboxCoalescer.ComputeRetryBackoffMs(state.RetryCount, BaseRetryBackoffMs, MaxRetryBackoffMs);
                            deadlineTicks = state.UpdatedAtUtcTicks + TimeSpan.FromMilliseconds(backoffMs).Ticks;
                        }
                        else
                        {
                            long firstTicks = state.FirstEnqueuedAtUtcTicks > 0
                                ? state.FirstEnqueuedAtUtcTicks
                                : state.UpdatedAtUtcTicks;
                            deadlineTicks = Math.Max(firstTicks + batchWindowTicks, state.UpdatedAtUtcTicks + quietGuardTicks);
                        }

                        if (nowTicks >= deadlineTicks)
                        {
                            readySnapshot = CloneStateUnderLock(state);
                            break;
                        }

                        int remainingMs = (int)Math.Min(int.MaxValue, TimeSpan.FromTicks(deadlineTicks - nowTicks).TotalMilliseconds);
                        if (remainingMs < minWaitMs)
                            minWaitMs = Math.Clamp(remainingMs, 100, 5000);
                    }
                }

                if (readySnapshot != null)
                {
                    await _flushLock.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await ProcessTargetStateAsync(readySnapshot, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        _flushLock.Release();
                    }

                    consecutiveFaults = 0;
                    continue;
                }

                if (!hasAnyForOwner)
                {
                    await Task.WhenAny(GetWakeUpTask(), cancelTask).ConfigureAwait(false);
                }
                else
                {
                    await WaitForWakeUpOrDelayAsync(minWaitMs, ct).ConfigureAwait(false);
                }

                consecutiveFaults = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveFaults++;
                int delayMs = CloudOutboxCoalescer.ComputeRetryBackoffMs(consecutiveFaults, 2_000, MaxRetryBackoffMs);
                Log.Warn($"[PlaylistOutbox] Worker loop exception #{consecutiveFaults} (next attempt in {delayMs}ms): {ex.Message}");
                if (!await ct.DelayNoThrowAsync(delayMs, continueOnCapturedContext: false).ConfigureAwait(false))
                    break;
            }
        }
    }

    private static string GetTaskKey(string targetId) => "outbox:" + targetId;

    private static string BuildOperationsSummary(CloudPlaylistOutboxState state)
    {
        if (string.Equals(state.TargetId, LikesOutboxTargetId, StringComparison.Ordinal))
        {
            return string.Format(SL["CloudTask_LikesSummary"], state.PendingLikes.Count);
        }

        if (state.PendingDeletePlaylist)
            return SL["CloudTask_DeletePlaylist"];

        var parts = new List<string>(6);
        if (state.PendingCreatePlaylist)
            parts.Add(SL["CloudTask_NewPlaylist"]);

        if (state.PendingAddTrackIds.Count > 0)
            parts.Add(SL.GetPlural("CloudTask_TracksAdded", state.PendingAddTrackIds.Count));

        if (state.PendingRemoveTracks.Count > 0)
            parts.Add(SL.GetPlural("CloudTask_TracksRemoved", state.PendingRemoveTracks.Count));

        if (state.HasPendingReorder)
            parts.Add(SL["CloudTask_Reorder"]);

        if (state.HasPendingName)
            parts.Add(SL["CloudTask_Rename"]);

        if (state.HasPendingDescription)
            parts.Add(SL["CloudTask_Description"]);

        if (state.HasPendingThumbnail)
            parts.Add(SL["CloudTask_Thumbnail"]);

        return parts.Count > 0 ? string.Join(", ", parts) : string.Empty;
    }

    private void SyncTaskNotificationForState(CloudPlaylistOutboxState snapshot, bool isSendingNow = false)
    {
        if (snapshot.IsEmpty)
        {
            _notifications.CompleteTaskNotification(GetTaskKey(snapshot.TargetId), removeIfAnnihilated: true);
            return;
        }

        bool isLikes = string.Equals(snapshot.TargetId, LikesOutboxTargetId, StringComparison.Ordinal);
        string? titleKey = isLikes ? "CloudTask_LikesTitle" : null;
        string? titleRaw = isLikes
            ? null
            : (!string.IsNullOrEmpty(snapshot.PlaylistDisplayName) ? snapshot.PlaylistDisplayName! : snapshot.TargetId);

        string? statusText = null;
        DateTime? deadlineUtc = null;
        if (isSendingNow)
        {
            statusText = SL["CloudTask_Sending"];
        }
        else if (snapshot.RetryCount > 0)
        {
            statusText = GetLastFailureKind(snapshot.OwnerId, snapshot.TargetId) switch
            {
                CloudOutboxFailureKind.AuthRequired => string.Format(SL["CloudTask_RetryingAuth"], snapshot.RetryCount),
                CloudOutboxFailureKind.Transient => string.Format(SL["CloudTask_Retrying"], snapshot.RetryCount),
                _ => string.Format(SL["CloudTask_RetryingError"], snapshot.RetryCount)
            };
        }
        else
        {
            // Same deadline as the worker: batch window from the first edit, but no earlier than the quiet window after the last
            int windowMs = Math.Max(50, DebounceWindowMs);
            long batchWindowTicks = TimeSpan.FromMilliseconds(windowMs).Ticks;
            long quietGuardTicks = TimeSpan.FromMilliseconds(Math.Min(QuietGuardWindowMs, windowMs)).Ticks;
            long firstTicks = snapshot.FirstEnqueuedAtUtcTicks > 0
                ? snapshot.FirstEnqueuedAtUtcTicks
                : snapshot.UpdatedAtUtcTicks;
            long deadlineTicks = Math.Max(firstTicks + batchWindowTicks, snapshot.UpdatedAtUtcTicks + quietGuardTicks);
            deadlineUtc = new DateTime(deadlineTicks, DateTimeKind.Utc);
        }

        var targetId = snapshot.TargetId;
        RelayCommand? forceCmd = null;
        if (!isSendingNow)
        {
            lock (_outboxLock)
            {
                if (!_forceCommandCache.TryGetValue(targetId, out forceCmd))
                {
                    forceCmd = new RelayCommand(() => _ = Task.Run(() => FlushPlaylistAsync(targetId, CancellationToken.None)));
                    _forceCommandCache[targetId] = forceCmd;
                }
            }
        }

        string? msgKey = isSendingNow ? "CloudTask_Sending" : (isLikes ? "CloudTask_LikesSummary" : null);
        object[]? msgArgs = !isSendingNow && isLikes ? [snapshot.PendingLikes.Count] : null;
        Func<string>? dynamicMsg = !isSendingNow && !isLikes
            ? () => string.Format(SL["CloudTask_Pending"], BuildOperationsSummary(snapshot))
            : null;

        _notifications.UpsertTaskNotification(
            taskKey: GetTaskKey(targetId),
            titleKey: titleKey,
            titleRaw: titleRaw,
            messageKey: msgKey,
            messageArgs: msgArgs,
            messageRaw: null,
            dynamicMessageFactory: dynamicMsg,
            statusText: statusText,
            progress: 0,
            isIndeterminate: false,
            actionKey: isSendingNow ? null : "CloudTask_ForceSend",
            actionCommand: forceCmd,
            isCancelAction: false,
            deadlineUtc: deadlineUtc,
            hideProgress: true);
    }

    private Task GetWakeUpTask()
    {
        lock (_outboxLock)
        {
            if (_wakeUpTcs.Task.IsCompleted)
                _wakeUpTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _wakeUpTcs.Task;
        }
    }

    private async Task WaitForWakeUpOrDelayAsync(int delayMs, CancellationToken ct)
    {
        var wakeTask = GetWakeUpTask();
        var delayTask = ct.DelayNoThrowAsync(delayMs, continueOnCapturedContext: false);
        await Task.WhenAny(wakeTask, delayTask).ConfigureAwait(false);
    }

    private async Task ProcessTargetStateAsync(CloudPlaylistOutboxState requested, CancellationToken ct)
    {
        if (!_auth.IsAuthenticated)
            return;

        // Snapshot is re-taken under the lock: a stale FlushAll snapshot could send mutations that were already annihilated
        var snapshot = BeginFlight(requested.OwnerId, requested.TargetId);
        if (snapshot == null)
            return;

        try
        {
            var sw = Stopwatch.StartNew();
            string opsSummary = BuildOperationsSummary(snapshot);
            SyncTaskNotificationForState(snapshot, isSendingNow: true);

            if (string.Equals(snapshot.TargetId, LikesOutboxTargetId, StringComparison.Ordinal))
            {
                await ProcessLikesOutboxAsync(snapshot, opsSummary, sw, ct).ConfigureAwait(false);
                return;
            }

            if (snapshot.PendingDeletePlaylist)
            {
                await ProcessDeletePlaylistAsync(snapshot, sw, ct).ConfigureAwait(false);
                return;
            }

            await ProcessPlaylistMutationsAsync(snapshot, opsSummary, sw, ct).ConfigureAwait(false);
        }
        finally
        {
            EndFlight(snapshot);
        }
    }

    private CloudPlaylistOutboxState? BeginFlight(string ownerId, string targetId)
    {
        lock (_outboxLock)
        {
            if (!_outboxByKey.TryGetValue((ownerId, targetId), out var live) || live.IsEmpty)
                return null;

            _inFlightOwnerId = ownerId;
            _inFlightTargetId = targetId;
            _inFlightLikeTrackId = null;
            _inFlightAddIds.Clear();
            _inFlightRemoveIds.Clear();

            for (int i = 0; i < live.PendingAddTrackIds.Count; i++)
                _inFlightAddIds.Add(live.PendingAddTrackIds[i]);
            for (int i = 0; i < live.PendingRemoveTracks.Count; i++)
                _inFlightRemoveIds.Add(live.PendingRemoveTracks[i].TrackId);

            return CloneStateUnderLock(live);
        }
    }

    private void EndFlight(CloudPlaylistOutboxState snapshot)
    {
        CloudPlaylistOutboxState? remaining = null;
        bool changed = false;
        bool removed = false;

        lock (_outboxLock)
        {
            var key = (snapshot.OwnerId, snapshot.TargetId);
            if (_outboxByKey.TryGetValue(key, out var live) &&
                CloudOutboxCoalescer.ResolveInFlightCompensations(live, _inFlightAddIds, _inFlightRemoveIds))
            {
                changed = true;
                if (live.IsEmpty)
                {
                    _outboxByKey.Remove(key);
                    removed = true;
                }
                else
                {
                    remaining = CloneStateUnderLock(live);
                }
            }

            _inFlightOwnerId = null;
            _inFlightTargetId = null;
            _inFlightLikeTrackId = null;
            _inFlightAddIds.Clear();
            _inFlightRemoveIds.Clear();
        }

        if (!changed)
            return;

        PersistOrDeleteAsyncFireAndForget(snapshot.OwnerId, snapshot.TargetId, remaining, removed);
        if (removed)
            _notifications.CompleteTaskNotification(GetTaskKey(snapshot.TargetId), removeIfAnnihilated: true);
        else if (remaining != null)
            SyncTaskNotificationForState(remaining);
    }

    private bool IsTargetInFlightUnderLock(string ownerId, string targetId) =>
        _inFlightTargetId != null &&
        string.Equals(_inFlightTargetId, targetId, StringComparison.Ordinal) &&
        string.Equals(_inFlightOwnerId, ownerId, StringComparison.Ordinal);

    private async Task ProcessDeletePlaylistAsync(CloudPlaylistOutboxState snapshot, Stopwatch sw, CancellationToken ct)
    {
        var youtubeId = snapshot.YoutubePlaylistId;
        if (!string.IsNullOrEmpty(youtubeId))
        {
            try
            {
                await _youtube.DeletePlaylistAsync(youtubeId).ConfigureAwait(false);
                Log.Info($"[PlaylistOutbox] Flushed DeletePlaylist for '{snapshot.PlaylistDisplayName ?? snapshot.TargetId}' (YT: {youtubeId}) in {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var kind = ClassifyFlushFailure(ex, ct, snapshot.RetryCount);
                if (kind == CloudOutboxFailureKind.NotFound)
                {
                    Log.Info($"[PlaylistOutbox] Cloud playlist '{youtubeId}' is already absent -> delete treated as completed");
                }
                else if (kind == CloudOutboxFailureKind.Forbidden)
                {
                    Log.Warn($"[PlaylistOutbox] Cloud delete of '{youtubeId}' is forbidden (not owner) -> dropped: {ex.Message}");
                }
                else
                {
                    if (kind == CloudOutboxFailureKind.Permanent)
                        MarkPermanentFailure(snapshot.OwnerId, snapshot.TargetId, ex);
                    else
                        MarkRetryNeeded(snapshot.OwnerId, snapshot.TargetId, ex.Message, kind);

                    return;
                }
            }

            RememberDeletedPlaylist(snapshot.OwnerId, youtubeId);
        }

        DiscardPlaylistOutbox(snapshot.TargetId, snapshot.OwnerId);
        _notifications.CompleteTaskNotification(
            GetTaskKey(snapshot.TargetId),
            dynamicMessageFactory: () => string.Format(SL["CloudTask_Completed"], BuildOperationsSummary(snapshot)),
            severity: NotificationSeverity.Success);
    }

    private async Task ProcessPlaylistMutationsAsync(
        CloudPlaylistOutboxState snapshot,
        string opsSummary,
        Stopwatch sw,
        CancellationToken ct)
    {
        Playlist? playlist = null;
        try
        {
            playlist = await _playlists.GetByIdAsync(snapshot.TargetId, snapshot.OwnerId, ct).ConfigureAwait(false);
            if (playlist == null || !playlist.IsFromAccount || !playlist.IsEditable)
            {
                DiscardPlaylistOutbox(snapshot.TargetId, snapshot.OwnerId);
                return;
            }

            List<string> addIds = snapshot.PendingAddTrackIds;
            bool isImplicitCreate = false;
            bool metadataSentWithCreate = false;
            List<string>? rejectedPhaseKeys = null;

            // ═══ PHASE 0: Offline-optimistic YouTube playlist creation ═══
            if (snapshot.PendingCreatePlaylist && !string.IsNullOrEmpty(playlist.YoutubeId))
            {
                // Creation is already committed in the DB (failure between YoutubeId write and outbox commit): retry would produce a duplicate
                CommitCompletedCreatePhase(snapshot.OwnerId, playlist.Id, playlist.YoutubeId!);
            }
            else if (snapshot.PendingCreatePlaylist || string.IsNullOrEmpty(playlist.YoutubeId))
            {
                string createdName = playlist.Name;
                string createdDescription = playlist.Description?.Trim() ?? string.Empty;
                var createdYtId = await _youtube.CreatePlaylistAsync(
                    createdName,
                    videoIds: null,
                    description: createdDescription,
                    ct).ConfigureAwait(false);

                if (string.IsNullOrEmpty(createdYtId))
                    throw new YoutubeExplodeException("YouTube returned empty playlistId on Phase 0 creation.");

                if (!await TryCommitCreatedPlaylistAsync(snapshot, playlist, createdYtId, ct).ConfigureAwait(false))
                    return;

                // Title and description are already in the cloud: Phase 4 of this pass skips them,
                // while edits made during creation remain queued for the next pass
                metadataSentWithCreate = true;
                CommitMetadataSentWithCreate(snapshot.OwnerId, playlist.Id, createdName, createdDescription);

                if (!snapshot.PendingCreatePlaylist)
                {
                    // Implicit creation (Create task lost): uploading the entire local composition instead of just the delta
                    isImplicitCreate = true;
                    addIds = await CollectImplicitCreateTrackIdsAsync(snapshot, playlist.Id, ct).ConfigureAwait(false);
                }
            }

            var youtubeId = playlist.YoutubeId!;

            // ═══ PHASE 1: Video deletion (ACTION_REMOVE_VIDEO) ═══
            if (snapshot.PendingRemoveTracks.Count > 0)
            {
                var setVideoIdsToRemove = await ResolveSetVideoIdsForRemovalAsync(
                    playlist, snapshot.PendingRemoveTracks, ct).ConfigureAwait(false);

                if (!playlist.CanSyncToCloud)
                {
                    DiscardPlaylistOutbox(playlist.Id, snapshot.OwnerId);
                    return;
                }

                // YouTube rejection after rebasing skips only this phase, not the entire task
                if (setVideoIdsToRemove.Count > 0 &&
                    !await TryRemoveTracksWithRebaseAsync(
                        playlist, setVideoIdsToRemove, snapshot.PendingRemoveTracks, ct).ConfigureAwait(false))
                {
                    (rejectedPhaseKeys ??= []).Add("CloudTask_RemoveTracks");
                }

                CommitCompletedRemovePhase(snapshot.OwnerId, playlist.Id, snapshot.PendingRemoveTracks);
            }

            // ═══ PHASE 2: Batch video addition (ACTION_ADD_VIDEO) ═══
            if (addIds.Count > 0)
            {
                var returnedSetVideoIds = await _youtube.MutationsClientAddTracksAsync(youtubeId, addIds, ct)
                    .ConfigureAwait(false);

                // Response is aligned by videoId; to_be_updated_by_client placeholders are not persisted to the DB
                var mappings = CloudOutboxCoalescer.MapAddedSetVideoIds(addIds, returnedSetVideoIds);
                if (mappings.Count > 0)
                {
                    await _playlists.UpdateSetVideoIdsAsync(playlist.Id, mappings, ct).ConfigureAwait(false);
                }

                CommitCompletedAddPhase(snapshot.OwnerId, playlist.Id, addIds, mappings);
            }

            // ═══ PHASE 3: Compact track reordering (LIS Move Compression) ═══
            if (snapshot.HasPendingReorder && snapshot.BaselineTrackOrder != null && snapshot.TargetTrackOrder != null)
            {
                // On implicit creation, composition is already uploaded in the up-to-date local order
                if (!isImplicitCreate &&
                    !await ExecuteCompressedReorderPhaseAsync(
                        playlist, snapshot.BaselineTrackOrder, snapshot.TargetTrackOrder, ct).ConfigureAwait(false))
                {
                    (rejectedPhaseKeys ??= []).Add("CloudTask_Reorder");
                }

                CommitCompletedReorderPhase(snapshot.OwnerId, playlist.Id, snapshot.TargetTrackOrder);
            }

            // ═══ PHASE 4: Metadata and Scotty cover (Last-Write-Wins) ═══
            bool sendName = !metadataSentWithCreate &&
                            snapshot.HasPendingName && !string.IsNullOrWhiteSpace(snapshot.TargetName);
            bool sendDescription = !metadataSentWithCreate &&
                                   snapshot.HasPendingDescription && snapshot.TargetDescription != null;
            if (sendName || sendDescription)
            {
                // Title and description are dispatched in a single edit_playlist with two actions
                await _youtube.EditPlaylistDetailsAsync(
                    youtubeId,
                    sendName ? snapshot.TargetName : null,
                    sendDescription ? snapshot.TargetDescription!.Trim() : null,
                    ct).ConfigureAwait(false);

                if (sendName)
                    CommitCompletedNamePhase(snapshot.OwnerId, playlist.Id, snapshot.TargetName!);
                if (sendDescription)
                    CommitCompletedDescriptionPhase(snapshot.OwnerId, playlist.Id, snapshot.TargetDescription!);
            }

            if (snapshot.HasPendingThumbnail && !string.IsNullOrWhiteSpace(snapshot.TargetThumbnailUrl))
            {
                await WaitForAudioBufferingQuietWindowAsync(ct).ConfigureAwait(false);

                bool uploaded = await UploadThumbnailToYoutubeAsync(youtubeId, snapshot.TargetThumbnailUrl!, ct)
                    .ConfigureAwait(false);
                if (uploaded)
                {
                    playlist.ETag = ThumbnailComparer.Normalize(snapshot.TargetThumbnailUrl);
                }

                CommitCompletedThumbnailPhase(snapshot.OwnerId, playlist.Id, snapshot.TargetThumbnailUrl!);
            }

            // Point update instead of a full Upsert: does not resurrect a deleted row or overwrite concurrent edits
            playlist.LastSyncedAtUtc = DateTime.UtcNow;
            playlist.IsCloudUnavailable = false;
            await _playlists.UpdateCloudLinkStateAsync(
                playlist.Id, youtubeId: null, playlist.LastSyncedAtUtc, isCloudUnavailable: false, ct).ConfigureAwait(false);

            sw.Stop();
            Log.Info($"[PlaylistOutbox] Flushed playlist '{playlist.Name}' ({youtubeId}) in {sw.ElapsedMilliseconds}ms: [{opsSummary}]");
            ClearPermanentStrikes(snapshot.OwnerId, playlist.Id);

            var remaining = GetPendingStateSnapshot(playlist.Id, snapshot.OwnerId);
            if (rejectedPhaseKeys != null)
            {
                var rejectedKeys = rejectedPhaseKeys;
                _notifications.CompleteTaskNotification(
                    GetTaskKey(playlist.Id),
                    dynamicMessageFactory: () =>
                        string.Format(SL["CloudTask_CompletedPartial"], JoinLocalized(rejectedKeys)),
                    severity: NotificationSeverity.Warning);

                if (remaining != null)
                    SyncTaskNotificationForState(remaining, isSendingNow: false);
            }
            else if (remaining == null)
            {
                _notifications.CompleteTaskNotification(
                    GetTaskKey(playlist.Id),
                    completedMessageKey: "CloudTask_Completed",
                    completedMessageArgs: [opsSummary],
                    dynamicMessageFactory: () => string.Format(SL["CloudTask_Completed"], BuildOperationsSummary(snapshot)),
                    severity: NotificationSeverity.Success);
            }
            else
            {
                SyncTaskNotificationForState(remaining, isSendingNow: false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await HandlePlaylistFlushFailureAsync(snapshot, playlist, ex, ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> TryCommitCreatedPlaylistAsync(
        CloudPlaylistOutboxState snapshot,
        Playlist playlist,
        string createdYtId,
        CancellationToken ct)
    {
        bool stateAlive = false;
        bool deleteRequested = false;
        lock (_outboxLock)
        {
            if (_outboxByKey.TryGetValue((snapshot.OwnerId, snapshot.TargetId), out var live))
            {
                stateAlive = true;
                deleteRequested = live.PendingDeletePlaylist;
            }
        }

        if (deleteRequested)
        {
            // Deletion queued during Phase 0: persisting YoutubeId, next worker pass will delete the cloud copy
            CommitCompletedCreatePhase(snapshot.OwnerId, snapshot.TargetId, createdYtId);
            SignalWorker();
            return false;
        }

        bool linked = stateAlive &&
                      await _playlists.UpdateCloudLinkStateAsync(
                          playlist.Id, createdYtId, lastSyncedAtUtc: null, isCloudUnavailable: false, ct).ConfigureAwait(false);

        if (!linked)
        {
            Log.Warn($"[PlaylistOutbox] Phase 0: playlist '{playlist.Id}' was deleted or unlinked during creation -> removing orphan cloud playlist {createdYtId}");
            DiscardPlaylistOutbox(snapshot.TargetId, snapshot.OwnerId);
            await TryDeleteOrphanCloudPlaylistAsync(snapshot.OwnerId, createdYtId).ConfigureAwait(false);
            return false;
        }

        playlist.YoutubeId = createdYtId;
        playlist.SyncMode = PlaylistSyncMode.TwoWaySync;
        playlist.Ownership = PlaylistOwnership.Mine;

        CommitCompletedCreatePhase(snapshot.OwnerId, playlist.Id, createdYtId);
        OnCloudPlaylistStateChanged?.Invoke(playlist);
        Log.Info($"[PlaylistOutbox] Phase 0 created cloud playlist '{playlist.Name}' -> YT ID: {createdYtId}");
        return true;
    }

    private async Task<List<string>> CollectImplicitCreateTrackIdsAsync(
        CloudPlaylistOutboxState snapshot,
        string playlistId,
        CancellationToken ct)
    {
        var localIds = await _playlists.GetTrackIdsAsync(playlistId, snapshot.OwnerId, ct).ConfigureAwait(false);
        var result = new List<string>(localIds.Count);
        for (int i = 0; i < localIds.Count; i++)
        {
            if (localIds[i].StartsWith("yt_", StringComparison.Ordinal))
                result.Add(localIds[i]);
        }

        lock (_outboxLock)
        {
            if (IsTargetInFlightUnderLock(snapshot.OwnerId, snapshot.TargetId))
                _inFlightAddIds.UnionWith(result);
        }

        return result;
    }

    private async Task TryDeleteOrphanCloudPlaylistAsync(string ownerId, string youtubeId)
    {
        try
        {
            await _youtube.DeletePlaylistAsync(youtubeId).ConfigureAwait(false);
            RememberDeletedPlaylist(ownerId, youtubeId);
        }
        catch (Exception ex)
        {
            Log.Warn($"[PlaylistOutbox] Failed to remove orphan cloud playlist '{youtubeId}': {ex.Message}");
        }
    }

    private async Task HandlePlaylistFlushFailureAsync(
        CloudPlaylistOutboxState snapshot,
        Playlist? playlist,
        Exception ex,
        CancellationToken ct)
    {
        var kind = ClassifyFlushFailure(ex, ct, snapshot.RetryCount);

        if (playlist != null && kind is CloudOutboxFailureKind.NotFound or CloudOutboxFailureKind.Forbidden)
        {
            try
            {
                DiscardPlaylistOutbox(playlist.Id, snapshot.OwnerId);
                if (kind == CloudOutboxFailureKind.NotFound)
                    await DowngradeDeadCloudPlaylistAsync(playlist, ct).ConfigureAwait(false);
                else
                    await DowngradeForeignPlaylistOnForbiddenAsync(playlist, ct).ConfigureAwait(false);

                OnCloudPlaylistStateChanged?.Invoke(playlist);
            }
            catch (Exception downgradeEx) when (downgradeEx is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Log.Warn($"[PlaylistOutbox] Downgrade after {kind} failed for '{playlist.Id}': {downgradeEx.Message}");
            }

            return;
        }

        if (kind == CloudOutboxFailureKind.Permanent)
        {
            Log.Error($"[PlaylistOutbox] Non-recoverable error for playlist '{snapshot.TargetId}': {ex.Message}");
            MarkPermanentFailure(snapshot.OwnerId, snapshot.TargetId, ex);
            return;
        }

        MarkRetryNeeded(snapshot.OwnerId, snapshot.TargetId, ex.Message, kind);
    }

    private async Task ProcessLikesOutboxAsync(
        CloudPlaylistOutboxState snapshot,
        string opsSummary,
        Stopwatch sw,
        CancellationToken ct)
    {
        _ = opsSummary;
        if (snapshot.PendingLikes.Count == 0)
            return;

        int flushedCount = 0;
        for (int i = 0; i < snapshot.PendingLikes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var trackId = snapshot.PendingLikes[i].TrackId;
            bool targetLiked;

            lock (_outboxLock)
            {
                // Sending the up-to-date live state target: user might have toggled like after the snapshot was taken
                if (!_outboxByKey.TryGetValue((snapshot.OwnerId, LikesOutboxTargetId), out var live) ||
                    !CloudOutboxCoalescer.TryGetLikeTarget(live, trackId, out targetLiked))
                {
                    continue;
                }

                _inFlightLikeTrackId = trackId;
            }

            try
            {
                await _youtube.LikeTrackAsync(trackId, targetLiked).ConfigureAwait(false);

                if (!_auth.IsAuthenticated)
                {
                    MarkRetryNeeded(snapshot.OwnerId, LikesOutboxTargetId, "Session lost during like flush", CloudOutboxFailureKind.AuthRequired);
                    return;
                }

                CommitCompletedLikeEntry(snapshot.OwnerId, trackId, targetLiked);
                flushedCount++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var kind = ClassifyFlushFailure(ex, ct, snapshot.RetryCount);
                if (kind is CloudOutboxFailureKind.Permanent or CloudOutboxFailureKind.Forbidden or CloudOutboxFailureKind.NotFound)
                {
                    Log.Warn($"[PlaylistOutbox] Dropping failed like mutation for '{trackId}' ({kind}): {ex.Message}");
                    CommitCompletedLikeEntry(snapshot.OwnerId, trackId, targetLiked, dropEntry: true);
                    continue;
                }

                MarkRetryNeeded(snapshot.OwnerId, LikesOutboxTargetId, ex.Message, kind);
                return;
            }
            finally
            {
                lock (_outboxLock)
                {
                    _inFlightLikeTrackId = null;
                }
            }
        }

        sw.Stop();
        Log.Info($"[PlaylistOutbox] Flushed {flushedCount} like mutation(s) in {sw.ElapsedMilliseconds}ms");

        var remaining = GetPendingStateSnapshot(LikesOutboxTargetId, snapshot.OwnerId);
        if (remaining == null)
        {
            _notifications.CompleteTaskNotification(
                GetTaskKey(LikesOutboxTargetId),
                completedMessageKey: "CloudTask_LikesCompleted",
                completedMessageArgs: [flushedCount],
                severity: NotificationSeverity.Success);
        }
        else
        {
            SyncTaskNotificationForState(remaining, isSendingNow: false);
        }
    }

    /// <summary>
    /// Removes tracks from the cloud; on YouTube rejection (stale <c>SetVideoId</c> after edits on another
    /// device), re-reads cloud composition and retries deletion once using current identifiers.
    /// </summary>
    /// <returns><see langword="false"/> if YouTube rejected the rebased deletion as well.</returns>
    private async Task<bool> TryRemoveTracksWithRebaseAsync(
        Playlist playlist,
        List<string> setVideoIds,
        List<CloudTrackRemoveEntry> removeEntries,
        CancellationToken ct)
    {
        var youtubeId = playlist.YoutubeId!;
        try
        {
            await _youtube.MutationsClientRemoveTracksAsync(youtubeId, setVideoIds, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (CloudOutboxCoalescer.IsRejectedEdit(ex) && _auth.IsAuthenticated)
        {
            Log.Warn($"[PlaylistOutbox] Cloud rejected removal of {setVideoIds.Count} track(s) in '{playlist.Name}' " +
                     $"({DescribeRejection(ex)}) -> rebasing on cloud state");
        }

        var cloudOrder = await RefreshCloudOrderAsync(playlist, ct).ConfigureAwait(false);
        var rebased = CloudOutboxCoalescer.SelectRemovalsStillInCloud(removeEntries, cloudOrder);
        if (rebased.Count == 0)
        {
            Log.Info($"[PlaylistOutbox] Tracks to remove are already absent in cloud for '{playlist.Name}' " +
                     "-> removal completed");
            return true;
        }

        var rebasedIds = new List<string>(rebased.Count);
        for (int i = 0; i < rebased.Count; i++)
        {
            if (rebased[i].SetVideoId is { } setVideoId)
                rebasedIds.Add(setVideoId);
        }

        try
        {
            await _youtube.MutationsClientRemoveTracksAsync(youtubeId, rebasedIds, ct).ConfigureAwait(false);
            Log.Info($"[PlaylistOutbox] Rebased removal succeeded for '{playlist.Name}': " +
                     $"{rebasedIds.Count} of {setVideoIds.Count} track(s)");
            return true;
        }
        catch (Exception ex) when (CloudOutboxCoalescer.IsRejectedEdit(ex) && _auth.IsAuthenticated)
        {
            Log.Warn($"[PlaylistOutbox] Cloud rejected rebased removal in '{playlist.Name}' " +
                     $"({DescribeRejection(ex)}) -> phase skipped");
            return false;
        }
    }

    /// <summary>
    /// Re-reads cloud playlist composition, updating local <c>SetVideoId</c> values based on first track occurrences.
    /// Data unavailability is treated as a transient failure to retry the task instead of dropping it.
    /// </summary>
    private async Task<List<(string TrackId, string SetVideoId)>> RefreshCloudOrderAsync(
        Playlist playlist,
        CancellationToken ct)
    {
        var fullData = await _youtube.GetFullPlaylistDataAsync(playlist.YoutubeId!, ct).ConfigureAwait(false)
            ?? throw new HttpRequestException("Cloud playlist state is unavailable for rebase.");

        var remoteTracks = fullData.Tracks;
        var cloudOrder = new List<(string TrackId, string SetVideoId)>(remoteTracks.Count);
        var mappings = new List<(string TrackId, string SetVideoId)>(remoteTracks.Count);
        var mappedTrackIds = new HashSet<string>(remoteTracks.Count, StringComparer.Ordinal);

        for (int i = 0; i < remoteTracks.Count; i++)
        {
            var remote = remoteTracks[i];
            if (!CloudOutboxCoalescer.IsUsableSetVideoId(remote.SetVideoId))
                continue;

            var trackId = "yt_" + remote.VideoId;
            cloudOrder.Add((trackId, remote.SetVideoId));
            if (mappedTrackIds.Add(trackId))
                mappings.Add((trackId, remote.SetVideoId));
        }

        if (mappings.Count > 0)
            await _playlists.UpdateSetVideoIdsAsync(playlist.Id, mappings, ct).ConfigureAwait(false);

        return cloudOrder;
    }

    private static string DescribeRejection(Exception ex)
    {
        if (ex is HttpRequestException { StatusCode: { } statusCode })
            return $"HTTP {(int)statusCode}";

        var message = ex.Message;
        return message.Length <= 80 ? message : string.Concat(message.AsSpan(0, 80), "…");
    }

    private static string JoinLocalized(List<string> keys)
    {
        var parts = new string[keys.Count];
        for (int i = 0; i < keys.Count; i++)
            parts[i] = SL[keys[i]];

        return string.Join(", ", parts);
    }

    private async Task<List<string>> ResolveSetVideoIdsForRemovalAsync(
        Playlist playlist,
        List<CloudTrackRemoveEntry> removeEntries,
        CancellationToken ct)
    {
        var result = new List<string>(removeEntries.Count);
        bool anyMissing = false;

        for (int i = 0; i < removeEntries.Count; i++)
        {
            var svId = removeEntries[i].SetVideoId;
            if (CloudOutboxCoalescer.IsUsableSetVideoId(svId))
            {
                result.Add(svId);
            }
            else
            {
                anyMissing = true;
            }
        }

        if (!anyMissing)
            return result;

        // Only if some tracks to be removed lacked a SetVideoId (or had a placeholder) prior to deletion,
        // perform exactly 1 cloud query to resolve all missing SetVideoId values at once.
        var fullData = await _youtube.GetFullPlaylistDataAsync(playlist.YoutubeId!, ct).ConfigureAwait(false);
        if (fullData is { IsEditable: false })
        {
            await DowngradeForeignPlaylistOnForbiddenAsync(playlist, ct).ConfigureAwait(false);
            return [];
        }

        if (fullData?.Tracks is { Count: > 0 } remoteTracks)
        {
            var mappings = new List<(string TrackId, string SetVideoId)>(remoteTracks.Count);
            var cloudLookup = new Dictionary<string, string>(remoteTracks.Count, StringComparer.Ordinal);

            for (int i = 0; i < remoteTracks.Count; i++)
            {
                var r = remoteTracks[i];
                var fullTrackId = "yt_" + r.VideoId;
                if (CloudOutboxCoalescer.IsUsableSetVideoId(r.SetVideoId))
                {
                    mappings.Add((fullTrackId, r.SetVideoId));
                    cloudLookup.TryAdd(fullTrackId, r.SetVideoId);
                }
            }

            if (mappings.Count > 0)
                await _playlists.UpdateSetVideoIdsAsync(playlist.Id, mappings, ct).ConfigureAwait(false);

            for (int i = 0; i < removeEntries.Count; i++)
            {
                var entry = removeEntries[i];
                if (!CloudOutboxCoalescer.IsUsableSetVideoId(entry.SetVideoId) &&
                    cloudLookup.TryGetValue(entry.TrackId, out var resolvedSvId))
                {
                    result.Add(resolvedSvId);
                }
            }
        }

        return result;
    }

    /// <returns>
    /// <see langword="false"/> if YouTube rejected reordering on top of re-read cloud composition as well.
    /// </returns>
    private async Task<bool> ExecuteCompressedReorderPhaseAsync(
        Playlist playlist,
        List<string> baselineTrackOrder,
        List<string> targetTrackOrder,
        CancellationToken ct)
    {
        var tracksWithSetIds = await _playlists.GetTracksWithSetVideoIdsAsync(playlist.Id, ct).ConfigureAwait(false);
        if (tracksWithSetIds.Count <= 1)
            return true;

        var setVideoIdMap = new Dictionary<string, string>(tracksWithSetIds.Count, StringComparer.Ordinal);
        bool missingAnySetId = false;

        for (int i = 0; i < tracksWithSetIds.Count; i++)
        {
            var (tId, svId) = tracksWithSetIds[i];
            if (!string.IsNullOrEmpty(svId) &&
                !svId.Equals("to_be_updated_by_client", StringComparison.OrdinalIgnoreCase))
            {
                setVideoIdMap[tId] = svId;
            }
            else
            {
                missingAnySetId = true;
            }
        }

        if (missingAnySetId)
        {
            var fullData = await _youtube.GetFullPlaylistDataAsync(playlist.YoutubeId!, ct).ConfigureAwait(false);
            if (fullData?.Tracks is { Count: > 0 } remoteTracks)
            {
                var mappings = new List<(string TrackId, string SetVideoId)>(remoteTracks.Count);
                for (int i = 0; i < remoteTracks.Count; i++)
                {
                    var r = remoteTracks[i];
                    if (!string.IsNullOrEmpty(r.SetVideoId))
                    {
                        var fullTrackId = "yt_" + r.VideoId;
                        setVideoIdMap[fullTrackId] = r.SetVideoId;
                        mappings.Add((fullTrackId, r.SetVideoId));
                    }
                }

                if (mappings.Count > 0)
                    await _playlists.UpdateSetVideoIdsAsync(playlist.Id, mappings, ct).ConfigureAwait(false);
            }
        }

        // Construct current cloud order (baseline + new added tracks appended) and target SetVideoId order
        var currentCloudSetIds = new List<string>(tracksWithSetIds.Count);
        var seenInBaseline = new HashSet<string>(tracksWithSetIds.Count, StringComparer.Ordinal);

        for (int i = 0; i < baselineTrackOrder.Count; i++)
        {
            var tId = baselineTrackOrder[i];
            if (setVideoIdMap.TryGetValue(tId, out var svId) && seenInBaseline.Add(tId))
                currentCloudSetIds.Add(svId);
        }

        for (int i = 0; i < targetTrackOrder.Count; i++)
        {
            var tId = targetTrackOrder[i];
            if (setVideoIdMap.TryGetValue(tId, out var svId) && seenInBaseline.Add(tId))
                currentCloudSetIds.Add(svId);
        }

        var desiredSetIds = new List<string>(targetTrackOrder.Count);
        var seenInTarget = new HashSet<string>(targetTrackOrder.Count, StringComparer.Ordinal);
        for (int i = 0; i < targetTrackOrder.Count; i++)
        {
            var tId = targetTrackOrder[i];
            if (setVideoIdMap.TryGetValue(tId, out var svId) && seenInTarget.Add(tId))
                desiredSetIds.Add(svId);
        }

        var moves = BuildMinimalReorderMoves(currentCloudSetIds, desiredSetIds);
        if (moves.Count == 0)
            return true;

        try
        {
            await _youtube.MoveTracksInPlaylistAsync(playlist.YoutubeId!, moves, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (CloudOutboxCoalescer.IsRejectedEdit(ex) && _auth.IsAuthenticated)
        {
            Log.Warn($"[PlaylistOutbox] Cloud rejected reorder of '{playlist.Name}' " +
                     $"({DescribeRejection(ex)}) -> rebasing on cloud state");
        }

        // Cloud order diverged from the local database (edits on another device):
        // building moves based on the actual composition
        var cloudOrder = await RefreshCloudOrderAsync(playlist, ct).ConfigureAwait(false);
        var (rebasedCurrent, rebasedDesired) = CloudOutboxCoalescer.BuildRebasedReorder(cloudOrder, targetTrackOrder);
        var rebasedMoves = BuildMinimalReorderMoves(rebasedCurrent, rebasedDesired);
        if (rebasedMoves.Count == 0)
        {
            Log.Info($"[PlaylistOutbox] Cloud order of '{playlist.Name}' already matches target after rebase");
            return true;
        }

        try
        {
            await _youtube.MoveTracksInPlaylistAsync(playlist.YoutubeId!, rebasedMoves, ct).ConfigureAwait(false);
            Log.Info($"[PlaylistOutbox] Rebased reorder succeeded for '{playlist.Name}': {rebasedMoves.Count} move(s)");
            return true;
        }
        catch (Exception ex) when (CloudOutboxCoalescer.IsRejectedEdit(ex) && _auth.IsAuthenticated)
        {
            Log.Warn($"[PlaylistOutbox] Cloud rejected rebased reorder of '{playlist.Name}' " +
                     $"({DescribeRejection(ex)}) -> phase skipped");
            return false;
        }
    }

    /// <summary>
    /// Computes the minimal sequence of move commands <c>(SetVideoId, Predecessor, Successor)</c>
    /// to transform <paramref name="currentOrder"/> into <paramref name="desiredOrder"/> based on the
    /// Longest Increasing Subsequence (LIS).
    /// Elements within the LIS remain stationary, minimizing HTTP batch size.
    /// </summary>
    public static List<(string SetVideoId, string? Predecessor, string? Successor)> BuildMinimalReorderMoves(
        IReadOnlyList<string> currentOrder,
        IReadOnlyList<string> desiredOrder)
    {
        var moves = new List<(string SetVideoId, string? Predecessor, string? Successor)>();
        if (currentOrder.Count <= 1 || desiredOrder.Count <= 1)
            return moves;

        var simulated = new List<string>(currentOrder.Count);
        var desiredIndexMap = new Dictionary<string, int>(desiredOrder.Count, StringComparer.Ordinal);
        for (int i = 0; i < desiredOrder.Count; i++)
            desiredIndexMap[desiredOrder[i]] = i;

        for (int i = 0; i < currentOrder.Count; i++)
        {
            if (desiredIndexMap.ContainsKey(currentOrder[i]))
                simulated.Add(currentOrder[i]);
        }

        int n = simulated.Count;
        if (n <= 1 || SequenceEqualOrdinal(simulated, desiredOrder))
            return moves;

        // Find LIS based on target element indices in simulated
        var tails = new int[n];
        var prev = new int[n];
        int lisLen = 0;

        for (int i = 0; i < n; i++)
        {
            int targetPos = desiredIndexMap[simulated[i]];
            int lo = 0, hi = lisLen;
            while (lo < hi)
            {
                int mid = (lo + hi) >>> 1;
                if (desiredIndexMap[simulated[tails[mid]]] < targetPos)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            tails[lo] = i;
            prev[i] = lo > 0 ? tails[lo - 1] : -1;
            if (lo == lisLen)
                lisLen++;
        }

        var stationarySet = new HashSet<string>(lisLen, StringComparer.Ordinal);
        int curr = tails[lisLen - 1];
        while (curr >= 0)
        {
            stationarySet.Add(simulated[curr]);
            curr = prev[curr];
        }

        // Move only elements not in the LIS, preserving their sequence in desiredOrder
        for (int targetIdx = 0; targetIdx < desiredOrder.Count; targetIdx++)
        {
            var sid = desiredOrder[targetIdx];
            if (stationarySet.Contains(sid))
                continue;

            int currentIdx = IndexOfOrdinal(simulated, sid);
            if (currentIdx < 0)
                continue;

            simulated.RemoveAt(currentIdx);

            // Find insertion position relative to the already built prefix desiredOrder[0..targetIdx-1]
            int insertIdx = 0;
            if (targetIdx > 0)
            {
                var prevDesiredSid = desiredOrder[targetIdx - 1];
                int prevIdxInSim = IndexOfOrdinal(simulated, prevDesiredSid);
                insertIdx = prevIdxInSim >= 0 ? prevIdxInSim + 1 : simulated.Count;
            }

            simulated.Insert(insertIdx, sid);

            if (insertIdx == 0)
            {
                var successor = simulated.Count > 1 ? simulated[1] : null;
                if (!string.IsNullOrEmpty(successor))
                    moves.Add((sid, null, successor));
            }
            else
            {
                var predecessor = simulated[insertIdx - 1];
                moves.Add((sid, predecessor, null));
            }

            stationarySet.Add(sid);
        }

        return moves;
    }

    private async Task WaitForAudioBufferingQuietWindowAsync(CancellationToken ct)
    {
        var check = IsAudioBufferingCheck;
        if (check == null) return;

        int waitedMs = 0;
        while (waitedMs < ScottyAudioBufferingMaxWaitMs && !ct.IsCancellationRequested)
        {
            bool isBuffering = false;
            try { isBuffering = check(); } catch { }

            if (!isBuffering)
                return;

            if (!await ct.DelayNoThrowAsync(ScottyAudioBufferingWaitStepMs, continueOnCapturedContext: false)
                    .ConfigureAwait(false))
                return;

            waitedMs += ScottyAudioBufferingWaitStepMs;
        }
    }

    private CloudOutboxFailureKind ClassifyFlushFailure(Exception ex, CancellationToken ct, int retryCount)
    {
        var kind = CloudOutboxCoalescer.ClassifyFailure(ex, ct);

        // 400/403 with an expired session indicates lost authorization, not a corrupted mutation
        if ((kind is CloudOutboxFailureKind.Permanent or CloudOutboxFailureKind.Forbidden) && !_auth.IsAuthenticated)
            return CloudOutboxFailureKind.AuthRequired;

        // Single 403 can result from anti-bot protection: playlist downgrade only after re-confirmation
        if (kind == CloudOutboxFailureKind.Forbidden && retryCount < ForbiddenConfirmationAttempts)
            return CloudOutboxFailureKind.Transient;

        return kind;
    }

    private CloudOutboxFailureKind GetLastFailureKind(string ownerId, string targetId)
    {
        lock (_outboxLock)
        {
            return _lastFailureKind.TryGetValue((ownerId, targetId), out var kind)
                ? kind
                : CloudOutboxFailureKind.Transient;
        }
    }

    private void RememberDeletedPlaylist(string ownerId, string youtubeId)
    {
        long nowTicks = DateTime.UtcNow.Ticks;
        lock (_outboxLock)
        {
            PruneExpiredTombstonesUnderLock(nowTicks);
            _deleteTombstones[(ownerId, youtubeId)] = nowTicks + RecentFlushTombstoneTtlMs * TimeSpan.TicksPerMillisecond;
        }
    }

    private void PruneExpiredTombstonesUnderLock(long nowTicks)
    {
        if (_likeTombstones.Count > 0)
        {
            foreach (var kvp in _likeTombstones)
            {
                if (kvp.Value.ExpiresAtTicks <= nowTicks)
                    _likeTombstones.Remove(kvp.Key);
            }
        }

        if (_deleteTombstones.Count > 0)
        {
            foreach (var kvp in _deleteTombstones)
            {
                if (kvp.Value <= nowTicks)
                    _deleteTombstones.Remove(kvp.Key);
            }
        }
    }

    private CloudPlaylistOutboxState? GetPersistSnapshot(string ownerId, string targetId)
    {
        lock (_outboxLock)
        {
            return _outboxByKey.TryGetValue((ownerId, targetId), out var state) && !state.IsEmpty
                ? CloneStateUnderLock(state)
                : null;
        }
    }

    private void CommitCompletedRemovePhase(
        string ownerId,
        string playlistId,
        List<CloudTrackRemoveEntry> completedRemoves)
    {
        CloudPlaylistOutboxState? snapshot = null;
        bool shouldDelete = false;

        lock (_outboxLock)
        {
            if (!_outboxByKey.TryGetValue((ownerId, playlistId), out var state))
                return;

            for (int i = 0; i < completedRemoves.Count; i++)
            {
                var trackId = completedRemoves[i].TrackId;
                for (int r = state.PendingRemoveTracks.Count - 1; r >= 0; r--)
                {
                    if (string.Equals(state.PendingRemoveTracks[r].TrackId, trackId, StringComparison.Ordinal))
                        state.PendingRemoveTracks.RemoveAt(r);
                }
            }

            state.RetryCount = 0;
            if (state.IsEmpty)
            {
                _outboxByKey.Remove((ownerId, playlistId));
                shouldDelete = true;
            }
            else
            {
                snapshot = CloneStateUnderLock(state);
            }
        }

        PersistOrDeleteAsyncFireAndForget(ownerId, playlistId, snapshot, shouldDelete);
    }

    private void CommitCompletedAddPhase(
        string ownerId,
        string playlistId,
        IReadOnlyList<string> completedAdds,
        IReadOnlyList<(string TrackId, string SetVideoId)> mappings)
    {
        CloudPlaylistOutboxState? snapshot = null;
        bool shouldDelete = false;

        lock (_outboxLock)
        {
            if (!_outboxByKey.TryGetValue((ownerId, playlistId), out var state))
                return;

            // Deletions queued in-flight receive real SetVideoId values of newly added tracks
            CloudOutboxCoalescer.CommitAdds(state, completedAdds, mappings);

            state.RetryCount = 0;
            if (state.IsEmpty)
            {
                _outboxByKey.Remove((ownerId, playlistId));
                _lastFailureKind.Remove((ownerId, playlistId));
                shouldDelete = true;
            }
            else
            {
                snapshot = CloneStateUnderLock(state);
            }
        }

        PersistOrDeleteAsyncFireAndForget(ownerId, playlistId, snapshot, shouldDelete);
    }

    private void CommitCompletedReorderPhase(
        string ownerId,
        string playlistId,
        List<string> syncedOrder)
    {
        CloudPlaylistOutboxState? snapshot = null;
        bool shouldDelete = false;

        lock (_outboxLock)
        {
            if (!_outboxByKey.TryGetValue((ownerId, playlistId), out var state))
                return;

            if (state.TargetTrackOrder != null && SequenceEqualOrdinal(state.TargetTrackOrder, syncedOrder))
            {
                state.HasPendingReorder = false;
                state.BaselineTrackOrder = null;
                state.TargetTrackOrder = null;
            }
            else
            {
                state.BaselineTrackOrder = [.. syncedOrder];
            }

            state.RetryCount = 0;
            if (state.IsEmpty)
            {
                _outboxByKey.Remove((ownerId, playlistId));
                shouldDelete = true;
            }
            else
            {
                snapshot = CloneStateUnderLock(state);
            }
        }

        PersistOrDeleteAsyncFireAndForget(ownerId, playlistId, snapshot, shouldDelete);
    }

    private void CommitCompletedNamePhase(string ownerId, string playlistId, string syncedName)
    {
        CloudPlaylistOutboxState? snapshot = null;
        bool shouldDelete = false;

        lock (_outboxLock)
        {
            if (!_outboxByKey.TryGetValue((ownerId, playlistId), out var state))
                return;

            if (string.Equals(state.TargetName, syncedName, StringComparison.Ordinal))
            {
                state.HasPendingName = false;
                state.BaselineName = null;
                state.TargetName = null;
            }
            else
            {
                state.BaselineName = syncedName;
            }

            state.RetryCount = 0;
            if (state.IsEmpty)
            {
                _outboxByKey.Remove((ownerId, playlistId));
                shouldDelete = true;
            }
            else
            {
                snapshot = CloneStateUnderLock(state);
            }
        }

        PersistOrDeleteAsyncFireAndForget(ownerId, playlistId, snapshot, shouldDelete);
    }

    private void CommitCompletedDescriptionPhase(string ownerId, string playlistId, string syncedDescription)
    {
        CloudPlaylistOutboxState? snapshot = null;
        bool shouldDelete = false;

        lock (_outboxLock)
        {
            if (!_outboxByKey.TryGetValue((ownerId, playlistId), out var state))
                return;

            if (string.Equals(state.TargetDescription, syncedDescription, StringComparison.Ordinal))
            {
                state.HasPendingDescription = false;
                state.BaselineDescription = null;
                state.TargetDescription = null;
            }
            else
            {
                state.BaselineDescription = syncedDescription;
            }

            state.RetryCount = 0;
            if (state.IsEmpty)
            {
                _outboxByKey.Remove((ownerId, playlistId));
                shouldDelete = true;
            }
            else
            {
                snapshot = CloneStateUnderLock(state);
            }
        }

        PersistOrDeleteAsyncFireAndForget(ownerId, playlistId, snapshot, shouldDelete);
    }

    private void CommitCompletedThumbnailPhase(string ownerId, string playlistId, string syncedThumbUrl)
    {
        CloudPlaylistOutboxState? snapshot = null;
        bool shouldDelete = false;

        lock (_outboxLock)
        {
            if (!_outboxByKey.TryGetValue((ownerId, playlistId), out var state))
                return;

            if (string.Equals(state.TargetThumbnailUrl, syncedThumbUrl, StringComparison.Ordinal))
            {
                state.HasPendingThumbnail = false;
                state.TargetThumbnailUrl = null;
            }

            state.RetryCount = 0;
            if (state.IsEmpty)
            {
                _outboxByKey.Remove((ownerId, playlistId));
                shouldDelete = true;
            }
            else
            {
                snapshot = CloneStateUnderLock(state);
            }
        }

        PersistOrDeleteAsyncFireAndForget(ownerId, playlistId, snapshot, shouldDelete);
    }

    private void CommitCompletedLikeEntry(string ownerId, string trackId, bool syncedLiked, bool dropEntry = false)
    {
        CloudPlaylistOutboxState? snapshot = null;
        bool shouldDelete = false;

        lock (_outboxLock)
        {
            if (string.Equals(_inFlightLikeTrackId, trackId, StringComparison.Ordinal))
                _inFlightLikeTrackId = null;

            if (!dropEntry)
            {
                // YouTube like propagation lags behind writes: sync must not roll back a confirmed value
                long nowTicks = DateTime.UtcNow.Ticks;
                PruneExpiredTombstonesUnderLock(nowTicks);
                _likeTombstones[(ownerId, trackId)] = (syncedLiked, nowTicks + RecentFlushTombstoneTtlMs * TimeSpan.TicksPerMillisecond);
            }

            if (!_outboxByKey.TryGetValue((ownerId, LikesOutboxTargetId), out var state))
                return;

            if (dropEntry)
                CloudOutboxCoalescer.DropLike(state, trackId);
            else
                CloudOutboxCoalescer.CommitLike(state, trackId, syncedLiked);

            state.RetryCount = 0;
            if (state.IsEmpty)
            {
                _outboxByKey.Remove((ownerId, LikesOutboxTargetId));
                _lastFailureKind.Remove((ownerId, LikesOutboxTargetId));
                shouldDelete = true;
            }
            else
            {
                snapshot = CloneStateUnderLock(state);
            }
        }

        PersistOrDeleteAsyncFireAndForget(ownerId, LikesOutboxTargetId, snapshot, shouldDelete);
    }

    private static void TouchTimestampsUnderLock(CloudPlaylistOutboxState state)
    {
        long nowTicks = DateTime.UtcNow.Ticks;
        if (state.FirstEnqueuedAtUtcTicks <= 0)
            state.FirstEnqueuedAtUtcTicks = nowTicks;
        state.UpdatedAtUtcTicks = nowTicks;
        state.RetryCount = 0;
    }

    private void CommitMetadataSentWithCreate(
        string ownerId,
        string playlistId,
        string sentName,
        string sentDescription)
    {
        CloudPlaylistOutboxState? snapshot = null;
        bool shouldDelete = false;

        lock (_outboxLock)
        {
            if (!_outboxByKey.TryGetValue((ownerId, playlistId), out var state))
                return;

            CloudOutboxCoalescer.CommitMetadataSentWithCreate(state, sentName, sentDescription);
            if (state.IsEmpty)
            {
                _outboxByKey.Remove((ownerId, playlistId));
                shouldDelete = true;
            }
            else
            {
                snapshot = CloneStateUnderLock(state);
            }
        }

        PersistOrDeleteAsyncFireAndForget(ownerId, playlistId, snapshot, shouldDelete);
    }

    private void CommitCompletedCreatePhase(string ownerId, string playlistId, string createdYoutubeId)
    {
        CloudPlaylistOutboxState? snapshot = null;
        bool shouldDelete = false;

        lock (_outboxLock)
        {
            if (!_outboxByKey.TryGetValue((ownerId, playlistId), out var state))
                return;

            state.PendingCreatePlaylist = false;
            state.YoutubePlaylistId = createdYoutubeId;
            state.RetryCount = 0;

            if (state.IsEmpty)
            {
                _outboxByKey.Remove((ownerId, playlistId));
                shouldDelete = true;
            }
            else
            {
                snapshot = CloneStateUnderLock(state);
            }
        }

        PersistOrDeleteAsyncFireAndForget(ownerId, playlistId, snapshot, shouldDelete);
    }

    private void MarkRetryNeeded(
        string ownerId,
        string targetId,
        string reason,
        CloudOutboxFailureKind kind = CloudOutboxFailureKind.Transient,
        int maxRetries = int.MaxValue,
        Func<string>? dropMessageFactory = null)
    {
        CloudPlaylistOutboxState? snapshot = null;
        bool shouldDelete = false;

        lock (_outboxLock)
        {
            if (!_outboxByKey.TryGetValue((ownerId, targetId), out var state))
                return;

            state.RetryCount++;
            state.UpdatedAtUtcTicks = DateTime.UtcNow.Ticks;

            if (state.RetryCount > maxRetries)
            {
                Log.Warn($"[PlaylistOutbox] Dropping target '{targetId}' after {state.RetryCount} failed attempts ({kind}): {reason}");
                _outboxByKey.Remove((ownerId, targetId));
                _lastFailureKind.Remove((ownerId, targetId));
                _permanentStrikes.Remove((ownerId, targetId));
                shouldDelete = true;
            }
            else
            {
                _lastFailureKind[(ownerId, targetId)] = kind;
                Log.Debug($"[PlaylistOutbox] Scheduled retry #{state.RetryCount} for '{targetId}' ({kind}): {reason}");
                snapshot = CloneStateUnderLock(state);
            }
        }

        PersistOrDeleteAsyncFireAndForget(ownerId, targetId, snapshot, shouldDelete);
        if (shouldDelete)
        {
            _notifications.CompleteTaskNotification(
                GetTaskKey(targetId),
                completedMessageRaw: dropMessageFactory == null ? reason : null,
                dynamicMessageFactory: dropMessageFactory,
                severity: NotificationSeverity.Error);
        }
        else if (snapshot != null)
        {
            SyncTaskNotificationForState(snapshot, isSendingNow: false);
        }
    }

    /// <summary>
    /// Handles unrecoverable YouTube failure: the first failure grants one retry, the second consecutive failure discards the task
    /// with a user-friendly notification instead of raw response text.
    /// </summary>
    private void MarkPermanentFailure(string ownerId, string targetId, Exception ex)
    {
        int strikes;
        lock (_outboxLock)
        {
            strikes = _permanentStrikes.TryGetValue((ownerId, targetId), out var previous) ? previous + 1 : 1;
            _permanentStrikes[(ownerId, targetId)] = strikes;
        }

        string rejection = DescribeRejection(ex);
        MarkRetryNeeded(
            ownerId,
            targetId,
            ex.Message,
            CloudOutboxFailureKind.Permanent,
            maxRetries: strikes >= PermanentFailureStrikeLimit ? 0 : int.MaxValue,
            dropMessageFactory: () => string.Format(SL["CloudTask_Rejected"], rejection));
    }

    private void ClearPermanentStrikes(string ownerId, string targetId)
    {
        lock (_outboxLock)
        {
            _permanentStrikes.Remove((ownerId, targetId));
        }
    }

    private CloudPlaylistOutboxState GetOrCreateStateUnderLock(
        string ownerId,
        string targetId,
        string? youtubePlaylistId,
        string? displayName = null)
    {
        if (!_outboxByKey.TryGetValue((ownerId, targetId), out var state))
        {
            long nowTicks = DateTime.UtcNow.Ticks;
            state = new CloudPlaylistOutboxState
            {
                TargetId = targetId,
                OwnerId = ownerId,
                PlaylistDisplayName = displayName,
                YoutubePlaylistId = youtubePlaylistId,
                FirstEnqueuedAtUtcTicks = nowTicks,
                UpdatedAtUtcTicks = nowTicks
            };
            _outboxByKey[(ownerId, targetId)] = state;
        }
        else
        {
            if (!string.IsNullOrEmpty(youtubePlaylistId))
                state.YoutubePlaylistId = youtubePlaylistId;
            if (!string.IsNullOrEmpty(displayName))
                state.PlaylistDisplayName = displayName;
        }

        return state;
    }

    private static CloudPlaylistOutboxState CloneStateUnderLock(CloudPlaylistOutboxState src)
    {
        var clone = new CloudPlaylistOutboxState
        {
            TargetId = src.TargetId,
            OwnerId = src.OwnerId,
            PlaylistDisplayName = src.PlaylistDisplayName,
            YoutubePlaylistId = src.YoutubePlaylistId,
            PendingCreatePlaylist = src.PendingCreatePlaylist,
            PendingDeletePlaylist = src.PendingDeletePlaylist,
            PendingAddTrackIds = [.. src.PendingAddTrackIds],
            PendingRemoveTracks = new List<CloudTrackRemoveEntry>(src.PendingRemoveTracks.Count),
            HasPendingReorder = src.HasPendingReorder,
            BaselineTrackOrder = src.BaselineTrackOrder != null ? [.. src.BaselineTrackOrder] : null,
            TargetTrackOrder = src.TargetTrackOrder != null ? [.. src.TargetTrackOrder] : null,
            HasPendingName = src.HasPendingName,
            BaselineName = src.BaselineName,
            TargetName = src.TargetName,
            HasPendingDescription = src.HasPendingDescription,
            BaselineDescription = src.BaselineDescription,
            TargetDescription = src.TargetDescription,
            HasPendingThumbnail = src.HasPendingThumbnail,
            TargetThumbnailUrl = src.TargetThumbnailUrl,
            PendingLikes = new List<CloudLikeMutationEntry>(src.PendingLikes.Count),
            RetryCount = src.RetryCount,
            FirstEnqueuedAtUtcTicks = src.FirstEnqueuedAtUtcTicks,
            UpdatedAtUtcTicks = src.UpdatedAtUtcTicks
        };

        for (int i = 0; i < src.PendingRemoveTracks.Count; i++)
        {
            clone.PendingRemoveTracks.Add(new CloudTrackRemoveEntry
            {
                TrackId = src.PendingRemoveTracks[i].TrackId,
                SetVideoId = src.PendingRemoveTracks[i].SetVideoId
            });
        }

        for (int i = 0; i < src.PendingLikes.Count; i++)
        {
            clone.PendingLikes.Add(new CloudLikeMutationEntry
            {
                TrackId = src.PendingLikes[i].TrackId,
                BaselineLiked = src.PendingLikes[i].BaselineLiked,
                TargetLiked = src.PendingLikes[i].TargetLiked
            });
        }

        return clone;
    }

    private Task EnsureOutboxHydratedAsync(CancellationToken ct)
    {
        if (_isHydratedFromDb)
            return Task.CompletedTask;

        if (_store == null)
        {
            _isHydratedFromDb = true;
            return Task.CompletedTask;
        }

        // Single-flight: all callers await the same download instead of reading the table in parallel
        var hydrationTask = StartHydrationIfNeeded();
        return ct.CanBeCanceled ? hydrationTask.WaitAsync(ct) : hydrationTask;
    }

    private Task StartHydrationIfNeeded()
    {
        lock (_outboxLock)
        {
            return _hydrationTask ??= Task.Run(HydrateOutboxCoreAsync);
        }
    }

    private async Task HydrateOutboxCoreAsync()
    {
        List<CloudPlaylistOutboxState>? rows = null;
        for (int attempt = 1; attempt <= MaxHydrationAttempts && rows == null; attempt++)
        {
            try
            {
                rows = await _store!.LoadAllAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"[PlaylistOutbox] Outbox hydration attempt {attempt}/{MaxHydrationAttempts} failed: {ex.Message}");
                if (attempt < MaxHydrationAttempts)
                    await Task.Delay(500 * attempt).ConfigureAwait(false);
            }
        }

        if (rows == null)
        {
            // The pump remains paused: writing over unread rows would destroy mutations from the previous session
            _isHydratedFromDb = true;
            Log.Warn("[PlaylistOutbox] Outbox hydration failed; SQLite persistence is paused for this session.");
            return;
        }

        var restoredSnapshots = new List<CloudPlaylistOutboxState>(rows.Count);
        List<(string OwnerId, string TargetId)>? mergedKeys = null;
        List<string>? annihilatedTargets = null;
        List<(string PlaylistId, List<(string TrackId, string SetVideoId)> Mappings)>? setVideoRestores = null;

        lock (_outboxLock)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                var restored = rows[i];
                var key = (restored.OwnerId, restored.TargetId);

                if (!_outboxByKey.TryGetValue(key, out var live))
                {
                    _outboxByKey[key] = restored;
                    restoredSnapshots.Add(CloneStateUnderLock(restored));
                    continue;
                }

                // Mutations made before hydration finishes are replayed on top of the previous session row
                var merged = CloudOutboxCoalescer.MergeRestored(live, restored, out var restoredSetVideoIds);
                if (merged.IsEmpty)
                {
                    _outboxByKey.Remove(key);
                    (annihilatedTargets ??= []).Add(restored.TargetId);
                }
                else
                {
                    _outboxByKey[key] = merged;
                    restoredSnapshots.Add(CloneStateUnderLock(merged));
                }

                (mergedKeys ??= []).Add(key);
                if (restoredSetVideoIds is { Count: > 0 })
                    (setVideoRestores ??= []).Add((restored.TargetId, restoredSetVideoIds));
            }

            _isHydratedFromDb = true;
        }

        if (_persistencePump != null)
        {
            if (mergedKeys != null)
            {
                for (int i = 0; i < mergedKeys.Count; i++)
                    _persistencePump.MarkDirty(mergedKeys[i].OwnerId, mergedKeys[i].TargetId);
            }

            _persistencePump.Resume();
        }

        if (setVideoRestores != null)
        {
            for (int i = 0; i < setVideoRestores.Count; i++)
                await RestoreSetVideoIdsNoThrowAsync(setVideoRestores[i].PlaylistId, setVideoRestores[i].Mappings).ConfigureAwait(false);
        }

        if (annihilatedTargets != null)
        {
            for (int i = 0; i < annihilatedTargets.Count; i++)
                _notifications.CompleteTaskNotification(GetTaskKey(annihilatedTargets[i]), removeIfAnnihilated: true);
        }

        if (restoredSnapshots.Count > 0)
        {
            Log.Info($"[PlaylistOutbox] Restored {restoredSnapshots.Count} pending cloud mutation target(s) from SQLite.");
            for (int i = 0; i < restoredSnapshots.Count; i++)
                SyncTaskNotificationForState(restoredSnapshots[i]);
            SignalWorker();
        }
    }

    private void PersistOrDeleteAsyncFireAndForget(
        string ownerId,
        string targetId,
        CloudPlaylistOutboxState? snapshot,
        bool shouldDelete)
    {
        // The pump reads the current state under lock at write time; arguments are preserved for call compatibility
        _ = snapshot;
        _ = shouldDelete;
        if (_persistencePump == null)
            return;

        _persistencePump.MarkDirty(ownerId, targetId);
        if (!_isHydratedFromDb)
            _ = StartHydrationIfNeeded();
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

    private static bool SequenceEqualOrdinal(List<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    #endregion

    #region Legacy-Compatible Cloud Wrappers & Full Sync Operations

    /// <summary>
    /// Optimistically switches the playlist to two-way sync mode and queues Phase 0 creation into the Outbox (0 ms, non-blocking UI).
    /// </summary>
    public async Task<bool> LinkToCloudAsync(Playlist playlist, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(playlist);

        if (!_auth.IsAuthenticated) return false;

        try
        {
            playlist.SyncMode = PlaylistSyncMode.TwoWaySync;
            playlist.Ownership = PlaylistOwnership.Mine;
            playlist.UpdatedAt = DateTime.Now;
            await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);

            var trackIds = await _playlists.GetTrackIdsAsync(playlist.Id, CurrentOwnerId, ct).ConfigureAwait(false);
            EnqueueCreatePlaylist(playlist, trackIds);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistSync] LinkToCloud failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Creates a diff snapshot between the local playlist and the remote YouTube representation.
    /// Force-flushes pending Outbox mutations for this playlist beforehand.
    /// </summary>
    public async Task<PlaylistSyncPreview?> BuildPreviewAsync(Playlist playlist, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(playlist);

        if (string.IsNullOrEmpty(playlist.YoutubeId))
            return null;

        try
        {
            VideoController.ThrowIfInCooldown();

            // First, flush all pending local Outbox mutations for this playlist
            await FlushPlaylistAsync(playlist.Id, ct).ConfigureAwait(false);

            var refreshedPlaylist = await _playlists.GetByIdAsync(playlist.Id, CurrentOwnerId, ct).ConfigureAwait(false);
            if (refreshedPlaylist != null)
            {
                playlist.Ownership = refreshedPlaylist.Ownership;
                playlist.SyncMode = refreshedPlaylist.SyncMode;
                playlist.YoutubeId = refreshedPlaylist.YoutubeId;
                if (string.IsNullOrEmpty(playlist.YoutubeId))
                    return null;
            }

            var fullDataTask = _youtube.GetFullPlaylistDataAsync(playlist.YoutubeId, ct);
            var localTrackIdsTask = _playlists.GetTrackIdsAsync(playlist.Id, CurrentOwnerId, ct);
            var localLinksTask = _playlists.GetTracksWithSetVideoIdsAsync(playlist.Id, ct);

            await Task.WhenAll(fullDataTask, localTrackIdsTask, localLinksTask).ConfigureAwait(false);

            var fullData = await fullDataTask.ConfigureAwait(false);
            var localTrackIds = await localTrackIdsTask.ConfigureAwait(false);
            var localLinks = await localLinksTask.ConfigureAwait(false);

            if (fullData == null)
            {
                playlist.IsCloudUnavailable = true;
                await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);
                return null;
            }

            if (string.IsNullOrEmpty(fullData.Title) && fullData.Tracks.Count == 0)
            {
                Log.Warn(
                    $"[PlaylistSync] Playlist '{playlist.Name}' ({playlist.YoutubeId}) received empty metadata from cloud. Marking unavailable without downgrade.");
                playlist.IsCloudUnavailable = true;
                await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);
                return null;
            }

            bool healedMetadata = false;
            if (ShouldUpdateViewCount(playlist.ViewCount, fullData.ViewCount))
            {
                playlist.ViewCount = fullData.ViewCount;
                healedMetadata = true;
            }

            if (fullData.ReleaseDate.HasValue && playlist.ReleaseDate != fullData.ReleaseDate)
            {
                playlist.ReleaseDate = fullData.ReleaseDate;
                healedMetadata = true;
            }
            else if (!fullData.ReleaseDate.HasValue && playlist.ReleaseDate is { Month: 1, Day: 1 })
            {
                playlist.ReleaseDate = null;
                healedMetadata = true;
            }

            if (!string.IsNullOrEmpty(fullData.Author) && !string.Equals(playlist.Author, fullData.Author, StringComparison.Ordinal))
            {
                playlist.Author = fullData.Author;
                healedMetadata = true;
            }

            if (!string.IsNullOrEmpty(fullData.OwnerChannelId) && !string.Equals(playlist.OwnerChannelId, fullData.OwnerChannelId, StringComparison.Ordinal))
            {
                playlist.OwnerChannelId = fullData.OwnerChannelId;
                healedMetadata = true;
            }

            if (fullData.Visibility != PlaylistVisibility.Unknown && playlist.Visibility != fullData.Visibility)
            {
                playlist.Visibility = fullData.Visibility;
                healedMetadata = true;
            }

            var detectedOwnership = fullData.IsEditable ? PlaylistOwnership.Mine : PlaylistOwnership.Foreign;
            if (playlist.Ownership != PlaylistOwnership.System && playlist.Ownership != detectedOwnership)
            {
                Log.Info($"[PlaylistSync] Updating ownership for '{playlist.Name}' ({playlist.YoutubeId}): {playlist.Ownership} -> {detectedOwnership}");
                playlist.Ownership = detectedOwnership;
                healedMetadata = true;
            }

            if (playlist.Ownership == PlaylistOwnership.Foreign &&
                !string.IsNullOrEmpty(fullData.ThumbnailUrl) &&
                !ThumbnailComparer.IsYoutubeHosted(playlist.ThumbnailUrl))
            {
                playlist.ThumbnailUrl = fullData.ThumbnailUrl;
                playlist.ComputedColor = null;
                healedMetadata = true;
            }

            if (ThumbnailComparer.IsYoutubeHosted(playlist.ThumbnailUrl) &&
                !string.IsNullOrEmpty(fullData.ThumbnailUrl) &&
                !string.Equals(playlist.ThumbnailUrl, fullData.ThumbnailUrl, StringComparison.Ordinal))
            {
                playlist.ThumbnailUrl = fullData.ThumbnailUrl;
                healedMetadata = true;
            }

            bool localCoverAlreadySynced = IsLocalCoverAlreadySyncedWithCloud(playlist, fullData.ThumbnailUrl, out var normLocalThumb);
            if (localCoverAlreadySynced && !string.IsNullOrEmpty(normLocalThumb) &&
                !string.Equals(playlist.ETag, normLocalThumb, StringComparison.Ordinal))
            {
                playlist.ETag = normLocalThumb;
                healedMetadata = true;
            }

            var localIdSet = new HashSet<string>(localTrackIds.Count, StringComparer.Ordinal);
            for (int i = 0; i < localTrackIds.Count; i++)
            {
                var rawId = YoutubeIdHelper.ExtractRawId(localTrackIds[i]);
                if (!string.IsNullOrEmpty(rawId))
                    localIdSet.Add(rawId);
            }

            var cloudIdSet = new HashSet<string>(fullData.Tracks.Count, StringComparer.Ordinal);
            var cloudTrackIds = new HashSet<string>(fullData.Tracks.Count, StringComparer.Ordinal);
            int commonCount = 0;
            for (int i = 0; i < fullData.Tracks.Count; i++)
            {
                cloudTrackIds.Add("yt_" + fullData.Tracks[i].VideoId);
                var cloudRawId = YoutubeIdHelper.ExtractRawId(fullData.Tracks[i].VideoId);
                if (!string.IsNullOrEmpty(cloudRawId) && cloudIdSet.Add(cloudRawId))
                {
                    if (localIdSet.Contains(cloudRawId))
                        commonCount++;
                }
            }

            // Cloud-linked tracks deleted in the cloud on another device are not "local additions"
            int cloudRemovedCount =
                CloudOutboxCoalescer.SelectCloudDeletedTrackIds(localLinks, cloudTrackIds)?.Count ?? 0;
            int cloudOnlyCount = cloudIdSet.Count - commonCount;
            int localOnlyCount = Math.Max(0, localIdSet.Count - commonCount - cloudRemovedCount);

            var preview = new PlaylistSyncPreview
            {
                LocalName = playlist.Name,
                CloudName = fullData.Title ?? playlist.Name,
                LocalDescription = playlist.Description,
                CloudDescription = fullData.Description,
                LocalThumbnailUrl = localCoverAlreadySynced ? fullData.ThumbnailUrl : playlist.ThumbnailUrl,
                CloudThumbnailUrl = fullData.ThumbnailUrl,
                LocalOnlyTrackCount = localOnlyCount,
                CloudOnlyTrackCount = cloudOnlyCount,
                CommonTrackCount = commonCount,
                CloudRemovedTrackCount = cloudRemovedCount,
                YoutubePlaylistId = playlist.YoutubeId,
                CachedCloudData = fullData
            };

            if (!preview.HasAnyDifference)
            {
                playlist.LastSyncedAtUtc = DateTime.UtcNow;
                healedMetadata = true;
            }

            if (playlist.IsCloudUnavailable || healedMetadata)
            {
                playlist.IsCloudUnavailable = false;
                await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);
            }

            Log.Info($"[PlaylistSync] Preview built for '{playlist.Name}' ({playlist.YoutubeId}): " +
                     $"hasDiff={preview.HasAnyDifference} (name={preview.NameDiffers}, desc={preview.DescriptionDiffers}, " +
                     $"thumb={preview.ThumbnailDiffers}, +local={localOnlyCount}, +cloud={cloudOnlyCount}, -cloud={cloudRemovedCount}, common={commonCount}), " +
                     $"ownership={playlist.Ownership}, canSyncToCloud={playlist.CanSyncToCloud}");

            return preview;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Log.Warn(
                $"[PlaylistSync] Playlist '{playlist.Name}' ({playlist.YoutubeId}) returned 404 Not Found. Downgrading to LocalOnly...");
            DiscardPlaylistOutbox(playlist.Id);
            await DowngradeDeadCloudPlaylistAsync(playlist, ct).ConfigureAwait(false);
            return null;
        }
        catch (PlaylistUnavailableException)
        {
            Log.Warn(
                $"[PlaylistSync] Playlist '{playlist.Name}' ({playlist.YoutubeId}) is unavailable. Downgrading to LocalOnly...");
            DiscardPlaylistOutbox(playlist.Id);
            await DowngradeDeadCloudPlaylistAsync(playlist, ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistSync] Preview failed: {ex.Message}");
            if (ex is HttpRequestException)
            {
                playlist.IsCloudUnavailable = true;
                try
                {
                    await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Applies the selected synchronization parameters to the local and cloud playlists.
    /// </summary>
    public async Task<PlaylistSyncResult> ApplySyncAsync(
        Playlist playlist,
        PlaylistSyncPreview preview,
        PlaylistSyncOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            DiscardPlaylistOutbox(playlist.Id);

            bool metadataChanged = await SyncMetadataAsync(playlist, preview, options, ct).ConfigureAwait(false);
            int addedLocally = 0, addedToCloud = 0, removedLocally = 0, removedFromCloud = 0;

            var cloudData = preview.CachedCloudData
                            ?? await _youtube.GetFullPlaylistDataAsync(playlist.YoutubeId!, ct).ConfigureAwait(false);

            if (options.SyncTracks && cloudData != null)
            {
                (addedLocally, addedToCloud, removedLocally, removedFromCloud) = options.Strategy switch
                {
                    PlaylistSyncStrategy.ReplaceLocal => await ReplaceLocalTracksAsync(playlist, cloudData, ct)
                        .ConfigureAwait(false),
                    PlaylistSyncStrategy.ReplaceCloud => await ReplaceCloudTracksAsync(playlist, cloudData, ct)
                        .ConfigureAwait(false),
                    PlaylistSyncStrategy.Merge => await MergeTracksAsync(playlist, cloudData, ct).ConfigureAwait(false),
                    _ => (0, 0, 0, 0)
                };
            }

            if (cloudData != null)
            {
                if (ShouldUpdateViewCount(playlist.ViewCount, cloudData.ViewCount))
                    playlist.ViewCount = cloudData.ViewCount;

                if (!string.IsNullOrEmpty(cloudData.Author))
                    playlist.Author = cloudData.Author;

                if (!string.IsNullOrEmpty(cloudData.OwnerChannelId))
                    playlist.OwnerChannelId = cloudData.OwnerChannelId;

                if (cloudData.Visibility != PlaylistVisibility.Unknown)
                    playlist.Visibility = cloudData.Visibility;

                if (cloudData.ReleaseDate.HasValue)
                {
                    playlist.ReleaseDate = cloudData.ReleaseDate;
                }
                else if (playlist.ReleaseDate is { Month: 1, Day: 1 })
                {
                    playlist.ReleaseDate = null;
                }

                if (addedToCloud > 0 || removedFromCloud > 0 ||
                    (metadataChanged && options.Strategy != PlaylistSyncStrategy.ReplaceLocal))
                {
                    playlist.ReleaseDate = DateOnly.FromDateTime(DateTime.UtcNow);
                }

                if (string.IsNullOrEmpty(playlist.ThumbnailUrl) && !string.IsNullOrEmpty(cloudData.ThumbnailUrl))
                {
                    var normCloud = ThumbnailComparer.Normalize(cloudData.ThumbnailUrl);
                    if (!string.IsNullOrEmpty(normCloud))
                    {
                        playlist.ThumbnailUrl = cloudData.ThumbnailUrl;
                        playlist.ComputedColor = null;
                        metadataChanged = true;
                    }
                }
            }

            playlist.LastSyncedAtUtc = DateTime.UtcNow;
            playlist.IsCloudUnavailable = false;
            playlist.UpdatedAt = DateTime.Now;

            await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);

            var syncResult = new PlaylistSyncResult
            {
                Success = true,
                MetadataChanged = metadataChanged,
                TracksAddedLocally = addedLocally,
                TracksAddedToCloud = addedToCloud,
                TracksRemovedLocally = removedLocally,
                TracksRemovedFromCloud = removedFromCloud
            };

            Log.Info($"[PlaylistSync] Sync completed for '{playlist.Name}' ({playlist.YoutubeId}) [Strategy={options.Strategy}]: " +
                     $"{syncResult.Summary}, views={playlist.ViewCount?.ToString() ?? "null"}, " +
                     $"releaseDate={playlist.ReleaseDate?.ToString("yyyy-MM-dd") ?? "null"}");

            return syncResult;
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistSync] Apply failed: {ex.Message}");
            return PlaylistSyncResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Performs direct playlist synchronization without showing a preliminary UI dialog.
    /// </summary>
    public async Task<PlaylistSyncResult> SyncDirectAsync(
        Playlist playlist,
        PlaylistSyncOptions options,
        CancellationToken ct = default)
    {
        var preview = await BuildPreviewAsync(playlist, ct).ConfigureAwait(false);
        if (preview == null)
            return PlaylistSyncResult.Fail("Failed to generate preview from YouTube");

        return await ApplySyncAsync(playlist, preview, options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Synchronizes the current account's Liked songs with YouTube Music.
    /// Flushes all pending local like mutations from the Outbox beforehand.
    /// </summary>
    public async Task SyncLikedTracksAsync(CancellationToken ct = default)
    {
        if (!_auth.IsAuthenticated) return;

        try
        {
            await FlushLikesAsync(ct).ConfigureAwait(false);

            Log.Info("[PlaylistSync] Starting liked videos sync from YouTube...");
            var likedTracks = await _ytUser.GetLikedTracksAsync(_youtube, ct: ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (likedTracks.Count == 0) return;

            var ownerId = CurrentOwnerId;

            // Pending and recently sent likes take precedence over cloud fetch, which lags behind writes
            Dictionary<string, bool>? overrides;
            lock (_outboxLock)
            {
                overrides = CollectLikeOverridesUnderLock(ownerId);
            }

            var filteredTracks = new List<TrackInfo>(likedTracks.Count);
            var trackIds = new List<string>(likedTracks.Count);
            var seen = new HashSet<string>(likedTracks.Count, StringComparer.Ordinal);
            for (int i = 0; i < likedTracks.Count; i++)
            {
                var t = likedTracks[i];
                if (overrides != null && overrides.TryGetValue(t.Id, out bool targetLiked) && !targetLiked)
                    continue;

                if (!seen.Add(t.Id))
                    continue;

                t.SetLikedState(true);
                _registry.RegisterOrUpdate(t, hasUserContext: true);
                filteredTracks.Add(t);
                trackIds.Add(t.Id);
            }

            if (overrides != null)
            {
                foreach (var kvp in overrides)
                {
                    if (kvp.Value && seen.Add(kvp.Key))
                        trackIds.Insert(0, kvp.Key);
                }
            }

            // Re-synchronization without changes must not rewrite the entire likes list in SQLite
            var localPrefix = await _tracks.GetLikedTrackIdsAsync(ownerId, limit: trackIds.Count, offset: 0, ct)
                .ConfigureAwait(false);
            if (CloudOutboxCoalescer.SequenceEqualOrdinal(localPrefix, trackIds))
            {
                Log.Info($"[PlaylistSync] Liked sync complete. {trackIds.Count} tracks already up to date.");
                return;
            }

            await _tracks.UpsertBatchAsync(filteredTracks, ct).ConfigureAwait(false);
            await _tracks.SetLikedBatchAsync(trackIds, ownerId, preserveOrder: true, ct).ConfigureAwait(false);

            Log.Info($"[PlaylistSync] Liked sync complete. Processed {trackIds.Count} tracks.");
        }
        catch (OperationCanceledException)
        {
            Log.Info("[PlaylistSync] Liked tracks sync cancelled by user.");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistSync] Liked tracks sync failed: {ex.Message}");
        }
    }

    private Dictionary<string, bool>? CollectLikeOverridesUnderLock(string ownerId)
    {
        PruneExpiredTombstonesUnderLock(DateTime.UtcNow.Ticks);
        Dictionary<string, bool>? overrides = null;

        foreach (var kvp in _likeTombstones)
        {
            if (!string.Equals(kvp.Key.OwnerId, ownerId, StringComparison.Ordinal))
                continue;

            overrides ??= new Dictionary<string, bool>(StringComparer.Ordinal);
            overrides[kvp.Key.TrackId] = kvp.Value.Liked;
        }

        // Unsent mutations override tombstones of already sent ones
        if (_outboxByKey.TryGetValue((ownerId, LikesOutboxTargetId), out var likeState))
        {
            for (int i = 0; i < likeState.PendingLikes.Count; i++)
            {
                overrides ??= new Dictionary<string, bool>(StringComparer.Ordinal);
                overrides[likeState.PendingLikes[i].TrackId] = likeState.PendingLikes[i].TargetLiked;
            }
        }

        return overrides;
    }

    /// <summary>
    /// Enqueues the remote playlist creation and track list upload into the background Outbox queue non-blockingly.
    /// </summary>
    public async Task UploadPlaylistToAccountAsync(Playlist playlist, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(playlist);

        if (!_auth.IsAuthenticated || playlist.SyncMode != PlaylistSyncMode.LocalOnly) return;

        await LinkToCloudAsync(playlist, ct).ConfigureAwait(false);
    }

    private async Task DowngradeForeignPlaylistOnForbiddenAsync(Playlist playlist, CancellationToken ct)
    {
        if (playlist.Ownership == PlaylistOwnership.Foreign)
            return;

        playlist.Ownership = PlaylistOwnership.Foreign;
        await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);
        Log.Warn($"[PlaylistSync] Playlist '{playlist.Name}' ({playlist.YoutubeId}) rejected cloud mutation (403 Forbidden / non-editable). Marked as Foreign read-only.");
    }

    /// <summary>
    /// Converts a remote playlist to local mode upon receiving a 404 Not Found status from the cloud.
    /// </summary>
    public async Task DowngradeDeadCloudPlaylistAsync(Playlist playlist, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(playlist);

        DiscardPlaylistOutbox(playlist.Id);

        playlist.SyncMode = PlaylistSyncMode.LocalOnly;
        playlist.YoutubeId = null;
        playlist.IsCloudUnavailable = false;
        await _playlists.UpsertAsync(playlist, ct).ConfigureAwait(false);

        Log.Warn(
            $"[PlaylistSync] Playlist '{playlist.Name}' (ID: {playlist.Id}) returned 404. Converted to LocalOnly.");

        await _notifications.ShowToastAsync(
            titleKey: "Dialog_Warning_Title",
            messageKey: "Playlist_NotFoundOnCloud_Downgraded",
            severity: NotificationSeverity.Warning,
            durationMs: 6000,
            messageArgs: [playlist.Name],
            ct: ct).ConfigureAwait(false);
    }

    private async Task<bool> SyncMetadataAsync(
        Playlist playlist,
        PlaylistSyncPreview preview,
        PlaylistSyncOptions options,
        CancellationToken ct)
    {
        bool changed = false;
        bool isCloudSource = options.Strategy == PlaylistSyncStrategy.ReplaceLocal || !playlist.CanSyncToCloud;

        if (options.SyncName && preview.NameDiffers)
        {
            if (isCloudSource)
            {
                playlist.Name = preview.CloudName;
                changed = true;
            }
            else
            {
                try
                {
                    await _youtube.RenamePlaylistAsync(playlist.YoutubeId!, playlist.Name).ConfigureAwait(false);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    Log.Warn($"[PlaylistSync] 403 Forbidden on rename for '{playlist.YoutubeId}'. Reverting to Foreign read-only.");
                    playlist.Ownership = PlaylistOwnership.Foreign;
                    playlist.Name = preview.CloudName;
                    isCloudSource = true;
                    changed = true;
                }
                catch (Exception ex)
                {
                    Log.Error($"[PlaylistSync] Rename on YouTube failed: {ex.Message}");
                }
            }
        }

        if (options.SyncDescription && preview.DescriptionDiffers)
        {
            if (isCloudSource)
            {
                playlist.Description = preview.CloudDescription ?? string.Empty;
                changed = true;
            }
            else
            {
                try
                {
                    var targetDesc = playlist.Description?.Trim() ?? string.Empty;
                    await _youtube.EditPlaylistDescriptionAsync(playlist.YoutubeId!, targetDesc).ConfigureAwait(false);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    Log.Warn($"[PlaylistSync] 403 Forbidden on description edit for '{playlist.YoutubeId}'. Reverting to Foreign read-only.");
                    playlist.Ownership = PlaylistOwnership.Foreign;
                    playlist.Description = preview.CloudDescription ?? string.Empty;
                    isCloudSource = true;
                    changed = true;
                }
                catch (Exception ex)
                {
                    Log.Error($"[PlaylistSync] Description update on YouTube failed: {ex.Message}");
                }
            }
        }

        if (options.SyncThumbnail && preview.ThumbnailDiffers)
        {
            if (isCloudSource || string.IsNullOrEmpty(playlist.ThumbnailUrl) || ThumbnailComparer.IsYoutubeHosted(playlist.ThumbnailUrl))
            {
                if (!string.IsNullOrEmpty(preview.CloudThumbnailUrl))
                {
                    playlist.ThumbnailUrl = preview.CloudThumbnailUrl;
                    playlist.ETag = ThumbnailComparer.Normalize(playlist.ThumbnailUrl);
                    playlist.ComputedColor = null;
                    changed = true;
                }
            }
            else
            {
                try
                {
                    var uploaded = await UploadThumbnailToYoutubeAsync(playlist.YoutubeId!, playlist.ThumbnailUrl, ct)
                        .ConfigureAwait(false);
                    if (uploaded)
                    {
                        playlist.ETag = ThumbnailComparer.Normalize(playlist.ThumbnailUrl);
                        changed = true;
                    }
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    Log.Warn($"[PlaylistSync] 403 Forbidden on thumbnail upload for '{playlist.YoutubeId}'. Reverting to Foreign read-only.");
                    playlist.Ownership = PlaylistOwnership.Foreign;
                    if (!string.IsNullOrEmpty(preview.CloudThumbnailUrl))
                    {
                        playlist.ThumbnailUrl = preview.CloudThumbnailUrl;
                        playlist.ETag = ThumbnailComparer.Normalize(playlist.ThumbnailUrl);
                        playlist.ComputedColor = null;
                    }
                    changed = true;
                }
                catch (Exception ex)
                {
                    Log.Error($"[PlaylistSync] Thumbnail upload failed: {ex.Message}");
                }
            }
        }

        return changed;
    }

    private async Task<(int AddedLocally, int AddedToCloud, int RemovedLocally, int RemovedFromCloud)>
        ReplaceLocalTracksAsync(Playlist playlist, FullPlaylistSyncData fullData, CancellationToken ct)
    {
        var localTrackIds = await _playlists.GetTrackIdsAsync(playlist.Id, CurrentOwnerId, ct).ConfigureAwait(false);

        int removedLocally = 0;
        if (localTrackIds.Count > 0)
        {
            removedLocally = await _playlists.RemoveTracksAsync(playlist.Id, localTrackIds, CurrentOwnerId, ct).ConfigureAwait(false);
            for (int i = 0; i < localTrackIds.Count; i++)
            {
                if (_registry.TryGet(localTrackIds[i]) is { } existingTrack)
                {
                    existingTrack.InPlaylists.Remove(playlist.Id);
                    _registry.UpdatePinStatus(existingTrack);
                }
            }
        }

        var canonicalTracks = new List<TrackInfo>(fullData.Tracks.Count);
        var newTrackIds = new List<string>(fullData.Tracks.Count);
        var mappings = new List<(string TrackId, string SetVideoId)>(fullData.Tracks.Count);

        for (int i = 0; i < fullData.Tracks.Count; i++)
        {
            var remote = fullData.Tracks[i];
            var track = CreateTrackInfo(remote);
            var canonical = _registry.RegisterOrUpdate(track);
            canonical.InPlaylists.Add(playlist.Id);
            _registry.UpdatePinStatus(canonical);

            canonicalTracks.Add(canonical);
            newTrackIds.Add(canonical.Id);

            if (!string.IsNullOrEmpty(remote.SetVideoId))
                mappings.Add((canonical.Id, remote.SetVideoId));
        }

        if (canonicalTracks.Count > 0)
        {
            await _tracks.UpsertBatchAsync(canonicalTracks, ct).ConfigureAwait(false);
            await _playlists.AddTracksAsync(playlist.Id, newTrackIds, CurrentOwnerId, ct).ConfigureAwait(false);
        }

        if (mappings.Count > 0)
            await _playlists.UpdateSetVideoIdsAsync(playlist.Id, mappings, ct).ConfigureAwait(false);

        return (canonicalTracks.Count, 0, removedLocally, 0);
    }

    private async Task<(int AddedLocally, int AddedToCloud, int RemovedLocally, int RemovedFromCloud)>
        ReplaceCloudTracksAsync(Playlist playlist, FullPlaylistSyncData fullData, CancellationToken ct)
    {
        var youtubeId = playlist.YoutubeId!;
        var localTrackIds = await _playlists.GetTrackIdsAsync(playlist.Id, CurrentOwnerId, ct).ConfigureAwait(false);

        var localRawIdMap = new Dictionary<string, string>(localTrackIds.Count, StringComparer.Ordinal);
        for (int i = 0; i < localTrackIds.Count; i++)
        {
            var localId = localTrackIds[i];
            if (!localId.StartsWith("yt_", StringComparison.Ordinal))
                continue;

            var raw = YoutubeIdHelper.ExtractRawId(localId);
            if (!string.IsNullOrEmpty(raw))
                localRawIdMap.TryAdd(raw, localId);
        }

        var toRemoveSetVideoIds = new List<string>();
        var currentCloudTracks = new List<RemoteTrackInfo>(fullData.Tracks.Count);
        var trackToSetVideoId = new Dictionary<string, string>(localRawIdMap.Count, StringComparer.Ordinal);
        var cloudRawIdSet = new HashSet<string>(fullData.Tracks.Count, StringComparer.Ordinal);

        for (int i = 0; i < fullData.Tracks.Count; i++)
        {
            var cloudTrack = fullData.Tracks[i];
            var cloudRawId = YoutubeIdHelper.ExtractRawId(cloudTrack.VideoId);
            bool hasValidSetId = !string.IsNullOrEmpty(cloudTrack.SetVideoId) &&
                                 !cloudTrack.SetVideoId.Equals("to_be_updated_by_client", StringComparison.OrdinalIgnoreCase);

            if (localRawIdMap.TryGetValue(cloudRawId, out var originalLocalId))
            {
                if (cloudRawIdSet.Add(cloudRawId))
                {
                    currentCloudTracks.Add(cloudTrack);
                    if (hasValidSetId)
                        trackToSetVideoId[originalLocalId] = cloudTrack.SetVideoId;
                }
                else if (hasValidSetId)
                {
                    toRemoveSetVideoIds.Add(cloudTrack.SetVideoId);
                }
            }
            else if (hasValidSetId)
            {
                toRemoveSetVideoIds.Add(cloudTrack.SetVideoId);
            }
        }

        int removedFromCloud = 0;
        if (toRemoveSetVideoIds.Count > 0)
        {
            await _youtube.RemoveTracksFromPlaylistAsync(youtubeId, toRemoveSetVideoIds).ConfigureAwait(false);
            removedFromCloud = toRemoveSetVideoIds.Count;
        }

        var toUpload = new List<string>();
        for (int i = 0; i < localTrackIds.Count; i++)
        {
            var localId = localTrackIds[i];
            if (!localId.StartsWith("yt_", StringComparison.Ordinal))
                continue;

            var raw = YoutubeIdHelper.ExtractRawId(localId);
            if (!string.IsNullOrEmpty(raw) && !cloudRawIdSet.Contains(raw))
                toUpload.Add(localId);
        }

        int addedToCloud = 0;
        if (toUpload.Count > 0)
        {
            var newSetVideoIds = await _youtube.AddTracksToPlaylistAsync(youtubeId, toUpload).ConfigureAwait(false);
            addedToCloud = toUpload.Count;

            for (int i = 0; i < newSetVideoIds.Count && i < toUpload.Count; i++)
            {
                if (!string.IsNullOrEmpty(newSetVideoIds[i]))
                    trackToSetVideoId[toUpload[i]] = newSetVideoIds[i]!;
            }
        }

        if (trackToSetVideoId.Count > 0)
        {
            var mappings = new List<(string TrackId, string SetVideoId)>(trackToSetVideoId.Count);
            foreach (var kvp in trackToSetVideoId)
                mappings.Add((kvp.Key, kvp.Value));

            await _playlists.UpdateSetVideoIdsAsync(playlist.Id, mappings, ct).ConfigureAwait(false);
        }

        var simulatedCloudOrder = new List<string>(currentCloudTracks.Count + toUpload.Count);
        for (int i = 0; i < currentCloudTracks.Count; i++)
        {
            var sid = currentCloudTracks[i].SetVideoId;
            if (!string.IsNullOrEmpty(sid))
                simulatedCloudOrder.Add(sid);
        }

        for (int i = 0; i < toUpload.Count; i++)
        {
            if (trackToSetVideoId.TryGetValue(toUpload[i], out var sid))
                simulatedCloudOrder.Add(sid);
        }

        var desiredOrder = new List<string>(localTrackIds.Count);
        for (int i = 0; i < localTrackIds.Count; i++)
        {
            if (trackToSetVideoId.TryGetValue(localTrackIds[i], out var sid))
                desiredOrder.Add(sid);
        }

        var reorderMoves = BuildMinimalReorderMoves(simulatedCloudOrder, desiredOrder);
        if (reorderMoves.Count > 0)
        {
            await _youtube.MoveTracksInPlaylistAsync(youtubeId, reorderMoves, ct).ConfigureAwait(false);
        }

        return (0, addedToCloud, 0, removedFromCloud);
    }

    private async Task<(int AddedLocally, int AddedToCloud, int RemovedLocally, int RemovedFromCloud)>
        MergeTracksAsync(Playlist playlist, FullPlaylistSyncData fullData, CancellationToken ct)
    {
        var youtubeId = playlist.YoutubeId!;
        var localTrackIds = await _playlists.GetTrackIdsAsync(playlist.Id, CurrentOwnerId, ct).ConfigureAwait(false);

        var localIdSet = new HashSet<string>(localTrackIds, StringComparer.Ordinal);
        var cloudIdSet = new HashSet<string>(fullData.Tracks.Count, StringComparer.Ordinal);
        for (int i = 0; i < fullData.Tracks.Count; i++)
            cloudIdSet.Add("yt_" + fullData.Tracks[i].VideoId);

        // Tracks deleted in the cloud on another device are removed locally rather than uploaded again
        var cloudDeleted = await SelectCloudDeletedLocalTracksAsync(playlist, cloudIdSet, ct).ConfigureAwait(false);
        int removedLocally = 0;
        if (cloudDeleted is { Count: > 0 })
            removedLocally = await RemoveCloudDeletedLocalTracksAsync(playlist, cloudDeleted, ct).ConfigureAwait(false);

        int addedLocally = 0;
        var setVideoIdMappings = new List<(string TrackId, string SetVideoId)>(fullData.Tracks.Count);

        for (int i = 0; i < fullData.Tracks.Count; i++)
        {
            var remote = fullData.Tracks[i];
            var fullId = "yt_" + remote.VideoId;

            if (!string.IsNullOrEmpty(remote.SetVideoId))
                setVideoIdMappings.Add((fullId, remote.SetVideoId));

            if (!localIdSet.Contains(fullId))
            {
                var track = CreateTrackInfo(remote);
                var canonical = _registry.RegisterOrUpdate(track);
                canonical.InPlaylists.Add(playlist.Id);
                _registry.UpdatePinStatus(canonical);
                await _tracks.UpsertAsync(canonical, ct).ConfigureAwait(false);
                await _playlists.AddTrackAsync(playlist.Id, canonical.Id, CurrentOwnerId, null, ct)
                    .ConfigureAwait(false);
                addedLocally++;
            }
        }

        var toUpload = new List<string>();
        for (int i = 0; i < localTrackIds.Count; i++)
        {
            var trackId = localTrackIds[i];
            if (trackId.StartsWith("yt_", StringComparison.Ordinal) && !cloudIdSet.Contains(trackId) &&
                cloudDeleted?.Contains(trackId) != true)
            {
                toUpload.Add(trackId);
            }
        }

        int addedToCloud = 0;
        if (toUpload.Count > 0)
        {
            var newSetVideoIds = await _youtube.AddTracksToPlaylistAsync(youtubeId, toUpload).ConfigureAwait(false);
            addedToCloud = toUpload.Count;

            for (int i = 0; i < newSetVideoIds.Count && i < toUpload.Count; i++)
            {
                if (!string.IsNullOrEmpty(newSetVideoIds[i]))
                    setVideoIdMappings.Add((toUpload[i], newSetVideoIds[i]!));
            }
        }

        if (setVideoIdMappings.Count > 0)
            await _playlists.UpdateSetVideoIdsAsync(playlist.Id, setVideoIdMappings, ct).ConfigureAwait(false);

        return (addedLocally, addedToCloud, removedLocally, 0);
    }

    private async Task<HashSet<string>?> SelectCloudDeletedLocalTracksAsync(
        Playlist playlist,
        HashSet<string> cloudTrackIds,
        CancellationToken ct)
    {
        var localLinks = await _playlists.GetTracksWithSetVideoIdsAsync(playlist.Id, ct).ConfigureAwait(false);
        var deleted = CloudOutboxCoalescer.SelectCloudDeletedTrackIds(localLinks, cloudTrackIds);
        if (deleted == null)
        {
            Log.Warn($"[PlaylistSync] Merge of '{playlist.Name}': too many linked tracks are absent in cloud " +
                     $"(cloud={cloudTrackIds.Count}, local={localLinks.Count}) -> " +
                     "cloud deletions are not applied locally");
        }
        else if (deleted.Count > 0)
        {
            Log.Info($"[PlaylistSync] Merge of '{playlist.Name}': {deleted.Count} track(s) " +
                     "were deleted in cloud -> removing locally");
        }

        return deleted;
    }

    private async Task<int> RemoveCloudDeletedLocalTracksAsync(
        Playlist playlist,
        HashSet<string> trackIds,
        CancellationToken ct)
    {
        int removed = await _playlists.RemoveTracksAsync(playlist.Id, trackIds, CurrentOwnerId, ct)
            .ConfigureAwait(false);
        foreach (var trackId in trackIds)
        {
            if (_registry.TryGet(trackId) is { } existingTrack)
            {
                existingTrack.InPlaylists.Remove(playlist.Id);
                _registry.UpdatePinStatus(existingTrack);
            }
        }

        return removed;
    }

    private static TrackInfo CreateTrackInfo(RemoteTrackInfo remote) => new()
    {
        Id = "yt_" + remote.VideoId,
        Title = remote.Title,
        Author = remote.Author,
        Duration = TimeSpan.FromSeconds(remote.DurationSeconds),
        ThumbnailUrl = remote.ThumbnailUrl,
        IsMusic = true,
        Url = $"https://music.youtube.com/watch?v={remote.VideoId}"
    };

    private static bool ShouldUpdateViewCount(long? currentViews, long? cloudViews)
    {
        if (cloudViews is null)
            return currentViews is not null;

        if (currentViews is null or <= 0)
            return true;

        long cur = currentViews.Value;
        long cloud = cloudViews.Value;
        if (cur == cloud)
            return false;

        if (cloud >= 1_000 && cloud % 100 == 0 && cur % 100 != 0)
        {
            long step = cloud >= 10_000 ? 1_000 : 100;
            if (Math.Abs(cur - cloud) < step)
                return false;
        }

        return true;
    }

    private static bool IsLocalCoverAlreadySyncedWithCloud(
        Playlist playlist,
        string? cloudThumbnailUrl,
        out string? normalizedLocal)
    {
        normalizedLocal = ThumbnailComparer.Normalize(playlist.ThumbnailUrl);
        if (string.IsNullOrEmpty(normalizedLocal) || string.IsNullOrEmpty(cloudThumbnailUrl))
            return false;

        if (ThumbnailComparer.IsYoutubeHosted(normalizedLocal))
            return true;

        if (playlist.LastSyncedAtUtc is not { } lastSyncUtc)
            return false;

        string? localPath = null;
        if (normalizedLocal.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(normalizedLocal, UriKind.Absolute, out var uri))
                localPath = uri.LocalPath;
        }
        else if (Path.IsPathRooted(normalizedLocal))
        {
            localPath = normalizedLocal;
        }

        if (!string.IsNullOrEmpty(localPath))
        {
            if (!File.Exists(localPath))
                return false;

            var lastWriteUtc = File.GetLastWriteTimeUtc(localPath);
            if (lastWriteUtc > lastSyncUtc.AddSeconds(5))
                return false;

            return string.IsNullOrEmpty(playlist.ETag) ||
                   string.Equals(playlist.ETag, normalizedLocal, StringComparison.OrdinalIgnoreCase);
        }

        return !string.IsNullOrEmpty(playlist.ETag) &&
               string.Equals(playlist.ETag, normalizedLocal, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> UploadThumbnailToYoutubeAsync(
        string youtubePlaylistId,
        string thumbnailUrl,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(thumbnailUrl) || string.IsNullOrWhiteSpace(youtubePlaylistId))
            return false;

        if (ThumbnailComparer.IsYoutubeHosted(thumbnailUrl))
        {
            Log.Debug($"[PlaylistSync] Skipping upload for YouTube-hosted thumbnail on playlist {youtubePlaylistId}");
            return false;
        }

        byte[] imageData;

        try
        {
            if (thumbnailUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                thumbnailUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                imageData = await _networkManager.ImageClient.GetByteArrayAsync(thumbnailUrl, linkedCts.Token)
                    .ConfigureAwait(false);
            }
            else if (thumbnailUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(thumbnailUrl);
                var localPath = uri.LocalPath;
                if (!File.Exists(localPath)) return false;
                imageData = await File.ReadAllBytesAsync(localPath, ct).ConfigureAwait(false);
            }
            else if (Path.IsPathRooted(thumbnailUrl) && File.Exists(thumbnailUrl))
            {
                imageData = await File.ReadAllBytesAsync(thumbnailUrl, ct).ConfigureAwait(false);
            }
            else
            {
                return false;
            }
        }
        catch (HttpRequestException ex)
        {
            Log.Warn($"[PlaylistSync] Thumbnail source unavailable (HTTP {(int?)ex.StatusCode}): {ex.Message}");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"[PlaylistSync] Failed to read thumbnail data for upload: {ex.Message}");
            return false;
        }

        if (imageData.Length == 0 || imageData.Length > 20 * 1024 * 1024)
            return false;

        return await _youtube.UploadPlaylistThumbnailAsync(youtubePlaylistId, imageData).ConfigureAwait(false);
    }

    #endregion

    #region IDisposable / IAsyncDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        if (!BeginShutdown())
            return;

        // Synchronous Dispose is called by the container on exit: timeout is capped to prevent hanging shutdown
        try
        {
            Task.Run(ShutdownCoreAsync).Wait(TimeSpan.FromMilliseconds(ShutdownWorkerWaitMs + ShutdownPersistDrainMs));
        }
        catch (Exception ex)
        {
            Log.Debug($"[PlaylistOutbox] Shutdown notice: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!BeginShutdown())
            return;

        await ShutdownCoreAsync().ConfigureAwait(false);
    }

    private bool BeginShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
            return false;

        _disposed = true;
        _networkManager.NetworkRebuilt -= HandleNetworkOrAuthRestored;
        _auth.OnAuthStateChanged -= HandleNetworkOrAuthRestored;

        _disposeCts.Cancel();
        _wakeUpTcs.TrySetResult();
        return true;
    }

    private async Task ShutdownCoreAsync()
    {
        bool workerStopped = _workerTask.IsCompleted;
        if (!workerStopped)
        {
            await Task.WhenAny(_workerTask, Task.Delay(ShutdownWorkerWaitMs)).ConfigureAwait(false);
            workerStopped = _workerTask.IsCompleted;
        }

        if (_persistencePump != null)
        {
            // Latest commits must reach SQLite; otherwise, acknowledged mutations will be re-sent on the next launch
            var hydrationTask = Volatile.Read(ref _hydrationTask);
            if (hydrationTask is { IsCompleted: false })
                await Task.WhenAny(hydrationTask, Task.Delay(ShutdownPersistDrainMs)).ConfigureAwait(false);

            if (!await _persistencePump.DrainAsync(ShutdownPersistDrainMs).ConfigureAwait(false))
                Log.Warn("[PlaylistOutbox] Outbox persistence was not fully drained before shutdown.");
        }

        // CTS is disposed only after the worker stops; _flushLock is intentionally left unreleased:
        // SemaphoreSlim without AvailableWaitHandle holds no unmanaged resources, and a late Release must not crash
        if (workerStopped)
            _disposeCts.Dispose();
    }

    #endregion
}
