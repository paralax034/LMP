using LMP.Core.Audio.Normalization;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio;

/// <summary>
/// Audio player initialization settings.
/// </summary>
public sealed class AudioPlayerOptions
{
    /// <summary>
    /// Stream URL provider for source: primary continuation URL acquire for partial cache and refresh on 403.
    /// The request explicitly carries the track and stream variant of the initiating source.
    /// </summary>
    public StreamUrlProvider? UrlProvider { get; init; }

    /// <summary>
    /// Called when starvation is detected (200+ consecutive underruns).
    /// Used by AudioEngine to force a rebuild of HTTP clients.
    /// </summary>
    public Action? OnStarvationDetected { get; init; }

    /// <summary>UI position update notification interval (ms).</summary>
    public TimeSpan PositionUpdateInterval { get; init; } = TimeSpan.FromMilliseconds(DefaultPositionUpdateIntervalMs);

    /// <summary>Number of reconnect attempts on network errors.</summary>
    public int MaxRetryAttempts { get; init; } = AudioConstants.MaxRetryAttempts;

    /// <summary>Delay between reconnect attempts.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(RetryDelayMs);

    /// <summary>Use a stub instead of a real audio driver.</summary>
    public bool UseNullBackend { get; init; }

    /// <summary>Streaming configuration (network buffering settings).</summary>
    public StreamingConfig? StreamingConfig { get; init; }

    /// <summary>
    /// Callback determining whether a fast-replay of the current track is required
    /// upon natural completion (RepeatMode.One).
    /// If it returns <c>true</c>, the player performs a pipeline rewind without recreation.
    /// </summary>
    public Func<bool>? ShouldFastReplay { get; init; }

    /// <summary>
    /// Callback for pipeline configuration before opening the gate.
    /// </summary>
    /// <remarks>
    /// <para><b>Arguments:</b></para>
    /// <list type="bullet">
    ///   <item><c>AudioPipeline</c> — pipeline to configure.</item>
    ///   <item><c>string?</c> — trackId of the track for which the pipeline was created.
    ///     Passed from <see cref="AudioPlayer.HandlePlayAsync"/> via <c>cmd.Descriptor.TrackId</c>.
    ///     Guarantees gain binding to a specific pipeline during rapid next/prev.</item>
    /// </list>
    /// </remarks>
    public Action<AudioPipeline, string?>? OnPipelineConfiguring { get; set; }

    /// <summary>
    /// Callback for storing integrated loudness from EBU R128 analysis.
    /// Called after pre-scan or after completing the real-time phase (~3 sec).
    /// Arguments: trackId, integrated LUFS, source.
    /// </summary>
    public Action<string, float, LoudnessSource>? OnIntegratedLufsResolved { get; init; }
}