namespace LMP.Core.Youtube.Bridge;

/// <summary>
/// Metadata interface for a raw YouTube media stream.
/// </summary>
internal interface IStreamData
{
    /// <summary>Format identifier (itag).</summary>
    int? Itag { get; }

    /// <summary>Direct or deciphered stream URL.</summary>
    string? Url { get; }

    /// <summary>Encrypted signature (when cipher is present).</summary>
    string? Signature { get; }

    /// <summary>Signature URL parameter name (usually sig or s).</summary>
    string? SignatureParameter { get; }

    /// <summary>Content size in bytes.</summary>
    long? ContentLength { get; }

    /// <summary>Stream bitrate in bps.</summary>
    long? Bitrate { get; }

    /// <summary>Media stream MIME type.</summary>
    string? MimeType { get; }

    /// <summary>Container format (webm, mp4, etc).</summary>
    string? Container { get; }

    /// <summary>Audio codec string (opus, mp4a.40.2, etc).</summary>
    string? AudioCodec { get; }

    /// <summary>Number of audio channels (1 = mono, 2 = stereo, 6 = 5.1 surround).</summary>
    int AudioChannels { get; }

    /// <summary>Audio track language code.</summary>
    string? AudioLanguageCode { get; }

    /// <summary>Audio track language display name.</summary>
    string? AudioLanguageName { get; }

    /// <summary>Indicates whether the audio track is the default language.</summary>
    bool? IsAudioLanguageDefault { get; }

    /// <summary>Video codec string.</summary>
    string? VideoCodec { get; }

    /// <summary>Video quality label (e.g., 1080p).</summary>
    string? VideoQualityLabel { get; }

    /// <summary>Frame width in pixels.</summary>
    int? VideoWidth { get; }

    /// <summary>Frame height in pixels.</summary>
    int? VideoHeight { get; }

    /// <summary>Video stream frame rate.</summary>
    int? VideoFramerate { get; }
}