using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;


namespace LMP.UI.Features.Library;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Сохраняет акцентную обводку карточки плейлиста при открытии контекстного меню.
    /// Вызывается после полного открытия меню, когда логические связи дерева уже выстроены.
    /// </summary>
    private void OnPlaylistContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu { PlacementTarget: Control target })
        {
            target.Classes.Add("menu-open");
        }
    }

    /// <summary>
    /// Снимает акцентную обводку после закрытия контекстного меню.
    /// </summary>
    private void OnPlaylistContextMenuClosed(object? sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu { PlacementTarget: Control target })
        {
            target.Classes.Remove("menu-open");
        }
    }
}