using System.Globalization;
using System.Text.Json;
using LMP.Core.Helpers.Extensions;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Youtube.Bridge;

/// <summary>
/// Плоская immutable-модель данных видеоролика внутри плейлиста YouTube.
/// Извлекает все поля при инициализации, не удерживая ссылку на <see cref="JsonElement"/> в памяти.
/// </summary>
internal sealed class PlaylistVideoData
{
    /// <summary>
    /// Порядковый номер видео в плейлисте (0-based индекс).
    /// </summary>
    public int? Index { get; init; }

    /// <summary>
    /// Идентификатор видеоролика YouTube (11 символов).
    /// </summary>
    public string? Id { get; init; }

    /// <summary>
    /// Текст заголовка видео.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Имя автора или канала, опубликовавшего видео.
    /// </summary>
    public string? Author { get; init; }

    /// <summary>
    /// Идентификатор канала автора видео.
    /// </summary>
    public string? ChannelId { get; init; }

    /// <summary>
    /// Длительность видеоролика.
    /// </summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>
    /// Список доступных миниатюр видеоролика.
    /// </summary>
    public IReadOnlyList<ThumbnailData> Thumbnails { get; init; } = [];

    /// <summary>
    /// Инициализирует плоскую модель трека из JSON-элемента <c>playlistVideoRenderer</c> или <c>playlistPanelVideoRenderer</c>.
    /// </summary>
    /// <param name="content">JSON-элемент видеоролика.</param>
    public PlaylistVideoData(JsonElement content)
    {
        Index = content
            .GetPropertyOrNull("navigationEndpoint")
            ?.GetPropertyOrNull("watchEndpoint")
            ?.GetPropertyOrNull("index")
            ?.GetInt32OrNull();

        Id = content.GetPropertyOrNull("videoId")?.GetStringOrNull();

        var titleEl = content.GetPropertyOrNull("title");
        Title = titleEl is null
            ? null
            : titleEl.Value.GetPropertyOrNull("simpleText")?.GetStringOrNull()
                ?? YoutubeParsingHelpers.ConcatTextRuns(titleEl.Value.GetPropertyOrNull("runs"));

        var authorDetails =
            content.GetPropertyOrNull("longBylineText")
                ?.GetPropertyOrNull("runs")
                ?.GetArrayElementOrNull(0)
            ?? content.GetPropertyOrNull("shortBylineText")
                ?.GetPropertyOrNull("runs")
                ?.GetArrayElementOrNull(0);

        Author = authorDetails?.GetPropertyOrNull("text")?.GetStringOrNull();

        ChannelId =
            authorDetails
                ?.GetPropertyOrNull("navigationEndpoint")
                ?.GetPropertyOrNull("browseEndpoint")
                ?.GetPropertyOrNull("browseId")
                ?.GetStringOrNull()
            // Some videos have multiple authors. Our current data model does not support that, so we only
            // extract the first one, since it's the channel that actually uploaded the video.
            ?? authorDetails
                ?.GetPropertyOrNull("navigationEndpoint")
                ?.GetPropertyOrNull("showDialogCommand")
                ?.GetPropertyOrNull("panelLoadingStrategy")
                ?.GetPropertyOrNull("inlineContent")
                ?.GetPropertyOrNull("dialogViewModel")
                ?.GetPropertyOrNull("customContent")
                ?.GetPropertyOrNull("listViewModel")
                ?.GetPropertyOrNull("listItems")
                ?.EnumerateArrayOrNull()
                ?.FirstOrNull()
                ?.GetPropertyOrNull("listItemViewModel")
                ?.GetPropertyOrNull("rendererContext")
                ?.GetPropertyOrNull("commandContext")
                ?.GetPropertyOrNull("onTap")
                ?.GetPropertyOrNull("innertubeCommand")
                ?.GetPropertyOrNull("browseEndpoint")
                ?.GetPropertyOrNull("browseId")
                ?.GetStringOrNull();

        var raw = content.GetPropertyOrNull("lengthSeconds")?.GetStringOrNull();
        if (raw is not null && double.TryParse(raw, CultureInfo.InvariantCulture, out var seconds))
        {
            Duration = TimeSpan.FromSeconds(seconds);
        }
        else
        {
            var simpleText = content
                .GetPropertyOrNull("lengthText")
                ?.GetPropertyOrNull("simpleText")
                ?.GetStringOrNull();

            if (simpleText is not null)
            {
                Duration = YoutubeClientUtils.DurationParser.Parse(simpleText);
            }
            else
            {
                var text = YoutubeParsingHelpers.ConcatTextRuns(
                    content.GetPropertyOrNull("lengthText")?.GetPropertyOrNull("runs"));
                Duration = text is not null ? YoutubeClientUtils.DurationParser.Parse(text) : null;
            }
        }

        var thumbsArray = content
            .GetPropertyOrNull("thumbnail")
            ?.GetPropertyOrNull("thumbnails");

        if (thumbsArray is { ValueKind: JsonValueKind.Array } arr)
        {
            int len = arr.GetArrayLength();
            if (len > 0)
            {
                var result = new ThumbnailData[len];
                for (int i = 0; i < len; i++)
                    result[i] = new ThumbnailData(arr[i]);
                Thumbnails = result;
            }
        }
    }
}