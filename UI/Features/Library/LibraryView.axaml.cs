using Avalonia.Controls;
using Avalonia.Input;

namespace LMP.UI.Features.Library;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Preserves the accent border of the playlist card when opening the context menu.
    /// </summary>
    /// <summary>
    /// Opens the general context menu for the playlist card.
    /// </summary>
    private void OnCardContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control cardControl) return;

        if (Resources.TryGetValue("SharedPlaylistCardFlyout", out var res) && res is MenuFlyout flyout)
        {
            flyout.ShowAt(cardControl, showAtPointer: true);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Preserves the accent border of the playlist card when opening the general context menu.
    /// </summary>
    private void OnPlaylistFlyoutOpened(object? sender, EventArgs e)
    {
        if (sender is MenuFlyout { Target.DataContext: PlaylistCardViewModel vm })
        {
            vm.IsMenuOpen = true;
        }
    }

    /// <summary>
    /// Removes the accent border after closing the general context menu.
    /// </summary>
    private void OnPlaylistFlyoutClosed(object? sender, EventArgs e)
    {
        if (sender is MenuFlyout { Target.DataContext: PlaylistCardViewModel vm })
        {
            vm.IsMenuOpen = false;
        }
    }
}