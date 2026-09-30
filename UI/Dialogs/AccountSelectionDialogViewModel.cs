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
        Accounts = new(accounts);

        ConfirmCommand = new RelayCommand(
            () => OnResult?.Invoke(SelectedAccount),
            () => SelectedAccount != null);

        CancelCommand = new RelayCommand(() => OnResult?.Invoke(null));

        YoutubeAccountItem? matched = null;
        YoutubeAccountItem? selected = null;
        YoutubeAccountItem? first = null;

        bool hasActiveUser = !string.IsNullOrEmpty(activeAuthUser);

        for (int i = 0; i < Accounts.Count; i++)
        {
            var acc = Accounts[i];
            first ??= acc;

            if (hasActiveUser && matched is null && acc.AuthUser == activeAuthUser)
            {
                matched = acc;
                break;
            }

            if (selected is null && acc.IsSelected)
            {
                selected = acc;
            }
        }

        SelectedAccount = matched ?? selected ?? first;
    }
}