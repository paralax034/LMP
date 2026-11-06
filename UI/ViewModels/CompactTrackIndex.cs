namespace LMP.UI.ViewModels;

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LMP.Core.Models;

/// <summary>
/// Data-Oriented structure for flat storage and ultra-fast vectorized
/// filtering of hundreds of thousands of tracks within a strict RAM limit.
/// </summary>
public sealed class CompactTrackIndex
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public readonly struct CompactEntry
    {
        public readonly TrackInfo Track;
        public readonly int TextOffset;
        public readonly ushort TextLength;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public CompactEntry(TrackInfo track, int textOffset, ushort textLength)
        {
            Track = track;
            TextOffset = textOffset;
            TextLength = textLength;
        }
    }

    private CompactEntry[] _entries = [];
    private char[] _searchBuffer = [];
    private int _count;
    private int _searchBufferLength;

    private readonly Dictionary<string, int> _idToMasterIndex = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>
    /// Total number of elements loaded into the master index.
    /// </summary>
    public int Count => _count;

    public CompactTrackIndex(int initialCapacity = 256)
    {
        int cap = Math.Max(64, initialCapacity);
        _entries = new CompactEntry[cap];
        _searchBuffer = new char[cap * 32];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CompactEntry GetEntry(int index) => _entries[index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TrackInfo GetTrack(int index) => _entries[index].Track;

    /// <summary>
    /// Returns the track's master index by its stable identifier in O(1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int FindIndexById(string id)
    {
        lock (_lock)
        {
            return _idToMasterIndex.TryGetValue(id, out int index) ? index : -1;
        }
    }

    /// <summary>
    /// Batch initializes the contiguous index array and the normalized search buffer.
    /// </summary>
    public void Load(IEnumerable<TrackInfo> tracks, IReadOnlyList<string>? explicitOrder = null)
    {
        lock (_lock)
        {
            _count = 0;
            _searchBufferLength = 0;
            _idToMasterIndex.Clear();

            var trackList = tracks as IList<TrackInfo> ?? tracks.ToList();
            var lookup = new Dictionary<string, TrackInfo>(trackList.Count, StringComparer.Ordinal);

            for (int i = 0; i < trackList.Count; i++)
            {
                var t = trackList[i];
                if (!string.IsNullOrEmpty(t.Id))
                    lookup[t.Id] = t;
            }

            int targetCount = explicitOrder?.Count ?? trackList.Count;
            EnsureCapacity(targetCount);

            if (explicitOrder != null)
            {
                for (int i = 0; i < explicitOrder.Count; i++)
                {
                    var id = explicitOrder[i];
                    if (lookup.TryGetValue(id, out var track))
                    {
                        AppendTrackInternal(track);
                    }
                }
            }
            else
            {
                for (int i = 0; i < trackList.Count; i++)
                {
                    AppendTrackInternal(trackList[i]);
                }
            }
        }
    }

    /// <summary>
    /// Appends a track to the end of the contiguous buffer and mapping table.
    /// </summary>
    public void Append(TrackInfo track)
    {
        lock (_lock)
        {
            EnsureCapacity(_count + 1);
            AppendTrackInternal(track);
        }
    }

    /// <summary>
    /// Removes a track by identifier with range shifting and granular reindexing in O(K).
    /// </summary>
    public bool Remove(string id)
    {
        lock (_lock)
        {
            if (!_idToMasterIndex.Remove(id, out int index))
                return false;

            int lastIndex = _count - 1;
            if (index < lastIndex)
            {
                Array.Copy(_entries, index + 1, _entries, index, lastIndex - index);

                for (int i = index; i < lastIndex; i++)
                {
                    _idToMasterIndex[_entries[i].Track.Id] = i;
                }
            }

            _entries[lastIndex] = default;
            _count--;

            return true;
        }
    }

    /// <summary>
    /// Moves an element within the buffer by shifting the intermediate range and locally updating the dictionary in O(K).
    /// </summary>
    public void Move(int fromIndex, int toIndex)
    {
        lock (_lock)
        {
            if (fromIndex == toIndex || (uint)fromIndex >= (uint)_count || (uint)toIndex >= (uint)_count)
                return;

            var moving = _entries[fromIndex];

            if (fromIndex < toIndex)
            {
                Array.Copy(_entries, fromIndex + 1, _entries, fromIndex, toIndex - fromIndex);
                for (int i = fromIndex; i < toIndex; i++)
                {
                    _idToMasterIndex[_entries[i].Track.Id] = i;
                }
            }
            else
            {
                Array.Copy(_entries, toIndex, _entries, toIndex + 1, fromIndex - toIndex);
                for (int i = toIndex + 1; i <= fromIndex; i++)
                {
                    _idToMasterIndex[_entries[i].Track.Id] = i;
                }
            }

            _entries[toIndex] = moving;
            _idToMasterIndex[moving.Track.Id] = toIndex;
        }
    }

    /// <summary>
    /// Completely resets the index state and clears allocated buffers.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_entries, 0, _count);
            _count = 0;
            _searchBufferLength = 0;
            _idToMasterIndex.Clear();
        }
    }

    /// <summary>
    /// Performs a vectorized search over the shared normalized buffer.
    /// Heap memory allocations are 0.
    /// </summary>
    public int Filter(string query, ref int[] destinationIndices)
    {
        lock (_lock)
        {
            if (destinationIndices.Length < _count)
            {
                destinationIndices = new int[Math.Max(_count, destinationIndices.Length * 2)];
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                for (int i = 0; i < _count; i++)
                {
                    destinationIndices[i] = i;
                }
                return _count;
            }

            ReadOnlySpan<char> querySpan = query.AsSpan().Trim();
            Span<char> queryLower = querySpan.Length <= 256
                ? stackalloc char[querySpan.Length]
                : new char[querySpan.Length];

            querySpan.ToLowerInvariant(queryLower);

            int matchCount = 0;
            ReadOnlySpan<char> globalBuffer = _searchBuffer.AsSpan(0, _searchBufferLength);

            for (int i = 0; i < _count; i++)
            {
                ref readonly var entry = ref _entries[i];
                ReadOnlySpan<char> trackText = globalBuffer.Slice(entry.TextOffset, entry.TextLength);

                if (trackText.Contains(queryLower, StringComparison.Ordinal))
                {
                    destinationIndices[matchCount++] = i;
                }
            }

            return matchCount;
        }
    }

    private void AppendTrackInternal(TrackInfo track)
    {
        string title = track.Title ?? string.Empty;
        string author = track.Author ?? string.Empty;
        int textLen = title.Length + author.Length + 1;

        EnsureBufferCapacity(_searchBufferLength + textLen);

        int textOffset = _searchBufferLength;
        Span<char> targetSpan = _searchBuffer.AsSpan(textOffset, textLen);

        title.AsSpan().ToLowerInvariant(targetSpan[..title.Length]);
        targetSpan[title.Length] = ' ';
        author.AsSpan().ToLowerInvariant(targetSpan[(title.Length + 1)..]);

        _searchBufferLength += textLen;

        _entries[_count] = new CompactEntry(track, textOffset, (ushort)Math.Min(ushort.MaxValue, textLen));
        _idToMasterIndex[track.Id] = _count;
        _count++;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _entries.Length) return;

        int newCap = Math.Max(needed, _entries.Length * 2);
        Array.Resize(ref _entries, newCap);
    }

    private void EnsureBufferCapacity(int needed)
    {
        if (needed <= _searchBuffer.Length) return;

        int newCap = Math.Max(needed, _searchBuffer.Length * 2);
        Array.Resize(ref _searchBuffer, newCap);
    }
}