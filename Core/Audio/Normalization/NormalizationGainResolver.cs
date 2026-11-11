namespace LMP.Core.Audio.Normalization;

/// <summary>
/// The single source of truth for runtime normalization gain calculation.
/// <para>Stateless, pure function. Gain is calculated exclusively from <see cref="TrackInfo.IntegratedLufs"/>
/// and the current <see cref="NormalizationConfig"/>.</para>
/// </summary>
internal static class NormalizationGainResolver
{
    /// <summary>
    /// Resolves linear normalization gain according to the configuration.
    /// </summary>
    /// <param name="track">Track information.</param>
    /// <param name="config">Current normalization configuration.</param>
    /// <returns>
    /// Linear gain if the source is found; <c>float.NaN</c> if EBU R128 analysis is required.
    /// </returns>
    public static float Resolve(TrackInfo? track, NormalizationConfig config)
    {
        if (track == null || !config.Enabled || !track.HasIntegratedLufs)
            return float.NaN;

        return ComputeGainFromIntegratedLufs(track.IntegratedLufs, config);
    }

    /// <summary>
    /// Calculates linear gain from canonical integrated LUFS.
    /// </summary>
    /// <param name="integratedLufs">Track integrated loudness in LUFS.</param>
    /// <param name="config">Current normalization configuration.</param>
    /// <returns>Linear gain.</returns>
    public static float ComputeGainFromIntegratedLufs(float integratedLufs, NormalizationConfig config)
    {
        if (!config.Enabled || !float.IsFinite(integratedLufs))
            return float.NaN;

        float gainDb = config.TargetLufs - integratedLufs;
        float gain = MathF.Pow(10f, gainDb / 20f);
        return ApplyConstraints(gain, config);
    }

    /// <summary>
    /// Applies mode constraints and clamping to the computed gain.
    /// </summary>
    /// <param name="gain">Calculated linear gain.</param>
    /// <param name="config">Normalization configuration.</param>
    /// <returns>Gain with constraints applied.</returns>
    private static float ApplyConstraints(float gain, NormalizationConfig config)
    {
        if (config.Mode == NormalizationMode.DownwardOnly)
            gain = MathF.Min(gain, 1.0f);

        return Math.Clamp(gain, 0.1f, config.MaxGain);
    }
}