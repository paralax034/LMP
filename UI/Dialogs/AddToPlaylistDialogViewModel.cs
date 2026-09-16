using System.Collections.ObjectModel;
using Avalonia.Threading;

namespace LMP.UI.Dialogs;

public sealed partial class AddToPlaylistDialogViewModel : ViewModelBase
{
    public TrackInfo Track { get; }
    public string TrackDisplayName { get; }

    private readonly List<PlaylistCheckItem> _allItems = [];
    public ObservableCollection<PlaylistCheckItem> FilteredPlaylists { get; } = [];

    [ObservableProperty]
    public partial string FilterQuery { get; set; } = "";

    [ObservableProperty]
    public partial string SummaryText { get; set; } = "";

    private readonly DispatcherTimer _filterDebounceTimer;

    /// <summary>
    /// Callback для закрытия диалога с результатом.
    /// </summary>
    public Action<List<string>>? OnResult { get; set; }

    public IRelayCommand ConfirmCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public AddToPlaylistDialogViewModel(TrackInfo track, IEnumerable<Playlist> playlists)
    {
        Track = track;
        TrackDisplayName = $"{track.Author} — {track.Title}";

        _filterDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _filterDebounceTimer.Tick += (s, e) =>
        {
            _filterDebounceTimer.Stop();
            ApplyFilter();
        };

        foreach (var p in playlists)
        {
            if (p.Id == LibraryService.LikedPlaylistId) continue;
            if (!p.IsEditable) continue;

            var item = new PlaylistCheckItem(p, track.InPlaylists.Contains(p.Id));
            item.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(PlaylistCheckItem.IsChecked))
                    UpdateSummary();
            };
            _allItems.Add(item);
        }

        ConfirmCommand = new RelayCommand(() =>
        {
            var result = new List<string>();
            for (int i = 0; i < _allItems.Count; i++)
            {
                if (_allItems[i].IsChecked && !_allItems[i].WasAlreadyIn)
                    result.Add(_allItems[i].PlaylistId);
            }
            OnResult?.Invoke(result);
        });

        CancelCommand = new RelayCommand(() =>
        {
            OnResult?.Invoke([]);
        });

        ApplyFilter();
        UpdateSummary();
    }

    partial void OnFilterQueryChanged(string value)
    {
        _filterDebounceTimer.Stop();
        _filterDebounceTimer.Start();
    }

    private void ApplyFilter()
    {
        FilteredPlaylists.Clear();

        var query = FilterQuery?.Trim() ?? "";
        bool hasQuery = query.Length > 0;

        var matched = new List<PlaylistCheckItem>(_allItems.Count);
        for (int i = 0; i < _allItems.Count; i++)
        {
            var item = _allItems[i];
            if (hasQuery && !item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            matched.Add(item);
        }

        matched.Sort(static (a, b) =>
        {
            if (a.IsChecked != b.IsChecked)
                return a.IsChecked ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        for (int i = 0; i < matched.Count; i++)
            FilteredPlaylists.Add(matched[i]);
    }

    private void UpdateSummary()
    {
        int newCount = 0;
        for (int i = 0; i < _allItems.Count; i++)
        {
            if (_allItems[i].IsChecked && !_allItems[i].WasAlreadyIn)
                newCount++;
        }

        SummaryText = newCount > 0
            ? string.Format(SL["AddToPlaylist_Selected"], newCount)
            : SL["AddToPlaylist_NoneSelected"];
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _filterDebounceTimer.Stop();
        }
        base.Dispose(disposing);
    }
}

public sealed partial class PlaylistCheckItem : ObservableObject
{
    public string PlaylistId { get; }
    public string Name { get; }
    public int TrackCount { get; }
    public bool WasAlreadyIn { get; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    public PlaylistCheckItem(Playlist playlist, bool alreadyContains)
    {
        PlaylistId = playlist.Id;
        Name = playlist.Name;
        TrackCount = playlist.TrackCount;
        WasAlreadyIn = alreadyContains;
        IsChecked = alreadyContains;
    }
}