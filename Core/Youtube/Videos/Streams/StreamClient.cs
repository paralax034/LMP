using System.Globalization;
using System.Runtime.CompilerServices;
using LMP.Core.Youtube.Bridge;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Core.Youtube.Bridge.NToken;
using LMP.Core.Youtube.Bridge.SigCipher;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Helpers.Extensions;
using LMP.Core.Youtube.Videos.ClosedCaptions;
using LMP.Core.Youtube.Bridge.PoToken;
using LMP.Core.Audio.Http;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Youtube.Videos.Streams;

/// <summary>
/// Обеспечивает доступ к медиа-потокам YouTube видео.
/// <para>
/// <b>Кэш-стратегия (3-уровневая):</b>
/// </para>
/// <list type="number">
///   <item>
///     <b>Disk manifest cache</b> (<see cref="SessionCacheStore"/>):
///     Полный манифест (все варианты) сохраняется на диск после первого API call.
///     Переживает рестарт приложения. Инвалидируется по HTTP 403/410 при probe.
///   </item>
///   <item>
///     <b>Network resolve</b>: Только если disk cache пуст или probe провалился.
///     Один API call → полный манифест → записывается на диск.
///   </item>
///   <item>
///     <b>CDN connection pre-warming</b>: TCP+TLS prewarm к известным CDN-нодам
///     параллельно с API call.
///   </item>
/// </list>
/// </summary>
public sealed class StreamClient
{
    private readonly VideoController _controller;
    private readonly HttpClient _http;
    private readonly NTokenDecryptor _nTokenDecryptor;
    private readonly SigCipherDecryptor _sigCipherDecryptor;
    private readonly PlayerContextManager _playerContextManager;
    private readonly Func<bool>? _isAuthenticatedCheck;
    private readonly PoTokenProvider? _poTokenProvider;

    /// <summary>
    /// Создает экземпляр StreamClient.
    /// </summary>
    public StreamClient(
        HttpClient http,
        NTokenDecryptor nTokenDecryptor,
        SigCipherDecryptor sigCipherDecryptor,
        Func<bool>? isAuthenticatedCheck = null,
        PoTokenProvider? poTokenProvider = null)
    {
        _http = http;
        _nTokenDecryptor = nTokenDecryptor;
        _sigCipherDecryptor = sigCipherDecryptor;
        _isAuthenticatedCheck = isAuthenticatedCheck;
        _playerContextManager = sigCipherDecryptor.PlayerManager;
        _controller = new VideoController(http, _playerContextManager);
        _poTokenProvider = poTokenProvider;
    }

    /// <summary>
    /// Генерирует аудиопотоки из сырых данных ответа YouTube.
    /// Чистые аудиопотоки имеют абсолютный приоритет. Совмещённые потоки (видео + аудио, например itag 18)
    /// подключаются исключительно как аварийный резерв, если чистые потоки отсутствуют (SABR-блокировка).
    /// </summary>
    private async IAsyncEnumerable<IStreamInfo> GetAudioStreamInfosAsync(
      VideoId videoId,
      IEnumerable<IStreamData> streamDatas,
      string? clientName,
      [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var rawList = streamDatas as IReadOnlyList<IStreamData> ?? streamDatas.ToList();

        // Проверяем, есть ли хотя бы один чистый аудиопоток с доступным URL или подписью
        bool hasPureAudioStreams = false;
        for (int i = 0; i < rawList.Count; i++)
        {
            var s = rawList[i];
            if (s.MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true
                && (!string.IsNullOrEmpty(s.Url) || !string.IsNullOrEmpty(s.Signature)))
            {
                hasPureAudioStreams = true;
                break;
            }
        }

        bool? isNTokenDecryptionRequired = null;

        string? pot = null;
        bool skipPoToken = string.Equals(clientName, YoutubeClientNames.AndroidVr, StringComparison.Ordinal);

        if (_poTokenProvider != null && !skipPoToken)
        {
            try
            {
                pot = await _poTokenProvider
                    .GetContentTokenAsync(videoId.Value, cancellationToken)
                    .ConfigureAwait(false);

                if (!string.IsNullOrEmpty(pot))
                    Log.Debug($"[StreamClient] PoToken ready ({pot.Length} chars)");
                else
                    Log.Warn("[StreamClient] PoToken unavailable — proceeding without");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn($"[StreamClient] PoToken fetch failed: {ex.Message} — proceeding without");
            }
        }

        for (int i = 0; i < rawList.Count; i++)
        {
            var streamData = rawList[i];
            cancellationToken.ThrowIfCancellationRequested();

            var itag = streamData.Itag;
            if (itag is null) continue;

            var mimeType = streamData.MimeType;
            if (string.IsNullOrEmpty(mimeType))
                continue;

            bool isAudioOnly = mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);

            // Совмещённый поток (video/*) принимается ТОЛЬКО если чистых аудиопотоков нет вообще
            bool isMuxedFallback = !hasPureAudioStreams
                                 && mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                                 && streamData.AudioChannels > 0
                                 && !string.IsNullOrWhiteSpace(streamData.AudioCodec);

            if (!isAudioOnly && !isMuxedFallback)
                continue;

            var audioCodec = streamData.AudioCodec;
            if (string.IsNullOrWhiteSpace(audioCodec)) continue;

            var url = streamData.Url;
            if (string.IsNullOrWhiteSpace(url)) continue;

            Log.Debug($"[StreamClient] itag={itag} raw URL (first 200): " +
                      $"{url[..Math.Min(url.Length, 200)]}");

            if (!string.IsNullOrWhiteSpace(streamData.Signature))
            {
                Log.Debug($"[StreamClient] itag={itag} needs signature decryption");
                try
                {
                    var decryptedSig = await _sigCipherDecryptor.DecipherAsync(
                        streamData.Signature, cancellationToken).ConfigureAwait(false);

                    var sigParam = streamData.SignatureParameter ?? "sig";
                    url = UrlEx.SetQueryParameter(url, sigParam, decryptedSig);
                    Log.Debug($"[StreamClient] itag={itag} sig decrypted");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Error($"[StreamClient] itag={itag} sig decryption failed: {ex.Message}");
                    continue;
                }
            }

            var nToken = UrlEx.TryGetQueryParameterValue(url, "n");
            bool hasEncryptedNToken = false;

            if (!string.IsNullOrEmpty(nToken))
            {
                // Для WEB и WEB_REMIX YouTube ВСЕГДА шифрует n-token.
                // Выполнять сетевой HEAD-запрос ценой 1.5-2 секунды ради получения заведомого HTTP 403 бессмысленно:
                // расшифровка в QuickJS занимает всего 5 мс и выполняется упреждающе.
                if (isNTokenDecryptionRequired == null)
                {
                    bool isKnownEncryptedClient = string.Equals(clientName, YoutubeClientNames.WebRemix, StringComparison.Ordinal)
                                               || string.Equals(clientName, YoutubeClientNames.Web, StringComparison.Ordinal);

                    if (isKnownEncryptedClient)
                    {
                        isNTokenDecryptionRequired = true;
                        Log.Debug($"[StreamClient] itag={itag}: Proactively decrypting n-token for web client '{clientName}' (bypassing 2s HEAD probe)");
                    }
                    else
                    {
                        try
                        {
                            using var headResponse = await _http.HeadAsync(url, cancellationToken).ConfigureAwait(false);
                            isNTokenDecryptionRequired = !headResponse.IsSuccessStatusCode;
                            Log.Debug($"[StreamClient] itag={itag} HEAD: {headResponse.StatusCode}, needsNToken={isNTokenDecryptionRequired}");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            isNTokenDecryptionRequired = true;
                            Log.Debug($"[StreamClient] HEAD probe failed ({ex.Message}), fallback to proactive decryption");
                        }
                    }
                }

                if (isNTokenDecryptionRequired == true)
                {
                    try
                    {
                        var decryptedN = await _nTokenDecryptor.DecryptAsync(
                            nToken, contextId: videoId.Value, ct: cancellationToken).ConfigureAwait(false);

                        hasEncryptedNToken = string.Equals(decryptedN, nToken, StringComparison.Ordinal);
                        url = UrlEx.SetQueryParameter(url, "n", decryptedN);
                        Log.Debug($"[StreamClient] itag={itag} n-token: '{nToken}' → '{decryptedN}'");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        hasEncryptedNToken = true;
                        Log.Warn($"[StreamClient] itag={itag} n-token failed: {ex.Message}");
                        Log.Warn($"[StreamClient] ⚠ URL will have ENCRYPTED n-token → expect 403!");
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            var urlSpan = url.AsSpan();
            if (urlSpan.Contains("ump=".AsSpan(), StringComparison.Ordinal))
                url = UrlEx.RemoveQueryParameter(url, "ump");

            urlSpan = url.AsSpan();
            if (urlSpan.Contains("alr=".AsSpan(), StringComparison.Ordinal))
                url = UrlEx.RemoveQueryParameter(url, "alr");

            urlSpan = url.AsSpan();
            if (urlSpan.Contains("srfvp=".AsSpan(), StringComparison.Ordinal))
                url = UrlEx.RemoveQueryParameter(url, "srfvp");

            if (!string.IsNullOrEmpty(pot))
            {
                url = UrlEx.SetQueryParameter(url, "pot", pot);
            }
            else if (url.AsSpan().Contains("pot=".AsSpan(), StringComparison.Ordinal))
            {
                url = UrlEx.RemoveQueryParameter(url, "pot");
            }

            Log.Debug($"[StreamClient] itag={itag} FINAL URL ready.");

            var contentLength = streamData.ContentLength ?? 0;
            if (contentLength == 0)
            {
                var clenStr = UrlEx.TryGetQueryParameterValue(url, "clen");
                if (clenStr != null && long.TryParse(clenStr, out var clenVal))
                {
                    contentLength = clenVal;
                }
                else if (streamData.Bitrate.HasValue)
                {
                    double durationSec = 0;
                    var durStr = UrlEx.TryGetQueryParameterValue(url, "dur");
                    if (durStr != null && double.TryParse(durStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var durVal))
                    {
                        durationSec = durVal;
                    }

                    if (durationSec <= 0) durationSec = 180;
                    contentLength = (long)Math.Ceiling(streamData.Bitrate.Value * durationSec / 8.0);
                }
            }

            if (contentLength == 0) continue;

            var container = streamData.Container is { } c ? new Container(c) : (Container?)null;
            if (container is null) continue;

            var bitrate = streamData.Bitrate is { } b ? new Bitrate(b) : (Bitrate?)null;
            if (bitrate is null) continue;

            // LMP воспроизводит стерео/моно. Исключаем 5.1 Surround (audioChannels > 2),
            // которые валят контейнерные парсеры и декодеры.
            int channels = streamData.AudioChannels;
            if (channels > 2) continue;

            Language? audioLanguage = null;
            if (!string.IsNullOrWhiteSpace(streamData.AudioLanguageCode))
            {
                audioLanguage = new Language(
                    streamData.AudioLanguageCode,
                    streamData.AudioLanguageName ?? "");
            }

            yield return new AudioOnlyStreamInfo(
                itag.Value,
                url,
                container.Value,
                new FileSize(contentLength),
                bitrate.Value,
                audioCodec,
                audioLanguage,
                streamData.IsAudioLanguageDefault,
                hasEncryptedNToken,
                channels);
        }
    }

    /// <summary>
    /// Получает манифест потоков для видео.
    /// <para>
    /// <b>Всегда идёт в сеть.</b> Кэширование выполняется вызывающим кодом
    /// (<see cref="SessionCacheStore.RecordManifest"/>).
    /// Это разделение ответственности гарантирует, что StreamClient
    /// не смешивает полные и неполные манифесты.
    /// </para>
    /// </summary>
    /// <param name="videoId">ID видео.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Манифест со всеми доступными аудио-потоками.</returns>
    public async ValueTask<StreamManifest> GetManifestAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default)
    {
        bool isAuth = _isAuthenticatedCheck?.Invoke() ?? false;

        var (playerResponse, clientName) = await _controller.GetPlayerResponseWithFallbackAsync(
            videoId, cancellationToken, isAuthenticated: isAuth).ConfigureAwait(false);

        if (!playerResponse.IsPlayable)
        {
            throw new VideoUnplayableException(
                $"Video {videoId} is not playable: {playerResponse.PlayabilityError}");
        }

        var streams = new List<IStreamInfo>(playerResponse.Streams.Count);
        await foreach (var stream in GetAudioStreamInfosAsync(videoId, playerResponse.Streams, clientName, cancellationToken).ConfigureAwait(false))
        {
            streams.Add(stream);
        }

        if (streams.Count == 0)
            throw new VideoUnplayableException($"No audio streams available for {videoId}");

        return new StreamManifest(streams, playerResponse.PerceptualLoudnessDb);
    }

    /// <summary>
    /// Инвалидирует signatureTimestamp в кэше менеджера контекста плеера.
    /// </summary>
    public void InvalidateCipherManifest()
    {
        _controller.InvalidateSignatureTimestamp();
        Log.Debug("[StreamClient] SignatureTimestamp invalidated via controller");
    }
}