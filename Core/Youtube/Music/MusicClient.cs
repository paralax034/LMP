using System.Text.Json;

namespace LMP.Core.Youtube.Music;

public sealed class MusicClient(HttpClient http)
{
    private readonly MusicController _controller = new(http);

    public async Task<List<TrackInfo>> GetLikedTracksAsync(
        CancellationToken cancellationToken = default)
    {
        var allTracks = new List<TrackInfo>(100);
        var response = await _controller.GetBrowseAsync(
            browseId: "VLLM", cancellationToken: cancellationToken);

        ProcessShelves(response.Shelves, allTracks);

        var continuation = response.ContinuationToken;
        int page = 1;

        while (!string.IsNullOrEmpty(continuation))
        {
            if (cancellationToken.IsCancellationRequested) break;

            response = await _controller.GetBrowseAsync(
                continuation: continuation, cancellationToken: cancellationToken);
            var prevCount = allTracks.Count;
            ProcessShelves(response.Shelves, allTracks);

            if (allTracks.Count == prevCount) break;

            continuation = response.ContinuationToken;
            page++;
            if (page > 50) break;
        }

        return allTracks;
    }

    public async Task<List<Playlist>> GetLibraryPlaylistsAsync(
       CancellationToken cancellationToken = default)
    {
        var response = await _controller.GetBrowseAsync(
            browseId: "FEmusic_liked_playlists", cancellationToken: cancellationToken);
        var result = new List<Playlist>();

        foreach (var shelf in response.Shelves)
        {
            foreach (var item in shelf.Items)
            {
                if (item.Type == "Playlist" && !string.IsNullOrEmpty(item.Id))
                {
                    string? bestThumb = ThumbnailUtils.GetBestUrl(item.Thumbnails);

                    result.Add(new Playlist
                    {
                        Id = string.Concat("yt_", item.Id),
                        YoutubeId = item.Id,
                        StoredName = item.Title,
                        // Используем локализацию
                        Author = item.Author ?? LocalizationService.Instance["Track_UnknownAuthor"],
                        ThumbnailUrl = bestThumb,
                        SyncMode = PlaylistSyncMode.TwoWaySync
                    });
                }
            }
        }
        return result;
    }

    private static void ProcessShelves(List<MusicShelf> shelves, List<TrackInfo> targetList)
    {
        for (int s = 0; s < shelves.Count; s++)
        {
            var items = shelves[s].Items;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.Type != "Song" || string.IsNullOrEmpty(item.Id))
                    continue;

                string bestThumb = ThumbnailUtils.GetBestUrl(item.Thumbnails)
                    ?? string.Concat("https://i.ytimg.com/vi/", item.Id, "/mqdefault.jpg");

                targetList.Add(new TrackInfo
                {
                    // Идентификатор передается без ручного добавления префикса "yt_",
                    // так как свойство Id класса TrackInfo гарантирует его автоматическое добавление.
                    Id = item.Id,
                    Title = item.Title,
                    Author = item.Author ?? item.Album ?? LocalizationService.Instance["Track_UnknownAuthor"],
                    Duration = item.Duration ?? TimeSpan.Zero,
                    ThumbnailUrl = bestThumb,
                    IsMusic = true,
                    IsLiked = true,
                    Url = string.Concat("https://www.youtube.com/watch?v=", item.Id)
                });
            }
        }
    }

    #region Track Actions

    public async Task LikeTrackAsync(
        string videoId, bool like, CancellationToken cancellationToken = default)
    {
        var endpoint = like ? "like/like" : "like/removelike";
        await _controller.SendLikeActionAsync(endpoint, videoId, cancellationToken);
    }

    #endregion

    #region Account

    /// <summary>
    /// Получает структуру переключателя аккаунтов со списком всех каналов бренда.
    /// </summary>
    public async Task<JsonElement> GetAccountSwitcherAsync(
        CancellationToken cancellationToken = default) =>
        await _controller.GetAccountSwitcherAsync(cancellationToken);

    public async Task<JsonElement> GetAccountMenuAsync(
        CancellationToken cancellationToken = default) =>
        await _controller.GetAccountMenuAsync(cancellationToken);

    #endregion
}