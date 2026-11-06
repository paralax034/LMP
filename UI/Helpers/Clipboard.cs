using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace LMP.UI.Helpers;

/// <summary>
/// Service for interacting with the system clipboard.
/// 
/// <para><b>Why it is needed:</b></para>
/// <para>
/// Avalonia does not provide static access to the Clipboard; it is only accessible via Window.
/// This service encapsulates retrieving the Clipboard from the main window and provides
/// a convenient API for copying text.
/// </para>
/// 
/// <para><b>Usage:</b></para>
/// <code>
/// await _clipboard.SetTextAsync("https://youtube.com/...");
/// </code>
/// 
/// <para><b>Thread Safety:</b></para>
/// <para>
/// Methods are safe to call from any thread — automatic dispatching
/// to the UI thread occurs internally if required.
/// </para>
/// </summary>
public static class Clipboard
{
    /// <summary>
    /// Copies text to the system clipboard.
    /// </summary>
    /// <param name="text">The text to copy.</param>
    /// <returns>
    /// A Task that completes after successful copying.
    /// If the main window is unavailable, completes silently without errors.
    /// </returns>
    /// <remarks>
    /// <para>The method is safe to call even if the window has not been created yet or is already closed.</para>
    /// <para>Does not throw exceptions — logs errors internally.</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Copying a playlist link
    /// await _clipboard.SetTextAsync($"https://music.youtube.com/playlist?list={playlistId}");
    /// 
    /// // Showing a notification after copying
    /// await _notifications.ShowToastAsync(
    ///     titleKey: "Track_Copied",
    ///     messageKey: "Track_Copied",
    ///     severity: NotificationSeverity.Success);
    /// </code>
    /// </example>
    public static async Task SetTextAsync(string text)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                await SetTextInternalAsync(text);
            }
            else
            {
                await Dispatcher.UIThread.InvokeAsync(() => SetTextInternalAsync(text));
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Clipboard] SetTextAsync failed: {ex.Message}");
        }
    }

    private static async Task SetTextInternalAsync(string text)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = desktop.MainWindow;
            if (window?.Clipboard != null)
            {
                await window.Clipboard.SetTextAsync(text);
                Log.Debug($"[Clipboard] Copied {text.Length} chars");
            }
            else
            {
                Log.Warn("[Clipboard] MainWindow or Clipboard is null");
            }
        }
        else
        {
            Log.Warn("[Clipboard] Application lifetime is not desktop");
        }
    }

    /// <summary>
    /// Retrieves text from the system clipboard.
    /// </summary>
    /// <returns>
    /// The text from the clipboard, or <c>null</c> if the clipboard is empty or unavailable.
    /// </returns>
    /// <remarks>
    /// The method is safe to call when the window is unavailable — returns <c>null</c>.
    /// </remarks>
    public static async Task<string?> GetTextAsync()
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                return await GetTextInternalAsync();
            }
            else
            {
                return await Dispatcher.UIThread.InvokeAsync(GetTextInternalAsync);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Clipboard] GetTextAsync failed: {ex.Message}");
        }

        return null;
    }

    private static async Task<string?> GetTextInternalAsync()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = desktop.MainWindow;
            if (window?.Clipboard != null)
            {
                return await window.Clipboard.TryGetTextAsync();
            }
        }
        return null;
    }
}