using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LMP.Core.Youtube;
using LMP.Core.Youtube.Music;
using LMP.Core.Youtube.Playlists;
using LMP.Core.Youtube.Search;
using LMP.Core.Youtube.Videos;
using LMP.Core.Youtube.Videos.Streams;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Bridge.NToken;
using LMP.Core.Youtube.Bridge.SigCipher;
using LMP.Core.Youtube.Bridge.PoToken;
using LMP.Core.Audio.Http;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Cache;
using LMP.Core.Youtube.Videos.ClosedCaptions;
using System.Text.Json;

namespace LMP.Core.Services;

/// <summary>
/// Central integration provider for YouTube and YouTube Music.
/// Coordinates API operations, token deciphering, search, playlists, and session stream cache management.
/// </summary>
public partial class YoutubeProvider : IDisposable
{
    #region Fields & Dependencies

    private readonly NetworkManager _networkManager;
    private readonly NTokenDecryptor _nTokenDecryptor;
    private readonly SigCipherDecryptor _sigCipherDecryptor;
    private readonly TrackRegistry _trackRegistry;
    private readonly LibraryService? _libraryService;
    private PoTokenProvider? _poTokenProvider;

    /// <summary>
    /// Global authorization service via session cookies.
    /// </summary>
    public readonly CookieAuthService AuthService = null!;

    /// <summary>
    /// RAM cache for full manifests. Key = trackId (with yt_ prefix).
    /// Mirrors the disk-based SessionCacheStore for instant access within the current session.
    /// </summary>
    private readonly ConcurrentDictionary<string, StreamManifest> _manifestRamCache = new(StringComparer.Ordinal);

    private YoutubeClient _youtube = null!;
    private volatile bool _disposed;

    private Task? _initTask;
    private readonly Lock _initLock = new();

    private static readonly Regex YoutubeVideoRegex = _YoutubeVideoRegex();
    private static readonly Regex YoutubePlaylistRegex = _YoutubePlaylistRegex();

    #endregion

    #region Properties & Events

    /// <summary>
    /// Indicates whether the background initialization of the provider is complete.
    /// </summary>
    public bool IsReady { get; private set; }

    /// <summary>
    /// Event raised when resource-intensive n-token deciphering begins.
    /// Passes the video ID to the UI to display a progress animation.
    /// </summary>
    public event Action<string>? OnNTokenDecryptionStarted;

    #endregion

    #region Constructor

    /// <summary>
    /// Creates an instance of the YouTube provider.
    /// </summary>
    public YoutubeProvider(
        NetworkManager networkManager,
        TrackRegistry trackRegistry,
        LibraryService? libraryService,
        CookieAuthService cookieAuth,
        NTokenDecryptor nTokenDecryptor,
        SigCipherDecryptor sigCipherDecryptor)
    {
        _networkManager = networkManager;
        _trackRegistry = trackRegistry;
        _libraryService = libraryService;
        AuthService = cookieAuth;
        _nTokenDecryptor = nTokenDecryptor;
        _sigCipherDecryptor = sigCipherDecryptor;
        _poTokenProvider = new PoTokenProvider(() => _networkManager.AudioClient);

        // Bind the centralized utility class to session cookies and network manager
        YoutubeClientUtils.Initialize(
            cookieAuth,
            () => _networkManager.ApiClient,
            () => _networkManager.IsVpnActive);

        _nTokenDecryptor.OnComplexDecryptionStarted += HandleNTokenDecryptionStarted;

        ReloadClient();

        AuthService?.OnAuthStateChanged += ReloadClient;

        _networkManager.NetworkRebuilt += ReloadClient;
    }

    #endregion

    #region Client Initialization

    /// <summary>
    /// Recreates the internal <see cref="YoutubeClient"/> based on the current ApiClient from <see cref="NetworkManager"/>.
    /// Called when switching accounts, changing network interfaces (VPN), or updating proxy settings.
    /// </summary>
    private void ReloadClient()
    {
        if (_disposed) return;

        // VisitorData and BotDetection cooldown are tied to the network route/session; reset them during rebuild.
        _poTokenProvider?.Invalidate();
        VideoController.ResetBotDetectionState();

        var baseHttpClient = _networkManager.ApiClient;
        var youtubeHandler = new YoutubeHttpHandler(baseHttpClient, AuthService, disposeClient: false);

        var wrappedClient = new HttpClient(youtubeHandler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        var newClient = new YoutubeClient(
            wrappedClient,
            _nTokenDecryptor,
            _sigCipherDecryptor,
            isAuthenticatedCheck: () => AuthService?.IsAuthenticated ?? false,
            ownsHttpClient: true,
            poTokenProvider: _poTokenProvider);

        var oldClient = Interlocked.Exchange(ref _youtube, newClient);
        if (oldClient != null)
        {
            // Deferred disposal of the old client wrapper to avoid breaking active calls
            _ = Task.Run(async () =>
            {
                if (await CancellationToken.None.DelayNoThrowAsync(15_000, continueOnCapturedContext: false).ConfigureAwait(false))
                {
                    try { oldClient.Dispose(); } catch { }
                }
            });
        }

        Log.Info($"[YouTube] Client reloaded via NetworkManager. Auth: {AuthService?.IsAuthenticated ?? false}");
    }

    /// <summary>
    /// Performs asynchronous initialization of the provider. Thread-safe.
    /// </summary>
    public Task InitializeAsync()
    {
        lock (_initLock)
        {
            IsReady = true;
            _initTask ??= InitializeInternalAsync();
            return _initTask;
        }
    }

    /// <summary>
    /// Ensures background initialization (VisitorData / BotGuard / QuickJS) is triggered on demand
    /// if the application was started in fast mode without initial warmup.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureInitializedOnDemand()
    {
        if (_disposed) return;
        if (!IsReady || _initTask is null)
        {
            _ = InitializeAsync();
        }
    }

    private async Task InitializeInternalAsync()
    {
        try
        {
            Log.Info("[YouTube] Initiating lazy background Visitor Data resolution...");

            // Retrieve VisitorData taking cookies into account and protecting with a timeout
            var visitorData = await YoutubeClientUtils.EnsureVisitorDataAsync().ConfigureAwait(false);

            // Launch non-blocking background warmup for BotGuard (IntegrityToken with 12-hour TTL) and player STS,
            // so the user's first clicked track uses fast-mint (2 ms instead of ~900 ms).
            var poProvider = _poTokenProvider;
            if (poProvider != null && !_disposed)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var warmupTask = poProvider.WarmupAsync(visitorData);
                        var jsInitTask = _sigCipherDecryptor.EnsureInitializedAsync().AsTask();
                        await Task.WhenAll(warmupTask, jsInitTask).ConfigureAwait(false);
                        Log.Info("[YouTube] Background BotGuard & QuickJS player context warmup completed.");
                    }
                    catch (Exception ex)
                    {
                        Log.Debug($"[YouTube] Background BotGuard/PlayerContext warmup notice: {ex.Message}");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[YouTube] Initialization non-fatal warning: {ex.Message}");
        }
        finally
        {
            IsReady = true;
            Log.Info("[YouTube] Initialized");
        }
    }

    /// <summary>
    /// Returns a configured <see cref="YoutubeClient"/> instance, triggering deferred warmup if necessary.
    /// </summary>
    public YoutubeClient GetClient()
    {
        EnsureInitializedOnDemand();
        return _youtube ?? throw new InvalidOperationException("YouTube client not initialized");
    }

    #endregion

    #region Handles

    private void HandleNTokenDecryptionStarted(string? rawVideoId)
    {
        if (string.IsNullOrWhiteSpace(rawVideoId))
            return;

        OnNTokenDecryptionStarted?.Invoke(rawVideoId);
    }

    #endregion

    #region Edit YouTube Data

    /// <summary>
    /// Sets the "Liked" status for a track on YouTube Music.
    /// </summary>
    public async Task LikeTrackAsync(string trackId, bool like)
    {
        if (AuthService?.IsAuthenticated != true) return;
        try
        {
            var rawId = YoutubeIdHelper.ExtractRawId(trackId);
            await _youtube.Music.LikeTrackAsync(rawId, like);
            Log.Info($"[Music] Like={like} for {rawId}");
        }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to like: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Creates a new cloud playlist in the YouTube account.
    /// </summary>
    public async Task<string?> CreatePlaylistAsync(
        string title,
        IReadOnlyList<string>? videoIds = null,
        string? description = null,
        CancellationToken ct = default)
    {
        if (AuthService?.IsAuthenticated != true)
            throw new InvalidOperationException("Cannot create cloud playlist: user is not authenticated.");

        VideoController.ThrowIfInCooldown();

        try
        {
            var ytId = await _youtube.Mutations.CreatePlaylistAsync(title, videoIds, description, ct);
            Log.Info($"[Music] Created playlist '{title}' → YT ID: {ytId}");
            return ytId;
        }
        catch (BotDetectionException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to create playlist '{title}': {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Performs batch addition of tracks to a cloud playlist.
    /// </summary>
    public async Task<List<string?>> AddTracksToPlaylistAsync(
        string playlistId, IReadOnlyList<string> trackIds)
    {
        if (AuthService?.IsAuthenticated != true) return [];

        VideoController.ThrowIfInCooldown();

        try
        {
            return await MutationsClientAddTracksAsync(playlistId, trackIds, CancellationToken.None).ConfigureAwait(false);
        }
        catch (BotDetectionException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to batch add to playlist: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Performs batch addition of tracks to a cloud playlist, rethrowing network and HTTP exceptions for the Outbox queue.
    /// </summary>
    internal async Task<List<string?>> MutationsClientAddTracksAsync(
        string playlistId,
        IReadOnlyList<string> trackIds,
        CancellationToken ct = default)
    {
        if (AuthService?.IsAuthenticated != true || trackIds.Count == 0) return [];

        VideoController.ThrowIfInCooldown();

        var rawIds = new List<string>(trackIds.Count);
        for (int i = 0; i < trackIds.Count; i++)
            rawIds.Add(YoutubeIdHelper.ExtractRawId(trackIds[i]));

        var result = await GetClient().Mutations.AddTracksAsync(playlistId, rawIds, ct).ConfigureAwait(false);
        Log.Info($"[Music] Batch added {trackIds.Count} tracks to {playlistId}");
        return result;
    }

    /// <summary>
    /// Performs batch deletion of items from a cloud playlist.
    /// </summary>
    public async Task RemoveTracksFromPlaylistAsync(
        string playlistId, IReadOnlyList<string> setVideoIds)
    {
        if (AuthService?.IsAuthenticated != true) return;

        VideoController.ThrowIfInCooldown();

        try
        {
            await MutationsClientRemoveTracksAsync(playlistId, setVideoIds, CancellationToken.None).ConfigureAwait(false);
        }
        catch (BotDetectionException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to batch remove from playlist: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Performs batch deletion of items from a cloud playlist with cancellation token support for the Outbox queue.
    /// </summary>
    internal async Task MutationsClientRemoveTracksAsync(
        string playlistId,
        IReadOnlyList<string> setVideoIds,
        CancellationToken ct = default)
    {
        if (AuthService?.IsAuthenticated != true || setVideoIds.Count == 0) return;

        VideoController.ThrowIfInCooldown();

        await GetClient().Mutations.RemoveTracksAsync(playlistId, setVideoIds, ct).ConfigureAwait(false);
        Log.Info($"[Music] Batch removed {setVideoIds.Count} tracks from {playlistId}");
    }

    /// <summary>
    /// Performs batch reordering of tracks in a YouTube cloud playlist.
    /// </summary>
    public async Task MoveTracksInPlaylistAsync(
        string playlistId,
        IReadOnlyList<(string SetVideoId, string? PredecessorSetVideoId, string? SuccessorSetVideoId)> moves,
        CancellationToken ct = default)
    {
        if (AuthService?.IsAuthenticated != true || moves.Count == 0) return;

        VideoController.ThrowIfInCooldown();

        try
        {
            await _youtube.Mutations.MoveTracksAsync(playlistId, moves, ct);
            Log.Info($"[Music] Reordered {moves.Count} tracks in playlist {playlistId}");
        }
        catch (BotDetectionException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to reorder tracks in playlist {playlistId}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Edits the text description of a cloud playlist.
    /// </summary>
    public async Task EditPlaylistDescriptionAsync(string playlistId, string description)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
            throw new ArgumentException("Playlist ID cannot be empty.", nameof(playlistId));

        if (AuthService?.IsAuthenticated != true)
            throw new InvalidOperationException("Cannot edit cloud playlist: user is not authenticated.");

        VideoController.ThrowIfInCooldown();

        try
        {
            await _youtube.Mutations.SetPlaylistDescriptionAsync(playlistId, description);
            Log.Info($"[Music] Updated description for playlist {playlistId}");
        }
        catch (BotDetectionException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to update playlist description {playlistId}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Uploads a custom cover art image for a cloud playlist.
    /// </summary>
    public async Task<bool> UploadPlaylistThumbnailAsync(
        string playlistId,
        byte[] imageData)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
            throw new ArgumentException("Playlist ID cannot be empty.", nameof(playlistId));

        if (AuthService?.IsAuthenticated != true)
            throw new InvalidOperationException("Cannot upload thumbnail: user is not authenticated.");

        VideoController.ThrowIfInCooldown();

        try
        {
            var result = await _youtube.Mutations.UploadPlaylistThumbnailAsync(
                playlistId, imageData);

            if (result)
            {
                Log.Info($"[Music] Thumbnail uploaded for playlist {playlistId}");
            }

            return result;
        }
        catch (BotDetectionException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to upload thumbnail for playlist {playlistId}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Updates the title and/or description of a cloud playlist in a single request.
    /// </summary>
    public async Task EditPlaylistDetailsAsync(
        string playlistId,
        string? newTitle,
        string? description,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
            throw new ArgumentException("Playlist ID cannot be empty.", nameof(playlistId));

        if (AuthService?.IsAuthenticated != true)
            throw new InvalidOperationException("Cannot edit cloud playlist: user is not authenticated.");

        VideoController.ThrowIfInCooldown();

        try
        {
            await _youtube.Mutations.EditPlaylistDetailsAsync(playlistId, newTitle, description, ct)
                .ConfigureAwait(false);
            Log.Info($"[Music] Updated details for playlist {playlistId} " +
                     $"(name={newTitle != null}, desc={description != null})");
        }
        catch (BotDetectionException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to update playlist details {playlistId}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Updates the title of a cloud playlist.
    /// </summary>
    public async Task RenamePlaylistAsync(string playlistId, string newTitle)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
            throw new ArgumentException("Playlist ID cannot be empty.", nameof(playlistId));

        if (AuthService?.IsAuthenticated != true)
            throw new InvalidOperationException("Cannot rename cloud playlist: user is not authenticated.");

        VideoController.ThrowIfInCooldown();

        try
        {
            await _youtube.Mutations.RenamePlaylistAsync(playlistId, newTitle);
            Log.Info($"[Music] Renamed playlist {playlistId} → '{newTitle}'");
        }
        catch (BotDetectionException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to rename playlist {playlistId}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Deletes a cloud playlist from the YouTube account.
    /// </summary>
    public async Task DeletePlaylistAsync(string playlistId)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
            throw new ArgumentException("Playlist ID cannot be empty.", nameof(playlistId));

        if (AuthService?.IsAuthenticated != true)
            throw new InvalidOperationException("Cannot delete cloud playlist: user is not authenticated.");

        VideoController.ThrowIfInCooldown();

        try
        {
            await _youtube.Mutations.DeletePlaylistAsync(playlistId);
            Log.Info($"[Music] Deleted playlist {playlistId} from YouTube");
        }
        catch (BotDetectionException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to delete playlist {playlistId}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Fetches complete playlist synchronization data from YouTube, including tracks, author, and privacy status.
    /// Rethrows resource missing exceptions (404 / PlaylistUnavailableException) for graceful fallback handling.
    /// </summary>
    public async Task<FullPlaylistSyncData?> GetFullPlaylistDataAsync(
        string youtubePlaylistId,
        CancellationToken ct = default)
    {
        if (AuthService?.IsAuthenticated != true) return null;

        VideoController.ThrowIfInCooldown();

        try
        {
            var data = await _youtube.Sync.GetFullPlaylistDataAsync(youtubePlaylistId, ct);

            Log.Info($"[Music] Fetched full playlist data: " +
                     $"'{data.Title}', {data.Tracks.Count} tracks for {youtubePlaylistId}");

            return data;
        }
        catch (BotDetectionException) { throw; }
        catch (HttpRequestException) { throw; }
        catch (PlaylistUnavailableException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to fetch full playlist data: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region Resolve Stream (3-Tier Cache Strategy)

    /// <summary>
    /// Resolves the stream URL for a track via the YouTube API.
    /// <para><b>High-performance 3-tier algorithm:</b></para>
    /// <list type="number">
    ///   <item>Exact full disk cache: Instantly returns local file if requested format matches.</item>
    ///   <item>Any full disk cache: Returns any local file if user didn't request a specific format.</item>
    ///   <item>RAM manifest cache: Extracts descriptor from full in-memory manifest copy (0 network calls).</item>
    ///   <item>Disk manifest cache: Performs adaptive HEAD request to CDN. On 200/206, restores entire manifest without querying YouTube API.</item>
    ///   <item>YouTube API call: Requests full manifest with all media variants, saving them to RAM and disk (1 API call for all formats).</item>
    /// </list>
    /// </summary>
    /// <param name="track">Track requiring stream URL.</param>
    /// <param name="forceRefresh">Bypass local caches and reset PoToken/cipher state prior to request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="variantOverride">
    /// Exact variant (container/bitrate) of currently playing source. Takes priority over track preference
    /// to prevent refresh from replacing WebM with MP4 during partial cache playback.
    /// </param>
    /// <param name="excludedClients">
    /// Clients whose URLs for this track were rejected by CDN with HTTP 403. When present, RAM/disk manifests are skipped.
    /// </param>
    public async Task<ResolvedStreamDescriptor?> RefreshStreamAsync(
        TrackInfo track,
        bool forceRefresh = false,
        CancellationToken ct = default,
        StreamSelectionHint? variantOverride = null,
        IReadOnlyList<string>? excludedClients = null)
    {
        EnsureInitializedOnDemand();

        var rawVideoId = track.GetRawIdSpan().ToString();
        if (string.IsNullOrEmpty(rawVideoId)) return null;

        var requested = variantOverride is { HasFormat: true } exactVariant
            ? exactVariant
            : StreamSelectionHint.FromTrack(track, _libraryService?.Settings.RememberTrackFormat == true);

        bool hasExcludedClients = excludedClients is { Count: > 0 };

        Log.Info($"[YouTube] RefreshStreamAsync start: track={track.Id}, force={forceRefresh}, target={requested.Format?.ToContainerName() ?? "-"}/{requested.BitrateKbps}" +
                 (hasExcludedClients ? $", excluded={string.Join(",", excludedClients!)}" : ""));

        if (forceRefresh)
        {
            PerformForceRefreshReset(rawVideoId, track.Id);
        }

        if (!forceRefresh && requested.HasFormat && requested.HasBitrate)
        {
            string cacheKey = AudioSourceFactory.BuildCacheKey(track.Id, requested.Format!.Value, requested.BitrateKbps);
            if (AudioSourceFactory.GlobalCache.IsFullyCached(cacheKey))
            {
                var entry = AudioSourceFactory.GlobalCache.GetCacheInfo(cacheKey);
                if (entry != null && AudioEngine.IsCacheSizePlausible(entry, track.Duration))
                {
                    var d = CreateDescriptorFromCacheEntry(track.Id, entry);
                    Log.Info($"[YouTube] RefreshStreamAsync FULL CACHE (exact) -> {d}");
                    return d;
                }
            }
        }

        if (!forceRefresh && !requested.HasFormat)
        {
            var anyCache = AudioSourceFactory.FindAnyCachedTrack(rawVideoId);
            if (anyCache != null && AudioEngine.IsCacheSizePlausible(anyCache.Value.Entry, track.Duration))
            {
                var d = CreateDescriptorFromCacheEntry(track.Id, anyCache.Value.Entry);
                Log.Info($"[YouTube] RefreshStreamAsync FULL CACHE (any) -> {d}");
                return d;
            }
        }

        // RAM/disk manifests may contain URLs for a client already rejected with HTTP 403 by CDN: bypass caches and call API if exclusions exist.
        if (!forceRefresh && !hasExcludedClients && _manifestRamCache.TryGetValue(track.Id, out var ramManifest))
        {
            var d = SelectFromManifest(ramManifest, track.Id, requested.Format, requested.BitrateKbps, StreamSource.ProviderMemoryCache);
            if (d != null && !StreamUrlEgressGuard.Shared.IsBoundToCurrentEgress(d.Value.Url))
            {
                Log.Info($"[YouTube] RAM manifest for {track.Id} is bound to another egress IP, dropping");
                _manifestRamCache.TryRemove(track.Id, out _);
                d = null;
            }

            if (d != null)
            {
                Log.Info($"[YouTube] RefreshStreamAsync RAM MANIFEST -> {d}");
                return d;
            }
        }

        if (!forceRefresh && !hasExcludedClients)
        {
            var diskEntry = await SessionCacheStore
                .TryGetManifestAndProbeAsync(track.Id, Audio.Http.SharedHttpClient.Instance, ct)
                .ConfigureAwait(false);

            if (diskEntry is { Variants.Count: > 0 })
            {
                var diskManifest = ReconstructManifest(diskEntry);
                _manifestRamCache[track.Id] = diskManifest;

                var d = SelectFromManifest(diskManifest, track.Id, requested.Format, requested.BitrateKbps, StreamSource.SessionCache);
                if (d != null)
                {
                    Log.Info($"[YouTube] RefreshStreamAsync DISK MANIFEST ({diskEntry.Variants.Count} variants) -> {d}");
                    return d;
                }
            }
        }

        VideoController.ThrowIfInCooldown();

        try
        {
            var vId = VideoId.Parse(rawVideoId);
            var manifest = await _youtube.Videos.Streams
                .GetManifestAsync(vId, ct, forceRefresh, excludedClients)
                .ConfigureAwait(false);

            var audioStreams = manifest.GetAudioOnlyStreams()
                .OrderByDescending(s => s.Bitrate)
                .ToList();

            if (audioStreams.Count == 0)
                throw new StreamUnavailableException($"No audio streams for {rawVideoId}", rawVideoId, StreamUnavailableReason.AllClientsFailed);

            _manifestRamCache[track.Id] = manifest;
            SessionCacheStore.RecordManifest(track.Id, audioStreams, manifest.IntegratedLufs);

            var descriptor = SelectFromManifest(manifest, track.Id, requested.Format, requested.BitrateKbps) ?? throw new StreamUnavailableException($"No matching stream for {rawVideoId}", rawVideoId, StreamUnavailableReason.AllClientsFailed);
            Log.Info($"[YouTube] RefreshStreamAsync YOUTUBE API ({audioStreams.Count} variants) -> {descriptor}");
            return descriptor;
        }
        catch (OperationCanceledException) { throw; }
        catch (StreamUnavailableException) { throw; }
        catch (BotDetectionException) { throw; }
        catch (LoginRequiredException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[YouTube] RefreshStreamAsync error: {ex.Message}");
            throw;
        }
    }

    #endregion

    #region Quality Options (Menu UI Support)

    /// <summary>
    /// Returns a list of available stream formats for the specified video, supporting cancellation and forced refresh.
    /// Prevents unnecessary network requests by reading downloaded files, previously saved manifests, and disk cache.
    /// </summary>
    /// <param name="videoId">Video or track ID.</param>
    /// <param name="ct">Operation cancellation token.</param>
    /// <param name="forceRefresh">Flag to force bypass of local cache and invoke YouTube API.</param>
    /// <returns>List of audio stream variants.</returns>
    public async Task<List<StreamOption>> GetStreamOptionsAsync(
        string videoId,
        CancellationToken ct = default,
        bool forceRefresh = false)
    {
        if (string.IsNullOrWhiteSpace(videoId)) return [];
        EnsureInitializedOnDemand();

        string trackId = videoId.StartsWith("yt_", StringComparison.Ordinal) ? videoId : $"yt_{videoId}";
        string rawId = videoId.StartsWith("yt_", StringComparison.Ordinal) ? videoId[3..] : videoId;

        var track = _trackRegistry.TryGet(trackId) ?? _libraryService?.GetTrack(trackId);
        bool isExplicitDownload = track != null && track.IsDownloaded && !string.IsNullOrEmpty(track.LocalPath) && File.Exists(track.LocalPath);

        StreamManifest? manifest = null;

        if (!forceRefresh)
        {
            // 1. FIRST check RAM Cache (0 ms, 0 network calls). If manifest was already fetched via "Refresh" button — use it!
            if (_manifestRamCache.TryGetValue(trackId, out var ramManifest))
            {
                manifest = ramManifest;
            }
            // 2. Next, check Disk Session Cache (0 ms, 0 network calls)
            else
            {
                var diskEntry = SessionCacheStore.GetManifest(trackId);
                if (diskEntry is { Variants.Count: > 0 })
                {
                    manifest = ReconstructManifest(diskEntry);
                    _manifestRamCache[trackId] = manifest;
                }
            }

            // Manifest found in cache — returning ALL formats!
            if (manifest != null)
            {
                var options = MapManifestToOptions(manifest);
                Log.Debug($"[YouTube] StreamOptions CACHE hit: {trackId} ({options.Count} formats)");
                return options;
            }

            // 3. If NO manifest exists, but the file is downloaded (Downloads folder) — return 1 format to avoid network requests
            if (isExplicitDownload)
            {
                var format = track!.PreferredFormat ?? AudioSourceFactory.DetectFormat(track.LocalPath!);
                if (format == AudioFormat.Unknown) format = AudioFormat.WebM;

                var localOption = new StreamOption
                {
                    Format = format,
                    CodecType = AudioSourceFactory.GetCodecForFormat(format),
                    Bitrate = track.PreferredBitrate > 0 ? track.PreferredBitrate : 128,
                    SizeMb = new FileInfo(track.LocalPath!).Length / (1024.0 * 1024.0),
                    IsDownloaded = true,
                    IsActive = true
                };

                Log.Debug($"[YouTube] StreamOptions EXPLICIT DOWNLOAD hit: {trackId}");
                return [localOption];
            }

            // 4. Same for Local Audio Disk Cache (hidden cache)
            var globalCache = AudioSourceFactory.GlobalCache;
            if (globalCache != null)
            {
                var cachedTrack = globalCache.FindBestCacheByTrackId(trackId)
                    ?? globalCache.FindBestStartupCache(trackId, 0);

                if (cachedTrack != null)
                {
                    var localOption = new StreamOption
                    {
                        Format = cachedTrack.Format,
                        CodecType = cachedTrack.Codec,
                        Bitrate = cachedTrack.Bitrate,
                        SizeMb = cachedTrack.TotalSize / (1024.0 * 1024.0),
                        IsDownloaded = true,
                        IsActive = true
                    };

                    Log.Debug($"[YouTube] StreamOptions LOCAL DISK AUDIO cache hit: {trackId}");
                    return [localOption];
                }
            }
        }

        // 5. YouTube API Call — invoked on forced refresh or local cache miss
        try
        {
            var vId = VideoId.Parse(rawId);
            manifest = await _youtube.Videos.Streams
                .GetManifestAsync(vId, ct)
                .ConfigureAwait(false);

            var audioStreams = manifest.GetAudioOnlyStreams()
                .OrderByDescending(s => s.Bitrate)
                .ToList();

            if (audioStreams.Count > 0)
            {
                _manifestRamCache[trackId] = manifest;
                SessionCacheStore.RecordManifest(trackId, audioStreams, manifest.IntegratedLufs);
            }

            return MapManifestToOptions(manifest);
        }
        catch (Exception ex)
        {
            Log.Error($"[YouTube] GetStreamOptions error: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Invalidates the RAM manifest cache for a specific track.
    /// Called from <see cref="AudioEngine"/> on 403 status code, so that the next
    /// <c>RefreshStreamAsync(force=false)</c> queries the YouTube API
    /// instead of returning the same dead URL from RAM.
    /// </summary>
    /// <param name="trackId">Track identifier (with or without yt_ prefix).</param>
    public void InvalidateMemoryCache(string trackId)
    {
        if (string.IsNullOrEmpty(trackId))
            return;

        // Remove by exact key (usually "yt_xxxxx")
        _manifestRamCache.TryRemove(trackId, out _);

        // Fallback: if a rawId without prefix was passed, try both variants.
        var rawId = YoutubeIdHelper.ExtractRawId(trackId);
        if (!string.Equals(rawId, trackId, StringComparison.Ordinal))
        {
            _manifestRamCache.TryRemove($"yt_{rawId}", out _);
            _manifestRamCache.TryRemove(rawId, out _);
        }

        Log.Debug($"[YouTube] RAM manifest cache invalidated for {trackId}");
    }
    #endregion

    #region Support Helpers

    private AudioOnlyStreamInfo? SelectBestStream(
         IReadOnlyList<AudioOnlyStreamInfo> streams,
         AudioFormat? preferredFormat,
         int preferredBitrate = 0)
    {
        if (streams.Count == 0) return null;

        var blacklist = AudioSourceFactory.CdnBlacklist;

        // Fast check: determines if there are blocked hosts without allocating a new List via LINQ
        bool hasBlocked = false;
        for (int i = 0; i < streams.Count; i++)
        {
            if (blacklist.IsBlockedUrl(streams[i].Url))
            {
                hasBlocked = true;
                break;
            }
        }

        AudioOnlyStreamInfo? bestMatch = null;
        double bestDelta = double.MaxValue;
        AudioOnlyStreamInfo? firstInFormat = null;
        AudioOnlyStreamInfo? firstUnblocked = null;
        AudioOnlyStreamInfo? standardFallback = null;

        var qualityPref = _libraryService?.Settings.QualityPreference ?? AudioQualityPreference.BestAvailable;

        for (int i = 0; i < streams.Count; i++)
        {
            var stream = streams[i];
            bool isBlocked = hasBlocked && blacklist.IsBlockedUrl(stream.Url);

            // If alternatives exist, skip blocked ones
            if (isBlocked) continue;

            firstUnblocked ??= stream;

            var streamFormat = YoutubeIdHelper.MapContainerToFormat(stream.Container.Name);

            if (qualityPref == AudioQualityPreference.Standard && standardFallback == null && streamFormat == AudioFormat.Mp4)
            {
                standardFallback = stream;
            }

            if (preferredFormat is { } requestedFormat && requestedFormat != AudioFormat.Unknown)
            {
                if (streamFormat == requestedFormat)
                {
                    firstInFormat ??= stream;

                    if (preferredBitrate > 0)
                    {
                        var delta = Math.Abs(stream.Bitrate.KiloBitsPerSecond - preferredBitrate);
                        if (delta < bestDelta)
                        {
                            bestDelta = delta;
                            bestMatch = stream;
                        }
                    }
                }
            }
        }

        // If all streams turn out to be blocked, fall back to the first available stream
        if (firstUnblocked == null && streams.Count > 0)
            return streams[0];

        if (preferredFormat is not null && preferredFormat != AudioFormat.Unknown)
        {
            if (preferredBitrate > 0 && bestMatch != null) return bestMatch;
            if (firstInFormat != null) return firstInFormat;
        }

        if (qualityPref == AudioQualityPreference.Standard && standardFallback != null)
            return standardFallback;

        return firstUnblocked ?? streams[0];
    }

    private static AudioCodec DetermineCodec(AudioOnlyStreamInfo stream, AudioFormat format)
    {
        return stream.AudioCodec.ToAudioCodec(format);
    }

    #endregion

    #region Search Base

    /// <summary>
    /// Determines the type of the given search query (URL, playlist, or plain text).
    /// </summary>
    public static QueryType DetectQueryType(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return QueryType.None;
        query = query.Trim();

        if (YoutubePlaylistRegex.IsMatch(query)) return QueryType.Playlist;
        if (YoutubeVideoRegex.IsMatch(query) ||
            query.StartsWith(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            query.StartsWith(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return QueryType.DirectUrl;
        }

        return QueryType.Search;
    }

    /// <summary>
    /// Extracts the 11-character YouTube video ID from any URL.
    /// </summary>
    public static string? ExtractVideoId(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var match = YoutubeVideoRegex.Match(url);
        if (match.Success) return match.Groups[1].Value;
        try { return VideoId.TryParse(url)?.Value; } catch { return null; }
    }

    /// <summary>
    /// Loads detailed track metadata by its direct link.
    /// </summary>
    public async Task<TrackInfo?> GetTrackByUrlAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        EnsureInitializedOnDemand();
        try
        {
            var videoId = VideoId.TryParse(url) ?? VideoId.Parse(ExtractVideoId(url) ?? "");
            var rawTrack = await _youtube.Videos.GetAsync(videoId);
            return _trackRegistry.RegisterOrUpdate(rawTrack);
        }
        catch (Exception ex)
        {
            Log.Error($"[YouTube] GetTrackByUrlAsync error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Performs a streaming search for music content on YouTube.
    /// </summary>
    private async IAsyncEnumerable<List<TrackInfo>> SearchStreamingAsync(
        string query,
        int maxResults = 300,
        SearchFilter filter = SearchFilter.MusicSong,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            yield break;

        EnsureInitializedOnDemand();

        try
        {
            VideoController.ThrowIfInCooldown();
        }
        catch (BotDetectionException ex)
        {
            Log.Error($"[YouTube] Search blocked: wait {ex.RemainingCooldown.TotalSeconds:F0}s");
            yield break;
        }

        var sw = Stopwatch.StartNew();
        int count = 0;
        bool isMusicFilter = filter.IsMusicContext();

        Log.Info($"[YouTube] Streaming search '{query}' (Filter: {filter})...");

        await foreach (var batch in _youtube.Search.GetResultBatchesAsync(query, filter, ct))
        {
            if (ct.IsCancellationRequested) yield break;

            if (VideoController.IsInCooldown)
            {
                Log.Warn("[YouTube] Search interrupted by bot detection cooldown");
                yield break;
            }

            var tracks = new List<TrackInfo>(batch.Items.Count);

            for (int i = 0; i < batch.Items.Count && count < maxResults; i++)
            {
                if (batch.Items[i] is not TrackInfo rawTrack) continue;

                if (isMusicFilter) rawTrack.IsMusic = true;

                var track = _trackRegistry.RegisterOrUpdate(rawTrack);
                tracks.Add(track);
                count++;
            }

            if (tracks.Count > 0)
            {
                Log.Info($"[YouTube] +{tracks.Count} (total: {count}) in {sw.ElapsedMilliseconds}ms");
                yield return tracks;
            }

            if (count >= maxResults) break;
        }

        sw.Stop();
        Log.Info($"[YouTube] Search done: {count} results in {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Lightweight fast search method without step-by-step batch streaming.
    /// </summary>
    public async Task<List<TrackInfo>> SearchFastAsync(
     string query,
     int maxResults = 100,
     SearchFilter filter = SearchFilter.MusicSong,
     CancellationToken ct = default)
    {
        try
        {
            VideoController.ThrowIfInCooldown();
        }
        catch (BotDetectionException ex)
        {
            Log.Error($"[YouTube] Search blocked: wait {ex.RemainingCooldown.TotalSeconds:F0}s");
            return [];
        }

        var results = new List<TrackInfo>(maxResults);

        try
        {
            await foreach (var batch in SearchStreamingAsync(query, maxResults, filter, ct))
            {
                results.AddRange(batch);
                if (results.Count >= maxResults) break;
            }
        }
        catch (OperationCanceledException)
        {
            Log.Info($"[YouTube] Search cancelled after {results.Count} results");
        }
        catch (BotDetectionException) { }
        catch (Exception ex)
        {
            Log.Error($"[YouTube] SearchFastAsync error: {ex.Message}");
        }

        return results;
    }

    /// <summary>
    /// Standard YouTube search.
    /// </summary>
    public Task<List<TrackInfo>> SearchAsync(
        string query,
        int maxResults = 100,
        SearchFilter filter = SearchFilter.MusicSong)
    {
        return SearchFastAsync(query, maxResults, filter);
    }

    #endregion

    #region Search Session Nested Type

    /// <summary>
    /// Iterative paged search session with auto-deduplication and non-blocking disposal.
    /// Dynamically requests the active YoutubeClient, preventing ObjectDisposedException during network rebuilds.
    /// </summary>
    public sealed class SearchSession : IAsyncDisposable, IDisposable
    {
        private readonly YoutubeProvider _provider;
        private readonly TrackRegistry _registry;
        private readonly string _query;
        private readonly int _maxResults;
        private readonly HashSet<string> _seenIds;
        private readonly CancellationTokenSource _sessionCts = new();

        private IAsyncEnumerator<Batch<ISearchResult>>? _enumerator;
        private bool _hasMore = true;
        private int _isDisposed;
        private int _isFetching;

        private readonly Queue<TrackInfo> _buffer = new();
        private string? _lastContinuationToken;

        /// <summary>
        /// Indicates whether there are more results to fetch.
        /// </summary>
        public bool HasMore => (_hasMore || _buffer.Count > 0) && _isDisposed == 0 && _seenIds.Count < _maxResults;

        /// <summary>
        /// Count of loaded unique tracks in the current session.
        /// </summary>
        public int LoadedCount => _seenIds.Count;

        /// <summary>
        /// Applied search filter.
        /// </summary>
        public SearchFilter Filter { get; }

        internal SearchSession(
            YoutubeProvider provider,
            TrackRegistry registry,
            string query,
            int maxResults = 300,
            SearchFilter filter = SearchFilter.Video,
            IEnumerable<string>? skipTrackIds = null)
        {
            _provider = provider;
            _registry = registry;
            _query = query;
            _maxResults = maxResults;
            Filter = filter;
            _seenIds = new HashSet<string>(64, StringComparer.Ordinal);

            if (skipTrackIds != null)
            {
                foreach (var id in skipTrackIds)
                {
                    _seenIds.Add(YoutubeIdHelper.ExtractRawId(id));
                }
            }
        }

        /// <summary>
        /// Fetches the next batch of search results with network quota control.
        /// Automatically recreates the enumerator when the network client changes (ObjectDisposedException recovery).
        /// </summary>
        public async Task<List<TrackInfo>> FetchNextBatchAsync(int count = 25, CancellationToken ct = default)
        {
            if (_isDisposed != 0 || _seenIds.Count >= _maxResults || !HasMore)
                return [];

            if (Interlocked.CompareExchange(ref _isFetching, 1, 0) != 0)
            {
                Log.Warn("[SearchSession] FetchNextBatchAsync rejected: another fetch is currently active.");
                return [];
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _sessionCts.Token);
            var token = linkedCts.Token;

            try
            {
                if (_isDisposed != 0 || _seenIds.Count >= _maxResults)
                    return [];

                var results = new List<TrackInfo>(Math.Min(count, 32));

                // 1. Yield remaining items from the buffer
                while (_buffer.Count > 0 && results.Count < count)
                {
                    results.Add(_buffer.Dequeue());
                }

                if (results.Count >= count || !_hasMore)
                    return results;

                // 2. Network fetching: maximum 2 network requests per single scroll phase
                int networkCalls = 0;
                const int maxNetworkCalls = 2;

                while (results.Count < count && _hasMore && _seenIds.Count < _maxResults && networkCalls < maxNetworkCalls)
                {
                    networkCalls++;

                    bool moveNextOk;
                    try
                    {
                        var client = _provider.GetClient();
                        _enumerator ??= client.Search
                            .GetResultBatchesAsync(_query, Filter, _lastContinuationToken, token)
                            .GetAsyncEnumerator(token);

                        moveNextOk = await _enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        // Network was rebuilt (RebuildAll). Restoring enumerator from the saved continuation token
                        Log.Info("[SearchSession] Client was rebuilt during active search session. Recovering enumerator with continuation token...");
                        _enumerator = null;
                        var freshClient = _provider.GetClient();
                        _enumerator = freshClient.Search
                            .GetResultBatchesAsync(_query, Filter, _lastContinuationToken, token)
                            .GetAsyncEnumerator(token);

                        moveNextOk = await _enumerator.MoveNextAsync().ConfigureAwait(false);
                    }

                    if (!moveNextOk)
                    {
                        _hasMore = false;
                        break;
                    }

                    var batch = _enumerator.Current;
                    _lastContinuationToken = batch.ContinuationToken;
                    for (int i = 0; i < batch.Items.Count; i++)
                    {
                        if (_seenIds.Count >= _maxResults)
                        {
                            _hasMore = false;
                            break;
                        }

                        if (batch.Items[i] is not TrackInfo tInfo)
                            continue;

                        var rawId = tInfo.GetRawId();
                        if (!_seenIds.Add(rawId))
                            continue;

                        var canonicalTrack = _registry.RegisterOrUpdate(tInfo);

                        if (results.Count < count)
                            results.Add(canonicalTrack);
                        else
                            _buffer.Enqueue(canonicalTrack);
                    }
                }

                return results;
            }
            catch (OperationCanceledException) { return []; }
            catch (Exception ex)
            {
                Log.Error($"[SearchSession] Iteration failed: {ex.Message}");
                _hasMore = false;
                return [];
            }
            finally
            {
                Interlocked.Exchange(ref _isFetching, 0);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
                return;

            _hasMore = false;
            _sessionCts.Cancel();

            _buffer.Clear();
            _seenIds.Clear();

            if (_enumerator != null)
            {
                try { await _enumerator.DisposeAsync().ConfigureAwait(false); }
                catch (NotSupportedException) { }
                catch (Exception ex) { Log.Warn($"[SearchSession] Async dispose warning: {ex.Message}"); }
                _enumerator = null;
            }

            _sessionCts.Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
                return;

            _hasMore = false;
            _sessionCts.Cancel();

            _buffer.Clear();
            _seenIds.Clear();

            var enumerator = _enumerator;
            _enumerator = null;

            if (enumerator != null)
            {
                _ = Task.Run(async () =>
                {
                    try { await enumerator.DisposeAsync().ConfigureAwait(false); }
                    catch (NotSupportedException) { }
                    catch (Exception ex) { Log.Warn($"[SearchSession] Background dispose warning: {ex.Message}"); }
                });
            }

            _sessionCts.Dispose();
        }
    }

    private SearchSession? _currentSearchSession;
    private readonly ConcurrentDictionary<string, (DateTime ExpireAt, List<string> Items)> _suggestionsCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a new iterative search session.
    /// </summary>
    public SearchSession CreateSearchSession(
        string query,
        int maxResults = 300,
        SearchFilter filter = SearchFilter.MusicSong,
        IEnumerable<string>? skipTrackIds = null)
    {
        _currentSearchSession?.Dispose();
        _currentSearchSession = new SearchSession(this, _trackRegistry, query, maxResults, filter, skipTrackIds);
        Log.Info($"[YouTube] Search session: '{query}' (max:{maxResults}, filter:{filter})");
        return _currentSearchSession;
    }

    /// <summary>
    /// Starts search, returning both first batch tracks and the active session object for further loading.
    /// </summary>
    public async Task<(List<TrackInfo> Tracks, SearchSession Session)> SearchWithSessionAsync(
        string query,
        int initialCount = 25,
        int maxResults = 300,
        SearchFilter filter = SearchFilter.MusicSong,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return ([], null!);

        EnsureInitializedOnDemand();

        var sw = Stopwatch.StartNew();
        var session = CreateSearchSession(query, maxResults, filter);
        var tracks = await session.FetchNextBatchAsync(initialCount, ct).ConfigureAwait(false);

        sw.Stop();
        Log.Info($"[YouTube] Initial '{query}': {tracks.Count} in {sw.ElapsedMilliseconds}ms (Filter: {filter})");

        return (tracks, session);
    }

    /// <summary>
    /// Fetches search suggestions from Google/YouTube Suggest API with independent node timeouts and freeze protection.
    /// </summary>
    /// <param name="query">Search query.</param>
    /// <param name="ct">Caller cancellation token.</param>
    /// <returns>List of text suggestions (up to 10 items).</returns>
    public async Task<List<string>> GetSearchSuggestionsAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var normalizedQuery = query.Trim();

        // Check local RAM suggestion cache (TTL 5 minutes) to skip redundant requests on Backspace
        if (_suggestionsCache.TryGetValue(normalizedQuery, out var cached) && cached.ExpireAt > DateTime.UtcNow)
        {
            return cached.Items;
        }

        var client = _networkManager.ProbeClient;
        var encoded = Uri.EscapeDataString(normalizedQuery);

        for (int i = 0; i < 2; i++)
        {
            if (ct.IsCancellationRequested) return [];

            using var attemptTimeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1800));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, attemptTimeoutCts.Token);
            var token = linkedCts.Token;

            var endpoint = i == 0
                ? $"https://suggestqueries.google.com/complete/search?client=firefox&ds=yt&q={encoded}&hl=ru"
                : $"https://suggestqueries-clients6.youtube.com/complete/search?client=firefox&ds=yt&q={encoded}&hl=ru";

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.UserAgent.ParseAdd(YoutubeClientUtils.UaWeb);

                using var response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                    continue;

                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);

                if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() < 2)
                    continue;

                var arr = doc.RootElement[1];
                if (arr.ValueKind != JsonValueKind.Array)
                    continue;

                int maxSuggestions = _libraryService?.Settings.MaxSuggestionsCount ?? 8;
                int length = arr.GetArrayLength();
                var result = new List<string>(Math.Min(length, maxSuggestions));

                foreach (var item in arr.EnumerateArray())
                {
                    var text = item.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        result.Add(text);
                        if (result.Count >= maxSuggestions)
                            break;
                    }
                }

                if (_suggestionsCache.Count > 100)
                    _suggestionsCache.Clear();

                _suggestionsCache[normalizedQuery] = (DateTime.UtcNow.AddMinutes(5), result);
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return [];
            }
            catch (OperationCanceledException) when (attemptTimeoutCts.IsCancellationRequested)
            {
                Log.Debug($"[YouTube] Suggestion timeout for '{normalizedQuery}' on endpoint #{i}");
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException && (ct.IsCancellationRequested || attemptTimeoutCts.IsCancellationRequested))
            {
                // Suppress expected socket interruptions when cancellation token triggers
            }
            catch (Exception ex)
            {
                Log.Debug($"[YouTube] Suggestion endpoint #{i} failed: {ex.Message}");
            }
        }

        return [];
    }

    #endregion

    #region Playlists, Channels, Radio & Downloads

    /// <summary>
    /// Retrieves the title and track list from an external YouTube playlist.
    /// </summary>
    public async Task<(string Name, List<TrackInfo> Tracks)?> GetPlaylistAsync(string url)
    {
        EnsureInitializedOnDemand();

        try
        {
            VideoController.ThrowIfInCooldown();
        }
        catch (BotDetectionException ex)
        {
            Log.Error($"[YouTube] Playlist blocked: wait {ex.RemainingCooldown.TotalSeconds:F0}s");
            return null;
        }

        try
        {
            var playlistId = PlaylistId.TryParse(url);
            if (playlistId == null) return null;

            var playlist = await _youtube.Playlists.GetAsync(playlistId.Value);
            var rawTracks = await _youtube.Playlists.GetVideosAsync(playlistId.Value).CollectAsync();

            var tracks = new List<TrackInfo>(rawTracks.Count);
            for (int i = 0; i < rawTracks.Count; i++)
            {
                var canonical = _trackRegistry.RegisterOrUpdate(rawTracks[i]);
                tracks.Add(canonical);
            }

            Log.Info($"[YouTube] Playlist '{playlist.Name}': {tracks.Count} tracks");
            return (playlist.Name, tracks);
        }
        catch (BotDetectionException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"[YouTube] GetPlaylistAsync error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Retrieves the list of cloud playlists for the currently authorized user directly from the YouTube Music API.
    /// Re-throws network layer exceptions to caller, preventing false reports of empty playlist collections.
    /// </summary>
    /// <returns>A list of <see cref="Playlist"/> models.</returns>
    public async Task<List<Playlist>> GetUserPlaylistsByAuthAsync()
    {
        if (AuthService?.IsAuthenticated != true) return [];

        try
        {
            return await GetClient().Music.GetLibraryPlaylistsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"[Sync] Failed to get user playlists: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Imports a playlist: loads metadata and all tracks in a single request,
    /// saving SetVideoId mappings to the local DB to avoid redundant browse queries on deletion.
    /// </summary>
    public async Task<Playlist?> ImportPlaylistAsync(
        string playlistId, bool isAccountSync = false, CancellationToken ct = default)
    {
        try
        {
            VideoController.ThrowIfInCooldown();
        }
        catch (BotDetectionException ex)
        {
            Log.Error($"[YouTube] Import blocked: wait {ex.RemainingCooldown.TotalSeconds:F0}s");
            return null;
        }

        try
        {
            var plId = new PlaylistId(playlistId);

            var result = await _youtube.Playlists.ImportAsync(plId, ct).ConfigureAwait(false);

            var playlist = result.Playlist;
            playlist.SyncMode = isAccountSync
                ? PlaylistSyncMode.TwoWaySync
                : PlaylistSyncMode.CloudPublic;

            // Ownership & Visibility
            ResolveOwnershipAndVisibility(playlist, isAccountSync);

            var tracks = result.Tracks;
            if (_libraryService != null)
            {
                ct.ThrowIfCancellationRequested();
                await _libraryService.AddOrUpdateTracksBatchAsync(tracks, ct).ConfigureAwait(false);
                for (int i = 0; i < tracks.Count; i++)
                    playlist.TrackIds.Add(tracks[i].Id);

                // Save SetVideoId extracted directly during the initial browse import,
                // and perform a repeat Sync request only if they were missing in the response.
                if (result.SetVideoIdMappings is { Count: > 0 } directMappings)
                {
                    await _libraryService.UpdateSetVideoIdsAsync(playlist.Id, directMappings, ct).ConfigureAwait(false);
                }
                else if (isAccountSync && AuthService?.IsAuthenticated == true && !string.IsNullOrEmpty(playlist.YoutubeId))
                {
                    try
                    {
                        var fullData = await _youtube.Sync.GetFullPlaylistDataAsync(playlist.YoutubeId, ct).ConfigureAwait(false);
                        if (fullData?.Tracks is { Count: > 0 } remoteTracks)
                        {
                            var mappings = new List<(string TrackId, string SetVideoId)>(remoteTracks.Count);
                            for (int i = 0; i < remoteTracks.Count; i++)
                            {
                                var r = remoteTracks[i];
                                if (!string.IsNullOrEmpty(r.SetVideoId))
                                    mappings.Add(("yt_" + r.VideoId, r.SetVideoId));
                            }

                            if (mappings.Count > 0)
                                await _libraryService.UpdateSetVideoIdsAsync(playlist.Id, mappings, ct).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[YouTube] Non-fatal: failed to cache setVideoIds during import of {playlistId}: {ex.Message}");
                    }
                }
            }
            else
            {
                for (int i = 0; i < tracks.Count; i++)
                    playlist.TrackIds.Add(tracks[i].Id);
            }

            Log.Info($"[YouTube] ImportPlaylist '{playlistId}': {tracks.Count} tracks, " +
                     $"Ownership={playlist.Ownership}, Visibility={playlist.Visibility}");
            return playlist;
        }
        catch (BotDetectionException) { throw; }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests)
        {
            throw;
        }
        catch (OperationCanceledException) { throw; }
        catch (PlaylistUnavailableException ex)
        {
            Log.Warn($"[YouTube] Playlist '{playlistId}' unavailable: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"[YouTube] Error importing playlist '{playlistId}': {ex.Message}");
            return null;
        }
    }

    private void ResolveOwnershipAndVisibility(Playlist playlist, bool isAccountSync)
    {
        if (!isAccountSync)
        {
            playlist.Ownership = PlaylistOwnership.Foreign;

            // URL import: if privacy couldn't be retrieved from API, assume at least Public
            if (playlist.Visibility == PlaylistVisibility.Unknown)
                playlist.Visibility = PlaylistVisibility.Public;

            return;
        }

        // Account sync: determine ownership by presence of private dropdown or comparison with profile
        if (AuthService?.IsAuthenticated != true)
        {
            playlist.Ownership = PlaylistOwnership.Unknown;
            return;
        }

        if (playlist.Visibility is PlaylistVisibility.Private or PlaylistVisibility.Unlisted)
        {
            playlist.Ownership = PlaylistOwnership.Mine;
            return;
        }

        var userName = AuthService.State.UserName;
        var playlistAuthor = playlist.Author;

        if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(playlistAuthor))
        {
            playlist.Ownership = PlaylistOwnership.Unknown;
            return;
        }

        bool matchesActiveUser = playlistAuthor.Equals(userName, StringComparison.OrdinalIgnoreCase);
        if (!matchesActiveUser && AuthService.State.CachedAccounts is { Count: > 0 } cachedAccounts)
        {
            for (int i = 0; i < cachedAccounts.Count; i++)
            {
                if (cachedAccounts[i].IsSelected &&
                    (playlistAuthor.Equals(cachedAccounts[i].Name, StringComparison.OrdinalIgnoreCase) ||
                     playlistAuthor.Equals(cachedAccounts[i].Handle, StringComparison.OrdinalIgnoreCase)))
                {
                    matchesActiveUser = true;
                    break;
                }
            }
        }

        playlist.Ownership = matchesActiveUser
            ? PlaylistOwnership.Mine
            : PlaylistOwnership.Foreign;

        // For third-party library playlists, fallback to Public only if privacy was not recognized
        if (playlist.IsForeign && playlist.Visibility == PlaylistVisibility.Unknown)
            playlist.Visibility = PlaylistVisibility.Public;
    }

    #endregion

    #region Manifest Mapping Helpers

    private ResolvedStreamDescriptor? SelectFromManifest(
        StreamManifest manifest,
        string trackId,
        AudioFormat? targetFormat,
        int targetBitrate,
        StreamSource origin = StreamSource.YouTubeApi)
    {
        var streams = manifest.GetAudioOnlyStreams();
        var selected = SelectBestStream(streams, targetFormat, targetBitrate);
        if (selected == null) return null;

        return CreateDescriptorFromStream(trackId, selected, manifest.IntegratedLufs, origin);
    }

    private ResolvedStreamDescriptor CreateDescriptorFromStream(
         string trackId,
         AudioOnlyStreamInfo stream,
         float perceptualLufs,
         StreamSource origin = StreamSource.YouTubeApi)
    {
        var url = stream.Url;
        var format = YoutubeIdHelper.MapContainerToFormat(stream.Container.Name);
        var codec = DetermineCodec(stream, format);

        DateTime expireUtc = DateTime.MaxValue;
        var expireStr = UrlEx.TryGetQueryParameterValue(url, "expire");
        if (expireStr != null && long.TryParse(expireStr, out var unix))
            expireUtc = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;

        string cdnHost = "";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            cdnHost = uri.Host;

        return new ResolvedStreamDescriptor
        {
            TrackId = trackId,
            Itag = stream.Itag,
            Url = url,
            Format = format,
            Codec = codec,
            BitrateKbps = (int)Math.Round(stream.Bitrate.KiloBitsPerSecond),
            ContentLengthBytes = stream.Size.Bytes,
            CdnHost = cdnHost,
            ExpireUtc = expireUtc,
            IntegratedLufs = perceptualLufs,
            LanguageCode = stream.AudioLanguage?.Code,
            IsDefaultLanguage = stream.IsAudioLanguageDefault ?? false,
            Origin = origin
        };
    }

    /// <summary>
    /// Creates a stream descriptor based on a fully cached entry.
    /// Bitrate is taken from the actual cache entry value rather than the user request,
    /// ensuring <see cref="AudioStreamInfo"/> accurately reflects the playing file.
    /// </summary>
    private static ResolvedStreamDescriptor CreateDescriptorFromCacheEntry(
        string trackId,
        AudioCacheEntry entry)
    {
        return new ResolvedStreamDescriptor
        {
            TrackId = trackId,
            Url = "",
            Format = entry.Format,
            Codec = entry.Codec,
            BitrateKbps = entry.Bitrate,
            ContentLengthBytes = entry.TotalSize,
            Origin = StreamSource.DiskCacheFull
        };
    }

    /// <summary>
    /// Reconstructs a <see cref="StreamManifest"/> from an on-disk manifest cache entry.
    /// </summary>
    private static StreamManifest ReconstructManifest(TrackManifestEntry entry)
    {
        var streams = new List<IStreamInfo>(entry.Variants.Count);
        for (int i = 0; i < entry.Variants.Count; i++)
        {
            var v = entry.Variants[i];
            Language? lang = !string.IsNullOrEmpty(v.LanguageCode)
                ? new Language(v.LanguageCode, v.LanguageCode)
                : null;

            streams.Add(new AudioOnlyStreamInfo(
                v.Itag,
                v.Url,
                new Container(v.Container),
                new FileSize(v.Clen),
                new Bitrate(v.Bitrate),
                v.Codec,
                lang,
                v.IsDefaultLanguage,
                hasEncryptedNToken: false));
        }

        return new StreamManifest(streams, entry.IntegratedLufs);
    }

    /// <summary>
    /// Converts a manifest into a list of UI options with deduplication by normalized bucket.
    /// Prevents displaying two formats associated with the same physical cache file.
    /// </summary>
    private static List<StreamOption> MapManifestToOptions(StreamManifest manifest)
    {
        var streams = manifest.GetAudioOnlyStreams();
        var seen = new HashSet<(AudioFormat, int)>();
        var result = new List<StreamOption>(streams.Count);

        for (int i = 0; i < streams.Count; i++)
        {
            var s = streams[i];
            var format = YoutubeIdHelper.MapContainerToFormat(s.Container.Name);
            int normalizedBitrate = AudioConstants.NormalizeBitrate((int)Math.Round(s.Bitrate.KiloBitsPerSecond));

            if (!seen.Add((format, normalizedBitrate)))
                continue;

            result.Add(new StreamOption
            {
                Format = format,
                CodecType = DetermineCodec(s, format),
                Bitrate = s.Bitrate.KiloBitsPerSecond,
                SizeMb = s.Size.MegaBytes
            });
        }

        return result;
    }

    private void PerformForceRefreshReset(string rawVideoId, string trackId)
    {
        Log.Warn($"[YouTube] [{rawVideoId}] Force refresh. Resetting caches...");
        try
        {
            _poTokenProvider?.Invalidate();
            _nTokenDecryptor.InvalidateCache();
            _sigCipherDecryptor.InvalidateCache();
            _nTokenDecryptor.PlayerManager.InvalidateContext();
            _youtube.Videos.Streams.InvalidateCipherManifest();
            SessionCacheStore.Invalidate(trackId);
            _manifestRamCache.TryRemove(trackId, out _);
            Log.Info($"[YouTube] [{rawVideoId}] Caches and PoToken purged for force refresh.");
        }
        catch (Exception ex)
        {
            Log.Error($"[YouTube] [{rawVideoId}] Force refresh cache purge failed: {ex.Message}");
        }
    }

    #endregion

    #region Diagnostic Logging & Cache Clear

    /// <summary>
    /// Attempts to quickly retrieve a stream descriptor from the RAM manifest cache without disk or network requests.
    /// Supports both full trackId (with yt_ prefix) and clean 11-character videoId.
    /// </summary>
    /// <param name="videoId">Video or track identifier.</param>
    /// <param name="format">Desired format.</param>
    /// <param name="bitrate">Desired bitrate.</param>
    public ResolvedStreamDescriptor? TryGetCachedStreamDescriptor(string videoId, AudioFormat? format, int bitrate)
    {
        string trackId = videoId.StartsWith("yt_", StringComparison.Ordinal) ? videoId : $"yt_{videoId}";
        if (_manifestRamCache.TryGetValue(trackId, out var manifest))
        {
            // A URL issued for a different external IP will be rejected by CDN with HTTP 403: do not yield such descriptor.
            var d = SelectFromManifest(manifest, trackId, format, bitrate, StreamSource.ProviderMemoryCache);
            return d is { } found && !StreamUrlEgressGuard.Shared.IsBoundToCurrentEgress(found.Url) ? null : d;
        }
        return null;
    }

    /// <summary>
    /// Clears the manifest cache from RAM.
    /// </summary>
    public void ClearCache()
    {
        _manifestRamCache.Clear();
        Log.Info("[YouTube] RAM manifest cache cleared");
    }

    #endregion

    #region Dispose

    /// <summary>
    /// Releases all managed resources of the YouTube provider.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        AuthService?.OnAuthStateChanged -= ReloadClient;

        _networkManager.NetworkRebuilt -= ReloadClient;

        _youtube?.Dispose();
        _poTokenProvider?.Dispose();
        _poTokenProvider = null;

        _manifestRamCache.Clear();
        Log.Info("[YouTube] Provider disposed");
    }

    [GeneratedRegex(
        @"(?:youtube\.com\/(?:watch\?v=|embed\/|v\/|shorts\/)|youtu\.be\/)([a-zA-Z0-9_-]{11})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex _YoutubeVideoRegex();

    [GeneratedRegex(
        @"(?:youtube\.com\/.*[?&]list=)([a-zA-Z0-9_-]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex _YoutubePlaylistRegex();

    #endregion
}

public sealed partial class StreamOption : ObservableObject
{
    /// <summary>Typed container format.</summary>
    public AudioFormat Format { get; init; }

    /// <summary>Typed audio codec.</summary>
    public AudioCodec CodecType { get; init; }

    public double Bitrate { get; set; }
    public double SizeMb { get; set; }

    public string Container => Format.ToContainerName();
    public string Codec => CodecType.ToDisplayName();

    public string DisplayName => $"{Codec} {string.Format(LocalizationService.Instance["Stream_Bitrate"], Bitrate)} ({Container})";

    public string SizeMbFormatted => string.Format(
        LocalizationService.Instance["Stream_Format_Mb"],
        SizeMb);

    [ObservableProperty] public partial bool IsDownloaded { get; set; }
    [ObservableProperty] public partial bool IsActive { get; set; }
}
