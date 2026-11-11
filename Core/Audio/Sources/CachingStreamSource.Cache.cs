using System.Buffers;

namespace LMP.Core.Audio.Sources;

internal sealed partial class CachingStreamSource
{
    // --- RamRangeBlock ---

    /// <summary>
    /// Reference-counted immutable data block in the RAM cache rented from the shared <see cref="ArrayPool{T}.Shared"/>.
    /// Allows concurrent reading of data from the RAM cache and asynchronous flushing to disk
    /// without allocating a second buffer (<c>diskCopy</c>) and without redundant memory copying.
    /// </summary>
    internal sealed class RamRangeBlock : IDisposable
    {
        private byte[]? _buffer;
        private int _refCount = 1;
        private int _ramCacheDisposed;

        /// <summary>Absolute offset of the start of the block within the content.</summary>
        public long StartOffset { get; }

        /// <summary>Absolute offset of the byte immediately following the last byte of the block.</summary>
        public long EndOffsetExclusive => StartOffset + Length;

        /// <summary>Actual byte length of the block data (for RAM cache).</summary>
        public int Length { get; }

        /// <summary>Rented memory slice containing exactly <see cref="Length"/> bytes.</summary>
        public Memory<byte> Memory { get; }

        /// <summary>
        /// Creates a block from an array rented from <see cref="ArrayPool{T}.Shared"/>.
        /// </summary>
        /// <param name="startOffset">Absolute offset of the block within the content.</param>
        /// <param name="rentedBuffer">Rented buffer from <see cref="ArrayPool{T}.Shared"/>.</param>
        /// <param name="actualLength">Number of bytes available for reading in the RAM cache.</param>
        public RamRangeBlock(long startOffset, byte[] rentedBuffer, int actualLength)
        {
            StartOffset = startOffset;
            _buffer = rentedBuffer;
            Length = actualLength;
            Memory = rentedBuffer.AsMemory(0, actualLength);
        }

        /// <summary>
        /// Increments the reference count for background disk writing and returns a slice of the full downloaded range.
        /// </summary>
        /// <param name="fullDownloadedLength">Full length of downloaded data to write to disk.</param>
        /// <param name="diskMemory">Memory slice for disk writing.</param>
        /// <returns><c>true</c> if the buffer was successfully retained; otherwise, <c>false</c>.</returns>
        public bool TryRetainForDiskWrite(int fullDownloadedLength, out ReadOnlyMemory<byte> diskMemory)
        {
            while (true)
            {
                int current = Volatile.Read(ref _refCount);
                if (current <= 0)
                {
                    diskMemory = default;
                    return false;
                }

                if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
                {
                    var buf = Volatile.Read(ref _buffer);
                    if (buf is null)
                    {
                        ReleaseReference();
                        diskMemory = default;
                        return false;
                    }

                    diskMemory = buf.AsMemory(0, fullDownloadedLength);
                    return true;
                }
            }
        }

        /// <summary>
        /// Releases one reference to the buffer (called upon completion of background disk writing).
        /// Returns the array to <see cref="ArrayPool{T}.Shared"/> when the count reaches zero.
        /// </summary>
        public void ReleaseReference()
        {
            if (Interlocked.Decrement(ref _refCount) == 0)
            {
                var buf = Interlocked.Exchange(ref _buffer, null);
                if (buf is not null)
                {
                    ArrayPool<byte>.Shared.Return(buf);
                }
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ramCacheDisposed, 1) != 0) return;
            ReleaseReference();
        }
    }

    // --- SlidingRamCache ---

    /// <summary>
    /// Thread-safe RAM cache of byte ranges with a sliding window around the current position.
    /// <para>
    /// Blocks are stored sorted by <see cref="RamRangeBlock.StartOffset"/>.
    /// Guarantees no overlaps: <see cref="TryAdd"/> rejects overlapping blocks.
    /// </para>
    /// </summary>
    internal sealed class SlidingRamCache : IDisposable
    {
        private readonly Lock _lock = new();
        private readonly List<RamRangeBlock> _blocks = new(32);
        private long _totalBytes;
        private long _version;

        /// <summary>Total size of data across all cache blocks (in bytes).</summary>
        public long TotalBytes
        {
            get { lock (_lock) return _totalBytes; }
        }

        /// <summary>Monotonically increasing version of the RAM cache blocks state.</summary>
        public long Version => Volatile.Read(ref _version);

        /// <summary>
        /// Adds a block to the cache while maintaining offset-sorted order.
        /// </summary>
        /// <param name="block">Block to add.</param>
        /// <returns>
        /// <c>true</c> if the block was added successfully;
        /// <c>false</c> if it overlaps with an existing block.
        /// </returns>
        public bool TryAdd(RamRangeBlock block)
        {
            lock (_lock)
            {
                int insertAt = _blocks.Count;
                for (int i = 0; i < _blocks.Count; i++)
                {
                    var current = _blocks[i];
                    if (current.EndOffsetExclusive <= block.StartOffset) continue;
                    if (block.EndOffsetExclusive <= current.StartOffset) { insertAt = i; break; }
                    return false;
                }
                _blocks.Insert(insertAt, block);
                _totalBytes += block.Length;
                Interlocked.Increment(ref _version);
                return true;
            }
        }

        /// <summary>
        /// Tries to read data starting from <paramref name="position"/> from the first matching block.
        /// </summary>
        /// <param name="position">Absolute start position for reading.</param>
        /// <param name="destination">Destination buffer.</param>
        /// <param name="read">Number of bytes copied.</param>
        /// <returns><c>true</c> if data was found and copied.</returns>
        public bool TryRead(long position, Memory<byte> destination, out int read)
        {
            lock (_lock)
            {
                for (int i = 0; i < _blocks.Count; i++)
                {
                    var current = _blocks[i];
                    if (current.StartOffset > position) break;
                    if (position >= current.EndOffsetExclusive) continue;
                    int offsetInBlock = (int)(position - current.StartOffset);
                    read = Math.Min(destination.Length, current.Length - offsetInBlock);
                    if (read <= 0) { read = 0; return false; }
                    current.Memory.Span.Slice(offsetInBlock, read).CopyTo(destination.Span);
                    return true;
                }
            }
            read = 0;
            return false;
        }

        /// <summary>
        /// Tries to synchronously read data into a <see cref="Span{T}"/> without allocating a <see cref="Memory{T}"/>.
        /// </summary>
        public bool TryReadSpan(long position, Span<byte> destination, out int read)
        {
            lock (_lock)
            {
                for (int i = 0; i < _blocks.Count; i++)
                {
                    var current = _blocks[i];
                    if (current.StartOffset > position) break;
                    if (position >= current.EndOffsetExclusive) continue;
                    int offsetInBlock = (int)(position - current.StartOffset);
                    read = Math.Min(destination.Length, current.Length - offsetInBlock);
                    if (read <= 0) { read = 0; return false; }
                    current.Memory.Span.Slice(offsetInBlock, read).CopyTo(destination);
                    return true;
                }
            }
            read = 0;
            return false;
        }

        /// <summary>
        /// Checks whether the cache contains the full range <c>[startOffset, startOffset+length)</c>.
        /// </summary>
        /// <param name="startOffset">Range start.</param>
        /// <param name="length">Range length.</param>
        /// <returns><c>true</c> if the range is fully covered by a single block.</returns>
        public bool ContainsRange(long startOffset, int length)
        {
            long endExclusive = startOffset + length;
            lock (_lock)
            {
                for (int i = 0; i < _blocks.Count; i++)
                {
                    var current = _blocks[i];
                    if (current.StartOffset > startOffset) break;
                    if (current.EndOffsetExclusive <= startOffset) continue;
                    return current.StartOffset <= startOffset && current.EndOffsetExclusive >= endExclusive;
                }
            }
            return false;
        }

        /// <summary>
        /// Returns the number of contiguous bytes from <paramref name="position"/>
        /// to the end of the first block containing that position.
        /// </summary>
        /// <param name="position">Start position.</param>
        /// <returns>Length of the available contiguous range; <c>0</c> if the position is not covered.</returns>
        public long GetContiguousBytesFrom(long position)
        {
            lock (_lock)
            {
                for (int i = 0; i < _blocks.Count; i++)
                {
                    var current = _blocks[i];
                    if (current.StartOffset > position) break;
                    if (position >= current.EndOffsetExclusive) continue;
                    return current.EndOffsetExclusive - position;
                }
            }
            return 0;
        }

        /// <summary>
        /// Returns a block-free gap around <paramref name="position"/>:
        /// from the end of the nearest block to the left to the start of the nearest block to the right.
        /// </summary>
        /// <param name="position">Position to find the gap for.</param>
        /// <param name="freeStart">End of the nearest block to the left; <c>0</c> if none.</param>
        /// <param name="freeEndExclusive">Start of the nearest block to the right; <see cref="long.MaxValue"/> if none.</param>
        /// <returns><c>false</c> if the position is already covered by a block.</returns>
        public bool TryGetFreeWindow(long position, out long freeStart, out long freeEndExclusive)
        {
            freeStart = 0;
            freeEndExclusive = long.MaxValue;
            lock (_lock)
            {
                for (int i = 0; i < _blocks.Count; i++)
                {
                    var current = _blocks[i];
                    if (current.StartOffset > position)
                    {
                        freeEndExclusive = current.StartOffset;
                        return true;
                    }

                    if (position < current.EndOffsetExclusive)
                        return false;

                    freeStart = current.EndOffsetExclusive;
                }
            }
            return true;
        }

        /// <summary>
        /// Removes the block containing <paramref name="position"/> and returns it.
        /// </summary>
        /// <param name="position">Position inside the target block.</param>
        /// <param name="block">Extracted block; <c>null</c> if not found.</param>
        /// <returns><c>true</c> if the block was found and removed.</returns>
        public bool TryRemoveContaining(long position, out RamRangeBlock? block)
        {
            lock (_lock)
            {
                for (int i = 0; i < _blocks.Count; i++)
                {
                    var current = _blocks[i];
                    if (current.StartOffset > position) break;
                    if (position >= current.EndOffsetExclusive) continue;
                    _blocks.RemoveAt(i);
                    _totalBytes -= current.Length;
                    Interlocked.Increment(ref _version);
                    block = current;
                    return true;
                }
            }
            block = null;
            return false;
        }

        /// <summary>
        /// Returns a snapshot of all cache blocks in ascending offset order.
        /// </summary>
        public RamRangeBlock[] GetRangesSnapshot() =>
             GetRangesSnapshot(out _);

        /// <summary>
        /// Atomically returns a snapshot of the RAM cache blocks and its version under a single lock.
        /// </summary>
        public RamRangeBlock[] GetRangesSnapshot(out long version)
        {
            lock (_lock)
            {
                version = _version;
                return _blocks.Count == 0 ? [] : [.. _blocks];
            }
        }

        /// <summary>
        /// Evicts blocks outside the sliding window boundaries and trims the cache down to <paramref name="maxRamBytes"/>.
        /// </summary>
        /// <param name="centerOffset">Current read position — window center.</param>
        /// <param name="evictionWindowBytes">Window half-width in bytes.</param>
        /// <param name="maxRamBytes">Absolute RAM cache limit.</param>
        public void Trim(long centerOffset, long evictionWindowBytes, long maxRamBytes)
        {
            lock (_lock)
            {
                bool modified = false;
                for (int i = _blocks.Count - 1; i >= 0; i--)
                {
                    var current = _blocks[i];
                    if (current.EndOffsetExclusive < centerOffset - evictionWindowBytes
                        || current.StartOffset > centerOffset + evictionWindowBytes)
                    {
                        _blocks.RemoveAt(i);
                        _totalBytes -= current.Length;
                        current.Dispose();
                        modified = true;
                    }
                }
                while (_totalBytes > maxRamBytes && _blocks.Count > 0)
                {
                    int removeIndex = ChooseFarthestIndex(centerOffset);
                    var removed = _blocks[removeIndex];
                    _blocks.RemoveAt(removeIndex);
                    _totalBytes -= removed.Length;
                    removed.Dispose();
                    modified = true;
                }
                if (modified)
                    Interlocked.Increment(ref _version);
            }
        }

        /// <summary>
        /// Releases all blocks and resets the byte counter.
        /// </summary>
        public void DisposeAll()
        {
            lock (_lock)
            {
                if (_blocks.Count > 0)
                {
                    for (int i = 0; i < _blocks.Count; i++) _blocks[i].Dispose();
                    _blocks.Clear();
                    Interlocked.Increment(ref _version);
                }
                _totalBytes = 0;
            }
        }

        /// <inheritdoc/>
        public void Dispose() => DisposeAll();

        // Selects the index of the block farthest from centerOffset.
        private int ChooseFarthestIndex(long centerOffset)
        {
            int chosen = 0;
            long farthestDistance = long.MinValue;
            for (int i = 0; i < _blocks.Count; i++)
            {
                long blockCenter = _blocks[i].StartOffset + (_blocks[i].Length >> 1);
                long distance = blockCenter >= centerOffset
                    ? blockCenter - centerOffset
                    : centerOffset - blockCenter;
                if (distance > farthestDistance) { farthestDistance = distance; chosen = i; }
            }
            return chosen;
        }
    }
}