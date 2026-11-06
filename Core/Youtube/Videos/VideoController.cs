using LMP.Core.Youtube.Bridge;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Utils;
using System.Runtime.CompilerServices;

namespace LMP.Core.Youtube.Videos;

/// <summary>
/// Unified controller for interacting with the YouTube API (retrieving video metadata, PlayerResponse, DASH/HLS manifests).
/// </summary>
internal partial class VideoController(HttpClient http, PlayerContextManager playerManager)
{
    #region Bot Detection State

    private static DateTime _lastBotDetection = DateTime.MinValue;
    private static int _consecutiveFailures;
    private static readonly SemaphoreSlim _requestThrottle = new(1, 1);
    private static readonly Lock _stateLock = new();

    /// <summary>
    /// Number of ANDROID_VR BotDetection failures in the current session.
    /// Used to skip a client consistently blocked by the server,
    /// avoiding lost time on a roundtrip (~700 ms) during each force-refresh.
    /// Reset on a successful ANDROID_VR response or explicit call to <see cref="ResetBotDetectionState"/>.
    /// </summary>
    private static volatile int _androidVrSessionBotDetections;
    private static DateTime _androidVrLastBotDetection = DateTime.MinValue;

    /// <summary>Quarantine duration for the ANDROID_VR client after BotDetection (2 hours, reset on network/account change).</summary>
    private const int AndroidVrCooldownSeconds = 7_200;

    /// <summary>Consecutive BotDetection error threshold to trigger a global cooldown.</summary>
    private const int BotDetectionCooldownThreshold = 3;

    /// <summary>Maximum number of attempts to fetch the HTML video watch page.</summary>
    private const int WatchPageMaxRetries = 5;

    /// <summary>Throttling delay after the 1st BotDetection failure.</summary>
    private static readonly TimeSpan ThrottleDelayLevel1 = TimeSpan.FromMilliseconds(300);

    /// <summary>Throttling delay after the 2nd BotDetection failure.</summary>
    private static readonly TimeSpan ThrottleDelayLevel2 = TimeSpan.FromMilliseconds(600);

    /// <summary>Maximum throttling delay on multiple BotDetection failures.</summary>
    private static readonly TimeSpan ThrottleDelayMax = TimeSpan.FromSeconds(1);

    /// <summary>Duration of global quarantine following a series of BotDetection failures.</summary>
    public static readonly TimeSpan CooldownDuration = TimeSpan.FromMinutes(2);

    public static bool IsInCooldown
    {
        get
        {
            lock (_stateLock)
            {
                if (_consecutiveFailures < BotDetectionCooldownThreshold) return false;
                var elapsed = DateTime.UtcNow - _lastBotDetection;
                return elapsed < CooldownDuration;
            }
        }
    }

    public static TimeSpan GetRemainingCooldown()
    {
        lock (_stateLock)
        {
            if (_consecutiveFailures < BotDetectionCooldownThreshold) return TimeSpan.Zero;

            var elapsed = DateTime.UtcNow - _lastBotDetection;
            var remaining = CooldownDuration - elapsed;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    public static void ResetBotDetectionState()
    {
        lock (_stateLock)
        {
            _consecutiveFailures = 0;
            _androidVrSessionBotDetections = 0;
            _androidVrLastBotDetection = DateTime.MinValue;
            _lastBotDetection = DateTime.MinValue;
            Log.Info("[VideoController] Bot detection state reset");
        }
    }

    public static void ThrowIfInCooldown()
    {
        if (IsInCooldown)
        {
            var remaining = GetRemainingCooldown();
            throw new BotDetectionException(
                $"Rate limited by YouTube. Please wait {remaining.TotalSeconds:F0} seconds.",
                remaining);
        }
    }

    #endregion

    #region Throttle

    private static TimeSpan GetThrottleDelay()
    {
        int failures;
        lock (_stateLock) failures = _consecutiveFailures;

        return failures switch
        {
            0 => TimeSpan.Zero,
            1 => ThrottleDelayLevel1,
            2 => ThrottleDelayLevel2,
            _ => ThrottleDelayMax
        };
    }

    #endregion

    private readonly PlayerContextManager _playerManager = playerManager;

    protected HttpClient Http { get; } = http;

    /// <summary>
    /// Fetches the video watch HTML page supporting retries on network failures.
    /// </summary>
    public async ValueTask<VideoWatchPage> GetVideoWatchPageAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default)
    {
        return await ResilienceExecutor.ExecuteWithRetryAsync(async () =>
        {
            var rawHtml = await Http.GetStringAsync(
                $"https://www.youtube.com/watch?v={videoId}&bpctr=9999999999",
                cancellationToken).ConfigureAwait(false);

            var watchPage = VideoWatchPage.TryParse(rawHtml) ?? throw new YoutubeExplodeException("Video watch page is broken. Please try again in a few minutes.");
            if (!watchPage.IsAvailable)
            {
                throw new VideoUnavailableException($"Video '{videoId}' is not available.");
            }

            return watchPage;
        }, maxRetries: WatchPageMaxRetries, cancellationToken).ConfigureAwait(false);
    }

    #region GetPlayerResponse

    public async ValueTask<PlayerResponse> GetPlayerResponseAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default)
    {
        var clientName = YoutubeClientUtils.GetClientApiName(YoutubeClientUtils.CurrentProfile);
        return await GetPlayerResponseWithClientAsync(videoId, clientName, cancellationToken);
    }

    public async ValueTask<PlayerResponse> GetPlayerResponseWithClientAsync(
           VideoId videoId,
           string clientName,
           CancellationToken cancellationToken,
           string? signatureTimestamp = null)
    {
        ThrowIfInCooldown();

        await _requestThrottle.WaitAsync(cancellationToken);
        try
        {
            var delay = GetThrottleDelay();
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);
        }
        finally { _requestThrottle.Release(); }

        Log.Info($"GetPlayerResponse START ({clientName}): {videoId}");

        var visitorData = await YoutubeClientUtils.EnsureVisitorDataAsync(ct: cancellationToken).ConfigureAwait(false);

        if (signatureTimestamp == null && clientName is YoutubeClientNames.Web or YoutubeClientNames.WebRemix or YoutubeClientNames.Tv)
        {
            signatureTimestamp = await ResolveSignatureTimestampAsync(cancellationToken);
        }

        var playerUrl = YoutubeClientUtils.GetPlayerEndpoint(clientName);

        using var request = new HttpRequestMessage(HttpMethod.Post, playerUrl);
        YoutubeClientUtils.ConfigurePlayerRequest(request, clientName);

        // The player strictly requires invariant English for deterministic parsing
        // of publish dates ("3 days ago") and textual block reasons (Bot Detection / Age Gate).
        request.Headers.AcceptLanguage.Clear();
        request.Headers.AcceptLanguage.ParseAdd(YoutubeHttpHandler.InvariantAcceptLanguage);

        bool isMobileClient = clientName is YoutubeClientNames.AndroidVr or
                              YoutubeClientNames.AndroidMusic or
                              YoutubeClientNames.Ios or
                              YoutubeClientNames.Tv or
                              YoutubeClientNames.AndroidTestSuite;

        request.Options.Set(YoutubeHttpHandler.IsPlayerContext, true);
        request.Options.Set(YoutubeHttpHandler.IsMobileClient, isMobileClient);

        string jsonBody = YoutubeClientUtils.GeneratePlayerContextForClient(
            clientName, videoId.Value, visitorData, signatureTimestamp);

        request.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;
            Log.Warn($"[VideoController] [{videoId}] {clientName} HTTP {statusCode}");

            if (statusCode == 403)
            {
                throw new StreamUnavailableException(
                    $"HTTP 403 Forbidden for video {videoId} via {clientName}",
                    videoId.Value,
                    StreamUnavailableReason.Forbidden403,
                    httpStatusCode: 403);
            }

            throw new YoutubeExplodeException($"HTTP {response.StatusCode} for client {clientName}");
        }

        var playerResponse = PlayerResponse.Parse(content);

        // Bot detection tracking BEFORE LoginRequired
        // Ensures _consecutiveFailures is incremented for LOGIN_REQUIRED + "bot"
        TrackBotDetection(playerResponse, clientName);

        if (playerResponse.IsLoginRequired)
        {
            var reason = playerResponse.LoginRequiredReason;

            Log.Warn($"[VideoController] [{videoId}] LOGIN_REQUIRED via {clientName}: " +
                     $"reason={reason}, raw=\"{playerResponse.PlayabilityError}\"");

            throw new LoginRequiredException(
                $"Video {videoId} requires login: {playerResponse.LoginRequiredReason}",
                videoId.Value,
                reason);
        }

        return playerResponse;
    }

    /// <summary>
    /// Extracts signatureTimestamp from the shared <see cref="PlayerContextManager"/> cache.
    /// Falls back to the disk cache on network timeout if available.
    /// Re-throws network errors and cancellations so the caller
    /// can correctly determine the cause of failure.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The signatureTimestamp string or <c>null</c> on a non-critical error.</returns>
    private async ValueTask<string?> ResolveSignatureTimestampAsync(CancellationToken ct)
    {
        // 1. Fast path: in-memory cache
        var cached = _playerManager.GetCachedSignatureTimestamp();
        if (!string.IsNullOrEmpty(cached)) return cached;

        try
        {
            var context = await _playerManager.GetOrLoadAsync(ct).ConfigureAwait(false);

            var sts = context.Sts;

            if (string.IsNullOrEmpty(sts) && !string.IsNullOrEmpty(context.BaseJs))
            {
                sts = YoutubeAstSolver.ExtractSts(context.BaseJs);
                Log.Debug($"[VideoController] STS resolved via BaseJs fallback: {sts}");
            }

            _playerManager.SetCachedSignatureTimestamp(sts);
            return sts;
        }
        catch (YoutubeNetworkException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Internal GetOrLoadAsync timeout (not caller cancellation) —
            // try the disk cache that might remain from a previous session
            Log.Warn("[VideoController] GetOrLoadAsync timed out internally, trying disk cache fallback");
            return TryFallbackSignatureTimestamp();
        }
        catch (OperationCanceledException)
        {
            // Caller cancellation — re-throw
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"[VideoController] Failed to get signatureTimestamp: {ex.Message}");
            return TryFallbackSignatureTimestamp();
        }
    }

    /// <summary>
    /// Attempts to extract signatureTimestamp from the PlayerContext disk cache
    /// without making network requests. Returns <c>null</c> if cache is missing.
    /// </summary>
    private string? TryFallbackSignatureTimestamp()
    {
        // Re-check in-memory — could have been populated by a concurrent thread
        var cached = _playerManager.GetCachedSignatureTimestamp();
        if (!string.IsNullOrEmpty(cached))
        {
            Log.Debug($"[VideoController] STS fallback: found in memory cache: {cached}");
            return cached;
        }

        // Try loading from disk cache without network access
        var diskContext = PlayerContextManager.TryLoadFromDiskCache();
        if (diskContext != null)
        {
            var sts = diskContext.Sts;

            if (string.IsNullOrEmpty(sts) && !string.IsNullOrEmpty(diskContext.BaseJs))
            {
                sts = YoutubeAstSolver.ExtractSts(diskContext.BaseJs);
            }

            if (!string.IsNullOrEmpty(sts))
            {
                _playerManager.SetCachedSignatureTimestamp(sts);
                Log.Info($"[VideoController] STS fallback: resolved from disk cache: {sts}");
                return sts;
            }
        }

        Log.Warn("[VideoController] STS fallback: no disk cache available");
        return null;
    }

    #endregion

    #region Bot Detection Tracking

    /// <summary>
    /// Checks whether a player error message indicates Bot Detection.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsBotDetectionError(string? error)
    {
        if (string.IsNullOrEmpty(error)) return false;

        var span = error.AsSpan();
        return span.Contains("bot".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
               span.Contains("Sign in".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
               span.Contains("LOGIN_REQUIRED".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
               span.Contains("confirm".AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    private static void TrackBotDetection(PlayerResponse response, string clientName)
    {
        // ANDROID_VR is isolated with its own 300-second cooldown.
        // We do not inflate global throttling because of it, otherwise WEB_REMIX will artificially delay by 1-5 sec.
        if (string.Equals(clientName, YoutubeClientNames.AndroidVr, StringComparison.OrdinalIgnoreCase))
            return;

        if (IsBotDetectionResponse(response))
        {
            lock (_stateLock)
            {
                _consecutiveFailures++;
                _lastBotDetection = DateTime.UtcNow;

                if (_consecutiveFailures == 1)
                {
                    Log.Warn("[VideoController] ⚠️ Bot detection triggered on web client — slowing down requests");
                }
                else if (_consecutiveFailures >= 3)
                {
                    Log.Error($"[VideoController] ❌ Multiple bot detections ({_consecutiveFailures}) — cooldown active");
                }
            }
        }
        else if (response.IsPlayable)
        {
            lock (_stateLock)
            {
                if (_consecutiveFailures > 0)
                {
                    Log.Info($"[VideoController] ✓ Bot detection cleared after {_consecutiveFailures} failures");
                    _consecutiveFailures = 0;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBotDetectionResponse(PlayerResponse response)
    {
        return IsBotDetectionError(response.PlayabilityError);
    }

    #endregion

    #region Fallback & DASH Methods

    /// <summary>
    /// Requests a player response, iterating through the client fallback chain until the first playable response.
    /// </summary>
    /// <param name="videoId">Video identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="isAuthenticated">Whether an authenticated session exists.</param>
    /// <param name="preferWebRemix">Place WEB_REMIX first (VPN + warmed-up BotGuard).</param>
    /// <param name="forceMobilePriority">Forced refresh: disables WEB_REMIX priority.</param>
    /// <param name="excludedClients">
    /// Clients whose URLs for this video were rejected by CDN with HTTP 403. Any non-<c>null</c> value (including empty)
    /// enables the <see cref="YoutubeClientUtils.GetStreamFallbackClientsAfterCdnForbidden"/> order.
    /// </param>
    public async ValueTask<(PlayerResponse Response, string ClientName)> GetPlayerResponseWithFallbackAsync(
        VideoId videoId,
        CancellationToken cancellationToken,
        bool isAuthenticated = false,
        bool preferWebRemix = false,
        bool forceMobilePriority = false,
        IReadOnlyList<string>? excludedClients = null)
    {
        // If VPN is active and authenticated (or BotGuard is already warmed up to 2ms fast-mint),
        // route immediately through WEB_REMIX, except during forced refresh after a series of CDN 403s.
        bool useWebPriority = !forceMobilePriority && (preferWebRemix || (isAuthenticated && YoutubeClientUtils.IsVpnActive));

        // After CDN 403, clients with rejected URLs are excluded, ANDROID_VR moves to the end of the chain.
        // excludedClients != null indicates a request after CDN 403. Even if empty (URL client unrecognised),
        // the CDN-403 order is used: ANDROID_VR as first almost guaranteed triggers bot detection.
        bool hasExcludedClients = excludedClients is not null;
        var clients = hasExcludedClients
            ? YoutubeClientUtils.GetStreamFallbackClientsAfterCdnForbidden(excludedClients!)
            : YoutubeClientUtils.GetStreamFallbackClients(isAuthenticated, useWebPriority);

        if (clients.Length == 0)
        {
            Log.Warn($"[VideoController] [{videoId}] All stream clients excluded after CDN 403: {string.Join(",", excludedClients!)}");
            throw new StreamUnavailableException(
                $"All stream clients were rejected by CDN with HTTP 403 for {videoId}",
                videoId.Value,
                StreamUnavailableReason.Forbidden403,
                httpStatusCode: 403);
        }

        if (hasExcludedClients)
            Log.Info($"[VideoController] [{videoId}] CDN-403 fallback order: {string.Join(" -> ", clients)}");
        var errors = new List<string>();
        var allBotDetection = true;
        bool hasLoginRequired = false;
        LoginRequiredException? loginException = null;

        bool hasNonNetworkFailure = false;
        YoutubeNetworkException? firstNetworkException = null;

        foreach (var clientName in clients)
        {
            // Skip client if it has already triggered BotDetection in the current session.
            // Prevents ~700 ms delay before falling back to WEB_REMIX.
            bool skipAndroidVr = false;
            if (string.Equals(clientName, YoutubeClientNames.AndroidVr, StringComparison.Ordinal)
                && _androidVrSessionBotDetections > 0)
            {
                lock (_stateLock)
                {
                    skipAndroidVr = (DateTime.UtcNow - _androidVrLastBotDetection).TotalSeconds < AndroidVrCooldownSeconds;
                }
            }

            if (skipAndroidVr)
            {
                int remainingSec;
                lock (_stateLock)
                {
                    remainingSec = AndroidVrCooldownSeconds - (int)(DateTime.UtcNow - _androidVrLastBotDetection).TotalSeconds;
                }

                Log.Debug($"[VideoController] [{videoId}] ANDROID_VR skipped (BotDetection active, {remainingSec}s cooldown remaining)");
                errors.Add("ANDROID_VR: SKIPPED_BOT_DETECTION_COOLDOWN");
                hasNonNetworkFailure = true;
                continue;
            }

            try
            {
                var response = await GetPlayerResponseWithClientAsync(videoId, clientName, cancellationToken);

                if (response.IsPlayable && HasPlayableAudioStream(response))
                {
                    // Successful ANDROID_VR response — block lifted, RESET counter
                    if (string.Equals(clientName, YoutubeClientNames.AndroidVr, StringComparison.Ordinal)
                        && _androidVrSessionBotDetections > 0)
                    {
                        _androidVrSessionBotDetections = 0;
                        _androidVrLastBotDetection = DateTime.MinValue;
                        Log.Debug("[VideoController] ANDROID_VR bot detection counter reset on success");
                    }

                    Log.Info($"[VideoController] [{videoId}] SUCCESS with {clientName}");
                    return (response, clientName);
                }

                var error = response.PlayabilityError ?? (response.IsPlayable ? "No playable audio streams (SABR-only)" : "Not playable");
                Log.Warn($"[VideoController] [{videoId}] {clientName}: {error}");
                errors.Add($"{clientName}: {error}");

                var reason = PlayabilityErrorClassifier.Classify(error, out _);
                if (reason is StreamUnavailableReason.CopyrightBlocked or
                             StreamUnavailableReason.RegionBlocked or
                             StreamUnavailableReason.Private or
                             StreamUnavailableReason.Removed)
                {
                    Log.Info($"[VideoController] [{videoId}] Hard legal restriction detected ({reason}). Fast-failing further clients.");
                    throw new StreamUnavailableException(error, videoId.Value, reason);
                }

                if (!IsBotDetectionResponse(response))
                    allBotDetection = false;

                hasNonNetworkFailure = true;
            }
            catch (LoginRequiredException ex)
            {
                if (ex.Reason == LoginRequiredReason.BotDetection)
                {
                    // ANDROID_VR BotDetection Tracking
                    if (string.Equals(clientName, YoutubeClientNames.AndroidVr, StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref _androidVrSessionBotDetections);
                        lock (_stateLock)
                        {
                            _androidVrLastBotDetection = DateTime.UtcNow;
                        }
                        Log.Warn($"[VideoController] ANDROID_VR hit bot detection ({_androidVrSessionBotDetections}). Entering {AndroidVrCooldownSeconds}s cooldown.");
                    }

                    errors.Add($"{clientName}: BOT_DETECTION (LOGIN_REQUIRED)");
                }
                else
                {
                    if (!hasLoginRequired)
                    {
                        hasLoginRequired = true;
                        loginException = ex;
                    }
                    errors.Add($"{clientName}: LOGIN_REQUIRED ({ex.Reason})");
                    allBotDetection = false;
                }
                hasNonNetworkFailure = true;
            }
            catch (YoutubeNetworkException netEx)
            {
                Log.Warn($"[VideoController] [{videoId}] {clientName} network error: {netEx.ErrorType} — {netEx.Message}");
                errors.Add($"{clientName}: NETWORK_{netEx.ErrorType}");
                firstNetworkException ??= netEx;
                allBotDetection = false;
            }
            catch (StreamUnavailableException) { throw; }
            catch (BotDetectionException) { throw; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Attempt to classify as a network error (in case of
                // If YoutubeHttpHandler did not wrap it (e.g., an error before SendAsync or an internal timeout)
                var classified = YoutubeNetworkException.TryClassify(ex, cancellationToken);
                if (classified is not null)
                {
                    Log.Warn($"[VideoController] [{videoId}] {clientName} classified as network: {classified.ErrorType} — {ex.Message}");
                    errors.Add($"{clientName}: NETWORK_{classified.ErrorType}");
                    firstNetworkException ??= classified;
                }
                else
                {
                    Log.Warn($"[VideoController] [{videoId}] {clientName} exception: {ex.Message}");
                    errors.Add($"{clientName}: {ex.Message}");
                    hasNonNetworkFailure = true;
                }
                allBotDetection = false;
            }
        }

        // Final error priority

        // 1. All clients failed due to network issues — the user should see "network problem"
        if (firstNetworkException is not null && !hasNonNetworkFailure)
        {
            Log.Error($"[VideoController] [{videoId}] All clients failed due to network: {firstNetworkException.ErrorType}");
            throw firstNetworkException;
        }

        // 2. All clients required login (not a bot)
        if (hasLoginRequired && loginException != null)
        {
            Log.Error($"[VideoController] [{videoId}] All clients require login: {loginException.Reason}");
            throw loginException;
        }

        var allErrors = string.Join("; ", errors);

        // 3. All clients are blocked by bot detection
        if (allBotDetection)
        {
            throw new BotDetectionException(
                $"All clients detected as bot for {videoId}",
                GetRemainingCooldown());
        }

        // 4. Mixed errors, but network error present — mention it in the message
        if (firstNetworkException is not null)
        {
            Log.Error($"[VideoController] [{videoId}] Mixed failures (including network: {firstNetworkException.ErrorType}). Errors: {allErrors}");
            throw firstNetworkException;
        }

        throw new VideoUnplayableException(
            $"Video {videoId} is not available through any client. Errors: {allErrors}",
            videoId.Value);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasPlayableAudioStream(PlayerResponse response)
    {
        foreach (var s in response.Streams)
        {
            if (string.IsNullOrEmpty(s.Url) && string.IsNullOrEmpty(s.Signature))
                continue;

            var mime = s.MimeType;
            if (string.IsNullOrEmpty(mime))
                continue;

            // Audio-only streams
            if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                return true;

            // Muxed streams (video + audio, e.g., itag 18 with SABR)
            if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) && s.AudioChannels > 0)
                return true;
        }

        return false;
    }

    public async ValueTask<PlayerResponse> GetPlayerResponseAsync(
        VideoId videoId,
        string? signatureTimestamp,
        CancellationToken cancellationToken = default)
    {
        return await GetPlayerResponseWithClientAsync(
            videoId, "TVHTML5_SIMPLY_EMBEDDED_PLAYER", cancellationToken, signatureTimestamp);
    }

    /// <summary>
    /// Resets the cached signatureTimestamp via <see cref="PlayerContextManager"/>.
    /// Affects all <see cref="VideoController"/> instances using the same manager.
    /// </summary>
    internal void InvalidateSignatureTimestamp()
    {
        _playerManager.InvalidateSignatureTimestamp();
        Log.Debug("[VideoController] SignatureTimestamp invalidated via PlayerContextManager");
    }

    #endregion
}