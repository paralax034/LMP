namespace LMP.UI.ViewModels;

using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Threading;
using LMP.Core.Models;
using LMP.UI.Features.Shared;
using LMP.UI.Services;

/// <summary>
/// Адаптер виртуализированного списка с поддержкой кольцевого пула ViewModel
/// и хранением состояния выбора по стабильным идентификаторам треков (исключает фантомы).
/// </summary>
public sealed class VirtualTrackList : IVirtualTrackList
{
    private const int MaxActiveVms = 192;
    private const int EvictionKeepRadius = 64;

    private readonly CompactTrackIndex _index = new();
    private readonly TrackViewModelFactory _vmFactory;
    private readonly Action<TrackInfo>? _onPlay;
    private readonly List<int> _evictionBuffer = new(MaxActiveVms);

    private int[] _filteredIndices = [];
    private int _filteredCount;

    private readonly Dictionary<int, TrackItemViewModel> _activeVms = new(MaxActiveVms);
    private readonly HashSet<string> _selectedTrackIds = new(StringComparer.Ordinal);

    private string? _currentActiveTrackId;
    private bool _currentIsPlaying;

    private bool _isDisposed;

    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public event Action? SelectionChanged;

    public int TotalCount => _index.Count;
    public int FilteredCount => _filteredCount;
    public int SelectedCount => _selectedTrackIds.Count;

    public int Count => _filteredCount;
    public bool IsReadOnly => false;
    public bool IsFixedSize => false;
    public bool IsSynchronized => false;
    public object SyncRoot => this;

    public Func<IReadOnlyList<TrackInfo>>? SelectionProvider { get; set; }
    public bool IsPlaylistContext { get; set; }
    public bool IsQueueContext { get; set; }
    public string? SourceContextId { get; set; }
    public Action<IReadOnlyList<TrackInfo>>? RemoveFromPlaylistAction { get; set; }
    public Action<TrackInfo>? StartRadioAction { get; set; }

    public VirtualTrackList(TrackViewModelFactory vmFactory, Action<TrackInfo>? onPlay = null)
    {
        ArgumentNullException.ThrowIfNull(vmFactory);
        _vmFactory = vmFactory;
        _onPlay = onPlay;
    }

    /// <summary>
    /// Возвращает или материализует модель представления трека для указанного визуального индекса из пула.
    /// </summary>
    public TrackItemViewModel this[int visualIndex]
    {
        get
        {
            if ((uint)visualIndex >= (uint)_filteredCount)
                throw new ArgumentOutOfRangeException(nameof(visualIndex));

            int masterIndex = _filteredIndices[visualIndex];

            if (_activeVms.TryGetValue(masterIndex, out var cachedVm))
            {
                return cachedVm;
            }

            var track = _index.GetTrack(masterIndex);
            var vm = _vmFactory.GetOrCreate(track, _onPlay);

            vm.IsSelected = _selectedTrackIds.Contains(track.Id);
            vm.IsPlaylistContext = IsPlaylistContext;
            vm.IsQueueContext = IsQueueContext;
            vm.SourceContextId = SourceContextId;
            vm.SelectionProvider = SelectionProvider ?? GetSelectedTracks;
            vm.RemoveFromPlaylistAction = RemoveFromPlaylistAction;
            vm.StartRadioAction = StartRadioAction;

            vm.PropertyChanged += HandleVmPropertyChanged;

            if (string.Equals(track.Id, _currentActiveTrackId, StringComparison.Ordinal))
            {
                vm.SetActive(true, _currentIsPlaying);
            }

            _activeVms[masterIndex] = vm;

            if (_activeVms.Count > MaxActiveVms)
            {
                EvictDistantViewModels(visualIndex);
            }

            return vm;
        }
        set => throw new NotSupportedException();
    }

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    private void HandleVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrackItemViewModel.IsSelected) && sender is TrackItemViewModel vm)
        {
            bool changed = vm.IsSelected
                ? _selectedTrackIds.Add(vm.Id)
                : _selectedTrackIds.Remove(vm.Id);

            if (changed)
                NotifySelectionChanged();
        }
    }

    /// <summary>
    /// Инициализирует список новой коллекцией с сохранением выбранных треков за O(S).
    /// </summary>
    public void SetTracks(IEnumerable<TrackInfo> tracks, IReadOnlyList<string>? explicitOrder = null,
        bool preserveSelection = false)
    {
        ClearActiveViewModels();

        _index.Load(tracks, explicitOrder);

        if (!preserveSelection)
        {
            _selectedTrackIds.Clear();
        }
        else if (_selectedTrackIds.Count > 0)
        {
            _selectedTrackIds.RemoveWhere(id => _index.FindIndexById(id) < 0);
        }

        RebuildFilteredIndices(string.Empty);
        RaiseReset();
        NotifySelectionChanged();
    }

    public void AppendTracks(IEnumerable<TrackInfo> newTracks)
    {
        foreach (var track in newTracks)
        {
            _index.Append(track);
        }

        RebuildFilteredIndices(string.Empty);
        RaiseReset();
    }

    public void ApplyFilter(string query)
    {
        ClearActiveViewModels();
        RebuildFilteredIndices(query);
        RaiseReset();
        NotifySelectionChanged();
    }

    /// <summary>
    /// Перемещает элемент визуального порядка с биективным сохранением ключей активных ViewModel.
    /// </summary>
    public void MoveTrack(int oldVisualIndex, int newVisualIndex)
    {
        if (oldVisualIndex == newVisualIndex ||
            (uint)oldVisualIndex >= (uint)_filteredCount ||
            (uint)newVisualIndex >= (uint)_filteredCount)
            return;

        int masterOld = _filteredIndices[oldVisualIndex];
        int masterNew = _filteredIndices[newVisualIndex];

        _index.Move(masterOld, masterNew);

        int movingMaster = _filteredIndices[oldVisualIndex];
        if (oldVisualIndex < newVisualIndex)
        {
            Array.Copy(_filteredIndices, oldVisualIndex + 1, _filteredIndices, oldVisualIndex,
                newVisualIndex - oldVisualIndex);
        }
        else
        {
            Array.Copy(_filteredIndices, newVisualIndex, _filteredIndices, newVisualIndex + 1,
                oldVisualIndex - newVisualIndex);
        }

        _filteredIndices[newVisualIndex] = movingMaster;

        if (_filteredCount != _index.Count)
        {
            for (int i = 0; i < _filteredCount; i++)
            {
                if (i == newVisualIndex) continue;
                int currentMaster = _filteredIndices[i];
                if (masterOld < masterNew)
                {
                    if (currentMaster > masterOld && currentMaster <= masterNew)
                        _filteredIndices[i]--;
                }
                else
                {
                    if (currentMaster >= masterNew && currentMaster < masterOld)
                        _filteredIndices[i]++;
                }
            }
        }
        else
        {
            for (int i = 0; i < _filteredCount; i++)
            {
                _filteredIndices[i] = i;
            }
        }

        if (_activeVms.Count > 0)
        {
            var remapped = new Dictionary<int, TrackItemViewModel>(_activeVms.Count);
            foreach (var kvp in _activeVms)
            {
                int oldKey = kvp.Key;
                int newKey;

                if (oldKey == masterOld)
                {
                    newKey = masterNew;
                }
                else if (masterOld < masterNew && oldKey > masterOld && oldKey <= masterNew)
                {
                    newKey = oldKey - 1;
                }
                else if (masterOld > masterNew && oldKey >= masterNew && oldKey < masterOld)
                {
                    newKey = oldKey + 1;
                }
                else
                {
                    newKey = oldKey;
                }

                remapped[newKey] = kvp.Value;
            }

            _activeVms.Clear();
            foreach (var kvp in remapped)
            {
                _activeVms[kvp.Key] = kvp.Value;
            }
        }

        RaiseCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Move,
            this[newVisualIndex],
            newVisualIndex,
            oldVisualIndex));
    }

    /// <summary>
    /// Удаляет трек из мастер-индекса со сдвигом ключей пула активных моделей.
    /// </summary>
    public bool RemoveTrack(string trackId)
    {
        int masterIndex = _index.FindIndexById(trackId);
        if (masterIndex < 0) return false;

        int visualIndex = -1;
        for (int i = 0; i < _filteredCount; i++)
        {
            if (_filteredIndices[i] == masterIndex)
            {
                visualIndex = i;
                break;
            }
        }

        bool removedSelection = _selectedTrackIds.Remove(trackId);

        if (_activeVms.Remove(masterIndex, out var vm))
        {
            vm.PropertyChanged -= HandleVmPropertyChanged;
            vm.Dispose();
        }

        _index.Remove(trackId);

        if (visualIndex >= 0)
        {
            Array.Copy(_filteredIndices, visualIndex + 1, _filteredIndices, visualIndex,
                _filteredCount - visualIndex - 1);
            _filteredCount--;

            for (int i = 0; i < _filteredCount; i++)
            {
                if (_filteredIndices[i] > masterIndex)
                    _filteredIndices[i]--;
            }

            if (_activeVms.Count > 0)
            {
                var remapped = new Dictionary<int, TrackItemViewModel>(_activeVms.Count);
                foreach (var kvp in _activeVms)
                {
                    int key = kvp.Key;
                    int newKey = key > masterIndex ? key - 1 : key;
                    remapped[newKey] = kvp.Value;
                }

                _activeVms.Clear();
                foreach (var kvp in remapped)
                {
                    _activeVms[kvp.Key] = kvp.Value;
                }
            }

            RaiseReset();
        }

        if (removedSelection)
            NotifySelectionChanged();

        return true;
    }

    public int IndexOfTrackId(string? trackId)
    {
        if (string.IsNullOrEmpty(trackId) || _filteredCount == 0) return -1;

        int masterIndex = _index.FindIndexById(trackId);
        if (masterIndex < 0) return -1;

        if (_filteredCount == _index.Count)
            return masterIndex;

        for (int i = 0; i < _filteredCount; i++)
        {
            if (_filteredIndices[i] == masterIndex)
                return i;
        }

        return -1;
    }

    public TrackInfo? GetTrackAt(int visualIndex)
    {
        if ((uint)visualIndex >= (uint)_filteredCount) return null;
        return _index.GetTrack(_filteredIndices[visualIndex]);
    }

    public TrackItemViewModel? TryGetVm(string trackId)
    {
        int masterIndex = _index.FindIndexById(trackId);
        if (masterIndex < 0) return null;

        _activeVms.TryGetValue(masterIndex, out var vm);
        return vm;
    }

    public bool IsIndexSelected(int visualIndex)
    {
        var track = GetTrackAt(visualIndex);
        return track != null && _selectedTrackIds.Contains(track.Id);
    }

    public void SetIndexSelected(int visualIndex, bool isSelected)
    {
        var track = GetTrackAt(visualIndex);
        if (track == null) return;

        bool changed = isSelected ? _selectedTrackIds.Add(track.Id) : _selectedTrackIds.Remove(track.Id);
        if (!changed) return;

        int masterIndex = _filteredIndices[visualIndex];
        if (_activeVms.TryGetValue(masterIndex, out var vm))
        {
            vm.IsSelected = isSelected;
        }

        NotifySelectionChanged();
    }

    public void ToggleIndexSelected(int visualIndex)
    {
        var track = GetTrackAt(visualIndex);
        if (track == null) return;

        bool isSelected = !_selectedTrackIds.Contains(track.Id);
        SetIndexSelected(visualIndex, isSelected);
    }

    public void SelectAll()
    {
        for (int i = 0; i < _filteredCount; i++)
        {
            var track = _index.GetTrack(_filteredIndices[i]);
            if (track != null)
                _selectedTrackIds.Add(track.Id);
        }

        foreach (var vm in _activeVms.Values)
        {
            vm.IsSelected = true;
        }

        NotifySelectionChanged();
    }

    public void ClearSelection()
    {
        if (_selectedTrackIds.Count == 0) return;

        _selectedTrackIds.Clear();

        foreach (var vm in _activeVms.Values)
        {
            vm.IsSelected = false;
        }

        NotifySelectionChanged();
    }

    public void SelectRange(int fromVisualIndex, int toVisualIndex, bool addToExisting)
    {
        if (_filteredCount == 0) return;

        int start = Math.Clamp(Math.Min(fromVisualIndex, toVisualIndex), 0, _filteredCount - 1);
        int end = Math.Clamp(Math.Max(fromVisualIndex, toVisualIndex), 0, _filteredCount - 1);

        if (!addToExisting)
        {
            _selectedTrackIds.Clear();
            foreach (var vm in _activeVms.Values)
                vm.IsSelected = false;
        }

        for (int i = start; i <= end; i++)
        {
            var track = GetTrackAt(i);
            if (track != null)
            {
                _selectedTrackIds.Add(track.Id);
                int masterIdx = _filteredIndices[i];
                if (_activeVms.TryGetValue(masterIdx, out var vm))
                    vm.IsSelected = true;
            }
        }

        NotifySelectionChanged();
    }

    public List<TrackInfo> GetSelectedTracks()
    {
        if (_selectedTrackIds.Count == 0) return [];

        var list = new List<TrackInfo>(_selectedTrackIds.Count);
        for (int i = 0; i < _filteredCount; i++)
        {
            var track = _index.GetTrack(_filteredIndices[i]);
            if (track != null && _selectedTrackIds.Contains(track.Id))
            {
                list.Add(track);
            }
        }

        return list;
    }

    public List<TrackItemViewModel> GetSelectedViewModelsOrdered()
    {
        var list = new List<TrackItemViewModel>(_activeVms.Count);
        for (int i = 0; i < _filteredCount; i++)
        {
            int masterIndex = _filteredIndices[i];
            var track = _index.GetTrack(masterIndex);
            if (track != null && _selectedTrackIds.Contains(track.Id))
            {
                if (_activeVms.TryGetValue(masterIndex, out var vm))
                    list.Add(vm);
            }
        }

        return list;
    }

    public void UpdatePlaybackState(TrackInfo? currentTrack, bool isPlaying)
    {
        _currentActiveTrackId = currentTrack?.Id;
        _currentIsPlaying = isPlaying;

        foreach (var kvp in _activeVms)
        {
            var vm = kvp.Value;
            bool isThis = string.Equals(vm.Id, _currentActiveTrackId, StringComparison.Ordinal);
            vm.SetActive(isThis, isThis && isPlaying);
        }
    }

    public void UpdateDownloadProgress(string trackId, float progress)
    {
        var vm = TryGetVm(trackId);
        vm?.SetDownloadState(true, progress);
    }

    public void UpdateDownloadCompleted(string trackId, bool ok, string? path)
    {
        var vm = TryGetVm(trackId);
        vm?.SetDownloadState(false, 0f);
    }

    public void UpdateCacheStatus(string trackId, AudioFormat format, int bitrate)
    {
        var vm = TryGetVm(trackId);
        if (vm != null && !vm.Track.IsCached)
        {
            vm.Track.MarkAsCached(format, bitrate);
        }
    }

    public int IndexOf(TrackItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        int masterIndex = _index.FindIndexById(item.Id);
        if (masterIndex < 0) return -1;

        if (_filteredCount == _index.Count)
            return masterIndex;

        for (int i = 0; i < _filteredCount; i++)
        {
            if (_filteredIndices[i] == masterIndex)
                return i;
        }

        return -1;
    }

    public bool Contains(TrackItemViewModel item) => IndexOf(item) >= 0;

    public void CopyTo(TrackItemViewModel[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        for (int i = 0; i < _filteredCount; i++)
        {
            array[arrayIndex + i] = this[i];
        }
    }

    void ICollection.CopyTo(Array array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        for (int i = 0; i < _filteredCount; i++)
        {
            array.SetValue(this[i], index + i);
        }
    }

    public IEnumerator<TrackItemViewModel> GetEnumerator()
    {
        for (int i = 0; i < _filteredCount; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => SetTracks([]);
    public bool Contains(object? value) => value is TrackItemViewModel vm && Contains(vm);
    public int IndexOf(object? value) => value is TrackItemViewModel vm ? IndexOf(vm) : -1;
    public void Insert(int index, object? value) => throw new NotSupportedException();

    public void Remove(object? value)
    {
        if (value is TrackItemViewModel vm) RemoveTrack(vm.Id);
    }

    public void RemoveAt(int index)
    {
        var t = GetTrackAt(index);
        if (t != null) RemoveTrack(t.Id);
    }

    public void Add(TrackItemViewModel item) => throw new NotSupportedException();
    public bool Remove(TrackItemViewModel item) => item != null && RemoveTrack(item.Id);
    public void Insert(int index, TrackItemViewModel item) => throw new NotSupportedException();

    private void RebuildFilteredIndices(string query)
    {
        _filteredCount = _index.Filter(query, ref _filteredIndices);
    }

    private void EvictDistantViewModels(int currentVisualIndex)
    {
        int minVisual = Math.Max(0, currentVisualIndex - EvictionKeepRadius);
        int maxVisual = Math.Min(_filteredCount - 1, currentVisualIndex + EvictionKeepRadius);

        _evictionBuffer.Clear();

        bool isUnfiltered = _filteredCount == _index.Count;

        foreach (var kvp in _activeVms)
        {
            int masterIdx = kvp.Key;
            var track = _index.GetTrack(masterIdx);
            if (track != null && _selectedTrackIds.Contains(track.Id))
                continue;

            bool isVisible;
            if (isUnfiltered)
            {
                isVisible = masterIdx >= minVisual && masterIdx <= maxVisual;
            }
            else
            {
                isVisible = false;
                for (int i = minVisual; i <= maxVisual; i++)
                {
                    if (_filteredIndices[i] == masterIdx)
                    {
                        isVisible = true;
                        break;
                    }
                }
            }

            if (!isVisible)
            {
                _evictionBuffer.Add(masterIdx);
            }
        }

        for (int i = 0; i < _evictionBuffer.Count; i++)
        {
            if (_activeVms.Remove(_evictionBuffer[i], out var vm))
            {
                vm.PropertyChanged -= HandleVmPropertyChanged;
                vm.Dispose();
            }
        }
    }

    private void UnsubscribeActiveViewModels()
    {
        foreach (var vm in _activeVms.Values)
        {
            vm.PropertyChanged -= HandleVmPropertyChanged;
        }
    }

    private void ClearActiveViewModels()
    {
        UnsubscribeActiveViewModels();
        foreach (var vm in _activeVms.Values)
        {
            vm.Dispose();
        }

        _activeVms.Clear();
    }

    private void NotifySelectionChanged() => SelectionChanged?.Invoke();

    private void RaiseReset()
    {
        RaiseCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private void RaiseCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            CollectionChanged?.Invoke(this, e);
        }
        else
        {
            Dispatcher.UIThread.Post(() => CollectionChanged?.Invoke(this, e));
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        ClearActiveViewModels();
        _index.Clear();
        _selectedTrackIds.Clear();
        _filteredCount = 0;
        _filteredIndices = [];
    }
}
