using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.UI.Dialogs;

/// <summary>
/// Режим выбора обложки плейлиста.
/// </summary>
public enum CoverMode
{
    /// <summary>Ручной ввод URL.</summary>
    Url,
    /// <summary>Выбор из обложек треков (мозаика).</summary>
    FromTracks,
    /// <summary>Выбор локального файла.</summary>
    File
}

/// <summary>
/// ViewModel редактора плейлиста. Используется как для создания, так и для редактирования.
/// Создаётся через фабричные методы <see cref="ForCreate"/> и <see cref="ForEdit"/>.
/// </summary>
public sealed partial class PlaylistEditorViewModel : ViewModelBase
{
    private readonly INetworkManager? _networkManager;
    private readonly DominantColorService? _dominantColorService;
    private readonly Lazy<YoutubeProvider>? _youtube;

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string? ThumbnailUrl { get; set; }

    [ObservableProperty]
    public partial string? CustomColor { get; set; }

    [ObservableProperty]
    public partial string? Description { get; set; }

    /// <summary>Исходное описание (для определения изменения).</summary>
    private readonly string? _originalDescription;

    /// <summary>
    /// Оригинальный плейлист (для доступа к YoutubeId и SyncMode).
    /// null для режима создания.
    /// </summary>
    private readonly Playlist? _originalPlaylist;

    /// <summary>true если VM создана для редактирования (а не создания).</summary>
    private readonly bool _isForEdit;

    // ComputedColor

    /// <summary>
    /// Автоматически вычисленный цвет из обложки (readonly, из БД).
    /// Показывается в UI как информационное поле.
    /// Обновляется при пересчёте через RecalculateColorCommand.
    /// </summary>
    [ObservableProperty]
    public partial string? ComputedColor { get; set; }

    /// <summary>Кисть превью вычисленного цвета.</summary>
    [ObservableProperty]
    public partial IBrush ComputedColorPreviewBrush { get; set; } = Brushes.Transparent;

    /// <summary>
    /// Идёт ли пересчёт цвета из обложки или загрузка обложки в YouTube.
    /// Используется для блокировки UI во время длительных операций.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecalculateColorCommand))]
    [NotifyCanExecuteChangedFor(nameof(UploadThumbnailCommand))]
    public partial bool IsRecalculatingColor { get; set; }

    /// <summary>Команда пересчёта доминантного цвета из текущей обложки.</summary>
    public IAsyncRelayCommand RecalculateColorCommand { get; }

    // System Playlist

    /// <summary>
    /// true если редактируется системный плейлист (например «Понравившиеся»).
    /// Для системных плейлистов имя задаётся локализацией и недоступно для ручного ввода.
    /// </summary>
    public bool IsSystemPlaylist { get; }

    /// <summary>
    /// Разрешено ли редактировать имя плейлиста.
    /// false для системных плейлистов — имя управляется <see cref="LocalizationService"/>.
    /// </summary>
    public bool IsNameEditable { get; }

    // For Edit / Create Copy

    /// <summary>
    /// true если VM создана для редактирования существующего плейлиста.
    /// Используется в UI для отображения кнопки «Создать копию».
    /// </summary>
    public bool IsForEdit { get; }

    /// <summary>
    /// Callback, вызываемый при нажатии кнопки «Создать копию».
    /// Устанавливается в <see cref="EditPlaylistDialogViewModel"/>.
    /// </summary>
    public Action? OnCreateCopy { get; set; }

    /// <summary>
    /// Создаёт локальную копию плейлиста с текущими данными из редактора.
    /// Копия всегда локальная (без привязки к YouTube).
    /// </summary>
    public IRelayCommand CreateCopyCommand { get; }

    // Cover Mode

    /// <summary>Текущий режим выбора обложки: URL, из треков, или файл.</summary>
    [ObservableProperty]
    public partial CoverMode SelectedCoverMode { get; set; } = CoverMode.Url;

    /// <summary>true если выбран режим ручного URL.</summary>
    [ObservableProperty]
    public partial bool IsCoverModeUrl { get; set; } = true;

    /// <summary>true если выбран режим "Из треков".</summary>
    [ObservableProperty]
    public partial bool IsCoverModeFromTracks { get; set; }

    /// <summary>true если выбран режим "Файл".</summary>
    [ObservableProperty]
    public partial bool IsCoverModeFile { get; set; }

    /// <summary>
    /// ViewModel выбора обложки из треков. null если треки не предоставлены.
    /// </summary>
    [ObservableProperty]
    public partial PlaylistCoverPickerViewModel? CoverPicker { get; set; }

    /// <summary>
    /// Показывать ли переключатель режима обложки.
    /// Всегда true — минимум URL + File.
    /// </summary>
    public bool ShowCoverModeSwitch { get; } = true;

    /// <summary>Показывать ли вкладку "Из треков".</summary>
    public bool HasTracksCoverOption { get; }

    /// <summary>Путь выбранного файла (для отображения в UI).</summary>
    [ObservableProperty]
    public partial string? SelectedFilePath { get; set; }

    /// <summary>Команда выбора файла через системный диалог.</summary>
    public IAsyncRelayCommand SelectFileCommand { get; }

    // Upload Thumbnail to YouTube

    /// <summary>
    /// Показывать ли кнопку загрузки обложки в YouTube.
    /// Видна только для TwoWaySync плейлистов с непустой обложкой.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UploadThumbnailCommand))]
    public partial bool ShowUploadThumbnailButton { get; set; }

    /// <summary>Загрузить текущую обложку в YouTube.</summary>
    public IAsyncRelayCommand UploadThumbnailCommand { get; }

    // Sync

    [ObservableProperty]
    public partial bool ShowSyncSection { get; set; }

    [ObservableProperty]
    public partial bool IsSyncedToCloud { get; set; }
    public bool IsAuthenticated { get; }
    public bool HasYoutubeBinding { get; }
    public bool OriginalSyncState { get; }

    // Validation
    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool HasErrors { get; set; }

    public bool CanSave => !HasErrors;

    // Preview

    /// <summary>
    /// URL или путь для превью обложки (HTTP URL или локальный путь).
    /// </summary>
    [ObservableProperty]
    public partial string? ThumbnailPreviewUrl { get; set; }

    /// <summary>Есть ли превью для отображения.</summary>
    [ObservableProperty]
    public partial bool HasThumbnailPreview { get; set; }

    /// <summary>Превью — это HTTP URL (для AsyncImageLoader).</summary>
    [ObservableProperty]
    public partial bool IsPreviewHttp { get; set; }

    /// <summary>Превью — это локальный файл (для LocalFileImageConverter).</summary>
    [ObservableProperty]
    public partial bool IsPreviewLocal { get; set; }

    /// <summary>
    /// Bitmap превью для локальных файлов (загружается напрямую).
    /// Для HTTP URL остаётся null — используется AsyncImageLoader.
    /// </summary>
    [ObservableProperty]
    public partial Bitmap? LocalPreviewBitmap { get; set; }

    [ObservableProperty]
    public partial IBrush ColorPreviewBrush { get; set; } = Brushes.Transparent;

    private readonly DispatcherTimer _thumbnailDebounceTimer;
    private readonly DispatcherTimer _colorDebounceTimer;

    /// <summary>
    /// Инициализирует новый экземпляр редактора плейлиста.
    /// Автоматически активирует режим выбора обложки из треков, если плейлист не имеет обложки, но содержит треки.
    /// </summary>
    public PlaylistEditorViewModel(
        string name = "",
        string? thumbnailUrl = null,
        string? customColor = null,
        string? description = null,
        string? computedColor = null,
        bool showSync = false,
        bool isSynced = false,
        bool isAuthenticated = false,
        bool hasYoutubeBinding = false,
        IReadOnlyList<TrackInfo>? playlistTracks = null,
        Playlist? originalPlaylist = null,
        bool isForEdit = false,
        bool isSystemPlaylist = false,
        INetworkManager? networkManager = null,
        DominantColorService? dominantColorService = null,
        Lazy<YoutubeProvider>? youtube = null)
    {
        _networkManager = networkManager ?? AppEntry.Services.GetService<INetworkManager>();
        _dominantColorService = dominantColorService ?? AppEntry.Services.GetService<DominantColorService>();
        _youtube = youtube ?? AppEntry.Services.GetService<Lazy<YoutubeProvider>>();
        _originalDescription = description;
        _originalPlaylist = originalPlaylist;
        _isForEdit = isForEdit;
        IsForEdit = isForEdit;
        IsSystemPlaylist = isSystemPlaylist;
        IsNameEditable = !isSystemPlaylist;
        ComputedColor = computedColor;
        ShowSyncSection = showSync;
        IsSyncedToCloud = isSynced;
        OriginalSyncState = isSynced;
        IsAuthenticated = isAuthenticated;
        HasYoutubeBinding = hasYoutubeBinding;

        ComputedColorPreviewBrush = TryParseColor(computedColor);

        _thumbnailDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _thumbnailDebounceTimer.Tick += (s, e) =>
        {
            _thumbnailDebounceTimer.Stop();
            UpdateThumbnailPreview(ThumbnailUrl);
            _ = AutoRecalculateColorAsync();
        };

        _colorDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _colorDebounceTimer.Tick += (s, e) =>
        {
            _colorDebounceTimer.Stop();
            ColorPreviewBrush = TryParseColor(CustomColor);
        };

        HasTracksCoverOption = playlistTracks != null && playlistTracks.Any(t => t.HasThumbnail) && _networkManager != null;
        if (HasTracksCoverOption && _networkManager != null)
        {
            CoverPicker = new PlaylistCoverPickerViewModel(playlistTracks!, _networkManager);
            CoverPicker.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(PlaylistCoverPickerViewModel.ResultPath) && CoverPicker?.ResultPath is { Length: > 0 } path)
                {
                    ThumbnailUrl = path;
                    SelectedCoverMode = CoverMode.Url;
                }
            };
        }

        SelectFileCommand = new AsyncRelayCommand(SelectFileAsync);

        RecalculateColorCommand = new AsyncRelayCommand(
            RecalculateColorFromCoverAsync,
            () => !string.IsNullOrWhiteSpace(ThumbnailUrl) && !IsRecalculatingColor);

        UploadThumbnailCommand = new AsyncRelayCommand(
            UploadThumbnailAsync,
            () => !string.IsNullOrEmpty(ThumbnailUrl) && !IsRecalculatingColor && ShowUploadThumbnailButton);

        // Команда создания копии
        CreateCopyCommand = new RelayCommand(() =>
        {
            OnCreateCopy?.Invoke();
        });

        Name = name;
        ThumbnailUrl = thumbnailUrl;
        CustomColor = customColor;
        Description = description;

        // Автоматически открываем мозаику из треков, если своей обложки у плейлиста еще нет
        if (string.IsNullOrWhiteSpace(thumbnailUrl) && HasTracksCoverOption)
        {
            SelectedCoverMode = CoverMode.FromTracks;
        }
        else
        {
            SelectedCoverMode = CoverMode.Url;
        }

        // Синхронизируем булевы флаги видимости вкладок в соответствии с выбранным режимом
        IsCoverModeUrl = SelectedCoverMode == CoverMode.Url;
        IsCoverModeFromTracks = SelectedCoverMode == CoverMode.FromTracks;
        IsCoverModeFile = SelectedCoverMode == CoverMode.File;

        UpdateValidation();
        UpdateUploadButtonVisibility();
        UpdateThumbnailPreview(ThumbnailUrl);

        if (string.IsNullOrEmpty(computedColor) && !string.IsNullOrWhiteSpace(thumbnailUrl) && IsValidUri(thumbnailUrl))
        {
            _ = AutoRecalculateColorAsync();
        }
    }

    partial void OnSelectedCoverModeChanged(CoverMode value)
    {
        IsCoverModeUrl = value == CoverMode.Url;
        IsCoverModeFromTracks = value == CoverMode.FromTracks;
        IsCoverModeFile = value == CoverMode.File;
    }

    partial void OnNameChanged(string value) => UpdateValidation();

    partial void OnThumbnailUrlChanged(string? value)
    {
        UpdateValidation();
        UpdateUploadButtonVisibility();
        RecalculateColorCommand?.NotifyCanExecuteChanged();
        UploadThumbnailCommand?.NotifyCanExecuteChanged();

        if (_thumbnailDebounceTimer is not null)
        {
            _thumbnailDebounceTimer.Stop();
            _thumbnailDebounceTimer.Start();
        }
    }

    partial void OnCustomColorChanged(string? value)
    {
        UpdateValidation();
        if (_colorDebounceTimer is not null)
        {
            _colorDebounceTimer.Stop();
            _colorDebounceTimer.Start();
        }
    }

    #region Upload Thumbnail to YouTube

    /// <summary>
    /// Обновляет видимость кнопки загрузки обложки в YouTube.
    /// Показываем только для TwoWaySync плейлистов с непустой обложкой.
    /// </summary>
    private void UpdateUploadButtonVisibility()
    {
        ShowUploadThumbnailButton =
            _isForEdit &&
            _originalPlaylist?.SyncMode == PlaylistSyncMode.TwoWaySync &&
            !string.IsNullOrEmpty(_originalPlaylist.YoutubeId) &&
            !string.IsNullOrEmpty(ThumbnailUrl);
    }

    /// <summary>
    /// Загружает текущую обложку в YouTube через Scotty Upload Protocol.
    /// Используется для ручной загрузки без синхронизации всего плейлиста.
    /// </summary>
    private async Task UploadThumbnailAsync()
    {
        if (_originalPlaylist == null || string.IsNullOrEmpty(_originalPlaylist.YoutubeId))
            return;

        if (string.IsNullOrEmpty(ThumbnailUrl))
        {
            SetError(SL["EditPlaylist_NoThumbnail"] ?? "No thumbnail to upload");
            return;
        }

        IsRecalculatingColor = true;
        ClearError();

        try
        {
            if (_youtube == null || _networkManager == null)
            {
                SetError("Required services not initialized");
                return;
            }

            byte[] imageData;

            if (IsHttpUrl(ThumbnailUrl))
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                imageData = await _networkManager.ImageClient.GetByteArrayAsync(ThumbnailUrl, cts.Token);
            }
            else if (ThumbnailUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(ThumbnailUrl);
                var localPath = uri.LocalPath;

                if (!File.Exists(localPath))
                {
                    SetError(SL["EditPlaylist_FileNotFound"] ?? "File not found");
                    return;
                }

                imageData = await File.ReadAllBytesAsync(localPath);
            }
            else if (Path.IsPathRooted(ThumbnailUrl) && File.Exists(ThumbnailUrl))
            {
                imageData = await File.ReadAllBytesAsync(ThumbnailUrl);
            }
            else
            {
                SetError(SL["EditPlaylist_InvalidThumbnailUrl"] ?? "Invalid thumbnail URL");
                return;
            }

            if (imageData.Length == 0)
            {
                SetError(SL["EditPlaylist_EmptyImage"] ?? "Image file is empty");
                return;
            }

            if (imageData.Length > 20 * 1024 * 1024)
            {
                SetError(SL["PlaylistSync_ThumbnailTooLarge"] ?? "Image too large (max 20MB)");
                return;
            }

            var success = await _youtube.Value.UploadPlaylistThumbnailAsync(
                _originalPlaylist.YoutubeId, imageData);

            if (success)
            {
                Log.Info($"[PlaylistEditor] Thumbnail uploaded for {_originalPlaylist.YoutubeId}");
                ErrorMessage = "✓ " + (SL["PlaylistSync_ThumbnailUploaded"] ?? "Thumbnail uploaded to YouTube");
                HasErrors = false;
                _ = ClearSuccessMessageAsync();
            }
            else
            {
                SetError(SL["EditPlaylist_UploadFailed"] ?? "Failed to upload thumbnail");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistEditor] Thumbnail upload error: {ex.Message}");
            SetError(ex.Message);
        }
        finally
        {
            IsRecalculatingColor = false;
        }
    }

    /// <summary>
    /// Очищает сообщение об успехе через 3 секунды.
    /// </summary>
    private async Task ClearSuccessMessageAsync()
    {
        try
        {
            await Task.Delay(3000);
            if (ErrorMessage?.StartsWith("✓") == true)
                ErrorMessage = null;
        }
        catch { /* ignore */ }
    }

    #endregion

    #region Recalculate Color

    /// <summary>
    /// Автоматически пересчитывает доминантный цвет при изменении обложки плейлиста.
    /// Сбрасывает цвет в прозрачный при пустом или невалидном пути обложки.
    /// </summary>
    /// <returns>Асинхронная задача.</returns>
    private async Task AutoRecalculateColorAsync()
    {
        if (string.IsNullOrWhiteSpace(ThumbnailUrl) || !IsValidUri(ThumbnailUrl))
        {
            ComputedColor = null;
            ComputedColorPreviewBrush = Brushes.Transparent;
            return;
        }

        await RecalculateColorFromCoverAsync(CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>
    /// Пересчитывает доминантный цвет из текущей обложки.
    /// Результат записывается в ComputedColor (будет сохранён при Apply).
    /// </summary>
    private async Task RecalculateColorFromCoverAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ThumbnailUrl)) return;
        if (!IsValidUri(ThumbnailUrl)) return;

        IsRecalculatingColor = true;
        try
        {
            if (_dominantColorService == null)
            {
                Log.Warn("[PlaylistEditor] DominantColorService is not configured");
                return;
            }

            var color = await _dominantColorService.GetDominantColorAsync(ThumbnailUrl, ct);

            if (color.HasValue)
            {
                var hex = $"#{color.Value.R:X2}{color.Value.G:X2}{color.Value.B:X2}";
                ComputedColor = hex;
                ComputedColorPreviewBrush = new SolidColorBrush(color.Value);
                Log.Info($"[PlaylistEditor] Recalculated color: {hex}");
            }
            else
            {
                Log.Warn("[PlaylistEditor] Could not extract dominant color");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistEditor] Color recalculation failed: {ex.Message}");
        }
        finally
        {
            IsRecalculatingColor = false;
        }
    }

    #endregion

    #region Thumbnail Preview

    /// <summary>Определяет, является ли URL HTTP/HTTPS ссылкой.</summary>
    private static bool IsHttpUrl(string url) =>
        url.StartsWith(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Преобразует file:// URI или абсолютный путь в локальный путь.
    /// </summary>
    private static string? ResolveLocalPath(string url)
    {
        if (url.StartsWith(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var fileUri))
                return fileUri.LocalPath;
            return null;
        }

        if (Path.IsPathRooted(url))
            return url;

        return null;
    }

    /// <summary>
    /// Обновляет превью обложки с автоматическим определением типа источника.
    /// HTTP/HTTPS/avares → AsyncImageLoader, локальный файл → Bitmap напрямую.
    /// </summary>
    private void UpdateThumbnailPreview(string? url)
    {
        var oldBitmap = LocalPreviewBitmap;

        if (string.IsNullOrWhiteSpace(url))
        {
            HasThumbnailPreview = false;
            IsPreviewHttp = false;
            IsPreviewLocal = false;
            ThumbnailPreviewUrl = null;
            LocalPreviewBitmap = null;
            oldBitmap?.Dispose();
            return;
        }

        if (IsHttpUrl(url))
        {
            HasThumbnailPreview = true;
            IsPreviewHttp = true;
            IsPreviewLocal = false;
            ThumbnailPreviewUrl = url;
            LocalPreviewBitmap = null;
            oldBitmap?.Dispose();
            return;
        }

        if (url.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
        {
            HasThumbnailPreview = true;
            IsPreviewHttp = true;
            IsPreviewLocal = false;
            ThumbnailPreviewUrl = url;
            LocalPreviewBitmap = null;
            oldBitmap?.Dispose();
            return;
        }

        var localPath = ResolveLocalPath(url);
        if (localPath != null && File.Exists(localPath))
        {
            try
            {
                using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var bitmap = new Bitmap(stream);

                HasThumbnailPreview = true;
                IsPreviewHttp = false;
                IsPreviewLocal = true;
                ThumbnailPreviewUrl = null;
                LocalPreviewBitmap = bitmap;
                oldBitmap?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn($"[PlaylistEditor] Failed to load local preview '{localPath}': {ex.Message}");
                HasThumbnailPreview = false;
                IsPreviewHttp = false;
                IsPreviewLocal = false;
                ThumbnailPreviewUrl = null;
                LocalPreviewBitmap = null;
                oldBitmap?.Dispose();
            }
            return;
        }

        HasThumbnailPreview = false;
        IsPreviewHttp = false;
        IsPreviewLocal = false;
        ThumbnailPreviewUrl = null;
        LocalPreviewBitmap = null;
        oldBitmap?.Dispose();
    }

    #endregion

    #region File Selection

    /// <summary>
    /// Открывает системный диалог выбора файла изображения.
    /// </summary>
    private async Task SelectFileAsync(CancellationToken ct)
    {
        var topLevel = GetTopLevel();
        if (topLevel == null)
        {
            Log.Warn("[PlaylistEditor] Cannot open file picker: no TopLevel found");
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = SL["CoverPicker_SelectFile"] ?? "Select image",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(SL["CoverPicker_ImageFiles"] ?? "Images")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"],
                    MimeTypes = ["image/png", "image/jpeg", "image/webp", "image/bmp"]
                }
            ]
        });

        if (files.Count == 0) return;

        var filePath = files[0].Path.LocalPath;
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

        SelectedFilePath = filePath;
        ThumbnailUrl = filePath;
        UpdateThumbnailPreview(filePath);
        _ = AutoRecalculateColorAsync();
    }

    private static TopLevel? GetTopLevel()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (var window in desktop.Windows)
            {
                if (window.IsActive) return window;
            }
            return desktop.MainWindow;
        }
        return null;
    }

    #endregion

    #region Validation

    private void UpdateValidation()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            SetError(SL["Error_EmptyName"] ?? "Name cannot be empty");
            return;
        }

        if (!string.IsNullOrWhiteSpace(ThumbnailUrl) && !IsValidUri(ThumbnailUrl))
        {
            SetError(SL["Error_InvalidUrl"] ?? "Invalid cover URL format");
            return;
        }

        if (!string.IsNullOrWhiteSpace(CustomColor) && !IsValidColor(CustomColor))
        {
            SetError(SL["Error_InvalidColor"] ?? "Invalid HEX color (example: #FF5555)");
            return;
        }

        ClearError();
    }

    private void SetError(string msg)
    {
        ErrorMessage = msg;
        HasErrors = true;
        OnPropertyChanged(nameof(CanSave));
    }

    private void ClearError()
    {
        ErrorMessage = null;
        HasErrors = false;
        OnPropertyChanged(nameof(CanSave));
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Проверяет, является ли строка валидным источником изображения.
    /// </summary>
    internal static bool IsValidUri(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        if (Path.IsPathRooted(url))
            return true;

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.Scheme switch
            {
                "http" or "https" => true,
                "avares" => true,
                "file" => true,
                _ => false
            };
        }

        return false;
    }

    private static bool IsValidColor(string? colorStr)
    {
        if (string.IsNullOrWhiteSpace(colorStr)) return false;
        try { Color.Parse(colorStr); return true; }
        catch { return false; }
    }

    private static IBrush TryParseColor(string? colorStr)
    {
        if (IsValidColor(colorStr))
            return new SolidColorBrush(Color.Parse(colorStr!));
        return Brushes.Transparent;
    }

    #endregion

    #region Cover Mode Commands

    public void SetCoverModeUrl() => SelectedCoverMode = CoverMode.Url;
    public void SetCoverModeFromTracks() => SelectedCoverMode = CoverMode.FromTracks;
    public void SetCoverModeFile() => SelectedCoverMode = CoverMode.File;

    #endregion

    #region Factory Methods

    /// <summary>
    /// Создаёт VM для создания нового плейлиста.
    /// </summary>
    public static PlaylistEditorViewModel ForCreate(
        INetworkManager? networkManager = null,
        DominantColorService? dominantColorService = null,
        Lazy<YoutubeProvider>? youtube = null) =>
        new(name: "", thumbnailUrl: null, customColor: null, description: null,
            computedColor: null,
            showSync: false, isSynced: false,
            isAuthenticated: false, hasYoutubeBinding: false,
            playlistTracks: null,
            originalPlaylist: null,
            isForEdit: false,
            isSystemPlaylist: false,
            networkManager: networkManager,
            dominantColorService: dominantColorService,
            youtube: youtube);

    /// <summary>
    /// Создаёт VM для редактирования существующего плейлиста.
    /// </summary>
    /// <param name="playlist">Редактируемый плейлист из БД.</param>
    /// <param name="isAuthenticated">Авторизован ли пользователь в YouTube.</param>
    /// <param name="playlistTracks">
    /// Треки плейлиста для <see cref="PlaylistCoverPickerViewModel"/>.
    /// null — вкладка «Из треков» скрыта.
    /// </param>
    /// <param name="networkManager">Централизованный менеджер сети.</param>
    /// <param name="dominantColorService">Сервис доминантных цветов.</param>
    /// <param name="youtube">Провайдер YouTube.</param>
    public static PlaylistEditorViewModel ForEdit(
        Playlist playlist,
        bool isAuthenticated,
        IReadOnlyList<TrackInfo>? playlistTracks = null,
        INetworkManager? networkManager = null,
        DominantColorService? dominantColorService = null,
        Lazy<YoutubeProvider>? youtube = null)
    {
        var isSystem = LibraryService.IsSystemPlaylist(playlist.Id);

        return new(
            name: playlist.Name,
            thumbnailUrl: playlist.ThumbnailUrl,
            customColor: playlist.CustomColor,
            description: playlist.Description,
            computedColor: playlist.ComputedColor,
            showSync: !isSystem && (isAuthenticated || playlist.IsFromAccount),
            isSynced: playlist.IsFromAccount,
            isAuthenticated: isAuthenticated,
            hasYoutubeBinding: !string.IsNullOrEmpty(playlist.YoutubeId),
            playlistTracks: playlistTracks,
            originalPlaylist: playlist,
            isForEdit: true,
            isSystemPlaylist: isSystem,
            networkManager: networkManager,
            dominantColorService: dominantColorService,
            youtube: youtube);
    }

    #endregion

    #region Result

    /// <summary>
    /// Собирает результат редактирования.
    /// </summary>
    public EditPlaylistResult ToResult() => new()
    {
        Name = Name.Trim(),
        ThumbnailUrl = string.IsNullOrWhiteSpace(ThumbnailUrl) ? null : ThumbnailUrl.Trim(),
        CustomColor = string.IsNullOrWhiteSpace(CustomColor) ? null : CustomColor.Trim(),
        Description = Description?.Trim(),
        ComputedColor = ComputedColor
    };

    public bool SyncStateChanged => IsSyncedToCloud != OriginalSyncState;
    public bool DescriptionChanged => !string.Equals(Description?.Trim(), _originalDescription?.Trim(), StringComparison.Ordinal);

    #endregion

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _thumbnailDebounceTimer.Stop();
            _colorDebounceTimer.Stop();
            LocalPreviewBitmap?.Dispose();
            CoverPicker?.Dispose();
        }
        base.Dispose(disposing);
    }
}