using System.Text.Json;
using LMP.Core.Helpers.Extensions;

namespace LMP.Core.Youtube.Bridge;

/// <summary>
/// Представляет легковесные метаданные миниатюры (URL и разрешение).
/// Реализован как readonly record struct для исключения аллокаций в управляемой куче.
/// </summary>
/// <param name="Url">Прямой URL изображения миниатюры.</param>
/// <param name="Width">Ширина изображения в пикселях.</param>
/// <param name="Height">Высота изображения в пикселях.</param>
internal readonly record struct ThumbnailData(string? Url, int? Width, int? Height)
{
    /// <summary>
    /// Извлекает данные миниатюры из JSON-элемента InnerTube API.
    /// </summary>
    /// <param name="content">JSON-элемент объекта миниатюры.</param>
    public ThumbnailData(JsonElement content)
        : this(
            content.GetPropertyOrNull("url")?.GetStringOrNull(),
            content.GetPropertyOrNull("width")?.GetInt32OrNull(),
            content.GetPropertyOrNull("height")?.GetInt32OrNull())
    {
    }
}