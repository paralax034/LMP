using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;

namespace LMP.UI.Dialogs;

/// <summary>
/// ViewModel выбора обложки плейлиста из треков с автоматической реактивной генерацией мозаики.
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

    /// <summary>Все обложки треков для выбора.</summary>
    public ObservableCollection<TrackCoverItemViewModel> TrackCovers { get; } = [];

    /// <summary>Текущее превью мозаики.</summary>
    [ObservableProperty]
    public partial Bitmap? MosaicPreview { get; set; }

    /// <summary>Флаг наличия готового превью.</summary>
    [ObservableProperty]
    public partial bool HasPreview { get; set; }

    /// <summary>Статус количества выбранных обложек.</summary>
    [ObservableProperty]
    public partial string SelectionStatus { get; set; } = "";

    /// <summary>
    /// Событие генерации нового снимка мозаики для моментального связывания с родительским редактором.
    /// </summary>
    public event Action<Bitmap?>? OnPreviewUpdated;

    /// <summary>
    /// Инициализирует модель выбора треков для мозаики.
    /// </summary>
    /// <param name="tracks">Список доступных треков.</param>
    /// <param name="networkManager">Сетевой менеджер загрузки миниатюр.</param>
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
    /// Возвращает идентификаторы треков, выбранных в текущую мозаику.
    /// </summary>
    public List<string> GetSelectedTrackIds()
    {
        var ids = new List<string>(_selectionOrder.Count);
        for (int i = 0; i < _selectionOrder.Count; i++)
            ids.Add(_selectionOrder[i].TrackId);
        return ids;
    }

    /// <summary>
    /// Переключает выбор элемента обложки.
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
            SL["CoverPicker_Status"] ?? "{0}/{1}",
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
    /// Сохраняет сгенерированную мозаику в дисковый файл и возвращает путь.
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
/// ViewModel одной обложки трека в сетке выбора.
/// </summary>
public sealed partial class TrackCoverItemViewModel : ObservableObject
{
    /// <summary>ID трека (для генерации имени файла мозаики).</summary>
    public string TrackId { get; }

    /// <summary>URL обложки трека.</summary>
    public string ThumbnailUrl { get; }

    /// <summary>Название трека (для tooltip).</summary>
    public string TrackTitle { get; }

    /// <summary>Выбран ли трек для мозаики.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Порядковый номер выбора (1-4). 0 = не выбран.</summary>
    [ObservableProperty]
    public partial int SelectionOrder { get; set; }

    /// <summary>Команда переключения выбора. Устанавливается parent VM.</summary>
    public IRelayCommand? ToggleCommand { get; set; }

    public TrackCoverItemViewModel(string trackId, string thumbnailUrl, string trackTitle)
    {
        TrackId = trackId;
        ThumbnailUrl = thumbnailUrl;
        TrackTitle = trackTitle;
    }
}