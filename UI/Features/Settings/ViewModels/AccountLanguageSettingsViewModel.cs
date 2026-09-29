using LMP.Core.Youtube.Exceptions;
using LMP.UI.Dialogs;
using LMP.UI.Features.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.UI.Features.Settings.ViewModels;

public sealed partial class AccountLanguageSettingsViewModel : ViewModelBase
{
    private readonly CookieAuthService _auth;
    private readonly YoutubeUserDataService _userData;
    private readonly LocalAuthServer _localServer;
    private readonly DialogService _dialog;
    private readonly NotificationService _notifications;
    private readonly LibraryService _library;
    private readonly YoutubeProvider _youtube;

    protected override bool HandlesAccountChanges => true;

    [ObservableProperty] public partial bool IsAuthenticated { get; private set; }
    [ObservableProperty] public partial bool IsAccountLoading { get; private set; }

    public string AccountName => IsAuthenticated ? _auth.State.UserName : SL["Auth_NotSignedIn"];
    public string? AccountAvatarUrl => IsAuthenticated ? _auth.State.AvatarUrl : null;
    public string AccountSubtitle => IsAuthenticated ? _auth.State.UserEmail : SL["Auth_Guest"];

    public static List<LanguageItem> Languages => LocalizationService.Instance.AvailableLanguages;

    [ObservableProperty] public partial LanguageItem? SelectedLanguage { get; set; }

    public IAsyncRelayCommand LoginCommand { get; }
    public IAsyncRelayCommand LogoutCommand { get; }
    public IAsyncRelayCommand SwitchAccountCommand { get; }
    public IAsyncRelayCommand RefreshProfileCommand { get; }

    public AccountLanguageSettingsViewModel(
        CookieAuthService auth,
        YoutubeUserDataService userData,
        LocalAuthServer localServer,
        DialogService dialog,
        NotificationService notifications,
        LibraryService library,
        YoutubeProvider youtube)
    {
        _auth = auth;
        _userData = userData;
        _localServer = localServer;
        _dialog = dialog;
        _notifications = notifications;
        _library = library;
        _youtube = youtube;

        LoginCommand = new AsyncRelayCommand(LoginAsync);
        LogoutCommand = new AsyncRelayCommand(LogoutAsync);
        SwitchAccountCommand = new AsyncRelayCommand(SwitchAccountAsync);
        RefreshProfileCommand = new AsyncRelayCommand(RefreshProfileAsync);

        LoadSettings();
    }

    public void LoadSettings()
    {
        IsAuthenticated = _auth.IsAuthenticated;
        RaiseAccountProperties();

        var langCode = _library.Settings.LanguageCode;
        SelectedLanguage = Languages.FirstOrDefault(x => x.Code == langCode) ?? Languages[0];
    }

    partial void OnSelectedLanguageChanged(LanguageItem? value)
    {
        if (value is null) return;
        LocalizationService.Instance.CurrentLanguage = value.Code;
        _library.UpdateSettings(s => s.LanguageCode = value.Code);
    }

    protected override void OnAccountChanged()
    {
        base.OnAccountChanged();
        IsAuthenticated = _auth.IsAuthenticated;
        RaiseAccountProperties();
    }

    private void RaiseAccountProperties()
    {
        OnPropertyChanged(nameof(AccountName));
        OnPropertyChanged(nameof(AccountAvatarUrl));
        OnPropertyChanged(nameof(AccountSubtitle));
    }

    private async Task LoginAsync()
    {
        IsAccountLoading = true;
        try
        {
            var authVm = new AuthDialogViewModel(_auth, _userData, _localServer);
            var host = AppEntry.Services.GetRequiredService<DialogHostViewModel>();
            var tcs = new TaskCompletionSource<bool>();

            authVm.OnResult = result =>
            {
                host.CloseDialog(result);
                tcs.TrySetResult(result);
            };

            _ = host.ShowAsync<object>(authVm);
            var success = await tcs.Task;

            if (success)
            {
                IsAuthenticated = _auth.IsAuthenticated;
                RaiseAccountProperties();

                await _notifications.ShowToastAsync(
                    titleKey: "Dialog_Success",
                    messageKey: "Auth_LoggedInAs",
                    messageArgs: [_auth.State.UserName],
                    severity: NotificationSeverity.Success);
            }
        }
        finally
        {
            IsAccountLoading = false;
        }
    }

    private async Task SwitchAccountAsync()
    {
        if (!IsAuthenticated) return;

        IsAccountLoading = true;
        try
        {
            var accounts = _auth.State.CachedAccounts;
            if (accounts == null || accounts.Count == 0)
            {
                Log.Info("[Settings] No cached accounts found. Making fallback network request...");
                accounts = await _userData.GetAvailableAccountsAsync();
            }

            if (accounts.Count == 0)
            {
                await _dialog.ShowInfoAsync(SL["Dialog_Error_Title"], SL["Auth_ProfileLoadError_Message"]);
                return;
            }

            if (accounts.Count <= 1)
            {
                await _dialog.ShowInfoAsync(SL["Dialog_Info_Title"], SL["Auth_NoMultipleAccounts"]);
                return;
            }

            var selectedAccount = await _dialog.ShowAccountSelectionDialogAsync(accounts);
            if (selectedAccount == null) return;

            _auth.SetAuthUser(selectedAccount.AuthUser);
            _auth.UpdateUserProfile(selectedAccount.Name, selectedAccount.Email, selectedAccount.AvatarUrl, selectedAccount.GaiaId);

            _youtube.ClearCache();
            RaiseAccountProperties();

            await _notifications.ShowToastAsync(
                titleKey: "Dialog_Success",
                messageKey: "Auth_LoggedInAs",
                messageArgs: [selectedAccount.Name],
                severity: NotificationSeverity.Success);
        }
        catch (LoginRequiredException ex) when (ex.Reason == LoginRequiredReason.SessionExpired)
        {
            Log.Warn("[Settings] Switch account failed due to expired session. Prompting user to update cookies.");

            bool wantsToLogin = await _dialog.ConfirmAsync(
                SL["Auth_SessionExpired_SwitchAccount_Title"],
                SL["Auth_SessionExpired_SwitchAccount_Message"],
                SL["Auth_Login"],
                SL["Common_Cancel"]);

            if (wantsToLogin)
                await LoginAsync();
        }
        catch (Exception ex)
        {
            await _dialog.ShowInfoAsync(SL["Dialog_Error_Title"], ex.Message);
        }
        finally
        {
            IsAccountLoading = false;
        }
    }

    private async Task RefreshProfileAsync()
    {
        if (!IsAuthenticated) return;

        IsAccountLoading = true;
        try
        {
            var (name, email, avatar, gaiaId) = await _userData.GetAccountInfoAsync();
            if (!string.IsNullOrEmpty(name))
            {
                _auth.UpdateUserProfile(name, email, avatar, gaiaId);
                RaiseAccountProperties();
            }

            _ = _userData.GetAvailableAccountsAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"[Settings] Failed to refresh profile manually: {ex.Message}");
        }
        finally
        {
            IsAccountLoading = false;
        }
    }

    private async Task LogoutAsync()
    {
        if (!await _dialog.ConfirmAsync(SL["Auth_Logout"], SL["Dialog_LogoutMessage"])) return;
        IsAccountLoading = true;
        try
        {
            _auth.Logout();
            IsAuthenticated = _auth.IsAuthenticated;
            RaiseAccountProperties();
        }
        finally
        {
            IsAccountLoading = false;
        }
    }
}
