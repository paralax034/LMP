using System.Diagnostics.CodeAnalysis;
using LMP.Core.Youtube.Videos.ClosedCaptions;

namespace LMP.Core.Youtube.Videos.Streams;

/// <summary>
/// Metadata associated with an audio-only YouTube media stream.
/// </summary>
public sealed class AudioOnlyStreamInfo(
    int itag,
    string url,
    Container container,
    FileSize size,
    Bitrate bitrate,
    string audioCodec,
    Language? audioLanguage,
    bool? isAudioLanguageDefault,
    bool hasEncryptedNToken,
    int audioChannels = 2
) : IAudioStreamInfo
{
    /// <inheritdoc />
    public int Itag { get; } = itag;

    /// <inheritdoc />
    public string Url { get; } = url;

    /// <inheritdoc />
    public Container Container { get; } = container;

    /// <inheritdoc />
    public FileSize Size { get; } = size;

    /// <inheritdoc />
    public Bitrate Bitrate { get; } = bitrate;

    /// <inheritdoc />
    public string AudioCodec { get; } = audioCodec;

    /// <summary>Количество аудиоканалов (1 = mono, 2 = stereo, 6 = 5.1 surround).</summary>
    public int AudioChannels { get; } = audioChannels;

    /// <inheritdoc />
    public Language? AudioLanguage { get; } = audioLanguage;

    /// <inheritdoc />
    public bool? IsAudioLanguageDefault { get; } = isAudioLanguageDefault;

    /// <inheritdoc />
    public bool HasEncryptedNToken { get; } = hasEncryptedNToken;

    /// <inheritdoc />
    [ExcludeFromCodeCoverage]
    public override string ToString() =>
        AudioLanguage is not null
            ? $"Audio-only ({Container} | {AudioLanguage})"
            : $"Audio-only ({Container})";
}