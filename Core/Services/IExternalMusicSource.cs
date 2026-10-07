using LMP.Core.Models;

namespace LMP.Core.Services;

/// <summary>
/// Контракт внешнего источника музыки (Яндекс Музыка, Spotify, VK и т.д.).
/// </summary>
public interface IExternalMusicSource
{
    /// <summary>
    /// Имя провайдера (для логов и UI).
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Проверяет токен и возвращает профиль пользователя.
    /// </summary>
    Task<ExternalUserProfile> GetProfileAsync(string token, CancellationToken ct = default);

    /// <summary>
    /// Возвращает список всех плейлистов пользователя (включая системный «Мне нравится»).
    /// </summary>
    Task<IReadOnlyList<ExternalPlaylist>> GetPlaylistsAsync(string token, long uid, CancellationToken ct = default);

    /// <summary>
    /// Загружает все треки выбранного плейлиста.
    /// </summary>
    Task<(ExternalPlaylist Metadata, IReadOnlyList<ExternalTrack> Tracks)> FetchPlaylistTracksAsync(
        string token,
        long uid,
        ExternalPlaylist targetPlaylist,
        CancellationToken ct = default);

    /// <summary>
    /// Возвращает суммарный объём переданных байт (Upload/Download).
    /// </summary>
    (long BytesReceived, long BytesSent) GetTrafficUsage();
}
