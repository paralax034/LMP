using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Notification = LMP.Core.Models.Notification;

namespace LMP.UI.Features.Notifications;

public sealed partial class NotificationPanelViewModel : ViewModelBase
{
    private readonly NotificationService _notificationService;

    public ObservableCollection<Notification> Notifications
        => _notificationService.Notifications;

    /// <summary>
    /// Коллекция привязанная к ListBox.
    /// </summary>
    public ObservableCollection<Notification> DisplayedNotifications => Notifications;

    public bool HasNotifications => Notifications.Count > 0;

    [ObservableProperty] public partial bool IsLoading { get; private set; }

    public IRelayCommand ClearAllCommand { get; }
    public IAsyncRelayCommand<string?> CopyErrorCommand { get; }

    public NotificationPanelViewModel(NotificationService notificationService)
    {
        _notificationService = notificationService;

        Notifications.CollectionChanged += OnSourceCollectionChanged;

        ClearAllCommand = new RelayCommand(() =>
        {
            _notificationService.ClearAll();
        });

        CopyErrorCommand = new AsyncRelayCommand<string?>(async details =>
        {
            if (!string.IsNullOrEmpty(details))
                await CopyToClipboardAsync(details, "Error details");
        });
    }

    /// <summary>
    /// Синхронизирует <see cref="DisplayedNotifications"/> при изменении источника.
    /// Работает только после завершения загрузки (<see cref="IsLoading"/> = false),
    /// чтобы не конкурировать с инкрементальным batch-flow.
    /// </summary>
    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasNotifications));
    }

    /// <summary>
    /// Вызывается при каждом открытии панели.
    /// </summary>
    public Task OnPanelOpenedAsync()
    {
        IsLoading = false;
        return Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Notifications.CollectionChanged -= OnSourceCollectionChanged;

        base.Dispose(disposing);
    }

    private static async Task CopyToClipboardAsync(string text, string description)
    {
        try
        {
            await Clipboard.SetTextAsync(text);
            Log.Info($"[Notification] {description} copied to clipboard");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Notification] Failed to copy: {ex.Message}");
        }
    }
}