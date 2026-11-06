namespace LMP.Core.Audio.Normalization;

/// <summary>
/// Loudness normalization mode.
/// </summary>
public enum NormalizationMode
{
    /// <summary>Spotify style: quiet tracks are boosted, loud tracks are attenuated to target.</summary>
    Bidirectional,

    /// <summary>YouTube style: attenuation of loud tracks only (gain ≤ 1.0).</summary>
    DownwardOnly
}

/// <summary>
/// Loudness normalization configuration for a single pipeline.
/// </summary>
/// <param name="Enabled">Whether normalization is enabled.</param>
/// <param name="TargetLufs">Target loudness level in LUFS.</param>
/// <param name="MaxGain">Maximum gain multiplier.</param>
/// <param name="Mode">Normalization mode (Upward / Downward / Bidirectional).</param>
/// <param name="PreScanDurationMs">
/// EBU R128 pre-scan duration in milliseconds.
/// <list type="bullet">
///   <item>30,000 ms — for streaming and partial-cache (default).</item>
///   <item>60,000 ms — for full-cache (entire file available, accuracy prioritized).</item>
/// </list>
/// </param>
public readonly record struct NormalizationConfig(
    bool Enabled,
    float TargetLufs,
    float MaxGain,
    NormalizationMode Mode,
    int PreScanDurationMs = 30_000);