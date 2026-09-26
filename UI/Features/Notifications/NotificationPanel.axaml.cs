using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LMP.Core.Services;

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
    /// При раскрытии Expander мягко подкручивает его в область видимости ScrollViewer после завершения раскладки.
    /// Не перехватывает клик мыши и не вызывает срыва взаимодействия.
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
                string copiedText = L.Get("Common_Copied", "Copied");

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