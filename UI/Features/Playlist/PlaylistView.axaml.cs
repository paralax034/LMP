using Avalonia;
using Avalonia.Controls;
using LMP.UI.Controls;

namespace LMP.UI.Features.Playlist;

public partial class PlaylistView : UserControl
{
    private ScrollViewer? _scrollViewer;
    private AmbientBackdrop? _ambientBackdrop;
    private PlaylistViewModel? _currentVm;

    public PlaylistView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTreeHandler;
        DetachedFromVisualTree += OnDetachedFromVisualTreeHandler;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _currentVm = DataContext as PlaylistViewModel;
        if (_currentVm != null && _ambientBackdrop != null)
            _currentVm.TopGlowAttenuation = _ambientBackdrop.EffectiveTopFactor;
    }

    private void OnAttachedToVisualTreeHandler(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _ambientBackdrop?.TopGlowFactorChanged -= OnBackdropTopGlowFactorChanged;

        _ambientBackdrop = this.FindControl<AmbientBackdrop>("AmbientBackdrop");
        _scrollViewer = this.FindControl<ScrollViewer>("PlaylistScrollViewer");
        _currentVm = DataContext as PlaylistViewModel;

        if (_ambientBackdrop != null)
        {
            _ambientBackdrop.TopGlowFactorChanged += OnBackdropTopGlowFactorChanged;
            _ambientBackdrop.AttachScrollViewer(_scrollViewer);
            _currentVm?.TopGlowAttenuation = _ambientBackdrop.EffectiveTopFactor;
        }
    }

    private void OnBackdropTopGlowFactorChanged(double factor)
    {
        _currentVm?.TopGlowAttenuation = factor;
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
        _currentVm = null;
    }
}