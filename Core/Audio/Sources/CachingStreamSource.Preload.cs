using LMP.Core.Exceptions;

namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    #region Suspend/Resume

    /// <summary>
    /// Приостанавливает фоновую загрузку (при сворачивании окна).
    /// Critical read-ahead продолжает работать для бесперебойного воспроизведения.
    /// </summary>
    public void Suspend()
    {
        _suspendGate.Reset();
        Log.Debug("[CachingSource] Suspended (background fill paused, critical read-ahead active)");
    }

    /// <summary>
    /// Возобновляет фоновую загрузку.
    /// </summary>
    public void Resume()
    {
        _suspendGate.Set();
        Log.Debug("[CachingSource] Resumed");
    }

    #endregion

    #region Preload Loop

    /// <summary>
    /// Фоновый цикл предзагрузки диапазонов.
    /// <para>
    /// Первая итерация выполняется без задержки, чтобы немедленно подхватить
    /// работу <see cref="SafeStartupPrefetchAsync"/> и продолжить заполнение буфера.
    /// Последующие итерации разделены интервалом <see cref="StreamingConfig.PreloadIntervalMs"/>.
    /// </para>
    /// <para>
    /// Адаптирует параллелизм и размер запросов к состоянию сети.
    /// Включает opportunistic completion: если трек почти скачан,
    /// добиваем оставшиеся gaps до полного кэширования.
    /// </para>
    /// </summary>
    private async Task PreloadLoopAsync(CancellationToken ct)
    {
        bool justResumed = false;

        /// <summary>
        /// Порог "почти скачан" — если скачано >= этого процента, включается completion fill.
        /// </summary>
        const double CompletionFillThresholdPercent = 90.0;

        /// <summary>
        /// Максимальный абсолютный размер недокачки, при котором включается completion fill.
        /// </summary>
        const long CompletionFillMaxRemainingBytes = 512 * 1024;

#if DEBUG
        int lastReportedProgress = -1;
#endif

        while (!ct.IsCancellationRequested && _cacheEntry is { IsComplete: false })
        {
            try
            {
                bool isSuspended = !_suspendGate.IsSet;
                bool isPaused = !_playbackGate.IsSet;

                // Пауза: ждём открытия gate без периодических холостых пробуждений
                if (isPaused)
                {
                    try { _playbackGate.Wait(ct); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                // Suspend mode: только critical read-ahead 
                if (isSuspended)
                {
                    long current = Volatile.Read(ref _currentReadOffset);
                    long bufferedAhead = GetBufferedBytesAheadIncludingInflight(current);

                    if (bufferedAhead < _config.MinRequestSizeBytes)
                    {
                        long nextPos = current + bufferedAhead;
                        int nextLen = GetAlignedReadLength(nextPos, _config.MinRequestSizeBytes);

                        try
                        {
                            await EnsureRangeAsync(nextPos, nextLen, ct, isCritical: true)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { break; }
                        catch (ChunkDownloadFatalException ex)
                        {
                            if (ex.Reason is ChunkDownloadFailureReason.UmpFormat
                                          or ChunkDownloadFailureReason.Forbidden403)
                            {
                                Log.Warn($"[CachingSource] Permanent preload fatal in suspend: {ex.Reason}");
                                break;
                            }

                            Log.Debug($"[CachingSource] Transient preload fatal in suspend, " +
                                      $"backing off: {ex.Message}");
                            if (!await ct.DelayNoThrowAsync(3000, continueOnCapturedContext: false))
                                break;
                        }
                        catch (Exception ex)
                        {
                            Log.Debug($"[CachingSource] Suspended critical preload: {ex.Message}");
                        }
                    }

                    int bufferedAheadMs = ConvertBufferedBytesToMs(bufferedAhead);
                    int suspendWaitMs = bufferedAheadMs > 4000
                        ? 2000
                        : bufferedAheadMs > 2000
                            ? 1000
                            : PlaybackGateCriticalTimeoutMs;

                    try { _suspendGate.Wait(suspendWaitMs, ct); }
                    catch (OperationCanceledException) { break; }

                    if (_suspendGate.IsSet)
                        justResumed = true;

                    continue;
                }

                // Критическая сетевая операция seek в процессе — уступаем ей весь канал
                if (_seekInProgress)
                {
                    if (!await ct.DelayNoThrowAsync(_config.PreloadIntervalMs, continueOnCapturedContext: false))
                        break;
                    continue;
                }

                // Дебаунс после resume 
                if (justResumed)
                {
                    justResumed = false;
                    if (!await ct.DelayNoThrowAsync(_config.PreloadIntervalMs, continueOnCapturedContext: false))
                        break;
                    continue;
                }

                if (_cacheEntry.IsComplete) break;

                // Snapshot текущего состояния 
                long epochAtLoopStart = Interlocked.Read(ref _downloadEpoch);
                var token = CurrentDownloadToken;
                long currentOffset = Volatile.Read(ref _currentReadOffset);

                int adaptiveMaxDownloads = GetAdaptiveMaxConcurrentDownloads();
                int pending = GetActiveDownloadCount();

                if (pending >= adaptiveMaxDownloads)
                {
                    if (!await ct.DelayNoThrowAsync(_config.PreloadIntervalMs, continueOnCapturedContext: false).ConfigureAwait(false))
                        break;
                    continue;
                }

                int adaptiveTargetBufferMs = GetAdaptiveTargetBufferMs();
                int refillStopMs = adaptiveTargetBufferMs + TargetBufferHysteresisMs;

                var degradation = GetNetworkDegradationLevel();

                // Prefetch loop: планируем запросы вперёд от текущей позиции 
                while (pending < adaptiveMaxDownloads)
                {
                    if (Interlocked.Read(ref _downloadEpoch) != epochAtLoopStart)
                        break;

                    long plannedAheadBytes = GetBufferedBytesAheadIncludingInflight(currentOffset);
                    int plannedAheadMs = ConvertBufferedBytesToMs(plannedAheadBytes);

                    if (plannedAheadMs >= refillStopMs)
                        break;

                    long nextPosition = currentOffset + plannedAheadBytes;
                    if (nextPosition >= _contentLength)
                        break;

                    int requestBytes = GetAlignedReadLength(nextPosition, _config.MinRequestSizeBytes);
                    if (requestBytes <= 0)
                        break;

                    bool critical =
                        plannedAheadMs < EmergencyRefillBufferMs
                        || (pending == 0 && plannedAheadMs < CriticalRefillBufferMs);

                    _ = SafeEnsureRangeAsync(nextPosition, requestBytes, token, critical);
                    pending++;

                    if (degradation != NetworkDegradationLevel.Normal)
                        break;

                    if (!critical)
                        break;
                }

                // Opportunistic completion fill: запускаем ТОЛЬКО когда весь хвост от текущей позиции
                // до конца файла уже полностью скачан и нет активных загрузок (pending == 0),
                // чтобы не создавать конкурирующие HTTP-запросы позади курсора при перемотках.
                long bufferedToTail = GetBufferedBytesAheadIncludingInflight(currentOffset);
                bool isTailFullyBuffered = currentOffset + bufferedToTail >= _contentLength;

                if (pending == 0 && isTailFullyBuffered && !_cacheEntry.IsComplete)
                {
                    long downloadedBytes = _cacheEntry.DownloadedBytes;
                    long totalSize = _cacheEntry.TotalSize;
                    long remainingBytes = totalSize - downloadedBytes;

                    bool shouldCompletionFill = totalSize > 0
                        && ((_cacheEntry.DownloadProgress >= CompletionFillThresholdPercent)
                            || (remainingBytes > 0 && remainingBytes <= CompletionFillMaxRemainingBytes));

                    if (shouldCompletionFill)
                    {
                        await TryCompletionFillAsync(token, maxRequests: 1)
                            .ConfigureAwait(false);
                    }
                }

                // Trim RAM cache 
                if (_ramCache.TotalBytes > _config.MaxRamBytes)
                    ReleaseRamBuffers();

#if DEBUG
                int progress = (int)(_cacheEntry?.DownloadProgress ?? 0);
                if (progress != lastReportedProgress)
                {
                    Log.Debug($"[CachingSource] Progress: {progress}% " +
                              $"({_cacheEntry?.DownloadedBytes ?? 0}/{_cacheEntry?.TotalSize ?? 0} bytes)");
                    lastReportedProgress = progress;
                }
#endif

                if (!await ct.DelayNoThrowAsync(_config.PreloadIntervalMs, continueOnCapturedContext: false).ConfigureAwait(false))
                    break;
            }
            catch (OperationCanceledException) { break; }
            catch { /* Защита от неожиданных исключений в фоновом цикле */ }
        }
    }

    /// <summary>
    /// Сканирует все скачанные диапазоны и заполняет первые найденные gaps.
    /// </summary>
    /// <param name="downloadToken">Токен текущей эпохи загрузки.</param>
    /// <param name="maxRequests">Максимальное количество одновременных запросов.</param>
    private async Task TryCompletionFillAsync(
        CancellationToken downloadToken,
        int maxRequests)
    {
        if (_cacheEntry == null || _cacheEntry.IsComplete || maxRequests <= 0)
            return;

        long totalSize = _cacheEntry.TotalSize;
        if (totalSize <= 0) return;

        var ranges = _cacheEntry.GetDownloadedRangesSnapshot();
        int fired = 0;

        long scanPosition = 0;

        for (int i = 0; i <= ranges.Length && fired < maxRequests; i++)
        {
            long gapStart;
            long gapEnd;

            if (i < ranges.Length)
            {
                gapStart = scanPosition;
                gapEnd = ranges[i].Start;
                scanPosition = ranges[i].EndExclusive;
            }
            else
            {
                gapStart = scanPosition;
                gapEnd = totalSize;
            }

            if (gapEnd <= gapStart)
                continue;

            long gapLength = gapEnd - gapStart;
            if (gapLength <= 0)
                continue;

            // Защита от спама на конце файла: если это хвостовой зазор после последнего диапазона
            // и он меньше размера блока выравнивания — сервер уже отдал весь файл, прекращаем повторные запросы
            if (i == ranges.Length && gapLength <= _requestAlignmentBytes * 2 && ranges.Length > 0)
                continue;

            int requestLength = (int)Math.Min(gapLength, _config.MaxRequestSizeBytes);

            _ = SafeEnsureRangeAsync(gapStart, requestLength, downloadToken, isCritical: false);
            fired++;
        }

#if DEBUG
        if (fired > 0)
        {
            Log.Debug($"[CachingSource] Completion fill: {fired} gap request(s) fired " +
                      $"({_cacheEntry.DownloadProgress:F0}% → 100%)");
        }
#endif
    }

    /// <summary>
    /// Безопасная обёртка над <see cref="EnsureRangeAsync"/> для fire-and-forget вызовов.
    /// </summary>
    private async Task SafeEnsureRangeAsync(
        long position, int minimumLength, CancellationToken ct, bool isCritical)
    {
        try
        {
            await EnsureRangeAsync(position, minimumLength, ct, isCritical).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ChunkDownloadFatalException ex)
        {
            Log.Debug($"[Preload] Range {position}: fatal: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Debug($"[Preload] Range {position}: {ex.Message}");
        }
    }

    #endregion

    #region Buffer Ranges

    /// <summary>
    /// Сливает отсортированные дисковые диапазоны и RAM-блоки в нормализованные отрезки <c>[0.0, 1.0]</c>
    /// одним линейным проходом, без промежуточных коллекций и сортировки.
    /// </summary>
    /// <param name="diskRanges">Дисковые диапазоны, отсортированные по началу.</param>
    /// <param name="ramBlocks">RAM-блоки, отсортированные по началу.</param>
    /// <param name="contentLength">Полный размер контента (байт), больше нуля.</param>
    /// <param name="destination">
    /// Буфер результата не короче значения режима подсчёта; пустой буфер включает режим подсчёта.
    /// </param>
    /// <returns>Число слитых отрезков.</returns>
    internal static int MergeBufferedRanges(
        ReadOnlySpan<LMP.Core.Audio.Cache.CacheByteRange> diskRanges,
        ReadOnlySpan<RamRangeBlock> ramBlocks,
        long contentLength,
        Span<(double Start, double End)> destination)
    {
        bool countOnly = destination.IsEmpty;
        int count = 0;
        int diskIndex = 0;
        int ramIndex = 0;
        long mergedStart = -1;
        long mergedEnd = -1;

        while (diskIndex < diskRanges.Length || ramIndex < ramBlocks.Length)
        {
            long start;
            long end;

            if (ramIndex >= ramBlocks.Length
                || (diskIndex < diskRanges.Length && diskRanges[diskIndex].Start <= ramBlocks[ramIndex].StartOffset))
            {
                start = diskRanges[diskIndex].Start;
                end = diskRanges[diskIndex].EndExclusive;
                diskIndex++;
            }
            else
            {
                start = ramBlocks[ramIndex].StartOffset;
                end = ramBlocks[ramIndex].EndOffsetExclusive;
                ramIndex++;
            }

            if (mergedStart < 0)
            {
                mergedStart = start;
                mergedEnd = end;
                continue;
            }

            if (start <= mergedEnd)
            {
                if (end > mergedEnd)
                    mergedEnd = end;
                continue;
            }

            if (!countOnly)
                destination[count] = ((double)mergedStart / contentLength, (double)mergedEnd / contentLength);
            count++;
            mergedStart = start;
            mergedEnd = end;
        }

        if (mergedStart >= 0)
        {
            if (!countOnly)
                destination[count] = ((double)mergedStart / contentLength, (double)mergedEnd / contentLength);
            count++;
        }

        return count;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Объединяет диапазоны из disk-кэша и RAM-кэша,
    /// нормализует в <c>[0.0, 1.0]</c> относительно <see cref="_contentLength"/>.
    /// Перестройка идёт только при смене версий и аллоцирует лишь снимки и точный по размеру результат.
    /// </remarks>
    public IReadOnlyList<(double Start, double End)> GetBufferedRanges()
    {
        var entry = _cacheEntry;
        if (entry == null || _contentLength <= 0)
            return [];

        if (entry.IsComplete)
        {
            if (_cachedBufferedRanges.Length > 0)
            {
                lock (_bufferedRangesCacheLock)
                {
                    _cachedBufferedRanges = [];
                }
            }
            return AudioConstants.FullBufferedRanges;
        }

        long diskVer = entry.RangesVersion;
        long ramVer = _ramCache.Version;

        lock (_bufferedRangesCacheLock)
        {
            if (diskVer == _cachedDiskRangesVersion && ramVer == _cachedRamRangesVersion)
                return _cachedBufferedRanges;
        }

        var diskRanges = entry.GetDownloadedRangesSnapshot(out long snapDiskVer);
        var ramBlocks = _ramCache.GetRangesSnapshot(out long snapRamVer);

        // Оба снимка отсортированы по началу: первый проход считает отрезки, второй заполняет точный массив.
        int count = MergeBufferedRanges(diskRanges, ramBlocks, _contentLength, []);
        (double Start, double End)[] merged = [];
        if (count > 0)
        {
            merged = new (double Start, double End)[count];
            _ = MergeBufferedRanges(diskRanges, ramBlocks, _contentLength, merged);
        }

        lock (_bufferedRangesCacheLock)
        {
            _cachedDiskRangesVersion = snapDiskVer;
            _cachedRamRangesVersion = snapRamVer;
            _cachedBufferedRanges = merged;
        }
            return _cachedBufferedRanges;
        }

    #endregion
}