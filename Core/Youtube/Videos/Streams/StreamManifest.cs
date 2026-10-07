namespace LMP.Core.Youtube.Videos.Streams;

/// <summary>
/// Describes media streams available for a YouTube video.
/// </summary>
public sealed class StreamManifest
{
    private readonly List<AudioOnlyStreamInfo> _audioOnlyStreams;
    private readonly List<IAudioStreamInfo> _audioStreams;

    /// <summary>
    /// Available streams.
    /// </summary>
    public IReadOnlyList<IStreamInfo> Streams { get; }

    /// <summary>
    /// Track-level integrated loudness in LUFS from YouTube <c>perceptualLoudnessDb</c>.
    /// <c>float.NaN</c> when absent.
    /// </summary>
    public float IntegratedLufs { get; }

    public StreamManifest(IReadOnlyList<IStreamInfo> streams, float integratedLufs = float.NaN)
    {
        Streams = streams;
        IntegratedLufs = integratedLufs;

        _audioOnlyStreams = new List<AudioOnlyStreamInfo>(streams.Count);
        _audioStreams = new List<IAudioStreamInfo>(streams.Count);

        for (int i = 0; i < streams.Count; i++)
        {
            var s = streams[i];
            if (s is AudioOnlyStreamInfo audioOnly)
            {
                _audioOnlyStreams.Add(audioOnly);
                _audioStreams.Add(audioOnly);
            }
            else if (s is IAudioStreamInfo audio)
            {
                _audioStreams.Add(audio);
            }
        }

        // Гарантируем строгий порядок по убыванию битрейта (Best Available идет первым).
        // Сортировка по месту (in-place) без LINQ исключает аллокации в Gen0.
        _audioOnlyStreams.Sort(static (a, b) => b.Bitrate.BitsPerSecond.CompareTo(a.Bitrate.BitsPerSecond));
    }

    /// <summary>
    /// Gets streams that contain audio (i.e. muxed and audio-only streams).
    /// </summary>
    public IReadOnlyList<IAudioStreamInfo> GetAudioStreams() => _audioStreams;

    /// <summary>
    /// Gets audio-only streams.
    /// </summary>
    public IReadOnlyList<AudioOnlyStreamInfo> GetAudioOnlyStreams() => _audioOnlyStreams;
}