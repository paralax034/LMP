using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LMP.UI.Controls;

namespace LMP.UI.Features.Queue;

public partial class QueueView : UserControl
{
    private TrackListControl? _trackList;
    private EventHandler<AvaloniaPropertyChangedEventArgs>? _loadingChangedHandler;

    public QueueView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTreeHandler;
        DetachedFromVisualTree += OnDetachedFromVisualTreeHandler;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == Visual.IsVisibleProperty && change.GetNewValue<bool>())
        {
            Dispatcher.UIThread.Post(ScrollToPlayingTrack, DispatcherPriority.Loaded);
        }
    }

    private void OnAttachedToVisualTreeHandler(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _trackList = this.FindControl<TrackListControl>("QueueTrackList");
        if (_trackList == null) return;

        if (!_trackList.IsLoading)
        {
            Dispatcher.UIThread.Post(ScrollToPlayingTrack, DispatcherPriority.Loaded);
        }
        else
        {
            _loadingChangedHandler = (s, args) =>
            {
                if (args.Property == TrackListControl.IsLoadingProperty && _trackList != null && !_trackList.IsLoading)
                {
                    DetachLoadingHandler();
                    Dispatcher.UIThread.Post(ScrollToPlayingTrack, DispatcherPriority.Loaded);
                }
            };

            _trackList.PropertyChanged += _loadingChangedHandler;
        }
    }

    private void OnDetachedFromVisualTreeHandler(object? sender, VisualTreeAttachmentEventArgs e)
    {
        DetachLoadingHandler();
        _trackList = null;
    }

    private void DetachLoadingHandler()
    {
        if (_trackList != null && _loadingChangedHandler != null)
        {
            _trackList.PropertyChanged -= _loadingChangedHandler;
            _loadingChangedHandler = null;
        }
    }

    private void ScrollToPlayingTrack()
    {
        if (DataContext is not QueueViewModel vm) return;

        var trackList = _trackList ?? this.FindControl<TrackListControl>("QueueTrackList");
        if (trackList == null) return;

        var playingTrackId = vm.CurrentPlayingTrack?.Id;
        if (string.IsNullOrEmpty(playingTrackId)) return;

        int visualIndex = vm.QueueItems.IndexOfTrackId(playingTrackId);
        if (visualIndex >= 0)
        {
            trackList.ScrollToTrackIndex(visualIndex, smooth: false);
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
