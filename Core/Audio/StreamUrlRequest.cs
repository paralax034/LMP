namespace LMP.Core.Audio;

/// <summary>
/// Назначение запроса stream URL от source.
/// </summary>
public enum StreamUrlRequestKind
{
    /// <summary>Первичное получение continuation URL у partial-cache source без live URL.</summary>
    Acquire,

    /// <summary>Обновление URL, отвергнутого CDN (HTTP 403) или истёкшего.</summary>
    Refresh
}

/// <summary>
/// Неизменяемый явный запрос stream URL: какой трек, какой вариант потока и почему он нужен.
/// </summary>
/// <remarks>
/// Запрос создаёт сам source в момент обращения, поэтому поставщику не нужно угадывать инициатора
/// по активному pipeline или ambient-контексту. Запрос не удерживает ссылку на source:
/// фоновые задачи поставщика не продлевают жизнь RAM-буферов и дискового кэша.
/// </remarks>
/// <param name="Kind">Назначение запроса.</param>
/// <param name="TrackId">Идентификатор трека source.</param>
/// <param name="Format">Контейнер, байты которого уже лежат в RAM и дисковом кэше.</param>
/// <param name="BitrateKbps">Битрейт варианта (kbps).</param>
/// <param name="ContentLength">Полный размер варианта в байтах (<c>clen</c>); <c>0</c>, если неизвестен.</param>
/// <param name="CurrentUrl">URL source на момент запроса; пустая строка до получения continuation URL.</param>
/// <param name="IsForbiddenDriven">Запрос инициирован HTTP 403 от CDN на <paramref name="CurrentUrl"/>.</param>
public sealed record StreamUrlRequest(
    StreamUrlRequestKind Kind,
    string TrackId,
    AudioFormat Format,
    int BitrateKbps,
    long ContentLength,
    string CurrentUrl,
    bool IsForbiddenDriven)
{
    /// <summary>
    /// Возвращает подсказку выбора потока, адресующую ровно вариант source.
    /// </summary>
    public StreamSelectionHint ToSelectionHint() => new(Format, BitrateKbps);

    /// <summary>
    /// Проверяет, можно ли продолжить байты source вариантом-кандидатом (тот же контейнер и полный размер).
    /// </summary>
    /// <param name="format">Контейнер кандидата.</param>
    /// <param name="contentLength">Полный размер кандидата в байтах; <c>0</c>, если неизвестен.</param>
    /// <returns><c>true</c>, если кандидат совместим или размер одной из сторон неизвестен.</returns>
    public bool IsCompatible(AudioFormat format, long contentLength) =>
        format == Format
        && (ContentLength <= 0 || contentLength <= 0 || contentLength == ContentLength);
}

/// <summary>
/// Поставщик stream URL для source: единая точка для <see cref="StreamUrlRequestKind.Acquire"/>
/// и <see cref="StreamUrlRequestKind.Refresh"/>.
/// </summary>
/// <param name="request">Явный запрос source.</param>
/// <param name="ct">Токен отмены.</param>
/// <returns>Live URL совместимого варианта либо <c>null</c>, если URL получить не удалось.</returns>
public delegate ValueTask<string?> StreamUrlProvider(StreamUrlRequest request, CancellationToken ct);