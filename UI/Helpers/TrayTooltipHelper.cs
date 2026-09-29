namespace LMP.UI.Helpers;

/// <summary>
/// Формирует tooltip для иконки системного трея.
/// Единый формат для Windows и non-Windows платформ (DRY).
/// 
/// <para><b>Формат с треком:</b> <c>{AppName}: {TrackTitle} ({Volume}{VolumeEmoji})</c></para>
/// <para><b>Формат без трека:</b> <c>{AppName}</c></para>
/// <para><b>Формат при скролле:</b> <c>{AppName}: {TrackTitle}\n{VolumeEmoji} {Volume}%</c></para>
/// </summary>
public static class TrayTooltipHelper
{
    /// <summary>
    /// Максимальная длина tooltip для Win32 NOTIFYICONDATAW.szTip.
    /// </summary>
    public const int MaxTooltipLength = 127;

    /// <summary>
    /// Формирует стандартный tooltip трея.
    /// </summary>
    /// <param name="track">Текущий трек (null если ничего не играет)</param>
    /// <param name="volume">Текущая громкость 0–100</param>
    /// <returns>Отформатированная строка tooltip</returns>
    public static string Format(TrackInfo? track, int volume)
    {
        var appName = LocalizationService.Instance["Common_AppName"];

        if (track == null)
            return Truncate(appName);

        var emoji = GetVolumeEmoji(volume);
        var result = $"{appName}: {track.Title} ({volume}{emoji})";

        return Truncate(result);
    }

    /// <summary>
    /// Формирует tooltip с акцентом на громкости (используется при скролле колесиком).
    /// Громкость выделена на отдельной строке для лучшей читаемости.
    /// </summary>
    /// <param name="track">Текущий трек (null если ничего не играет)</param>
    /// <param name="volume">Текущая громкость 0–100</param>
    /// <returns>Отформатированная строка tooltip с выделенной громкостью</returns>
    public static string FormatWithVolumeAccent(TrackInfo? track, int volume)
    {
        var appName = LocalizationService.Instance["Common_AppName"];
        var emoji = GetVolumeEmoji(volume);

        var trackPart = track != null
            ? $"{appName}: {track.Title}"
            : appName;

        var result = $"{trackPart}\n{emoji} {volume}%";

        return Truncate(result);
    }

    /// <summary>
    /// Возвращает монохромный Unicode-эмодзи громкости.
    /// </summary>
    /// <param name="volume">Уровень громкости 0–100</param>
    /// <returns>🔇 (mute), 🔈 (low), 🔉 (medium), 🔊 (high)</returns>
    public static string GetVolumeEmoji(int volume) => volume switch
    {
        0 => "🔇",
        <= 33 => "🔈",
        <= 66 => "🔉",
        _ => "🔊"
    };

    /// <summary>
    /// Обрезает строку до <see cref="MaxTooltipLength"/> символов.
    /// Необходимо для Win32 szTip (128 chars включая null terminator).
    /// </summary>
    private static string Truncate(string text)
        => text.Length > MaxTooltipLength ? text[..MaxTooltipLength] : text;
}