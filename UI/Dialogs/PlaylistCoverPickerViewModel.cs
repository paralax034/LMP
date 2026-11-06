using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;

namespace LMP.UI.Dialogs;

/// <summary>
/// ViewModel for selecting playlist cover artwork from tracks with automatic reactive mosaic generation.
/// </summary>
public sealed partial class PlaylistCoverPickerViewModel : ViewModelBase
{
    private const int MaxSelection = 4;

    private readonly NetworkManager _networkManager;
    private readonly List<TrackCoverItemViewModel> _selectionOrder = [];
    private readonly Dictionary<string, Bitmap> _bitmapCache = [];
    private readonly SemaphoreSlim _loadSemaphore = new(3, 3);

    private CancellationTokenSource? _previewCts;
    private RenderTargetBitmap? _currentPreview;

    /// <summary>All track cover artworks available for selection.</summary>
    public ObservableCollection<TrackCoverItemViewModel> TrackCovers { get; } = [];

    /// <summary>Current mosaic preview.</summary>
    [ObservableProperty]
    public partial Bitmap? MosaicPreview { get; set; }

    /// <summary>Flag indicating whether a generated preview is available.</summary>
    [ObservableProperty]
    public partial bool HasPreview { get; set; }

    /// <summary>Status text showing the count of selected artworks.</summary>
    [ObservableProperty]
    public partial string SelectionStatus { get; set; } = "";

    /// <summary>
    /// Event raised when a new mosaic snapshot is generated for immediate binding to the parent editor.
    /// </summary>
    public event Action<Bitmap?>? OnPreviewUpdated;

    /// <summary>
    /// Initializes the track selection model for the mosaic.
    /// </summary>
    /// <param name="tracks">List of available tracks.</param>
    /// <param name="networkManager">Network manager for thumbnail downloading.</param>
    public PlaylistCoverPickerViewModel(IReadOnlyList<TrackInfo> tracks, NetworkManager networkManager)
    {
        _networkManager = networkManager;

        var seenUrls = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            if (!track.HasThumbnail || !seenUrls.Add(track.ThumbnailUrl))
                continue;

            var item = new TrackCoverItemViewModel(track.Id, track.ThumbnailUrl, track.Title);
            item.ToggleCommand = new RelayCommand(() => ToggleSelection(item));
            TrackCovers.Add(item);
        }

        UpdateSelectionStatus();
    }

    /// <summary>
    /// Returns the track IDs selected for the current mosaic.
    /// </summary>
    public List<string> GetSelectedTrackIds()
    {
        var ids = new List<string>(_selectionOrder.Count);
        for (int i = 0; i < _selectionOrder.Count; i++)
            ids.Add(_selectionOrder[i].TrackId);
        return ids;
    }

    /// <summary>
    /// Toggles the selection state of a cover artwork item.
    /// </summary>
    private void ToggleSelection(TrackCoverItemViewModel item)
    {
        if (item.IsSelected)
        {
            item.IsSelected = false;
            _selectionOrder.Remove(item);
            item.SelectionOrder = 0;
        }
        else
        {
            if (_selectionOrder.Count >= MaxSelection)
            {
                var first = _selectionOrder[0];
                first.IsSelected = false;
                first.SelectionOrder = 0;
                _selectionOrder.RemoveAt(0);
            }

            item.IsSelected = true;
            _selectionOrder.Add(item);
        }

        for (int i = 0; i < _selectionOrder.Count; i++)
            _selectionOrder[i].SelectionOrder = i + 1;

        UpdateSelectionStatus();
        RegeneratePreview();
    }

    private void UpdateSelectionStatus()
    {
        SelectionStatus = string.Format(
            SL["CoverPicker_Status"],
            _selectionOrder.Count, MaxSelection);
    }

    private void RegeneratePreview()
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = new CancellationTokenSource();
        var ct = _previewCts.Token;

        var selectedItems = _selectionOrder.ToList();
        if (selectedItems.Count == 0)
        {
            ClearPreview();
            return;
        }

        _ = GeneratePreviewInternalAsync(selectedItems, ct);
    }

    private async Task GeneratePreviewInternalAsync(List<TrackCoverItemViewModel> selectedItems, CancellationToken ct)
    {
        try
        {
            await Task.Delay(100, ct).ConfigureAwait(true);

            var bitmaps = new List<Bitmap>(selectedItems.Count);
            for (int i = 0; i < selectedItems.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var bmp = await GetOrLoadBitmapAsync(selectedItems[i].ThumbnailUrl, ct).ConfigureAwait(true);
                if (bmp != null)
                    bitmaps.Add(bmp);
            }

            if (ct.IsCancellationRequested) return;

            if (bitmaps.Count == 0)
            {
                ClearPreview();
                return;
            }

            var oldPreview = _currentPreview;
            _currentPreview = MosaicGenerator.GeneratePreview(bitmaps);
            MosaicPreview = _currentPreview;
            HasPreview = true;
            oldPreview?.Dispose();

            OnPreviewUpdated?.Invoke(_currentPreview);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"[CoverPicker] Preview generation failed: {ex.Message}");
            if (!ct.IsCancellationRequested)
                ClearPreview();
        }
    }

    private void ClearPreview()
    {
        var old = _currentPreview;
        _currentPreview = null;
        MosaicPreview = null;
        HasPreview = false;
        old?.Dispose();
        OnPreviewUpdated?.Invoke(null);
    }

    private async Task<Bitmap?> GetOrLoadBitmapAsync(string url, CancellationToken ct)
    {
        if (_bitmapCache.TryGetValue(url, out var cached))
            return cached;

        await _loadSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_bitmapCache.TryGetValue(url, out cached))
                return cached;

            var data = await _networkManager.ImageClient.GetByteArrayAsync(url, ct).ConfigureAwait(false);
            using var stream = new MemoryStream(data);
            var bitmap = new Bitmap(stream);
            _bitmapCache[url] = bitmap;
            return bitmap;
        }
        catch
        {
            return null;
        }
        finally
        {
            _loadSemaphore.Release();
        }
    }

    /// <summary>
    /// Saves the generated mosaic to a disk file and returns its path.
    /// </summary>
    public async Task<string?> PersistMosaicAsync(CancellationToken ct = default)
    {
        var selectedItems = _selectionOrder.ToList();
        if (selectedItems.Count == 0) return null;

        var bitmaps = new List<Bitmap>();
        var trackIds = new List<string>();

        for (int i = 0; i < selectedItems.Count; i++)
        {
            var bmp = await GetOrLoadBitmapAsync(selectedItems[i].ThumbnailUrl, ct).ConfigureAwait(true);
            if (bmp != null)
            {
                bitmaps.Add(bmp);
                trackIds.Add(selectedItems[i].TrackId);
            }
        }

        if (bitmaps.Count == 0 || ct.IsCancellationRequested) return null;

        return await MosaicGenerator.GenerateAsync(bitmaps, trackIds, ct).ConfigureAwait(true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _previewCts?.Cancel();
            _previewCts?.Dispose();
            _currentPreview?.Dispose();
            _currentPreview = null;

            foreach (var bmp in _bitmapCache.Values)
                bmp.Dispose();
            _bitmapCache.Clear();

            _loadSemaphore.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// ViewModel for an individual track cover artwork item in the selection grid.
/// </summary>
public sealed partial class TrackCoverItemViewModel : ObservableObject
{
    /// <summary>Track ID (used for generating mosaic filenames).</summary>
    public string TrackId { get; }

    /// <summary>Track cover artwork URL.</summary>
    public string ThumbnailUrl { get; }

    /// <summary>Track title (used for tooltips).</summary>
    public string TrackTitle { get; }

    /// <summary>Indicates whether the track is selected for the mosaic.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>1-based selection order index (1-4). 0 = not selected.</summary>
    [ObservableProperty]
    public partial int SelectionOrder { get; set; }

    /// <summary>Selection toggle command. Set by the parent VM.</summary>
    public IRelayCommand? ToggleCommand { get; set; }

    public TrackCoverItemViewModel(string trackId, string thumbnailUrl, string trackTitle)
    {
        TrackId = trackId;
        ThumbnailUrl = thumbnailUrl;
        TrackTitle = trackTitle;
    }
}