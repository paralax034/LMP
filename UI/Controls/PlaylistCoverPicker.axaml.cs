using Avalonia.Controls;

namespace LMP.UI.Controls;

/// <summary>
/// A control for selecting a playlist cover from track covers.
/// Displays a virtualized grid and a mosaic preview.
/// </summary>
public partial class PlaylistCoverPicker : UserControl
{
    public PlaylistCoverPicker()
    {
        InitializeComponent();
    }
}