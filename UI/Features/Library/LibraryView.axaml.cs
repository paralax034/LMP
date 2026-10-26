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
    /// Сохраняет акцентную обводку карточки плейлиста при открытии контекстного меню.
    /// </summary>
    /// <summary>
    /// Открывает общее контекстное меню для карточки плейлиста.
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
    /// Сохраняет акцентную обводку карточки плейлиста при открытии общего контекстного меню.
    /// </summary>
    private void OnPlaylistFlyoutOpened(object? sender, EventArgs e)
    {
        if (sender is MenuFlyout { Target.DataContext: PlaylistCardViewModel vm })
        {
            vm.IsMenuOpen = true;
        }
    }

    /// <summary>
    /// Снимает акцентную обводку после закрытия общего контекстного меню.
    /// </summary>
    private void OnPlaylistFlyoutClosed(object? sender, EventArgs e)
    {
        if (sender is MenuFlyout { Target.DataContext: PlaylistCardViewModel vm })
        {
            vm.IsMenuOpen = false;
        }
    }
}