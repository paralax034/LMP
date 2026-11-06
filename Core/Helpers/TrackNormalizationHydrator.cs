using System.Runtime.CompilerServices;
using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Normalization;

namespace LMP.Core.Helpers;

/// <summary>
/// Unified logic for transferring LUFS metadata from cache to the runtime track model.
/// </summary>
public static class TrackNormalizationHydrator
{
    /// <summary>
    /// Transfers integrated loudness from a cache entry to the track model.
    /// </summary>
    /// <remarks>
    /// Source priority is delegated to <see cref="TrackInfo.SetIntegratedLufs"/>,
    /// which uses the numerical order of <see cref="LoudnessSource"/>.
    /// This means:
    /// <list type="bullet">
    ///   <item>If the cache stores <see cref="LoudnessSource.YoutubePerceptual"/>,
    ///         and the track has <see cref="LoudnessSource.EbuMeasured"/> or <see cref="LoudnessSource.Unknown"/>,
    ///         the cached value will be applied (upgrade).</item>
    ///   <item>If the track already has <see cref="LoudnessSource.YoutubePerceptual"/>,
    ///         <see cref="LoudnessSource.EbuMeasured"/> from the cache will not overwrite it (guard).</item>
    /// </list>
    ///
    /// The <c>!track.HasIntegratedLufs</c> guard was intentionally removed:
    /// it was blocking upgrades from a higher-quality source in the cache.
    /// </remarks>
    /// <param name="track">Runtime track model.</param>
    /// <param name="entry">Cache entry with LUFS metadata.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void HydrateNormalization(TrackInfo track, AudioCacheEntry entry)
    {
        if (entry.IntegratedLufs is float lufs && float.IsFinite(lufs))
        {
            track.SetIntegratedLufs(lufs, (LoudnessSource)entry.IntegratedLufsSource);
        }
    }
}