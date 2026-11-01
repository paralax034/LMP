using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Http;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Normalization;

namespace LMP.Core.Services;

public sealed partial class AudioEngine
{
    private string? _lastSoftRefreshTrackId;
    private long _lastSoftRefreshTick;

    /// <summary>Время жизни списка клиентов, исключённых после CDN 403 (мс).</summary>
    private const long CdnForbiddenClientsTtlMs = 10 * 60 * 1000;

    /// <summary>Защищает состояние клиентов, чьи URL получили CDN 403 для текущего трека.</summary>
    private readonly Lock _cdnForbiddenClientsLock = new();

    /// <summary>Трек, к которому относится <see cref="_cdnForbiddenClients"/>.</summary>
    private string? _cdnForbiddenClientsTrackId;

    /// <summary>Момент последней регистрации CDN 403 (<see cref="Environment.TickCount64"/>).</summary>
    private long _cdnForbiddenClientsTick;

    /// <summary>InnerTube-клиенты (параметр <c>c</c> stream URL), чьи URL CDN отверг с HTTP 403.</summary>
    private readonly List<string> _cdnForbiddenClients = new(4);

    #region Stream Resolution

    /// <summary>
    /// Разрешает источник аудиопотока для воспроизведения трека.
    /// </summary>
    /// <param name="track">Метаданные трека.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <param name="seekPosition">Начальная позиция воспроизведения при перемотке.</param>
    /// <returns>Дескриптор готового аудиопотока.</returns>
    private async Task<ResolvedStreamDescriptor> ResolveStreamAsync(
        TrackInfo track,
        CancellationToken ct,
        TimeSpan? seekPosition = null)
    {
        var requested = StreamSelectionHint.FromTrack(track, _library.Settings.RememberTrackFormat);

        Log.Debug(
            $"[AudioEngine] ResolveStreamAsync start: track={track.Id}, seek={seekPosition?.TotalMilliseconds ?? 0}ms, requestedFormat={requested.Format?.ToContainerName() ?? "-"}, requestedBitrate={requested.BitrateKbps}");

        var rawId = track.GetRawIdSpan().ToString();

        // --- Path 0: Downloaded local files (Downloads folder) ---
        if (track.IsDownloaded && !string.IsNullOrEmpty(track.LocalPath) && File.Exists(track.LocalPath))
        {
            var downloadedFormat = AudioSourceFactory.DetectFormat(track.LocalPath);
            if (downloadedFormat == AudioFormat.Unknown) downloadedFormat = AudioFormat.WebM;

            bool isUserOverrodeFormat = requested.HasFormat && requested.Format != downloadedFormat;

            if (!isUserOverrodeFormat)
            {
                var fileInfo = new FileInfo(track.LocalPath);
                var codec = AudioSourceFactory.GetCodecForFormat(downloadedFormat);
                int fileBitrateKbps = track.Duration.TotalSeconds > 0
                    ? Math.Max((int)(fileInfo.Length * 8 / track.Duration.TotalSeconds / 1000), 32)
                    : 128;

                var descriptor = new ResolvedStreamDescriptor
                {
                    TrackId = track.Id,
                    Url = track.LocalPath,
                    Format = downloadedFormat,
                    Codec = codec,
                    BitrateKbps = fileBitrateKbps,
                    ContentLengthBytes = fileInfo.Length,
                    Origin = StreamSource.DiskCacheFull
                };

                Log.Info($"[AudioEngine] ResolveStreamAsync LOCAL DOWNLOAD -> {descriptor}");
                TryEnrichIntegratedLufsFromLocalSources(track);
                return descriptor;
            }
        }

        // --- Path 1: Full disk cache (exact match by format+bitrate bucket) ---
        if (requested.HasFormat && requested.HasBitrate)
        {
            string exactCacheKey =
                AudioSourceFactory.BuildCacheKey(track.Id, requested.Format!.Value, requested.BitrateKbps);
            if (AudioSourceFactory.GlobalCache.IsFullyCached(exactCacheKey))
            {
                var exactEntry = AudioSourceFactory.GlobalCache.GetCacheInfo(exactCacheKey);
                if (exactEntry != null && IsCacheSizePlausible(exactEntry, track.Duration))
                {
                    TrackNormalizationHydrator.HydrateNormalization(track, exactEntry);
                    TryEnrichIntegratedLufsFromLocalSources(track);

                    var descriptor = new ResolvedStreamDescriptor
                    {
                        TrackId = track.Id,
                        Url = "",
                        Format = exactEntry.Format,
                        Codec = exactEntry.Codec,
                        BitrateKbps = exactEntry.Bitrate,
                        ContentLengthBytes = exactEntry.TotalSize,
                        Origin = StreamSource.DiskCacheFull
                    };

                    Log.Info($"[AudioEngine] ResolveStreamAsync FULL CACHE (exact) -> {descriptor}");
                    return descriptor;
                }
            }
        }

        // --- Path 1b: Full disk cache (any format, no user preference) ---
        if (!requested.HasFormat)
        {
            var fullCache = AudioSourceFactory.FindAnyCachedTrack(track.Id)
                            ?? (rawId != track.Id ? AudioSourceFactory.FindAnyCachedTrack(rawId) : null);

            if (fullCache != null && IsCacheSizePlausible(fullCache.Value.Entry, track.Duration))
            {
                var entry = fullCache.Value.Entry;
                TrackNormalizationHydrator.HydrateNormalization(track, entry);
                TryEnrichIntegratedLufsFromLocalSources(track);

                var descriptor = new ResolvedStreamDescriptor
                {
                    TrackId = track.Id,
                    Url = "",
                    Format = entry.Format,
                    Codec = entry.Codec,
                    BitrateKbps = entry.Bitrate,
                    ContentLengthBytes = entry.TotalSize,
                    Origin = StreamSource.DiskCacheFull
                };

                Log.Info($"[AudioEngine] ResolveStreamAsync FULL CACHE -> {descriptor}");
                return descriptor;
            }
        }

        // --- Path 2: Partial cache fast-start ---
        var bootstrapCache = TryGetPartialBootstrapCache(track, seekPosition);
        if (bootstrapCache != null)
        {
            TrackNormalizationHydrator.HydrateNormalization(track, bootstrapCache);
            TryEnrichIntegratedLufsFromLocalSources(track);

            if (TryGetCompatibleContinuationUrl(track, bootstrapCache, out var eagerUrl))
            {
                Log.Info($"[AudioEngine] Partial-cache fast start with eager continuation URL: {track.Id}");

                var descriptor = new ResolvedStreamDescriptor
                {
                    TrackId = track.Id,
                    Url = eagerUrl,
                    Format = bootstrapCache.Format,
                    Codec = bootstrapCache.Codec,
                    BitrateKbps = bootstrapCache.Bitrate,
                    ContentLengthBytes = bootstrapCache.TotalSize,
                    Origin = StreamSource.DiskCachePartial
                };

                Log.Info($"[AudioEngine] ResolveStreamAsync PARTIAL CACHE (eager URL) -> {descriptor}");
                return descriptor;
            }

            _ = PrimeContinuationUrlAsync(track, bootstrapCache, ct);

            Log.Info($"[AudioEngine] Partial-cache fast start: {track.Id}");

            var lazyDescriptor = new ResolvedStreamDescriptor
            {
                TrackId = track.Id,
                Url = "",
                Format = bootstrapCache.Format,
                Codec = bootstrapCache.Codec,
                BitrateKbps = bootstrapCache.Bitrate,
                ContentLengthBytes = bootstrapCache.TotalSize,
                Origin = StreamSource.DiskCachePartial
            };

            Log.Info($"[AudioEngine] ResolveStreamAsync PARTIAL CACHE (lazy URL) -> {lazyDescriptor}");
            return lazyDescriptor;
        }

        ct.ThrowIfCancellationRequested();

        // --- Path 3: Session cache (disk manifest with HEAD probe) ---
        var diskEntry = await SessionCacheStore
            .TryGetManifestAndProbeAsync(track.Id, SharedHttpClient.Instance, ct)
            .ConfigureAwait(false);

        if (diskEntry is { Variants.Count: > 0 })
        {
            var cacheEntry = FindNormalizationCacheEntry(track.Id);
            if (cacheEntry != null)
                TrackNormalizationHydrator.HydrateNormalization(track, cacheEntry);

            var selectedVariant = SelectBestVariantFromEntry(diskEntry.Variants, requested.Format);
            if (selectedVariant != null)
            {
                var format = selectedVariant.Format;
                var codec = selectedVariant.CodecType != AudioCodec.Unknown
                    ? selectedVariant.CodecType
                    : AudioSourceFactory.GetCodecForFormat(format);

                var descriptor = new ResolvedStreamDescriptor
                {
                    TrackId = track.Id,
                    Itag = selectedVariant.Itag,
                    Format = format,
                    Codec = codec,
                    BitrateKbps = selectedVariant.Bitrate / 1000,
                    ContentLengthBytes = selectedVariant.Clen,
                    Url = selectedVariant.Url,
                    ExpireUtc = diskEntry.ExpireUtc,
                    CdnHost = diskEntry.CdnHost,
                    IntegratedLufs = diskEntry.IntegratedLufs,
                    LanguageCode = selectedVariant.LanguageCode,
                    IsDefaultLanguage = selectedVariant.IsDefaultLanguage,
                    Origin = StreamSource.SessionCache
                };

                Log.Info($"[AudioEngine] Session cache hit: {track.Id} -> {descriptor}");
                return descriptor;
            }
        }

        // --- Path 4: Provider memory cache (RAM manifest) ---
        var memDescriptor = _youtube.TryGetCachedStreamDescriptor(
            track.Id,
            requested.Format,
            requested.BitrateKbps);

        if (memDescriptor != null)
        {
            if (memDescriptor.Value.ExpireUtc == default ||
                DateTime.UtcNow.AddMinutes(5) < memDescriptor.Value.ExpireUtc)
            {
                var cacheEntry = FindNormalizationCacheEntry(track.Id);
                if (cacheEntry != null)
                    TrackNormalizationHydrator.HydrateNormalization(track, cacheEntry);

                var descriptor = memDescriptor.Value with { TrackId = track.Id };
                Log.Info($"[AudioEngine] Provider memory cache hit: {track.Id} -> {descriptor}");
                return descriptor;
            }
            else
            {
                Log.Debug($"[AudioEngine] Provider memory cache hit ignored due to ExpireUtc passed: {track.Id}");
            }
        }

        // --- Path 5: YouTube API call (cold path) ---
        var freshDescriptor = await _youtube.RefreshStreamAsync(track, false, ct).ConfigureAwait(false)
                              ?? throw new InvalidOperationException($"Failed to resolve stream URL for {track.Id}");

        Log.Info($"[AudioEngine] ResolveStreamAsync YOUTUBE API -> {freshDescriptor}");
        return freshDescriptor;
    }

    #endregion

    #region URL Acquisition & Continuation

    /// <summary>
    /// Возвращает текущую single-flight задачу получения continuation URL трека либо запускает новую.
    /// </summary>
    /// <param name="track">Трек, для которого нужен continuation URL.</param>
    /// <param name="variant">
    /// Вариант (контейнер/битрейт), байты которого уже лежат в source и partial cache;
    /// <c>null</c> — предпочтение трека (прежнее поведение).
    /// </param>
    /// <param name="expectedContentLength">Полный размер этого варианта (<c>clen</c>); <c>0</c>, если неизвестен.</param>
    private Task<ContinuationUrlResult?> GetOrStartContinuationUrlAcquisitionAsync(
        TrackInfo track,
        StreamSelectionHint? variant,
        long expectedContentLength)
    {
        if (_pendingUrlAcquisitions.TryGetValue(track.Id, out var existing))
            return existing;

        var sessionToken = GetSessionToken();
        var created = AcquireContinuationUrlCoreAsync(track, variant, expectedContentLength, sessionToken);

        if (_pendingUrlAcquisitions.TryAdd(track.Id, created))
        {
            _ = RemovePendingContinuationAcquisitionAsync(track.Id, created);
            return created;
        }

        return _pendingUrlAcquisitions[track.Id];
    }

    /// <summary>
    /// Получает continuation URL ровно того варианта, который уже частично лежит в кэше:
    /// session-манифест выбирается по точному <c>clen</c>, refresh выполняется с подсказкой варианта source.
    /// URL другого варианта не возвращается: guard source всё равно отверг бы его и воспроизведение встало бы.
    /// </summary>
    /// <param name="track">Трек, для которого нужен continuation URL.</param>
    /// <param name="variant">Вариант source; <c>null</c> — предпочтение трека.</param>
    /// <param name="expectedContentLength">Ожидаемый <c>clen</c>; <c>0</c>, если неизвестен.</param>
    /// <param name="ct">Токен сессии воспроизведения.</param>
    private async Task<ContinuationUrlResult?> AcquireContinuationUrlCoreAsync(
        TrackInfo track,
        StreamSelectionHint? variant,
        long expectedContentLength,
        CancellationToken ct)
    {
        var requested = variant is { HasFormat: true } exactVariant
            ? exactVariant
            : StreamSelectionHint.FromTrack(track, _library.Settings.RememberTrackFormat);

        var diskEntry = await SessionCacheStore
            .TryGetManifestAndProbeAsync(track.Id, SharedHttpClient.Instance, ct)
            .ConfigureAwait(false);

        if (diskEntry != null)
        {
            var selectedVariant = SelectContinuationVariantFromEntry(diskEntry.Variants, requested, expectedContentLength);
            if (selectedVariant != null)
            {
                return new ContinuationUrlResult(
                    selectedVariant.Url,
                    selectedVariant.Clen,
                    selectedVariant.Bitrate / 1000,
                    selectedVariant.Format,
                    selectedVariant.CodecType,
                    diskEntry.IntegratedLufs);
            }
        }

        var descriptor = await _youtube.RefreshStreamAsync(track, false, ct, variant).ConfigureAwait(false);
        if (descriptor is null || !descriptor.Value.HasLiveUrl)
            return null;

        var d = descriptor.Value;

        if (expectedContentLength > 0 && d.ContentLengthBytes > 0 && d.ContentLengthBytes != expectedContentLength)
        {
            Log.Warn($"[AudioEngine] Continuation URL for {track.Id} skipped: variant {d.Format}/{d.BitrateKbps}kbps " +
                     $"(itag={d.Itag}, clen={d.ContentLengthBytes}) does not match cached clen={expectedContentLength}");
            return null;
        }

        return new ContinuationUrlResult(
            d.Url,
            d.ContentLengthBytes,
            d.BitrateKbps,
            d.Format,
            d.Codec,
            d.IntegratedLufs);
    }

    private async Task RemovePendingContinuationAcquisitionAsync(
        string trackId,
        Task<ContinuationUrlResult?> task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }

        _pendingUrlAcquisitions.TryRemove(
            new KeyValuePair<string, Task<ContinuationUrlResult?>>(trackId, task));
    }

    private async ValueTask<TrackInfo?> TryResolveActiveTrackForCallbackAsync(
        string trackId,
        CancellationToken ct)
    {
        if (IsSealedFailedTrack(trackId)) return null;

        var track = (CurrentTrack?.Id == trackId ? CurrentTrack : null)
                    ?? _trackRegistry.TryGet(trackId)
                    ?? await _library.GetTrackAsync(trackId, ct).ConfigureAwait(false);

        return (track == null || IsSealedFailedTrack(trackId)) ? null : track;
    }

    /// <summary>
    /// Выбирает вариант session-манифеста для continuation URL. При известном размере допускается только точное
    /// совпадение контейнера и <c>clen</c>: соседний вариант того же контейнера (например, itag 250 вместо 251)
    /// имеет другие байтовые смещения, и guard source всё равно его отвергнет.
    /// </summary>
    /// <param name="variants">Варианты session-манифеста.</param>
    /// <param name="requested">Подсказка варианта (контейнер/битрейт).</param>
    /// <param name="expectedContentLength">Ожидаемый <c>clen</c>; <c>0</c> — выбор только по контейнеру (прежнее поведение).</param>
    /// <returns>Подходящий вариант либо <c>null</c>.</returns>
    internal static VariantEntry? SelectContinuationVariantFromEntry(
        List<VariantEntry> variants,
        StreamSelectionHint requested,
        long expectedContentLength)
    {
        ArgumentNullException.ThrowIfNull(variants);

        if (expectedContentLength <= 0 || !requested.HasFormat)
            return SelectBestVariantFromEntry(variants, requested.Format);

        var blacklist = AudioSourceFactory.CdnBlacklist;
        VariantEntry? blockedMatch = null;

        for (int i = 0; i < variants.Count; i++)
        {
            var v = variants[i];
            if (v.Format != requested.Format || v.Clen != expectedContentLength)
                continue;

            if (!blacklist.IsBlockedUrl(v.Url))
                return v;

            blockedMatch ??= v;
        }

        return blockedMatch;
    }

    /// <summary>
    /// Единая точка входа source за stream URL. Запрос явно несёт трек и вариант потока source-инициатора,
    /// поэтому engine не угадывает его по активному pipeline: это верно и во время <c>InitializeAsync</c>,
    /// когда pipeline ещё не опубликован в <c>AudioPlayer</c>.
    /// </summary>
    /// <param name="request">Запрос source.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Live URL совместимого варианта либо <c>null</c>.</returns>
    private ValueTask<string?> ProvideStreamUrlAsync(StreamUrlRequest request, CancellationToken ct) =>
        request.Kind == StreamUrlRequestKind.Refresh
            ? RefreshUrlCallbackAsync(request, ct)
            : AcquireUrlCallbackAsync(request, ct);

    /// <summary>
    /// Проверяет, относится ли сохранённый список исключённых клиентов к треку и не истёк ли TTL. Вызывать под lock.
    /// </summary>
    /// <param name="trackId">Идентификатор трека.</param>
    private bool IsCdnForbiddenStateCurrent(string trackId) =>
        string.Equals(_cdnForbiddenClientsTrackId, trackId, StringComparison.Ordinal)
        && Environment.TickCount64 - _cdnForbiddenClientsTick < CdnForbiddenClientsTtlMs;

    /// <summary>
    /// Возвращает снимок клиентов, исключённых после CDN 403 для трека; пустой массив, если исключений нет.
    /// </summary>
    /// <param name="trackId">Идентификатор трека.</param>
    private string[] GetCdnForbiddenClients(string trackId)
    {
        lock (_cdnForbiddenClientsLock)
        {
            return IsCdnForbiddenStateCurrent(trackId) ? _cdnForbiddenClients.ToArray() : Array.Empty<string>();
        }
    }

    /// <summary>
    /// Регистрирует клиента, чей URL отвергнут CDN с HTTP 403, и возвращает актуальный список исключений трека.
    /// </summary>
    /// <param name="trackId">Идентификатор трека.</param>
    /// <param name="clientName">Значение параметра <c>c</c> отвергнутого URL; <c>null</c> — только продление TTL.</param>
    private string[] RegisterCdnForbiddenClient(string trackId, string? clientName)
    {
        lock (_cdnForbiddenClientsLock)
        {
            if (!IsCdnForbiddenStateCurrent(trackId))
            {
                _cdnForbiddenClients.Clear();
                _cdnForbiddenClientsTrackId = trackId;
            }

            _cdnForbiddenClientsTick = Environment.TickCount64;

            if (!string.IsNullOrWhiteSpace(clientName)
                && !_cdnForbiddenClients.Exists(c => string.Equals(c, clientName, StringComparison.OrdinalIgnoreCase)))
            {
                _cdnForbiddenClients.Add(clientName);
            }

            return _cdnForbiddenClients.ToArray();
        }
    }

    /// <summary>
    /// Возвращает URL обновлённого дескриптора, только если он адресует тот же вариант потока, что и source.
    /// </summary>
    /// <param name="request">Запрос source, описывающий ожидаемый вариант потока.</param>
    /// <param name="descriptor">Результат refresh.</param>
    /// <param name="trackId">Идентификатор трека (для диагностики).</param>
    /// <param name="stage">Стадия refresh (для диагностики).</param>
    /// <returns>Совместимый live URL либо <c>null</c>.</returns>
    private static string? AcceptRefreshedUrl(
        StreamUrlRequest request,
        ResolvedStreamDescriptor? descriptor,
        string trackId,
        string stage)
    {
        if (descriptor is not { HasLiveUrl: true } d)
            return null;

        if (!request.IsCompatible(d.Format, d.ContentLengthBytes))
        {
            Log.Warn($"[AudioEngine] {stage} refresh for {trackId} returned incompatible variant " +
                     $"({d.Format}/{d.BitrateKbps}kbps, itag={d.Itag}, clen={d.ContentLengthBytes}); source expects " +
                     $"{request.Format}/{request.BitrateKbps}kbps, clen={request.ContentLength}");
            return null;
        }

        return d.Url;
    }

    /// <summary>
    /// Обновляет URL варианта потока source: soft refresh, при повторном CDN 403 — force refresh с исключением клиента.
    /// </summary>
    /// <param name="request">Запрос source с <see cref="StreamUrlRequestKind.Refresh"/>.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Совместимый live URL либо <c>null</c>.</returns>
    private async ValueTask<string?> RefreshUrlCallbackAsync(StreamUrlRequest request, CancellationToken ct)
    {
        string trackId = request.TrackId;

        var track = await TryResolveActiveTrackForCallbackAsync(trackId, ct).ConfigureAwait(false);
        if (track == null)
        {
            Log.Warn($"[AudioEngine] URL refresh skipped for {trackId}: track is sealed after a fatal error or unresolvable");
            return null;
        }

        var sessionToken = GetSessionToken();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, sessionToken);

        // Обновляем ровно тот вариант (контейнер/битрейт), чьи байты уже лежат в source и partial cache,
        // а не текущее предпочтение пользователя: подмена WebM на MP4 ломает байтовые смещения.
        StreamSelectionHint? variant = request.ToSelectionHint();

        bool isForbiddenDriven = request.IsForbiddenDriven;

        // URL, выданный для другого внешнего IP (смена узла VPN/сети), CDN отвергает при любом клиенте:
        // клиент не виноват и не исключается, а свежий URL получает обычный soft refresh.
        bool isEgressMismatch = isForbiddenDriven
            && !LMP.Core.Youtube.Utils.StreamUrlEgressGuard.Shared.IsBoundToCurrentEgress(request.CurrentUrl);
        bool blameClient = isForbiddenDriven && !isEgressMismatch;

        // Клиент, чей URL только что получил HTTP 403 от CDN (параметр c=...).
        string? failedClient = blameClient
            ? UrlEx.TryGetQueryParameterValue(request.CurrentUrl, "c")
            : null;

        if (isEgressMismatch)
        {
            Log.Warn($"[AudioEngine] 403 for {trackId}: URL is bound to ip=" +
                     $"{LMP.Core.Youtube.Utils.StreamUrlEgressGuard.TryGetBoundIp(request.CurrentUrl)}, " +
                     $"current egress ip={LMP.Core.Youtube.Utils.StreamUrlEgressGuard.Shared.ObservedIp}; client is not blamed");
        }

        try
        {
            SessionCacheStore.Invalidate(trackId);
            _youtube.InvalidateMemoryCache(trackId);

            long nowTick = Environment.TickCount64;
            bool hadRecentSoftRefresh = string.Equals(_lastSoftRefreshTrackId, trackId, StringComparison.Ordinal)
                && (nowTick - Volatile.Read(ref _lastSoftRefreshTick)) < 60_000;

            // Первый 403 трека — обычно истёкший URL: soft refresh тем же клиентом.
            // Повторный 403 означает, что CDN отвергает URL этого клиента — исключаем его.
            string[] excludedClients = GetCdnForbiddenClients(trackId);
            if (blameClient && (hadRecentSoftRefresh || excludedClients.Length > 0))
                excludedClients = RegisterCdnForbiddenClient(trackId, failedClient);

            if (!hadRecentSoftRefresh || isEgressMismatch)
            {
                _lastSoftRefreshTrackId = trackId;
                Volatile.Write(ref _lastSoftRefreshTick, nowTick);

                Log.Info($"[AudioEngine] {(isForbiddenDriven ? "403" : "TTL")} refresh: per-track caches invalidated for {trackId}");

                var descriptor = await _youtube
                    .RefreshStreamAsync(track, false, linked.Token, variant, excludedClients.Length > 0 ? excludedClients : null)
                    .ConfigureAwait(false);

                string? softUrl = AcceptRefreshedUrl(request, descriptor, trackId, "Soft");
                if (softUrl != null)
                {
                    Log.Info($"[AudioEngine] Soft refresh returned new URL for {trackId}");
                    return softUrl;
                }
            }

            if (blameClient)
                excludedClients = RegisterCdnForbiddenClient(trackId, failedClient);

            Log.Warn($"[AudioEngine] Escalating 403 refresh to force refresh (PoToken + client rotation) for {trackId}, " +
                     $"failed client: {failedClient ?? "-"}, " +
                     $"excluded clients: {(excludedClients.Length == 0 ? "-" : string.Join(",", excludedClients))}");
            _lastSoftRefreshTrackId = null;

            // После CDN 403 force refresh всегда идёт по CDN-403 порядку (WEB первым), даже если клиент
            // не распознан: ANDROID_VR первым почти гарантированно ловит bot detection и уходит в карантин на 2 часа.
            IReadOnlyList<string>? forcedExclusions = isForbiddenDriven || excludedClients.Length > 0
                ? excludedClients
                : null;

            var forcedDescriptor = await _youtube
                .RefreshStreamAsync(track, true, linked.Token, variant, forcedExclusions)
                .ConfigureAwait(false);

            return AcceptRefreshedUrl(request, forcedDescriptor, trackId, "Force");
        }
        catch (Exception) when (linked.IsCancellationRequested
                                || sessionToken.IsCancellationRequested
                                || !string.Equals(CurrentTrack?.Id, trackId, StringComparison.Ordinal))
        {
            Log.Debug($"[AudioEngine] URL refresh for {trackId} abandoned: cancelled or track switched");
            return null;
        }
        catch (Exception ex)
        {
            // Ошибка фонового обновления URL (например, временный обрыв сети/VPN) не должна убивать и убирать играющий трек;
            // возврат null позволяет CachingStreamSource перейти в режим ожидания восстановления сети.
            Log.Warn($"[AudioEngine] URL refresh failed for {trackId} (non-fatal, awaiting network recovery): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Получает continuation URL для варианта потока partial-cache source.
    /// </summary>
    /// <param name="request">Запрос source с <see cref="StreamUrlRequestKind.Acquire"/>.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Live URL либо <c>null</c>.</returns>
    private async ValueTask<string?> AcquireUrlCallbackAsync(StreamUrlRequest request, CancellationToken ct)
    {
        // Continuation URL запрашивается для варианта source (partial cache), а не для предпочтения трека:
        // иначе WebM-кэш получает MP4 URL, guard его отвергает, и воспроизведение встаёт навсегда.
        string trackId = request.TrackId;

        var track = await TryResolveActiveTrackForCallbackAsync(trackId, ct).ConfigureAwait(false);
        if (track == null) return null;

        try
        {
            var task = GetOrStartContinuationUrlAcquisitionAsync(
                track,
                request.ToSelectionHint(),
                request.ContentLength);
            var result = await task.WaitAsync(ct).ConfigureAwait(false);
            return result?.Url;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception) when (!string.Equals(CurrentTrack?.Id, trackId, StringComparison.Ordinal))
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Debug($"[AudioEngine] Acquire URL skipped for {trackId}: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region Partial Cache Fast Start

    private static int ComputePartialCacheBootstrapBytes(int bitrateKbps)
    {
        double bitrateBytesPerSec = Math.Max(1, bitrateKbps) * 1000.0 / 8.0;
        int bytes = (int)Math.Ceiling(bitrateBytesPerSec * PartialCacheBootstrapTargetMs / 1000.0);
        return Math.Clamp(bytes, PartialCacheBootstrapMinBytes, PartialCacheBootstrapMaxBytes);
    }

    private AudioCacheEntry? TryGetPartialBootstrapCache(TrackInfo track, TimeSpan? seekPosition)
    {
        if (seekPosition is { TotalMilliseconds: > 0 })
            return null;

        var cacheManager = AudioSourceFactory.GlobalCache;
        if (cacheManager == null)
            return null;

        int bitrateHint = StreamSelectionHint.FromTrack(track, _library.Settings.RememberTrackFormat).BitrateKbps;
        if (bitrateHint <= 0)
            bitrateHint = 160;

        int requiredBytes = ComputePartialCacheBootstrapBytes(bitrateHint);
        return cacheManager.FindBestStartupCache(track.Id, requiredBytes);
    }

    private bool TryAttachPrimedContinuationUrlToActiveSource(TrackInfo track, string url)
    {
        if (_disposed || string.IsNullOrWhiteSpace(url))
            return false;

        var current = CurrentTrack;
        if (current == null || !string.Equals(current.Id, track.Id, StringComparison.Ordinal))
            return false;

        var pipeline = _player.GetActivePipeline();
        if (pipeline?.Source is not Audio.Sources.CachingStreamSource cachingSource)
            return false;

        if (cachingSource.TryAttachContinuationUrl(url))
        {
            Log.Debug($"[AudioEngine] Primed continuation URL attached to live source: {track.Id}");
            return true;
        }

        return false;
    }

    private async Task PrimeContinuationUrlAsync(
        TrackInfo track,
        AudioCacheEntry expectedEntry,
        CancellationToken ct)
    {
        try
        {
            if (ct.IsCancellationRequested || IsSealedFailedTrack(track.Id))
                return;

            if (TryGetCompatibleContinuationUrl(track, expectedEntry, out var existingUrl))
            {
                TryAttachPrimedContinuationUrlToActiveSource(track, existingUrl);
                return;
            }

            var result = await GetOrStartContinuationUrlAcquisitionAsync(
                    track,
                    new StreamSelectionHint(expectedEntry.Format, expectedEntry.Bitrate),
                    expectedEntry.TotalSize)
                .WaitAsync(ct)
                .ConfigureAwait(false);

            if (result == null || string.IsNullOrEmpty(result.Value.Url))
                return;

            if (!IsContinuationVariantCompatible(expectedEntry, result.Value.Format, result.Value.Bitrate))
            {
                Log.Warn($"[AudioEngine] Continuation priming variant mismatch for {track.Id}: " +
                         $"expected={expectedEntry.Format}/{expectedEntry.Bitrate}kbps, " +
                         $"actual={result.Value.Format}/{result.Value.Bitrate}kbps");
                return;
            }

            TryAttachPrimedContinuationUrlToActiveSource(track, result.Value.Url);

            if (float.IsFinite(result.Value.IntegratedLufs))
            {
                CommitIntegratedLufs(
                    track.Id,
                    result.Value.IntegratedLufs,
                    LoudnessSource.YoutubePerceptual);

                UpdateRunningPipelineGain(track.Id, result.Value.IntegratedLufs);
            }

            Log.Info($"[AudioEngine] Partial-cache continuation primed: {track.Id} " +
                     $"({result.Value.Codec.ToDisplayName()}/{result.Value.Bitrate}kbps)");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Debug($"[AudioEngine] Continuation priming skipped for {track.Id}: {ex.Message}");
        }
    }

    #endregion

    #region Cache & Metadata Helpers

    /// <summary>
    /// Выбирает подходящий стерео-вариант из записи манифеста, отсекая многоканальные (Surround 5.1) потоки и хосты из CDN blacklist.
    /// </summary>
    private static VariantEntry? SelectBestVariantFromEntry(
        List<VariantEntry> variants,
        AudioFormat? preferredFormat)
    {
        if (variants.Count == 0) return null;

        var blacklist = AudioSourceFactory.CdnBlacklist;

        // Предикат допустимости: только стерео/моно потоки (YouTube itag 328/338 и 5.1 surround исключаются)
        static bool IsStereoCompatible(VariantEntry v) =>
            v.Itag is not (325 or 328 or 338);

        // 1. Поиск по предпочитаемому формату среди не заблокированных стерео-потоков
        if (preferredFormat is { } fmt && fmt != AudioFormat.Unknown)
        {
            for (int i = 0; i < variants.Count; i++)
            {
                var v = variants[i];
                if (v.Format != fmt || !IsStereoCompatible(v)) continue;
                if (!blacklist.IsBlockedUrl(v.Url)) return v;
            }

            for (int i = 0; i < variants.Count; i++)
            {
                var v = variants[i];
                if (v.Format == fmt && IsStereoCompatible(v)) return v;
            }
        }

        // 2. Без привязки к формату: любой совместимый не заблокированный стерео-поток
        for (int i = 0; i < variants.Count; i++)
        {
            var v = variants[i];
            if (IsStereoCompatible(v) && !blacklist.IsBlockedUrl(v.Url)) return v;
        }

        // 3. Fallback: любой стерео-поток (даже если CDN в blacklist)
        for (int i = 0; i < variants.Count; i++)
        {
            var v = variants[i];
            if (IsStereoCompatible(v)) return v;
        }

        // 4. Крайний fallback на первый элемент списка
        Log.Warn("[AudioEngine] No stereo-compatible variants found in manifest — falling back to first variant");
        return variants[0];
    }

    private static bool IsContinuationVariantCompatible(
        AudioCacheEntry expectedEntry,
        AudioFormat format,
        int bitrate)
    {
        if (format == AudioFormat.Unknown)
            return false;

        if (format != expectedEntry.Format)
            return false;

        if (bitrate <= 0 || expectedEntry.Bitrate <= 0)
            return true;

        string candidateKey = AudioSourceFactory.BuildCacheKey(
            expectedEntry.TrackId,
            format,
            bitrate);

        return string.Equals(candidateKey, expectedEntry.CacheKey, StringComparison.Ordinal);
    }

    private bool TryGetCompatibleContinuationUrl(
        TrackInfo track,
        AudioCacheEntry expectedEntry,
        out string url)
    {
        url = string.Empty;

        var manifest = SessionCacheStore.GetManifest(track.Id);

        if (manifest != null && IsContinuationUrlLikelyFresh(manifest))
        {
            for (int i = 0; i < manifest.Variants.Count; i++)
            {
                var variant = manifest.Variants[i];
                if (IsContinuationVariantCompatible(expectedEntry, variant.Format, variant.Bitrate / 1000))
                {
                    url = variant.Url;
                    return true;
                }
            }
        }

        var rawId = track.GetRawIdSpan().ToString();
        var descriptor = _youtube.TryGetCachedStreamDescriptor(
            rawId,
            expectedEntry.Format,
            expectedEntry.Bitrate);

        if (descriptor is { HasLiveUrl: true } d &&
            IsContinuationVariantCompatible(expectedEntry, d.Format, d.BitrateKbps))
        {
            if (d.ExpireUtc == default || DateTime.UtcNow.AddMinutes(5) < d.ExpireUtc)
            {
                url = d.Url;
                return true;
            }
        }

        return false;
    }

    private static bool IsContinuationUrlLikelyFresh(
        TrackManifestEntry manifest,
        int safetyBufferMinutes = 5)
    {
        if (manifest.ExpireUtc == default || manifest.ExpireUtc == DateTime.MinValue)
            return true;

        return DateTime.UtcNow.AddMinutes(safetyBufferMinutes) < manifest.ExpireUtc;
    }

    private static AudioCacheEntry? FindNormalizationCacheEntry(string trackId)
    {
        var cache = AudioSourceFactory.GlobalCache;
        if (cache == null || string.IsNullOrEmpty(trackId))
            return null;

        return cache.FindBestCacheByTrackId(trackId) ?? cache.FindBestStartupCache(trackId, 0);
    }

    private static void TryEnrichIntegratedLufsFromLocalSources(TrackInfo track)
    {
        if (track.HasIntegratedLufs)
            return;

        var manifest = SessionCacheStore.GetManifest(track.Id);
        if (manifest is null)
            return;

        if (!float.IsFinite(manifest.IntegratedLufs))
            return;

        track.SetIntegratedLufs(
            manifest.IntegratedLufs,
            LoudnessSource.YoutubePerceptual);

        AudioSourceFactory.GlobalCache.TryUpdateIntegratedLufs(
            track.Id,
            manifest.IntegratedLufs,
            LoudnessSource.YoutubePerceptual);

        Log.Debug($"[AudioEngine] Enriched LUFS from SessionCache: {track.Id} → {manifest.IntegratedLufs:F2} LUFS");
    }

    /// <summary>
    /// Проверяет, является ли размер закэшированного файла правдоподобным для указанной длительности трека.
    /// </summary>
    /// <param name="entry">Запись кэша.</param>
    /// <param name="duration">Длительность трека из метаданных.</param>
    /// <returns><c>true</c>, если размер файла соответствует физике кодека.</returns>
    private static bool IsCacheSizePlausible(AudioCacheEntry entry, TimeSpan duration)
    {
        if (duration <= TimeSpan.FromSeconds(5))
            return entry.TotalSize > 32 * 1024;

        int bitrate = entry.Bitrate > 0 ? entry.Bitrate : 128;
        long minExpectedBytes = (long)(duration.TotalSeconds * bitrate * 1000.0 / 8.0 * 0.50);
        return entry.TotalSize >= minExpectedBytes;
    }

    #endregion
}
