using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// Extremely fast Lock-Free ring buffer for the Single-Producer / Single-Consumer scenario
/// using memory pooling via <see cref="ArrayPool{T}.Shared"/> (0 B allocations in LOH when recreated).
/// </summary>
internal sealed class LockFreeRingBuffer<T> : IDisposable where T : unmanaged
{
    private T[]? _buffer;
    private readonly int _mask;
    private readonly int _capacity;
    private volatile int _frameAlignment;

    private ProducerState _producer;
    private ConsumerState _consumer;

    [StructLayout(LayoutKind.Sequential, Size = 128)]
    private struct ProducerState
    {
        /// <summary>Absolute counter of written elements (advanced by producer).</summary>
        public int Head;
        /// <summary>Cached Tail value from consumer.</summary>
        public int CachedTail;
    }

    [StructLayout(LayoutKind.Sequential, Size = 128)]
    private struct ConsumerState
    {
        /// <summary>Absolute counter of read elements (advanced by consumer).</summary>
        public int Tail;
        /// <summary>Cached Head value from producer.</summary>
        public int CachedHead;
    }

    public LockFreeRingBuffer(int requestedCapacity, int frameAlignment = 1)
    {
        _capacity = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(requestedCapacity, 16));
        _mask = _capacity - 1;
        _frameAlignment = Math.Max(1, frameAlignment);
        _buffer = ArrayPool<T>.Shared.Rent(_capacity);
    }

    internal void SetFrameAlignment(int frameAlignment) =>
        _frameAlignment = Math.Max(1, frameAlignment);

    /// <summary>Current number of elements in the buffer.</summary>
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => unchecked(Volatile.Read(ref _producer.Head) - Volatile.Read(ref _consumer.Tail));
    }

    /// <summary>Buffer capacity (always a power of two).</summary>
    public int Capacity => _capacity;

    /// <summary>Available space in the buffer (1 slot reserved).</summary>
    public int Available
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _capacity - 1 - Count;
    }

    /// <summary>
    /// Locally cached available space.
    /// Does not perform cross-core reads (no Volatile.Read(Tail)), safe for Producer.
    /// </summary>
    public int ProducerCachedAvailable
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _capacity - 1 - unchecked(_producer.Head - _producer.CachedTail);
    }

    /// <summary>Buffer is empty.</summary>
    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _producer.Head) == Volatile.Read(ref _consumer.Tail);
    }

    /// <summary>
    /// Writes data to the buffer.
    /// Must be called ONLY by Producer (decoder loop).
    /// </summary>
    public int Write(ReadOnlySpan<T> data)
    {
        T[]? buffer = _buffer;
        if (buffer is null) return 0;

        int head = _producer.Head;
        int tail = _producer.CachedTail;
        int available = _capacity - 1 - unchecked(head - tail);

        if (available < data.Length)
        {
            tail = Volatile.Read(ref _consumer.Tail);
            _producer.CachedTail = tail;
            available = _capacity - 1 - unchecked(head - tail);
        }

        int toWrite = Math.Min(data.Length, available);
        int align = _frameAlignment;
        if (align > 1)
            toWrite = align == 2 ? (toWrite & ~1) : (toWrite - (toWrite % align));
        if (toWrite == 0) return 0;

        ref T bufferBase = ref MemoryMarshal.GetArrayDataReference(buffer);
        int headIndex = head & _mask;
        int firstPart = Math.Min(toWrite, _capacity - headIndex);

        data[..firstPart].CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref bufferBase, headIndex), firstPart));

        int secondPart = toWrite - firstPart;
        if (secondPart > 0)
        {
            data.Slice(firstPart, secondPart).CopyTo(MemoryMarshal.CreateSpan(ref bufferBase, secondPart));
        }

        Volatile.Write(ref _producer.Head, unchecked(head + toWrite));
        return toWrite;
    }

    /// <summary>
    /// Reads data from the buffer.
    /// Must be called ONLY by Consumer (audio callback / WinAudio).
    /// </summary>
    public int Read(Span<T> output)
    {
        T[]? buffer = _buffer;
        if (buffer is null) return 0;

        int tail = _consumer.Tail;
        int head = _consumer.CachedHead;
        int count = unchecked(head - tail);

        if (count < output.Length)
        {
            head = Volatile.Read(ref _producer.Head);
            _consumer.CachedHead = head;
            count = unchecked(head - tail);
        }

        int toRead = Math.Min(output.Length, count);
        int align = _frameAlignment;
        if (align > 1)
            toRead = align == 2 ? (toRead & ~1) : (toRead - (toRead % align));
        if (toRead == 0) return 0;

        ref T bufferBase = ref MemoryMarshal.GetArrayDataReference(buffer);
        int tailIndex = tail & _mask;
        int firstPart = Math.Min(toRead, _capacity - tailIndex);

        MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref bufferBase, tailIndex), firstPart)
            .CopyTo(output[..firstPart]);

        int secondPart = toRead - firstPart;
        if (secondPart > 0)
        {
            MemoryMarshal.CreateReadOnlySpan(ref bufferBase, secondPart)
                .CopyTo(output.Slice(firstPart, secondPart));
        }

        Volatile.Write(ref _consumer.Tail, unchecked(tail + toRead));
        return toRead;
    }

    /// <summary>
    /// Clears the buffer. Call ONLY when playback is stopped!
    /// </summary>
    public void Clear()
    {
        Volatile.Write(ref _producer.Head, 0);
        Volatile.Write(ref _consumer.Tail, 0);
        _consumer.CachedHead = 0;
        _producer.CachedTail = 0;
    }

    /// <summary>
    /// Returns the rented array back to <see cref="ArrayPool{T}.Shared"/>.
    /// Call after Producer and Consumer have completely stopped.
    /// </summary>
    public void Dispose()
    {
        T[]? buf = Interlocked.Exchange(ref _buffer, null);
        if (buf is not null)
        {
            ArrayPool<T>.Shared.Return(buf);
        }
    }
}