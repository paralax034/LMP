using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Threading;
using Notification = LMP.Core.Models.Notification;

namespace LMP.UI.Features.Notifications;

/// <summary>
/// Item filtering mode in the notification center and background tasks.
/// </summary>
public enum NotificationFilterMode
{
    All = 0,
    NotificationsOnly = 1,
    TasksOnly = 2
}

public sealed partial class NotificationPanelViewModel : ViewModelBase
{
    private readonly NotificationService _notificationService;
    private readonly PlaylistSyncService? _playlistSync;

    // The 1-second countdown timer runs only when the panel is open and pending tasks exist
    private DispatcherTimer? _countdownTimer;
    private bool _isPanelVisible;

    public ObservableCollection<Notification> Notifications
        => _notificationService.Notifications;

    /// <summary>
    /// Filtered collection bound to the virtualized list.
    /// </summary>
    public ObservableCollection<Notification> DisplayedNotifications { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsFilterNotifications))]
    [NotifyPropertyChangedFor(nameof(IsFilterTasks))]
    public partial NotificationFilterMode FilterMode { get; set; } = NotificationFilterMode.All;

    public bool IsFilterAll => FilterMode == NotificationFilterMode.All;
    public bool IsFilterNotifications => FilterMode == NotificationFilterMode.NotificationsOnly;
    public bool IsFilterTasks => FilterMode == NotificationFilterMode.TasksOnly;

    public bool HasNotifications => DisplayedNotifications.Count > 0;
    public bool HasActiveTasks => _notificationService.HasActiveTasks;

    [ObservableProperty] public partial bool IsLoading { get; private set; }

    public IRelayCommand SetFilterAllCommand { get; }
    public IRelayCommand SetFilterNotificationsCommand { get; }
    public IRelayCommand SetFilterTasksCommand { get; }
    public IAsyncRelayCommand ForceFlushAllCommand { get; }
    public IRelayCommand ClearAllCommand { get; }
    public IAsyncRelayCommand<string?> CopyErrorCommand { get; }

    public NotificationPanelViewModel(
        NotificationService notificationService,
        PlaylistSyncService? playlistSync = null)
    {
        _notificationService = notificationService;
        _playlistSync = playlistSync;

        Notifications.CollectionChanged += OnSourceCollectionChanged;
        _notificationService.OnTasksChanged += OnTasksStateChanged;
        _notificationService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NotificationService.HasActiveTasks))
                OnPropertyChanged(nameof(HasActiveTasks));
        };

        SetFilterAllCommand = new RelayCommand(() => FilterMode = NotificationFilterMode.All);
        SetFilterNotificationsCommand = new RelayCommand(() => FilterMode = NotificationFilterMode.NotificationsOnly);
        SetFilterTasksCommand = new RelayCommand(() => FilterMode = NotificationFilterMode.TasksOnly);

        ForceFlushAllCommand = new AsyncRelayCommand(async () =>
        {
            if (_playlistSync != null)
                await _playlistSync.FlushAllAsync();
        });

        ClearAllCommand = new RelayCommand(() =>
        {
            _notificationService.ClearAll();
        });

        CopyErrorCommand = new AsyncRelayCommand<string?>(async details =>
        {
            if (!string.IsNullOrEmpty(details))
                await CopyToClipboardAsync(details, "Error details");
        });

        RebuildDisplayedNotifications();
    }

    partial void OnFilterModeChanged(NotificationFilterMode value)
    {
        _ = value;
        RebuildDisplayedNotifications();
    }

    private bool MatchesCurrentFilter(Notification item) => FilterMode switch
    {
        NotificationFilterMode.NotificationsOnly => !item.IsTask,
        NotificationFilterMode.TasksOnly => item.IsTask,
        _ => true
    };

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Incremental updates without full Clear() prevent ListBox container recreation and UI spikes
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is { Count: 1 } added && added[0] is Notification newItem)
        {
            if (!MatchesCurrentFilter(newItem))
                return;

            if (e.NewStartingIndex == 0 && FilterMode == NotificationFilterMode.All)
                DisplayedNotifications.Insert(0, newItem);
            else
                DisplayedNotifications.Add(newItem);

            OnPropertyChanged(nameof(HasNotifications));
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems is { Count: 1 } removed && removed[0] is Notification oldItem)
        {
            if (DisplayedNotifications.Remove(oldItem))
                OnPropertyChanged(nameof(HasNotifications));
            return;
        }

        RebuildDisplayedNotifications();
    }

    private void OnTasksStateChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            OnPropertyChanged(nameof(HasActiveTasks));
            UpdateCountdownTimer();
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                OnPropertyChanged(nameof(HasActiveTasks));
                UpdateCountdownTimer();
            });
        }
    }

    /// <summary>
    /// Reports panel visibility: live task countdown ticks only while the panel is open.
    /// </summary>
    public void SetPanelVisible(bool isVisible)
    {
        _isPanelVisible = isVisible;
        UpdateCountdownTimer();
    }

    private void UpdateCountdownTimer()
    {
        if (_isPanelVisible && HasDisplayedCountdown())
        {
            if (_countdownTimer == null)
            {
                _countdownTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                _countdownTimer.Tick += OnCountdownTick;
            }

            if (!_countdownTimer.IsEnabled)
                _countdownTimer.Start();
        }
        else
        {
            _countdownTimer?.Stop();
        }
    }

    private bool HasDisplayedCountdown()
    {
        for (int i = 0; i < DisplayedNotifications.Count; i++)
        {
            if (DisplayedNotifications[i].HasTaskCountdown)
                return true;
        }

        return false;
    }

    private void OnCountdownTick(object? sender, EventArgs e)
    {
        bool anyCountdown = false;
        for (int i = 0; i < DisplayedNotifications.Count; i++)
        {
            var item = DisplayedNotifications[i];
            if (!item.HasTaskCountdown)
                continue;

            item.RefreshCountdown();
            anyCountdown = true;
        }

        if (!anyCountdown || !_isPanelVisible)
            _countdownTimer?.Stop();
    }

    private void RebuildDisplayedNotifications()
    {
        void Apply()
        {
            DisplayedNotifications.Clear();
            for (int i = 0; i < Notifications.Count; i++)
            {
                var item = Notifications[i];
                if (MatchesCurrentFilter(item))
                    DisplayedNotifications.Add(item);
            }

            OnPropertyChanged(nameof(HasNotifications));
            UpdateCountdownTimer();
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }

    public Task OnPanelOpenedAsync()
    {
        IsLoading = false;

        // Smoothly update only the relative TimeAgo timestamps without recreating the collection (Clear)
        for (int i = 0; i < DisplayedNotifications.Count; i++)
        {
            DisplayedNotifications[i].NotifyVisualStateChanged();
        }

        return Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Notifications.CollectionChanged -= OnSourceCollectionChanged;
            _notificationService.OnTasksChanged -= OnTasksStateChanged;

            if (_countdownTimer != null)
            {
                _countdownTimer.Stop();
                _countdownTimer.Tick -= OnCountdownTick;
                _countdownTimer = null;
            }
        }

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
