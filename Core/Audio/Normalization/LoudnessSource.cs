namespace LMP.Core.Audio.Normalization;

/// <summary>
/// Measurement source for track integrated loudness.
/// </summary>
/// <remarks>
/// The numerical order of values strictly corresponds to semantic priority:
/// a higher number = a more accurate / reliable source.
/// This property is used directly in the <c>existing &gt; incoming</c> comparison
/// in <see cref="TrackInfo.SetIntegratedLufs"/> and <see cref="AudioCacheManager.TryUpdateIntegratedLufs"/>.
///
/// <para>
/// Intentionally contains only two meaningful values:
/// <list type="bullet">
///   <item>
///     <b>EbuMeasured</b> — result of local EBU R128 analysis.
///     Accuracy depends on the amount of analyzed audio (prefixscan / full-scan),
///     but this is a measurement parameter, not a property of the source.
///     No need to distinguish "30 seconds" and "full file" at the enum level —
///     the pipeline selects the analysis window based on available data.
///   </item>
///   <item>
///     <b>YoutubePerceptual</b> — server-side loudness metadata from YouTube API.
///     Always takes precedence over <see cref="EbuMeasured"/> because it is measured
///     on the full track using YouTube's perceptual model.
///   </item>
/// </list>
/// </para>
/// </remarks>
public enum LoudnessSource
{
    /// <summary>Source is undefined. LUFS value is unreliable.</summary>
    Unknown = 0,

    /// <summary>
    /// EBU R128 measurement performed locally.
    /// Analysis window: 30s for streaming/partial-cache, 60s for full-cache.
    /// </summary>
    EbuMeasured = 1,

    /// <summary>
    /// Perceptual loudness from YouTube API metadata.
    /// Measured on the full track on the server side.
    /// Serves as the canonical value — overwrites <see cref="EbuMeasured"/>.
    /// </summary>
    YoutubePerceptual = 2
}