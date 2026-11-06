using System.Buffers;

namespace LMP.Core.Youtube.Utils;

/// <summary>
/// Contiguous network stream read buffer backed by <see cref="ArrayPool{T}"/>.
/// Allows dumping an entire network socket response into a memory pool without LOH (Large Object Heap) allocations
/// and provides a contiguous buffer for <see cref="System.Text.Json.Utf8JsonReader"/> with isFinalBlock: true.
/// </summary>
internal sealed class InnerTubeStreamBuffer : IDisposable
{
    private const int DefaultInitialCapacity = 128 * 1024; // 128 KB
    private const int MaxBufferSize = 16 * 1024 * 1024;    // 16 MB

    private byte[] _buffer;
    private int _validBytes;
    private bool _isDisposed;

    /// <summary>
    /// Initializes the buffer by renting an initial memory segment from <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    /// <param name="initialCapacity">Initial buffer capacity in bytes.</param>
    public InnerTubeStreamBuffer(int initialCapacity = DefaultInitialCapacity)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
        _validBytes = 0;
    }

    /// <summary>
    /// Contiguous slice of valid bytes read from the network stream.
    /// </summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _validBytes);

    /// <summary>
    /// Contiguous memory segment of valid bytes read from the network stream.
    /// </summary>
    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _validBytes);

    /// <summary>
    /// Reads the entire network stream into a rented buffer from the memory pool.
    /// Expands the buffer dynamically via re-renting from the pool if necessary.
    /// </summary>
    /// <param name="stream">HTTP response network stream.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <exception cref="InvalidOperationException">Thrown if the response size exceeds the maximum allowed limit.</exception>
    public async ValueTask ReadAllAsync(Stream stream, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        while (true)
        {
            if (_validBytes == _buffer.Length)
            {
                if (_buffer.Length >= MaxBufferSize)
                {
                    throw new InvalidOperationException(
                        $"InnerTube stream exceeded maximum buffer capacity ({MaxBufferSize} bytes).");
                }

                int newCapacity = Math.Min(_buffer.Length * 2, MaxBufferSize);
                var newBuffer = ArrayPool<byte>.Shared.Rent(newCapacity);
                _buffer.AsSpan(0, _validBytes).CopyTo(newBuffer);
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = newBuffer;
            }

            int bytesRead = await stream.ReadAsync(
                _buffer.AsMemory(_validBytes, _buffer.Length - _validBytes), ct).ConfigureAwait(false);

            if (bytesRead <= 0)
                break;

            _validBytes += bytesRead;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        var buf = _buffer;
        _buffer = [];
        _validBytes = 0;

        if (buf.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }
}