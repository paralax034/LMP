using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LMP.UI.Controls;

namespace LMP.UI.Features.Queue;

public partial class QueueView : UserControl
{
    private TrackListControl? _trackList;
    private ScrollViewer? _scrollViewer;
    private AmbientBackdrop? _ambientBackdrop;
    private QueueViewModel? _currentVm;

    public QueueView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTreeHandler;
        DetachedFromVisualTree += OnDetachedFromVisualTreeHandler;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // In PersistentPageHost, pages are permanently mounted with IsVisible=true.
        // Page navigation is detected by the activation of IsHitTestVisible (an InputElement property).
        if ((change.Property == IsHitTestVisibleProperty || change.Property == IsVisibleProperty)
            && change.GetNewValue<bool>())
        {
            Dispatcher.UIThread.Post(ScrollToPlayingTrackInstant, DispatcherPriority.Background);
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        AttachViewModel(DataContext as QueueViewModel);
    }

    private void OnAttachedToVisualTreeHandler(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _trackList = this.FindControl<TrackListControl>("QueueTrackList");

        if (_ambientBackdrop != null)
            _ambientBackdrop.TopGlowFactorChanged -= OnBackdropTopGlowFactorChanged;

        _ambientBackdrop = this.FindControl<AmbientBackdrop>("AmbientBackdrop");
        _scrollViewer = this.FindControl<ScrollViewer>("QueueScrollViewer");

        if (_ambientBackdrop != null)
        {
            _ambientBackdrop.TopGlowFactorChanged += OnBackdropTopGlowFactorChanged;
            _ambientBackdrop.AttachScrollViewer(_scrollViewer);
        }

        AttachViewModel(DataContext as QueueViewModel);

        if (_ambientBackdrop != null && _currentVm != null)
            _currentVm.TopGlowAttenuation = _ambientBackdrop.EffectiveTopFactor;

        if (IsHitTestVisible)
        {
            Dispatcher.UIThread.Post(ScrollToPlayingTrackInstant, DispatcherPriority.Background);
        }
    }

    private void OnBackdropTopGlowFactorChanged(double factor)
    {
        if (_currentVm != null)
            _currentVm.TopGlowAttenuation = factor;
    }

    private void OnDetachedFromVisualTreeHandler(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_ambientBackdrop != null)
        {
            _ambientBackdrop.TopGlowFactorChanged -= OnBackdropTopGlowFactorChanged;
            _ambientBackdrop.DetachScrollViewer();
        }

        _scrollViewer = null;
        _ambientBackdrop = null;
        AttachViewModel(null);
        _trackList = null;
    }

    private void AttachViewModel(QueueViewModel? vm)
    {
        if (ReferenceEquals(_currentVm, vm))
            return;

        if (_currentVm != null)
            _currentVm.ScrollToCurrentRequested -= OnScrollToCurrentRequested;

        _currentVm = vm;

        if (_currentVm != null)
            _currentVm.ScrollToCurrentRequested += OnScrollToCurrentRequested;
    }

    private void OnScrollToCurrentRequested() => ScrollToPlayingTrack(smooth: true);

    private void ScrollToPlayingTrackInstant() => ScrollToPlayingTrack(smooth: false);

    private void ScrollToPlayingTrack(bool smooth)
    {
        if (DataContext is not QueueViewModel vm) return;

        var trackList = _trackList ?? this.FindControl<TrackListControl>("QueueTrackList");
        if (trackList == null) return;

        var playingTrackId = vm.CurrentPlayingTrack?.Id;
        if (string.IsNullOrEmpty(playingTrackId)) return;

        int visualIndex = vm.QueueItems.IndexOfTrackId(playingTrackId);
        if (visualIndex >= 0)
        {
            trackList.ScrollToTrackIndex(visualIndex, smooth: smooth);
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}