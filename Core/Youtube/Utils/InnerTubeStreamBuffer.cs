using System.Buffers;

namespace LMP.Core.Youtube.Utils;

/// <summary>
/// Непрерывный буфер чтения сетевых потоков на базе <see cref="ArrayPool{T}"/>.
/// Позволяет выкачивать ответ сетевого сокета целиком в пул памяти без аллокаций в LOH (Large Object Heap)
/// и обеспечивает непрерывный буфер для <see cref="System.Text.Json.Utf8JsonReader"/> с флагом isFinalBlock: true.
/// </summary>
internal sealed class InnerTubeStreamBuffer : IDisposable
{
    private const int DefaultInitialCapacity = 128 * 1024; // 128 KB
    private const int MaxBufferSize = 16 * 1024 * 1024;    // 16 MB

    private byte[] _buffer;
    private int _validBytes;
    private bool _isDisposed;

    /// <summary>
    /// Инициализирует буфер с арендой начального сегмента памяти из <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    /// <param name="initialCapacity">Начальная ёмкость буфера в байтах.</param>
    public InnerTubeStreamBuffer(int initialCapacity = DefaultInitialCapacity)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
        _validBytes = 0;
    }

    /// <summary>
    /// Непрерывный срез валидных байт, вычитанных из сетевого потока.
    /// </summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _validBytes);

    /// <summary>
    /// Считывает весь сетевой поток в арендованный буфер из пула памяти.
    /// При необходимости буфер динамически расширяется с переарендой из пула.
    /// </summary>
    /// <param name="stream">Сетевой поток HTTP-ответа.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <exception cref="InvalidOperationException">Выбрасывается, если размер ответа превышает максимально допустимый предел.</exception>
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