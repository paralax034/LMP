using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Threading;
using LMP.Core.Youtube.Search;

namespace LMP.UI.Features.Search;

/// <summary>
/// Search bar suggestion item (history or YouTube network suggestion).
/// </summary>
public sealed record SearchSuggestionItem(string Text, bool IsFromHistory, SearchViewModel Owner);

/// <summary>
/// Track search screen ViewModel supporting Ghost Text, in-place suggestion updates, and direct URL prioritization.
/// </summary>
public sealed partial class SearchViewModel : TrackListBaseViewModel
{
    #region Constants

    /// <inheritdoc />
    protected override bool HandlesAccountChanges => true;

    private const int DebounceMs = 300;
    private const int LocalSearchDebounceMs = 150;
    private const int MaxResults = 300;

    #endregion

    #region Fields

    private readonly YoutubeProvider _youtube;
    private readonly SearchCacheService _searchCache;
    private readonly ImageCacheService _imageCache;
    private readonly HashSet<string> _dismissedSuggestions = new(StringComparer.OrdinalIgnoreCase);

    private readonly DispatcherTimer _suggestDebounceTimer;
    private readonly DispatcherTimer _localSearchDebounceTimer;

    private string _currentQuery = string.Empty;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _suggestCts;
    private YoutubeProvider.SearchSession? _searchSession;
    private DateTime _lastSearchTime = DateTime.MinValue;

    private bool _isDisposed;
    private int _displayTrackCount;

    #endregion

    #region Properties

    private int InitialBatchSize => LibService.Settings.LoadBatchSize > 0
        ? LibService.Settings.LoadBatchSize
        : 25;

    private int ScrollBatchSize => LibService.Settings.SearchBatchSize > 0
        ? LibService.Settings.SearchBatchSize
        : 25;

    private string _searchQuery = string.Empty;

    /// <summary>
    /// Current search query text with automatic newline stripping.
    /// Prevents destructive whitespace trimming during character-by-character input to avoid cursor jumps.
    /// </summary>
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            var raw = value ?? string.Empty;
            string clean;
            if (raw.Contains('\r') || raw.Contains('\n'))
            {
                clean = raw.Replace("\r", string.Empty).Replace("\n", " ");
            }
            else
            {
                clean = raw;
            }

            if (SetProperty(ref _searchQuery, clean))
            {
                OnSearchQueryChanged(clean);
            }
        }
    }

    /// <summary>
    /// Full auto-completion text (Ghost Text) applied when pressing Tab / Right Arrow.
    /// </summary>
    [ObservableProperty] public partial string GhostText { get; private set; } = string.Empty;

    /// <summary>
    /// Auto-completion suffix (suggestion tail displayed after the typed text).
    /// </summary>
    [ObservableProperty] public partial string GhostTextSuffix { get; private set; } = string.Empty;

    /// <summary>
    /// Flag indicating whether an available Ghost Text suffix exists for rendering the translucent layer.
    /// </summary>
    public bool HasGhostText => !string.IsNullOrEmpty(GhostTextSuffix);

    /// <summary>
    /// Search provider: YouTube Music, YouTube, Local.
    /// </summary>
    [ObservableProperty] public partial ContentSource Source { get; set; } = ContentSource.YouTubeMusic;

    [ObservableProperty] public partial bool HasResults { get; private set; }
    [ObservableProperty] public partial string? ErrorMessage { get; private set; }
    [ObservableProperty] public partial bool IsFromCache { get; private set; }
    [ObservableProperty] public partial bool IsOfflineMode { get; private set; }

    private void OnSearchQueryChanged(string value)
    {
        if (_isDisposed) return;

        // 1. Instant synchronous input response
        UpdateLocalSuggestionsAndGhostText(value);

        // 2. 200 ms debounce for network InnerTube/Suggest API
        _suggestDebounceTimer.Stop();
        if (!string.IsNullOrWhiteSpace(value) && Source != ContentSource.Local)
        {
            _suggestDebounceTimer.Start();
        }

        // 3. As-you-type local search when Local source is selected
        if (Source == ContentSource.Local)
        {
            _localSearchDebounceTimer.Stop();
            _localSearchDebounceTimer.Start();
        }

        NotifyCanExecuteSafe();
    }

    private void OnSuggestDebounceTimerTick(object? sender, EventArgs e)
    {
        _suggestDebounceTimer.Stop();
        if (!_isDisposed && !string.IsNullOrWhiteSpace(SearchQuery))
        {
            FetchRemoteSuggestionsThrottled(SearchQuery);
        }
    }

    private void OnLocalSearchDebounceTimerTick(object? sender, EventArgs e)
    {
        _localSearchDebounceTimer.Stop();
        if (!_isDisposed && Source == ContentSource.Local)
        {
            _ = ExecuteSearchAsync(forceNetwork: false, bypassDebounce: true);
        }
    }

    partial void OnSourceChanged(ContentSource value)
    {
        OnPropertyChanged(nameof(IsSourceYtm));
        OnPropertyChanged(nameof(IsSourceYt));
        OnPropertyChanged(nameof(IsSourceLocal));
        IsOfflineMode = value == ContentSource.Local;

        NotifyCanExecuteSafe();

        if (!_isDisposed)
        {
            if (value == ContentSource.Local || !string.IsNullOrWhiteSpace(SearchQuery))
            {
                _ = ExecuteSearchAsync(forceNetwork: false, bypassDebounce: true);
            }
        }
    }

    partial void OnIsFromCacheChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowForceSearchButton));
        NotifyCanExecuteSafe();
    }

    /// <summary>
    /// Exact count of displayed tracks (listens to the Items UI collection).
    /// </summary>
    public int DisplayTrackCount
    {
        get => _displayTrackCount;
        private set
        {
            if (SetProperty(ref _displayTrackCount, value))
                NotifyBadgeChanged();
        }
    }

    /// <summary>
    /// Counter value displayed to the left of the search bar.
    /// </summary>
    public int BadgeCount => DisplayTrackCount > 0 ? DisplayTrackCount : Suggestions.Count;

    /// <summary>
    /// Counter visibility indicator.
    /// </summary>
    public bool IsBadgeVisible => true;

    /// <summary>
    /// Tooltip for the tracks/suggestions counter.
    /// </summary>
    public string BadgeTooltip => DisplayTrackCount > 0
        ? string.Format(LocalizationService.Instance["Search_BadgeTooltip_Tracks"], DisplayTrackCount)
        : Suggestions.Count > 0
            ? string.Format(LocalizationService.Instance["Search_BadgeTooltip_Suggestions"], Suggestions.Count)
            : LocalizationService.Instance["Search_BadgeTooltip_Empty"];

    /// <summary>
    /// Placeholder text for the suggestion feed when no suggestions are available.
    /// </summary>
    public string RibbonPlaceholderText
    {
        get
        {
            var trimmed = SearchQuery.Trim();
            if (!string.IsNullOrEmpty(trimmed) && YoutubeProvider.DetectQueryType(trimmed) != QueryType.Search)
                return LocalizationService.Instance["Search_DirectUrlHint"];

            return string.IsNullOrWhiteSpace(trimmed)
                ? LocalizationService.Instance["Search_NoHistoryPlaceholder"]
                : LocalizationService.Instance["Search_NoSuggestionsPlaceholder"];
        }
    }

    /// <summary>
    /// Force-refresh button: visible only when cached results exist.
    /// </summary>
    public bool ShowForceSearchButton =>
        LibService.Settings.EnableSearchCache && IsFromCache && !IsLoading;

    /// <summary>
    /// Locally saved search history.
    /// </summary>
    public ObservableCollection<string> RecentSearches { get; } = [];

    /// <summary>
    /// Flag indicating whether saved queries exist in local history.
    /// </summary>
    public bool HasRecentSearches => RecentSearches.Count > 0;

    /// <summary>
    /// Combined suggestion list (History + YouTube Suggest).
    /// </summary>
    public ObservableCollection<SearchSuggestionItem> Suggestions { get; } = [];

    /// <summary>
    /// Flag indicating whether suggestions exist for rendering chips in the horizontal ribbon.
    /// </summary>
    public bool HasSuggestions => Suggestions.Count > 0;

    /// <summary>
    /// Flag indicating the presence of history items in suggestions (zero-alloc pass without LINQ).
    /// </summary>
    public bool HasHistoryInSuggestions
    {
        get
        {
            for (int i = 0; i < Suggestions.Count; i++)
            {
                if (Suggestions[i].IsFromHistory)
                    return true;
            }
            return false;
        }
    }

    #endregion

    #region Commands

    public IAsyncRelayCommand SearchCommand { get; }
    public IAsyncRelayCommand ForceSearchCommand { get; }
    public IAsyncRelayCommand<string> SuggestionClickCommand { get; }
    public IRelayCommand<string> RemoveSuggestionCommand { get; }
    public IRelayCommand<string> RemoveHistoryCommand => RemoveSuggestionCommand;
    public IRelayCommand ClearHistoryCommand { get; }
    public IRelayCommand ClearQueryCommand { get; }
    public IRelayCommand CompleteGhostTextCommand { get; }
    public IRelayCommand<string> SetSourceCommand { get; }

    #endregion

    #region UI Presentation State

    /// <summary>
    /// Flag indicating an active network operation (initial load or paged fetch).
    /// </summary>
    public bool IsBusy => IsLoading || IsFetchingFromNetwork;

    public bool IsSourceYtm => Source == ContentSource.YouTubeMusic;
    public bool IsSourceYt => Source == ContentSource.YouTube;
    public bool IsSourceLocal => Source == ContentSource.Local;

    /// <summary>
    /// Determines whether to display the "Nothing found" placeholder.
    /// </summary>
    public bool ShowEmptyState => !IsLoading && !HasResults && (!string.IsNullOrWhiteSpace(_currentQuery) || Source == ContentSource.Local);

    /// <summary>
    /// Intercepts base model property changes and ensures timely notification of the computed property <see cref="IsBusy"/>.
    /// </summary>
    /// <param name="e">Property changed event arguments.</param>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName is nameof(IsLoading) or nameof(IsFetchingFromNetwork))
        {
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    /// <summary>
    /// Thread-safely invalidates UI command execution state on the main UI thread.
    /// </summary>
    private void NotifyCanExecuteSafe()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            SearchCommand.NotifyCanExecuteChanged();
            ForceSearchCommand.NotifyCanExecuteChanged();
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                SearchCommand.NotifyCanExecuteChanged();
                ForceSearchCommand.NotifyCanExecuteChanged();
            });
        }
    }

    public void ClearQuery()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        _suggestCts?.Cancel();
        _suggestCts?.Dispose();
        _suggestCts = null;

        _suggestDebounceTimer.Stop();
        _localSearchDebounceTimer.Stop();

        IsLoading = false;
        IsFetchingFromNetwork = false;
        OnPropertyChanged(nameof(IsBusy));

        SearchQuery = string.Empty;
        _currentQuery = string.Empty;
        ClearItems();
        HasResults = false;
        ErrorMessage = null;
        UpdateLocalSuggestionsAndGhostText(string.Empty);

        if (Source == ContentSource.Local)
        {
            _ = ExecuteSearchAsync(forceNetwork: false, bypassDebounce: true);
        }
    }

    public void OpenHistoryIfAvailable()
    {
        UpdateLocalSuggestionsAndGhostText(SearchQuery);
    }

    /// <summary>
    /// Auto-completes the input field with the discovered Ghost Text.
    /// </summary>
    public void CompleteGhostText()
    {
        if (_isDisposed || string.IsNullOrEmpty(GhostText)) return;

        if (GhostText.StartsWith(SearchQuery, StringComparison.OrdinalIgnoreCase) &&
            GhostText.Length > SearchQuery.Length)
        {
            SearchQuery = GhostText;
        }
    }

    #endregion

    #region Constructor

    public SearchViewModel(
        AudioEngine audio,
        DownloadService downloads,
        TrackViewModelFactory vmFactory,
        YoutubeProvider youtube,
        SearchCacheService searchCache,
        ImageCacheService imageCache)
        : base(audio, downloads, vmFactory)
    {
        _youtube = youtube;
        _searchCache = searchCache;
        _imageCache = imageCache;

        _suggestDebounceTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(200),
            DispatcherPriority.Normal,
            OnSuggestDebounceTimerTick);

        _localSearchDebounceTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(LocalSearchDebounceMs),
            DispatcherPriority.Normal,
            OnLocalSearchDebounceTimerTick);

        SearchCommand = new AsyncRelayCommand(
            () => ExecuteSearchAsync(forceNetwork: false, bypassDebounce: true),
            () => (Source == ContentSource.Local || !string.IsNullOrWhiteSpace(SearchQuery)) && !IsLoading);

        ForceSearchCommand = new AsyncRelayCommand(
            () => ExecuteSearchAsync(forceNetwork: true, bypassDebounce: true),
            () => IsFromCache && !IsLoading);

        SuggestionClickCommand = new AsyncRelayCommand<string>(async q =>
        {
            if (_isDisposed || string.IsNullOrEmpty(q)) return;
            SearchQuery = q;
            await ExecuteSearchAsync(forceNetwork: false, bypassDebounce: true);
        });

        CompleteGhostTextCommand = new RelayCommand(CompleteGhostText);

        RemoveSuggestionCommand = new RelayCommand<string>(q =>
        {
            if (_isDisposed || string.IsNullOrEmpty(q)) return;

            _dismissedSuggestions.Add(q);

            for (int i = RecentSearches.Count - 1; i >= 0; i--)
            {
                if (string.Equals(RecentSearches[i], q, StringComparison.OrdinalIgnoreCase))
                    RecentSearches.RemoveAt(i);
            }

            UpdateHistoryStorage();
            OnPropertyChanged(nameof(HasRecentSearches));

            for (int i = Suggestions.Count - 1; i >= 0; i--)
            {
                if (string.Equals(Suggestions[i].Text, q, StringComparison.OrdinalIgnoreCase))
                {
                    Suggestions.RemoveAt(i);
                    break;
                }
            }

            CalculateGhostText(SearchQuery, Suggestions);
            NotifySuggestionsChanged();
        });

        ClearHistoryCommand = new RelayCommand(() =>
        {
            if (_isDisposed) return;
            RecentSearches.Clear();
            _dismissedSuggestions.Clear();
            UpdateHistoryStorage();
            OnPropertyChanged(nameof(HasRecentSearches));
            UpdateLocalSuggestionsAndGhostText(SearchQuery);
        });

        ClearQueryCommand = new RelayCommand(ClearQuery);

        SetSourceCommand = new RelayCommand<string>(sourceStr =>
        {
            if (_isDisposed) return;
            if (Enum.TryParse<ContentSource>(sourceStr, true, out var result))
                Source = result;
        });

        Items.CollectionChanged += OnItemsCollectionChanged;

        IsLoading = false;
        IsFetchingFromNetwork = false;
        OnPropertyChanged(nameof(IsBusy));
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        DisplayTrackCount = Items.Count;
        NotifyBadgeChanged();
    }

    private void NotifyBadgeChanged()
    {
        OnPropertyChanged(nameof(BadgeCount));
        OnPropertyChanged(nameof(IsBadgeVisible));
        OnPropertyChanged(nameof(BadgeTooltip));
    }

    private void NotifySuggestionsChanged()
    {
        NotifyBadgeChanged();
        OnPropertyChanged(nameof(HasSuggestions));
        OnPropertyChanged(nameof(HasHistoryInSuggestions));
        OnPropertyChanged(nameof(RibbonPlaceholderText));
        OnPropertyChanged(nameof(HasGhostText));
    }

    #endregion

    #region Navigation

    public override async Task OnNavigatedToAsync()
    {
        if (_isDisposed) return;

        await base.OnNavigatedToAsync().ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(_currentQuery))
        {
            IsLoading = false;
            IsFetchingFromNetwork = false;
        }

        OnPropertyChanged(nameof(IsBusy));
        await LoadHistoryAsync();

        if (Source == ContentSource.Local && Items.Count == 0)
        {
            _ = ExecuteSearchAsync(forceNetwork: false, bypassDebounce: true);
        }
    }

    #endregion

    #region TrackListBaseViewModel Implementation

    protected override void OnPlay(TrackInfo track)
    {
        if (_isDisposed) return;
        _ = Audio.StartQueueAsync([track], track);
        _ = LibService.AddToRecentlyPlayedAsync(track);
    }

    protected override async Task<List<TrackInfo>> FetchMoreFromNetworkAsync(CancellationToken ct)
    {
        if (_isDisposed || Source == ContentSource.Local || TotalCount >= MaxResults)
            return [];

        if (_searchSession == null && !string.IsNullOrEmpty(_currentQuery))
        {
            var existingIds = GetLoadedItemsIds();
            _searchSession = _youtube.CreateSearchSession(
                _currentQuery, MaxResults, GetSearchFilter(), existingIds);
            Log.Info($"[Search] Continuation session created from cache ({TotalCount} existing items)");
        }

        if (_searchSession == null || !_searchSession.HasMore)
        {
            Log.Debug("[Search] FetchMore skipped: no active session or reached end.");
            SetCanFetchMore(false);
            return [];
        }

        var sw = Stopwatch.StartNew();
        Log.Info($"[Search] FetchMore started: current items={TotalCount}, requesting batch size={ScrollBatchSize}...");

        try
        {
            var newTracks = await _searchSession.FetchNextBatchAsync(ScrollBatchSize, ct);
            if (ct.IsCancellationRequested || _isDisposed) return [];

            if (Source == ContentSource.YouTubeMusic)
            {
                for (int i = 0; i < newTracks.Count; i++)
                    newTracks[i].IsMusic = true;
            }

            if (newTracks.Count > 0)
            {
                AudioSourceFactory.GlobalCache.HydrateCacheStatus(newTracks);

                if (LibService.Settings.EnableSearchCache)
                {
                    var snapshot = GetItemsSnapshot();
                    var all = new List<TrackInfo>(snapshot.Count + newTracks.Count);
                    all.AddRange(snapshot);
                    all.AddRange(newTracks);
                    _ = _searchCache.SetAsync(_currentQuery, SourceToSearchSource(), all);

                    var imageUrls = newTracks.Take(10)
                        .Select(static t => t.ThumbnailUrl)
                        .Where(static u => !string.IsNullOrEmpty(u));
                    _ = _imageCache.PrefetchAsync(imageUrls!, ct);
                }
            }

            sw.Stop();
            Log.Info($"[Search] FetchMore finished: received {newTracks.Count} tracks in {sw.ElapsedMilliseconds}ms, session.HasMore={_searchSession.HasMore}");

            if (newTracks.Count == 0 || !_searchSession.HasMore)
                SetCanFetchMore(false);

            return newTracks;
        }
        catch (OperationCanceledException)
        {
            Log.Debug("[Search] FetchMore was canceled.");
            return [];
        }
        catch (HttpRequestException ex)
        {
            Log.Error($"[Search] Network error during FetchMore: {ex.Message}");
            ErrorMessage = SL["Search_NetworkError"];
            return [];
        }
        catch (Exception ex)
        {
            Log.Error($"[Search] FetchMore unexpected failure: {ex.Message}");
            return [];
        }
        finally
        {
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    protected override void OnAccountChanged()
    {
        base.OnAccountChanged();

        SearchQuery = string.Empty;
        _currentQuery = string.Empty;
        GhostText = string.Empty;
        GhostTextSuffix = string.Empty;
        ErrorMessage = null;
        IsFromCache = false;
        HasResults = false;
        IsLoading = false;
        IsFetchingFromNetwork = false;
        OnPropertyChanged(nameof(IsBusy));

        try { _searchSession?.Dispose(); } catch { }
        _searchSession = null;

        ClearItems();
        _ = Dispatcher.UIThread.InvokeAsync(LoadHistoryAsync, DispatcherPriority.Background);
    }

    #endregion

    #region Search Logic

    private SearchFilter GetSearchFilter() => Source switch
    {
        ContentSource.YouTubeMusic => SearchFilter.MusicSong,
        ContentSource.YouTube => SearchFilter.Video,
        ContentSource.Local => SearchFilter.None,
        _ => SearchFilter.MusicSong
    };

    private SearchSource SourceToSearchSource() => Source switch
    {
        ContentSource.YouTubeMusic => SearchSource.YouTubeMusic,
        ContentSource.YouTube => SearchSource.YouTube,
        _ => SearchSource.YouTube
    };

    private bool CanExecuteSearch()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastSearchTime).TotalMilliseconds < DebounceMs) return false;
        _lastSearchTime = now;
        return true;
    }

    private async Task ExecuteSearchAsync(bool forceNetwork, bool bypassDebounce = false)
    {
        if (_isDisposed) return;
        if (!forceNetwork && !bypassDebounce && !CanExecuteSearch()) return;

        _lastSearchTime = DateTime.UtcNow;
        CancellationTokenSource? currentCts = null;

        try
        {
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = new CancellationTokenSource();
            currentCts = _searchCts;
            var ct = currentCts.Token;

            try { _searchSession?.Dispose(); } catch { }
            _searchSession = null;

            CancelLoading();
            IsLoading = true;
            ErrorMessage = null;
            IsFromCache = false;
            HasResults = false;

            _currentQuery = SearchQuery.Trim();
            if (!string.IsNullOrWhiteSpace(_currentQuery))
                AddToHistory(_currentQuery);

            // 1. PRIORITY URL CHECK
            var queryType = YoutubeProvider.DetectQueryType(_currentQuery);

            if (queryType == QueryType.DirectUrl)
            {
                await HandleDirectUrlAsync(ct);
                return;
            }

            if (queryType == QueryType.Playlist)
            {
                await HandlePlaylistAsync(ct);
                return;
            }

            // 2. Local SQLite search
            if (Source == ContentSource.Local)
            {
                await HandleLocalSearchAsync(ct);
                return;
            }

            // 3. Network YouTube / YouTube Music search
            await HandleSearchAsync(ct, forceNetwork);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_isDisposed)
            {
                ErrorMessage = ex.Message;
                Log.Error($"[Search] Error executing search: {ex}");
            }
        }
        finally
        {
            void FinalizeSearchState()
            {
                if (!_isDisposed && ReferenceEquals(currentCts, _searchCts))
                {
                    IsLoading = false;
                    IsFetchingFromNetwork = false;
                }

                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(ShowEmptyState));
                OnPropertyChanged(nameof(ShowForceSearchButton));
                SearchCommand.NotifyCanExecuteChanged();
                ForceSearchCommand.NotifyCanExecuteChanged();
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                FinalizeSearchState();
            }
            else
            {
                Dispatcher.UIThread.Post(FinalizeSearchState);
            }
        }
    }

    /// <summary>
    /// Performs a local search across the entire SQLite database (user media library),
    /// supporting both empty queries (fetching recently added items) and filtering by title and artist.
    /// </summary>
    /// <param name="ct">Asynchronous operation cancellation token.</param>
    private async Task HandleLocalSearchAsync(CancellationToken ct)
    {
        if (_isDisposed) return;

        var filtered = string.IsNullOrWhiteSpace(_currentQuery)
            ? await LibService.GetAllTracksAsync(MaxResults, 0, ct)
            : await LibService.SearchTracksAsync(_currentQuery, MaxResults, 0, ct);

        ct.ThrowIfCancellationRequested();
        if (_isDisposed) return;

        await InitializeItemsAsync(filtered, canFetchMore: false);
        HasResults = filtered.Count > 0;

        if (!HasResults)
        {
            var localCount = await LibService.GetTrackCountAsync(ct);
            ErrorMessage = localCount == 0 ? SL["Search_NoLocalFiles"] : SL["Search_NoResults"];
        }
    }

    private async Task HandleDirectUrlAsync(CancellationToken ct)
    {
        if (_isDisposed) return;

        var track = await _youtube.GetTrackByUrlAsync(_currentQuery);
        ct.ThrowIfCancellationRequested();
        if (_isDisposed) return;

        var tracks = track != null ? [track] : new List<TrackInfo>();
        await InitializeItemsAsync(tracks, canFetchMore: false);

        if (track != null && LibService.Settings.AutoPlayOnUrlPaste)
            _ = Audio.PlayTrackAsync(track);

        HasResults = tracks.Count > 0;
        if (!HasResults) ErrorMessage = SL["Search_NoResults"];
    }

    private async Task HandlePlaylistAsync(CancellationToken ct)
    {
        if (_isDisposed) return;

        IsFetchingFromNetwork = true;
        try
        {
            var playlist = await _youtube.GetPlaylistAsync(_currentQuery);
            ct.ThrowIfCancellationRequested();
            if (_isDisposed) return;

            var tracks = playlist?.Tracks ?? [];
            await InitializeItemsAsync(tracks, canFetchMore: false);

            if (tracks.Count > 0 && LibService.Settings.AutoPlayOnUrlPaste)
                _ = Audio.StartQueueAsync(tracks, tracks[0]);

            HasResults = tracks.Count > 0;
            if (!HasResults) ErrorMessage = SL["Search_NoResults"];
        }
        finally
        {
            IsFetchingFromNetwork = false;
        }
    }

    private async Task HandleSearchAsync(CancellationToken ct, bool forceNetwork)
    {
        if (_isDisposed) return;

        var sw = Stopwatch.StartNew();
        var cacheSource = SourceToSearchSource();
        bool useCache = !forceNetwork && LibService.Settings.EnableSearchCache;

        if (useCache)
        {
            var cached = await _searchCache.GetAsync(_currentQuery, cacheSource, minCount: 20);
            ct.ThrowIfCancellationRequested();
            if (_isDisposed) return;

            if (cached is { Count: >= 20 })
            {
                IsFromCache = true;
                _searchSession = null;

                await InitializeItemsAsync(cached, canFetchMore: cached.Count < MaxResults);
                HasResults = true;

                var urls = cached.Take(20).Select(static t => t.ThumbnailUrl);
                _ = _imageCache.PrefetchAsync(urls!, ct);

                Log.Debug($"[Search] Cache hit: {cached.Count} items in {sw.ElapsedMilliseconds}ms");
                return;
            }
        }

        IsFetchingFromNetwork = true;
        IsFromCache = false;

        try
        {
            if (forceNetwork)
                _searchCache.InvalidateQuery(_currentQuery, cacheSource);

            var (tracks, session) = await _youtube.SearchWithSessionAsync(
                _currentQuery, InitialBatchSize, MaxResults, GetSearchFilter(), ct);

            if (_isDisposed) return;

            _searchSession = session;

            if (Source == ContentSource.YouTubeMusic)
            {
                for (int i = 0; i < tracks.Count; i++)
                    tracks[i].IsMusic = true;
            }

            ct.ThrowIfCancellationRequested();
            if (_isDisposed) return;

            if (tracks.Count > 0 && LibService.Settings.EnableSearchCache)
            {
                _ = _searchCache.SetAsync(_currentQuery, cacheSource, tracks);
                var urls = tracks.Take(20).Select(static t => t.ThumbnailUrl);
                _ = _imageCache.PrefetchAsync(urls!, ct);
            }

            bool hasMore = tracks.Count > 0 && (session?.HasMore ?? false);
            await InitializeItemsAsync(tracks, canFetchMore: hasMore);

            HasResults = tracks.Count > 0;
            if (!HasResults) ErrorMessage = SL["Search_NoResults"];

            sw.Stop();
            Log.Info($"[Search] Search completed: {tracks.Count} items in {sw.ElapsedMilliseconds}ms, hasMore={hasMore}");
        }
        finally
        {
            IsFetchingFromNetwork = false;
        }
    }

    #endregion

    #region History, Suggestions & Ghost Text Engine

    private async Task LoadHistoryAsync()
    {
        try
        {
            var history = await LibService.GetSearchHistoryAsync();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_isDisposed) return;

                if (RecentSearches.Count == history.Count)
                {
                    bool same = true;
                    for (int i = 0; i < history.Count; i++)
                    {
                        if (!string.Equals(RecentSearches[i], history[i], StringComparison.Ordinal))
                        {
                            same = false;
                            break;
                        }
                    }
                    if (same) return;
                }

                RecentSearches.Clear();
                for (int i = 0; i < history.Count; i++)
                    RecentSearches.Add(history[i]);

                OnPropertyChanged(nameof(HasRecentSearches));
                UpdateLocalSuggestionsAndGhostText(SearchQuery);
            }, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            Log.Error($"[Search] Failed to load search history: {ex.Message}");
        }
    }

    private void AddToHistory(string query)
    {
        if (_isDisposed || string.IsNullOrWhiteSpace(query)) return;

        if (YoutubeProvider.DetectQueryType(query) != QueryType.Search)
            return;

        for (int i = RecentSearches.Count - 1; i >= 0; i--)
        {
            if (string.Equals(RecentSearches[i], query, StringComparison.OrdinalIgnoreCase))
                RecentSearches.RemoveAt(i);
        }

        RecentSearches.Insert(0, query);

        while (RecentSearches.Count > 12)
            RecentSearches.RemoveAt(RecentSearches.Count - 1);

        UpdateHistoryStorage();
        OnPropertyChanged(nameof(HasRecentSearches));
    }

    private void UpdateHistoryStorage()
    {
        if (_isDisposed) return;
        var historyStrings = RecentSearches.ToList();
        _ = LibService.SaveSearchHistoryAsync(historyStrings);
    }

    /// <summary>
    /// Retrieves relevant suggestions from local history for the specified input fragment.
    /// </summary>
    /// <param name="trimmedQuery">Trimmed search query string.</param>
    /// <returns>A list of suggestions based on search history.</returns>
    private List<SearchSuggestionItem> GetLocalMatchingSuggestions(string trimmedQuery)
    {
        int maxCount = LibService.Settings.MaxSuggestionsCount;
        var result = new List<SearchSuggestionItem>(maxCount);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(trimmedQuery))
        {
            for (int i = 0; i < RecentSearches.Count && result.Count < maxCount; i++)
            {
                var h = RecentSearches[i];
                if (!_dismissedSuggestions.Contains(h) && seen.Add(h))
                    result.Add(new SearchSuggestionItem(h, true, this));
            }
            return result;
        }

        for (int i = 0; i < RecentSearches.Count && result.Count < 4; i++)
        {
            var h = RecentSearches[i];
            if (!_dismissedSuggestions.Contains(h) && h.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase))
            {
                if (seen.Add(h))
                    result.Add(new SearchSuggestionItem(h, true, this));
            }
        }

        return result;
    }

    /// <summary>
    /// Instantly filters currently available suggestions (history and already fetched YouTube data)
    /// and calculates Ghost Text at frame 0.
    /// </summary>
    private void UpdateLocalSuggestionsAndGhostText(string query)
    {
        var rawQuery = query ?? string.Empty;
        var trimmed = rawQuery.Trim();

        if (string.IsNullOrEmpty(trimmed) || YoutubeProvider.DetectQueryType(trimmed) != QueryType.Search)
        {
            GhostText = string.Empty;
            GhostTextSuffix = string.Empty;

            if (string.IsNullOrEmpty(trimmed))
            {
                ApplySuggestions(GetLocalMatchingSuggestions(string.Empty));
            }
            else
            {
                ApplySuggestions([]);
            }
            return;
        }

        // 1. Match against local history
        var matchedItems = GetLocalMatchingSuggestions(trimmed);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < matchedItems.Count; i++)
            seen.Add(matchedItems[i].Text);

        // 2. Append already fetched YouTube suggestions
        for (int i = 0; i < Suggestions.Count; i++)
        {
            var item = Suggestions[i];
            if (!item.IsFromHistory && item.Text.StartsWith(rawQuery, StringComparison.OrdinalIgnoreCase))
            {
                if (seen.Add(item.Text))
                    matchedItems.Add(item);

                if (matchedItems.Count >= LibService.Settings.MaxSuggestionsCount)
                    break;
            }
        }

        ApplySuggestions(matchedItems);
        CalculateGhostText(rawQuery, matchedItems);
    }

    /// <summary>
    /// Calculates full Ghost Text and isolated suffix.
    /// </summary>
    private void CalculateGhostText(string rawQuery, IReadOnlyList<SearchSuggestionItem> items)
    {
        if (string.IsNullOrEmpty(rawQuery) || items.Count == 0)
        {
            GhostText = string.Empty;
            GhostTextSuffix = string.Empty;
            return;
        }

        // Priority 1: exact match including case and whitespace
        for (int i = 0; i < items.Count; i++)
        {
            var candidate = items[i].Text;
            if (candidate.StartsWith(rawQuery, StringComparison.OrdinalIgnoreCase) && candidate.Length > rawQuery.Length)
            {
                GhostText = string.Concat(rawQuery, candidate.AsSpan(rawQuery.Length));
                GhostTextSuffix = candidate[rawQuery.Length..];
                return;
            }
        }

        // Priority 2: if the user added a trailing space
        var trimmed = rawQuery.TrimStart();
        if (trimmed.Length > 0)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var candidate = items[i].Text;
                if (candidate.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase) && candidate.Length > rawQuery.Length)
                {
                    GhostText = string.Concat(rawQuery, candidate.AsSpan(rawQuery.Length));
                    GhostTextSuffix = candidate[rawQuery.Length..];
                    return;
                }
            }
        }

        GhostText = string.Empty;
        GhostTextSuffix = string.Empty;
    }

    /// <summary>
    /// Performs background preloading of suggestions from the network after the input debounce delay expires.
    /// </summary>
    private void FetchRemoteSuggestionsThrottled(string query)
    {
        _suggestCts?.Cancel();
        _suggestCts?.Dispose();
        _suggestCts = new CancellationTokenSource();
        var ct = _suggestCts.Token;

        var trimmed = query?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed) || Source == ContentSource.Local || YoutubeProvider.DetectQueryType(trimmed) != QueryType.Search)
            return;

        _ = FetchAndMergeRemoteSuggestionsAsync(trimmed, ct);
    }

    /// <summary>
    /// Executes a network request for suggestions and merges them with the up-to-date local history.
    /// Discards stale results if the user has continued typing.
    /// </summary>
    /// <param name="targetQuery">The query for which suggestions were requested.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    private async Task FetchAndMergeRemoteSuggestionsAsync(
        string targetQuery,
        CancellationToken ct)
    {
        try
        {
            if (ct.IsCancellationRequested || _isDisposed)
                return;

            var ytSuggestions = await _youtube.GetSearchSuggestionsAsync(targetQuery, ct);
            if (ct.IsCancellationRequested || _isDisposed)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested || _isDisposed)
                    return;

                var currentTrimmed = SearchQuery.Trim();
                if (!string.Equals(targetQuery, currentTrimmed, StringComparison.OrdinalIgnoreCase))
                    return;

                var matched = GetLocalMatchingSuggestions(currentTrimmed);
                var seen = new HashSet<string>(matched.Count + ytSuggestions.Count, StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < matched.Count; i++)
                    seen.Add(matched[i].Text);

                int maxSuggestions = LibService.Settings.MaxSuggestionsCount;
                for (int i = 0; i < ytSuggestions.Count && matched.Count < maxSuggestions; i++)
                {
                    var s = ytSuggestions[i];
                    if (!_dismissedSuggestions.Contains(s) && seen.Add(s))
                        matched.Add(new SearchSuggestionItem(s, false, this));
                }

                ApplySuggestions(matched);
                CalculateGhostText(SearchQuery, matched);
            }, DispatcherPriority.Normal, ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
        {
            // Cancel the background debounced request if text input continues
        }
        catch (Exception ex)
        {
            Log.Debug($"[Search] Suggestion fetch error: {ex.Message}");
        }
    }

    /// <summary>
    /// Compares two sequences of suggestion items by value.
    /// </summary>
    /// <param name="current">Current collection of items.</param>
    /// <param name="next">Target collection of items.</param>
    /// <returns><c>true</c> if the collections are identical in length and content.</returns>
    private static bool AreSuggestionsEqual(ObservableCollection<SearchSuggestionItem> current, List<SearchSuggestionItem> next)
    {
        if (current.Count != next.Count)
            return false;

        for (int i = 0; i < current.Count; i++)
        {
            if (current[i] != next[i])
                return false;
        }

        return true;
    }

    /// <summary>
    /// Performs in-place synchronization of suggestions without calling Clear().
    /// </summary>
    private void ApplySuggestions(List<SearchSuggestionItem> newItems)
    {
        if (AreSuggestionsEqual(Suggestions, newItems))
            return;

        int commonCount = Math.Min(Suggestions.Count, newItems.Count);
        for (int i = 0; i < commonCount; i++)
        {
            if (Suggestions[i] != newItems[i])
            {
                Suggestions[i] = newItems[i];
            }
        }

        if (newItems.Count > Suggestions.Count)
        {
            for (int i = commonCount; i < newItems.Count; i++)
            {
                Suggestions.Add(newItems[i]);
            }
        }
        else if (Suggestions.Count > newItems.Count)
        {
            for (int i = Suggestions.Count - 1; i >= commonCount; i--)
            {
                Suggestions.RemoveAt(i);
            }
        }

        NotifySuggestionsChanged();
    }

    #endregion

    #region IDisposable

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            _isDisposed = true;

            Items.CollectionChanged -= OnItemsCollectionChanged;

            _suggestDebounceTimer.Stop();
            _localSearchDebounceTimer.Stop();

            _suggestCts?.Cancel();
            _suggestCts?.Dispose();
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = null;

            try { _searchSession?.Dispose(); } catch { }
            _searchSession = null;
        }

        base.Dispose(disposing);
    }

    #endregion
}