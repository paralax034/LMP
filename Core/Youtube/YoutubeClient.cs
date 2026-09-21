using LMP.Core.Youtube.Bridge.NToken;
using LMP.Core.Youtube.Bridge.PoToken;
using LMP.Core.Youtube.Bridge.SigCipher;
using LMP.Core.Youtube.Music;
using LMP.Core.Youtube.Playlists;
using LMP.Core.Youtube.Search;
using LMP.Core.Youtube.Videos;

namespace LMP.Core.Youtube;

/// <summary>
/// Фасад над всеми субклиентами YouTube InnerTube API.
/// </summary>
public sealed class YoutubeClient : IDisposable
{
    private readonly HttpClient _youtubeHttp;
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// Инициализирует клиент YouTube с внедрением всех необходимых зависимостей.
    /// </summary>
    /// <param name="http">Настроенный HTTP-клиент с обработчиками YouTube.</param>
    /// <param name="nTokenDecryptor">Провайдер расшифровки N-Token.</param>
    /// <param name="sigCipherDecryptor">Провайдер расшифровки подписи потока.</param>
    /// <param name="isAuthenticatedCheck">Callback проверки наличия активной сессии.</param>
    /// <param name="poTokenProvider">
    /// Провайдер PoToken для подписи videoplayback URL.
    /// <c>null</c> отключает добавление параметра <c>pot</c>.
    /// </param>
    /// <param name="ownsHttpClient">
    /// <c>true</c> — <see cref="Dispose"/> уничтожит <paramref name="http"/>.
    /// </param>
    public YoutubeClient(
        HttpClient http,
        NTokenDecryptor nTokenDecryptor,
        SigCipherDecryptor sigCipherDecryptor,
        Func<bool>? isAuthenticatedCheck = null,
        PoTokenProvider? poTokenProvider = null,
        bool ownsHttpClient = false)
    {
        _youtubeHttp = http;
        _ownsHttpClient = ownsHttpClient;

        Videos = new VideoClient(
            _youtubeHttp,
            nTokenDecryptor,
            sigCipherDecryptor,
            isAuthenticatedCheck,
            poTokenProvider);

        Playlists = new PlaylistClient(_youtubeHttp);
        Search = new SearchClient(_youtubeHttp);
        Music = new MusicClient(_youtubeHttp);
        Mutations = new PlaylistMutationController(_youtubeHttp);
        Sync = new PlaylistSyncController(_youtubeHttp);
    }

    /// <summary>Клиент для работы с видео и аудио-потоками.</summary>
    public VideoClient Videos { get; }

    /// <summary>Клиент для работы с плейлистами.</summary>
    public PlaylistClient Playlists { get; }

    /// <summary>Клиент для поиска.</summary>
    public SearchClient Search { get; }

    /// <summary>Клиент YouTube Music.</summary>
    public MusicClient Music { get; }

    /// <summary>
    /// Playlist mutations via WEB client (create, add, remove, rename, delete).
    /// </summary>
    internal PlaylistMutationController Mutations { get; }

    /// <summary>
    /// Playlist sync operations (user playlist listing, track fetching with setVideoId).
    /// </summary>
    internal PlaylistSyncController Sync { get; }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsHttpClient) _youtubeHttp.Dispose();
        GC.SuppressFinalize(this);
    }
}