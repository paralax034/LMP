using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace LMP.UI.Features.Notifications;

public partial class NotificationPanel : UserControl
{
    public NotificationPanel()
    {
        InitializeComponent();

        AddHandler(
            Expander.ExpandedEvent,
            OnExpanderExpanded,
            RoutingStrategies.Bubble,
            handledEventsToo: false);
    }

    /// <summary>
    /// Smoothly scrolls the Expander into the ScrollViewer's viewport upon expansion after layout completes.
    /// Does not intercept mouse clicks or break user interaction.
    /// </summary>
    private static void OnExpanderExpanded(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Expander expander)
        {
            Dispatcher.UIThread.Post(() =>
            {
                expander.BringIntoView();
            }, DispatcherPriority.Background);
        }
    }

    private async void OnCopyErrorClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string details && !string.IsNullOrEmpty(details))
        {
            try
            {
                await Clipboard.SetTextAsync(details);
                Log.Info("[Notification] Error details copied to clipboard");

                var L = LocalizationService.Instance;
                string copiedText = L["Common_Copied"];

                ToolTip.SetTip(btn, copiedText);
                ToolTip.SetIsOpen(btn, true);

                DispatcherTimer.RunOnce(() =>
                {
                    ToolTip.SetIsOpen(btn, false);
                    ToolTip.SetTip(btn, L["Notification_CopyError"]);
                }, TimeSpan.FromMilliseconds(1200));
            }
            catch (Exception ex)
            {
                Log.Warn($"[Notification] Failed to copy error details: {ex.Message}");
            }
        }
    }
}