using System.ComponentModel;

namespace LMP.Core.Services;

/// <summary>
/// Event-driven coordinator bridging <see cref="PlayerControlService"/> and <see cref="ImageCacheService"/>
/// with <see cref="ISystemMediaTransport"/> without background polling timers or bitmap decoding.
/// </summary>
public sealed class MediaTransportCoordinator : IDisposable
{
    private const int CoverDebounceMs = 100;
    private const long TimelineSyncIntervalMs = 5_000;
    private static readonly TimeSpan SeekStep = TimeSpan.FromSeconds(10);

    private readonly ISystemMediaTransport _transport;
    private readonly PlayerControlService _playerControl;
    private readonly ImageCacheService _imageCache;
    private readonly Lock _coverLock = new();
    private readonly Lock _publishLock = new();
    private readonly Lock _metadataLock = new();

    private readonly LibraryService? _library;

    private TrackInfo? _observedTrack;
    private CancellationTokenSource? _coverCts;
    private long _trackGeneration;
    private nint _windowHandle;
    private volatile bool _enabled = true;
    private volatile bool _subscribed;
    private bool _hasLastCapabilities;
    private MediaTransportCapabilities _lastCapabilities;
    private PlaybackState? _lastPublishedState;
    private bool _hasLastTimeline;
    private MediaTransportTimeline _lastTimeline;
    private long _lastTimelineSyncTick;
    private int _disposed;

    public MediaTransportCoordinator(
        ISystemMediaTransport transport,
        PlayerControlService playerControl,
        ImageCacheService imageCache,
        LibraryService? library = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(playerControl);
        ArgumentNullException.ThrowIfNull(imageCache);

        _transport = transport;
        _playerControl = playerControl;
        _imageCache = imageCache;
        _library = library;

        if (_library != null)
        {
            _enabled = _library.Settings.SystemMediaTransportEnabled;
            if (!_library.IsInitialized)
                _library.OnInitialized += HandleLibraryInitialized;
        }
    }

    private void HandleLibraryInitialized()
    {
        if (_library != null)
            SetEnabled(_library.Settings.SystemMediaTransportEnabled);
    }

    /// <summary>
    /// Enables or disables OS media transport integration on the fly without restarting the application.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        if (Volatile.Read(ref _disposed) != 0 || !_transport.IsSupported)
            return;

        lock (_publishLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || _enabled == enabled)
                return;

            _enabled = enabled;
            if (!enabled)
            {
                DetachTransportCoreLocked();
                Log.Info("[MediaTransport] Disabled by user setting");
            }
            else if (_windowHandle != 0)
            {
                _transport.AttachWindow(_windowHandle);
                EnsureSubscribedLocked();
                SyncAllLocked();
                Log.Info("[MediaTransport] Enabled by user setting");
            }
        }
    }

    /// <summary>
    /// Attaches the top-level window handle and starts event-driven synchronization when supported.
    /// </summary>
    public void AttachWindow(nint windowHandle)
    {
        if (Volatile.Read(ref _disposed) != 0 || !_transport.IsSupported || windowHandle == 0)
            return;

        lock (_publishLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            _windowHandle = windowHandle;
            if (!_enabled)
                return;

            _transport.AttachWindow(windowHandle);
            EnsureSubscribedLocked();
            SyncAllLocked();
        }
    }

    /// <summary>
    /// Detaches from the window handle before HWND teardown and stops synchronization.
    /// </summary>
    public void DetachWindow()
    {
        if (!_transport.IsSupported)
            return;

        lock (_publishLock)
        {
            _windowHandle = 0;
            DetachTransportCoreLocked();
        }
    }

    private void DetachTransportCoreLocked()
    {
        Interlocked.Increment(ref _trackGeneration);
        UnsubscribeLocked();
        CancelPendingCover();
        ClearTransportLocked();
        _transport.DetachWindow();
    }

    private void EnsureSubscribedLocked()
    {
        if (_subscribed || !_transport.IsSupported)
            return;

        _subscribed = true;
        _transport.CommandReceived += HandleTransportCommand;
        _playerControl.CurrentTrackChanged += HandleCurrentTrackChanged;
        _playerControl.PlaybackStateChanged += HandlePlaybackStateChanged;
        _playerControl.CanNavigateChanged += HandleCapabilitiesChanged;
        _playerControl.SeekCompleted += HandleSeekCompleted;
        _playerControl.PositionChanged += HandlePositionChanged;
    }

    private void UnsubscribeLocked()
    {
        if (!_subscribed)
            return;

        _subscribed = false;
        _hasLastCapabilities = false;
        _lastPublishedState = null;
        _hasLastTimeline = false;

        _transport.CommandReceived -= HandleTransportCommand;
        _playerControl.CurrentTrackChanged -= HandleCurrentTrackChanged;
        _playerControl.PlaybackStateChanged -= HandlePlaybackStateChanged;
        _playerControl.CanNavigateChanged -= HandleCapabilitiesChanged;
        _playerControl.SeekCompleted -= HandleSeekCompleted;
        _playerControl.PositionChanged -= HandlePositionChanged;

        AttachObservedTrackLocked(null);
    }

    private void SyncAllLocked()
    {
        var track = _playerControl.CurrentTrack;
        AttachObservedTrackLocked(track);
        PublishCapabilitiesLocked(track);
        PublishTrackMetadataLocked(track);
        PublishStateAndTimelineLocked(_playerControl.PlaybackState);
    }

    private void HandleCurrentTrackChanged(TrackInfo? track)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        lock (_publishLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_subscribed)
                return;

            _hasLastTimeline = false;
            AttachObservedTrackLocked(track);
            PublishCapabilitiesLocked(track);
            PublishTrackMetadataLocked(track);
            if (track != null)
                PublishTimelineIfChangedLocked(new MediaTransportTimeline(TimeSpan.Zero, track.Duration));
        }
    }

    private void HandlePlaybackStateChanged(PlaybackState state)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        lock (_publishLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_subscribed)
                return;

            PublishCapabilitiesLocked(_playerControl.CurrentTrack);
            PublishStateAndTimelineLocked(state);
        }
    }

    private void HandleCapabilitiesChanged()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        lock (_publishLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_subscribed)
                return;

            PublishCapabilitiesLocked(_playerControl.CurrentTrack);
        }
    }

    private void HandleSeekCompleted(TimeSpan position)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        lock (_publishLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_subscribed || _playerControl.CurrentTrack == null)
                return;

            PublishTimelineIfChangedLocked(new MediaTransportTimeline(position, _playerControl.TotalDuration));
        }
    }

    private void HandlePositionChanged(TimeSpan position)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref _lastTimelineSyncTick) < TimelineSyncIntervalMs)
            return;

        lock (_publishLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_subscribed)
                return;

            if (_playerControl.CurrentTrack == null || _playerControl.PlaybackState != PlaybackState.Playing)
                return;

            if (now - _lastTimelineSyncTick < TimelineSyncIntervalMs)
                return;

            PublishTimelineIfChangedLocked(new MediaTransportTimeline(position, _playerControl.TotalDuration));
        }
    }

    private void AttachObservedTrackLocked(TrackInfo? track)
    {
        if (ReferenceEquals(_observedTrack, track))
            return;

        _observedTrack?.PropertyChanged -= HandleObservedTrackPropertyChanged;

        _observedTrack = track;

        track?.PropertyChanged += HandleObservedTrackPropertyChanged;
    }

    private void HandleObservedTrackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0 || sender is not TrackInfo track)
            return;

        if (e.PropertyName is not (nameof(TrackInfo.Title) or nameof(TrackInfo.Author) or nameof(TrackInfo.ThumbnailUrl)))
            return;

        lock (_publishLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_subscribed || !ReferenceEquals(track, _observedTrack))
                return;

            PublishTrackMetadataLocked(track);
        }
    }

    private void PublishCapabilitiesLocked(TrackInfo? track)
    {
        bool hasTrack = track != null;
        bool canSeek = hasTrack
            && _playerControl.HasSeekableDuration
            && _playerControl.PlaybackState is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Buffering;

        var caps = new MediaTransportCapabilities(
            CanPlay: hasTrack,
            CanPause: hasTrack,
            CanStop: hasTrack,
            CanGoNext: hasTrack && _playerControl.CanGoNext,
            CanGoPrevious: hasTrack && _playerControl.CanGoPrevious,
            CanSeek: canSeek);

        if (_hasLastCapabilities && _lastCapabilities == caps)
            return;

        _lastCapabilities = caps;
        _hasLastCapabilities = true;
        _transport.UpdateCapabilities(caps);
    }

    private void PublishStateAndTimelineLocked(PlaybackState state)
    {
        bool stateChanged = _lastPublishedState != state;
        if (stateChanged)
        {
            _lastPublishedState = state;
            _transport.UpdateState(state);
        }

        if (_playerControl.CurrentTrack != null
            && state is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Stopped
                or PlaybackState.Loading or PlaybackState.Buffering)
        {
            TimeSpan position = state is PlaybackState.Loading
                ? TimeSpan.Zero
                : _playerControl.CurrentPosition;

            var timeline = new MediaTransportTimeline(
                position,
                _playerControl.TotalDuration);

            if (stateChanged || !_hasLastTimeline || _lastTimeline != timeline)
                PublishTimelineIfChangedLocked(timeline);
        }
    }

    private void PublishTimelineIfChangedLocked(in MediaTransportTimeline timeline)
    {
        if (_hasLastTimeline && _lastTimeline == timeline)
            return;

        _lastTimeline = timeline;
        _hasLastTimeline = true;
        Volatile.Write(ref _lastTimelineSyncTick, Environment.TickCount64);
        _transport.UpdateTimeline(timeline);
    }

    private void ClearTransportLocked()
    {
        lock (_metadataLock)
        {
            _transport.Clear();
        }
    }

    private void PublishTrackMetadataLocked(TrackInfo? track)
    {
        long generation = Interlocked.Increment(ref _trackGeneration);
        CancelPendingCover();

        if (track == null)
        {
            ClearTransportLocked();
            return;
        }

        var metadata = new MediaTransportMetadata(
            track.Id,
            track.Title ?? string.Empty,
            track.Author ?? string.Empty);

        string? url = track.ThumbnailUrl;
        if (string.IsNullOrEmpty(url))
        {
            _ = Task.Run(() => PublishMetadataIfCurrent(metadata, null, generation, CancellationToken.None));
            return;
        }

        var cts = new CancellationTokenSource();
        lock (_coverLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                cts.Dispose();
                return;
            }
            _coverCts = cts;
        }

        _ = Task.Run(() => ResolveCoverAndPublishAsync(metadata, url, generation, cts));
    }

    private async Task ResolveCoverAndPublishAsync(
        MediaTransportMetadata metadata,
        string url,
        long generation,
        CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || ct.IsCancellationRequested || Interlocked.Read(ref _trackGeneration) != generation)
                return;

            if (_imageCache.TryGetCachedDiskPath(url) is { } diskPath)
            {
                PublishMetadataIfCurrent(metadata, diskPath, generation, ct);
                return;
            }

            // Clear stale cover immediately while debouncing/downloading the new one
            PublishMetadataIfCurrent(metadata, null, generation, ct);

            if (!await ct.DelayNoThrowAsync(CoverDebounceMs, continueOnCapturedContext: false).ConfigureAwait(false))
                return;

            if (Volatile.Read(ref _disposed) != 0 || Interlocked.Read(ref _trackGeneration) != generation)
                return;

            string? coverPath = await _imageCache.EnsureCoverDiskPathAsync(url, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(coverPath))
                PublishMetadataIfCurrent(metadata, coverPath, generation, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Debug($"[MediaTransport] Cover update skipped: {ex.Message}");
        }
        finally
        {
            lock (_coverLock)
            {
                if (ReferenceEquals(_coverCts, cts))
                    _coverCts = null;
            }
            cts.Dispose();
        }
    }

    private void PublishMetadataIfCurrent(
        in MediaTransportMetadata metadata,
        string? coverFilePath,
        long expectedGeneration,
        CancellationToken ct)
    {
        lock (_metadataLock)
        {
            if (Volatile.Read(ref _disposed) != 0
                || ct.IsCancellationRequested
                || !_subscribed
                || Interlocked.Read(ref _trackGeneration) != expectedGeneration)
            {
                return;
            }

            _transport.UpdateMetadata(metadata, coverFilePath);
        }
    }

    private void CancelPendingCover()
    {
        CancellationTokenSource? cts;
        lock (_coverLock)
        {
            cts = _coverCts;
            _coverCts = null;
        }

        if (cts != null)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private void HandleTransportCommand(MediaTransportCommand command, TimeSpan seekPosition)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        try
        {
            _ = DispatchCommandSafeAsync(command, seekPosition);
        }
        catch (Exception ex)
        {
            Log.Warn($"[MediaTransport] Command dispatch '{command}' failed: {ex.Message}");
        }
    }

    private async Task DispatchCommandSafeAsync(MediaTransportCommand command, TimeSpan seekPosition)
    {
        try
        {
            switch (command)
            {
                case MediaTransportCommand.Play:
                    await _playerControl.PlayAsync().ConfigureAwait(false);
                    break;
                case MediaTransportCommand.Pause:
                    await _playerControl.PauseAsync().ConfigureAwait(false);
                    break;
                case MediaTransportCommand.PlayPause:
                    await _playerControl.PlayPauseAsync().ConfigureAwait(false);
                    break;
                case MediaTransportCommand.Stop:
                    _playerControl.StopPlaybackKeepTrack();
                    break;
                case MediaTransportCommand.Next:
                    await _playerControl.NextAsync().ConfigureAwait(false);
                    break;
                case MediaTransportCommand.Previous:
                    await _playerControl.PreviousAsync().ConfigureAwait(false);
                    break;
                case MediaTransportCommand.FastForward:
                    await _playerControl.SeekRelativeAsync(SeekStep).ConfigureAwait(false);
                    break;
                case MediaTransportCommand.Rewind:
                    await _playerControl.SeekRelativeAsync(-SeekStep).ConfigureAwait(false);
                    break;
                case MediaTransportCommand.Seek:
                    await _playerControl.SeekAsync(seekPosition).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[MediaTransport] Command '{command}' failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _library?.OnInitialized -= HandleLibraryInitialized;

        DetachWindow();
        _transport.Dispose();
    }
}