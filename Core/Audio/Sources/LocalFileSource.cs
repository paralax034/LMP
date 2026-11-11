using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Parsers;
using LMP.Core.Exceptions;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio.Sources;

/// <summary>
/// Source for local audio files (fully cached or downloaded).
/// 
/// <para><b>Characteristics:</b></para>
/// <list type="bullet">
///   <item>Always fully buffered (BufferProgress = 100%)</item>
///   <item>Seek via container points (Cluster/moof), without linear interpolation</item>
///   <item>Supports WebM/Opus, MP4/AAC, Ogg/Opus</item>
/// </list>
/// 
/// <para><b>Thread Safety:</b></para>
/// <c>ReadFrameAsync</c> and <c>SeekAsync</c> are NOT thread-safe with respect to each other.
/// The calling code (<see cref="AudioPipeline"/>) guarantees sequential access:
/// <c>StopDecoding → Seek → StartDecoding</c>.
/// </summary>
internal sealed class LocalFileSource : IAudioSource
{
    private const long StreamStartPosition = 0;

    private readonly string _filePath;
    private readonly long _expectedSize;
    private readonly string? _trackId;
    private readonly AudioCacheManager? _cacheManager;
    private readonly string? _cacheKey;

    private FileStream? _fileStream;
    private IContainerParser? _parser;
    private long _positionMs;
    private bool _initialized;
    private bool _disposed;
    private bool _leaseAcquired;

    /// <summary>
    /// Creates a source from a local file.
    /// </summary>
    /// <param name="filePath">Path to the audio file.</param>
    /// <param name="expectedSize">Expected file size in bytes.</param>
    /// <param name="trackId">Track identifier (if the file is opened from cache).</param>
    /// <param name="cacheManager">
    /// Cache manager for lease tracking.
    /// <c>null</c> for files outside cache (user downloads).
    /// </param>
    /// <param name="cacheKey">
    /// Cache key for lease. Required if <paramref name="cacheManager"/> is not <c>null</c>.
    /// </param>
    public LocalFileSource(
        string filePath,
        long expectedSize = 0,
        string? trackId = null,
        AudioCacheManager? cacheManager = null,
        string? cacheKey = null)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _expectedSize = expectedSize;
        _trackId = trackId;
        _cacheManager = cacheManager;
        _cacheKey = cacheKey;

        if (!File.Exists(filePath))
            throw new FileNotFoundException("Audio file not found", filePath);
    }

    #region Properties

    /// <inheritdoc/>
    public long DurationMs { get; private set; } = UnknownDurationMs;

    /// <inheritdoc/>
    public long PositionMs => Volatile.Read(ref _positionMs);

    /// <inheritdoc/>
    public bool CanSeek => true;

    /// <inheritdoc/>
    public AudioCodec Codec { get; private set; } = AudioCodec.Unknown;

    /// <inheritdoc/>
    /// <remarks>Always 100% — the file is fully available.</remarks>
    public double BufferProgress => FullBufferProgressPercent;

    /// <inheritdoc/>
    /// <remarks>Always true — the file is fully available.</remarks>
    public bool IsFullyBuffered => true;

    /// <inheritdoc/>
    public byte[]? DecoderConfig => _parser?.DecoderConfig;

    /// <inheritdoc/>
    public int SampleRate => _parser?.SampleRate ?? UnknownSampleRate;

    /// <inheritdoc/>
    public int Channels => _parser?.Channels ?? UnknownChannels;

    /// <summary>
    /// Container parser. Exposed for diagnostics (resync detection).
    /// </summary>
    internal IContainerParser? Parser => _parser;

    /// <summary>Absolute path to the file on disk.</summary>
    public string FilePath => _filePath;

    #endregion

    #region Initialization

    /// <inheritdoc/>
    public async ValueTask<bool> InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return true;

        try
        {
            if (_cacheManager != null && _cacheKey != null)
            {
                _cacheManager.AcquireLease(_cacheKey);
                _leaseAcquired = true;
            }

            _fileStream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: CacheFileBufferSize,
                useAsync: true);

            if (_expectedSize > 0 && _fileStream.Length < _expectedSize)
            {
                Log.Error($"[LocalFileSource] Truncated file rejected: " +
                          $"actual={_fileStream.Length}, expected={_expectedSize}, " +
                          $"path={Path.GetFileName(_filePath)}");
                return false;
            }

            var format = await DetectFormatAsync(ct).ConfigureAwait(false);
            _parser = CreateParser(format);

            if (!await _parser.ParseHeadersAsync(ct).ConfigureAwait(false))
            {
                Log.Error("[LocalFileSource] Failed to parse container headers");
                return false;
            }

            DurationMs = _parser.DurationMs;
            Codec = _parser.Codec;
            _initialized = true;

            Log.Info($"[LocalFileSource] Initialized: duration={DurationMs}ms, " +
                     $"codec={Codec}, size={_fileStream.Length / BytesPerKilobyte}KB");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[LocalFileSource] Init failed: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// Detects the container format by magic bytes, falling back to file extension.
    /// The buffer is rented from <see cref="System.Buffers.ArrayPool{T}.Shared"/>
    /// to avoid heap allocations during initialization.
    /// </summary>
    private async Task<AudioFormat> DetectFormatAsync(CancellationToken ct)
    {
        var header = System.Buffers.ArrayPool<byte>.Shared.Rent(FormatDetectionHeaderSize);
        try
        {
            int totalRead = 0;

            while (totalRead < FormatDetectionHeaderSize)
            {
                int read = await _fileStream!.ReadAsync(
                    header.AsMemory(totalRead, FormatDetectionHeaderSize - totalRead), ct);
                if (read == 0) break;
                totalRead += read;
            }

            _fileStream!.Position = StreamStartPosition;

            var format = AudioSourceFactory.DetectFormatByMagic(header);

            if (format != AudioFormat.Unknown)
                return format;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(header);
        }

        // Fallback to extension
        return Path.GetExtension(_filePath).ToLowerInvariant() switch
        {
            ".webm" => AudioFormat.WebM,
            ".m4a" or ".mp4" or ".aac" => AudioFormat.Mp4,
            ".ogg" or ".opus" => AudioFormat.Ogg,
            _ => throw new NotSupportedException(
                $"Cannot detect audio format for: {Path.GetFileName(_filePath)}")
        };
    }

    /// <summary>
    /// Creates a container parser.
    /// </summary>
    private IContainerParser CreateParser(AudioFormat format) => format switch
    {
        AudioFormat.WebM or AudioFormat.Ogg => new WebMContainerParser(_fileStream!),
        AudioFormat.Mp4 => new Mp4ContainerParser(_fileStream!),
        _ => throw new NotSupportedException($"Unsupported format: {format}")
    };

    #endregion

    #region Reading

    /// <inheritdoc/>
    /// <remarks>
    /// <para><b>Surgical Cache Repair:</b> when corruption is detected in a cache file,
    /// precise range-based invalidation of the damaged on-disk segment is executed.</para>
    /// </remarks>
    public ValueTask<AudioFrame?> ReadFrameAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var parser = _parser;
        if (!_initialized || parser == null)
            throw new InvalidOperationException("Source not initialized");

        if (ct.IsCancellationRequested)
            return ValueTask.FromCanceled<AudioFrame?>(ct);

        try
        {
            var vt = parser.ReadNextFrameAsync(ct);
            if (vt.IsCompletedSuccessfully)
            {
                var frame = vt.Result;
                if (frame != null)
                    Volatile.Write(ref _positionMs, frame.Value.TimestampMs);
                return new ValueTask<AudioFrame?>(frame);
            }

            return ReadFrameSlowAsync(vt, ct);
        }
        catch (ParserCorruptionException ex)
        {
            return HealAndReadFrameSlowAsync(ex, ct);
        }
    }

    /// <summary>
    /// Async frame reading path on parser synchronous buffer miss.
    /// </summary>
    private async ValueTask<AudioFrame?> ReadFrameSlowAsync(
        ValueTask<AudioFrame?> pendingRead,
        CancellationToken ct)
    {
        try
        {
            var frame = await pendingRead.ConfigureAwait(false);
            if (frame != null)
                Volatile.Write(ref _positionMs, frame.Value.TimestampMs);
            return frame;
        }
        catch (ParserCorruptionException ex)
        {
            return await HealAndReadFrameSlowAsync(ex, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Slow recovery path after container structure error (Surgical Cache Repair / Resync).
    /// </summary>
    private async ValueTask<AudioFrame?> HealAndReadFrameSlowAsync(
        ParserCorruptionException initialEx,
        CancellationToken ct)
    {
        const int maxHealingAttempts = 3;
        ParserCorruptionException? currentEx = initialEx;

        for (int attempt = 0; attempt < maxHealingAttempts; attempt++)
        {
            if (currentEx != null)
            {
                if (ct.IsCancellationRequested) throw currentEx;

                int alignmentBytes = ResolveRepairAlignmentBytes();
                long alignedStart = currentEx.AbsoluteBytePosition - (currentEx.AbsoluteBytePosition % alignmentBytes);
                int logicalIndex = (int)(alignedStart / alignmentBytes);

                if (!string.IsNullOrEmpty(_trackId) && _expectedSize > 0)
                {
                    Log.Warn($"[LocalFileSource] Cache corruption detected for track {_trackId} " +
                             $"at byte {currentEx.AbsoluteBytePosition} " +
                             $"(Range {alignedStart}-{alignedStart + alignmentBytes - 1}). " +
                             $"Performing surgical invalidation.");

                    AudioSourceFactory.GlobalCache.InvalidateRangeByTrackId(_trackId, alignedStart, alignmentBytes);

                    throw new CacheInvalidatedException(
                        $"Cache corruption detected at byte {currentEx.AbsoluteBytePosition} " +
                        $"(Range {alignedStart}-{alignedStart + alignmentBytes - 1})",
                        CacheInvalidationKind.ParserResync,
                        isRecoverable: true,
                        trackId: _trackId,
                        chunkIndex: logicalIndex,
                        inner: currentEx);
                }

                Log.Warn($"[LocalFileSource] Offline fallback: skipping corrupted range around byte {currentEx.AbsoluteBytePosition}");

                long nextBoundary = Math.Min(alignedStart + alignmentBytes, _fileStream!.Length);
                if (nextBoundary >= _fileStream.Length)
                    return null;

                _fileStream.Position = nextBoundary;
                _parser!.RequireResync();

                Volatile.Write(ref _positionMs, DurationMs * nextBoundary / _fileStream.Length);
            }

            try
            {
                var frame = await _parser!.ReadNextFrameAsync(ct).ConfigureAwait(false);
                if (frame == null) return null;

                Volatile.Write(ref _positionMs, frame.Value.TimestampMs);
                return frame;
            }
            catch (ParserCorruptionException nextEx)
            {
                currentEx = nextEx;
            }
        }

        throw new InvalidDataException("Unrecoverable local file corruption after maximum resync attempts.");
    }

    /// <summary>
    /// Determines byte alignment for fine-grained invalidation and resync fallback.
    /// </summary>
    /// <returns>Aligned range size in bytes.</returns>
    private int ResolveRepairAlignmentBytes()
    {
        if (!string.IsNullOrEmpty(_trackId))
        {
            int alignment = AudioSourceFactory.GlobalCache.FindBestCache(_trackId)?.AlignmentBytes ?? 0;
            if (alignment > 0)
                return alignment;
        }

        return ChunkSize;
    }

    #endregion

    #region Seeking

    /// <inheritdoc/>
    public ValueTask<bool> SeekAsync(long positionMs, CancellationToken ct = default)
    {
        if (_parser == null || _fileStream == null)
            return ValueTask.FromResult(false);

        var seekInfo = _parser.FindSeekPosition(positionMs);
        if (seekInfo == null)
        {
            Log.Warn($"[LocalFileSource] No seek point for {positionMs}ms");
            return ValueTask.FromResult(false);
        }

        Log.Debug($"[LocalFileSource] Seek: target={positionMs}ms, actual={seekInfo.Value.TimestampMs}ms, byte={seekInfo.Value.BytePosition}");

        _fileStream.Position = seekInfo.Value.BytePosition;
        _parser.Reset();
        Volatile.Write(ref _positionMs, seekInfo.Value.TimestampMs);

        return ValueTask.FromResult(true);
    }

    #endregion

    #region Buffer Info (No-op for local files)

    /// <inheritdoc/>
    /// <remarks>Always returns the full range [0, 1].</remarks>
    public IReadOnlyList<(double Start, double End)> GetBufferedRanges() => FullBufferedRanges;

    /// <inheritdoc/>
    /// <remarks>No-op — no RAM buffers for local files.</remarks>
    public void ReleaseRamBuffers() { }

    /// <inheritdoc/>
    /// <remarks>No-op — no background operations for local files.</remarks>
    public void CancelPendingOperations() { }

    /// <inheritdoc/>
    public void SetPlaybackActive(bool active) { }

    #endregion

    #region Dispose

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _parser?.Dispose();
        _fileStream?.Dispose();

        if (_leaseAcquired)
            _cacheManager?.ReleaseLease(_cacheKey!);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_parser != null)
            await _parser.DisposeAsync().ConfigureAwait(false);

        if (_fileStream != null)
            await _fileStream.DisposeAsync().ConfigureAwait(false);

        if (_leaseAcquired)
            _cacheManager?.ReleaseLease(_cacheKey!);
    }

    #endregion
}
