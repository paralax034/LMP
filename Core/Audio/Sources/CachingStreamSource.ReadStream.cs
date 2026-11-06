namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    /// <summary>
    /// Stream wrapper over <see cref="CachingStreamSource"/> for container parsers (WebM/MP4).
    ///
    /// <para><b>Position setter optimization:</b></para>
    /// <list type="bullet">
    ///   <item><c>Position setter</c> — lightweight <c>Volatile.Write</c>, without CTS allocations</item>
    ///   <item><see cref="SeekAndCancelPendingReads"/> — explicit cancel for external seek</item>
    /// </list>
    ///
    /// <para><b>ReadAsync contract:</b> Returns <c>0</c> only when reaching end of content.
    /// Cancellation throws <see cref="OperationCanceledException"/>, disposal throws <see cref="ObjectDisposedException"/>.</para>
    /// </summary>
    internal sealed class AsyncCachingReadStream : Stream
    {
        private readonly CachingStreamSource _source;
        private long _position;

        /// <summary>
        /// CTS bound to the current logical stream position.
        /// Replaced only via <see cref="SwapReadEpoch"/>; <see langword="null"/> indicates a disposed stream.
        /// </summary>
        private CancellationTokenSource? _readCts = new();

        /// <summary>Creates a Stream wrapper over the source.</summary>
        public AsyncCachingReadStream(CachingStreamSource source) => _source = source;

        /// <summary>The source or the stream itself is already disposed.</summary>
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
        /// Sets the stream position AND cancels all pending ReadAsync operations.
        /// Called ONLY from <see cref="SeekAsync"/> on an external seek.
        /// </summary>
        /// <param name="position">New absolute position in bytes.</param>
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
        /// Instantly cancels active read operations synchronously.
        /// </summary>
        internal void CancelActiveReads()
        {
            var oldCts = SwapReadEpoch();
            if (oldCts == null) return;

            try
            {
                // Synchronous cancellation immediately interrupts blocking Read() in DecoderLoop
                oldCts.Cancel();
            }
            catch (ObjectDisposedException) { }

            // Defer Dispose to avoid ObjectDisposedException in other threads
            DeferDisposeCancellationTokenSource(oldCts, 500);
        }

        /// <summary>
        /// Atomically replaces the current read epoch's CTS with a new one and returns the previous one.
        /// Returns <see langword="null"/> if the stream is already disposed: the epoch is not resurrected,
        /// and <see cref="SeekAndCancelPendingReads"/> does not throw NullReferenceException after Dispose.
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

            // Stream disposed concurrently: the new CTS was acquired by nobody
            fresh.Dispose();
            return null;
        }

        /// <summary>
        /// Safely retrieves the <see cref="CancellationToken"/> from the current <see cref="_readCts"/>.
        /// Returns a canceled token for a disposed stream.
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
        /// Tries to synchronously read data exclusively from the RAM cache without blocking the thread or performing network I/O.
        /// Returns 0 on RAM cache miss, allowing the caller to transition to the async path allocation-free on a RAM hit.
        /// </summary>
        internal int TryReadFromRamSync(Span<byte> destination)
        {
            // Internal contract: 0 = miss; the async path will subsequently report disposal via exception
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
        /// Hot path: if data is in RAM — synchronous copy without scheduler overhead.
        /// Cold path: sync-over-async fallback for external <see cref="Stream"/> consumers; Core parsers do not use it.
        /// The contract matches <see cref="ReadAsync(Memory{byte}, CancellationToken)"/>: <c>0</c> means end of content only,
        /// cancellation throws <see cref="OperationCanceledException"/>, disposal throws <see cref="ObjectDisposedException"/>,
        /// and a RAM hit advances position via CAS without overwriting a concurrent seek.
        /// </remarks>
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (IsDisposed)
                throw CreateDisposedException();

            long posBefore = Volatile.Read(ref _position);
            if (posBefore >= _source._contentLength) return 0;

            // Fast path: data is already in RAM; a lost CAS indicates a seek, routing the read to the slow path from the new position
            if (_source._ramCache.TryRead(posBefore, buffer.AsMemory(offset, count), out int ramRead)
                && Interlocked.CompareExchange(ref _position, posBefore + ramRead, posBefore) == posBefore)
                return ramRead;

            // Slow path: sync-over-async; cancellation, disposal, and I/O errors are propagated without masking as EOF
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
        /// Cancellation and disposal are not masked as EOF: by the <see cref="Stream"/> contract, 0 means end of data only.
        /// Otherwise <c>Mp4BinaryReader</c> gets an empty header, and the parser gets a false <c>ParserCorruptionException</c>.
        /// Exceptions are instantiated only on error paths; RAM hits remain synchronous and allocation-free.
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
        /// Core read logic: delegates to <see cref="ReadAtAsync"/> and
        /// atomically advances the position only if no seek occurred during the read.
        /// </summary>
        /// <param name="buffer">Buffer to write read bytes into.</param>
        /// <param name="ct">Cancellation token for the read operation.</param>
        /// <returns>Number of bytes read; <c>0</c> only when reaching end of content.</returns>
        /// <exception cref="OperationCanceledException">Token cancellation or concurrent seek during I/O.</exception>
        /// <exception cref="ObjectDisposedException">Source or stream disposed.</exception>
        private async ValueTask<int> ReadAsyncCore(Memory<byte> buffer, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (IsDisposed)
                throw CreateDisposedException();

            long posBefore = Volatile.Read(ref _position);
            int read = await _source.ReadAtAsync(posBefore, buffer, ct).ConfigureAwait(false);

            // If a seek occurred during I/O, the data belongs to a stale position.
            // Cannot return 0 (that means EOF): report cancellation so the caller retries the read from the new position.
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