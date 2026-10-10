using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LMP.UI.Controls;

namespace LMP.UI.Features.Queue;

public partial class QueueView : UserControl
{
    private TrackListControl? _trackList;

    public QueueView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTreeHandler;
        DetachedFromVisualTree += OnDetachedFromVisualTreeHandler;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // В PersistentPageHost страницы постоянно смонтированы с IsVisible=true.
        // Переход на страницу детектируется по активации IsHitTestVisible (свойство InputElement).
        if ((change.Property == IsHitTestVisibleProperty || change.Property == IsVisibleProperty)
            && change.GetNewValue<bool>())
        {
            Dispatcher.UIThread.Post(ScrollToPlayingTrack, DispatcherPriority.Background);
        }
    }

    private void OnAttachedToVisualTreeHandler(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _trackList = this.FindControl<TrackListControl>("QueueTrackList");

        if (IsHitTestVisible)
        {
            Dispatcher.UIThread.Post(ScrollToPlayingTrack, DispatcherPriority.Background);
        }
    }

    private void OnDetachedFromVisualTreeHandler(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _trackList = null;
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