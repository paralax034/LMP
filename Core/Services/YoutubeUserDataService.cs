using LMP.Core.Youtube.Playlists;
using LMP.Core.Helpers.Extensions;

namespace LMP.Core.Services;

/// <summary>
/// Сервис для управления пользовательским профилем, Google-аккаунтами и брендовыми каналами на YouTube.
/// </summary>
public partial class YoutubeUserDataService
{
    private readonly CookieAuthService _auth;
    private readonly TrackRegistry? _trackRegistry;

    /// <summary>
    /// Инициализирует новый экземпляр службы работы с профилем пользователя.
    /// </summary>
    /// <param name="auth">Служба управления аутентификацией и куками.</param>
    public YoutubeUserDataService(CookieAuthService auth)
        : this(auth, null)
    {
    }

    /// <summary>
    /// Инициализирует новый экземпляр службы работы с профилем пользователя с поддержкой реестра Identity Map.
    /// </summary>
    /// <param name="auth">Служба управления аутентификацией и куками.</param>
    /// <param name="trackRegistry">Реестр канонических моделей треков для обеспечения единого источника истины.</param>
    public YoutubeUserDataService(CookieAuthService auth, TrackRegistry? trackRegistry)
    {
        _auth = auth;
        _trackRegistry = trackRegistry;
    }

    #region Лайки YouTube

    /// <summary>
    /// Загружает список понравившихся треков пользователя в зависимости от текущего режима синхронизации библиотеки.
    /// </summary>
    /// <remarks>
    /// Выполняет запрос списка понравившихся треков напрямую через контекст активного клиента YouTube Music.
    /// </remarks>
    /// <param name="provider">Провайдер YouTube для получения сконфигурированного клиента.</param>
    /// <param name="mode">Режим синхронизации лайков (только музыка или все видео).</param>
    /// <returns>Список моделей треков <see cref="TrackInfo"/>, отмеченных лайком на YouTube.</returns>
    public async Task<List<TrackInfo>> GetLikedTracksAsync(
        YoutubeProvider provider,
        LikeSyncMode mode = LikeSyncMode.MusicOnly)
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
                        likedTracks = await provider.GetClient().Music.GetLikedTracksAsync().ConfigureAwait(false);
                        Log.Info($"[Sync] Got {likedTracks.Count} music likes from LM.");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[Sync] Music API failed, falling back to LL: {ex.Message}");
                        var allLikes = await GetAllLikedVideosAsync(provider).ConfigureAwait(false);
                        likedTracks = allLikes.FindAll(t => t.IsMusic);
                    }
                    break;

                case LikeSyncMode.AllVideos:
                    Log.Info("[Sync] Fetching ALL Liked Videos (LL)...");
                    likedTracks = await GetAllLikedVideosAsync(provider).ConfigureAwait(false);
                    break;

                case LikeSyncMode.LocalOnly:
                    Log.Info("[Sync] LocalOnly mode - no cloud sync.");
                    return [];

                default:
                    return [];
            }

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
        catch (Exception ex)
        {
            Log.Error($"[Sync] Failed to fetch liked tracks: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Выполняет постраничную выгрузку всех понравившихся видео (плейлист "LL") до лимита в 1000 элементов.
    /// </summary>
    private static async Task<List<TrackInfo>> GetAllLikedVideosAsync(YoutubeProvider provider)
    {
        return await provider.GetClient().Playlists
            .GetVideosAsync(new PlaylistId("LL"))
            .TakeAsync(1000)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    #endregion

    #region Получение профилей и каналов

    /// <summary>
    /// Возвращает список всех каналов (бренд-аккаунтов), привязанных к текущим кукам.
    /// Использует данные, автоматически собранные при стартовой валидации сессии.
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
    /// Возвращает базовые метаданные текущего авторизованного пользователя (имя, почту и аватар).
    /// </summary>
    public async Task<(string Name, string Email, string AvatarUrl, string ActiveGaiaId)> GetAccountInfoAsync()
    {
        if (!_auth.IsAuthenticated) return ("Guest", "", "", "");

        await _auth.ValidateSessionAsync().ConfigureAwait(false);

        return (_auth.State.UserName, _auth.State.UserEmail, _auth.State.AvatarUrl, _auth.State.ActiveGaiaId);
    }

    #endregion
}