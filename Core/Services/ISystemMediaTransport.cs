namespace LMP.Core.Services;

/// <summary>
/// Platform-agnostic hardware/OS media transport command.
/// </summary>
public enum MediaTransportCommand : byte
{
    Play = 0,
    Pause = 1,
    PlayPause = 2,
    Stop = 3,
    Next = 4,
    Previous = 5,
    FastForward = 6,
    Rewind = 7,
    Seek = 8
}

/// <summary>
/// Lightweight metadata snapshot pushed to OS media overlays.
/// </summary>
public readonly record struct MediaTransportMetadata(
    string TrackId,
    string Title,
    string Artist);

/// <summary>
/// Transport capability flags synchronized with OS media overlays and AVRCP.
/// </summary>
public readonly record struct MediaTransportCapabilities(
    bool CanPlay,
    bool CanPause,
    bool CanStop,
    bool CanGoNext,
    bool CanGoPrevious,
    bool CanSeek);

/// <summary>
/// Coarse timeline anchor published on state transitions and completed seeks.
/// </summary>
public readonly record struct MediaTransportTimeline(
    TimeSpan Position,
    TimeSpan Duration);

/// <summary>
/// Platform abstraction for OS media transport controls (Windows SMTC, Linux MPRIS2).
/// </summary>
public interface ISystemMediaTransport : IDisposable
{
    /// <summary>Gets whether the platform transport is active and supported on the current OS.</summary>
    bool IsSupported { get; }

    /// <summary>Raised when the OS or hardware media device dispatches a playback command.</summary>
    event Action<MediaTransportCommand, TimeSpan>? CommandReceived;

    /// <summary>Binds the transport session to the main top-level window handle.</summary>
    void AttachWindow(nint windowHandle);

    /// <summary>Detaches and clears OS media controls before the native window handle is destroyed.</summary>
    void DetachWindow();

    /// <summary>Updates OS playback status (Playing, Paused, Loading/Changing, Stopped).</summary>
    void UpdateState(PlaybackState state);

    /// <summary>Updates enabled transport button flags.</summary>
    void UpdateCapabilities(in MediaTransportCapabilities capabilities);

    /// <summary>Updates displayed track metadata and optional local cover image path.</summary>
    void UpdateMetadata(in MediaTransportMetadata metadata, string? coverFilePath);

    /// <summary>Updates coarse timeline anchor (without high-frequency polling).</summary>
    void UpdateTimeline(in MediaTransportTimeline timeline);

    /// <summary>Clears active OS media metadata and resets transport status.</summary>
    void Clear();
}

/// <summary>
/// Zero-overhead no-op implementation used when no OS media transport backend is active.
/// </summary>
public sealed class NullSystemMediaTransport : ISystemMediaTransport
{
    public static readonly NullSystemMediaTransport Instance = new();

    private NullSystemMediaTransport() { }

    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public event Action<MediaTransportCommand, TimeSpan>? CommandReceived { add { } remove { } }

    /// <inheritdoc />
    public void AttachWindow(nint windowHandle) { }

    /// <inheritdoc />
    public void DetachWindow() { }

    /// <inheritdoc />
    public void UpdateState(PlaybackState state) { }

    /// <inheritdoc />
    public void UpdateCapabilities(in MediaTransportCapabilities capabilities) { }

    /// <inheritdoc />
    public void UpdateMetadata(in MediaTransportMetadata metadata, string? coverFilePath) { }

    /// <inheritdoc />
    public void UpdateTimeline(in MediaTransportTimeline timeline) { }

    /// <inheritdoc />
    public void Clear() { }

    /// <inheritdoc />
    public void Dispose() { }
}