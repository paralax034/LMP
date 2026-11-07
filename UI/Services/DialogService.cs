using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Search;
using LMP.UI.Dialogs;
using LMP.UI.Dialogs.Content;
using LMP.UI.Features.Shell;

namespace LMP.UI.Services;

/// <summary>
/// Centralized service for displaying dialog windows.
/// 
/// <para><b>Architecture:</b></para>
/// <list type="bullet">
///   <item><b>Overlay dialogs</b> — rendered via DialogHost on top of content,
///     but UNDER TopBar and PlayerBar. TopBar, notifications, and window controls remain active.
///     Navigation is locked automatically.</item>
///   <item><b>Modal windows</b> — BotDetection and StreamUnavailable.
///     Block the ENTIRE application. Used only for critical errors.</item>
/// </list>
/// 
/// <para><b>Pattern for overlay dialogs:</b></para>
/// <code>
/// 1. Create Content/ViewModel with a callback (OnResult / OnClose)
/// 2. Set callback → tcs.TrySetResult() + dialogHost.CloseDialog()
/// 3. Call dialogHost.ShowAsync() (fire-and-forget)
/// 4. await tcs.Task to receive the result
/// </code>
/// </summary>
public sealed class DialogService
{
    private static LocalizationService L => LocalizationService.Instance;

    private readonly CookieAuthService _authService;
    private readonly YoutubeUserDataService _youtubeUserDataService;
    private readonly LocalAuthServer _localAuthServer;
    private readonly Func<DialogHostViewModel> _getDialogHost;

    /// <param name="authService">Authentication service — for checking YouTube login state.</param>
    /// <param name="getDialogHost">
    /// Lazy accessor for DialogHostViewModel.
    /// Uses <c>Func</c> instead of direct injection due to a circular dependency:
    /// DialogService is created before MainWindowViewModel.
    /// </param>
    public DialogService(
        CookieAuthService authService,
        YoutubeUserDataService youtubeUserDataService,
        LocalAuthServer localAuthServer,
        Func<DialogHostViewModel> getDialogHost)
    {
        _authService = authService;
        _youtubeUserDataService = youtubeUserDataService;
        _localAuthServer = localAuthServer;
        _getDialogHost = getDialogHost;
    }

    #region Helpers

    /// <summary>
    /// Gets the main window if it is loaded and visible.
    /// Used only for modal dialogs (BotDetection, StreamUnavailable).
    /// </summary>
    private static Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = desktop.MainWindow;
            if (window is { IsLoaded: true, IsVisible: true })
                return window;
        }
        return null;
    }

    #endregion

    #region Generic Choice Dialog (Overlay)

    /// <summary>
    /// Universal choice dialog with a custom set of buttons and an optional checkbox.
    /// </summary>
    public async Task<ChoiceResult<T>?> ShowChoiceAsync<T>(
        string title,
        string? message,
        IReadOnlyList<ChoiceOption<T>> options,
        string? cancelText = null,
        string? checkBoxText = null)
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<ChoiceResult<T>?>();

        var vm = ChoiceDialogViewModel.Create(
            title: title,
            message: message,
            options: options,
            onResult: (value, isChecked) =>
            {
                // Initiate dialog closure first, preventing overlap with new TCS sessions
                host.CloseDialog(value);
                tcs.TrySetResult(new ChoiceResult<T>(value, isChecked));
            },
            onCancel: () =>
            {
                host.CloseDialog();
                tcs.TrySetResult(null);
            },
            cancelText: cancelText,
            checkBoxText: checkBoxText);

        _ = host.ShowAsync<object>(vm);
        return await tcs.Task;
    }

    #endregion

    #region Basic Dialogs (Overlay)

    /// <summary>
    /// YouTube account selection dialog (main/brand).
    /// </summary>
    public async Task<YoutubeAccountItem?> ShowAccountSelectionDialogAsync(IEnumerable<YoutubeAccountItem> accounts)
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<YoutubeAccountItem?>();

        var vm = new AccountSelectionDialogViewModel(accounts, _authService.State.AuthUser)
        {
            OnResult = result =>
            {
                host.CloseDialog(result);
                tcs.TrySetResult(result);
            }
        };

        _ = host.ShowAsync<object>(vm);
        return await tcs.Task;
    }

    /// <summary>
    /// Shows a dialog for automated downloading, extraction, and manual activation of the LMP Auth extension.
    /// </summary>
    public async Task<bool> ShowExtensionInstallDialogAsync()
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<bool>();

        var vm = new AuthDialogViewModel(_authService, _youtubeUserDataService, _localAuthServer)
        {
            OnResult = result =>
            {
                host.CloseDialog(result);
                tcs.TrySetResult(result);
            }
        };

        _ = host.ShowAsync<object>(vm);
        return await tcs.Task;
    }

    /// <summary>
    /// Shows a confirmation dialog (Yes/No, OK/Cancel).
    /// </summary>
    public async Task<bool> ConfirmAsync(
        string title, string message,
        string confirmText, string cancelText)
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<bool>();

        var content = new ConfirmDialogContent(
            title, message, confirmText, cancelText,
            onResult: result =>
            {
                host.CloseDialog(result);
                tcs.TrySetResult(result);
            });

        _ = host.ShowAsync<object>(content);
        return await tcs.Task;
    }

    /// <summary>
    /// Shows a confirmation dialog with standard OK / Cancel buttons.
    /// </summary>
    public Task<bool> ConfirmAsync(string title, string message) =>
        ConfirmAsync(title, message, L["Common_OK"], L["Common_Cancel"]);

    /// <summary>
    /// Shows an informational dialog with a single button.
    /// </summary>
    public async Task ShowInfoAsync(string title, string message, string buttonText)
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<object?>();

        var content = new InfoDialogContent(
            title, message, buttonText,
            onClose: () =>
            {
                host.CloseDialog();
                tcs.TrySetResult(null);
            });

        _ = host.ShowAsync<object>(content);
        await tcs.Task;
    }

    /// <summary>
    /// Shows an informational dialog with a standard OK button.
    /// </summary>
    public Task ShowInfoAsync(string title, string message) =>
        ShowInfoAsync(title, message, L["Common_OK"]);

    /// <summary>
    /// Shows a text input dialog.
    /// </summary>
    public async Task<string?> ShowInputAsync(
        string title, string prompt, string? watermark = null)
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<string?>();

        var content = new InputDialogContent(
            title, prompt,
            watermark ?? L["Input_Watermark"],
            onResult: result =>
            {
                host.CloseDialog(result);
                tcs.TrySetResult(result);
            });

        _ = host.ShowAsync<object>(content);
        return await tcs.Task;
    }

    /// <summary>
    /// Shows a system folder picker dialog.
    /// This is the only dialog using the native OS picker.
    /// </summary>
    public static async Task<string?> SelectFolderAsync(string? startPath = null)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = GetMainWindow();
            if (window == null) return null;

            var storage = window.StorageProvider;
            IStorageFolder? suggested = null;

            if (!string.IsNullOrEmpty(startPath) && Directory.Exists(startPath))
                suggested = await storage.TryGetFolderFromPathAsync(startPath);

            var result = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = L["Dialog_SelectDownloadFolder_Title"],
                SuggestedStartLocation = suggested,
                AllowMultiple = false
            });

            return result.Count > 0 ? result[0].Path.LocalPath : null;
        });
    }

    #endregion

    #region Close Action Dialog

    /// <summary>
    /// Prompts the user to apply, discard, or cancel when leaving a page with unsaved changes.
    /// </summary>
    /// <param name="guard">The active guard with unsaved changes.</param>
    /// <returns><c>true</c> if changes were applied or discarded and navigation may proceed; <c>false</c> if navigation was canceled.</returns>
    public async Task<bool> ConfirmUnsavedChangesAsync(IUnsavedChangesGuard guard)
    {
        if (!guard.HasUnsavedChanges)
            return true;

        var choice = await ShowChoiceAsync(
            title: L["Theme_UnsavedDialog_Title"],
            message: L["Theme_UnsavedDialog_Message"],
            options:
            [
                new ChoiceOption<bool>
                {
                    Text = L["Common_Apply"],
                    Value = true,
                    IsPrimary = true
                },
                new ChoiceOption<bool>
                {
                    Text = L["Theme_DiscardChanges"],
                    Value = false
                }
            ],
            cancelText: L["Common_Cancel"]);

        if (choice is null)
            return false;

        if (choice.Value)
            guard.ApplyChanges();
        else
            guard.DiscardChanges();

        return true;
    }

    public async Task<ChoiceResult<CloseAction>?> ShowCloseActionDialogAsync()
    {
        return await ShowChoiceAsync(
            title: L["Dialog_CloseAction_Title"],
            message: L["Dialog_CloseAction_Message"],
            options:
            [
                new ChoiceOption<CloseAction>
                {
                    Text = L["Dialog_CloseAction_Tray"],
                    Value = CloseAction.MinimizeToTray,
                    IsPrimary = true
                },
                new ChoiceOption<CloseAction>
                {
                    Text = L["Dialog_CloseAction_Exit"],
                    Value = CloseAction.Exit
                }
            ],
            cancelText: L["Common_Cancel"],
            checkBoxText: L["Dialog_CloseAction_Remember"]);
    }

    #endregion

    #region Playlist Dialogs (Overlay)

    /// <summary>
    /// Create new playlist dialog.
    /// </summary>
    public async Task<CreatePlaylistResult?> ShowCreatePlaylistDialogAsync()
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<CreatePlaylistResult?>();

        var vm = new CreatePlaylistDialogViewModel(_authService.IsAuthenticated)
        {
            OnResult = result =>
            {
                host.CloseDialog(result);
                tcs.TrySetResult(result);
            }
        };

        _ = host.ShowAsync<object>(vm);
        return await tcs.Task;
    }

    /// <summary>
    /// Delete playlist dialog.
    /// </summary>
    public async Task<DeletePlaylistResult?> ShowDeletePlaylistDialogAsync(
        Playlist playlist, bool isAuthenticated)
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<DeletePlaylistResult?>();

        var vm = new DeletePlaylistDialogViewModel(playlist, isAuthenticated)
        {
            OnResult = result =>
            {
                host.CloseDialog(result);
                tcs.TrySetResult(result);
            }
        };

        _ = host.ShowAsync<object>(vm);
        return await tcs.Task;
    }

    /// <summary>
    /// "Add track to playlist" dialog — list of playlists with checkboxes.
    /// </summary>
    public async Task<List<string>> ShowAddToPlaylistDialogAsync(TrackInfo track)
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<List<string>>();

        var playlistService = Microsoft.Extensions.DependencyInjection
            .ServiceProviderServiceExtensions
            .GetRequiredService<PlaylistService>(AppEntry.Services);
        var playlists = await playlistService.GetAllPlaylistsAsync();

        var vm = new AddToPlaylistDialogViewModel(track, playlists)
        {
            OnResult = result =>
            {
                host.CloseDialog(result);
                tcs.TrySetResult(result);
            }
        };

        _ = host.ShowAsync<object>(vm);
        return await tcs.Task;
    }

    /// <summary>
    /// Edit playlist dialog.
    /// Automatically loads playlist tracks and resolves dependencies for the mosaic generator and color palette.
    /// </summary>
    public async Task<EditPlaylistResult?> ShowEditPlaylistDialogAsync(
        Playlist playlist,
        IReadOnlyList<TrackInfo>? playlistTracks = null)
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<EditPlaylistResult?>();

        var tracks = playlistTracks;
        if (tracks == null || tracks.Count == 0)
        {
            try
            {
                var playlistService = Microsoft.Extensions.DependencyInjection
                    .ServiceProviderServiceExtensions
                    .GetRequiredService<PlaylistService>(AppEntry.Services);
                var loaded = await playlistService.GetPlaylistTracksAsync(playlist.Id).ConfigureAwait(false);
                if (loaded.Count > 0) tracks = loaded;
            }
            catch (Exception ex)
            {
                Log.Warn($"[DialogService] CoverPicker tracks load failed: {ex.Message}");
            }
        }

        var vm = new EditPlaylistDialogViewModel(
            playlist,
            _authService.IsAuthenticated,
            tracks)
        {
            OnResult = result =>
            {
                host.CloseDialog(result);
                tcs.TrySetResult(result);
            }
        };

        _ = host.ShowAsync<object>(vm);
        return await tcs.Task;
    }

    #endregion

    #region Sync Dialogs (Overlay)

    /// <summary>
    /// Bulk synchronization dialog.
    /// </summary>
    public async Task<List<SyncDecision>> ShowSyncSelectionAsync(
        IEnumerable<PlaylistSearchResult> items,
        ISet<string> existingLocalNames,
        IReadOnlyDictionary<string, int>? trackCounts = null)
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<List<SyncDecision>>();

        var vm = new SyncSelectionViewModel(
            items, existingLocalNames, trackCounts)
        {
            OnResult = result =>
            {
                host.CloseDialog(result);
                tcs.TrySetResult(result);
            }
        };

        _ = host.ShowAsync<object>(vm);
        return await tcs.Task;
    }

    /// <summary>
    /// Single playlist synchronization dialog.
    /// </summary>
    public async Task<PlaylistSyncOptions?> ShowPlaylistSyncDialogAsync(
        PlaylistSyncPreview preview)
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<PlaylistSyncOptions?>();

        var vm = new SyncPlaylistDialogViewModel(preview)
        {
            OnResult = result =>
            {
                host.CloseDialog(result);
                tcs.TrySetResult(result);
            }
        };

        _ = host.ShowAsync<object>(vm);
        return await tcs.Task;
    }

    #endregion

    #region Critical Dialogs (Modal Windows)

    /// <summary>
    /// Authentication required dialog.
    /// </summary>
    public Task ShowLoginRequiredAsync(LoginRequiredException exception) =>
        ShowInfoAsync(
            L["Dialog_Login_Title"],
            L[exception.GetLocalizationKey()],
            L["Common_OK"]);

    /// <summary>
    /// Shows a modal overlay warning indicating the need to re-sync playlists after a DB migration.
    /// Completely blocks the UI for the specified duration (countdown) using DialogHost.
    /// </summary>
    public async Task ShowLegacyMigrationAlertAsync(int countdownSeconds)
    {
        var host = _getDialogHost();
        var tcs = new TaskCompletionSource<object?>();

        var vm = new MigrationWarningDialogViewModel(
            title: L["Dialog_Warning_Title"],
            message: L["Library_LegacyPlaylists_Message"],
            countdownSeconds: countdownSeconds,
            onClose: () =>
            {
                host.CloseDialog();
                tcs.TrySetResult(null);
            });

        _ = host.ShowAsync<object>(vm);
        await tcs.Task;
    }

    #endregion
}

/// <summary>
/// Choice dialog result: selected value + checkbox state.
/// </summary>
/// <typeparam name="T">Type of the selected value.</typeparam>
public sealed record ChoiceResult<T>(T Value, bool IsChecked)
{
    public override string ToString()
    {
        return $"Value: {Value}, IsChecked: {IsChecked}";
    }
}