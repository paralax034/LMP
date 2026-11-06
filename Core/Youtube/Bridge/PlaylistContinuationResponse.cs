using System.Text.Json;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Youtube.Bridge;

/// <summary>
/// Represents a YouTube API response for a video list continuation request (pagination).
/// Flat immutable model: does not retain <see cref="JsonDocument"/> in memory.
/// </summary>
internal partial class PlaylistContinuationResponse
{
    /// <summary>
    /// List of videos retrieved in the current pagination iteration.
    /// </summary>
    public IReadOnlyList<PlaylistVideoData> Videos { get; init; } = [];

    /// <summary>
    /// Token for fetching the next page, if one exists.
    /// </summary>
    public string? ContinuationToken { get; init; }

    /// <summary>
    /// User session data.
    /// </summary>
    public string? VisitorData { get; init; }

    /// <summary>
    /// Creates a pagination response instance by extracting data from the JSON element in a single pass.
    /// </summary>
    /// <param name="content">Root JSON element of the continuation response.</param>
    public PlaylistContinuationResponse(JsonElement content)
    {
        var actions = content.GetPropertyOrNull(InnerTubeTokens.OnResponseReceivedActions);
        if (actions is { ValueKind: JsonValueKind.Array } actionsArr && actionsArr.GetArrayLength() > 0)
        {
            var firstAction = actionsArr[0];
            var continuationItems = firstAction
                .GetPropertyOrNull(InnerTubeTokens.AppendContinuationItemsAction)
                ?.GetPropertyOrNull(InnerTubeTokens.ContinuationItems);

            if (continuationItems is { ValueKind: JsonValueKind.Array } itemsArr)
            {
                int len = itemsArr.GetArrayLength();
                if (len > 0)
                {
                    var result = new List<PlaylistVideoData>(len);
                    for (int i = 0; i < len; i++)
                    {
                        var renderer = itemsArr[i].GetPropertyOrNull(InnerTubeTokens.PlaylistVideoRenderer);
                        if (renderer is not null)
                            result.Add(new PlaylistVideoData(renderer.Value));
                    }

                    if (result.Count > 0)
                        Videos = result;
                }

                ContinuationToken = BridgeUtils.FindTokenInContents(continuationItems.Value);
            }
        }

        VisitorData = content.GetVisitorData();
    }
}

internal partial class PlaylistContinuationResponse
{
    /// <summary>
    /// Parses a playlist pagination response from a string with immediate disposal of <see cref="JsonDocument"/>.
    /// </summary>
    public static PlaylistContinuationResponse Parse(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return new PlaylistContinuationResponse(doc.RootElement);
    }

    /// <summary>
    /// Parses a playlist pagination response directly from a network stream without intermediate string allocations in LOH.
    /// Disposes <see cref="JsonDocument"/> immediately after populating the model.
    /// </summary>
    /// <param name="stream">HTTP response network stream.</param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>A playlist continuation response instance.</returns>
    public static async ValueTask<PlaylistContinuationResponse> ParseAsync(Stream stream, CancellationToken ct = default)
    {
        using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
        return new PlaylistContinuationResponse(doc.RootElement);
    }
}