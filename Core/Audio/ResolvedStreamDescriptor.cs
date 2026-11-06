using LMP.Core.Audio.Interfaces;

namespace LMP.Core.Audio;

/// <summary>
/// Unified immutable descriptor of a resolved audio stream.
/// Passed from the resolve layer to the audio source without metadata loss.
/// </summary>
public readonly record struct ResolvedStreamDescriptor()
{
    /// <summary>Track identifier (prefixed with <c>yt_</c>).</summary>
    public required string TrackId { get; init; }

    /// <summary>YouTube stream itag. 0 = unknown (cache-only path).</summary>
    public int Itag { get; init; }

    /// <summary>Container format (WebM, Mp4, Ogg).</summary>
    public required AudioFormat Format { get; init; }

    /// <summary>Audio codec (Opus, Aac).</summary>
    public required AudioCodec Codec { get; init; }

    /// <summary>Bitrate in kbps.</summary>
    public required int BitrateKbps { get; init; }

    /// <summary>Total content size in bytes. 0 = unknown.</summary>
    public long ContentLengthBytes { get; init; }

    /// <summary>
    /// Full videoplayback URL. Empty string for cache-only playback.
    /// </summary>
    public string Url { get; init; } = "";

    /// <summary>
    /// URL expiration time (UTC). <see cref="DateTime.MaxValue"/> if no expiration.
    /// </summary>
    public DateTime ExpireUtc { get; init; } = DateTime.MaxValue;

    /// <summary>CDN hostname.</summary>
    public string CdnHost { get; init; } = "";

    /// <summary>
    /// Track-level integrated loudness in LUFS from YouTube <c>perceptualLoudnessDb</c>.
    /// <see cref="float.NaN"/> = value missing.
    /// </summary>
    public float IntegratedLufs { get; init; } = float.NaN;

    /// <summary>Audio track language code.</summary>
    public string? LanguageCode { get; init; }

    /// <summary>Whether the audio track is the default language.</summary>
    public bool IsDefaultLanguage { get; init; }

    /// <summary>Source from which the descriptor was obtained.</summary>
    public required StreamSource Origin { get; init; }

    /// <summary><c>true</c> if <see cref="Url"/> is non-empty and valid for HTTP requests.</summary>
    public bool HasLiveUrl => !string.IsNullOrEmpty(Url);

    /// <summary><c>true</c> if the URL has expired.</summary>
    public bool IsExpired => ExpireUtc != DateTime.MaxValue
        && DateTime.UtcNow >= ExpireUtc.AddMinutes(-30);

    /// <summary><c>true</c> if the perceptual LUFS value is present.</summary>
    public bool HasPerceptualLufs => !float.IsNaN(IntegratedLufs) && float.IsFinite(IntegratedLufs);

    /// <summary><c>true</c> if the descriptor contains URL expiration information.</summary>
    public bool HasExpiry => ExpireUtc != DateTime.MaxValue;

    /// <summary>
    /// Returns a string representation of the descriptor.
    /// </summary>
    public override string ToString()
    {
        return $"track={TrackId}, origin={Origin}, format={Format}, codec={Codec}, " +
               $"bitrate={BitrateKbps}kbps, itag={Itag}, size={ContentLengthBytes}, " +
               $"liveUrl={HasLiveUrl}, host={CdnHost}, expiry={(HasExpiry ? ExpireUtc.ToString("O") : "none")}, " +
               $"lufs={(HasPerceptualLufs ? IntegratedLufs.ToString("F2") : "NaN")}, " +
               $"lang={LanguageCode ?? "-"}, defaultLang={IsDefaultLanguage}";
    }
}

/// <summary>
/// Source from which the <see cref="ResolvedStreamDescriptor"/> was obtained.
/// </summary>
public enum StreamSource
{
    /// <summary>Fresh YouTube InnerTube API response.</summary>
    YouTubeApi = 0,

    /// <summary>Persistent session cache (probe-validated URL).</summary>
    SessionCache = 1,

    /// <summary>Fully cached track on disk.</summary>
    DiskCacheFull = 2,

    /// <summary>Partial disk cache (partial cache fast start).</summary>
    DiskCachePartial = 3,

    /// <summary>YoutubeProvider in-memory cache.</summary>
    ProviderMemoryCache = 4
}