using Avalonia.Threading;

namespace LMP.UI.Features.Notifications;

public sealed partial class NotificationButtonViewModel : ViewModelBase
{
    private readonly NotificationService _notificationService;
    private readonly NotificationPanelViewModel _panelViewModel;

    [ObservableProperty] public partial bool IsPanelOpen { get; set; }

    public bool HasUnread => _notificationService.HasUnread;
    public int UnreadCount => _notificationService.UnreadCount;
    public string UnreadCountText => UnreadCount > 9 ? "9+" : UnreadCount.ToString();

    public IRelayCommand TogglePanelCommand { get; }

    public NotificationButtonViewModel(
        NotificationService notificationService,
        NotificationPanelViewModel panelViewModel)
    {
        _notificationService = notificationService;
        _panelViewModel = panelViewModel;

        _notificationService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(NotificationService.UnreadCount)
                                or nameof(NotificationService.HasUnread))
            {
                OnPropertyChanged(nameof(HasUnread));
                OnPropertyChanged(nameof(UnreadCount));
                OnPropertyChanged(nameof(UnreadCountText));
            }
        };

        TogglePanelCommand = new RelayCommand(() =>
        {
            IsPanelOpen = !IsPanelOpen;

            if (IsPanelOpen)
            {
                Log.Debug("[NotificationButton] Panel opened");
                _ = _panelViewModel.OnPanelOpenedAsync();

                // Откладываем маркировку прочтения на фоновый приоритет UI-потока,
                // чтобы не блокировать открывающий проход Measure/Arrange для Popup.
                Dispatcher.UIThread.Post(
                    static state => ((NotificationService)state!).MarkAllAsRead(),
                    _notificationService,
                    DispatcherPriority.Background);
            }
        });
    }
}