namespace LMP.Core.Audio;

/// <summary>
/// All audio player events.
/// </summary>
internal sealed class AudioPlayerEvents
{
    /// <summary>Playback position changed.</summary>
    public event Action<TimeSpan>? PositionChanged;

    /// <summary>State changed (Playing, Paused, etc).</summary>
    public event Action<PlaybackState>? StateChanged;

    /// <summary>Track ended naturally.</summary>
    public event Action? TrackEnded;

    /// <summary>An error occurred.</summary>
    public event Action<AudioPlayerError>? ErrorOccurred;

    /// <summary>Stream information became available or updated.</summary>
    public event Action<AudioStreamInfo>? StreamInfoChanged;

    /// <summary>Buffering progress changed.</summary>
    public event Action<BufferState>? BufferStateChanged;

    /// <summary>Seek completed.</summary>
    public event Action<TimeSpan>? SeekCompleted;

    /// <summary>
    /// Audio device disconnected during playback.
    /// Player automatically paused. Pipeline remains alive, position preserved.
    /// Used for displaying an informational toast.
    /// </summary>
    public event Action? DeviceLost;

    /// <summary>
    /// Audio device restored after loss — playback resumed automatically.
    /// Used for displaying an informational toast.
    /// </summary>
    public event Action? DeviceRestored;

    // Internal raise methods
    internal void RaisePositionChanged(TimeSpan pos) => PositionChanged?.Invoke(pos);
    internal void RaiseStateChanged(PlaybackState state) => StateChanged?.Invoke(state);
    internal void RaiseTrackEnded() => TrackEnded?.Invoke();
    internal void RaiseError(AudioPlayerError error) => ErrorOccurred?.Invoke(error);
    internal void RaiseStreamInfo(AudioStreamInfo info) => StreamInfoChanged?.Invoke(info);
    internal void RaiseBufferState(BufferState state) => BufferStateChanged?.Invoke(state);
    internal void RaiseSeekCompleted(TimeSpan pos) => SeekCompleted?.Invoke(pos);

    /// <summary>Publishes a device loss event (informational, not an error).</summary>
    internal void RaiseDeviceLost() => DeviceLost?.Invoke();

    /// <summary>Publishes a device restoration event (informational).</summary>
    internal void RaiseDeviceRestored() => DeviceRestored?.Invoke();
}

internal readonly record struct AudioPlayerError(string Message, Exception? Exception = null);

public readonly record struct BufferState(
    double Progress,           // 0-100%
    bool IsFullyBuffered,
    IReadOnlyList<(double Start, double End)> Ranges,
    double SpeedBytesPerSec = 0,
    double AveragePingMs = 0);