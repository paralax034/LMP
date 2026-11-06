using Avalonia;

namespace LMP.UI.Services;

/// <summary>
/// Singleton service for displaying global toast notifications.
/// A single overlay across the entire application — MainWindow subscribes and manages the single Border.
/// Any VM or control calls Show() without knowledge of the UI tree.
/// </summary>
public sealed class CopyHintService
{
    public static readonly CopyHintService Instance = new();

    private CopyHintService() { }

    /// <summary>
    /// Raised when a hint needs to be shown.
    /// <paramref name="screenPosition"/> — cursor position in window coordinates,
    /// null = bottom center of the window.
    /// </summary>
    public event Action<string, CopyHintKind, Point?>? HintRequested;

    /// <summary>
    /// Recommended display duration for the notification in milliseconds.
    /// Increased to 4.5 seconds for comfortable reading of detailed instructions by the user.
    /// </summary>
    public int DisplayDurationMs { get; set; } = 4500;

    /// <summary>
    /// Show a toast notification.
    /// </summary>
    /// <param name="text">Hint text.</param>
    /// <param name="kind">Type: Success / Warning / Error.</param>
    /// <param name="screenPosition">Cursor position in window coordinates. Null = bottom center.</param>
    public void Show(string text, CopyHintKind kind = CopyHintKind.Success, Point? screenPosition = null)
        => HintRequested?.Invoke(text, kind, screenPosition);
}

/// <summary>
/// Visual hint type — determines the icon and accent color.
/// </summary>
public enum CopyHintKind
{
    /// <summary>Link copied — green checkmark.</summary>
    Success,
    /// <summary>No link / not linked to YouTube — orange triangle.</summary>
    Warning,
    /// <summary>Error — red cross.</summary>
    Error
}