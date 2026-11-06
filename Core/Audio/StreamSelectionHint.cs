namespace LMP.Core.Audio;

/// <summary>
/// Strongly typed audio stream selection hint.
/// Combines the desired format and bitrate without a string state representation.
/// </summary>
/// <param name="Format">Desired audio container format.</param>
/// <param name="BitrateKbps">Desired bitrate in kbps.</param>
public readonly record struct StreamSelectionHint(AudioFormat? Format, int BitrateKbps)
{
    /// <summary>
    /// Gets a value indicating whether the container format is explicitly specified.
    /// </summary>
    public bool HasFormat => Format is { } value && value != AudioFormat.Unknown;

    /// <summary>
    /// Gets a value indicating whether the stream bitrate is explicitly specified.
    /// </summary>
    public bool HasBitrate => BitrateKbps > 0;

    /// <summary>
    /// Creates a strongly typed stream selection hint from the current track state and application settings.
    /// </summary>
    /// <param name="track">Runtime model of the music track.</param>
    /// <param name="rememberTrackFormat">Application setting flag indicating whether user format preferences should be retained.</param>
    /// <returns>An instance of the <see cref="StreamSelectionHint"/> struct.</returns>
    /// <exception cref="ArgumentNullException">Thrown when the provided track object is null.</exception>
    public static StreamSelectionHint FromTrack(TrackInfo track, bool rememberTrackFormat)
    {
        ArgumentNullException.ThrowIfNull(track);

        AudioFormat? format = track.TransientFormat;
        int bitrate = track.TransientBitrate;

        if (rememberTrackFormat)
        {
            format ??= track.PreferredFormat;

            if (bitrate <= 0)
                bitrate = track.PreferredBitrate;
        }

        if (format == AudioFormat.Unknown)
            format = null;

        return new StreamSelectionHint(format, bitrate);
    }
}