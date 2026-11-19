using System.Text;
using LMP.Core.Audio.Http;

namespace LMP.Core.Services;

/// <summary>
/// Runtime connection status of <see cref="DiscordRpcService"/>.
/// </summary>
public enum DiscordRpcConnectionState : byte
{
    Disabled = 0,
    MissingClientId = 1,
    Connecting = 2,
    Connected = 3,
    WaitingForDiscord = 4,
    Error = 5
}

/// <summary>
/// High-level shell page reported by the UI for idle Rich Presence context.
/// </summary>
public enum DiscordNavigationPage : byte
{
    Unknown = 0,
    Home = 1,
    Search = 2,
    Library = 3,
    Queue = 4,
    Settings = 5,
    Playlist = 6
}

/// <summary>
/// Live preview snapshot for rendering an accurate Discord Rich Presence card inside Settings UI.
/// </summary>
public sealed record DiscordRpcPreviewSnapshot(
    bool IsVisibleInDiscord,
    bool IsDemoTrack,
    bool IsPaused,
    DiscordRpcActivityType ActivityType,
    string? Details,
    string? State,
    string? LargeText,
    string? CoverUrl,
    string? SmallImageUrl,
    string? SmallImageTooltip,
    bool ShowProgressBar,
    bool ShowElapsedOnly,
    double ProgressFraction,
    string ElapsedText,
    string DurationText,
    string? Button1Label,
    string? Button2Label)
{
    public static DiscordRpcPreviewSnapshot Empty { get; } = new(
        IsVisibleInDiscord: false,
        IsDemoTrack: false,
        IsPaused: false,
        ActivityType: DiscordRpcActivityType.Listening,
        Details: null,
        State: null,
        LargeText: null,
        CoverUrl: null,
        SmallImageUrl: null,
        SmallImageTooltip: null,
        ShowProgressBar: false,
        ShowElapsedOnly: false,
        ProgressFraction: 0,
        ElapsedText: "00:00",
        DurationText: "00:00",
        Button1Label: null,
        Button2Label: null);

    public double ProgressPercent => ProgressFraction * 100.0;

    public bool HasButtons =>
        !string.IsNullOrEmpty(Button1Label) || !string.IsNullOrEmpty(Button2Label);
}

/// <summary>
/// Cached public playlist metadata resolved asynchronously from <see cref="PlaylistService"/>.
/// </summary>
internal readonly record struct CachedPlaylistRpcMeta(
    string PlaylistId,
    bool IsPublicCloud,
    string? PublicUrl,
    string? ThumbnailUrl,
    string? DisplayName,
    bool IsThumbnailVerified);

/// <summary>
/// Immutable snapshot of playback, navigation, and configuration state used to build a Discord Rich Presence payload.
/// </summary>
internal readonly record struct DiscordPresenceInput(
    bool Enabled,
    string ClientId,
    DiscordRpcActivityType ActivityType,
    DiscordRpcStatusDisplay StatusDisplay,
    DiscordRpcTimestampMode TimestampMode,
    DiscordRpcDetailsFormat DetailsFormat,
    DiscordRpcPausedBehavior PausedBehavior,
    bool ShowCoverArt,
    bool ShowTrackButton,
    bool ShowGitHubButton,
    bool ShowPlaylistName,
    bool ShowPublicPlaylistLink,
    bool ShowLikeStatus,
    bool ShowRepeatMode,
    bool ShowShuffleMode,
    bool ShowQueuePosition,
    bool ShowListenerChannel,
    bool ShowIdleStatus,
    bool StripAuthorFromTitle,
    DiscordRpcBadgeStyle BadgeStyle,
    string? CustomBadgeUrl,
    string? TrackId,
    string? RawVideoId,
    string? TrackTitle,
    string? TrackAuthor,
    string? ChannelId,
    string? ThumbnailUrl,
    bool IsLiked,
    RepeatMode RepeatMode,
    bool ShuffleEnabled,
    int QueueIndex,
    int QueueCount,
    TimeSpan Position,
    TimeSpan Duration,
    PlaybackState PlaybackState,
    string? PlaylistName,
    string? PublicPlaylistUrl,
    string? PublicPlaylistThumbnailUrl,
    string? ListenerHandle,
    string? ListenerChannelUrl,
    string? ListenerAvatarUrl,
    DiscordNavigationPage CurrentPage,
    string? ViewingPlaylistName,
    string? ViewingPlaylistUrl,
    string? ViewingPlaylistThumbnailUrl,
    DateTimeOffset? IdleSinceUtc,
    DateTimeOffset CapturedAtUtc);

/// <summary>
/// Background actor coordinating Discord Rich Presence over local IPC.
/// Features:
/// <list type="bullet">
///   <item>Zero UI/audio thread blocking (dedicated event-driven async worker).</item>
///   <item>Debounce + 2.5s rate-limit throttle for seeks and rapid track switches.</item>
///   <item>Zero-exception exponential backoff (<see cref="CancellationTokenExtensions.DelayNoThrowAsync(CancellationToken, int, bool)"/>) when Discord is closed.</item>
///   <item>Automatic one-shot refresh of expired YouTube playlist thumbnails so Discord never shows broken images.</item>
///   <item>Clean activity clear and IPC close on disable or application shutdown.</item>
/// </list>
/// </summary>
public sealed class DiscordRpcService : IAsyncDisposable
{
    /// <summary>
    /// Official Lite Music Player Discord Application ID.
    /// Can be overridden per-user via <see cref="DiscordRpcSettings.CustomClientId"/>.
    /// </summary>
    public const string DefaultClientId = "1572757212286423131";

    // Crisp monochrome Unicode glyphs (never converted to bright Twemoji in Discord).
    internal const string LikedGlyph = "♥\uFE0E";
    internal const string UnlikedGlyph = "♡";
    internal const string RepeatOneGlyph = "↻1";
    internal const string RepeatAllGlyph = "↻";
    internal const string ShuffleGlyph = "⇄";
    internal const string PauseGlyph = "❚❚";

    private const int DebounceMs = 400;
    private const int MinUpdateIntervalMs = 2_500;
    private const int LoadingGraceTimeoutMs = 800;
    private const int InitialReconnectDelayMs = 3_000;
    private const int MaxReconnectDelayMs = 30_000;
    private const int KeepAliveIntervalMs = 30_000;
    private const int ImageProbeTimeoutMs = 2_500;
    private const int MaxUtf8FieldBytes = 128;
    private const int MaxButtonLabelBytes = 32;
    private const int MaxAssetUrlLength = 256;
    private const int ShutdownGraceTimeoutMs = 1_500;

    private readonly PlayerControlService _playerControl;
    private readonly LibraryService _library;
    private readonly PlaylistService? _playlistService;
    private readonly CookieAuthService? _authService;
    private readonly Lazy<YoutubeProvider>? _youtube;
    private readonly DiscordIpcClient _ipc;
    private readonly Lock _stateLock = new();
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly HashSet<string> _refreshedPlaylistThumbIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string? SourceUrl, string? VerifiedUrl)> _verifiedThumbCache = new(StringComparer.Ordinal);

    private Task? _workerTask;
    private bool _started;
    private bool _dirty = true;
    private bool _forcePublish;
    private bool _forceReconnect;
    private bool _stopRequested;
    private int _disposeState;

    private DiscordRpcConnectionState _connectionState = DiscordRpcConnectionState.Disabled;
    private string? _connectedUserName;
    private string? _lastError;
    private CachedPlaylistRpcMeta? _cachedActivePlaylist;

    // Navigation & idle context
    private DiscordNavigationPage _currentPage = DiscordNavigationPage.Home;
    private string? _viewingPlaylistId;
    private CachedPlaylistRpcMeta? _cachedViewingPlaylist;
    private DateTimeOffset _idleSinceUtc = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets the current IPC connection state.
    /// </summary>
    public DiscordRpcConnectionState ConnectionState
    {
        get
        {
            lock (_stateLock) return _connectionState;
        }
    }

    /// <summary>
    /// Gets the display name of the connected Discord user when <see cref="ConnectionState"/> is <see cref="DiscordRpcConnectionState.Connected"/>.
    /// </summary>
    public string? ConnectedUserName
    {
        get
        {
            lock (_stateLock) return _connectedUserName;
        }
    }

    /// <summary>
    /// Gets the last IPC error message when <see cref="ConnectionState"/> is <see cref="DiscordRpcConnectionState.Error"/>.
    /// </summary>
    public string? LastError
    {
        get
        {
            lock (_stateLock) return _lastError;
        }
    }

    /// <summary>
    /// Raised whenever <see cref="ConnectionState"/>, <see cref="ConnectedUserName"/>, or <see cref="LastError"/> changes.
    /// </summary>
    public event Action? StatusChanged;

    /// <summary>
    /// Raised whenever playback or settings changes may affect the live Rich Presence preview.
    /// </summary>
    public event Action? PreviewChanged;

    public DiscordRpcService(
        PlayerControlService playerControl,
        LibraryService library,
        PlaylistService? playlistService = null,
        CookieAuthService? authService = null,
        Lazy<YoutubeProvider>? youtube = null)
        : this(playerControl, library, playlistService, authService, youtube, new DiscordIpcClient())
    {
    }

    internal DiscordRpcService(
        PlayerControlService playerControl,
        LibraryService library,
        PlaylistService? playlistService,
        CookieAuthService? authService,
        Lazy<YoutubeProvider>? youtube,
        DiscordIpcClient ipcClient)
    {
        _playerControl = playerControl;
        _library = library;
        _playlistService = playlistService;
        _authService = authService;
        _youtube = youtube;
        _ipc = ipcClient;

        _playerControl.CurrentTrackChanged += HandleTrackChanged;
        _playerControl.IsPlayingChanged += HandleBoolChanged;
        _playerControl.IsPausedChanged += HandleBoolChanged;
        _playerControl.PlaybackStateChanged += HandlePlaybackStateChanged;
        _playerControl.SeekCompleted += HandleSeekCompleted;
        _playerControl.PlaybackPurityChanged += HandlePlaybackPurityChanged;
        _playerControl.RepeatModeChanged += HandleRepeatModeChanged;
        _playerControl.ShuffleEnabledChanged += HandleBoolChanged;
        _playerControl.QueueChanged += HandleQueueChanged;
        _playerControl.ForceSyncTriggered += HandleForceSync;
        _library.OnTrackUpdated += HandleLibraryTrackUpdated;

        _playlistService?.OnPlaylistChanged += HandlePlaylistMetadataChanged;

        _authService?.OnAuthStateChanged += HandleAuthStateChanged;
        LocalizationService.Instance.LanguageChanged += HandleLanguageChanged;
    }

    /// <summary>
    /// Starts the background Discord RPC actor loop idempotently.
    /// Safe to call from UI or background startup code (never blocks).
    /// </summary>
    public void Start()
    {
        lock (_stateLock)
        {
            if (_started || Volatile.Read(ref _disposeState) != 0)
                return;

            _started = true;
            _dirty = true;
            _workerTask = Task.Run(RunWorkerLoopAsync);
        }

        SignalWorker();
    }

    /// <summary>
    /// Updates the currently visible UI shell page for idle Rich Presence context.
    /// Does not emit IPC updates while a track is actively playing or paused.
    /// </summary>
    public void SetActivePage(DiscordNavigationPage page)
    {
        if (Volatile.Read(ref _disposeState) != 0)
            return;

        bool changed;
        lock (_stateLock)
        {
            changed = _currentPage != page;
            _currentPage = page;
            if (page != DiscordNavigationPage.Playlist)
            {
                _viewingPlaylistId = null;
                _cachedViewingPlaylist = null;
            }
        }

        if (changed && IsCurrentlyIdleForNavigation())
            MarkDirty();
    }

    /// <summary>
    /// Updates the playlist currently opened in <c>PlaylistViewModel</c> for idle Rich Presence display.
    /// </summary>
    public void SetViewingPlaylist(Playlist? playlist)
    {
        if (Volatile.Read(ref _disposeState) != 0)
            return;

        string? newId = playlist?.Id;
        bool changed;
        lock (_stateLock)
        {
            changed = !string.Equals(_viewingPlaylistId, newId, StringComparison.Ordinal)
                || _currentPage != DiscordNavigationPage.Playlist;

            _currentPage = DiscordNavigationPage.Playlist;
            _viewingPlaylistId = newId;

            if (playlist == null)
            {
                _cachedViewingPlaylist = null;
            }
            else
            {
                bool isPublic = IsPublicCloudPlaylist(playlist);
                string? rawThumb = isPublic && IsValidExternalImageUrl(playlist.ThumbnailUrl)
                    ? playlist.ThumbnailUrl!.Trim()
                    : null;

                if (_cachedViewingPlaylist is not { } existing
                    || !string.Equals(existing.PlaylistId, playlist.Id, StringComparison.Ordinal)
                    || existing.IsPublicCloud != isPublic
                    || !string.Equals(existing.DisplayName, playlist.Name, StringComparison.Ordinal))
                {
                    bool verified = !isPublic;
                    string? cachedThumb = null;
                    if (isPublic && _verifiedThumbCache.TryGetValue(playlist.Id, out var cached)
                        && string.Equals(cached.SourceUrl, rawThumb, StringComparison.Ordinal))
                    {
                        verified = true;
                        cachedThumb = cached.VerifiedUrl;
                    }

                    _cachedViewingPlaylist = new CachedPlaylistRpcMeta(
                        PlaylistId: playlist.Id,
                        IsPublicCloud: isPublic,
                        PublicUrl: isPublic ? playlist.Url : null,
                        ThumbnailUrl: cachedThumb,
                        DisplayName: playlist.Name,
                        IsThumbnailVerified: verified);
                    changed = true;
                }
            }
        }

        if (changed && IsCurrentlyIdleForNavigation())
            MarkDirty();
    }

    /// <summary>
    /// Re-reads <see cref="AppSettings.DiscordRpcEnabled"/> and <see cref="AppSettings.DiscordRpc"/>
    /// and schedules an immediate presence refresh (or clean disconnect when disabled).
    /// </summary>
    public void ApplySettings()
    {
        if (Volatile.Read(ref _disposeState) != 0)
            return;

        if (!_started)
            Start();

        lock (_stateLock)
        {
            _dirty = true;
        }

        SignalWorker();
        PreviewChanged?.Invoke();
    }

    /// <summary>
    /// Resets reconnect backoff and triggers an immediate IPC reconnect attempt.
    /// </summary>
    public void Reconnect()
    {
        if (Volatile.Read(ref _disposeState) != 0)
            return;

        if (!_started)
            Start();

        lock (_stateLock)
        {
            _forceReconnect = true;
            _dirty = true;
        }

        SignalWorker();
    }

    /// <summary>
    /// Validates a Discord Application ID snowflake (17–22 ASCII decimal digits).
    /// </summary>
    public static bool IsValidClientId(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return false;

        var span = clientId.AsSpan().Trim();
        if (span.Length is < 17 or > 22)
            return false;

        for (int i = 0; i < span.Length; i++)
        {
            if ((uint)(span[i] - '0') > 9u)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Resolves the effective Discord Application ID from user settings or <see cref="DefaultClientId"/>.
    /// </summary>
    public static string ResolveEffectiveClientId(DiscordRpcSettings? settings)
    {
        string? custom = settings?.CustomClientId?.Trim();
        if (IsValidClientId(custom))
            return custom!;

        return IsValidClientId(DefaultClientId) ? DefaultClientId : string.Empty;
    }

    private bool IsCurrentlyIdleForNavigation()
    {
        if (!_library.Settings.DiscordRpcEnabled || !_library.Settings.DiscordRpc.ShowIdleStatus)
            return false;

        var state = _playerControl.PlaybackState;
        return _playerControl.CurrentTrack == null
            || state is PlaybackState.Stopped or PlaybackState.Error;
    }

    private void HandleTrackChanged(TrackInfo? track)
    {
        if (track == null)
        {
            lock (_stateLock)
                _idleSinceUtc = DateTimeOffset.UtcNow;
        }

        MarkDirty();
    }

    private void HandleBoolChanged(bool _) => MarkDirty();

    private void HandlePlaybackStateChanged(PlaybackState state)
    {
        if (state is PlaybackState.Stopped or PlaybackState.Error)
        {
            lock (_stateLock)
                _idleSinceUtc = DateTimeOffset.UtcNow;
        }

        MarkDirty();
    }

    private void HandleSeekCompleted(TimeSpan _)
    {
        lock (_stateLock)
        {
            _forcePublish = true;
        }

        MarkDirty();
    }

    private void HandleRepeatModeChanged(RepeatMode _) => MarkDirty();
    private void HandleQueueChanged() => MarkDirty();
    private void HandleForceSync() => MarkDirty();
    private void HandleAuthStateChanged() => MarkDirty();
    private void HandleLanguageChanged(object? sender, string _) => MarkDirty();

    private void HandlePlaybackPurityChanged(string? playlistId, bool _, bool _1)
    {
        lock (_stateLock)
        {
            if (_cachedActivePlaylist is { } cached
                && !string.Equals(cached.PlaylistId, playlistId, StringComparison.Ordinal))
            {
                _cachedActivePlaylist = null;
            }
        }

        MarkDirty();
    }

    private void HandleLibraryTrackUpdated(TrackInfo updatedTrack)
    {
        var current = _playerControl.CurrentTrack;
        if (current != null && string.Equals(current.Id, updatedTrack.Id, StringComparison.Ordinal))
            MarkDirty();
    }

    private void HandlePlaylistMetadataChanged(Playlist playlist)
    {
        if (playlist == null)
            return;

        bool affectsActive = string.Equals(_playerControl.ActivePlaylistId, playlist.Id, StringComparison.Ordinal);
        bool affectsViewing;
        lock (_stateLock)
        {
            _verifiedThumbCache.Remove(playlist.Id);
            affectsViewing = string.Equals(_viewingPlaylistId, playlist.Id, StringComparison.Ordinal);
            if (affectsActive)
                _cachedActivePlaylist = null;
            if (affectsViewing)
                _cachedViewingPlaylist = null;
        }

        if (affectsActive || (affectsViewing && IsCurrentlyIdleForNavigation()))
            MarkDirty();
    }

    private void MarkDirty()
    {
        if (Volatile.Read(ref _disposeState) != 0)
            return;

        lock (_stateLock)
        {
            _dirty = true;
        }

        SignalWorker();
        PreviewChanged?.Invoke();
    }

    private void SignalWorker()
    {
        if (_wakeSignal.CurrentCount == 0)
        {
            try { _wakeSignal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Builds a live UI preview snapshot using the exact same payload builder sent to Discord IPC.
    /// When no track is active and idle status is disabled, renders a realistic sample track so
    /// every settings toggle is immediately visible in the preview card.
    /// </summary>
    public DiscordRpcPreviewSnapshot GetLivePreview()
    {
        var input = CaptureInputSnapshot();
        bool hasTrack = !string.IsNullOrEmpty(input.TrackId) && !string.IsNullOrWhiteSpace(input.TrackTitle);
        bool isDemo = false;

        if (!hasTrack && !input.ShowIdleStatus)
        {
            isDemo = true;
            input = input with
            {
                TrackId = "yt_dQw4w9WgXcQ",
                RawVideoId = "dQw4w9WgXcQ",
                TrackTitle = "Rory in early 20s - 拷問の時間",
                TrackAuthor = "Rory in early 20s",
                ThumbnailUrl = null,
                IsLiked = true,
                RepeatMode = RepeatMode.One,
                ShuffleEnabled = true,
                QueueIndex = 0,
                QueueCount = 31,
                Position = TimeSpan.FromSeconds(43),
                Duration = TimeSpan.FromSeconds(118),
                PlaybackState = PlaybackState.Playing,
                PlaylistName = "Late Night Mix",
                PublicPlaylistUrl = "https://www.youtube.com/playlist?list=PLdemo",
                PublicPlaylistThumbnailUrl = null
            };
        }

        var activity = BuildActivity(input with { Enabled = true });
        if (activity == null)
        {
            return new DiscordRpcPreviewSnapshot(
                IsVisibleInDiscord: false,
                IsDemoTrack: isDemo,
                IsPaused: input.PlaybackState == PlaybackState.Paused,
                ActivityType: input.ActivityType,
                Details: null,
                State: null,
                LargeText: null,
                CoverUrl: null,
                SmallImageUrl: null,
                SmallImageTooltip: null,
                ShowProgressBar: false,
                ShowElapsedOnly: false,
                ProgressFraction: 0,
                ElapsedText: "00:00",
                DurationText: "00:00",
                Button1Label: null,
                Button2Label: null);
        }

        bool isPlaying = input.PlaybackState == PlaybackState.Playing;
        bool isPaused = input.PlaybackState == PlaybackState.Paused;
        bool isActiveTrack = hasTrack || isDemo;
        bool hasDuration = isActiveTrack && input.Duration > TimeSpan.FromSeconds(1);
        bool showBar = isActiveTrack && (isPlaying || isPaused)
            && input.TimestampMode == DiscordRpcTimestampMode.ProgressBar
            && hasDuration;
        bool showElapsed = isActiveTrack && isPlaying && input.TimestampMode == DiscordRpcTimestampMode.ElapsedOnly;

        TimeSpan pos = input.Position < TimeSpan.Zero ? TimeSpan.Zero : input.Position;
        if (hasDuration && pos > input.Duration)
            pos = input.Duration;

        double fraction = showBar ? Math.Clamp(pos.TotalSeconds / input.Duration.TotalSeconds, 0.0, 1.0) : 0.0;

        string? btn1 = activity.Buttons is { Count: >= 1 } ? activity.Buttons[0].Label : null;
        string? btn2 = activity.Buttons is { Count: >= 2 } ? activity.Buttons[1].Label : null;

        return new DiscordRpcPreviewSnapshot(
            IsVisibleInDiscord: true,
            IsDemoTrack: isDemo,
            IsPaused: isPaused,
            ActivityType: input.ActivityType,
            Details: activity.Details,
            State: activity.State,
            LargeText: activity.Assets?.LargeText,
            CoverUrl: activity.Assets?.LargeImage,
            SmallImageUrl: activity.Assets?.SmallImage,
            SmallImageTooltip: activity.Assets?.SmallText,
            ShowProgressBar: showBar,
            ShowElapsedOnly: showElapsed,
            ProgressFraction: fraction,
            ElapsedText: FormatTime(pos),
            DurationText: FormatTime(input.Duration),
            Button1Label: btn1,
            Button2Label: btn2);
    }

    internal static string FormatTime(TimeSpan time) => DurationFormatter.FormatZeroPadded(time);

    private void UpdateStatus(
        DiscordRpcConnectionState state,
        string? userName = null,
        string? error = null)
    {
        bool changed;
        lock (_stateLock)
        {
            changed = _connectionState != state
                || !string.Equals(_connectedUserName, userName, StringComparison.Ordinal)
                || !string.Equals(_lastError, error, StringComparison.Ordinal);

            if (!changed)
                return;

            _connectionState = state;
            _connectedUserName = userName;
            _lastError = error;
        }

        StatusChanged?.Invoke();
    }

    private static bool IsPublicCloudPlaylist(Playlist playlist) =>
        playlist.Visibility == PlaylistVisibility.Public
        && !string.IsNullOrEmpty(playlist.YoutubeId)
        && !playlist.IsCloudUnavailable;

    /// <summary>
    /// Verifies that an external image URL is anonymously reachable by Discord's media proxy.
    /// Uses <see cref="HttpMethod.Get"/> with <see cref="HttpCompletionOption.ResponseHeadersRead"/>
    /// (headers only, zero body read) because some YouTube thumbnail endpoints reject <c>HEAD</c>.
    /// </summary>
    private static async ValueTask<bool> IsExternalImagePubliclyReachableAsync(string url, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ImageProbeTimeoutMs);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await SharedHttpClient.Instance
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Log.Debug($"[DiscordRPC] External cover unreachable anonymously (HTTP {(int)response.StatusCode}): {url}");
                return false;
            }

            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.IsNullOrEmpty(mediaType)
                && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                Log.Debug($"[DiscordRPC] External cover returned non-image content-type '{mediaType}': {url}");
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug($"[DiscordRPC] External cover probe failed ({ex.Message}): {url}");
            return false;
        }
    }

    private async ValueTask<CachedPlaylistRpcMeta> ResolveVerifiedPlaylistMetaAsync(
        Playlist playlist,
        bool verifyThumbnail,
        CancellationToken ct)
    {
        bool isPublicCloud = IsPublicCloudPlaylist(playlist);
        string? verifiedThumb = null;
        bool isVerified = !isPublicCloud;

        if (isPublicCloud && verifyThumbnail)
        {
            string? candidate = IsValidExternalImageUrl(playlist.ThumbnailUrl)
                ? playlist.ThumbnailUrl!.Trim()
                : null;

            bool cacheHit;
            lock (_stateLock)
            {
                cacheHit = _verifiedThumbCache.TryGetValue(playlist.Id, out var cached)
                    && string.Equals(cached.SourceUrl, candidate, StringComparison.Ordinal);
                if (cacheHit)
                    verifiedThumb = cached.VerifiedUrl;
            }

            if (cacheHit)
            {
                isVerified = true;
            }
            else
            {
                string? persistedSource = candidate;

                if (candidate != null
                    && await IsExternalImagePubliclyReachableAsync(candidate, ct).ConfigureAwait(false))
                {
                    verifiedThumb = candidate;
                }
                else if (_youtube != null && !string.IsNullOrEmpty(playlist.YoutubeId))
                {
                    // One-shot refresh per session for expired YouTube pl_c studio_square_thumbnail URLs.
                    bool shouldRefresh;
                    lock (_stateLock)
                    {
                        shouldRefresh = _refreshedPlaylistThumbIds.Add(playlist.Id);
                    }

                    if (shouldRefresh)
                    {
                        try
                        {
                            var fresh = await _youtube.Value.GetClient().Playlists
                                .GetAsync(playlist.YoutubeId, ct)
                                .ConfigureAwait(false);

                            if (IsValidExternalImageUrl(fresh.ThumbnailUrl))
                            {
                                string refreshedUrl = fresh.ThumbnailUrl!.Trim();
                                if (await IsExternalImagePubliclyReachableAsync(refreshedUrl, ct).ConfigureAwait(false))
                                {
                                    verifiedThumb = refreshedUrl;
                                    persistedSource = refreshedUrl;
                                    if (_playlistService != null)
                                    {
                                        await _playlistService
                                            .UpdateThumbnailUrlSilentlyAsync(playlist.Id, refreshedUrl, ct)
                                            .ConfigureAwait(false);
                                    }

                                    Log.Info($"[DiscordRPC] Refreshed expired thumbnail for public playlist '{playlist.Name}'.");
                                }
                            }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            Log.Debug($"[DiscordRPC] Playlist thumbnail refresh skipped for '{playlist.YoutubeId}': {ex.Message}");
                        }
                    }
                }

                isVerified = true;
                lock (_stateLock)
                {
                    _verifiedThumbCache[playlist.Id] = (persistedSource, verifiedThumb);
                }
            }
        }

        return new CachedPlaylistRpcMeta(
            PlaylistId: playlist.Id,
            IsPublicCloud: isPublicCloud,
            PublicUrl: isPublicCloud ? playlist.Url : null,
            ThumbnailUrl: verifiedThumb,
            DisplayName: playlist.Name,
            IsThumbnailVerified: isVerified);
    }

    private static bool IsIdleState(in DiscordPresenceInput input) =>
        string.IsNullOrEmpty(input.TrackId)
        || string.IsNullOrWhiteSpace(input.TrackTitle)
        || input.PlaybackState is PlaybackState.Stopped or PlaybackState.Error;

    private async ValueTask<bool> EnsurePlaylistMetadataLoadedAsync(DiscordPresenceInput input, CancellationToken ct)
    {
        if (_playlistService == null)
            return false;

        bool updated = false;
        bool isIdle = IsIdleState(input);

        // 1. Active playing playlist (only query DB/HTTP when settings actually use playlist metadata)
        string? activeId = _playerControl.ActivePlaylistId;
        if (string.IsNullOrEmpty(activeId) || LibraryService.IsSystemPlaylist(activeId))
        {
            lock (_stateLock)
                _cachedActivePlaylist = null;
        }
        else if (!isIdle && (input.ShowPlaylistName
            || input.ShowPublicPlaylistLink
            || input.BadgeStyle == DiscordRpcBadgeStyle.PublicPlaylistCover))
        {
            bool needActiveThumb = input.BadgeStyle == DiscordRpcBadgeStyle.PublicPlaylistCover;
            bool needsLoad;
            lock (_stateLock)
            {
                needsLoad = _cachedActivePlaylist is not { } existing
                    || !string.Equals(existing.PlaylistId, activeId, StringComparison.Ordinal)
                    || (needActiveThumb && !existing.IsThumbnailVerified);
            }

            if (needsLoad)
            {
                try
                {
                    var playlist = await _playlistService.GetPlaylistAsync(activeId, ct).ConfigureAwait(false);
                    var meta = playlist != null
                        ? await ResolveVerifiedPlaylistMetaAsync(playlist, needActiveThumb, ct).ConfigureAwait(false)
                        : new CachedPlaylistRpcMeta(activeId, false, null, null, _playerControl.ActivePlaylistName, true);

                    lock (_stateLock)
                    {
                        if (string.Equals(_playerControl.ActivePlaylistId, activeId, StringComparison.Ordinal))
                        {
                            _cachedActivePlaylist = meta;
                            updated = true;
                        }
                    }

                    if (updated)
                        PreviewChanged?.Invoke();
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Log.Debug($"[DiscordRPC] Failed to resolve active playlist metadata '{activeId}': {ex.Message}");
                }
            }
        }

        // 2. Currently viewed playlist (only when Idle + ShowIdleStatus)
        if (input.ShowIdleStatus && isIdle)
        {
            string? viewingId;
            bool needViewingThumb = input.ShowCoverArt;
            bool viewingNeedsVerify;
            lock (_stateLock)
            {
                viewingId = _currentPage == DiscordNavigationPage.Playlist ? _viewingPlaylistId : null;
                viewingNeedsVerify = !string.IsNullOrEmpty(viewingId)
                    && !LibraryService.IsSystemPlaylist(viewingId)
                    && (_cachedViewingPlaylist is not { } v
                        || !string.Equals(v.PlaylistId, viewingId, StringComparison.Ordinal)
                        || (needViewingThumb && !v.IsThumbnailVerified));
            }

            if (viewingNeedsVerify && viewingId != null)
            {
                try
                {
                    var playlist = await _playlistService.GetPlaylistAsync(viewingId, ct).ConfigureAwait(false);
                    if (playlist != null)
                    {
                        var meta = await ResolveVerifiedPlaylistMetaAsync(playlist, needViewingThumb, ct).ConfigureAwait(false);
                        bool applied = false;
                        lock (_stateLock)
                        {
                            if (string.Equals(_viewingPlaylistId, viewingId, StringComparison.Ordinal))
                            {
                                _cachedViewingPlaylist = meta;
                                applied = true;
                                updated = true;
                            }
                        }

                        if (applied)
                            PreviewChanged?.Invoke();
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Log.Debug($"[DiscordRPC] Failed to resolve viewed playlist metadata '{viewingId}': {ex.Message}");
                }
            }
        }

        return updated;
    }

    private (string? Handle, string? ChannelUrl, string? AvatarUrl) ResolveListenerChannelInfo()
    {
        var auth = _authService;
        if (auth == null || !auth.IsAuthenticated)
            return (null, null, null);

        var state = auth.State;
        if (state == null)
            return (null, null, null);

        YoutubeAccountItem? activeAccount = null;
        var accounts = state.CachedAccounts;
        if (accounts != null)
        {
            for (int i = 0; i < accounts.Count; i++)
            {
                var item = accounts[i];
                if (!string.IsNullOrEmpty(state.ActiveGaiaId)
                    && string.Equals(item.GaiaId, state.ActiveGaiaId, StringComparison.Ordinal))
                {
                    activeAccount = item;
                    break;
                }

                if (item.IsSelected)
                    activeAccount ??= item;
            }
        }

        string? rawHandle = activeAccount?.Handle?.Trim();
        string? normalizedHandle = null;
        string? channelUrl = null;
        if (!string.IsNullOrEmpty(rawHandle))
        {
            normalizedHandle = rawHandle.StartsWith('@') ? rawHandle : "@" + rawHandle;
            if (!normalizedHandle.Contains(' ') && normalizedHandle.Length <= 64)
                channelUrl = $"https://www.youtube.com/{normalizedHandle}";
        }

        string? avatar = activeAccount?.AvatarUrl;
        if (!IsValidExternalImageUrl(avatar))
            avatar = state.AvatarUrl;

        string? validAvatar = IsValidExternalImageUrl(avatar) ? avatar!.Trim() : null;
        return (normalizedHandle, channelUrl, validAvatar);
    }

    private DiscordPresenceInput CaptureInputSnapshot()
    {
        var appSettings = _library.Settings;
        var rpc = appSettings.DiscordRpc ?? new DiscordRpcSettings();
        var track = _playerControl.CurrentTrack;

        string? rawVideoId = null;
        if (track != null)
        {
            var rawSpan = track.GetRawIdSpan();
            if (rawSpan.Length == 11)
                rawVideoId = rawSpan.ToString();
        }

        string? activePlaylistId = _playerControl.ActivePlaylistId;
        CachedPlaylistRpcMeta? playlistMeta = null;
        DiscordNavigationPage currentPage;
        CachedPlaylistRpcMeta? viewingMeta;
        DateTimeOffset idleSince;

        lock (_stateLock)
        {
            if (!string.IsNullOrEmpty(activePlaylistId)
                && _cachedActivePlaylist is { } cached
                && string.Equals(cached.PlaylistId, activePlaylistId, StringComparison.Ordinal))
            {
                playlistMeta = cached;
            }

            currentPage = _currentPage;
            viewingMeta = _currentPage == DiscordNavigationPage.Playlist ? _cachedViewingPlaylist : null;
            idleSince = _idleSinceUtc;
        }

        var (listenerHandle, listenerChannelUrl, listenerAvatarUrl) = ResolveListenerChannelInfo();

        return new DiscordPresenceInput(
            Enabled: appSettings.DiscordRpcEnabled,
            ClientId: ResolveEffectiveClientId(rpc),
            ActivityType: rpc.ActivityType,
            StatusDisplay: rpc.StatusDisplay,
            TimestampMode: rpc.TimestampMode,
            DetailsFormat: rpc.DetailsFormat,
            PausedBehavior: rpc.PausedBehavior,
            ShowCoverArt: rpc.ShowCoverArt,
            ShowTrackButton: rpc.ShowTrackButton,
            ShowGitHubButton: rpc.ShowGitHubButton,
            ShowPlaylistName: rpc.ShowPlaylistName,
            ShowPublicPlaylistLink: rpc.ShowPublicPlaylistLink,
            ShowLikeStatus: rpc.ShowLikeStatus,
            ShowRepeatMode: rpc.ShowRepeatMode,
            ShowShuffleMode: rpc.ShowShuffleMode,
            ShowQueuePosition: rpc.ShowQueuePosition,
            ShowListenerChannel: rpc.ShowListenerChannel,
            ShowIdleStatus: rpc.ShowIdleStatus,
            StripAuthorFromTitle: rpc.StripAuthorFromTitle,
            BadgeStyle: rpc.BadgeStyle,
            CustomBadgeUrl: rpc.CustomBadgeUrl,
            TrackId: track?.Id,
            RawVideoId: rawVideoId,
            TrackTitle: track?.Title,
            TrackAuthor: track?.Author,
            ChannelId: track?.ChannelId,
            ThumbnailUrl: track?.ThumbnailUrl,
            IsLiked: track?.IsLiked == true,
            RepeatMode: _playerControl.RepeatMode,
            ShuffleEnabled: _playerControl.ShuffleEnabled,
            QueueIndex: _playerControl.CurrentQueueIndex,
            QueueCount: _playerControl.QueueCount,
            Position: _playerControl.CurrentPosition,
            Duration: _playerControl.TotalDuration,
            PlaybackState: _playerControl.PlaybackState,
            PlaylistName: playlistMeta?.DisplayName ?? _playerControl.ActivePlaylistName,
            PublicPlaylistUrl: playlistMeta is { IsPublicCloud: true } ? playlistMeta.Value.PublicUrl : null,
            PublicPlaylistThumbnailUrl: playlistMeta is { IsPublicCloud: true } ? playlistMeta.Value.ThumbnailUrl : null,
            ListenerHandle: listenerHandle,
            ListenerChannelUrl: listenerChannelUrl,
            ListenerAvatarUrl: listenerAvatarUrl,
            CurrentPage: currentPage,
            ViewingPlaylistName: viewingMeta?.DisplayName,
            ViewingPlaylistUrl: viewingMeta is { IsPublicCloud: true } ? viewingMeta.Value.PublicUrl : null,
            ViewingPlaylistThumbnailUrl: viewingMeta is { IsPublicCloud: true } ? viewingMeta.Value.ThumbnailUrl : null,
            IdleSinceUtc: idleSince,
            CapturedAtUtc: DateTimeOffset.UtcNow);
    }

    private bool IsStopRequested()
    {
        lock (_stateLock) return _stopRequested;
    }

    private async ValueTask<bool> WaitDelayOrStopAsync(int delayMs, CancellationToken ct)
    {
        long deadline = Environment.TickCount64 + delayMs;
        while (!ct.IsCancellationRequested)
        {
            if (IsStopRequested())
                return false;

            int remaining = (int)(deadline - Environment.TickCount64);
            if (remaining <= 0)
                return true;

            await WaitForWakeAsync(remaining, ct).ConfigureAwait(false);
        }

        return false;
    }

    private async Task RunWorkerLoopAsync()
    {
        var ct = _disposeCts.Token;
        string activeClientId = string.Empty;
        long lastSentTick = 0;
        long nextReconnectTick = 0;
        int reconnectDelayMs = InitialReconnectDelayMs;
        bool hasPublishedActivity = false;
        DiscordActivity? lastSentActivity = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                bool stopRequested;
                bool dirty;
                bool forceReconnect;
                bool forcePublish;

                lock (_stateLock)
                {
                    stopRequested = _stopRequested;
                    dirty = _dirty;
                    forceReconnect = _forceReconnect;
                    forcePublish = _forcePublish;
                    _dirty = false;
                    _forceReconnect = false;
                    _forcePublish = false;
                }

                if (stopRequested)
                    break;

                var input = CaptureInputSnapshot();

                // 1. Disabled in settings: clear presence immediately & close IPC channel.
                if (!input.Enabled)
                {
                    if (_ipc.IsConnected)
                    {
                        if (hasPublishedActivity)
                        {
                            await _ipc.SetActivityAsync(null, ct).ConfigureAwait(false);
                            hasPublishedActivity = false;
                        }

                        await _ipc.DisconnectAsync(sendCloseFrame: true, ct).ConfigureAwait(false);
                    }

                    activeClientId = string.Empty;
                    lastSentActivity = null;
                    reconnectDelayMs = InitialReconnectDelayMs;
                    nextReconnectTick = 0;
                    UpdateStatus(DiscordRpcConnectionState.Disabled);

                    await WaitForWakeAsync(Timeout.Infinite, ct).ConfigureAwait(false);
                    continue;
                }

                // 2. Client ID missing or invalid.
                if (!IsValidClientId(input.ClientId))
                {
                    if (_ipc.IsConnected)
                    {
                        if (hasPublishedActivity)
                        {
                            await _ipc.SetActivityAsync(null, ct).ConfigureAwait(false);
                            hasPublishedActivity = false;
                        }

                        await _ipc.DisconnectAsync(sendCloseFrame: true, ct).ConfigureAwait(false);
                    }

                    activeClientId = string.Empty;
                    lastSentActivity = null;
                    UpdateStatus(DiscordRpcConnectionState.MissingClientId);

                    await WaitForWakeAsync(Timeout.Infinite, ct).ConfigureAwait(false);
                    continue;
                }

                // 3. Reconnect if Client ID changed or manual reconnect requested.
                if (forceReconnect
                    || (_ipc.IsConnected && !string.Equals(activeClientId, input.ClientId, StringComparison.Ordinal)))
                {
                    if (_ipc.IsConnected)
                    {
                        if (hasPublishedActivity)
                        {
                            await _ipc.SetActivityAsync(null, ct).ConfigureAwait(false);
                            hasPublishedActivity = false;
                        }

                        await _ipc.DisconnectAsync(sendCloseFrame: true, ct).ConfigureAwait(false);
                    }

                    activeClientId = string.Empty;
                    lastSentActivity = null;
                    reconnectDelayMs = InitialReconnectDelayMs;
                    nextReconnectTick = 0;
                }

                // 4. Connect when disconnected (with zero-exception backoff).
                if (!_ipc.IsConnected)
                {
                    long now = Environment.TickCount64;
                    if (nextReconnectTick > now)
                    {
                        int waitMs = (int)Math.Min(nextReconnectTick - now, MaxReconnectDelayMs);
                        await WaitForWakeAsync(waitMs, ct).ConfigureAwait(false);
                        continue;
                    }

                    UpdateStatus(DiscordRpcConnectionState.Connecting);

                    var handshake = await _ipc.ConnectAsync(input.ClientId, ct).ConfigureAwait(false);
                    if (handshake.Success)
                    {
                        activeClientId = input.ClientId;
                        reconnectDelayMs = InitialReconnectDelayMs;
                        nextReconnectTick = 0;
                        lastSentTick = 0;
                        hasPublishedActivity = false;
                        lastSentActivity = null;
                        UpdateStatus(DiscordRpcConnectionState.Connected, handshake.UserDisplayName, null);
                        Log.Info($"[DiscordRPC] Connected to Discord IPC (user='{handshake.UserDisplayName ?? "unknown"}').");
                        dirty = true;
                        forcePublish = true;
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(handshake.ErrorMessage))
                        {
                            UpdateStatus(DiscordRpcConnectionState.WaitingForDiscord);
                        }
                        else
                        {
                            UpdateStatus(DiscordRpcConnectionState.Error, null, handshake.ErrorMessage);
                            Log.Warn($"[DiscordRPC] Handshake failed: {handshake.ErrorMessage}");
                        }

                        nextReconnectTick = Environment.TickCount64 + reconnectDelayMs;
                        reconnectDelayMs = Math.Min(reconnectDelayMs * 2, MaxReconnectDelayMs);
                        continue;
                    }
                }

                // 5. Publish dirty state with interruptible debounce + rate-limit throttle + dedup.
                if (dirty)
                {
                    if (!await WaitDelayOrStopAsync(DebounceMs, ct).ConfigureAwait(false))
                        break;

                    long elapsedSinceLast = Environment.TickCount64 - lastSentTick;
                    if (lastSentTick > 0 && elapsedSinceLast < MinUpdateIntervalMs)
                    {
                        int throttleWaitMs = (int)(MinUpdateIntervalMs - elapsedSinceLast);
                        if (!await WaitDelayOrStopAsync(throttleWaitMs, ct).ConfigureAwait(false))
                            break;
                    }

                    lock (_stateLock)
                    {
                        if (_stopRequested)
                            break;
                        forcePublish |= _forcePublish;
                        _dirty = false;
                        _forcePublish = false;
                    }

                    var freshInput = CaptureInputSnapshot();

                    // Wait up to 800ms while a track is in Loading/Buffering so we publish one clean Playing frame with progress bar.
                    if (freshInput.Enabled
                        && freshInput.PlaybackState is PlaybackState.Loading or PlaybackState.Buffering
                        && !string.IsNullOrEmpty(freshInput.TrackId))
                    {
                        long loadingDeadline = Environment.TickCount64 + LoadingGraceTimeoutMs;
                        while (!ct.IsCancellationRequested && !IsStopRequested())
                        {
                            int remaining = (int)(loadingDeadline - Environment.TickCount64);
                            if (remaining <= 0)
                                break;

                            await WaitForWakeAsync(remaining, ct).ConfigureAwait(false);
                            lock (_stateLock)
                            {
                                if (_stopRequested)
                                    break;
                                forcePublish |= _forcePublish;
                                _dirty = false;
                                _forcePublish = false;
                            }

                            freshInput = CaptureInputSnapshot();
                            if (freshInput.PlaybackState is not (PlaybackState.Loading or PlaybackState.Buffering))
                                break;
                        }

                        if (IsStopRequested())
                            break;
                    }

                    bool metadataUpdated = await EnsurePlaylistMetadataLoadedAsync(freshInput, ct).ConfigureAwait(false);
                    bool dirtiedDuringAwait;
                    lock (_stateLock)
                    {
                        dirtiedDuringAwait = _dirty;
                        if (!dirtiedDuringAwait)
                        {
                            forcePublish |= _forcePublish;
                            _forcePublish = false;
                        }
                    }

                    if (dirtiedDuringAwait)
                        continue;

                    if (metadataUpdated)
                        freshInput = CaptureInputSnapshot();

                    if (!freshInput.Enabled || !string.Equals(freshInput.ClientId, activeClientId, StringComparison.Ordinal))
                    {
                        lock (_stateLock) _dirty = true;
                        continue;
                    }

                    var activity = BuildActivity(freshInput);
                    bool shouldSend = activity == null
                        ? hasPublishedActivity
                        : (forcePublish
                            || lastSentActivity == null
                            || !AreActivitiesEquivalent(lastSentActivity, activity));

                    if (shouldSend)
                    {
                        bool sent = await _ipc.SetActivityAsync(activity, ct).ConfigureAwait(false);
                        if (sent)
                        {
                            lastSentTick = Environment.TickCount64;
                            hasPublishedActivity = activity != null;
                            lastSentActivity = activity;
                        }
                        else
                        {
                            hasPublishedActivity = false;
                            lastSentActivity = null;
                            UpdateStatus(DiscordRpcConnectionState.WaitingForDiscord);
                            nextReconnectTick = Environment.TickCount64 + reconnectDelayMs;
                            reconnectDelayMs = Math.Min(reconnectDelayMs * 2, MaxReconnectDelayMs);
                            continue;
                        }
                    }
                }

                // 6. Sleep until next player/settings event or keep-alive ping interval.
                bool wokenBySignal = await WaitForWakeAsync(KeepAliveIntervalMs, ct).ConfigureAwait(false);
                if (!wokenBySignal && _ipc.IsConnected)
                {
                    bool alive = await _ipc.PingAsync(ct).ConfigureAwait(false);
                    if (!alive)
                    {
                        Log.Debug("[DiscordRPC] Keep-alive ping failed; waiting for Discord reconnect.");
                        hasPublishedActivity = false;
                        lastSentActivity = null;
                        UpdateStatus(DiscordRpcConnectionState.WaitingForDiscord);
                        nextReconnectTick = Environment.TickCount64 + reconnectDelayMs;
                        reconnectDelayMs = Math.Min(reconnectDelayMs * 2, MaxReconnectDelayMs);
                    }
                }
            }

            // Graceful exit path: clear presence and close pipe cleanly.
            if (_ipc.IsConnected)
            {
                using var shutdownCts = new CancellationTokenSource(ShutdownGraceTimeoutMs);
                if (hasPublishedActivity)
                    await _ipc.SetActivityAsync(null, shutdownCts.Token).ConfigureAwait(false);

                await _ipc.DisconnectAsync(sendCloseFrame: true, shutdownCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"[DiscordRPC] Worker loop exited with error: {ex.Message}");
        }
        finally
        {
            await _ipc.DisconnectAsync(sendCloseFrame: false, CancellationToken.None).ConfigureAwait(false);
            UpdateStatus(DiscordRpcConnectionState.Disabled);
        }
    }

    private async Task<bool> WaitForWakeAsync(int timeoutMs, CancellationToken ct)
    {
        try
        {
            if (timeoutMs == Timeout.Infinite)
            {
                await _wakeSignal.WaitAsync(ct).ConfigureAwait(false);
                return true;
            }

            return await _wakeSignal.WaitAsync(timeoutMs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    #region Activity Payload Builder

    /// <summary>
    /// Builds a Discord Rich Presence <see cref="DiscordActivity"/> from the current playback snapshot,
    /// or returns <see langword="null"/> when presence should be cleared.
    /// </summary>
    internal static DiscordActivity? BuildActivity(in DiscordPresenceInput input)
    {
        if (!input.Enabled)
            return null;

        bool hasTrack = !string.IsNullOrEmpty(input.TrackId) && !string.IsNullOrWhiteSpace(input.TrackTitle);
        if (!hasTrack)
        {
            if (!input.ShowIdleStatus)
                return null;

            return BuildIdleActivity(input);
        }

        bool isPaused = input.PlaybackState == PlaybackState.Paused;
        bool isStopped = input.PlaybackState is PlaybackState.Stopped or PlaybackState.Error;
        bool isPlaying = input.PlaybackState == PlaybackState.Playing;

        if (isStopped)
            return input.ShowIdleStatus ? BuildIdleActivity(input) : null;

        if (isPaused && input.PausedBehavior == DiscordRpcPausedBehavior.ClearPresence)
            return null;

        string author = string.IsNullOrWhiteSpace(input.TrackAuthor)
            ? LocalizationService.Instance["Track_UnknownAuthor"]
            : CleanYouTubeTopicAuthor(input.TrackAuthor.Trim());

        string rawTitle = input.TrackTitle!.Trim();
        string title = input.StripAuthorFromTitle
            ? StripDuplicateAuthorPrefix(rawTitle, author)
            : rawTitle;

        string? detailsRaw = input.DetailsFormat switch
        {
            DiscordRpcDetailsFormat.TrackAndArtist => $"{title} — {author}",
            DiscordRpcDetailsFormat.ArtistAndTrack => $"{author} — {title}",
            _ => title
        };

        // Keep line 2 clean; when paused include a crisp monochrome pause stamp (❚❚ mm:ss / mm:ss).
        bool playlistShownInState = false;
        string? stateRaw;
        if (input.DetailsFormat == DiscordRpcDetailsFormat.TrackTitle)
        {
            stateRaw = author;
        }
        else if (input.ShowPlaylistName && !string.IsNullOrWhiteSpace(input.PlaylistName))
        {
            stateRaw = input.PlaylistName.Trim();
            playlistShownInState = true;
        }
        else
        {
            stateRaw = isPaused
                ? FormatPausedStamp(input)
                : LocalizationService.Instance["Common_AppName"];
        }

        if (isPaused && !stateRaw.StartsWith(PauseGlyph, StringComparison.Ordinal))
        {
            stateRaw = $"{stateRaw} · {FormatPausedStamp(input)}";
        }

        string? details = NormalizeDiscordText(detailsRaw, MaxUtf8FieldBytes);
        string? state = NormalizeDiscordText(stateRaw, MaxUtf8FieldBytes);

        string? trackUrl = !string.IsNullOrEmpty(input.RawVideoId)
            ? $"https://www.youtube.com/watch?v={input.RawVideoId}"
            : null;

        string? channelUrl = !string.IsNullOrWhiteSpace(input.ChannelId)
            ? $"https://www.youtube.com/channel/{input.ChannelId.Trim()}"
            : null;

        string? stateUrl = input.DetailsFormat == DiscordRpcDetailsFormat.TrackTitle
            ? channelUrl
            : (playlistShownInState && input.ShowPublicPlaylistLink ? input.PublicPlaylistUrl : null);

        DiscordActivityTimestamps? timestamps = null;
        if (isPlaying && input.TimestampMode != DiscordRpcTimestampMode.None)
        {
            long nowSec = input.CapturedAtUtc.ToUnixTimeSeconds();
            long posSec = Math.Max(0L, (long)input.Position.TotalSeconds);
            long startSec = nowSec - posSec;

            if (input.TimestampMode == DiscordRpcTimestampMode.ProgressBar
                && input.Duration > TimeSpan.FromSeconds(1))
            {
                long durSec = (long)input.Duration.TotalSeconds;
                long clampedPos = Math.Min(posSec, durSec);
                startSec = nowSec - clampedPos;
                long endSec = startSec + durSec;
                timestamps = new DiscordActivityTimestamps
                {
                    Start = startSec,
                    End = endSec > startSec ? endSec : startSec + 1
                };
            }
            else
            {
                timestamps = new DiscordActivityTimestamps
                {
                    Start = startSec
                };
            }
        }

        DiscordActivityAssets? assets = BuildAssets(input, trackUrl, playlistShownInState);
        List<DiscordActivityButton>? buttons = BuildButtons(input, trackUrl);

        return new DiscordActivity
        {
            Type = (int)input.ActivityType,
            StatusDisplayType = (int)input.StatusDisplay,
            Details = details,
            DetailsUrl = trackUrl,
            State = state,
            StateUrl = stateUrl,
            Timestamps = timestamps,
            Assets = assets,
            Buttons = buttons,
            Instance = false
        };
    }

    private static string FormatPausedStamp(in DiscordPresenceInput input)
    {
        if (input.Duration > TimeSpan.FromSeconds(1))
        {
            TimeSpan clamped = input.Position < TimeSpan.Zero
                ? TimeSpan.Zero
                : input.Position > input.Duration ? input.Duration : input.Position;
            return $"{PauseGlyph} {FormatTime(clamped)} / {FormatTime(input.Duration)}";
        }

        return $"{PauseGlyph} {LocalizationService.Instance["DiscordRpc_State_Paused"]}";
    }

    /// <summary>
    /// Removes the YouTube auto-generated <c>" - Topic"</c> suffix from artist channel names.
    /// </summary>
    internal static string CleanYouTubeTopicAuthor(string author)
    {
        const string topicSuffix = " - Topic";
        if (author.Length > topicSuffix.Length
            && author.EndsWith(topicSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return author[..^topicSuffix.Length].TrimEnd();
        }

        return author;
    }

    /// <summary>
    /// Conservatively strips a leading <c>"{author} - "</c> (or en/em dash / colon) prefix from a track title
    /// only when the prefix matches <paramref name="author"/> exactly (case-insensitive) and leaves a non-trivial title.
    /// </summary>
    internal static string StripDuplicateAuthorPrefix(string title, string? author)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(author))
            return title;

        string cleanAuthor = CleanYouTubeTopicAuthor(author.Trim());
        if (cleanAuthor.Length < 2 || title.Length <= cleanAuthor.Length + 3)
            return title;

        if (!title.StartsWith(cleanAuthor, StringComparison.OrdinalIgnoreCase))
            return title;

        var remainder = title.AsSpan(cleanAuthor.Length);

        ReadOnlySpan<char> afterSep;
        if (remainder.StartsWith(" - ", StringComparison.Ordinal)
            || remainder.StartsWith(" — ", StringComparison.Ordinal)
            || remainder.StartsWith(" – ", StringComparison.Ordinal))
        {
            afterSep = remainder[3..].TrimStart();
        }
        else if (remainder.StartsWith(": ", StringComparison.Ordinal))
        {
            afterSep = remainder[2..].TrimStart();
        }
        else
        {
            return title;
        }

        return afterSep.Length >= 2 ? afterSep.ToString() : title;
    }

    private static DiscordActivity BuildIdleActivity(in DiscordPresenceInput input)
    {
        var L = LocalizationService.Instance;

        string detailsRaw;
        string stateRaw = L["Common_AppName"];
        DiscordActivityAssets? assets = null;
        string? detailsUrl = null;
        string? playlistButtonUrl = null;

        switch (input.CurrentPage)
        {
            case DiscordNavigationPage.Playlist when !string.IsNullOrWhiteSpace(input.ViewingPlaylistName):
                detailsRaw = L["DiscordRpc_Idle_Playlist"];
                stateRaw = input.ViewingPlaylistName.Trim();

                if (input.ShowCoverArt && IsValidExternalImageUrl(input.ViewingPlaylistThumbnailUrl))
                {
                    string? clickUrl = input.ShowPublicPlaylistLink ? input.ViewingPlaylistUrl : null;
                    assets = new DiscordActivityAssets
                    {
                        LargeImage = input.ViewingPlaylistThumbnailUrl!.Trim(),
                        LargeText = NormalizeDiscordText(stateRaw, MaxUtf8FieldBytes),
                        LargeUrl = clickUrl
                    };
                    detailsUrl = clickUrl;
                }

                if (input.ShowPublicPlaylistLink && !string.IsNullOrEmpty(input.ViewingPlaylistUrl))
                    playlistButtonUrl = input.ViewingPlaylistUrl;
                break;

            case DiscordNavigationPage.Home:
                detailsRaw = L["DiscordRpc_Idle_Home"];
                break;

            case DiscordNavigationPage.Search:
                detailsRaw = L["DiscordRpc_Idle_Search"];
                break;

            case DiscordNavigationPage.Library:
                detailsRaw = L["DiscordRpc_Idle_Library"];
                break;

            case DiscordNavigationPage.Queue:
                detailsRaw = L["DiscordRpc_Idle_Queue"];
                break;

            case DiscordNavigationPage.Settings:
                detailsRaw = L["DiscordRpc_Idle_Settings"];
                break;

            default:
                detailsRaw = L["DiscordRpc_State_Idle"];
                break;
        }

        List<DiscordActivityButton>? buttons = null;

        if (!string.IsNullOrEmpty(playlistButtonUrl))
            TryAddButton(ref buttons, L["DiscordRpc_Button_OpenPlaylist"], playlistButtonUrl);

        if (input.ShowListenerChannel && !string.IsNullOrEmpty(input.ListenerChannelUrl))
            TryAddButton(ref buttons, FormatListenerButtonLabel(input.ListenerHandle), input.ListenerChannelUrl);

        if (input.ShowGitHubButton)
            TryAddButton(ref buttons, L["DiscordRpc_Button_GitHub"], G.GitHubUrl);

        DiscordActivityTimestamps? timestamps = null;
        if (input.TimestampMode != DiscordRpcTimestampMode.None && input.IdleSinceUtc is { } idleSince)
        {
            timestamps = new DiscordActivityTimestamps
            {
                Start = idleSince.ToUnixTimeSeconds()
            };
        }

        return new DiscordActivity
        {
            Type = (int)input.ActivityType,
            StatusDisplayType = (int)DiscordRpcStatusDisplay.AppName,
            Details = NormalizeDiscordText(detailsRaw, MaxUtf8FieldBytes),
            DetailsUrl = detailsUrl,
            State = NormalizeDiscordText(stateRaw, MaxUtf8FieldBytes),
            Timestamps = timestamps,
            Assets = assets,
            Buttons = buttons,
            Instance = false
        };
    }

    /// <summary>
    /// Builds the compact monochrome LMP metadata line (<c>assets.large_text</c>):
    /// <c>"{playlist} · {index}/{total} · ♥︎ ↻1 ⇄"</c>, grouping status glyphs tightly without noisy dots.
    /// </summary>
    internal static string? BuildCompactMetaLine(in DiscordPresenceInput input, bool playlistAlreadyInState)
    {
        var sb = new StringBuilder(64);

        static void AppendSection(StringBuilder builder, string text)
        {
            if (builder.Length > 0)
                builder.Append(" · ");
            builder.Append(text);
        }

        // 1. Context: playlist name
        if (input.ShowPlaylistName
            && !playlistAlreadyInState
            && !string.IsNullOrWhiteSpace(input.PlaylistName))
        {
            AppendSection(sb, input.PlaylistName.Trim());
        }

        // 2. Position in queue
        if (input.ShowQueuePosition && input.QueueCount > 1 && input.QueueIndex >= 0)
        {
            int trackNum = Math.Clamp(input.QueueIndex + 1, 1, input.QueueCount);
            AppendSection(sb, $"{trackNum}/{input.QueueCount}");
        }

        // 3. Tight monochrome playback flags cluster (Like / Repeat / Shuffle)
        bool hasFlag = false;

        void AppendFlag(string glyph)
        {
            if (!hasFlag)
            {
                if (sb.Length > 0)
                    sb.Append(" · ");
                hasFlag = true;
            }
            else
            {
                sb.Append(' ');
            }

            sb.Append(glyph);
        }

        if (input.ShowLikeStatus)
            AppendFlag(input.IsLiked ? LikedGlyph : UnlikedGlyph);

        if (input.ShowRepeatMode && input.RepeatMode != RepeatMode.None)
            AppendFlag(input.RepeatMode == RepeatMode.One ? RepeatOneGlyph : RepeatAllGlyph);

        if (input.ShowShuffleMode && input.ShuffleEnabled)
            AppendFlag(ShuffleGlyph);

        return sb.Length > 0 ? NormalizeDiscordText(sb.ToString(), MaxUtf8FieldBytes) : null;
    }

    private static DiscordActivityAssets? BuildAssets(
        in DiscordPresenceInput input,
        string? trackUrl,
        bool playlistShownInState)
    {
        string? largeImage = null;
        if (input.ShowCoverArt)
        {
            if (IsValidExternalImageUrl(input.ThumbnailUrl))
                largeImage = input.ThumbnailUrl!.Trim();
            else if (!string.IsNullOrEmpty(input.RawVideoId))
                largeImage = $"https://i.ytimg.com/vi/{input.RawVideoId}/hqdefault.jpg";
        }

        string? largeText = BuildCompactMetaLine(input, playlistShownInState);

        var (smallImage, smallText, smallUrl) = ResolveSmallBadge(input);

        if (largeImage == null && largeText == null && smallImage == null)
            return null;

        return new DiscordActivityAssets
        {
            LargeImage = largeImage,
            LargeText = largeText,
            LargeUrl = largeImage != null ? trackUrl : null,
            SmallImage = smallImage,
            SmallText = smallText,
            SmallUrl = smallUrl
        };
    }

    private static (string? ImageUrl, string? Tooltip, string? ClickUrl) ResolveSmallBadge(
        in DiscordPresenceInput input)
    {
        switch (input.BadgeStyle)
        {
            case DiscordRpcBadgeStyle.PublicPlaylistCover:
                if (IsValidExternalImageUrl(input.PublicPlaylistThumbnailUrl))
                {
                    string? tooltip = !string.IsNullOrWhiteSpace(input.PlaylistName)
                        ? NormalizeDiscordText(input.PlaylistName.Trim(), MaxUtf8FieldBytes)
                        : null;
                    string? clickUrl = input.ShowPublicPlaylistLink ? input.PublicPlaylistUrl : null;
                    return (input.PublicPlaylistThumbnailUrl!.Trim(), tooltip, clickUrl);
                }
                return (null, null, null);

            case DiscordRpcBadgeStyle.ListenerAvatar:
                if (IsValidExternalImageUrl(input.ListenerAvatarUrl))
                {
                    string? tooltip = !string.IsNullOrWhiteSpace(input.ListenerHandle)
                        ? NormalizeDiscordText(input.ListenerHandle, MaxUtf8FieldBytes)
                        : null;
                    string? clickUrl = input.ShowListenerChannel ? input.ListenerChannelUrl : null;
                    return (input.ListenerAvatarUrl!.Trim(), tooltip, clickUrl);
                }
                return (null, null, null);

            case DiscordRpcBadgeStyle.Custom:
                if (IsValidExternalImageUrl(input.CustomBadgeUrl))
                {
                    return (
                        input.CustomBadgeUrl!.Trim(),
                        NormalizeDiscordText(LocalizationService.Instance["Common_AppName"], MaxUtf8FieldBytes),
                        G.GitHubUrl);
                }
                return (null, null, null);

            default:
                return (null, null, null);
        }
    }

    private static void TryAddButton(ref List<DiscordActivityButton>? list, string label, string url)
    {
        if (list is { Count: >= 2 })
            return;

        string? normalized = NormalizeDiscordText(label, MaxButtonLabelBytes);
        if (string.IsNullOrEmpty(normalized))
            return;

        list ??= new List<DiscordActivityButton>(2);
        list.Add(new DiscordActivityButton { Label = normalized, Url = url });
    }

    private static List<DiscordActivityButton>? BuildButtons(
        in DiscordPresenceInput input,
        string? trackUrl)
    {
        List<DiscordActivityButton>? buttons = null;

        if (input.ShowTrackButton && !string.IsNullOrEmpty(trackUrl))
        {
            TryAddButton(ref buttons, LocalizationService.Instance["DiscordRpc_Button_ListenYouTube"], trackUrl);
        }

        if (input.ShowPublicPlaylistLink && !string.IsNullOrEmpty(input.PublicPlaylistUrl))
        {
            TryAddButton(ref buttons, LocalizationService.Instance["DiscordRpc_Button_OpenPlaylist"], input.PublicPlaylistUrl);
        }

        if (input.ShowListenerChannel && !string.IsNullOrEmpty(input.ListenerChannelUrl))
        {
            TryAddButton(ref buttons, FormatListenerButtonLabel(input.ListenerHandle), input.ListenerChannelUrl);
        }

        if (input.ShowGitHubButton)
        {
            TryAddButton(ref buttons, LocalizationService.Instance["DiscordRpc_Button_GitHub"], G.GitHubUrl);
        }

        return buttons;
    }

    private static string FormatListenerButtonLabel(string? handle) =>
        !string.IsNullOrWhiteSpace(handle)
            ? $"YouTube · {handle}"
            : LocalizationService.Instance["DiscordRpc_Button_ListenerChannel"];

    /// <summary>
    /// Normalizes a Rich Presence string to comply with Discord's UTF-8 byte limits (2..maxBytes).
    /// Truncates safely on Unicode scalar boundaries without breaking surrogate pairs.
    /// </summary>
    internal static string? NormalizeDiscordText(string? value, int maxUtf8Bytes)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string trimmed = value.Trim();
        int byteCount = Encoding.UTF8.GetByteCount(trimmed);

        if (byteCount < 2)
            return string.Concat(trimmed, "\u200B");

        if (byteCount <= maxUtf8Bytes)
            return trimmed;

        const string ellipsis = "…";
        int ellipsisBytes = Encoding.UTF8.GetByteCount(ellipsis);
        int budget = Math.Max(1, maxUtf8Bytes - ellipsisBytes);

        int accumulatedBytes = 0;
        int charIndex = 0;

        foreach (var rune in trimmed.EnumerateRunes())
        {
            int runeBytes = rune.Utf8SequenceLength;
            if (accumulatedBytes + runeBytes > budget)
                break;

            accumulatedBytes += runeBytes;
            charIndex += rune.Utf16SequenceLength;
        }

        if (charIndex == 0)
            return ellipsis;

        return string.Concat(trimmed.AsSpan(0, charIndex).TrimEnd(), ellipsis);
    }

    /// <summary>
    /// Validates an external HTTPS image URL accepted by Discord's media proxy.
    /// </summary>
    internal static bool IsValidExternalImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        var trimmed = url.AsSpan().Trim();
        return trimmed.Length <= MaxAssetUrlLength
            && trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Compares two <see cref="DiscordActivity"/> payloads to suppress redundant IPC updates.
    /// Allows up to ±2s drift on timestamps to ignore sub-second capture rounding when playback did not seek.
    /// </summary>
    internal static bool AreActivitiesEquivalent(DiscordActivity a, DiscordActivity b)
    {
        if (a.Type != b.Type
            || a.StatusDisplayType != b.StatusDisplayType
            || a.Instance != b.Instance
            || !string.Equals(a.Details, b.Details, StringComparison.Ordinal)
            || !string.Equals(a.DetailsUrl, b.DetailsUrl, StringComparison.Ordinal)
            || !string.Equals(a.State, b.State, StringComparison.Ordinal)
            || !string.Equals(a.StateUrl, b.StateUrl, StringComparison.Ordinal))
        {
            return false;
        }

        if (a.Timestamps == null != (b.Timestamps == null))
            return false;

        if (a.Timestamps != null && b.Timestamps != null)
        {
            if (a.Timestamps.Start.HasValue != b.Timestamps.Start.HasValue
                || a.Timestamps.End.HasValue != b.Timestamps.End.HasValue)
            {
                return false;
            }

            if (a.Timestamps.Start.HasValue
                && Math.Abs(a.Timestamps.Start.Value - b.Timestamps.Start!.Value) > 2)
            {
                return false;
            }

            if (a.Timestamps.End.HasValue
                && Math.Abs(a.Timestamps.End.Value - b.Timestamps.End!.Value) > 2)
            {
                return false;
            }
        }

        if (a.Assets == null != (b.Assets == null))
            return false;

        if (a.Assets != null && b.Assets != null)
        {
            if (!string.Equals(a.Assets.LargeImage, b.Assets.LargeImage, StringComparison.Ordinal)
                || !string.Equals(a.Assets.LargeText, b.Assets.LargeText, StringComparison.Ordinal)
                || !string.Equals(a.Assets.LargeUrl, b.Assets.LargeUrl, StringComparison.Ordinal)
                || !string.Equals(a.Assets.SmallImage, b.Assets.SmallImage, StringComparison.Ordinal)
                || !string.Equals(a.Assets.SmallText, b.Assets.SmallText, StringComparison.Ordinal)
                || !string.Equals(a.Assets.SmallUrl, b.Assets.SmallUrl, StringComparison.Ordinal))
            {
                return false;
            }
        }

        int countA = a.Buttons?.Count ?? 0;
        int countB = b.Buttons?.Count ?? 0;
        if (countA != countB)
            return false;

        for (int i = 0; i < countA; i++)
        {
            if (!string.Equals(a.Buttons![i].Label, b.Buttons![i].Label, StringComparison.Ordinal)
                || !string.Equals(a.Buttons[i].Url, b.Buttons[i].Url, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    #endregion

    #region Disposal

    private void UnsubscribeEvents()
    {
        _playerControl.CurrentTrackChanged -= HandleTrackChanged;
        _playerControl.IsPlayingChanged -= HandleBoolChanged;
        _playerControl.IsPausedChanged -= HandleBoolChanged;
        _playerControl.PlaybackStateChanged -= HandlePlaybackStateChanged;
        _playerControl.SeekCompleted -= HandleSeekCompleted;
        _playerControl.PlaybackPurityChanged -= HandlePlaybackPurityChanged;
        _playerControl.RepeatModeChanged -= HandleRepeatModeChanged;
        _playerControl.ShuffleEnabledChanged -= HandleBoolChanged;
        _playerControl.QueueChanged -= HandleQueueChanged;
        _playerControl.ForceSyncTriggered -= HandleForceSync;
        _library.OnTrackUpdated -= HandleLibraryTrackUpdated;

        _playlistService?.OnPlaylistChanged -= HandlePlaylistMetadataChanged;

        _authService?.OnAuthStateChanged -= HandleAuthStateChanged;
        LocalizationService.Instance.LanguageChanged -= HandleLanguageChanged;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        UnsubscribeEvents();

        Task? worker;
        lock (_stateLock)
        {
            _stopRequested = true;
            worker = _workerTask;
        }

        SignalWorker();

        bool workerExited = worker == null;
        if (worker != null)
        {
            try
            {
                await worker.WaitAsync(TimeSpan.FromMilliseconds(ShutdownGraceTimeoutMs)).ConfigureAwait(false);
                workerExited = true;
            }
            catch (TimeoutException)
            {
                _disposeCts.Cancel();
                try
                {
                    await worker.WaitAsync(TimeSpan.FromMilliseconds(400)).ConfigureAwait(false);
                    workerExited = true;
                }
                catch { }
            }
            catch
            {
                workerExited = true;
            }
        }

        _disposeCts.Cancel();
        await _ipc.DisposeAsync().ConfigureAwait(false);

        if (workerExited)
        {
            _wakeSignal.Dispose();
            _disposeCts.Dispose();
        }
    }

    #endregion
}