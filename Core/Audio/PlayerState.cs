using LMP.Core.Audio.Interfaces;

namespace LMP.Core.Audio;

/// <summary>
/// Состояния конечного автомата плеера.
/// Переходы строго определены в AudioPlayerStateMachine.
/// </summary>
public enum PlayerState
{
    /// <summary>Начальное состояние, ничего не загружено.</summary>
    Idle,

    /// <summary>Загрузка источника и инициализация декодера.</summary>
    Loading,

    /// <summary>Буферизация перед стартом воспроизведения.</summary>
    Buffering,

    /// <summary>Активное воспроизведение.</summary>
    Playing,

    /// <summary>Пауза.</summary>
    Paused,

    /// <summary>Выполняется seek операция.</summary>
    Seeking,

    /// <summary>Критическая ошибка, требуется Stop.</summary>
    Error,

    /// <summary>Плеер уничтожен.</summary>
    Disposed
}

/// <summary>
/// Допустимые переходы состояний.
/// </summary>
public static class PlayerStateTransitions
{
    public static bool CanTransition(PlayerState from, PlayerState to)
    {
        return (from, to) switch
        {
            (PlayerState.Idle, PlayerState.Loading) => true,
            (PlayerState.Idle, PlayerState.Disposed) => true,

            (PlayerState.Loading, PlayerState.Buffering) => true,
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
    /// Может ли команда быть принята в текущем состоянии.
    /// </summary>
    public static bool CanAcceptCommand(PlayerState state, IAudioCommand command)
    {
        if (state == PlayerState.Disposed)
            return command is DisposeCommand;

        return command switch
        {
            PlayCommand => true,
            StopCommand => state is not PlayerState.Idle,

            PauseCommand => state is PlayerState.Playing or PlayerState.Buffering or PlayerState.Seeking,
            ResumeCommand => state is PlayerState.Paused,

            SeekCommand => state is PlayerState.Playing or PlayerState.Paused or PlayerState.Buffering,

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