using System.Text.Json;
using LMP.Core.Helpers.Extensions;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Youtube.Bridge;

/// <summary>
/// Представляет ответ API YouTube на запрос Next для плейлиста.
/// Чистая плоская DTO-модель: извлекает данные при создании и не удерживает <see cref="JsonDocument"/> в памяти.
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
    /// Список видео в текущей партии Next-ответа.
    /// </summary>
    public IReadOnlyList<PlaylistVideoData> Videos { get; init; } = [];

    /// <summary>
    /// Данные о сессии пользователя.
    /// </summary>
    public string? VisitorData { get; init; }

    /// <summary>
    /// Инициализирует плоскую модель Next-ответа из JSON-элемента за один проход.
    /// </summary>
    /// <param name="content">Корневой JSON-элемент ответа Next.</param>
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
    /// Парсит ответ Next плейлиста из строки с мгновенным освобождением <see cref="JsonDocument"/>.
    /// </summary>
    public static PlaylistNextResponse Parse(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return new PlaylistNextResponse(doc.RootElement);
    }

    /// <summary>
    /// Парсит ответ Next плейлиста напрямую из сетевого потока без промежуточных строк в LOH.
    /// Освобождает <see cref="JsonDocument"/> сразу после формирования модели.
    /// </summary>
    /// <param name="stream">Сетевой поток HTTP-ответа.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Экземпляр плоского ответа Next плейлиста.</returns>
    public static async ValueTask<PlaylistNextResponse> ParseAsync(Stream stream, CancellationToken ct = default)
    {
        using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
        return new PlaylistNextResponse(doc.RootElement);
    }
}