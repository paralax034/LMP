namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    /// <summary>
    /// Stream-обёртка над <see cref="CachingStreamSource"/> для парсеров контейнеров (WebM/MP4).
    ///
    /// <para><b>Оптимизация Position setter:</b></para>
    /// <list type="bullet">
    ///   <item><c>Position setter</c> — лёгкий <c>Volatile.Write</c>, без CTS-аллокаций</item>
    ///   <item><see cref="SeekAndCancelPendingReads"/> — явный cancel для внешнего seek</item>
    /// </list>
    ///
    /// <para><b>Контракт ReadAsync:</b> <c>0</c> возвращается только при достижении конца контента.
    /// Отмена бросает <see cref="OperationCanceledException"/>, утилизация — <see cref="ObjectDisposedException"/>.</para>
    /// </summary>
    internal sealed class AsyncCachingReadStream : Stream
    {
        private readonly CachingStreamSource _source;
        private long _position;

        /// <summary>
        /// CTS, привязанный к текущей логической позиции потока.
        /// Заменяется только через <see cref="SwapReadEpoch"/>; <see langword="null"/> означает утилизированный поток.
        /// </summary>
        private CancellationTokenSource? _readCts = new();

        /// <summary>Создаёт Stream-обёртку над источником.</summary>
        public AsyncCachingReadStream(CachingStreamSource source) => _source = source;

        /// <summary>Источник или сам поток уже утилизированы.</summary>
        private bool IsDisposed => _source._disposed || Volatile.Read(ref _readCts) is null;

        #region Stream Properties

        /// <inheritdoc/>
        public override bool CanRead => true;

        /// <inheritdoc/>
        public override bool CanSeek => true;

        /// <inheritdoc/>
        public override bool CanWrite => false;

        /// <inheritdoc/>
        public override long Length => _source._contentLength;

        /// <inheritdoc/>
        public override long Position
        {
            get => Volatile.Read(ref _position);
            set => Volatile.Write(ref _position, value);
        }

        #endregion

        #region Seek & Cancel

        /// <summary>
        /// Устанавливает позицию потока И отменяет все pending ReadAsync.
        /// Вызывается ТОЛЬКО из <see cref="SeekAsync"/> при внешнем seek.
        /// </summary>
        /// <param name="position">Новая абсолютная позиция в байтах.</param>
        internal void SeekAndCancelPendingReads(long position)
        {
            var oldCts = SwapReadEpoch();
            if (oldCts != null)
            {
                try { oldCts.Cancel(); }
                catch (ObjectDisposedException) { }

                DeferDisposeCancellationTokenSource(oldCts, 500);
            }

            Volatile.Write(ref _position, position);
        }

        /// <summary>
        /// Мгновенно отменяет активные операции чтения синхронно.
        /// </summary>
        internal void CancelActiveReads()
        {
            var oldCts = SwapReadEpoch();
            if (oldCts == null) return;

            try
            {
                // Синхронная отмена немедленно прерывает блокирующий Read() в DecoderLoop
                oldCts.Cancel();
            }
            catch (ObjectDisposedException) { }

            // Dispose откладываем, чтобы избежать ObjectDisposedException в других потоках
            DeferDisposeCancellationTokenSource(oldCts, 500);
        }

        /// <summary>
        /// Атомарно заменяет CTS текущей эпохи чтения новым и возвращает прежний.
        /// Возвращает <see langword="null"/>, если поток уже утилизирован: эпоха не воскрешается,
        /// а <see cref="SeekAndCancelPendingReads"/> не падает с NullReferenceException после Dispose.
        /// </summary>
        private CancellationTokenSource? SwapReadEpoch()
        {
            var current = Volatile.Read(ref _readCts);
            if (current == null)
                return null;

            var fresh = new CancellationTokenSource();
            while (current != null)
            {
                var observed = Interlocked.CompareExchange(ref _readCts, fresh, current);
                if (ReferenceEquals(observed, current))
                    return current;

                current = observed;
            }

            // Поток утилизирован конкурентно: новый CTS никому не достался
            fresh.Dispose();
            return null;
        }

        /// <summary>
        /// Безопасно извлекает <see cref="CancellationToken"/> из текущего <see cref="_readCts"/>.
        /// Для утилизированного потока возвращает отменённый токен.
        /// </summary>
        private CancellationToken GetCurrentReadToken()
        {
            try
            {
                return Volatile.Read(ref _readCts)?.Token ?? new CancellationToken(canceled: true);
            }
            catch (ObjectDisposedException)
            {
                return new CancellationToken(canceled: true);
            }
        }

        private static ObjectDisposedException CreateDisposedException() =>
            new(nameof(AsyncCachingReadStream));

        #endregion

        #region Read

        /// <summary>
        /// Пытается синхронно прочитать данные исключительно из RAM-кэша без блокировки потока и сетевого I/O.
        /// Возвращает 0 при промахе RAM-кэша, позволяя вызывающему коду перейти на асинхронный путь без аллокаций в случае RAM-хита.
        /// </summary>
        internal int TryReadFromRamSync(Span<byte> destination)
        {
            // Внутренний контракт: 0 = промах; асинхронный путь затем сообщит об утилизации исключением
            if (IsDisposed) return 0;

            long posBefore = Volatile.Read(ref _position);
            if (posBefore >= _source._contentLength) return 0;

            if (_source._ramCache.TryReadSpan(posBefore, destination, out int ramRead))
            {
                if (Interlocked.CompareExchange(ref _position, posBefore + ramRead, posBefore) == posBefore)
                    return ramRead;
            }

            return 0;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Горячий путь: если данные в RAM — синхронное копирование без планировщика.
        /// Холодный путь: sync-over-async fallback для внешних потребителей <see cref="Stream"/>; парсеры Core его не используют.
        /// Контракт совпадает с <see cref="ReadAsync(Memory{byte}, CancellationToken)"/>: <c>0</c> означает только конец контента,
        /// отмена бросает <see cref="OperationCanceledException"/>, утилизация — <see cref="ObjectDisposedException"/>,
        /// а RAM-хит продвигает позицию через CAS и не затирает конкурентный seek.
        /// </remarks>
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (IsDisposed)
                throw CreateDisposedException();

            long posBefore = Volatile.Read(ref _position);
            if (posBefore >= _source._contentLength) return 0;

            // Быстрый путь: данные уже в RAM; проигранный CAS означает seek, и чтение уходит в медленный путь с новой позиции
            if (_source._ramCache.TryRead(posBefore, buffer.AsMemory(offset, count), out int ramRead)
                && Interlocked.CompareExchange(ref _position, posBefore + ramRead, posBefore) == posBefore)
                return ramRead;

            // Медленный путь: sync-over-async; отмена, утилизация и ошибки I/O пробрасываются без маскировки под EOF
            return ReadAsyncCore(buffer.AsMemory(offset, count), GetCurrentReadToken())
                .AsTask()
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        }

        /// <inheritdoc/>
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        /// <inheritdoc/>
        /// <remarks>
        /// Отмена и утилизация не маскируются под EOF: по контракту <see cref="Stream"/> 0 означает только конец данных.
        /// Иначе <c>Mp4BinaryReader</c> получает пустой заголовок, а парсер — ложный <c>ParserCorruptionException</c>.
        /// Исключения создаются только на путях ошибок; RAM-хит остаётся синхронным и без аллокаций.
        /// </remarks>
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (ct.IsCancellationRequested)
                return ValueTask.FromCanceled<int>(ct);

            if (IsDisposed)
                return ValueTask.FromException<int>(CreateDisposedException());

            var readToken = GetCurrentReadToken();
            if (readToken.IsCancellationRequested)
                return ValueTask.FromCanceled<int>(readToken);

            long posBefore = Volatile.Read(ref _position);
            if (posBefore >= _source._contentLength)
                return ValueTask.FromResult(0);

            if (_source._ramCache.TryRead(posBefore, buffer, out int ramRead))
            {
                if (Interlocked.CompareExchange(ref _position, posBefore + ramRead, posBefore) == posBefore)
                    return ValueTask.FromResult(ramRead);
            }

            return ReadAsyncSlowPath(buffer, ct, readToken);
        }

        private async ValueTask<int> ReadAsyncSlowPath(
            Memory<byte> buffer,
            CancellationToken externalCt,
            CancellationToken readToken)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt, readToken);
            return await ReadAsyncCore(buffer, linkedCts.Token).ConfigureAwait(false);
        }

        /// <summary>
        /// Ядро чтения: делегирует в <see cref="ReadAtAsync"/> и
        /// атомарно продвигает позицию только если seek не произошёл во время чтения.
        /// </summary>
        /// <param name="buffer">Буфер для записи прочитанных байт.</param>
        /// <param name="ct">Токен отмены операции чтения.</param>
        /// <returns>Количество вычитанных байт; <c>0</c> только при достижении конца контента.</returns>
        /// <exception cref="OperationCanceledException">Отмена токена или конкурентный seek во время I/O.</exception>
        /// <exception cref="ObjectDisposedException">Источник или поток утилизированы.</exception>
        private async ValueTask<int> ReadAsyncCore(Memory<byte> buffer, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (IsDisposed)
                throw CreateDisposedException();

            long posBefore = Volatile.Read(ref _position);
            int read = await _source.ReadAtAsync(posBefore, buffer, ct).ConfigureAwait(false);

            // Если seek произошёл во время I/O, данные относятся к устаревшей позиции.
            // Возвращать 0 нельзя (это EOF): сообщаем об отмене, вызывающий код повторит чтение с новой позиции.
            if (Interlocked.CompareExchange(ref _position, posBefore + read, posBefore) != posBefore)
                throw new OperationCanceledException("Read superseded by a concurrent seek", ct);

            return read;
        }

        #endregion

        #region Seek

        /// <inheritdoc/>
        public override long Seek(long offset, SeekOrigin origin)
        {
            long length = _source._contentLength;
            long newPos = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Volatile.Read(ref _position) + offset,
                SeekOrigin.End => length + offset,
                _ => Volatile.Read(ref _position)
            };

            newPos = Math.Clamp(newPos, 0, length);
            Position = newPos;
            return newPos;
        }

        #endregion

        #region Not Supported

        /// <inheritdoc/>
        public override void Flush() { }

        /// <inheritdoc/>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc/>
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        #endregion

        #region Dispose

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var cts = Interlocked.Exchange(ref _readCts, null);
                if (cts != null)
                {
                    try { cts.Cancel(); } catch (ObjectDisposedException) { }
                    try { cts.Dispose(); } catch (ObjectDisposedException) { }
                }
            }

            base.Dispose(disposing);
        }

        #endregion
    }
}