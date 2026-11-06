namespace LMP.Core.Audio;

/// <summary>
/// Audio playback state
/// </summary>
public enum PlaybackState
{
    /// <summary>Stopped, nothing loaded</summary>
    Stopped,
    
    /// <summary>Loading and initialization</summary>
    Loading,
    
    /// <summary>Buffering data</summary>
    Buffering,
    
    /// <summary>Active playback</summary>
    Playing,
    
    /// <summary>Paused</summary>
    Paused,
    
    /// <summary>Playback error</summary>
    Error
}