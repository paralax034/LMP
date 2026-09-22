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
    /// <summary>Прямой ввод HTTP/HTTPS URL адреса.</summary>
    Url,

    /// <summary>Генерация мозаики из обложек треков текущего плейлиста.</summary>
    FromTracks,

    /// <summary>Выбор локального файла изображения с файловой системы.</summary>
    File
}

/// <summary>
/// ViewModel редактора метаданных плейлиста.
/// Поддерживает двухколоночный макет, реактивное превью и автоматическую синхронизацию палитры.
/// </summary>
public sealed partial class PlaylistEditorViewModel : ViewModelBase
{
    private readonly INetworkManager? _networkManager;
    private readonly DominantColorService? _dominantColorService;
    private readonly string? _originalDescription;

    /// <summary>
    /// Название плейлиста.
    /// </summary>
    [ObservableProperty]
    public partial string Name { get; set; } = "";

    /// <summary>
    /// URL или локальный путь к обложке плейлиста.
    /// </summary>
    [ObservableProperty]
    public partial string? ThumbnailUrl { get; set; }

    /// <summary>
    /// Пользовательский акцентный цвет в формате HEX (например, #FF5500).
    /// </summary>
    [ObservableProperty]
    public partial string? CustomColor { get; set; }

    /// <summary>
    /// Пользовательское текстовое описание плейлиста.
    /// </summary>
    [ObservableProperty]
    public partial string? Description { get; set; }

    /// <summary>
    /// Автоматически вычисленный доминантный цвет обложки в формате HEX.
    /// </summary>
    [ObservableProperty]
    public partial string? ComputedColor { get; set; }

    /// <summary>
    /// Кисть для отображения превью активного цвета.
    /// </summary>
    [ObservableProperty]
    public partial IBrush ComputedColorPreviewBrush { get; set; } = Brushes.Transparent;

    /// <summary>
    /// Текстовое представление активного цвета для отображения в компактной карточке палитры.
    /// </summary>
    [ObservableProperty]
    public partial string EffectiveColorText { get; set; } = "Auto";

    /// <summary>
    /// Флаг выполнения асинхронного пересчёта доминантного цвета из изображения.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecalculateColorCommand))]
    public partial bool IsRecalculatingColor { get; set; }

    /// <summary>
    /// Команда принудительного пересчёта доминантного цвета из активной обложки.
    /// </summary>
    public IAsyncRelayCommand RecalculateColorCommand { get; }

    /// <summary>
    /// Флаг системного неизменяемого плейлиста (например, «Любимые треки»).
    /// </summary>
    public bool IsSystemPlaylist { get; }

    /// <summary>
    /// Доступно ли имя для ручного редактирования.
    /// </summary>
    public bool IsNameEditable { get; }

    /// <summary>
    /// Флаг режима редактирования существующего плейлиста.
    /// </summary>
    public bool IsForEdit { get; }

    /// <summary>
    /// Делегат обратного вызова для запуска процесса создания локальной копии плейлиста.
    /// </summary>
    public Action? OnCreateCopy { get; set; }

    /// <summary>
    /// Команда создания локальной копии плейлиста.
    /// </summary>
    public IRelayCommand CreateCopyCommand { get; }

    /// <summary>
    /// Активный режим выбора источника обложки.
    /// </summary>
    [ObservableProperty]
    public partial CoverMode SelectedCoverMode { get; set; } = CoverMode.Url;

    /// <summary>
    /// Флаг активности вкладки прямого URL.
    /// </summary>
    [ObservableProperty]
    public partial bool IsCoverModeUrl { get; set; } = true;

    /// <summary>
    /// Флаг активности вкладки мозаики из треков.
    /// </summary>
    [ObservableProperty]
    public partial bool IsCoverModeFromTracks { get; set; }

    /// <summary>
    /// Флаг активности вкладки локального файла.
    /// </summary>
    [ObservableProperty]
    public partial bool IsCoverModeFile { get; set; }

    /// <summary>
    /// ViewModel выбора обложек для построения мозаики.
    /// </summary>
    [ObservableProperty]
    public partial PlaylistCoverPickerViewModel? CoverPicker { get; set; }

    /// <summary>
    /// Флаг отображения переключателя режимов обложки.
    /// </summary>
    public bool ShowCoverModeSwitch { get; } = true;

    /// <summary>
    /// Доступен ли режим создания мозаики из треков (требует наличия треков с обложками).
    /// </summary>
    public bool HasTracksCoverOption { get; }

    /// <summary>
    /// Путь к файлу, выбранному пользователем через системный диалог.
    /// </summary>
    [ObservableProperty]
    public partial string? SelectedFilePath { get; set; }

    /// <summary>
    /// Команда вызова системного диалога открытия файла изображения.
    /// </summary>
    public IAsyncRelayCommand SelectFileCommand { get; }

    /// <summary>
    /// Флаг видимости секции облачной синхронизации с YouTube Music.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowSyncSection { get; set; }

    /// <summary>
    /// Флаг включения двусторонней синхронизации с YouTube Music.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSyncedToCloud { get; set; }

    /// <summary>
    /// Флаг авторизации текущей пользовательской сессии.
    /// </summary>
    public bool IsAuthenticated { get; }

    /// <summary>
    /// Флаг наличия привязанного удалённого идентификатора YouTube.
    /// </summary>
    public bool HasYoutubeBinding { get; }

    /// <summary>
    /// Исходное состояние флага синхронизации на момент открытия диалога.
    /// </summary>
    public bool OriginalSyncState { get; }

    /// <summary>
    /// Текст текущей ошибки валидации данных формы.
    /// </summary>
    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    /// <summary>
    /// Флаг наличия ошибок валидации.
    /// </summary>
    [ObservableProperty]
    public partial bool HasErrors { get; set; }

    /// <summary>
    /// Доступно ли сохранение формы в текущем состоянии.
    /// </summary>
    public bool CanSave => !HasErrors;

    /// <summary>
    /// URL превью для загрузки по сети или из ресурсов приложения.
    /// </summary>
    [ObservableProperty]
    public partial string? ThumbnailPreviewUrl { get; set; }

    /// <summary>
    /// Флаг наличия доступного превью обложки любого типа.
    /// </summary>
    [ObservableProperty]
    public partial bool HasThumbnailPreview { get; set; }

    /// <summary>
    /// Флаг сетевого источника превью (HTTP/HTTPS/avares).
    /// </summary>
    [ObservableProperty]
    public partial bool IsPreviewHttp { get; set; }

    /// <summary>
    /// Флаг локального источника превью (дисковый файл или ин-мемори растр).
    /// </summary>
    [ObservableProperty]
    public partial bool IsPreviewLocal { get; set; }

    /// <summary>
    /// Локальный растровый снимок для мгновенного отображения мозаики или локального файла.
    /// </summary>
    [ObservableProperty]
    public partial Bitmap? LocalPreviewBitmap { get; set; }

    private readonly DispatcherTimer _thumbnailDebounceTimer;

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="PlaylistEditorViewModel"/>.
    /// </summary>
    /// <param name="name">Название плейлиста.</param>
    /// <param name="thumbnailUrl">URL или путь к обложке.</param>
    /// <param name="customColor">Пользовательский цвет в формате HEX.</param>
    /// <param name="description">Описание плейлиста.</param>
    /// <param name="computedColor">Ранее вычисленный доминантный цвет.</param>
    /// <param name="showSync">Флаг доступности настройки синхронизации.</param>
    /// <param name="isSynced">Исходное состояние синхронизации.</param>
    /// <param name="isAuthenticated">Авторизован ли пользователь.</param>
    /// <param name="hasYoutubeBinding">Имеет ли плейлист привязку к ID YouTube.</param>
    /// <param name="playlistTracks">Коллекция треков плейлиста для мозаики.</param>
    /// <param name="isForEdit">Создан ли редактор для существующего плейлиста.</param>
    /// <param name="isSystemPlaylist">Является ли плейлист системным.</param>
    /// <param name="networkManager">Менеджер сетевых запросов.</param>
    /// <param name="dominantColorService">Сервис анализа цветовой палитры.</param>
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
        bool isForEdit = false,
        bool isSystemPlaylist = false,
        INetworkManager? networkManager = null,
        DominantColorService? dominantColorService = null)
    {
        _networkManager = networkManager ?? AppEntry.Services.GetService<INetworkManager>();
        _dominantColorService = dominantColorService ?? AppEntry.Services.GetService<DominantColorService>();
        _originalDescription = description;
        IsForEdit = isForEdit;
        IsSystemPlaylist = isSystemPlaylist;
        IsNameEditable = !isSystemPlaylist;
        ComputedColor = computedColor;
        ShowSyncSection = showSync;
        IsSyncedToCloud = isSynced;
        OriginalSyncState = isSynced;
        IsAuthenticated = isAuthenticated;
        HasYoutubeBinding = hasYoutubeBinding;

        UpdateColorVisuals(computedColor, customColor);

        _thumbnailDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _thumbnailDebounceTimer.Tick += (s, e) =>
        {
            _thumbnailDebounceTimer.Stop();
            UpdateThumbnailPreview(ThumbnailUrl);
            _ = AutoRecalculateColorAsync();
        };

        HasTracksCoverOption = playlistTracks != null && playlistTracks.Any(t => t.HasThumbnail) && _networkManager != null;
        if (HasTracksCoverOption && _networkManager != null)
        {
            CoverPicker = new PlaylistCoverPickerViewModel(playlistTracks!, _networkManager);
            CoverPicker.OnPreviewUpdated += (bitmap) =>
            {
                if (SelectedCoverMode == CoverMode.FromTracks)
                {
                    LocalPreviewBitmap = bitmap;
                    HasThumbnailPreview = bitmap != null;
                    IsPreviewLocal = bitmap != null;
                    IsPreviewHttp = false;
                }
            };
        }

        SelectFileCommand = new AsyncRelayCommand(SelectFileAsync);
        RecalculateColorCommand = new AsyncRelayCommand(
            RecalculateColorFromCoverAsync,
            () => (!string.IsNullOrWhiteSpace(ThumbnailUrl) || LocalPreviewBitmap != null) && !IsRecalculatingColor);

        CreateCopyCommand = new RelayCommand(() => OnCreateCopy?.Invoke());

        Name = name;
        ThumbnailUrl = thumbnailUrl;
        CustomColor = customColor;
        Description = description;

        if (string.IsNullOrWhiteSpace(thumbnailUrl) && HasTracksCoverOption)
            SelectedCoverMode = CoverMode.FromTracks;
        else
            SelectedCoverMode = CoverMode.Url;

        SyncCoverModes(SelectedCoverMode);
        UpdateValidation();
        UpdateThumbnailPreview(ThumbnailUrl);
    }

    partial void OnSelectedCoverModeChanged(CoverMode value) => SyncCoverModes(value);

    private void SyncCoverModes(CoverMode mode)
    {
        IsCoverModeUrl = mode == CoverMode.Url;
        IsCoverModeFromTracks = mode == CoverMode.FromTracks;
        IsCoverModeFile = mode == CoverMode.File;

        if (mode == CoverMode.FromTracks && CoverPicker?.MosaicPreview != null)
        {
            LocalPreviewBitmap = CoverPicker.MosaicPreview;
            HasThumbnailPreview = true;
            IsPreviewLocal = true;
            IsPreviewHttp = false;
        }
        else if (mode == CoverMode.Url)
        {
            UpdateThumbnailPreview(ThumbnailUrl);
        }
        else if (mode == CoverMode.File)
        {
            UpdateThumbnailPreview(SelectedFilePath);
        }
    }

    partial void OnNameChanged(string value) => UpdateValidation();

    partial void OnThumbnailUrlChanged(string? value)
    {
        UpdateValidation();
        RecalculateColorCommand?.NotifyCanExecuteChanged();
        if (_thumbnailDebounceTimer is not null)
        {
            _thumbnailDebounceTimer.Stop();
            _thumbnailDebounceTimer.Start();
        }
    }

    /// <summary>Активирует режим ввода обложки по URL.</summary>
    public void SetCoverModeUrl() => SelectedCoverMode = CoverMode.Url;

    /// <summary>Активирует режим формирования мозаики из треков.</summary>
    public void SetCoverModeFromTracks() => SelectedCoverMode = CoverMode.FromTracks;

    /// <summary>Активирует режим выбора локального файла.</summary>
    public void SetCoverModeFile() => SelectedCoverMode = CoverMode.File;

    private async Task SelectFileAsync(CancellationToken ct)
    {
        var topLevel = GetTopLevel();
        if (topLevel == null) return;

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
            for (int i = 0; i < desktop.Windows.Count; i++)
            {
                if (desktop.Windows[i].IsActive) return desktop.Windows[i];
            }
            return desktop.MainWindow;
        }
        return null;
    }

    private void UpdateColorVisuals(string? comp, string? cust)
    {
        var active = !string.IsNullOrWhiteSpace(cust) ? cust : comp;
        if (TryParseColor(active, out var brush, out var hex))
        {
            ComputedColorPreviewBrush = brush;
            EffectiveColorText = hex;
        }
        else
        {
            ComputedColorPreviewBrush = Brushes.Transparent;
            EffectiveColorText = "Auto";
        }
    }

    private async Task AutoRecalculateColorAsync()
    {
        if (string.IsNullOrWhiteSpace(ThumbnailUrl) || !IsValidUri(ThumbnailUrl))
            return;

        await RecalculateColorFromCoverAsync(CancellationToken.None).ConfigureAwait(true);
    }

    private async Task RecalculateColorFromCoverAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ThumbnailUrl) && LocalPreviewBitmap == null) return;

        IsRecalculatingColor = true;
        try
        {
            if (_dominantColorService == null) return;

            var targetSource = ThumbnailUrl;
            if (string.IsNullOrEmpty(targetSource) && SelectedCoverMode == CoverMode.FromTracks && CoverPicker != null)
                targetSource = await CoverPicker.PersistMosaicAsync(ct).ConfigureAwait(true);

            if (string.IsNullOrEmpty(targetSource)) return;

            var color = await _dominantColorService.GetDominantColorAsync(targetSource, ct);
            if (color.HasValue)
            {
                var hex = $"#{color.Value.R:X2}{color.Value.G:X2}{color.Value.B:X2}";
                ComputedColor = hex;
                CustomColor = null;
                UpdateColorVisuals(hex, null);
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

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
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
            catch
            {
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

    private static string? ResolveLocalPath(string url)
    {
        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var fileUri))
                return fileUri.LocalPath;
            return null;
        }
        return Path.IsPathRooted(url) ? url : null;
    }

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

    /// <summary>
    /// Проверяет, представляет ли переданная строка валидный локальный путь или поддерживаемый URI схемы.
    /// </summary>
    public static bool IsValidUri(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (Path.IsPathRooted(url)) return true;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.Scheme is "http" or "https" or "avares" or "file";
        }
        return false;
    }

    private static bool TryParseColor(string? colorStr, out IBrush brush, out string hex)
    {
        brush = Brushes.Transparent;
        hex = "Auto";
        if (string.IsNullOrWhiteSpace(colorStr)) return false;
        try
        {
            var parsed = Color.Parse(colorStr);
            brush = new SolidColorBrush(parsed);
            hex = $"#{parsed.R:X2}{parsed.G:X2}{parsed.B:X2}";
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Создаёт модель редактора для сценария создания нового плейлиста.
    /// </summary>
    public static PlaylistEditorViewModel ForCreate(
        bool isAuthenticated = false,
        INetworkManager? networkManager = null,
        DominantColorService? dominantColorService = null) =>
        new(name: "", thumbnailUrl: null, customColor: null, description: null,
            computedColor: null,
            showSync: isAuthenticated,
            isSynced: isAuthenticated,
            isAuthenticated: isAuthenticated,
            hasYoutubeBinding: false,
            playlistTracks: null,
            isForEdit: false,
            isSystemPlaylist: false,
            networkManager: networkManager,
            dominantColorService: dominantColorService);

    /// <summary>
    /// Создаёт модель редактора для сценария редактирования существующего плейлиста.
    /// </summary>
    public static PlaylistEditorViewModel ForEdit(
        Playlist playlist,
        bool isAuthenticated,
        IReadOnlyList<TrackInfo>? playlistTracks = null,
        INetworkManager? networkManager = null,
        DominantColorService? dominantColorService = null)
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
            isForEdit: true,
            isSystemPlaylist: isSystem,
            networkManager: networkManager,
            dominantColorService: dominantColorService);
    }

    /// <summary>
    /// Экспортирует состояние полей формы в неизменяемый результат <see cref="EditPlaylistResult"/>.
    /// </summary>
    public EditPlaylistResult ToResult() => new()
    {
        Name = Name.Trim(),
        ThumbnailUrl = string.IsNullOrWhiteSpace(ThumbnailUrl) ? null : ThumbnailUrl.Trim(),
        CustomColor = string.IsNullOrWhiteSpace(CustomColor) ? null : CustomColor.Trim(),
        Description = Description?.Trim(),
        ComputedColor = ComputedColor
    };

    /// <summary>
    /// Флаг изменения состояния облачной синхронизации относительно исходного.
    /// </summary>
    public bool SyncStateChanged => IsSyncedToCloud != OriginalSyncState;

    /// <summary>
    /// Флаг изменения текстового описания относительно исходного значения.
    /// </summary>
    public bool DescriptionChanged => !string.Equals(Description?.Trim(), _originalDescription?.Trim(), StringComparison.Ordinal);

    /// <summary>
    /// Освобождает занятые ресурсы таймера, растровых превью и дочерней модели мозаики.
    /// </summary>
    /// <param name="disposing">Флаг вызова из Dispose.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _thumbnailDebounceTimer.Stop();
            LocalPreviewBitmap?.Dispose();
            CoverPicker?.Dispose();
        }
        base.Dispose(disposing);
    }
}