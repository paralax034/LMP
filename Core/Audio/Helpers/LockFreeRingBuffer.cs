using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// Экстремально быстрый Lock-Free кольцевой буфер для сценария Single-Producer / Single-Consumer
/// с пулингом памяти через <see cref="ArrayPool{T}.Shared"/> (0 B аллокаций в LOH при пересоздании).
/// </summary>
public sealed class LockFreeRingBuffer<T> : IDisposable where T : unmanaged
{
    private T[]? _buffer;
    private readonly int _mask;
    private readonly int _capacity;

    private ProducerState _producer;
    private ConsumerState _consumer;

    [StructLayout(LayoutKind.Sequential, Size = 128)]
    private struct ProducerState
    {
        /// <summary>Абсолютный счётчик записанных элементов (producer двигает вперёд).</summary>
        public int Head;
        /// <summary>Кэшированное значение Tail от consumer.</summary>
        public int CachedTail;
    }

    [StructLayout(LayoutKind.Sequential, Size = 128)]
    private struct ConsumerState
    {
        /// <summary>Абсолютный счётчик прочитанных элементов (consumer двигает вперёд).</summary>
        public int Tail;
        /// <summary>Кэшированное значение Head от producer.</summary>
        public int CachedHead;
    }

    public LockFreeRingBuffer(int requestedCapacity)
    {
        _capacity = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(requestedCapacity, 16));
        _mask = _capacity - 1;
        _buffer = ArrayPool<T>.Shared.Rent(_capacity);
    }

    /// <summary>Текущее количество элементов в буфере.</summary>
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => unchecked(Volatile.Read(ref _producer.Head) - Volatile.Read(ref _consumer.Tail));
    }

    /// <summary>Ёмкость буфера (всегда степень двойки).</summary>
    public int Capacity => _capacity;

    /// <summary>Свободное место в буфере (1 слот зарезервирован).</summary>
    public int Available
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _capacity - 1 - Count;
    }

    /// <summary>
    /// Локально кэшированное свободное место.
    /// Не делает межъядерных чтений (без Volatile.Read(Tail)), безопасно для Producer-а.
    /// </summary>
    public int ProducerCachedAvailable
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _capacity - 1 - unchecked(_producer.Head - _producer.CachedTail);
    }

    /// <summary>Буфер пуст.</summary>
    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _producer.Head) == Volatile.Read(ref _consumer.Tail);
    }

    /// <summary>
    /// Записывает данные в буфер.
    /// Вызывается ТОЛЬКО Producer-ом (decoder loop).
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
    /// Читает данные из буфера.
    /// Вызывается ТОЛЬКО Consumer-ом (audio callback / WinAudio).
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
    /// Очищает буфер. Вызывать ТОЛЬКО при остановленном воспроизведении!
    /// </summary>
    public void Clear()
    {
        Volatile.Write(ref _producer.Head, 0);
        Volatile.Write(ref _consumer.Tail, 0);
        _consumer.CachedHead = 0;
        _producer.CachedTail = 0;
    }

    /// <summary>
    /// Возвращает арендованный массив обратно в <see cref="ArrayPool{T}.Shared"/>.
    /// Вызывать после полной остановки Producer и Consumer.
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