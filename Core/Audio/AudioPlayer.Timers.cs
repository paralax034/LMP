namespace LMP.Core.Audio;

internal sealed partial class AudioPlayer
{
    private readonly Lock _timersLock = new();
    private Timer? _positionTimer;
    private Timer? _bufferTimer;
    private volatile bool _uiTimersSuspended;

    /// <summary>
    /// Suspends or resumes UI thread notification timers in background mode.
    /// Upon exiting suspended mode during playback, immediately publishes current position and buffer state.
    /// </summary>
    /// <param name="suspended">Indicates whether the UI is moved to background mode.</param>
    public void SetUiTimersSuspended(bool suspended)
    {
        if (_disposed || _uiTimersSuspended == suspended) return;
        _uiTimersSuspended = suspended;

        if (suspended)
        {
            StopTimers();
            return;
        }

        if (_activePipeline == null) return;

        _events.RaisePositionChanged(Position);
        RaiseBufferState();

        if (_state == PlayerState.Playing)
        {
            StartTimers();
        }
        else if (_state == PlayerState.Buffering)
        {
            StartBufferTimer();
        }
    }

    /// <summary>Starts position and buffer timers.</summary>
    private void StartTimers()
    {
        if (_uiTimersSuspended || _disposed) return;

        int interval = (int)_options.PositionUpdateInterval.TotalMilliseconds;

        lock (_timersLock)
        {
            if (_uiTimersSuspended || _disposed) return;

            if (_positionTimer == null)
                _positionTimer = new Timer(_ => _events.RaisePositionChanged(Position), null, 0, interval);
            else
                _positionTimer.Change(0, interval);
        }

        StartBufferTimer();
    }

    /// <summary>Starts or restarts the buffer state timer only.</summary>
    private void StartBufferTimer()
    {
        if (_uiTimersSuspended || _disposed) return;

        lock (_timersLock)
        {
            if (_uiTimersSuspended || _disposed) return;

            if (_bufferTimer == null)
                _bufferTimer = new Timer(_ => RaiseBufferState(), null, 0, AudioConstants.BufferStateUpdateIntervalMs);
            else
                _bufferTimer.Change(0, AudioConstants.BufferStateUpdateIntervalMs);
        }
    }

    /// <summary>Stops timers without dispose.</summary>
    private void StopTimers()
    {
        lock (_timersLock)
        {
            _positionTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _bufferTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    /// <summary>Starts the position timer with a delayed first tick (post-seek) and ensures the buffer timer is running.</summary>
    private void StartPositionTimerDelayed()
    {
        if (_uiTimersSuspended || _disposed) return;

        if (_state == PlayerState.Playing || (_state == PlayerState.Seeking && CurrentPlaybackIntent == PlaybackIntent.Play))
        {
            int interval = (int)_options.PositionUpdateInterval.TotalMilliseconds;

            lock (_timersLock)
            {
                if (!_uiTimersSuspended && !_disposed)
                {
                    if (_positionTimer == null)
                        _positionTimer = new Timer(_ => _events.RaisePositionChanged(Position), null, interval, interval);
                    else
                        _positionTimer.Change(interval, interval);
                }
            }
        }
        else
        {
            StopPositionTimer();
            _events.RaisePositionChanged(Position);
        }

        if (_state is PlayerState.Playing or PlayerState.Buffering)
        {
            StartBufferTimer();
        }
        else
        {
            RaiseBufferState();
        }
    }

    /// <summary>Stops the position timer only.</summary>
    private void StopPositionTimer()
    {
        lock (_timersLock)
        {
            _positionTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    /// <summary>Full timer finalization. Dispose only.</summary>
    private void DisposeTimers()
    {
        lock (_timersLock)
        {
            _positionTimer?.Dispose();
            _positionTimer = null;
            _bufferTimer?.Dispose();
            _bufferTimer = null;
        }
    }

    /// <summary>
    /// Publishes the current buffer state and source network metrics.
    /// </summary>
    /// <remarks>
    /// <para>Auto-resume after deferred seek is NOT executed here,
    /// but via <see cref="AwaitDeferredSeekBufferAndResumeAsync"/>,
    /// because the backend buffer does not reflect the actual readiness of the
    /// decoder/ring buffer when the gate is closed.</para>
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