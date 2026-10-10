using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Parsers;
using LMP.Core.Exceptions;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio.Sources;

/// <summary>
/// Источник для локальных аудио файлов (полностью закэшированных или скачанных).
/// 
/// <para><b>Характеристики:</b></para>
/// <list type="bullet">
///   <item>Всегда fully buffered (BufferProgress = 100%)</item>
///   <item>Seek через точки контейнера (Cluster/moof), без линейной интерполяции</item>
///   <item>Поддерживает WebM/Opus, MP4/AAC, Ogg/Opus</item>
/// </list>
/// 
/// <para><b>Потокобезопасность:</b></para>
/// <c>ReadFrameAsync</c> и <c>SeekAsync</c> НЕ потокобезопасны между собой.
/// Вызывающий код (<see cref="AudioPipeline"/>) гарантирует последовательный доступ:
/// <c>StopDecoding → Seek → StartDecoding</c>.
/// </summary>
public sealed class LocalFileSource : IAudioSource
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
    /// Создаёт источник из локального файла.
    /// </summary>
    /// <param name="filePath">Путь к аудио файлу.</param>
    /// <param name="expectedSize">Ожидаемый размер файла в байтах.</param>
    /// <param name="trackId">Идентификатор трека (если файл открыт из кэша).</param>
    /// <param name="cacheManager">
    /// Менеджер кэша для lease tracking.
    /// <c>null</c> для файлов вне cache (пользовательские downloads).
    /// </param>
    /// <param name="cacheKey">
    /// Ключ кэша для lease. Обязателен если <paramref name="cacheManager"/> не <c>null</c>.
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
    /// <remarks>Всегда 100% — файл полностью доступен.</remarks>
    public double BufferProgress => FullBufferProgressPercent;

    /// <inheritdoc/>
    /// <remarks>Всегда true — файл полностью доступен.</remarks>
    public bool IsFullyBuffered => true;

    /// <inheritdoc/>
    public byte[]? DecoderConfig => _parser?.DecoderConfig;

    /// <inheritdoc/>
    public int SampleRate => _parser?.SampleRate ?? UnknownSampleRate;

    /// <inheritdoc/>
    public int Channels => _parser?.Channels ?? UnknownChannels;

    /// <summary>
    /// Парсер контейнера. Доступен для диагностики (resync detection).
    /// </summary>
    internal IContainerParser? Parser => _parser;

    /// <summary>Абсолютный путь к файлу на диске.</summary>
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
    /// Определяет формат контейнера по magic bytes, с fallback на расширение файла.
    /// Буфер арендуется из <see cref="System.Buffers.ArrayPool{T}.Shared"/>
    /// для избежания heap-аллокации при инициализации.
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

        // Fallback на расширение
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
    /// Создаёт парсер контейнера.
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
    /// <para><b>Surgical Cache Repair:</b> при обнаружении коррупции в файле кэша
    /// выполняется точечная range-based инвалидация повреждённого сегмента на диске.</para>
    /// </remarks>
    public async ValueTask<AudioFrame?> ReadFrameAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_initialized || _parser == null)
            throw new InvalidOperationException("Source not initialized");

        int maxHealingAttempts = 3;
        for (int attempt = 0; attempt < maxHealingAttempts; attempt++)
        {
            try
            {
                var frame = await _parser.ReadNextFrameAsync(ct).ConfigureAwait(false);
                if (frame == null) return null;

                Volatile.Write(ref _positionMs, frame.Value.TimestampMs);
                return frame;
            }
            catch (ParserCorruptionException ex)
            {
                if (ct.IsCancellationRequested) throw;

                int alignmentBytes = ResolveRepairAlignmentBytes();
                long alignedStart = ex.AbsoluteBytePosition - (ex.AbsoluteBytePosition % alignmentBytes);
                int logicalIndex = (int)(alignedStart / alignmentBytes);

                if (!string.IsNullOrEmpty(_trackId) && _expectedSize > 0)
                {
                    Log.Warn($"[LocalFileSource] Cache corruption detected for track {_trackId} " +
                             $"at byte {ex.AbsoluteBytePosition} " +
                             $"(Range {alignedStart}-{alignedStart + alignmentBytes - 1}). " +
                             $"Performing surgical invalidation.");

                    AudioSourceFactory.GlobalCache.InvalidateRangeByTrackId(_trackId, alignedStart, alignmentBytes);

                    throw new CacheInvalidatedException(
                        $"Cache corruption detected at byte {ex.AbsoluteBytePosition} " +
                        $"(Range {alignedStart}-{alignedStart + alignmentBytes - 1})",
                        CacheInvalidationKind.ParserResync,
                        isRecoverable: true,
                        trackId: _trackId,
                        chunkIndex: logicalIndex,
                        inner: ex);
                }

                Log.Warn($"[LocalFileSource] Offline fallback: skipping corrupted range around byte {ex.AbsoluteBytePosition}");

                long nextBoundary = Math.Min(alignedStart + alignmentBytes, _fileStream!.Length);
                if (nextBoundary >= _fileStream.Length)
                    return null;

                _fileStream.Position = nextBoundary;
                _parser.RequireResync();

                Volatile.Write(ref _positionMs, DurationMs * nextBoundary / _fileStream.Length);
            }
        }

        throw new InvalidDataException("Unrecoverable local file corruption after maximum resync attempts.");
    }

    /// <summary>
    /// Определяет байтовое выравнивание для точечной инвалидации и resync fallback.
    /// </summary>
    /// <returns>Размер выровненного диапазона в байтах.</returns>
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
    /// <remarks>Всегда возвращает полный диапазон [0, 1].</remarks>
    public IReadOnlyList<(double Start, double End)> GetBufferedRanges() => [(BufferedRangeStart, BufferedRangeEnd)];

    /// <inheritdoc/>
    /// <remarks>No-op — нет RAM буферов для локальных файлов.</remarks>
    public void ReleaseRamBuffers() { }

    /// <inheritdoc/>
    /// <remarks>No-op — нет фоновых операций для локальных файлов.</remarks>
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
