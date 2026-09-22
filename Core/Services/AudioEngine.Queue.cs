using LMP.Core.Audio.Http;

namespace LMP.Core.Services;

public sealed partial class AudioEngine
{
    #region Granular Queue Events

    /// <summary>Событие перемещения элемента в очереди. Параметры: (старый индекс, новый индекс).</summary>
    public event Action<int, int>? OnQueueItemMoved;

    /// <summary>Событие вставки одиночного элемента в очередь. Параметры: (индекс вставки, вставленный трек).</summary>
    public event Action<int, TrackInfo>? OnQueueItemInserted;

    /// <summary>Событие удаления одиночного элемента из очереди. Параметры: (индекс удаленного элемента, удаленный трек).</summary>
    public event Action<int, TrackInfo>? OnQueueItemRemoved;

    /// <summary>Событие пакетной вставки диапазона элементов. Параметры: (начальный индекс вставки, количество элементов).</summary>
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
    /// Добавляет в очередь диапазон треков, отфильтровывая дубликаты.
    /// Если очередь была пуста, автоматически запускает проигрывание первого добавленного трека.
    /// </summary>
    /// <param name="tracks">Список добавляемых треков.</param>
    /// <returns>Количество фактически добавленных уникальных треков.</returns>
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
                    _currentIndex = _queue.Count - unique.Count; // Позиционируемся на первый из новых
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
                _ = PlayTrackCoreAsync(playbackTrack, session, GetSessionToken());
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
            if (_queue.Any(t => t.Id == track.Id)) return;
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
            _ = PlayTrackCoreAsync(playbackTrack, session, GetSessionToken());
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

    public void RemoveFromQueue(TrackInfo track)
    {
        bool needStop = false;
        int removedIndex = -1;

        lock (_queueLock)
        {
            int idx = _queue.FindIndex(t => t.Id == track.Id);
            if (idx == -1) return;
            removedIndex = idx;
            if (idx == _currentIndex) { needStop = _queue.Count == 1; if (idx == _queue.Count - 1) _currentIndex--; }
            else if (idx < _currentIndex) _currentIndex--;
            _queue.RemoveAt(idx);
            InvalidateQueueSnapshot();
        }

        if (removedIndex >= 0)
        {
            RaiseOnUI(() =>
            {
                OnQueueItemRemoved?.Invoke(removedIndex, track);
                OnQueueChanged?.Invoke();
            });
        }

        if (needStop) Stop();
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
    /// Спекулятивно прогревает CDN-соединения для следующих треков в очереди.
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

        if (_currentIndex + 1 < _queue.Count) { _currentIndex++; return true; }

        if (RepeatMode == RepeatMode.All)
        {
            if (!userInitiated && _queue.Count == 1) return false;
            if (ShuffleEnabled && _queue.Count > 1)
            {
                ApplyShuffleInPlace(preserveCurrentAtStart: false);
                _queueMutatedByNavigation = true;
            }
            _currentIndex = 0;
            return true;
        }
        return false;
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