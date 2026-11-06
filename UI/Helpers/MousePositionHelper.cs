using Avalonia;
using Avalonia.Input;

namespace LMP.UI.Helpers;

/// <summary>
/// Global mouse cursor position tracker.
/// Registered once in MainWindow — all controls read without subscriptions.
/// </summary>
public static class MousePositionHelper
{
    private static Point _position;

    /// <summary>Last known cursor position in window coordinates.</summary>
    public static Point Position => _position;

    /// <summary>
    /// Binds the tracker to the root element of the window.
    /// Call once during MainWindow initialization.
    /// </summary>
    public static void Attach(Visual root)
    {
        if (root is InputElement input)
            input.PointerMoved += (_, e) => _position = e.GetPosition(root);
    }
}