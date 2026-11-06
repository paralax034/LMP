using LMP.Core.Youtube.Bridge.NToken;
using LMP.Core.Youtube.Bridge.PoToken;
using LMP.Core.Youtube.Bridge.SigCipher;
using LMP.Core.Youtube.Music;
using LMP.Core.Youtube.Playlists;
using LMP.Core.Youtube.Search;
using LMP.Core.Youtube.Videos;

namespace LMP.Core.Youtube;

/// <summary>
/// Facade over all YouTube InnerTube API sub-clients.
/// </summary>
public sealed class YoutubeClient : IDisposable
{
    private readonly HttpClient _youtubeHttp;
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// Initializes the YouTube client with all required dependencies injected.
    /// </summary>
    /// <param name="http">Configured HTTP client with YouTube handlers.</param>
    /// <param name="nTokenDecryptor">N-Token decryption provider.</param>
    /// <param name="sigCipherDecryptor">Stream signature decryption provider.</param>
    /// <param name="isAuthenticatedCheck">Callback to check for an active session.</param>
    /// <param name="poTokenProvider">
    /// PoToken provider for signing videoplayback URLs.
    /// <c>null</c> disables adding the <c>pot</c> parameter.
    /// </param>
    /// <param name="ownsHttpClient">
    /// <c>true</c> if <see cref="Dispose"/> should dispose <paramref name="http"/>.
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

    /// <summary>Client for working with video and audio streams.</summary>
    public VideoClient Videos { get; }

    /// <summary>Client for working with playlists.</summary>
    public PlaylistClient Playlists { get; }

    /// <summary>Client for search.</summary>
    public SearchClient Search { get; }

    /// <summary>Client for YouTube Music.</summary>
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
    }
}