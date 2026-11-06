using LMP.Core.Youtube.Playlists;

namespace LMP.Core.Services;

/// <summary>
/// Service for managing user profiles, Google accounts, and YouTube brand channels.
/// </summary>
public partial class YoutubeUserDataService
{
    private readonly CookieAuthService _auth;
    private readonly TrackRegistry? _trackRegistry;

    /// <summary>
    /// Initializes a new instance of the user profile service.
    /// </summary>
    /// <param name="auth">The authentication and cookie management service.</param>
    public YoutubeUserDataService(CookieAuthService auth)
        : this(auth, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the user profile service with Identity Map registry support.
    /// </summary>
    /// <param name="auth">The authentication and cookie management service.</param>
    /// <param name="trackRegistry">The registry of canonical track models to maintain a single source of truth.</param>
    public YoutubeUserDataService(CookieAuthService auth, TrackRegistry? trackRegistry)
    {
        _auth = auth;
        _trackRegistry = trackRegistry;
    }

    #region Лайки YouTube

    /// <summary>
    /// Loads the user's liked tracks based on the current library synchronization mode.
    /// </summary>
    /// <remarks>
    /// Requests the list of liked tracks directly via the active YouTube Music client context.
    /// </remarks>
    /// <param name="provider">The YouTube provider used to obtain a configured client.</param>
    /// <param name="mode">The likes synchronization mode (music only or all videos).</param>
    /// <returns>A list of <see cref="TrackInfo"/> track models liked on YouTube.</returns>
    public async Task<List<TrackInfo>> GetLikedTracksAsync(
        YoutubeProvider provider,
        LikeSyncMode mode = LikeSyncMode.MusicOnly,
        CancellationToken ct = default)
    {
        if (!_auth.IsAuthenticated) return [];

        try
        {
            List<TrackInfo> likedTracks;

            switch (mode)
            {
                case LikeSyncMode.MusicOnly:
                    Log.Info("[Sync] Fetching Music Likes (LM) from YouTube Music...");
                    try
                    {
                        likedTracks = await provider.GetClient().Music.GetLikedTracksAsync(ct).ConfigureAwait(false);
                        Log.Info($"[Sync] Got {likedTracks.Count} music likes from LM.");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[Sync] Music API failed, falling back to LL: {ex.Message}");
                        var allLikes = await GetAllLikedVideosAsync(provider, ct).ConfigureAwait(false);
                        likedTracks = allLikes.FindAll(t => t.IsMusic);
                    }
                    break;

                case LikeSyncMode.AllVideos:
                    Log.Info("[Sync] Fetching ALL Liked Videos (LL)...");
                    likedTracks = await GetAllLikedVideosAsync(provider, ct).ConfigureAwait(false);
                    break;

                case LikeSyncMode.LocalOnly:
                    Log.Info("[Sync] LocalOnly mode - no cloud sync.");
                    return [];

                default:
                    return [];
            }

            ct.ThrowIfCancellationRequested();

            for (int i = 0; i < likedTracks.Count; i++)
            {
                var track = likedTracks[i];
                track.IsLiked = true;

                if (_trackRegistry != null)
                {
                    likedTracks[i] = _trackRegistry.RegisterOrUpdate(track, hasUserContext: true);
                }
            }

            Log.Info($"[Sync] Total liked tracks: {likedTracks.Count}");
            return likedTracks;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"[Sync] Failed to fetch liked tracks: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Performs paged fetch of all liked videos ("LL" playlist) up to a 1000 item limit.
    /// </summary>
    private static async Task<List<TrackInfo>> GetAllLikedVideosAsync(
        YoutubeProvider provider,
        CancellationToken ct)
    {
        return await provider.GetClient().Playlists
            .GetVideosAsync(new PlaylistId("LL"), ct)
            .TakeAsync(1000, ct)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    #endregion

    #region Получение профилей и каналов

    /// <summary>
    /// Returns a list of all channels (brand accounts) linked to the current cookies.
    /// Uses data automatically gathered during initial session validation.
    /// </summary>
    public async Task<List<YoutubeAccountItem>> GetAvailableAccountsAsync()
    {
        if (!_auth.IsAuthenticated) return [];

        if (_auth.State.CachedAccounts != null && _auth.State.CachedAccounts.Count > 0)
        {
            return _auth.State.CachedAccounts;
        }

        var (isValid, error, _) = await _auth.ValidateSessionAsync().ConfigureAwait(false);
        if (!isValid)
        {
            Log.Warn($"[UserDataService] Session validation failed during account fetch: {error}");
            return [];
        }

        return _auth.State.CachedAccounts ?? [];
    }

    /// <summary>
    /// Returns basic metadata of the currently authenticated user (name, email, and avatar).
    /// </summary>
    public async Task<(string Name, string Email, string AvatarUrl, string ActiveGaiaId)> GetAccountInfoAsync()
    {
        if (!_auth.IsAuthenticated) return ("Guest", "", "", "");

        await _auth.ValidateSessionAsync().ConfigureAwait(false);

        return (_auth.State.UserName, _auth.State.UserEmail, _auth.State.AvatarUrl, _auth.State.ActiveGaiaId);
    }

    #endregion
}