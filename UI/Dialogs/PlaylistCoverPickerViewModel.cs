using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;

namespace LMP.UI.Dialogs;

/// <summary>
/// ViewModel для контрола выбора обложки из треков плейлиста.
/// 
/// <para><b>Поведение:</b></para>
/// <list type="bullet">
///   <item>Показывает сетку обложек треков (дедупликация по URL)</item>
///   <item>Пользователь выбирает от 1 до 4 треков</item>
///   <item>Превью мозаики генерируется в реальном времени</item>
///   <item>Apply сохраняет мозаику как PNG → устанавливает ResultPath</item>
/// </list>
/// 
/// <para><b>Ограничение:</b> максимум 4 выбранных трека.
/// При попытке выбрать 5-й — первый автоматически снимается (FIFO).</para>
/// 
/// <para><b>Потокобезопасность:</b></para>
/// <para><c>_selectionOrder</c> модифицируется только на UI-потоке (через ToggleSelection).
/// Перед итерацией в async-методах делается snapshot через <c>.ToList()</c>.</para>
/// </summary>
public sealed partial class PlaylistCoverPickerViewModel : ViewModelBase
{
    /// <summary>Максимальное количество выбранных обложек.</summary>
    private const int MaxSelection = 4;

    /// <summary>
    /// Порядок выбора (FIFO для автоснятия при переполнении).
    /// Модифицируется ТОЛЬКО на UI-потоке.
    /// </summary>
    private readonly List<TrackCoverItemViewModel> _selectionOrder = [];

    /// <summary>
    /// Кэш загруженных Bitmap для превью.
    /// Key = ThumbnailUrl, Value = загруженный Bitmap.
    /// Защищён семафором для предотвращения параллельных загрузок одного URL.
    /// </summary>
    private readonly Dictionary<string, Bitmap> _bitmapCache = [];

    /// <summary>Семафор для ограничения параллельных HTTP-запросов.</summary>
    private readonly SemaphoreSlim _loadSemaphore = new(3, 3);

    private CancellationTokenSource? _previewCts;

    /// <summary>
    /// Статический HttpClient — переиспользуется для всех загрузок.
    /// Избегает IOException от множественных параллельных создании HttpClient.
    /// </summary>
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    /// <summary>Текущий превью-bitmap (для Dispose).</summary>
    private RenderTargetBitmap? _currentPreview;

    /// <summary>Все обложки треков для выбора.</summary>
    public ObservableCollection<TrackCoverItemViewModel> TrackCovers { get; } = [];

    /// <summary>Превью сгенерированной мозаики.</summary>
    [ObservableProperty]
    public partial Bitmap? MosaicPreview { get; set; }

    /// <summary>Есть ли превью для отображения.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool HasPreview { get; set; }

    /// <summary>Текст подсказки: "Выберите 1-4 обложки".</summary>
    [ObservableProperty]
    public partial string SelectionHint { get; set; } = "";

    /// <summary>Статус выбора: "Выбрано: 2/4".</summary>
    [ObservableProperty]
    public partial string SelectionStatus { get; set; } = "";

    /// <summary>
    /// Результат: путь к сохранённому PNG файлу мозаики.
    /// Заполняется после Apply. null = ещё не применено.
    /// </summary>
    [ObservableProperty]
    public partial string? ResultPath { get; set; }

    /// <summary>Применить мозаику: сохраняет PNG и устанавливает ResultPath.</summary>
    public IAsyncRelayCommand ApplyCommand { get; }

    /// <summary>
    /// Создаёт VM для выбора обложки из треков.
    /// </summary>
    /// <param name="tracks">Треки плейлиста (нужны ThumbnailUrl и Id).</param>
    public PlaylistCoverPickerViewModel(IReadOnlyList<TrackInfo> tracks)
    {
        SelectionHint = SL["CoverPicker_Hint"] ?? "1-4";

        // Фильтруем треки с обложками, убираем дубликаты URL
        var seenUrls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var track in tracks)
        {
            if (!track.HasThumbnail || !seenUrls.Add(track.ThumbnailUrl))
                continue;

            var item = new TrackCoverItemViewModel(track.Id, track.ThumbnailUrl, track.Title);
            TrackCovers.Add(item);
        }

        // Команда Apply: сохраняет мозаику на диск
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => HasPreview);

        // Подписка на toggle каждого элемента
        foreach (var item in TrackCovers)
        {
            item.ToggleCommand = new RelayCommand(() => ToggleSelection(item));
        }

        UpdateSelectionStatus();
    }

    /// <summary>
    /// Переключает выбор обложки.
    /// При превышении MaxSelection автоматически снимает первую выбранную (FIFO).
    /// После переключения обновляет превью.
    /// Вызывается только на UI-потоке.
    /// </summary>
    private void ToggleSelection(TrackCoverItemViewModel item)
    {
        if (item.IsSelected)
        {
            // Снимаем выбор
            item.IsSelected = false;
            _selectionOrder.Remove(item);
            item.SelectionOrder = 0;
        }
        else
        {
            // Если лимит достигнут — снимаем первый выбранный (FIFO)
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

        // Обновляем номера порядка выбора
        for (int i = 0; i < _selectionOrder.Count; i++)
            _selectionOrder[i].SelectionOrder = i + 1;

        UpdateSelectionStatus();

        // Генерируем превью (snapshot списка для потокобезопасности)
        RegeneratePreviewAsync();
    }

    /// <summary>
    /// Обновляет текст статуса выбора.
    /// </summary>
    private void UpdateSelectionStatus()
    {
        var count = _selectionOrder.Count;
        SelectionStatus = string.Format(
            SL["CoverPicker_Status"] ?? "{0}/{1}",
            count, MaxSelection);
    }

    /// <summary>
    /// Регенерирует превью мозаики.
    /// Отменяет предыдущую генерацию при быстрых кликах (debounce 150ms).
    /// Делает snapshot <c>_selectionOrder</c> перед async-операцией
    /// для предотвращения <c>Collection was modified</c>.
    /// </summary>
    private async void RegeneratePreviewAsync()
    {
        // Отменяем предыдущую генерацию (предотвращает параллельные загрузки)
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = new CancellationTokenSource();
        var ct = _previewCts.Token;

        // Snapshot на UI-потоке ДО любой async операции
        var selectedItems = _selectionOrder.ToList();

        if (selectedItems.Count == 0)
        {
            ClearPreview();
            return;
        }

        try
        {
            // Debounce: ждём 150ms перед генерацией (быстрые клики отменяют предыдущий)
            await Task.Delay(150, ct);

            // Загружаем недостающие Bitmap
            var bitmaps = new List<Bitmap>(selectedItems.Count);
            foreach (var item in selectedItems)
            {
                ct.ThrowIfCancellationRequested();
                var bmp = await GetOrLoadBitmapAsync(item.ThumbnailUrl);
                if (bmp != null)
                    bitmaps.Add(bmp);
            }

            if (ct.IsCancellationRequested) return;

            if (bitmaps.Count == 0)
            {
                ClearPreview();
                return;
            }

            // Генерируем превью на UI-потоке (Avalonia rendering requirement)
            var oldPreview = _currentPreview;
            _currentPreview = MosaicGenerator.GeneratePreview(bitmaps);
            MosaicPreview = _currentPreview;
            HasPreview = true;
            oldPreview?.Dispose();
        }
        catch (OperationCanceledException)
        {
            // Нормально — отменено новым кликом
        }
        catch (Exception ex)
        {
            Log.Warn($"[CoverPicker] Preview generation failed: {ex.Message}");
            if (!ct.IsCancellationRequested)
                ClearPreview();
        }
    }

    /// <summary>
    /// Очищает текущее превью и освобождает ресурсы.
    /// </summary>
    private void ClearPreview()
    {
        var old = _currentPreview;
        _currentPreview = null;
        MosaicPreview = null;
        HasPreview = false;
        old?.Dispose();
    }

    /// <summary>
    /// Загружает Bitmap из URL с кэшированием и ограничением параллелизма.
    /// 
    /// <para><b>Потокобезопасность:</b> семафор ограничивает до 3 параллельных загрузок,
    /// предотвращая IOException от перегрузки сети.</para>
    /// 
    /// <para><b>Кэширование:</b> один Bitmap на URL — не загружаем повторно.</para>
    /// </summary>
    private async Task<Bitmap?> GetOrLoadBitmapAsync(string url)
    {
        // Быстрый путь: уже в кэше
        if (_bitmapCache.TryGetValue(url, out var cached))
            return cached;

        await _loadSemaphore.WaitAsync();
        try
        {
            // Double-check после ожидания семафора
            if (_bitmapCache.TryGetValue(url, out cached))
                return cached;

            var data = await SharedHttpClient.GetByteArrayAsync(url);
            using var stream = new MemoryStream(data);
            var bitmap = new Bitmap(stream);
            _bitmapCache[url] = bitmap;
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Warn($"[CoverPicker] Failed to load bitmap from {url}: {ex.Message}");
            return null;
        }
        finally
        {
            _loadSemaphore.Release();
        }
    }

    /// <summary>
    /// Сохраняет мозаику как PNG файл и устанавливает ResultPath.
    /// </summary>
    private async Task ApplyAsync(CancellationToken ct)
    {
        // Snapshot на UI-потоке
        var selectedItems = _selectionOrder.ToList();
        if (selectedItems.Count == 0) return;

        try
        {
            var bitmaps = new List<Bitmap>();
            var trackIds = new List<string>();

            foreach (var item in selectedItems)
            {
                var bmp = await GetOrLoadBitmapAsync(item.ThumbnailUrl);
                if (bmp != null)
                {
                    bitmaps.Add(bmp);
                    trackIds.Add(item.TrackId);
                }
            }

            if (bitmaps.Count == 0) return;

            ResultPath = await MosaicGenerator.GenerateAsync(bitmaps, trackIds, ct);
        }
        catch (Exception ex)
        {
            Log.Error($"[CoverPicker] Apply failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Очищает кэш Bitmap, превью, CTS и семафор при Dispose.
    /// </summary>
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