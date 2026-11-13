namespace LMP.Core.Audio.Interfaces;

/// <summary>
/// Marker interface for all audio player commands.
/// </summary>
internal interface IAudioCommand
{
    /// <summary>
    /// Unique session ID to cancel obsolete commands.
    /// </summary>
    int SessionId { get; }
}

/// <summary>
/// Playback command.
/// </summary>
/// <param name="Descriptor">
/// Unified descriptor of a resolved audio stream containing URL, trackId, bitrate,
/// format, codec, contentLength, loudnessDb, and other metadata.
/// </param>
/// <param name="SessionId">Unique session ID.</param>
/// <param name="SeekPosition">
/// Seek position BEFORE playback starts (atomic seek-before-play).
/// null = start from the beginning of the track.
///
/// <para><b>Why:</b> When switching quality (SwitchQualityAsync), playback must
/// start from the current position rather than from the beginning. Without this field,
/// an audio artifact is audible between PlayAsync and SeekAsync — 16-300ms of sound from position 0.</para>
/// </param>
/// <param name="ExternalCancellationToken">
/// Cancellation token for the user playback session.
/// Allows instantly aborting the launch of an obsolete track if the user has already switched.</param>
internal sealed record PlayCommand(
    ResolvedStreamDescriptor Descriptor,
    int SessionId,
    TimeSpan? SeekPosition = null,
    CancellationToken ExternalCancellationToken = default,
    TaskCompletionSource<bool>? Completion = null) : IAudioCommand;

/// <summary>
/// Command to completely stop playback.
/// </summary>
/// <param name="SessionId">Unique session ID.</param>
internal sealed record StopCommand(int SessionId) : IAudioCommand;

/// <summary>
/// Command to pause playback.
/// </summary>
/// <param name="SessionId">Unique session ID.</param>
internal sealed record PauseCommand(int SessionId) : IAudioCommand;

/// <summary>
/// Command to resume playback after pause.
/// </summary>
/// <param name="SessionId">Unique session ID.</param>
internal sealed record ResumeCommand(int SessionId) : IAudioCommand;

/// <summary>
/// Command to seek to a new position within the current track.
/// </summary>
/// <param name="Position">Target position.</param>
/// <param name="SessionId">Unique session ID.</param>
/// <param name="SeekGeneration">
/// Monotonically increasing seek operation generation.
/// Used to discard obsolete deferred resume completions.
/// </param>
/// <param name="Completion">Optional completion source for awaitable seek.</param>
internal sealed record SeekCommand(
    TimeSpan Position,
    int SessionId,
    int SeekGeneration,
    TaskCompletionSource<bool>? Completion = null) : IAudioCommand;

/// <summary>
/// Command for deferred resume after seek-buffering.
/// </summary>
/// <param name="SessionId">Session in which the seek was initiated.</param>
/// <param name="SeekGeneration">Seek operation generation.</param>
/// <param name="Pipeline">Pipeline for which the resume was prepared.</param>
/// <param name="ThresholdReached">
/// <c>true</c> if the ring buffer reached the seek threshold;
/// <c>false</c> if waiting completed due to timeout and resume must be forced.
/// </param>
/// <param name="BufferedSamples">Actual sample count in the ring buffer when waiting completed.</param>
internal sealed record DeferredResumeCommand(
    int SessionId,
    int SeekGeneration,
    AudioPipeline Pipeline,
    bool ThresholdReached,
    int BufferedSamples) : IAudioCommand;

/// <summary>
/// Command to notify the actor loop about audio device loss.
/// </summary>
/// <param name="SessionId">Session at the time of device loss.</param>
/// <param name="Pipeline">Pipeline associated with the device.</param>
internal sealed record DeviceLostCommand(
    int SessionId,
    AudioPipeline Pipeline) : IAudioCommand;

/// <summary>
/// Command to notify the actor loop about audio device arrival.
/// </summary>
/// <param name="SessionId">Session at the time of device arrival.</param>
/// <param name="Pipeline">Pipeline awaiting device recovery.</param>
internal sealed record DeviceAvailableCommand(
    int SessionId,
    AudioPipeline Pipeline) : IAudioCommand;

/// <summary>
/// Command to deliver a background error to the actor loop.
/// </summary>
/// <param name="SessionId">Session in which the error occurred.</param>
/// <param name="Pipeline">Pipeline from which the error originated.</param>
/// <param name="Error">Error exception.</param>
internal sealed record PlayerErrorCommand(
    int SessionId,
    AudioPipeline Pipeline,
    Exception Error) : IAudioCommand;

/// <summary>
/// Command for final disposal of the player and its infrastructure.
/// </summary>
/// <param name="SessionId">Unique session ID.</param>
internal sealed record DisposeCommand(int SessionId) : IAudioCommand;

/// <summary>
/// Command for natural completion of the current track.
/// </summary>
/// <remarks>
/// <para>The command is published from the decoder loop to the actor channel instead of calling
/// <c>AudioPlayer.OnTrackEnded()</c> directly from a background thread.</para>
/// <para>This preserves single-threaded actor semantics for the player state machine and
/// eliminates late stale callbacks that could reset a newly created pipeline.</para>
/// </remarks>
/// <param name="SessionId">Session in which the track ended.</param>
internal sealed record TrackEndedCommand(int SessionId) : IAudioCommand;

/// <summary>
/// Command to recover an audio device after loss (BT disconnect, etc.).
/// The pipeline remains alive, while the backend is recreated via a retry loop.
/// </summary>
/// <param name="SessionId">Session at the time the command was sent.</param>
internal sealed record DeviceRecoveryCommand(int SessionId) : IAudioCommand;

/// <summary>
/// Command to notify the actor loop about critical audio buffer underrun (starvation).
/// </summary>
/// <remarks>
/// <para>Published from the backend fill thread via pipeline callback.
/// The actor transitions the player to Buffering state and triggers deferred resume,
/// instead of performing destructive CancelActiveReads that kills the only active HTTP request
/// on a slow network.</para>
/// </remarks>
/// <param name="SessionId">Session at the time starvation was detected.</param>
/// <param name="Pipeline">Pipeline in which starvation occurred.</param>
internal sealed record StarvationCommand(
    int SessionId,
    AudioPipeline Pipeline) : IAudioCommand;