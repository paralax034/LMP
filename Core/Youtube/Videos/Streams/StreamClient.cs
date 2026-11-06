using System.Globalization;
using System.Runtime.CompilerServices;
using LMP.Core.Youtube.Bridge;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Core.Youtube.Bridge.NToken;
using LMP.Core.Youtube.Bridge.SigCipher;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Videos.ClosedCaptions;
using LMP.Core.Youtube.Bridge.PoToken;
using LMP.Core.Audio.Http;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Youtube.Videos.Streams;

/// <summary>
/// Provides access to YouTube video media streams.
/// <para>
/// <b>Cache strategy (3-level):</b>
/// </para>
/// <list type="number">
///   <item>
///     <b>Disk manifest cache</b> (<see cref="SessionCacheStore"/>):
///     The full manifest (all variants) is saved to disk after the first API call.
///     Persists across application restarts. Invalidated on HTTP 403/410 during probe.
///   </item>
///   <item>
///     <b>Network resolve</b>: Only if disk cache is empty or probe failed.
///     Single API call → full manifest → written to disk.
///   </item>
///   <item>
///     <b>CDN connection pre-warming</b>: TCP+TLS prewarm to known CDN nodes
///     in parallel with the API call.
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
    /// Creates an instance of StreamClient.
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
    /// Generates audio streams from raw YouTube response data.
    /// Audio-only streams have absolute priority. Muxed streams (video + audio, e.g., itag 18)
    /// are attached exclusively as an emergency fallback if audio-only streams are missing (SABR block).
    /// </summary>
    private async IAsyncEnumerable<IStreamInfo> GetAudioStreamInfosAsync(
      VideoId videoId,
      IEnumerable<IStreamData> streamDatas,
      string? clientName,
      [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var rawList = streamDatas as IReadOnlyList<IStreamData> ?? streamDatas.ToList();

        // Check if there is at least one audio-only stream with an available URL or signature,
        // and immediately trigger speculative TCP+TLS pre-warming of the CDN host BEFORE PoToken generation and sig/n decryption.
        bool hasPureAudioStreams = false;
        string? earlyCdnUrl = null;
        for (int i = 0; i < rawList.Count; i++)
        {
            var s = rawList[i];
            if (s.MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true
                && (!string.IsNullOrEmpty(s.Url) || !string.IsNullOrEmpty(s.Signature)))
            {
                hasPureAudioStreams = true;
                if (earlyCdnUrl is null && !string.IsNullOrEmpty(s.Url))
                {
                    earlyCdnUrl = s.Url;
                }
            }
        }

        bool skipPoToken = string.Equals(clientName, YoutubeClientNames.AndroidVr, StringComparison.Ordinal);

        // Trigger speculative CDN host pre-warming ONLY if a cold BotGuard initialization (~800 ms)
// or cold QuickJS compilation (~5-1000 ms) is ahead. When both contexts are already warm (fast-mint = 2 ms),
        // Range 0-65535 starts in ~10 ms, and a parallel /generate_204 merely creates a duplicate cold socket.
        // Local memoization of the decrypted n-token within a single PlayerResponse
// (all audio streams in the same response share the same input n parameter).
        bool expectsColdCryptoDelay = !_sigCipherDecryptor.IsInitialized
            || (!_nTokenDecryptor.IsInitialized)
            || (_poTokenProvider != null && !skipPoToken && !_poTokenProvider.HasActiveContext);

        if (expectsColdCryptoDelay
            && !string.IsNullOrEmpty(earlyCdnUrl)
            && !skipPoToken)
        {
            CdnConnectionPreWarmer.PreWarmHost(SharedHttpClient.Instance, earlyCdnUrl, cancellationToken);
        }

        bool? isNTokenDecryptionRequired = null;

        string? pot = null;

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

        // Muxed stream (video/*) is accepted ONLY if there are no audio-only streams at all
        // For YouTube WEB and WEB_REMIX, the n-token is ALWAYS encrypted.
        string? lastRawNToken = null;
        string? lastDecryptedNToken = null;

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

            // Executing a network HEAD request costing 1.5-2 seconds just to get a guaranteed HTTP 403 is pointless:
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
                // decryption in QuickJS takes only 5 ms and is performed speculatively.
                // LMP plays stereo/mono. Exclude 5.1 Surround (audioChannels > 2),
                // which crash container parsers and decoders.
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
                        string decryptedN;
                        if (lastDecryptedNToken != null && string.Equals(nToken, lastRawNToken, StringComparison.Ordinal))
                        {
                            decryptedN = lastDecryptedNToken;
                        }
                        else
                        {
                            decryptedN = await _nTokenDecryptor.DecryptAsync(
                                nToken, contextId: videoId.Value, ct: cancellationToken).ConfigureAwait(false);
                            lastRawNToken = nToken;
                            lastDecryptedNToken = decryptedN;
                        }

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

                /// <summary>
    /// Gets the stream manifest for a video.
    /// <para>
    /// <b>Always hits the network.</b> Caching is performed by the caller
    /// (<see cref="SessionCacheStore.RecordManifest"/>).
    /// This separation of concerns guarantees that StreamClient
    /// does not mix full and incomplete manifests.
    /// </para>
    /// </summary>
    /// <param name="videoId">Video ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="forceRefresh">Forced refresh after CDN 403: without WEB_REMIX priority.</param>
    /// <param name="excludedClients">Clients whose URLs were rejected by CDN with HTTP 403; excluded from the fallback chain.</param>
    /// <returns>Manifest containing all available audio streams.</returns>
                /// <summary>
    /// Invalidates signatureTimestamp in the player context manager cache.
    /// </summary>
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
    /// Gets the stream manifest for a video.
    /// <para>
    /// <b>Always hits the network.</b> Caching is performed by the caller
    /// (<see cref="SessionCacheStore.RecordManifest"/>).
    /// This separation of concerns guarantees that StreamClient
    /// does not mix full and incomplete manifests.
    /// </para>
    /// </summary>
    /// <param name="videoId">Video ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="forceRefresh">Forced refresh after CDN 403: without WEB_REMIX priority.</param>
    /// <param name="excludedClients">Clients whose URLs were rejected by CDN with HTTP 403; excluded from the fallback chain.</param>
    /// <returns>Manifest containing all available audio streams.</returns>
    public async ValueTask<StreamManifest> GetManifestAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false,
        IReadOnlyList<string>? excludedClients = null)
    {
        bool isAuth = _isAuthenticatedCheck?.Invoke() ?? false;

        bool preferWebRemix = !forceRefresh
               && YoutubeClientUtils.IsVpnActive
               && (_poTokenProvider?.HasActiveContext == true || isAuth);

        var (playerResponse, clientName) = await _controller.GetPlayerResponseWithFallbackAsync(
            videoId,
            cancellationToken,
            isAuthenticated: isAuth,
            preferWebRemix: preferWebRemix,
            forceMobilePriority: forceRefresh,
            excludedClients: excludedClients).ConfigureAwait(false);

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
    /// Invalidates signatureTimestamp in the player context manager cache.
    /// </summary>
    public void InvalidateCipherManifest()
    {
        _controller.InvalidateSignatureTimestamp();
        Log.Debug("[StreamClient] SignatureTimestamp invalidated via controller");
    }
}