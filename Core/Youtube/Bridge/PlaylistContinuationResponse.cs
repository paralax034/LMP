using System.Text.Json;
using LMP.Core.Youtube.Utils;
using LMP.Core.Helpers.Extensions;

namespace LMP.Core.Youtube.Bridge;

/// <summary>
/// Представляет ответ API YouTube на запрос продолжения списка видео (пагинация).
/// Плоская immutable-модель: не удерживает <see cref="JsonDocument"/> в памяти.
/// </summary>
internal partial class PlaylistContinuationResponse
{
    /// <summary>
    /// Список видео, полученных в текущей итерации пагинации.
    /// </summary>
    public IReadOnlyList<PlaylistVideoData> Videos { get; init; } = [];

    /// <summary>
    /// Токен для получения следующей страницы, если она существует.
    /// </summary>
    public string? ContinuationToken { get; init; }

    /// <summary>
    /// Данные о сессии пользователя.
    /// </summary>
    public string? VisitorData { get; init; }

    /// <summary>
    /// Создает экземпляр ответа пагинации, извлекая данные из JSON-элемента за один проход.
    /// </summary>
    /// <param name="content">Корневой JSON-элемент ответа продолжения.</param>
    public PlaylistContinuationResponse(JsonElement content)
    {
        var items = content
            .GetPropertyOrNull("onResponseReceivedActions")
            ?.GetArrayElementOrNull(0)
            ?.GetPropertyOrNull("appendContinuationItemsAction")
            ?.GetPropertyOrNull("continuationItems");

        if (items is { ValueKind: JsonValueKind.Array } arr)
        {
            int len = arr.GetArrayLength();
            if (len > 0)
            {
                var result = new List<PlaylistVideoData>(len);
                for (int i = 0; i < len; i++)
                {
                    var renderer = arr[i].GetPropertyOrNull("playlistVideoRenderer");
                    if (renderer is not null)
                        result.Add(new PlaylistVideoData(renderer.Value));
                }

                if (result.Count > 0)
                    Videos = result;
            }
        }

        var actions = content.GetPropertyOrNull("onResponseReceivedActions");
        if (actions != null)
        {
            var appendAction = actions.Value.EnumerateArrayOrNull()?.FirstOrNull()
                ?.GetPropertyOrNull("appendContinuationItemsAction");

            if (appendAction != null)
            {
                var continuationItems = appendAction.Value.GetPropertyOrNull("continuationItems");
                if (continuationItems != null)
                {
                    ContinuationToken = BridgeUtils.FindTokenInContents(continuationItems.Value);
                }
            }
        }

        VisitorData = content.GetVisitorData();
    }
}

internal partial class PlaylistContinuationResponse
{
    /// <summary>
    /// Парсит ответ пагинации плейлиста из строки с мгновенным освобождением <see cref="JsonDocument"/>.
    /// </summary>
    public static PlaylistContinuationResponse Parse(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return new PlaylistContinuationResponse(doc.RootElement);
    }

    /// <summary>
    /// Парсит ответ пагинации плейлиста напрямую из сетевого потока без промежуточных строк в LOH.
    /// Освобождает <see cref="JsonDocument"/> сразу после формирования модели.
    /// </summary>
    /// <param name="stream">Сетевой поток HTTP-ответа.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Экземпляр ответа продолжения плейлиста.</returns>
    public static async ValueTask<PlaylistContinuationResponse> ParseAsync(Stream stream, CancellationToken ct = default)
    {
        using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
        return new PlaylistContinuationResponse(doc.RootElement);
    }
}