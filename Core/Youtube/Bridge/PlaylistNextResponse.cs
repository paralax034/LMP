using System.Text.Json;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Youtube.Bridge;

/// <summary>
/// Represents YouTube API response for a playlist Next request.
/// Pure flat DTO model: extracts data upon creation and does not retain <see cref="JsonDocument"/> in memory.
/// </summary>
internal partial class PlaylistNextResponse : IPlaylistData
{
    public bool IsAvailable { get; init; }
    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? ChannelId => null;
    public string? Description => null;
    public int? Count { get; init; }
    public IReadOnlyList<ThumbnailData> Thumbnails => Videos.Count > 0 ? Videos[0].Thumbnails : [];

    /// <summary>
    /// List of videos in the current batch of the Next response.
    /// </summary>
    public IReadOnlyList<PlaylistVideoData> Videos { get; init; } = [];

    /// <summary>
    /// User session data.
    /// </summary>
    public string? VisitorData { get; init; }

    /// <summary>
    /// Initializes a flat Next response model from a JSON element in a single pass.
    /// </summary>
    /// <param name="content">Root JSON element of the Next response.</param>
    public PlaylistNextResponse(JsonElement content)
    {
        var contentRoot = content
            .GetPropertyOrNull(InnerTubeTokens.Contents)
            ?.GetPropertyOrNull(InnerTubeTokens.TwoColumnWatchNextResults)
            ?.GetPropertyOrNull(InnerTubeTokens.Playlist)
            ?.GetPropertyOrNull(InnerTubeTokens.Playlist);

        IsAvailable = contentRoot is not null;

        if (contentRoot is { } root)
        {
            Title = root.GetPropertyOrNull(InnerTubeTokens.Title)?.GetStringOrNull();
            Author = root.GetPropertyOrNull(InnerTubeTokens.OwnerName)?.GetPropertyOrNull(InnerTubeTokens.SimpleText)?.GetStringOrNull();

            var totalText = root.GetPropertyOrNull(InnerTubeTokens.TotalVideosText)?.GetStringOrNull()
                ?? YoutubeParsingHelpers.ConcatTextRuns(root.GetPropertyOrNull(InnerTubeTokens.TotalVideosText)?.GetPropertyOrNull(InnerTubeTokens.Runs));

            var videoCountText = root.GetPropertyOrNull(InnerTubeTokens.VideoCountText)?.GetStringOrNull()
                ?? YoutubeParsingHelpers.ConcatTextRuns(root.GetPropertyOrNull(InnerTubeTokens.VideoCountText)?.GetPropertyOrNull(InnerTubeTokens.Runs));

            var parsedCount = YoutubeParsingHelpers.ParseLongFromText(totalText)
                ?? YoutubeParsingHelpers.ParseLongFromText(videoCountText);

            if (parsedCount.HasValue)
            {
                Count = (int)Math.Min(parsedCount.Value, int.MaxValue);
            }

            var contents = root.GetPropertyOrNull(InnerTubeTokens.Contents);
            if (contents is { ValueKind: JsonValueKind.Array } arr)
            {
                int len = arr.GetArrayLength();
                if (len > 0)
                {
                    var result = new List<PlaylistVideoData>(len);
                    for (int i = 0; i < len; i++)
                    {
                        var renderer = arr[i].GetPropertyOrNull(InnerTubeTokens.PlaylistPanelVideoRenderer);
                        if (renderer is not null)
                            result.Add(new PlaylistVideoData(renderer.Value));
                    }

                    if (result.Count > 0)
                        Videos = result;
                }
            }
        }

        VisitorData = content.GetVisitorData();
    }
}

internal partial class PlaylistNextResponse
{
    /// <summary>
    /// Parses playlist Next response from a string with immediate disposal of <see cref="JsonDocument"/>.
    /// </summary>
    public static PlaylistNextResponse Parse(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return new PlaylistNextResponse(doc.RootElement);
    }

    /// <summary>
    /// Parses playlist Next response directly from a network stream without intermediate strings in LOH.
    /// Disposes <see cref="JsonDocument"/> immediately after constructing the model.
    /// </summary>
    /// <param name="stream">HTTP response network stream.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>An instance of the flat playlist Next response.</returns>
    public static async ValueTask<PlaylistNextResponse> ParseAsync(Stream stream, CancellationToken ct = default)
    {
        using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
        return new PlaylistNextResponse(doc.RootElement);
    }
}