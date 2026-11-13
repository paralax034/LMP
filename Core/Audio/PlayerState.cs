using LMP.Core.Audio.Interfaces;

namespace LMP.Core.Audio;

/// <summary>
/// State machine states of the player.
/// Transitions are strictly defined in AudioPlayerStateMachine.
/// </summary>
internal enum PlayerState
{
    /// <summary>Initial state, nothing is loaded.</summary>
    Idle,

    /// <summary>Loading source and initializing decoder.</summary>
    Loading,

    /// <summary>Buffering before playback starts.</summary>
    Buffering,

    /// <summary>Active playback.</summary>
    Playing,

    /// <summary>Paused.</summary>
    Paused,

    /// <summary>Seek operation in progress.</summary>
    Seeking,

    /// <summary>Critical error, requires Stop.</summary>
    Error,

    /// <summary>Player is disposed.</summary>
    Disposed
}

/// <summary>
/// Valid state transitions.
/// </summary>
internal static class PlayerStateTransitions
{
    public static bool CanTransition(PlayerState from, PlayerState to)
    {
        return (from, to) switch
        {
            (PlayerState.Idle, PlayerState.Loading) => true,
            (PlayerState.Idle, PlayerState.Disposed) => true,

            (PlayerState.Loading, PlayerState.Buffering) => true,
            (PlayerState.Loading, PlayerState.Paused) => true,
            (PlayerState.Loading, PlayerState.Error) => true,
            (PlayerState.Loading, PlayerState.Idle) => true,
            (PlayerState.Loading, PlayerState.Loading) => true,
            (PlayerState.Loading, PlayerState.Disposed) => true,

            (PlayerState.Buffering, PlayerState.Playing) => true,
            (PlayerState.Buffering, PlayerState.Error) => true,
            (PlayerState.Buffering, PlayerState.Idle) => true,
            (PlayerState.Buffering, PlayerState.Loading) => true,
            (PlayerState.Buffering, PlayerState.Paused) => true,
            (PlayerState.Buffering, PlayerState.Seeking) => true,
            (PlayerState.Buffering, PlayerState.Disposed) => true,

            (PlayerState.Playing, PlayerState.Buffering) => true,
            (PlayerState.Playing, PlayerState.Paused) => true,
            (PlayerState.Playing, PlayerState.Seeking) => true,
            (PlayerState.Playing, PlayerState.Idle) => true,
            (PlayerState.Playing, PlayerState.Loading) => true,
            (PlayerState.Playing, PlayerState.Error) => true,
            (PlayerState.Playing, PlayerState.Disposed) => true,

            (PlayerState.Paused, PlayerState.Playing) => true,
            (PlayerState.Paused, PlayerState.Seeking) => true,
            (PlayerState.Paused, PlayerState.Idle) => true,
            (PlayerState.Paused, PlayerState.Loading) => true,
            (PlayerState.Paused, PlayerState.Buffering) => true,
            (PlayerState.Paused, PlayerState.Error) => true,
            (PlayerState.Paused, PlayerState.Disposed) => true,

            (PlayerState.Seeking, PlayerState.Playing) => true,
            (PlayerState.Seeking, PlayerState.Buffering) => true,
            (PlayerState.Seeking, PlayerState.Paused) => true,
            (PlayerState.Seeking, PlayerState.Idle) => true,
            (PlayerState.Seeking, PlayerState.Loading) => true,
            (PlayerState.Seeking, PlayerState.Error) => true,
            (PlayerState.Seeking, PlayerState.Disposed) => true,

            (PlayerState.Error, PlayerState.Idle) => true,
            (PlayerState.Error, PlayerState.Loading) => true,
            (PlayerState.Error, PlayerState.Buffering) => true,
            (PlayerState.Error, PlayerState.Disposed) => true,

            _ => false
        };
    }

    /// <summary>
    /// Determines whether the command can be accepted in the current state.
    /// </summary>
    public static bool CanAcceptCommand(PlayerState state, IAudioCommand command)
    {
        if (state == PlayerState.Disposed)
            return command is DisposeCommand;

        return command switch
        {
            PlayCommand => true,
            StopCommand => state is not PlayerState.Idle,

            PauseCommand => state is PlayerState.Loading or PlayerState.Playing or PlayerState.Buffering or PlayerState.Seeking,
            ResumeCommand => state is PlayerState.Paused,

            SeekCommand => state is PlayerState.Playing or PlayerState.Paused or PlayerState.Buffering or PlayerState.Seeking,

            StarvationCommand => state is PlayerState.Playing or PlayerState.Buffering,

            DeferredResumeCommand => state is PlayerState.Buffering or PlayerState.Seeking or PlayerState.Paused or PlayerState.Playing,

            TrackEndedCommand => state is PlayerState.Buffering or PlayerState.Playing or PlayerState.Paused or PlayerState.Seeking,

            DeviceLostCommand => state is not (PlayerState.Idle or PlayerState.Disposed),
            DeviceAvailableCommand => state is not (PlayerState.Idle or PlayerState.Disposed),

            DeviceRecoveryCommand => state is PlayerState.Buffering or PlayerState.Paused or PlayerState.Error,

            PlayerErrorCommand => state is not PlayerState.Disposed,

            DisposeCommand => true,

            _ => false
        };
    }
}