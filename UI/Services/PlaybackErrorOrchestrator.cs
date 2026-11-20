using Avalonia.Threading;
using LMP.Core.Exceptions;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Utils;

namespace LMP.UI.Services;

/// <summary>
/// Single source of truth for mapping playback and stream resolution exceptions
/// to localization message and recommendation keys.
/// </summary>
internal static class PlaybackErrorMappingHelper
{
    public static string GetMessageKey(Exception exception, string? fallbackKey = null) => exception switch
    {
        YoutubeNetworkException netEx => netEx.GetLocalizationKey(),
        LoginRequiredException lre => lre.GetLocalizationKey(),
        StreamUnavailableException streamEx => streamEx.GetLocalizationKey(),
        ChunkDownloadFatalException chunkEx => chunkEx.GetLocalizationKey(),
        BotDetectionException => "Error_Login_BotDetection",
        VideoUnplayableException => "Error_Video_Unavailable",
        _ => fallbackKey ?? exception.Message
    };

    public static string? GetRecommendationKey(Exception exception, bool isAuthenticated)
    {
        if (NetworkErrorHelper.IsSslOrTlsHandshakeFailure(exception))
            return "Recommendation_DpiBlocked";

        return exception switch
        {
            LoginRequiredException login => login.Reason switch
            {
                LoginRequiredReason.AgeRestricted => "Recommendation_Login_AgeRestricted",
                LoginRequiredReason.Private => "Recommendation_Private",
                LoginRequiredReason.MembersOnly => "Recommendation_MembersOnly",
                LoginRequiredReason.BotDetection => "Recommendation_BotDetection",
                _ => "Recommendation_Login"
            },
            BotDetectionException => "Recommendation_BotDetection",
            VideoUnplayableException => isAuthenticated
                ? "Recommendation_VideoUnavailable"
                : "Recommendation_Login_403",
            StreamUnavailableException stream => GetStreamRecommendation(stream, isAuthenticated),
            ChunkDownloadFatalException chunk => GetChunkRecommendation(chunk, isAuthenticated),
            YoutubeNetworkException netEx => netEx.GetRecommendationKey(),
            _ => null
        };
    }

    private static string GetStreamRecommendation(StreamUnavailableException stream, bool isAuthenticated)
    {
        if (stream.Reason == StreamUnavailableReason.CopyrightBlocked)
            return "Recommendation_Copyright";

        if (stream.Reason == StreamUnavailableReason.Forbidden403)
            return isAuthenticated ? "Recommendation_ChangeClient" : "Recommendation_Login_403";

        if (stream.Reason == StreamUnavailableReason.AllClientsFailed)
            return isAuthenticated ? "Recommendation_AllClientsFailed_Auth" : "Recommendation_Login_403";

        return stream.Reason switch
        {
            StreamUnavailableReason.RegionBlocked => "Recommendation_UseVPN",
            StreamUnavailableReason.AgeRestricted => "Recommendation_Login_AgeRestricted",
            StreamUnavailableReason.Private => "Recommendation_Private",
            StreamUnavailableReason.Removed => "Recommendation_Removed",
            StreamUnavailableReason.PaymentRequired => "Recommendation_Payment",
            StreamUnavailableReason.LiveStream => "Recommendation_LiveStream",
            _ => "Recommendation_ContactDev"
        };
    }

    private static string GetChunkRecommendation(ChunkDownloadFatalException chunk, bool isAuthenticated)
    {
        if (chunk.Reason == ChunkDownloadFailureReason.Forbidden403)
            return isAuthenticated ? "Recommendation_ChangeClient" : "Recommendation_Login_403";

        return chunk.Reason switch
        {
            ChunkDownloadFailureReason.NetworkError => "Recommendation_CheckNetwork",
            ChunkDownloadFailureReason.UmpFormat => "Recommendation_ChangeClient",
            _ => "Recommendation_ContactDev"
        };
    }
}

/// <summary>
/// Playback error handling orchestrator.
/// Single point of decision making.
/// Completely non-blocking — uses toast notifications instead of modal dialogs.
/// </summary>
public sealed class PlaybackErrorOrchestrator : IDisposable
{
    private readonly YoutubeProvider _youtube;
    private readonly AudioEngine _audioEngine;
    private readonly NotificationService _notificationService;
    private readonly LibraryService _libraryService;

    private readonly Dictionary<string, long> _recentlyShownErrors = new(StringComparer.Ordinal);
    private long _lastErrorCleanupTick = Environment.TickCount64;

    private static readonly TimeSpan ErrorCacheCleanupInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ErrorDeduplicationWindow = TimeSpan.FromSeconds(10);

    private const int DialogToastDurationMs = 8000;
    private const int SkipToastDurationMs = 5000;

    private volatile bool _disposed;

    public PlaybackErrorOrchestrator(
         YoutubeProvider youtube,
         AudioEngine audioEngine,
         NotificationService notificationService,
         LibraryService libraryService)
    {
        _youtube = youtube ?? throw new ArgumentNullException(nameof(youtube));
        _audioEngine = audioEngine ?? throw new ArgumentNullException(nameof(audioEngine));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));

        _audioEngine.OnErrorOccurred += HandleError;
        _audioEngine.OnDeviceLost += HandleDeviceLost;
        _audioEngine.OnDeviceRestored += HandleDeviceRestored;
        _audioEngine.OnNTokenDecryptionWarning += HandleNTokenDecryptionWarning;

        Core.Audio.Sources.CachingStreamSource.OnSourceWarning += HandleSourceWarning;

        Log.Info("[PlaybackErrorOrchestrator] Initialized and ready");
    }

    #region Error Handling

    private void HandleSourceWarning(string trackId, Exception exception)
    {
        if (_disposed) return;

        string warningKey = $"warning_{trackId}_{exception.GetType().Name}";
        if (!TryRegisterError(warningKey)) return;

        Log.Warn($"[Orchestrator] Non-fatal stream warning for track {trackId}: {exception.Message}");

        _ = ShowStreamWarningToastAsync();
    }

    private async Task ShowStreamWarningToastAsync()
    {
        try
        {
            await _notificationService.ShowToastAsync(
                titleKey: "Notification_PlaybackDelay_Title",
                messageKey: "Notification_PlaybackDelay_Message",
                severity: NotificationSeverity.Warning,
                durationMs: SkipToastDurationMs);
        }
        catch (Exception ex)
        {
            Log.Warn($"[Orchestrator] Failed to show warning toast: {ex.Message}");
        }
    }

    private void HandleDeviceLost()
    {
        if (_disposed || !TryRegisterError("device_lost")) return;
        Log.Info("[Orchestrator] Device lost — showing informational toast");
        _ = ShowDeviceLostToastAsync();
    }

    private async Task ShowDeviceLostToastAsync()
    {
        try
        {
            var L = LocalizationService.Instance;
            string title = L["Notification_DeviceLost_Title"];
            string message = L["Notification_DeviceLost_Message"];

            await _notificationService.ShowToastAsync(
                titleKey: "Notification_DeviceLost_Title",
                messageKey: "Notification_DeviceLost_Message",
                severity: NotificationSeverity.Warning,
                durationMs: SkipToastDurationMs);

            await NotificationService.ShowOsNotificationAsync(title, message, NotificationSeverity.Warning);
        }
        catch (Exception ex)
        {
            Log.Warn($"[Orchestrator] Failed to show device lost toast: {ex.Message}");
        }
    }

    private void HandleDeviceRestored()
    {
        if (_disposed) return;
        Log.Info("[Orchestrator] Device restored — showing success toast");
        _ = ShowDeviceRestoredToastAsync();
    }

    private void HandleNTokenDecryptionWarning(AudioEngine.NTokenWarningInfo warning)
    {
        if (_disposed)
            return;

        var mode = _libraryService.Settings.Audio.NTokenNotificationMode;
        switch (mode)
        {
            case NTokenNotificationMode.Disabled:
                Log.Debug($"[Orchestrator] N-Token warning suppressed for track '{warning.Track?.Id}' as configured.");
                return;

            case NTokenNotificationMode.PanelOnly:
                _ = PublishNTokenWarningAsync(warning, showToast: false);
                return;

            default:
                _ = PublishNTokenWarningAsync(warning, showToast: true);
                return;
        }
    }

    private async Task PublishNTokenWarningAsync(
        AudioEngine.NTokenWarningInfo warning,
        bool showToast)
    {
        try
        {
            var track = warning.Track;
            string trackDisplay = track?.Title ?? track?.Id ?? "Unknown";
            string? trackTitle = track?.Title ?? track?.Id;
            string messageKey = warning.WasSkipped
                ? "Notification_NToken_Skipped"
                : "Notification_NToken_Message";

            if (showToast)
            {
                await _notificationService.ShowToastAsync(
                    titleKey: "Notification_NToken_Title",
                    messageKey: messageKey,
                    severity: NotificationSeverity.Warning,
                    messageArgs: [trackDisplay],
                    trackId: track?.Id,
                    trackTitle: trackTitle);
            }
            else
            {
                await _notificationService.AddToPanelAsync(
                    titleKey: "Notification_NToken_Title",
                    messageKey: messageKey,
                    severity: NotificationSeverity.Warning,
                    messageArgs: [trackDisplay],
                    trackId: track?.Id,
                    trackTitle: trackTitle);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[Orchestrator] Failed to publish n-token warning: {ex.Message}");
        }
    }

    private async Task ShowDeviceRestoredToastAsync()
    {
        try
        {
            await _notificationService.ShowToastAsync(
                titleKey: "Notification_DeviceRestored_Title",
                messageKey: "Notification_DeviceRestored_Message",
                severity: NotificationSeverity.Success,
                durationMs: SkipToastDurationMs);
        }
        catch (Exception ex)
        {
            Log.Warn($"[Orchestrator] Failed to show device restored toast: {ex.Message}");
        }
    }

    private void HandleError(Exception exception)
    {
        if (_disposed) return;
        Log.Info($"[Orchestrator] ◆ Received error event: {exception.GetType().Name}: {exception.Message}");
        _ = HandleErrorAsync(exception);
    }

    public async Task HandleErrorAsync(Exception exception)
    {
        if (_disposed) return;

        var actualException = UnwrapException(exception);
        if (NetworkErrorHelper.IsCancellationLike(exception) || NetworkErrorHelper.IsCancellationLike(actualException))
        {
            Log.Debug($"[Orchestrator] Suppressing cancelled error: {exception.GetType().Name}");
            return;
        }

        if (IsStalePlaybackError(actualException))
        {
            Log.Debug($"[Orchestrator] Suppressing stale error: {actualException.GetType().Name}");
            return;
        }

        string errorKey = GetErrorDeduplicationKey(actualException);
        bool isDuplicate = !TryRegisterError(errorKey);

        Log.Info($"[Orchestrator] Handling error: {actualException.GetType().Name} (isDuplicate={isDuplicate})");

        try
        {
            await (actualException switch
            {
                AudioDeviceException devEx => HandleAudioDeviceUnavailableAsync(devEx),
                BotDetectionException botEx => HandleBotDetectionAsync(botEx),
                CdnUnavailableException cdnEx
                    => HandleCdnUnavailableAsync(cdnEx, isDuplicate),
                YoutubeNetworkException netEx => HandleNetworkErrorAsync(netEx, isDuplicate),
                LoginRequiredException loginEx => HandleLoginRequiredAsync(loginEx, isDuplicate),
                StreamUnavailableException streamEx => HandleStreamUnavailableAsync(streamEx, isDuplicate),
                VideoUnplayableException unplayableEx => HandleVideoUnplayableAsync(unplayableEx, isDuplicate),
                ChunkDownloadFatalException chunkEx => HandleChunkFatalAsync(chunkEx, isDuplicate),
                _ => HandleGenericErrorAsync(actualException, isDuplicate)
            });
        }
        catch (Exception ex)
        {
            Log.Error($"[Orchestrator] Error in handler: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Handles audio output device unreadiness without killing or sealing the active track.
    /// </summary>
    private async Task HandleAudioDeviceUnavailableAsync(AudioDeviceException exception)
    {
        Log.Warn($"[Orchestrator] Audio device endpoint is currently unavailable: {exception.Message}. Playback preserved in paused state.");

        if (!TryRegisterError("audio_device_unavailable"))
            return;

        _notificationService.TryPlayErrorSound();

        await _notificationService.ShowToastAsync(
            titleKey: "Notification_DeviceLost_Title",
            messageKey: "Notification_DeviceLost_Message",
            severity: NotificationSeverity.Warning,
            durationMs: SkipToastDurationMs);

        await NotificationService.ShowOsNotificationAsync(
            LocalizationService.Instance["Notification_DeviceLost_Title"],
            LocalizationService.Instance["Notification_DeviceLost_Message"],
            NotificationSeverity.Warning);
    }

    private async Task HandleCdnUnavailableAsync(
        CdnUnavailableException exception,
        bool isDuplicate)
    {
        Log.Warn($"[Orchestrator] CDN unavailable (all failover attempts exhausted): " +
                 $"host={exception.Host}");

        await DispatchPlaybackErrorAsync(
            exception,
            "Error_Cdn_Unavailable",
            null,
            isDuplicate,
            recommendationKeyOverride: "Recommendation_DpiBlocked");
    }

    private async Task HandleNetworkErrorAsync(YoutubeNetworkException exception, bool isDuplicate)
    {
        Log.Warn($"[Orchestrator] Network error: {exception.ErrorType} — {exception.Message}");

        var messageKey = exception.GetLocalizationKey();
        var recommendationKey = exception.GetRecommendationKey();

        await DispatchPlaybackErrorAsync(exception, messageKey, null, isDuplicate, recommendationKey);
    }

    private async Task HandleLoginRequiredAsync(LoginRequiredException exception, bool isDuplicate)
    {
        Log.Warn($"[Orchestrator] Login required: {exception.Reason} for {exception.VideoId}");

        await InvokeOnUIAsync(() => _audioEngine.SetPlaybackStateAsync(false));

        if (isDuplicate)
        {
            Log.Debug("[Orchestrator] Suppressing duplicate LoginRequired notification toast");
            return;
        }

        _notificationService.TryPlayErrorSound();

        var messageKey = PlaybackErrorMappingHelper.GetMessageKey(exception);
        var recommendationKey = PlaybackErrorMappingHelper.GetRecommendationKey(exception, _youtube.AuthService.IsAuthenticated);
        var (Id, Title) = GetCurrentTrackInfo();

        await _notificationService.ShowPlaybackErrorAsync(
            "Error_Playback_Title", messageKey, Id, Title, null, exception.ToString(),
            NotificationSeverity.Error, DialogToastDurationMs, recommendationKey);

        await NotificationService.ShowOsNotificationAsync(
            LocalizationService.Instance["Error_Playback_Title"],
            LocalizationService.Instance[messageKey],
            NotificationSeverity.Error);
    }

    private async Task HandleStreamUnavailableAsync(StreamUnavailableException exception, bool isDuplicate)
    {
        Log.Error($"[Orchestrator] Stream unavailable: {exception.Reason} for {exception.VideoId}");
        var attempts = ExtractAttemptsFromException(exception);
        var messageKey = PlaybackErrorMappingHelper.GetMessageKey(exception);
        await DispatchPlaybackErrorAsync(exception, messageKey, attempts, isDuplicate);
    }

    private async Task HandleVideoUnplayableAsync(VideoUnplayableException exception, bool isDuplicate)
    {
        Log.Error($"[Orchestrator] Video unplayable: {exception.VideoId} — {exception.Message}");
        var messageKey = PlaybackErrorMappingHelper.GetMessageKey(exception);
        await DispatchPlaybackErrorAsync(exception, messageKey, null, isDuplicate);
    }

    private async Task HandleChunkFatalAsync(ChunkDownloadFatalException exception, bool isDuplicate)
    {
        Log.Error($"[Orchestrator] Chunk fatal: {exception.Reason} at chunk {exception.ChunkIndex}");
        var attempts = new List<AttemptRecord> {
            new($"Chunk {exception.ChunkIndex}", false, $"{exception.Reason}: {exception.Message}", DateTime.UtcNow)
        };
        var messageKey = PlaybackErrorMappingHelper.GetMessageKey(exception);
        await DispatchPlaybackErrorAsync(exception, messageKey, attempts, isDuplicate);
    }

    private async Task HandleGenericErrorAsync(Exception exception, bool isDuplicate)
    {
        Log.Error($"[Orchestrator] Generic error: {exception.Message}");
        await DispatchPlaybackErrorAsync(exception, exception.Message, null, isDuplicate);
    }

    #endregion

    #region Specific Error Handlers

    private bool IsStalePlaybackError(Exception exception)
    {
        var current = _audioEngine.CurrentTrack;
        if (current == null) return true;
        var currentRaw = current.GetRawIdSpan();

        return exception switch
        {
            StreamUnavailableException stream => !currentRaw.SequenceEqual(stream.VideoId.AsSpan()),
            LoginRequiredException login => !currentRaw.SequenceEqual(login.VideoId.AsSpan()),
            VideoUnplayableException vp => !currentRaw.SequenceEqual(vp.VideoId.AsSpan()),
            ChunkDownloadFatalException chunk => !string.Equals(current.Id, chunk.TrackId, StringComparison.Ordinal),
            _ => false
        };
    }

    private async Task HandleBotDetectionAsync(BotDetectionException exception)
    {
        int waitSeconds = Math.Max(1, (int)Math.Ceiling(exception.RemainingCooldown.TotalSeconds));
        Log.Warn($"[Orchestrator] Bot detection active: cooldown {waitSeconds}s. Showing non-blocking notification.");

        await InvokeOnUIAsync(_audioEngine.Stop);

        _notificationService.TryPlayErrorSound();

        await _notificationService.ShowToastAsync(
            titleKey: "Dialog_BotDetection_Title",
            messageKey: "Dialog_BotDetection_Message",
            severity: NotificationSeverity.Warning,
            durationMs: Math.Min(waitSeconds * 1000, 10_000),
            messageArgs: [waitSeconds],
            recommendationKey: "Recommendation_CheckNetwork");
    }

    #endregion

    #region Behavior Strategies

    private async Task DispatchPlaybackErrorAsync(
        Exception exception,
        string messageOrKey,
        List<AttemptRecord>? attempts = null,
        bool skipNotification = false,
        string? recommendationKeyOverride = null)
    {
        var (trackId, trackTitle) = GetCurrentTrackInfo();

        var configuredFailureBehavior = _libraryService.Settings.Audio.PlaybackFailureBehavior;
        if (exception is VideoUnplayableException
            && configuredFailureBehavior == PlaybackFailureBehavior.SkipAndPause)
        {
            configuredFailureBehavior = PlaybackFailureBehavior.SkipAndPlay;
        }

        var policy = PlaybackErrorBehaviorMatrix.Resolve(
            _libraryService.Settings.Audio.CriticalErrorBehavior,
            configuredFailureBehavior);

        await ApplyRecoveryPolicyAsync(policy);

        if (!policy.ShowNotification)
        {
            Log.Debug("[Orchestrator] Recovered silently according to CriticalErrorBehavior.");
            return;
        }

        if (skipNotification)
        {
            Log.Debug($"[Orchestrator] Silently recovered duplicate error without UI notification: {messageOrKey}");
            return;
        }

        bool isSslFailure = exception is YoutubeNetworkException { IsSslFailure: true }
                         || NetworkErrorHelper.IsSslOrTlsHandshakeFailure(exception);

        string finalMessageOrKey = isSslFailure ? "Error_SslHandshake_Failed" : messageOrKey;

        string? recommendationKey = recommendationKeyOverride
            ?? (isSslFailure
                ? "Recommendation_DpiBlocked"
                : PlaybackErrorMappingHelper.GetRecommendationKey(exception, _youtube.AuthService.IsAuthenticated));

        _notificationService.TryPlayErrorSound();

        var severity = NotificationSeverity.Error;
        int duration = policy.RequiresUserAction ? DialogToastDurationMs : SkipToastDurationMs;

        object[]? messageArgs = null;
        if (exception is StreamUnavailableException { Reason: StreamUnavailableReason.CopyrightBlocked } copyrightEx)
        {
            string claimant = PlayabilityErrorClassifier.ExtractCopyrightHolder(copyrightEx.Message);
            messageArgs = [claimant];
        }

        await _notificationService.ShowPlaybackErrorAsync(
            "Error_Playback_Title", finalMessageOrKey, trackId, trackTitle, attempts, exception.ToString(),
            severity, durationMs: duration, recommendationKey: recommendationKey, messageArgs: messageArgs);

        string localizedTitle = LocalizationService.Instance["Error_Playback_Title"];
        string localizedMessage = finalMessageOrKey.StartsWith("Error_")
            ? (messageArgs != null
                ? string.Format(LocalizationService.Instance[finalMessageOrKey], messageArgs)
                : LocalizationService.Instance[finalMessageOrKey])
            : finalMessageOrKey;

        await NotificationService.ShowOsNotificationAsync(localizedTitle, localizedMessage, severity);
    }

    private Task ApplyRecoveryPolicyAsync(EffectivePlaybackErrorPolicy policy)
    {
        return policy.RecoveryMode switch
        {
            PlaybackRecoveryMode.AwaitUserAction => PauseOrStopForUserActionAsync(),
            PlaybackRecoveryMode.Stop => InvokeOnUIAsync(_audioEngine.StopAfterFatalPlaybackError),
            PlaybackRecoveryMode.SkipAndPause => _audioEngine.Queue.Count <= 1
                ? InvokeOnUIAsync(_audioEngine.StopAfterFatalPlaybackError)
                : InvokeOnUIAsync(() => _audioEngine.PlayNextAsync(startPlaying: false)),
            PlaybackRecoveryMode.SkipAndPlay => _audioEngine.Queue.Count <= 1
                ? InvokeOnUIAsync(_audioEngine.StopAfterFatalPlaybackError)
                : InvokeOnUIAsync(() => _audioEngine.PlayNextAsync(startPlaying: true)),
            _ => _audioEngine.Queue.Count <= 1
                ? InvokeOnUIAsync(_audioEngine.StopAfterFatalPlaybackError)
                : InvokeOnUIAsync(() => _audioEngine.PlayNextAsync(startPlaying: false))
        };
    }

    private Task PauseOrStopForUserActionAsync()
    {
        if (_audioEngine.Queue.Count <= 1)
            return InvokeOnUIAsync(_audioEngine.StopAfterFatalPlaybackError);

        return InvokeOnUIAsync(() => _audioEngine.SetPlaybackStateAsync(false));
    }

    #endregion

    #region Helpers

    private (string Id, string Title) GetCurrentTrackInfo()
    {
        var track = _audioEngine.CurrentTrack;
        return (track?.Id ?? "unknown", track?.Title ?? "Unknown Track");
    }

    private static List<AttemptRecord> ExtractAttemptsFromException(StreamUnavailableException exception)
    {
        return
        [
            new AttemptRecord(
                "Stream Request",
                false,
                $"{exception.Reason}: {exception.Message}",
                DateTime.UtcNow)
        ];
    }

    private static Exception UnwrapException(Exception exception)
    {
        if (exception is AggregateException agg && agg.InnerExceptions.Count > 0)
            return UnwrapException(agg.InnerExceptions[0]);

        var inner = exception.InnerException;
        while (inner != null)
        {
            if (inner is BotDetectionException or
                LoginRequiredException or
                StreamUnavailableException or
                ChunkDownloadFatalException or
                VideoUnplayableException or
                YoutubeNetworkException or
                CdnUnavailableException or
                AudioDeviceException)
            {
                return inner;
            }
            inner = inner.InnerException;
        }

        return exception;
    }

    private static string GetErrorDeduplicationKey(Exception exception)
    {
        if (NetworkErrorHelper.IsSslOrTlsHandshakeFailure(exception))
            return "ssl_handshake_failure";

        return exception switch
        {
            AudioDeviceException => "audio_device_unavailable",
            BotDetectionException => "bot_detection",
            YoutubeNetworkException net => $"network_{net.ErrorType}",
            LoginRequiredException login => $"login_{login.VideoId}",
            StreamUnavailableException stream => $"stream_{stream.VideoId}_{stream.Reason}",
            VideoUnplayableException vp => $"unplayable_{vp.VideoId}",
            ChunkDownloadFatalException chunk => $"chunk_{chunk.TrackId}_{chunk.Reason}",
            _ => $"generic_{exception.GetType().Name}_{exception.Message.GetHashCode()}"
        };
    }

    private bool TryRegisterError(string errorKey)
    {
        long now = Environment.TickCount64;
        long windowMs = (long)ErrorDeduplicationWindow.TotalMilliseconds;
        long cleanupMs = (long)ErrorCacheCleanupInterval.TotalMilliseconds;

        lock (_recentlyShownErrors)
        {
            if (now - _lastErrorCleanupTick > cleanupMs)
            {
                _lastErrorCleanupTick = now;
                if (_recentlyShownErrors.Count > 0)
                {
                    List<string>? expired = null;
                    foreach (var kvp in _recentlyShownErrors)
                    {
                        if (kvp.Value <= now)
                        {
                            expired ??= [];
                            expired.Add(kvp.Key);
                        }
                    }

                    if (expired != null)
                    {
                        for (int i = 0; i < expired.Count; i++)
                            _recentlyShownErrors.Remove(expired[i]);
                    }
                }
            }

            if (_recentlyShownErrors.TryGetValue(errorKey, out long expiresAt) && expiresAt > now)
                return false;

            _recentlyShownErrors[errorKey] = now + windowMs;
            return true;
        }
    }

    private static async Task InvokeOnUIAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else await Dispatcher.UIThread.InvokeAsync(action);
    }

    private static async Task InvokeOnUIAsync(Func<Task> action)
    {
        if (Dispatcher.UIThread.CheckAccess()) await action();
        else await Dispatcher.UIThread.InvokeAsync(action);
    }

    #endregion

    #region Dispose

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _audioEngine.OnErrorOccurred -= HandleError;
        _audioEngine.OnDeviceLost -= HandleDeviceLost;
        _audioEngine.OnDeviceRestored -= HandleDeviceRestored;
        _audioEngine.OnNTokenDecryptionWarning -= HandleNTokenDecryptionWarning;

        Core.Audio.Sources.CachingStreamSource.OnSourceWarning -= HandleSourceWarning;

        lock (_recentlyShownErrors) _recentlyShownErrors.Clear();

        Log.Info("[PlaybackErrorOrchestrator] Disposed");
    }

    #endregion
}