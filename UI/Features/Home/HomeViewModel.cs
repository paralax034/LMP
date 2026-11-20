
using System.Collections.ObjectModel;
using Avalonia.Threading;

namespace LMP.UI.Features.Home;
/// <summary>
/// Main screen ViewModel. Categories + YouTube search with caching.
/// </summary>
public sealed partial class HomeViewModel : TrackListBaseViewModel
{
    #region Constants

    private const int DefaultFetchSize = 100;

    #endregion

    #region Fields

    private readonly YoutubeProvider _youtube;
    private readonly SearchCacheService _searchCache;
    private readonly ImageCacheService _imageCache;
    private readonly EventHandler<string> _languageChangedHandler;

    private CancellationTokenSource? _categoryCts;
    private bool _isDisposed;
    private bool _isDataLoaded;

    #endregion

    #region Properties

    [ObservableProperty] public partial string Greeting { get; private set; } = string.Empty;
    [ObservableProperty] public partial CategoryItem? SelectedCategory { get; set; }

    public ObservableCollection<CategoryItem> Categories { get; } = [];

    partial void OnSelectedCategoryChanged(CategoryItem? value)
    {
        if (_isDisposed || value is null || !_isDataLoaded) return;

        _ = LoadTracksAsync();
    }

    #endregion

    #region Commands

    public IAsyncRelayCommand RefreshCommand { get; }

    #endregion

    #region Constructor

    public HomeViewModel(
        YoutubeProvider youtube,
        SearchCacheService searchCache,
        ImageCacheService imageCache,
        DownloadService downloads,
        TrackViewModelFactory vmFactory,
        PlayerControlService playerControl)
        : base(downloads, vmFactory, playerControl)
    {
        _youtube = youtube;
        _searchCache = searchCache;
        _imageCache = imageCache;

        UpdateGreeting();

        _languageChangedHandler = (_, _) =>
        {
            UpdateGreeting();
            InitializeCategories();
        };
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;

        InitializeCategories();

        RefreshCommand = new AsyncRelayCommand(async () => await LoadTracksAsync(force: true));
    }

    #endregion

    #region Navigation

    public override async Task OnNavigatedToAsync()
    {
        if (_isDisposed) return;

        await base.OnNavigatedToAsync();
        if (_isDisposed) return;

        if (!_isDataLoaded)
        {
            await LoadTracksAsync();
        }
    }

    #endregion

    #region Base Implementation

    protected override void OnPlay(TrackInfo track) =>
        _ = PlayerControl.PlayTrackAsync(track);

    /// <inheritdoc />
    protected override void OnAccountChanged()
    {
        base.OnAccountChanged();
        _isDataLoaded = false;

        if (CurrentSuspendLevel == SuspendLevel.None)
        {
            _ = Dispatcher.UIThread.InvokeAsync(async () => await LoadTracksAsync(force: true), DispatcherPriority.Background);
        }
    }

    #endregion

    #region Private Methods

    private async Task LoadTracksAsync(bool force = false)
    {
        if (_isDisposed) return;

        var category = SelectedCategory;
        if (category is null) return;

        var oldCts = _categoryCts;
        var loadCts = new CancellationTokenSource();
        _categoryCts = loadCts;
        oldCts?.Cancel();
        oldCts?.Dispose();

        var ct = loadCts.Token;

        IsLoading = true;

        try
        {
            if (!await ct.DelayNoThrowAsync(50, continueOnCapturedContext: true))
                return;

            if (category.IsSpecial)
            {
                var recent = await LibService.GetRecentlyPlayedAsync(DefaultFetchSize, ct);
                if (ct.IsCancellationRequested) return;
                SetTracks(recent);
            }
            else
            {
                string query = category.Query;

                var cached = !force
                    ? await _searchCache.GetAsync(query, SearchSource.YouTube, 30)
                    : null;

                List<TrackInfo> tracks;

                if (cached is { Count: > 0 })
                {
                    tracks = cached;
                    _ = RefreshCacheInBackgroundAsync(query, ct);
                }
                else
                {
                    tracks = await _youtube.SearchFastAsync(query, DefaultFetchSize, ct: ct);
                    if (ct.IsCancellationRequested) return;

                    if (tracks.Count > 0)
                        _ = _searchCache.SetAsync(query, SearchSource.YouTube, tracks);
                }

                int prefetchLimit = Math.Min(20, tracks.Count);
                var imageUrls = new List<string>(prefetchLimit);
                for (int i = 0; i < prefetchLimit; i++)
                {
                    if (!string.IsNullOrEmpty(tracks[i].ThumbnailUrl))
                        imageUrls.Add(tracks[i].ThumbnailUrl);
                }
                _ = _imageCache.PrefetchAsync(imageUrls, ct);

                if (ct.IsCancellationRequested) return;
                SetTracks(tracks);
            }

            _ = HydrateCacheStatusAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error($"[HomeVM] Load error: {ex.Message}");
        }
        finally
        {
            if (!_isDisposed && ReferenceEquals(_categoryCts, loadCts))
            {
                _isDataLoaded = true;
                IsLoading = false;
            }
        }
    }

    private async Task RefreshCacheInBackgroundAsync(string query, CancellationToken ct)
    {
        try
        {
            if (!await ct.DelayNoThrowAsync(3000, continueOnCapturedContext: false))
                return;

            var fresh = await _youtube.SearchFastAsync(query, DefaultFetchSize, ct: ct);
            if (ct.IsCancellationRequested || fresh.Count == 0) return;
            await _searchCache.SetAsync(query, SearchSource.YouTube, fresh);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"[HomeVM] Background cache refresh failed: {ex.Message}");
        }
    }

    private void UpdateGreeting()
    {
        var key = DateTime.Now.Hour switch
        {
            >= 0 and < 5 => "Home_Greeting_Night",
            >= 5 and < 12 => "Home_Greeting_Morning",
            >= 12 and < 18 => "Home_Greeting_Afternoon",
            _ => "Home_Greeting_Evening"
        };
        Greeting = SL[key];
    }

    private void InitializeCategories()
    {
        ReadOnlySpan<(string key, string fallback, string query, bool special)> defs =
        [
            ("Category_RecentlyPlayed", "Recently Played", "",                         true),
            ("Category_Trending",       "Trending",        "trending music 2025",      false),
            ("Category_Pop",            "Pop",             "pop hits 2025",            false),
            ("Category_HipHop",         "Hip-Hop",         "hip hop 2025",             false),
            ("Category_Electronic",     "Electronic",      "electronic music",         false),
            ("Category_LoFi",           "Lo-Fi",           "lofi hip hop chill beats", false),
            ("Category_Rock",           "Rock",            "rock music",               false),
        ];

        for (int i = Categories.Count; i < defs.Length; i++)
            Categories.Add(new CategoryItem());

        for (int i = 0; i < defs.Length; i++)
        {
            var (key, fallback, query, special) = defs[i];
            var name = SL[key];
            if (string.IsNullOrEmpty(name) || name == key) name = fallback;

            var cat = Categories[i];
            cat.Name = name;
            cat.Query = query;
            cat.IsSpecial = special;
            cat.LocKey = key;
        }

        while (Categories.Count > defs.Length)
            Categories.RemoveAt(Categories.Count - 1);

        SelectedCategory ??= Categories.FirstOrDefault();
    }

    #endregion

    #region IDisposable

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            Log.Debug("[HomeVM] Disposing");
            LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;
            _categoryCts?.Cancel();
            _categoryCts?.Dispose();
            _categoryCts = null;
        }

        base.Dispose(disposing);
        _isDisposed = true;
    }

    #endregion
}

public sealed partial class CategoryItem : ObservableObject
{
    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public bool IsSpecial { get; set; }
    public string LocKey { get; set; } = "";
}