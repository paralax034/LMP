using LMP.Core.Youtube.Bridge;
using LMP.Core.Youtube.Bridge.NToken;
using LMP.Core.Youtube.Bridge.PoToken;
using LMP.Core.Youtube.Bridge.SigCipher;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Videos.Streams;

namespace LMP.Core.Youtube.Videos;

/// <summary>
/// Provides methods for retrieving YouTube video information.
/// </summary>
public sealed class VideoClient
{
    private readonly VideoController _controller;
    private readonly Func<bool>? _isAuthenticatedCheck;

    /// <summary>Audio stream management client.</summary>
    public StreamClient Streams { get; }

    /// <param name="http">HTTP client.</param>
    /// <param name="nTokenDecryptor">N-Token decryption provider.</param>
    /// <param name="sigCipherDecryptor">Signature decryption provider.</param>
    /// <param name="isAuthenticatedCheck">Authentication check callback.</param>
    /// <param name="poTokenProvider">PoToken provider; <c>null</c> disables pot.</param>
    public VideoClient(
        HttpClient http,
        NTokenDecryptor nTokenDecryptor,
        SigCipherDecryptor sigCipherDecryptor,
        Func<bool>? isAuthenticatedCheck = null,
        PoTokenProvider? poTokenProvider = null)
    {
        _controller = new VideoController(http, sigCipherDecryptor.PlayerManager);
        _isAuthenticatedCheck = isAuthenticatedCheck;
        Streams = new StreamClient(
            http, nTokenDecryptor, sigCipherDecryptor,
            isAuthenticatedCheck, poTokenProvider);
    }

    /// <summary>
    /// Returns track metadata by its identifier.
    /// </summary>
    /// <param name="videoId">YouTube video ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async ValueTask<TrackInfo> GetAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default)
    {
        var watchPage = await _controller
            .GetVideoWatchPageAsync(videoId, cancellationToken)
            .ConfigureAwait(false);

        var playerResponse = watchPage.PlayerResponse
                             ?? await GetPlayerResponseAsync(videoId, cancellationToken)
                                 .ConfigureAwait(false);

        var title = playerResponse.Title ?? "";
        var channelTitle = playerResponse.Author
                           ?? throw new YoutubeExplodeException("Failed to extract video author.");
        var channelId = playerResponse.ChannelId
                        ?? throw new YoutubeExplodeException("Failed to extract video channel ID.");

        var thumb = ThumbnailUtils.GetBestUrl(
            playerResponse.Thumbnails, videoId.Value);

        return new TrackInfo
        {
            Id = $"yt_{videoId.Value}",
            Title = title,
            Author = channelTitle,
            ChannelId = channelId,
            Duration = playerResponse.Duration ?? TimeSpan.Zero,
            ThumbnailUrl = thumb,
            Url = $"https://www.youtube.com/watch?v={videoId}",
            IsMusic = playerResponse.IsMusic,
        };
    }

    /// <summary>
    /// Returns PlayerResponse with a full client fallback chain and correct auth context.
    /// </summary>
    /// <param name="videoId">YouTube video ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal async ValueTask<PlayerResponse> GetPlayerResponseAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default)
    {
        bool isAuth = _isAuthenticatedCheck?.Invoke() ?? false;
        var (response, _) = await _controller.GetPlayerResponseWithFallbackAsync(
            videoId,
            cancellationToken,
            isAuthenticated: isAuth).ConfigureAwait(false);

        return response;
    }
}