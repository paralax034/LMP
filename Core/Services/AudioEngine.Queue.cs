using LMP.Core.Audio.Http;

namespace LMP.Core.Services;

/// <summary>
/// Identifies the origin of the active playback queue.
/// </summary>
public enum QueueSourceKind : byte
{
    /// <summary>Queue was assembled manually, from search/home, or detached from its original playlist.</summary>
    Manual = 0,

    /// <summary>Queue is bound to a specific library or system playlist.</summary>
    Playlist = 1
}

/// <summary>
/// Immutable snapshot of the active queue origin, track composition, and generation counter.
/// Mutated exclusively under <see cref="AudioEngine._queueLock"/> alongside the queue itself.
/// </summary>
public sealed class QueueSource
{
    private readonly HashSet<string> _snapshotSet;

    /// <summary>Singleton initial manual source at generation 0.</summary>
    public static readonly QueueSource EmptyManual = new(
        QueueSourceKind.Manual,
        id: null,
        name: null,
        orderedIds: [],
        snapshotSet: new HashSet<string>(StringComparer.Ordinal),
        generation: 0);

    /// <summary>Origin kind of the queue.</summary>
    public QueueSourceKind Kind { get; }

    /// <summary>Source playlist identifier, or <see langword="null"/> when <see cref="Kind"/> is <see cref="QueueSourceKind.Manual"/>.</summary>
    public string? Id { get; }

    /// <summary>Display name of the source playlist, or <see langword="null"/> when manual.</summary>
    public string? Name { get; }

    /// <summary>Ordered track identifiers in the source snapshot.</summary>
    public IReadOnlyList<string> OrderedIds { get; }

    /// <summary>Monotonically increasing generation counter incremented on every source transition.</summary>
    public int Generation { get; }

    /// <summary>Number of unique tracks in the source snapshot.</summary>
    public int SnapshotCount => _snapshotSet.Count;

    private QueueSource(
        QueueSourceKind kind,
        string? id,
        string? name,
        IReadOnlyList<string> orderedIds,
        HashSet<string> snapshotSet,
        int generation)
    {
        Kind = kind;
        Id = id;
        Name = name;
        OrderedIds = orderedIds;
        _snapshotSet = snapshotSet;
        Generation = generation;
    }

    /// <summary>
    /// Checks in O(1) whether the specified track identifier belongs to the source snapshot.
    /// </summary>
    public bool ContainsTrack(string trackId) => _snapshotSet.Contains(trackId);

    /// <summary>
    /// Creates a manual queue source with the specified generation.
    /// </summary>
    public static QueueSource CreateManual(int generation) =>
        new(QueueSourceKind.Manual, id: null, name: null, orderedIds: [], new HashSet<string>(StringComparer.Ordinal), generation);

    /// <summary>
    /// Creates a playlist queue source with a deduplicated snapshot of track identifiers.
    /// </summary>
    public static QueueSource CreatePlaylist(
        string playlistId,
        string? name,
        IReadOnlyList<string> trackIds,
        int generation)
    {
        var set = new HashSet<string>(trackIds.Count, StringComparer.Ordinal);
        var ordered = new List<string>(trackIds.Count);
        for (int i = 0; i < trackIds.Count; i++)
        {
            string id = trackIds[i];
            if (!string.IsNullOrEmpty(id) && set.Add(id))
                ordered.Add(id);
        }

        return new QueueSource(QueueSourceKind.Playlist, playlistId, name, ordered, set, generation);
    }

    /// <summary>
    /// Returns a new <see cref="QueueSource"/> of the same generation with an updated track composition snapshot and optional name.
    /// </summary>
    internal QueueSource WithUpdatedSnapshot(IReadOnlyList<string> newTrackIds, string? newName = null) =>
        CreatePlaylist(Id ?? string.Empty, newName ?? Name, newTrackIds, Generation);

    /// <summary>
    /// Returns a new <see cref="QueueSource"/> of the same generation and snapshot with an updated display name.
    /// </summary>
    internal QueueSource WithName(string? newName) =>
        new(Kind, Id, newName, OrderedIds, _snapshotSet, Generation);
}

/// <summary>
/// Result of applying a live playlist diff to the playback queue.
/// </summary>
public readonly record struct PlaylistDiffOutcome(
    bool Applied,
    bool QueueMutated,
    bool SourceChanged,
    bool RemovedCurrent,
    bool CanMoveNext,
    bool IsQueuePure);

public sealed partial class AudioEngine
{
    #region Queue State

    private readonly List<TrackInfo> _queue = new(64);
    private IReadOnlyList<TrackInfo>? _queueSnapshot;
    private int _currentIndex = -1;
    private bool _queueMutatedByNavigation;
    private QueueSource _queueSource = QueueSource.EmptyManual;
    private int _queueSourceGeneration;
    private bool _isQueuePure;

    public IReadOnlyList<TrackInfo> Queue
    {
        get
        {
            lock (_queueLock)
            {
                _queueSnapshot ??= [.. _queue];
                return _queueSnapshot;
            }
        }
    }

    /// <summary>
    /// Current queue source snapshot read atomically under <see cref="_queueLock"/>.
    /// </summary>
    public QueueSource QueueSource
    {
        get
        {
            lock (_queueLock)
                return _queueSource;
        }
    }

    /// <summary>
    /// Indicates whether the current queue composition matches the active playlist source snapshot.
    /// </summary>
    public bool IsQueuePure
    {
        get
        {
            lock (_queueLock)
                return _isQueuePure;
        }
    }

    #endregion

    #region Public API

    /// <summary>
    /// Adds a range of tracks to the queue, filtering out duplicates without LINQ allocations.
    /// If the queue was empty, automatically starts playback of the first added track.
    /// </summary>
    /// <param name="tracks">The list of tracks to add.</param>
    /// <returns>The number of unique tracks actually added.</returns>
    public int EnqueueRangeUnique(IEnumerable<TrackInfo> tracks)
    {
        int addedCount = 0;
        TrackInfo? playbackTrack = null;
        bool shouldAutoplay = false;

        lock (_queueLock)
        {
            var existingIds = new HashSet<string>(_queue.Count + 16, StringComparer.Ordinal);
            for (int i = 0; i < _queue.Count; i++)
                existingIds.Add(_queue[i].Id);

            int initialCount = _queue.Count;
            foreach (var track in tracks)
            {
                if (existingIds.Add(track.Id))
                {
                    _queue.Add(track);
                    addedCount++;
                }
            }

            if (addedCount > 0)
            {
                RecalculateQueuePurityLocked();
                InvalidateQueueSnapshot();

                if (CurrentTrack == null && !IsPlaying && !IsLoading)
                {
                    _currentIndex = initialCount;
                    playbackTrack = _queue[_currentIndex];
                    shouldAutoplay = true;
                }
            }
        }

        if (addedCount > 0)
        {
            RaiseOnUI(_raiseQueueChangedOnUIDelegate);

            if (shouldAutoplay && playbackTrack != null)
            {
                ResetSealedFailedTrack();
                int session = BeginNewSession();
                EnqueueCommand(new PlayCurrentIndexCommand(session, StartPlaying: true));
            }
        }

        return addedCount;
    }

    public void Enqueue(TrackInfo track)
    {
        TrackInfo? playbackTrack = null;
        bool shouldAutoplay = false;

        lock (_queueLock)
        {
            if (FindTrackIndexById(_queue, track.Id) >= 0)
                return;

            _queue.Add(track);
            RecalculateQueuePurityLocked();
            InvalidateQueueSnapshot();

            if (CurrentTrack == null && !IsPlaying && !IsLoading)
            {
                _currentIndex = _queue.Count - 1;
                playbackTrack = _queue[_currentIndex];
                shouldAutoplay = true;
            }
        }

        RaiseOnUI(_raiseQueueChangedOnUIDelegate);

        if (shouldAutoplay && playbackTrack != null)
        {
            ResetSealedFailedTrack();
            int session = BeginNewSession();
            EnqueueCommand(new PlayCurrentIndexCommand(session, StartPlaying: true));
        }
    }

    public void EnqueueRange(IEnumerable<TrackInfo> tracks)
    {
        int count;

        lock (_queueLock)
        {
            if (tracks is ICollection<TrackInfo> coll && coll.Count == 0)
                return;

            int before = _queue.Count;
            _queue.AddRange(tracks);
            count = _queue.Count - before;
            if (count == 0) return;

            RecalculateQueuePurityLocked();
            InvalidateQueueSnapshot();
        }

        RaiseOnUI(_raiseQueueChangedOnUIDelegate);
    }

    internal void ShuffleQueue()
    {
        lock (_queueLock)
        {
            if (_queue.Count < 2) return;
            ApplyShuffleInPlace(preserveCurrentAtStart: true);
            RecalculateQueuePurityLocked();
        }
        RaiseOnUI(_raiseQueueChangedOnUIDelegate);
    }

    internal void ClearQueue()
    {
        lock (_queueLock)
        {
            var current = CurrentTrack;
            _queue.Clear();
            _currentIndex = -1;
            if (current != null) { _queue.Add(current); _currentIndex = 0; }
            _queueSource = QueueSource.CreateManual(++_queueSourceGeneration);
            RecalculateQueuePurityLocked();
            InvalidateQueueSnapshot();
        }
        RaiseOnUI(_raiseQueueChangedOnUIDelegate);
    }

    /// <summary>
    /// Removes a track from the queue. A removed currently playing track is immediately replaced by the next one according to the "Next" rules:
    /// at the end of the queue — loops if RepeatMode.All or stops; if paused, the next track is loaded without starting playback.
    /// </summary>
    public void RemoveFromQueue(TrackInfo track)
    {
        bool removedCurrent;
        bool canMove = false;
        // Intent to listen comes from engine state: after loading while paused, player is Stopped, and !IsPaused would start playback
        bool startPlaying = CurrentTrack != null && _playIntent;

        lock (_queueLock)
        {
            int removedIndex = FindTrackIndexById(_queue, track.Id);
            if (removedIndex == -1) return;

            removedCurrent = removedIndex == _currentIndex;
            _queue.RemoveAt(removedIndex);
            _currentIndex = ComputeIndexAfterRemove(removedIndex, _currentIndex);
            if (removedCurrent) canMove = TryMoveNext(userInitiated: true);
            RecalculateQueuePurityLocked();
            InvalidateQueueSnapshot();
        }

        RaiseOnUI(_raiseQueueChangedOnUIDelegate);

        if (!removedCurrent) return;

        if (canMove)
        {
            ResetSealedFailedTrack();
            int session = BeginNewSession();
            EnqueueCommand(new PlayCurrentIndexCommand(session, startPlaying));
        }
        else
        {
            Stop();
        }
    }

    internal void MoveQueueItem(int from, int to)
    {
        lock (_queueLock)
        {
            if (from < 0 || from >= _queue.Count || to < 0 || to >= _queue.Count || from == to) return;
            var item = _queue[from];
            _queue.RemoveAt(from);
            _queue.Insert(to, item);
            if (_currentIndex == from) _currentIndex = to;
            else if (from < _currentIndex && to >= _currentIndex) _currentIndex--;
            else if (from > _currentIndex && to <= _currentIndex) _currentIndex++;
            InvalidateQueueSnapshot();
        }

        RaiseOnUI(_raiseQueueChangedOnUIDelegate);
    }

    /// <summary>
    /// Detaches the active playlist source (transitioning to <see cref="QueueSourceKind.Manual"/>) if the specified playlist was deleted,
    /// leaving the queued tracks untouched.
    /// </summary>
    public bool DetachPlaylistSource(string playlistId)
    {
        bool detached = false;
        lock (_queueLock)
        {
            if (_queueSource.Kind == QueueSourceKind.Playlist
                && string.Equals(_queueSource.Id, playlistId, StringComparison.Ordinal))
            {
                _queueSource = QueueSource.CreateManual(++_queueSourceGeneration);
                _isQueuePure = false;
                detached = true;
            }
        }

        if (detached)
            RaiseOnUI(_raiseQueueSourceChangedOnUIDelegate);

        return detached;
    }

    /// <summary>
    /// Synchronously applies a live playlist diff under <see cref="_queueLock"/>, rejecting stale generations
    /// and advancing playback immediately if the playing track was removed from the playlist.
    /// </summary>
    public PlaylistDiffOutcome ApplyPlaylistDelta(
        string playlistId,
        int expectedGeneration,
        IReadOnlyList<string> newPlaylistTrackIds,
        IReadOnlyList<TrackInfo> addedTracks,
        string? newPlaylistName = null)
    {
        bool startPlaying = CurrentTrack != null && _playIntent;
        PlaylistDiffOutcome outcome;
        bool purityChanged;

        lock (_queueLock)
        {
            outcome = ApplyPlaylistDiffCore(
                _queue,
                ref _currentIndex,
                ref _queueSource,
                playlistId,
                expectedGeneration,
                newPlaylistTrackIds,
                addedTracks,
                ShuffleEnabled,
                RepeatMode == RepeatMode.All,
                newPlaylistName);

            purityChanged = outcome.Applied && _isQueuePure != outcome.IsQueuePure;
            if (outcome.Applied)
            {
                _isQueuePure = outcome.IsQueuePure;

                if (outcome.QueueMutated)
                    InvalidateQueueSnapshot();
            }
        }

        if (!outcome.Applied)
            return outcome;

        if (outcome.QueueMutated)
        {
            RaiseOnUI(_raiseQueueChangedOnUIDelegate);
        }
        else if (outcome.SourceChanged || purityChanged)
        {
            RaiseOnUI(_raiseQueueSourceChangedOnUIDelegate);
        }

        if (outcome.RemovedCurrent)
        {
            if (outcome.CanMoveNext)
            {
                ResetSealedFailedTrack();
                int session = BeginNewSession();
                EnqueueCommand(new PlayCurrentIndexCommand(session, startPlaying));
            }
            else
            {
                Stop();
            }
        }

        return outcome;
    }

    #endregion

    #region Pure QueueSource & Live Diff Algorithms

    /// <summary>
    /// Finds the index of a track with the specified identifier in a list without closure allocations.
    /// </summary>
    internal static int FindTrackIndexById(IReadOnlyList<TrackInfo> list, string? trackId)
    {
        if (string.IsNullOrEmpty(trackId)) return -1;
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].Id, trackId, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Determines whether the queue composition matches the source playlist snapshot (order-independent).
    /// </summary>
    internal static bool ComputeIsQueuePure(IReadOnlyList<TrackInfo> queue, QueueSource source)
    {
        if (source.Kind != QueueSourceKind.Playlist || source.SnapshotCount == 0)
            return false;

        if (queue.Count != source.SnapshotCount)
            return false;

        for (int i = 0; i < queue.Count; i++)
        {
            if (!source.ContainsTrack(queue[i].Id))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Inserts a track at a random position strictly after <paramref name="currentIndex"/> within the user queue.
    /// </summary>
    internal static int InsertRandomAfterCurrent(
        List<TrackInfo> queue,
        int currentIndex,
        TrackInfo track,
        Random? random = null)
    {
        int count = queue.Count;
        if (count == 0 || currentIndex < 0 || currentIndex >= count)
        {
            queue.Add(track);
            return count;
        }

        int minIndex = currentIndex + 1;
        int maxExclusive = count + 1;
        int insertIndex = minIndex >= count
            ? count
            : (random ?? Random.Shared).Next(minIndex, maxExclusive);

        queue.Insert(insertIndex, track);
        return insertIndex;
    }

    /// <summary>
    /// O(1) allocation-free filter for <see cref="LibraryService.OnTrackUpdated"/>:
    /// returns <see langword="true"/> only when the active source is the system "Liked" playlist
    /// and <see cref="TrackInfo.IsLiked"/> diverges from the current source snapshot.
    /// </summary>
    internal static bool ShouldSyncLikedOnTrackUpdated(QueueSource source, TrackInfo? track)
    {
        if (track == null
            || source.Kind != QueueSourceKind.Playlist
            || !string.Equals(source.Id, LibraryService.LikedPlaylistId, StringComparison.Ordinal))
        {
            return false;
        }

        return track.IsLiked != source.ContainsTrack(track.Id);
    }

    /// <summary>
    /// Pure transition for starting a queue from a playlist or manually.
    /// </summary>
    internal static QueueSource TransitionOnStartQueue(
        ref int generationCounter,
        IReadOnlyList<TrackInfo> tracks,
        string? playlistId,
        string? playlistName)
    {
        int gen = ++generationCounter;
        if (string.IsNullOrEmpty(playlistId))
            return QueueSource.CreateManual(gen);

        var ids = new List<string>(tracks.Count);
        for (int i = 0; i < tracks.Count; i++)
            ids.Add(tracks[i].Id);

        return QueueSource.CreatePlaylist(playlistId, playlistName, ids, gen);
    }

    /// <summary>
    /// Pure transition for playing a single track: preserves source if the track is already in the queue,
    /// or resets to <see cref="QueueSourceKind.Manual"/> with a new generation if the queue was replaced.
    /// </summary>
    internal static QueueSource TransitionOnPlayTrack(
        QueueSource currentSource,
        ref int generationCounter,
        bool wasInQueue)
    {
        return wasInQueue
            ? currentSource
            : QueueSource.CreateManual(++generationCounter);
    }

    /// <summary>
    /// Pure transition for clearing the queue: resets source to <see cref="QueueSourceKind.Manual"/> with a new generation.
    /// </summary>
    internal static QueueSource TransitionOnClearQueue(ref int generationCounter) =>
        QueueSource.CreateManual(++generationCounter);

    /// <summary>
    /// Pure function that applies a live playlist diff ("old snapshot -> new composition") to the queue:
    /// - Rejects diffs for a stale generation or different playlist;
    /// - Removes tracks deleted from the playlist (advancing immediately if the current track was removed) while leaving manually added foreign tracks intact;
    /// - Inserts newly added playlist tracks without duplicates (at the end when <paramref name="shuffleEnabled"/> is <see langword="false"/>, or at a random index after <paramref name="currentIndex"/> when <see langword="true"/>), without re-adding tracks previously removed from the queue by the user;
    /// - Ignores pure track reordering in the playlist;
    /// - Updates source metadata and recalculates queue purity.
    /// </summary>
    internal static PlaylistDiffOutcome ApplyPlaylistDiffCore(
        List<TrackInfo> queue,
        ref int currentIndex,
        ref QueueSource source,
        string playlistId,
        int expectedGeneration,
        IReadOnlyList<string> newPlaylistTrackIds,
        IReadOnlyList<TrackInfo> addedTracks,
        bool shuffleEnabled,
        bool repeatAll,
        string? newPlaylistName = null,
        Random? random = null)
    {
        if (source.Kind != QueueSourceKind.Playlist
            || source.Generation != expectedGeneration
            || !string.Equals(source.Id, playlistId, StringComparison.Ordinal))
        {
            return new PlaylistDiffOutcome(
                Applied: false,
                QueueMutated: false,
                SourceChanged: false,
                RemovedCurrent: false,
                CanMoveNext: false,
                IsQueuePure: ComputeIsQueuePure(queue, source));
        }

        var newSet = new HashSet<string>(newPlaylistTrackIds.Count, StringComparer.Ordinal);
        for (int i = 0; i < newPlaylistTrackIds.Count; i++)
        {
            string id = newPlaylistTrackIds[i];
            if (!string.IsNullOrEmpty(id))
                newSet.Add(id);
        }

        bool queueMutated = false;
        bool removedCurrent = false;

        // 1. Remove tracks that were part of the previous playlist snapshot but are absent from the new composition
        int idx = 0;
        while (idx < queue.Count)
        {
            string trackId = queue[idx].Id;
            if (source.ContainsTrack(trackId) && !newSet.Contains(trackId))
            {
                if (idx == currentIndex)
                    removedCurrent = true;

                queue.RemoveAt(idx);
                currentIndex = ComputeIndexAfterRemove(idx, currentIndex);
                queueMutated = true;
                continue;
            }

            idx++;
        }

        bool canMoveNext = false;
        if (removedCurrent && queue.Count > 0)
        {
            int nextIdx = ComputeNextIndex(currentIndex, queue.Count, repeatAll, userInitiated: true);
            if (nextIdx >= 0)
            {
                currentIndex = nextIdx;
                canMoveNext = true;
            }
            else
            {
                currentIndex = -1;
            }
        }

        // 2. Insert tracks newly added to the playlist (absent from the previous snapshot) without duplicates
        for (int i = 0; i < newPlaylistTrackIds.Count; i++)
        {
            string id = newPlaylistTrackIds[i];
            if (string.IsNullOrEmpty(id) || source.ContainsTrack(id))
                continue;

            if (FindTrackIndexById(queue, id) >= 0)
                continue;

            int addedIdx = FindTrackIndexById(addedTracks, id);
            if (addedIdx < 0)
                continue;

            var trackToInsert = addedTracks[addedIdx];
            if (shuffleEnabled)
            {
                InsertRandomAfterCurrent(queue, currentIndex, trackToInsert, random);
            }
            else
            {
                queue.Add(trackToInsert);
            }

            queueMutated = true;

            if (removedCurrent && !canMoveNext && queue.Count > 0)
            {
                currentIndex = 0;
                canMoveNext = true;
            }
        }

        // 3. Update snapshot and metadata
        bool compositionChanged = newSet.Count != source.SnapshotCount
            || !CloudOutboxCoalescer.SequenceEqualOrdinal(source.OrderedIds, newPlaylistTrackIds);
        bool nameChanged = newPlaylistName != null
            && !string.Equals(source.Name, newPlaylistName, StringComparison.Ordinal);

        if (compositionChanged)
        {
            source = source.WithUpdatedSnapshot(newPlaylistTrackIds, newPlaylistName ?? source.Name);
        }
        else if (nameChanged)
        {
            source = source.WithName(newPlaylistName);
        }

        bool isPure = ComputeIsQueuePure(queue, source);

        return new PlaylistDiffOutcome(
            Applied: true,
            QueueMutated: queueMutated,
            SourceChanged: compositionChanged || nameChanged,
            RemovedCurrent: removedCurrent,
            CanMoveNext: canMoveNext,
            IsQueuePure: isPure);
    }

    private void RecalculateQueuePurityLocked()
    {
        _isQueuePure = ComputeIsQueuePure(_queue, _queueSource);
    }

    #endregion

    #region Pre-warm

    /// <summary>
    /// Speculatively prewarms CDN connections for upcoming tracks in the queue.
    /// </summary>
    private void PreWarmNextTracksInQueue(int currentIndex, HttpClient httpClient, CancellationToken ct)
    {
        TrackInfo? next1 = null;
        TrackInfo? next2 = null;

        lock (_queueLock)
        {
            int idx1 = currentIndex + 1;
            int idx2 = currentIndex + 2;
            if ((uint)idx1 < (uint)_queue.Count) next1 = _queue[idx1];
            if ((uint)idx2 < (uint)_queue.Count) next2 = _queue[idx2];
        }

        PreWarmSingleQueueTrack(next1, currentIndex + 1, httpClient, ct);
        PreWarmSingleQueueTrack(next2, currentIndex + 2, httpClient, ct);
    }

    private void PreWarmSingleQueueTrack(TrackInfo? next, int queueIdx, HttpClient httpClient, CancellationToken ct)
    {
        if (next == null) return;
        if (AudioSourceFactory.FindAnyCachedTrack(next.Id) != null) return;

        string? targetUrl = null;

        var manifest = SessionCacheStore.GetManifest(next.Id);
        if (manifest is { Variants.Count: > 0 })
        {
            targetUrl = manifest.Variants[0].Url;
        }
        else
        {
            string rawId = next.GetRawIdSpan().ToString();
            var hint = StreamSelectionHint.FromTrack(next, _library.Settings.RememberTrackFormat);
            var descriptor = _youtube.TryGetCachedStreamDescriptor(
                rawId,
                hint.Format,
                hint.BitrateKbps);

            if (descriptor is { HasLiveUrl: true } d)
                targetUrl = d.Url;
        }

        if (!string.IsNullOrEmpty(targetUrl))
        {
            CdnConnectionPreWarmer.PreWarmHost(httpClient, targetUrl, ct);
            Log.Debug($"[AudioEngine] Lookahead CDN prewarm queue[{queueIdx}]: {next.Id}");
        }
    }

    #endregion

    #region Navigation

    private bool TryMoveNext(bool userInitiated)
    {
        _queueMutatedByNavigation = false;
        if (_queue.Count == 0) return false;
        if (!userInitiated && RepeatMode == RepeatMode.One) return true;

        int next = ComputeNextIndex(_currentIndex, _queue.Count, RepeatMode == RepeatMode.All, userInitiated);
        if (next < 0) return false;

        string? sealedId = Volatile.Read(ref _sealedFailedTrackId);
        if (sealedId != null && string.Equals(_queue[next].Id, sealedId, StringComparison.Ordinal))
        {
            return TryMoveNextSkippingTrack(sealedId);
        }

        // Loop transition on RepeatMode.All: in auto-shuffle mode, the queue is reshuffled
        if (next <= _currentIndex && ShuffleEnabled && _queue.Count > 1)
        {
            ApplyShuffleInPlace(preserveCurrentAtStart: false);
            _queueMutatedByNavigation = true;
        }

        _currentIndex = next;
        return true;
    }

    /// <summary>
    /// Pure arithmetic for moving one step forward in the queue, ignoring RepeatMode.One.
    /// Returns the index of the next track, or -1 if the end of the queue is reached.
    /// </summary>
    internal static int ComputeNextIndex(int currentIndex, int count, bool repeatAll, bool userInitiated)
    {
        if (count == 0) return -1;
        if (currentIndex + 1 < count) return currentIndex + 1;
        if (!repeatAll || (!userInitiated && count == 1)) return -1;
        return 0;
    }

    /// <summary>
    /// Pure arithmetic for recalculating the current track index after removing a queue item.
    /// Removing the current track itself shifts the index to the previous slot so that the next step forward lands on the track that followed it.
    /// </summary>
    internal static int ComputeIndexAfterRemove(int removedIndex, int currentIndex)
    {
        return removedIndex <= currentIndex ? currentIndex - 1 : currentIndex;
    }

    /// <summary>
    /// Whether the automatic track-end transition is stale: the queue has already moved to a different track (via removal of the playing track or manual transition).
    /// </summary>
    internal static bool IsStaleTrackEnd(string? endedTrackId, string? queueTrackId)
    {
        return endedTrackId != null && queueTrackId != null && !string.Equals(endedTrackId, queueTrackId, StringComparison.Ordinal);
    }

    private bool TryMovePrevious()
    {
        if (_queue.Count == 0) return false;
        if (CurrentPosition.TotalSeconds > 3) return false;
        if (_currentIndex > 0) { _currentIndex--; return true; }
        if (RepeatMode == RepeatMode.All) { _currentIndex = _queue.Count - 1; return true; }
        return false;
    }

    private bool TryMoveNextSkippingTrack(string? skippedTrackId)
    {
        if (_queue.Count <= 1 || _currentIndex < 0 || _currentIndex >= _queue.Count) return false;
        for (int step = 1; step < _queue.Count; step++)
        {
            int idx = (_currentIndex + step) % _queue.Count;
            if (_queue[idx].Id != skippedTrackId) { _currentIndex = idx; return true; }
        }
        return false;
    }

    /// <summary>Fisher-Yates shuffle in-place.</summary>
    private void ApplyShuffleInPlace(bool preserveCurrentAtStart)
    {
        if (_queue.Count < 2) return;

        if (preserveCurrentAtStart && _currentIndex >= 0 && _currentIndex < _queue.Count)
        {
            if (_currentIndex != 0) (_queue[0], _queue[_currentIndex]) = (_queue[_currentIndex], _queue[0]);
            for (int n = _queue.Count - 1; n > 1; n--)
            {
                int k = 1 + Random.Shared.Next(n);
                (_queue[k], _queue[n]) = (_queue[n], _queue[k]);
            }
            _currentIndex = 0;
        }
        else
        {
            for (int n = _queue.Count - 1; n > 0; n--)
            {
                int k = Random.Shared.Next(n + 1);
                (_queue[k], _queue[n]) = (_queue[n], _queue[k]);
            }
        }

        InvalidateQueueSnapshot();
    }

    private void InvalidateQueueSnapshot() => _queueSnapshot = null;

    #endregion
}