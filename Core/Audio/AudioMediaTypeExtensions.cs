using LMP.Core.Audio.Interfaces;

namespace LMP.Core.Audio;

/// <summary>
/// Unified helper methods for displaying and normalizing audio formats and codecs.
/// </summary>
public static class AudioMediaTypeExtensions
{
    /// <summary>
    /// Returns the canonical container name for persistence and transport boundary.
    /// </summary>
    /// <param name="format">Audio format.</param>
    /// <returns>Canonical container name in lower-case.</returns>
    public static string ToContainerName(this AudioFormat format) => format switch
    {
        AudioFormat.WebM => "webm",
        AudioFormat.Mp4 => "mp4",
        AudioFormat.Ogg => "ogg",
        AudioFormat.Hls => "hls",
        _ => ""
    };

    /// <summary>
    /// Returns the display name of the format for UI.
    /// </summary>
    /// <param name="format">Audio format.</param>
    /// <returns>Human-readable format name.</returns>
    public static string ToDisplayName(this AudioFormat format) => format switch
    {
        AudioFormat.WebM => "WebM",
        AudioFormat.Mp4 => "MP4",
        AudioFormat.Ogg => "Ogg",
        AudioFormat.Hls => "HLS",
        _ => "Unknown"
    };

    /// <summary>
    /// Returns the display name of the codec for UI.
    /// </summary>
    /// <param name="codec">Audio codec.</param>
    /// <returns>Human-readable codec name.</returns>
    public static string ToDisplayName(this AudioCodec codec) => codec switch
    {
        AudioCodec.Opus => "Opus",
        AudioCodec.Aac => "AAC",
        AudioCodec.Vorbis => "Vorbis",
        _ => "Unknown"
    };

    /// <summary>
    /// Normalizes the string representation of a codec to <see cref="AudioCodec"/>.
    /// </summary>
    /// <param name="value">String representation of the codec.</param>
    /// <param name="fallbackFormat">Format used for fallback codec resolution.</param>
    /// <returns>Normalized codec.</returns>
    internal static AudioCodec ToAudioCodec(this string? value, AudioFormat fallbackFormat = AudioFormat.Unknown)
    {
        if (string.IsNullOrWhiteSpace(value))
            return AudioSourceFactory.GetCodecForFormat(fallbackFormat);

        if (value.Contains("opus", StringComparison.OrdinalIgnoreCase))
            return AudioCodec.Opus;

        if (value.Contains("vorbis", StringComparison.OrdinalIgnoreCase))
            return AudioCodec.Vorbis;

        if (value.Contains("aac", StringComparison.OrdinalIgnoreCase)
            || value.Contains("mp4a", StringComparison.OrdinalIgnoreCase))
        {
            return AudioCodec.Aac;
        }

        return Enum.TryParse<AudioCodec>(value, true, out var codec)
            ? codec
            : AudioSourceFactory.GetCodecForFormat(fallbackFormat);
    }
}