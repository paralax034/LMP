namespace LMP.UI.Helpers;

/// <summary>
/// Generates a tooltip for the system tray icon.
/// Unified format for Windows and non-Windows platforms (DRY).
/// 
/// <para><b>Track format:</b> <c>{AppName}: {TrackTitle} ({Volume}{VolumeEmoji})</c></para>
/// <para><b>No track format:</b> <c>{AppName}</c></para>
/// <para><b>Scroll format:</b> <c>{AppName}: {TrackTitle}\n{VolumeEmoji} {Volume}%</c></para>
/// </summary>
public static class TrayTooltipHelper
{
    /// <summary>
    /// Maximum tooltip length for Win32 NOTIFYICONDATAW.szTip.
    /// </summary>
    public const int MaxTooltipLength = 127;

    /// <summary>
    /// Generates the standard tray tooltip.
    /// </summary>
    /// <param name="track">Current track (null if nothing is playing)</param>
    /// <param name="volume">Current volume 0–100</param>
    /// <returns>Formatted tooltip string</returns>
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
    /// Generates a tooltip emphasizing volume (used during wheel scrolling).
    /// Volume is highlighted on a separate line for better readability.
    /// </summary>
    /// <param name="track">Current track (null if nothing is playing)</param>
    /// <param name="volume">Current volume 0–100</param>
    /// <returns>Formatted tooltip string with highlighted volume</returns>
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
    /// Returns a monochrome Unicode volume emoji.
    /// </summary>
    /// <param name="volume">Volume level 0–100</param>
    /// <returns>🔇 (mute), 🔈 (low), 🔉 (medium), 🔊 (high)</returns>
    public static string GetVolumeEmoji(int volume) => volume switch
    {
        0 => "🔇",
        <= 33 => "🔈",
        <= 66 => "🔉",
        _ => "🔊"
    };

    /// <summary>
    /// Truncates the string to <see cref="MaxTooltipLength"/> characters.
    /// Required for Win32 szTip (128 chars including null terminator).
    /// </summary>
    private static string Truncate(string text)
        => text.Length > MaxTooltipLength ? text[..MaxTooltipLength] : text;
}