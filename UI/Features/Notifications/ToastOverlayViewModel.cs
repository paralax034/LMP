using Notification = LMP.Core.Models.Notification;

namespace LMP.UI.Features.Notifications;

public sealed partial class ToastOverlayViewModel : ViewModelBase
{
    private readonly NotificationService _notificationService;

    public Notification? CurrentToast => _notificationService.CurrentToast;
    public bool IsVisible => _notificationService.IsToastVisible;

    #region Null-safe wrapper properties for XAML binding

    public string ToastTitle => CurrentToast?.Title ?? string.Empty;
    public string ToastMessage => CurrentToast?.Message ?? string.Empty;
    public string ToastIcon => CurrentToast?.Icon ?? string.Empty;

    /// <summary>
    /// Severity для конвертера в XAML.
    /// </summary>
    public NotificationSeverity ToastSeverity => CurrentToast?.Severity ?? NotificationSeverity.Info;

    #endregion

    public IRelayCommand DismissCommand { get; }

    public ToastOverlayViewModel(NotificationService notificationService)
    {
        _notificationService = notificationService;

        _notificationService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NotificationService.CurrentToast))
            {
                OnPropertyChanged(nameof(CurrentToast));
                OnPropertyChanged(nameof(IsVisible));
                RaiseToastWrapperProperties();
            }
            if (e.PropertyName == nameof(NotificationService.IsToastVisible))
            {
                OnPropertyChanged(nameof(IsVisible));
            }
        };

        DismissCommand = new RelayCommand(() =>
        {
            _notificationService.DismissToast();
        });
    }

    private void RaiseToastWrapperProperties()
    {
        OnPropertyChanged(nameof(ToastTitle));
        OnPropertyChanged(nameof(ToastMessage));
        OnPropertyChanged(nameof(ToastIcon));
        OnPropertyChanged(nameof(ToastSeverity));
    }
}