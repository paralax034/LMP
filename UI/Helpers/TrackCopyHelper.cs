namespace LMP.UI.Helpers;

/// <summary>
/// Хелпер для форматирования и копирования информации о треках в буфер обмена.
/// </summary>
public static class TrackCopyHelper
{
    /// <summary>
    /// Извлекает флаг зажатого Shift из параметра команды.
    /// </summary>
    public static bool ParseWithAuthorModifier(object? parameter)
    {
        return parameter switch
        {
            bool b => b,
            string s => bool.TryParse(s, out var b) && b,
            _ => false
        };
    }

    /// <summary>
    /// Форматирует один трек (Название или Название — Автор).
    /// </summary>
    public static string FormatTrack(TrackInfo track, bool withAuthor = false)
    {
        if (withAuthor && !string.IsNullOrWhiteSpace(track.Author))
        {
            return $"{track.Title} — {track.Author}";
        }

        return track.Title;
    }

    /// <summary>
    /// Форматирует список треков (каждый с новой строки).
    /// </summary>
    public static string FormatTracks(IEnumerable<TrackInfo> tracks, bool withAuthor = false)
    {
        return string.Join(Environment.NewLine, tracks.Select(t => FormatTrack(t, withAuthor)));
    }

    /// <summary>
    /// Копирует название одиночного трека в буфер обмена и показывает тост.
    /// </summary>
    public static async Task CopyTrackTitleAsync(TrackInfo? track, object? parameter = null)
    {
        if (track == null) return;

        bool withAuthor = ParseWithAuthorModifier(parameter);
        string text = FormatTrack(track, withAuthor);

        if (string.IsNullOrWhiteSpace(text)) return;

        await Clipboard.SetTextAsync(text).ConfigureAwait(false);

        CopyHintService.Instance.Show(
            LocalizationService.Instance["Track_Copied"],
            CopyHintKind.Success,
            null);
    }

    /// <summary>
    /// Копирует список треков в буфер обмена (одиночный или пачку с новой строки) и показывает тост.
    /// </summary>
    public static async Task CopyTrackTitlesAsync(IReadOnlyList<TrackInfo>? tracks, object? parameter = null)
    {
        if (tracks == null || tracks.Count == 0) return;

        bool withAuthor = ParseWithAuthorModifier(parameter);
        string text = tracks.Count == 1
            ? FormatTrack(tracks[0], withAuthor)
            : FormatTracks(tracks, withAuthor);

        if (string.IsNullOrWhiteSpace(text)) return;

        await Clipboard.SetTextAsync(text).ConfigureAwait(false);

        CopyHintService.Instance.Show(
            LocalizationService.Instance["Track_Copied"],
            CopyHintKind.Success,
            null);
    }
}
