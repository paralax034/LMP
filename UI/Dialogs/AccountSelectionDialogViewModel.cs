using System.Collections.ObjectModel;

namespace LMP.UI.Dialogs;

public sealed partial class AccountSelectionDialogViewModel : ViewModelBase
{
    public ObservableCollection<YoutubeAccountItem> Accounts { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial YoutubeAccountItem? SelectedAccount { get; set; }
    public Action<YoutubeAccountItem?>? OnResult { get; set; }

    public IRelayCommand ConfirmCommand { get; }
    public IRelayCommand CancelCommand { get; }

    /// <summary>
    /// Инициализирует модель представления выбора аккаунта.
    /// </summary>
    public AccountSelectionDialogViewModel(
        IEnumerable<YoutubeAccountItem> accounts,
        string? activeAuthUser = "")
    {
        Accounts = new ObservableCollection<YoutubeAccountItem>(accounts);
        SelectedAccount = Accounts.FirstOrDefault(a => a.AuthUser == activeAuthUser && !string.IsNullOrEmpty(activeAuthUser))
                        ?? Accounts.FirstOrDefault(a => a.IsSelected)
                        ?? Accounts.FirstOrDefault();

        ConfirmCommand = new RelayCommand(
            () => OnResult?.Invoke(SelectedAccount),
            () => SelectedAccount != null);

        CancelCommand = new RelayCommand(() => OnResult?.Invoke(null));
    }
}