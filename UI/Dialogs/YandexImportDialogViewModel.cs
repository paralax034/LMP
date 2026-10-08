using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using LMP.Core.Helpers;
using LMP.Core.Models;
using LMP.Core.Services;

namespace LMP.UI.Dialogs;

/// <summary>
/// Результат работы диалога импорта Яндекс Музыки.
/// </summary>
public sealed record YandexImportDialogResult(
    ExternalPlaylist SelectedPlaylist,
    string Token,
    long Uid);

/// <summary>
/// Модель представления оверлей-диалога авторизации и выбора плейлиста Яндекс Музыки.
/// </summary>
public sealed partial class YandexImportDialogViewModel : ViewModelBase
{
    private const string OAuthUrl = "https://oauth.yandex.ru/authorize?response_type=token&client_id=23cabbbdc6cd418abb4b39c32c41195d";

    private readonly YandexAuthService _authService;
    private readonly YandexMusicClient _client;

    #region Observable Properties

    [ObservableProperty]
    public partial string TokenText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsError { get; set; }

    [ObservableProperty]
    public partial bool IsAuthorized { get; set; }

    [ObservableProperty]
    public partial string ProfileName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProfileLogin { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ExternalPlaylist? SelectedPlaylist { get; set; }

    public ObservableCollection<ExternalPlaylist> Playlists { get; } = [];

    #endregion

    public Action<YandexImportDialogResult?>? OnResult { get; set; }

    public IAsyncRelayCommand ConnectCommand { get; }
    public IAsyncRelayCommand OpenBrowserCommand { get; }
    public IAsyncRelayCommand PasteTokenCommand { get; }
    public IRelayCommand ChangeAccountCommand { get; }
    public IRelayCommand ConfirmImportCommand { get; }
    public IRelayCommand CloseCommand { get; }

    private long _currentUid;
    private readonly CancellationTokenSource _cts = new();

    public YandexImportDialogViewModel(
        YandexAuthService authService,
        YandexMusicClient client)
    {
        _authService = authService;
        _client = client;

        ConnectCommand = new AsyncRelayCommand(ConnectWithTokenAsync);
        OpenBrowserCommand = new AsyncRelayCommand(OpenBrowserOAuthAsync);
        PasteTokenCommand = new AsyncRelayCommand(PasteTokenFromClipboardAsync);
        ChangeAccountCommand = new RelayCommand(ResetAuthorization);
        ConfirmImportCommand = new RelayCommand(ConfirmImport, () => SelectedPlaylist != null);
        CloseCommand = new RelayCommand(() => OnResult?.Invoke(null));

        var savedToken = _authService.GetSavedToken();
        if (!string.IsNullOrWhiteSpace(savedToken))
        {
            TokenText = savedToken;
            _ = Task.Run(() => ConnectWithTokenAsync());
        }
        else
        {
            SetStatus(SL["Dialog_Login_WaitingStatus"], isError: false);
        }
    }

    partial void OnSelectedPlaylistChanged(ExternalPlaylist? value)
    {
        ConfirmImportCommand.NotifyCanExecuteChanged();
    }

    private async Task OpenBrowserOAuthAsync()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = OAuthUrl,
                UseShellExecute = true
            });
            SetStatus(SL["Import_Yandex_BrowserOpened"], isError: false);
        }
        catch (Exception ex)
        {
            SetStatus(string.Format(SL["Import_Yandex_ConnectionError"], ex.Message), isError: true);
        }

        await Task.CompletedTask;
    }

    [GeneratedRegex(@"access_token=([^&]+)")]
    private static partial Regex AccessTokenRegex();

    private async Task PasteTokenFromClipboardAsync()
    {
        try
        {
            var text = await Helpers.Clipboard.GetTextAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (text.Contains("access_token="))
                {
                    var match = AccessTokenRegex().Match(text);
                    if (match.Success)
                    {
                        text = match.Groups[1].Value;
                    }
                }

                TokenText = text.Trim();
                await ConnectWithTokenAsync();
            }
        }
        catch (Exception ex)
        {
            SetStatus(string.Format(SL["Import_Yandex_ClipboardError"], ex.Message), isError: true);
        }
    }

    private async Task ConnectWithTokenAsync()
    {
        if (string.IsNullOrWhiteSpace(TokenText))
        {
            SetStatus(SL["Import_Yandex_EnterToken"], isError: true);
            return;
        }

        IsLoading = true;
        SetStatus(SL["Import_Yandex_Checking"], isError: false);

        try
        {
            var cleanToken = TokenText.Trim();
            var profile = await _client.GetProfileAsync(cleanToken, _cts.Token).ConfigureAwait(false);
            var playlists = await _client.GetPlaylistsAsync(cleanToken, profile.Uid, _cts.Token).ConfigureAwait(false);

            _currentUid = profile.Uid;
            _authService.SaveToken(cleanToken);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ProfileName = profile.DisplayName;
                ProfileLogin = profile.Login;
                Playlists.Clear();

                foreach (var p in playlists)
                {
                    Playlists.Add(p);
                }

                SelectedPlaylist = Playlists.FirstOrDefault();
                IsAuthorized = true;
                IsLoading = false;
                SetStatus(string.Empty, isError: false);
            });
        }
        catch (UnauthorizedAccessException)
        {
            _authService.ClearToken();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsAuthorized = false;
                IsLoading = false;
                SetStatus(SL["Import_Yandex_InvalidToken"], isError: true);
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsLoading = false;
                SetStatus(string.Format(SL["Import_Yandex_ConnectionError"], ex.Message), isError: true);
            });
        }
    }

    private void ResetAuthorization()
    {
        _authService.ClearToken();
        TokenText = string.Empty;
        IsAuthorized = false;
        Playlists.Clear();
        SelectedPlaylist = null;
        SetStatus(SL["Import_Yandex_TokenReset"], isError: false);
    }

    private void ConfirmImport()
    {
        if (SelectedPlaylist == null) return;

        OnResult?.Invoke(new YandexImportDialogResult(
            SelectedPlaylist,
            TokenText.Trim(),
            _currentUid));
    }

    private void SetStatus(string text, bool isError)
    {
        StatusText = text;
        IsError = isError;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}
