using Avalonia.Threading;

namespace LMP.UI.Features.Settings.ViewModels;

public sealed partial class GeneralSettingsViewModel : ViewModelBase, IDisposable
{
    private readonly LibraryService _library;
    private readonly SearchCacheService _searchCache;
    private readonly DialogService _dialog;

    private DispatcherTimer? _suggestionsDebounceTimer;
    private bool _isLoading;

    [ObservableProperty] public partial bool DiscordRpcEnabled { get; set; }
    [ObservableProperty] public partial bool EnableSearchCache { get; set; }
    [ObservableProperty] public partial int SearchCacheTtlMinutes { get; set; }
    [ObservableProperty] public partial int MaxSuggestionsCount { get; set; }

    public IAsyncRelayCommand ResetLibraryCommand { get; }

    public Action? OnLibraryReset { get; set; }

    public GeneralSettingsViewModel(
        LibraryService library,
        SearchCacheService searchCache,
        DialogService dialog)
    {
        _library = library;
        _searchCache = searchCache;
        _dialog = dialog;

        ResetLibraryCommand = new AsyncRelayCommand(ResetLibraryAsync);
        LoadSettings();
    }

    public void LoadSettings()
    {
        _isLoading = true;
        try
        {
            var s = _library.Settings;
            DiscordRpcEnabled = s.DiscordRpcEnabled;
            EnableSearchCache = s.EnableSearchCache;
            SearchCacheTtlMinutes = s.SearchCacheTtlMinutes;
            MaxSuggestionsCount = s.MaxSuggestionsCount > 0 ? s.MaxSuggestionsCount : 8;
        }
        finally
        {
            _isLoading = false;
        }
    }

    partial void OnDiscordRpcEnabledChanged(bool value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.DiscordRpcEnabled = value);
    }

    partial void OnEnableSearchCacheChanged(bool value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.EnableSearchCache = value);
    }

    partial void OnSearchCacheTtlMinutesChanged(int value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.SearchCacheTtlMinutes = value);
        _ = _searchCache.CleanupExpiredAsync();
    }

    partial void OnMaxSuggestionsCountChanged(int value)
    {
        if (_isLoading) return;
        _suggestionsDebounceTimer?.Stop();
        _suggestionsDebounceTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(400),
            DispatcherPriority.Normal,
            (_, _) =>
            {
                _suggestionsDebounceTimer?.Stop();
                _library.UpdateSettings(s => s.MaxSuggestionsCount = value);
            });
        _suggestionsDebounceTimer.Start();
    }

    private async Task ResetLibraryAsync()
    {
        if (!await _dialog.ConfirmAsync(SL["Dialog_Warning_Title"], SL["Dialog_ResetMessage"])) return;
        await _library.ResetAsync();
        OnLibraryReset?.Invoke();
        await _dialog.ShowInfoAsync(SL["Dialog_Done_Title"], SL["Dialog_ResetComplete"]);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _suggestionsDebounceTimer?.Stop();
        }
        base.Dispose(disposing);
    }
}
