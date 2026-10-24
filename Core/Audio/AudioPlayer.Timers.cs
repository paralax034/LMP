namespace LMP.Core.Audio;

public sealed partial class AudioPlayer
{
    private Timer? _positionTimer;
    private Timer? _bufferTimer;
    private volatile bool _uiTimersSuspended;

    /// <summary>
    /// Приостанавливает или возобновляет работу таймеров оповещения UI-потока в фоновом режиме.
    /// При выходе из режима приостановки во время воспроизведения немедленно публикует актуальные позицию и состояние буфера.
    /// </summary>
    /// <param name="suspended">Признак перевода UI в фоновый режим.</param>
    public void SetUiTimersSuspended(bool suspended)
    {
        if (_disposed || _uiTimersSuspended == suspended) return;
        _uiTimersSuspended = suspended;

        if (suspended)
        {
            StopTimers();
        }
        else if (_state == PlayerState.Playing && _activePipeline != null)
        {
            StartTimers();
        }
    }

    /// <summary>Запускает таймеры позиции и буфера.</summary>
    private void StartTimers()
    {
        if (_uiTimersSuspended || _disposed) return;

        int interval = (int)_options.PositionUpdateInterval.TotalMilliseconds;

        if (_positionTimer == null)
            _positionTimer = new Timer(_ => _events.RaisePositionChanged(Position), null, 0, interval);
        else
            _positionTimer.Change(0, interval);

        if (_bufferTimer == null)
            _bufferTimer = new Timer(_ => RaiseBufferState(), null, 0, AudioConstants.BufferStateUpdateIntervalMs);
        else
            _bufferTimer.Change(0, AudioConstants.BufferStateUpdateIntervalMs);
    }

    /// <summary>Останавливает таймеры без dispose.</summary>
    private void StopTimers()
    {
        _positionTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _bufferTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Запускает position timer с задержкой первого tick (после seek).</summary>
    private void StartPositionTimerDelayed()
    {
        if (_uiTimersSuspended || _disposed) return;

        int interval = (int)_options.PositionUpdateInterval.TotalMilliseconds;

        if (_positionTimer == null)
            _positionTimer = new Timer(_ => _events.RaisePositionChanged(Position), null, interval, interval);
        else
            _positionTimer.Change(interval, interval);
    }

    /// <summary>Останавливает только position timer.</summary>
    private void StopPositionTimer()
    {
        _positionTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Полная финализация таймеров. Только при Dispose.</summary>
    private void DisposeTimers()
    {
        _positionTimer?.Dispose();
        _positionTimer = null;
        _bufferTimer?.Dispose();
        _bufferTimer = null;
    }

    /// <summary>
    /// Публикует текущее состояние буфера и сетевые метрики источника.
    /// </summary>
    /// <remarks>
    /// <para>Авто-resume после deferred seek выполняется НЕ здесь,
    /// а через <see cref="AwaitDeferredSeekBufferAndResumeAsync"/>,
    /// потому что backend buffer не отражает реальную готовность
    /// decoder/ring buffer при закрытом gate.</para>
    /// </remarks>
    private void RaiseBufferState()
    {
        var pipeline = _activePipeline;
        if (pipeline == null) return;

        var source = pipeline.Source;

        double speed = source is Sources.CachingStreamSource caching
            ? caching.EstimatedSpeedBytesPerSec
            : 0;

        double ping = source is Sources.CachingStreamSource c
            ? c.AveragePingMs
            : 0;

        _events.RaiseBufferState(new BufferState(
            source.BufferProgress,
            source.IsFullyBuffered,
            source.GetBufferedRanges(),
            speed,
            ping));
    }
}