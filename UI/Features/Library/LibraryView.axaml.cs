using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LMP.UI.Features.Library;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Сохраняет акцентную обводку карточки плейлиста при открытии контекстного меню.
    /// </summary>
    private void OnPlaylistContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu { DataContext: PlaylistCardViewModel vm })
        {
            vm.IsMenuOpen = true;
        }
    }

    /// <summary>
    /// Снимает акцентную обводку после закрытия контекстного меню.
    /// </summary>
    private void OnPlaylistContextMenuClosed(object? sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu { DataContext: PlaylistCardViewModel vm })
        {
            vm.IsMenuOpen = false;
        }
    }
}