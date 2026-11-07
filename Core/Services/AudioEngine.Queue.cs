using LMP.Core.Audio.Http;

namespace LMP.Core.Services;

public sealed partial class AudioEngine
{
    #region Granular Queue Events

    /// <summary>Occurs when an item in the queue is moved. Parameters: (old index, new index).</summary>
    public event Action<int, int>? OnQueueItemMoved;

    /// <summary>Occurs when a single item is inserted into the queue. Parameters: (insertion index, inserted track).</summary>
    public event Action<int, TrackInfo>? OnQueueItemInserted;

    /// <summary>Occurs when a single item is removed from the queue. Parameters: (index of removed item, removed track).</summary>
    public event Action<int, TrackInfo>? OnQueueItemRemoved;

    /// <summary>Occurs when a range of items is batch-inserted into the queue. Parameters: (start insertion index, item count).</summary>
    public event Action<int, int>? OnQueueRangeInserted;

    #endregion

    #region Queue State

    private readonly List<TrackInfo> _queue = new(64);
    private IReadOnlyList<TrackInfo>? _queueSnapshot;
    private int _currentIndex = -1;
    private bool _queueMutatedByNavigation;

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

    #endregion

    #region Public API

    /// <summary>
    /// Adds a range of tracks to the queue, filtering out duplicates.
    /// If the queue was empty, automatically starts playback of the first added track.
    /// </summary>
    /// <param name="tracks">The list of tracks to add.</param>
    /// <returns>The number of unique tracks actually added.</returns>
    public int EnqueueRangeUnique(IEnumerable<TrackInfo> tracks)
    {
        int addedCount = 0;
        int startIndex = -1;
        TrackInfo? playbackTrack = null;
        bool shouldAutoplay = false;

        lock (_queueLock)
        {
            var existingIds = _queue.Select(static t => t.Id).ToHashSet(StringComparer.Ordinal);
            var unique = new List<TrackInfo>();

            foreach (var track in tracks)
            {
                if (existingIds.Add(track.Id))
                {
                    unique.Add(track);
                    addedCount++;
                }
            }

            if (unique.Count > 0)
            {
                startIndex = _queue.Count;
                _queue.AddRange(unique);
                InvalidateQueueSnapshot();

                if (CurrentTrack == null && !IsPlaying && !IsLoading)
                {
                    _currentIndex = _queue.Count - unique.Count; // Seek to the first newly added track
                    playbackTrack = _queue[_currentIndex];
                    shouldAutoplay = true;
                }
            }
        }

        if (addedCount > 0)
        {
            RaiseOnUI(() =>
            {
                if (startIndex >= 0)
                    OnQueueRangeInserted?.Invoke(startIndex, addedCount);

                OnQueueChanged?.Invoke();
            });

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
        int insertedIndex = -1;

        lock (_queueLock)
        {
            for (int i = 0; i < _queue.Count; i++)
            {
                if (string.Equals(_queue[i].Id, track.Id, StringComparison.Ordinal))
                    return;
            }

            _queue.Add(track);
            insertedIndex = _queue.Count - 1;
            InvalidateQueueSnapshot();

            if (CurrentTrack == null && !IsPlaying && !IsLoading)
            {
                _currentIndex = _queue.Count - 1;
                playbackTrack = _queue[_currentIndex];
                shouldAutoplay = true;
            }
        }

        RaiseOnUI(() =>
        {
            if (insertedIndex >= 0)
                OnQueueItemInserted?.Invoke(insertedIndex, track);

            OnQueueChanged?.Invoke();
        });

        if (shouldAutoplay && playbackTrack != null)
        {
            ResetSealedFailedTrack();
            int session = BeginNewSession();
            EnqueueCommand(new PlayCurrentIndexCommand(session, StartPlaying: true));
        }
    }

    public void EnqueueRange(IEnumerable<TrackInfo> tracks)
    {
        int startIndex;
        int count;

        lock (_queueLock)
        {
            startIndex = _queue.Count;
            var list = tracks as IList<TrackInfo> ?? tracks.ToList();
            count = list.Count;
            if (count == 0) return;

            _queue.AddRange(list);
            InvalidateQueueSnapshot();
        }

        RaiseOnUI(() =>
        {
            OnQueueRangeInserted?.Invoke(startIndex, count);
            OnQueueChanged?.Invoke();
        });
    }

    public void ShuffleQueue()
    {
        lock (_queueLock)
        {
            if (_queue.Count < 2) return;
            ApplyShuffleInPlace(preserveCurrentAtStart: true);
        }
        RaiseOnUI(() => OnQueueChanged?.Invoke());
    }

    public void ClearQueue()
    {
        lock (_queueLock)
        {
            var current = CurrentTrack;
            _queue.Clear();
            _currentIndex = -1;
            if (current != null) { _queue.Add(current); _currentIndex = 0; }
            InvalidateQueueSnapshot();
        }
        RaiseOnUI(() => OnQueueChanged?.Invoke());
    }

    /// <summary>
    /// Removes a track from the queue. A removed currently playing track is immediately replaced by the next one according to the "Next" rules:
    /// at the end of the queue — loops if RepeatMode.All or stops; if paused, the next track is loaded without starting playback.
    /// </summary>
    public void RemoveFromQueue(TrackInfo track)
    {
        int removedIndex = -1;
        bool removedCurrent = false;
        bool canMove = false;
        // Intent to listen comes from engine state: after loading while paused, player is Stopped, and !IsPaused would start playback
        bool startPlaying = CurrentTrack != null && _playIntent;
        var trackId = track.Id;

        lock (_queueLock)
        {
            for (int i = 0; i < _queue.Count; i++)
            {
                if (_queue[i].Id == trackId) { removedIndex = i; break; }
            }

            if (removedIndex == -1) return;

            removedCurrent = removedIndex == _currentIndex;
            _queue.RemoveAt(removedIndex);
            _currentIndex = ComputeIndexAfterRemove(removedIndex, _currentIndex);
            if (removedCurrent) canMove = TryMoveNext(userInitiated: true);
            InvalidateQueueSnapshot();
        }

        RaiseOnUI(() =>
        {
            OnQueueItemRemoved?.Invoke(removedIndex, track);
            OnQueueChanged?.Invoke();
        });

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

    public void MoveQueueItem(int from, int to)
    {
        bool moved = false;

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
            moved = true;
        }

        if (moved)
        {
            RaiseOnUI(() =>
            {
                OnQueueItemMoved?.Invoke(from, to);
                OnQueueChanged?.Invoke();
            });
        }
    }

    #endregion

    #region Pre-warm

    /// <summary>
    /// Speculatively prewarms CDN connections for upcoming tracks in the queue.
    /// </summary>
    private void PreWarmNextTracksInQueue(int currentIndex, HttpClient httpClient, CancellationToken ct)
    {
        const int LookaheadCount = 2;

        var queue = Queue;

        for (int i = 1; i <= LookaheadCount; i++)
        {
            int idx = currentIndex + i;
            if (idx >= queue.Count)
                break;

            var next = queue[idx];

            if (AudioSourceFactory.FindAnyCachedTrack(next.Id) != null)
                continue;

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
                Log.Debug($"[AudioEngine] Lookahead CDN prewarm queue[{idx}]: {next.Id}");
            }
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