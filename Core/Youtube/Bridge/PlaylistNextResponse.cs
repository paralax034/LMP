using System.Globalization;
using System.Text.Json;
using LMP.Core.Helpers.Extensions;

namespace LMP.Core.Youtube.Bridge;

/// <summary>
/// Представляет ответ API YouTube на запрос Next для плейлиста.
/// Чистая плоская DTO-модель: извлекает данные при создании и не удерживает <see cref="JsonDocument"/> в памяти.
/// </summary>
internal partial class PlaylistNextResponse : IPlaylistData
{
    /// <inheritdoc />
    public bool IsAvailable { get; init; }

    /// <inheritdoc />
    public string? Title { get; init; }

    /// <inheritdoc />
    public string? Author { get; init; }

    /// <inheritdoc />
    public string? ChannelId => null;

    /// <inheritdoc />
    public string? Description => null;

    /// <inheritdoc />
    public int? Count { get; init; }

    /// <inheritdoc />
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
            .GetPropertyOrNull("contents")
            ?.GetPropertyOrNull("twoColumnWatchNextResults")
            ?.GetPropertyOrNull("playlist")
            ?.GetPropertyOrNull("playlist");

        IsAvailable = contentRoot is not null;

        if (contentRoot is { } root)
        {
            Title = root.GetPropertyOrNull("title")?.GetStringOrNull();
            Author = root.GetPropertyOrNull("ownerName")?.GetPropertyOrNull("simpleText")?.GetStringOrNull();

            var text = root
                .GetPropertyOrNull("totalVideosText")
                ?.GetPropertyOrNull("runs")
                ?.GetArrayElementOrNull(0)
                ?.GetPropertyOrNull("text")
                ?.GetStringOrNull();

            if (text is not null && int.TryParse(text, CultureInfo.InvariantCulture, out var r1))
            {
                Count = r1;
            }
            else
            {
                text = root
                    .GetPropertyOrNull("videoCountText")
                    ?.GetPropertyOrNull("runs")
                    ?.GetArrayElementOrNull(2)
                    ?.GetPropertyOrNull("text")
                    ?.GetStringOrNull();

                if (text is not null && int.TryParse(text, CultureInfo.InvariantCulture, out var r2))
                    Count = r2;
            }

            var contents = root.GetPropertyOrNull("contents");
            if (contents is { ValueKind: JsonValueKind.Array } arr)
            {
                int len = arr.GetArrayLength();
                if (len > 0)
                {
                    var result = new List<PlaylistVideoData>(len);
                    for (int i = 0; i < len; i++)
                    {
                        var renderer = arr[i].GetPropertyOrNull("playlistPanelVideoRenderer");
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