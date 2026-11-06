namespace LMP.Core.Models;

/// <summary>
/// Defines the behavior when the window close button is clicked.
/// </summary>
public enum CloseAction
{
    /// <summary>Close the application completely.</summary>
    Exit = 0,

    /// <summary>Minimize to the system tray; the application continues running.</summary>
    MinimizeToTray = 1,

    /// <summary>Ask every time.</summary>
    Ask = 2
}