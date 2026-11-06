using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Parsers;

namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    // --- InitializeAsync ---

    /// <inheritdoc/>
    public async ValueTask<bool> InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return true;

        try
        {
            Log.Info($"[CachingSource] Initialize: track={_trackId}, cacheKey={_cacheKey}, " +
                     $"format={_format}, codec={Codec}, bitrate={_bitrate}kbps, " +
                     $"contentLength={_contentLength}, " +
                     $"hasInitialUrl={!string.IsNullOrWhiteSpace(_currentUrl)}");

            _cacheManager.AcquireLease(_cacheKey);
            _leaseAcquired = true;

            _cacheEntry = _cacheManager.CreateOrUpdate(
                _cacheKey, _trackId, _currentUrl, _contentLength, _format,
                AudioSourceFactory.GetCodecForFormat(_format), _bitrate,
                alignmentBytes: _requestAlignmentBytes);

            Log.Debug($"[CachingSource] Cache entry: downloaded={_cacheEntry.DownloadedBytes}, " +
                      $"total={_cacheEntry.TotalSize}, complete={_cacheEntry.IsComplete}, " +
                      $"alignment={_cacheEntry.AlignmentBytes}");

            if (_cacheEntry.DownloadedBytes > 0)
            {
                _requestAlignmentBytes = Math.Max(4096, _cacheEntry.AlignmentBytes);
                Log.Info($"[CachingSource] Resuming: {_cacheEntry.DownloadedBytes}/{_cacheEntry.TotalSize} bytes");
            }

            _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            InitializeFirstEpoch();

            int initialBytes = Math.Min(
                _config.InitialPrebufferBytes,
                (int)Math.Min(_contentLength, int.MaxValue));

            bool hasLocalBootstrap = HasLocalInitialBootstrapData(initialBytes);

            if (!hasLocalBootstrap)
            {
                if (string.IsNullOrWhiteSpace(_currentUrl))
                {
                    bool urlReady = await EnsureUrlAvailableAsync(_lifetimeCts.Token)
                        .ConfigureAwait(false);
                    if (!urlReady)
                        throw new InvalidOperationException(
                            "Failed to acquire continuation URL for source initialization");
                }

                await EnsureRangeAsync(0, initialBytes, _lifetimeCts.Token, isCritical: true)
                    .ConfigureAwait(false);
            }
            else
            {
                Log.Debug($"[CachingSource] Local bootstrap prefix is sufficient: {initialBytes} bytes");
            }

            _readStream = new AsyncCachingReadStream(this);
            _parser = CreateParser(_readStream);

            if (!await _parser.ParseHeadersAsync(ct).ConfigureAwait(false))
                throw new InvalidOperationException("Failed to parse container headers");

            Codec = _parser.Codec;
            _cacheEntry.Codec = Codec;
            _cacheEntry.DurationMs = _parser.DurationMs;
            _cacheEntry.Bitrate = _bitrate;
            _initialized = true;

            Log.Info($"[CachingSource] Parser ready: track={_trackId}, codec={Codec}, " +
                     $"sampleRate={SampleRate}, channels={Channels}, duration={DurationMs}ms");

            // Startup Prefetch: fill the warmup buffer in parallel with decoder init.
            FireStartupPrefetchIfNeeded(initialBytes);

            _preloadTask = Task.Run(
                () => PreloadLoopAsync(_lifetimeCts.Token), _lifetimeCts.Token);

            Log.Info($"[CachingSource] Initialized: duration={DurationMs}ms, " +
                     $"cached={_cacheEntry.DownloadProgress:F0}%");
            return true;
        }
        catch (Exceptions.CdnUnavailableException)
        {
            // Pass through unchanged to add host to CdnBlacklist and perform failover retry
            throw;
        }
        catch (Exceptions.UrlExpiredException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"[CachingSource] Init failed: {ex.Message}", ex);
            return false;
        }
    }

    // --- Startup Prefetch ---

    /// <summary>
    /// Starts an immediate fire-and-forget prefetch right after successful parser init.
    /// <para>
    /// Bridges the dead time between <see cref="InitializeAsync"/> completion
    /// and the first iteration of the preload loop (<see cref="StreamingConfig.PreloadIntervalMs"/>).
    /// Without this, on channels with a 1–2.5 s TTFB, warmup waits 500 ms + N round-trips,
    /// delaying playback by 1–1.5 s.
    /// </para>
    /// </summary>
    /// <param name="alreadyFetchedBytes">
    /// Amount of data already retrieved by the initial fetch. Prefetch starts seamlessly right after it.
    /// </param>
    private void FireStartupPrefetchIfNeeded(int alreadyFetchedBytes)
    {
        if (_cacheEntry is { IsComplete: true })
            return;

        long prefetchStart = alreadyFetchedBytes;
        long remaining = _contentLength - prefetchStart;
        if (remaining <= 0)
            return;

        int prefetchLength = (int)Math.Min(_config.StartupPrefetchBytes, remaining);
        if (prefetchLength <= 0)
            return;

        Log.Debug(
            $"[CachingSource] Startup prefetch: {prefetchLength / 1024}KB " +
            $"at offset {prefetchStart} (initial={alreadyFetchedBytes / 1024}KB)");

        _ = SafeStartupPrefetchAsync(prefetchStart, prefetchLength);
    }

    /// <summary>
    /// Best-effort background prefetch to populate the warmup buffer.
    /// Errors do not abort initialization — the preload loop will pick up remaining data.
    /// </summary>
    private async Task SafeStartupPrefetchAsync(long start, int length)
    {
        try
        {
            var token = CurrentDownloadToken;
            await EnsureRangeAsync(start, length, token, isCritical: false)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Debug($"[CachingSource] Startup prefetch failed (non-fatal): {ex.Message}");
        }
    }

    // --- Bootstrap Check ---

    /// <summary>
    /// Checks whether enough locally available data exists from the beginning of the file
    /// for a safe bootstrap start of the parser/decoder without making a network request.
    /// </summary>
    /// <param name="initialBytes">Minimum starting prefix size in bytes.</param>
    /// <returns>
    /// <c>true</c> if the contiguous local prefix starting from position 0 is sufficient;
    /// otherwise, <c>false</c>.
    /// </returns>
    private bool HasLocalInitialBootstrapData(int initialBytes)
    {
        if (_cacheEntry == null || initialBytes <= 0)
            return false;

        long contiguous = _cacheEntry.GetContiguousDownloadedBytesFrom(0);
        return contiguous >= initialBytes;
    }

    // --- Parser Factory ---

    /// <summary>Selects the container parser based on the track format.</summary>
    private IContainerParser CreateParser(Stream stream) => _format switch
    {
        AudioFormat.WebM or AudioFormat.Ogg => new WebMContainerParser(stream),
        AudioFormat.Mp4 => new Mp4ContainerParser(stream),
        _ => throw new NotSupportedException($"Format not supported: {_format}")
    };
}