using LMP.Core.Audio.Interfaces;

namespace LMP.Core.Audio;

/// <summary>
/// Immutable information about the current audio stream.
/// Single source of truth for format/codec/bitrate.
/// </summary>
public sealed record AudioStreamInfo
{
    /// <summary>Empty instance for a missing stream.</summary>
    public static readonly AudioStreamInfo Empty = new();

    /// <summary>Track identifier.</summary>
    public string TrackId { get; init; } = "";

    /// <summary>Strongly-typed container format.</summary>
    public AudioFormat Format { get; init; }

    /// <summary>Strongly-typed audio codec.</summary>
    public AudioCodec CodecType { get; init; }

    /// <summary>Stream bitrate in kbps.</summary>
    public int Bitrate { get; init; }

    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate { get; init; }

    /// <summary>Number of channels.</summary>
    public int Channels { get; init; }

    /// <summary>Stream duration in milliseconds.</summary>
    public long DurationMs { get; init; }

    /// <summary><c>true</c> if the stream is played from local cache.</summary>
    public bool IsFromCache { get; init; }

    /// <summary>Container display name for UI.</summary>
    public string Container => Format.ToDisplayName();

    /// <summary>Codec display name for UI.</summary>
    public string Codec => CodecType.ToDisplayName();

    /// <summary><c>true</c> if the stream-info contains valid data.</summary>
    public bool IsValid => CodecType != AudioCodec.Unknown && Bitrate > 0;

    /// <summary>Formatted string for UI display of the stream format.</summary>
    public string FormatDisplay => IsValid
        ? $"{Format.ToDisplayName()}/{CodecType.ToDisplayName()}/{Bitrate:F0}kbps"
        : "Loading...";

    /// <summary>
    /// Creates an <see cref="AudioStreamInfo"/> from a <see cref="ResolvedStreamDescriptor"/>
    /// and decoder runtime parameters.
    /// </summary>
    /// <param name="descriptor">Resolved stream descriptor.</param>
    /// <param name="sampleRate">Sample rate from the decoder.</param>
    /// <param name="channels">Number of channels from the decoder.</param>
    /// <param name="durationMs">Duration from the source.</param>
    /// <param name="isFromCache">Source is full disk cache.</param>
    /// <returns>Strongly-typed stream information.</returns>
    internal static AudioStreamInfo FromDescriptor(
        ResolvedStreamDescriptor descriptor,
        int sampleRate,
        int channels,
        long durationMs,
        bool isFromCache)
    {
        return new AudioStreamInfo
        {
            TrackId = descriptor.TrackId,
            Format = descriptor.Format,
            CodecType = descriptor.Codec,
            Bitrate = descriptor.BitrateKbps,
            SampleRate = sampleRate,
            Channels = channels,
            DurationMs = durationMs,
            IsFromCache = isFromCache
        };
    }
}