using Avalonia.Threading;
using LMP.Core.Audio.Cache;

namespace LMP.UI.Features.Settings.ViewModels;

public sealed partial class StorageCacheSettingsViewModel : ViewModelBase
{
    private readonly LibraryService _library;
    private readonly TrackRegistry _registry;
    private readonly ImageCacheService _imageCache;
    private readonly DialogService _dialog;

    private DispatcherTimer? _storageDebounceTimer;
    private DispatcherTimer? _downloadsLimitDebounceTimer;
    private bool _isLoading;
    private bool _isUpdatingPreset;

    [ObservableProperty] public partial string DownloadPath { get; set; } = string.Empty;
    [ObservableProperty] public partial IReadOnlyList<LocalizedItem<ImageCachePreset>> ImageCachePresets { get; private set; } = [];
    [ObservableProperty] public partial LocalizedItem<ImageCachePreset>? SelectedImageCachePreset { get; set; }
    [ObservableProperty] public partial int MaxBitmapCacheItems { get; set; }

    [ObservableProperty] public partial int ImageCacheLimitMb { get; set; }
    [ObservableProperty] public partial int AudioCacheLimitMb { get; set; }
    [ObservableProperty] public partial int DownloadedTracksLimitMb { get; set; }

    [ObservableProperty] public partial string ImageCacheStats { get; private set; } = "...";
    [ObservableProperty] public partial string AudioCacheStats { get; private set; } = "...";
    [ObservableProperty] public partial double ImageCacheUsagePercent { get; private set; }
    [ObservableProperty] public partial double AudioCacheUsagePercent { get; private set; }
    [ObservableProperty] public partial string DownloadsStats { get; private set; } = "...";
    [ObservableProperty] public partial double DownloadsUsagePercent { get; private set; }
    [ObservableProperty] public partial bool AutoSaveToDownloads { get; set; }

    public IAsyncRelayCommand BrowseDownloadPathCommand { get; }
    public IAsyncRelayCommand ClearDownloadsCommand { get; }
    public IAsyncRelayCommand ClearImageCacheCommand { get; }
    public IAsyncRelayCommand ClearAudioCacheCommand { get; }

    public StorageCacheSettingsViewModel(
         LibraryService library,
         TrackRegistry registry,
         ImageCacheService imageCache,
         DialogService dialog)
    {
        _library = library;
        _registry = registry;
        _imageCache = imageCache;
        _dialog = dialog;

        BrowseDownloadPathCommand = new AsyncRelayCommand(BrowseDownloadPathAsync);
        ClearDownloadsCommand = new AsyncRelayCommand(ClearDownloadsAsync);
        ClearImageCacheCommand = new AsyncRelayCommand(ClearImageCacheAsync);
        ClearAudioCacheCommand = new AsyncRelayCommand(ClearAudioCacheAsync);

        var cache = AudioSourceFactory.GlobalCache;
        if (cache != null)
        {
            cache.OnFormatCached += OnAudioFormatCached;
            cache.OnCacheCleared += OnAudioCacheCleared;
        }

        RefreshLists();
        LoadSettings();
    }

    private void OnAudioFormatCached(string trackId, AudioFormat format, int bitrate, bool isDownloaded) =>
        Dispatcher.UIThread.Post(UpdateCacheStats);

    private void OnAudioCacheCleared() =>
        Dispatcher.UIThread.Post(UpdateCacheStats);

    public void RefreshLists()
    {
        _isLoading = true;
        try
        {
            var currentImgPreset = SelectedImageCachePreset?.Value ?? ImageCachePreset.Custom;
            ImageCachePresets =
            [
                new(ImageCachePreset.Low, $"{SL["Cache_Low"]} (20)"),
                new(ImageCachePreset.Medium, $"{SL["Cache_Medium"]} (50)"),
                new(ImageCachePreset.High, $"{SL["Cache_High"]} (100)"),
            ];
            if (currentImgPreset != ImageCachePreset.Custom)
                SelectedImageCachePreset = ImageCachePresets.FindByValue(currentImgPreset);
        }
        finally
        {
            _isLoading = false;
        }
    }

    public void LoadSettings()
    {
        _isLoading = true;
        try
        {
            var s = _library.Settings;
            DownloadPath = _library.DownloadPath;

            ImageCacheLimitMb = s.Storage.ImageCacheLimitMb;
            AudioCacheLimitMb = s.Storage.AudioCacheLimitMb;
            DownloadedTracksLimitMb = s.Storage.DownloadedTracksLimitMb;
            AutoSaveToDownloads = s.Storage.AutoSaveToDownloads;

            MaxBitmapCacheItems = s.Storage.MaxBitmapCacheItems > 0 ? s.Storage.MaxBitmapCacheItems : 40;
            _isUpdatingPreset = true;
            SelectedImageCachePreset = MaxBitmapCacheItems switch
            {
                20 => ImageCachePresets.FindByValue(ImageCachePreset.Low),
                50 => ImageCachePresets.FindByValue(ImageCachePreset.Medium),
                100 => ImageCachePresets.FindByValue(ImageCachePreset.High),
                _ => null
            };
            _isUpdatingPreset = false;
        }
        finally
        {
            _isLoading = false;
        }
    }

    partial void OnAutoSaveToDownloadsChanged(bool value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.Storage.AutoSaveToDownloads = value);
    }

    partial void OnDownloadedTracksLimitMbChanged(int value)
    {
        if (_isLoading) return;
        _downloadsLimitDebounceTimer?.Stop();
        _downloadsLimitDebounceTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(500),
            DispatcherPriority.Normal,
            (_, _) =>
            {
                _downloadsLimitDebounceTimer?.Stop();
                _library.UpdateSettings(s => s.Storage.DownloadedTracksLimitMb = value);
                UpdateCacheStats();
            });
        _downloadsLimitDebounceTimer.Start();
    }

    partial void OnImageCacheLimitMbChanged(int value) => ScheduleStorageSettingsSave();
    partial void OnAudioCacheLimitMbChanged(int value) => ScheduleStorageSettingsSave();

    private void ScheduleStorageSettingsSave()
    {
        if (_isLoading) return;
        _storageDebounceTimer?.Stop();
        _storageDebounceTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(500),
            DispatcherPriority.Normal,
            (_, _) =>
            {
                _storageDebounceTimer?.Stop();
                _library.UpdateSettings(s =>
                {
                    s.Storage.ImageCacheLimitMb = ImageCacheLimitMb;
                    s.Storage.AudioCacheLimitMb = AudioCacheLimitMb;
                });
                UpdateCacheStats();
            });
        _storageDebounceTimer.Start();
    }

    partial void OnSelectedImageCachePresetChanged(LocalizedItem<ImageCachePreset>? value)
    {
        if (_isUpdatingPreset || _isLoading || value is null) return;
        _isUpdatingPreset = true;
        MaxBitmapCacheItems = value.Value switch
        {
            ImageCachePreset.Low => 20,
            ImageCachePreset.Medium => 50,
            ImageCachePreset.High => 100,
            _ => MaxBitmapCacheItems
        };
        _isUpdatingPreset = false;
    }

    partial void OnMaxBitmapCacheItemsChanged(int value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.Storage.MaxBitmapCacheItems = value);
        _imageCache.EnforceLimits();

        if (_isUpdatingPreset) return;

        _isUpdatingPreset = true;
        SelectedImageCachePreset = value switch
        {
            20 => ImageCachePresets.FindByValue(ImageCachePreset.Low),
            50 => ImageCachePresets.FindByValue(ImageCachePreset.Medium),
            100 => ImageCachePresets.FindByValue(ImageCachePreset.High),
            _ => null
        };
        _isUpdatingPreset = false;
    }

    public void UpdateCacheStats()
    {
        var (memItems, _, imgCount, imgSizeMb) = _imageCache.GetStats();

        var cache = AudioSourceFactory.GlobalCache;
        var (audioFileCount, audioSizeMb) = cache?.GetStatsCompact() ?? (0, 0);
        var (downloadFileCount, downloadSizeMb) = AudioCacheManager.GetDownloadsStats();

        ImageCacheStats = $"{imgSizeMb} MB / {ImageCacheLimitMb} MB ({imgCount} {SL["Common_Files"]}, RAM: {memItems})";
        AudioCacheStats = $"{audioSizeMb} MB / {AudioCacheLimitMb} MB ({audioFileCount} {SL["Common_Files"]})";
        DownloadsStats = $"{downloadSizeMb} MB / {DownloadedTracksLimitMb} MB ({downloadFileCount} {SL["Common_Files"]})";

        ImageCacheUsagePercent = ImageCacheLimitMb > 0 ? Math.Clamp((double)imgSizeMb / ImageCacheLimitMb, 0, 1) : 0;
        AudioCacheUsagePercent = AudioCacheLimitMb > 0 ? Math.Clamp((double)audioSizeMb / AudioCacheLimitMb, 0, 1) : 0;
        DownloadsUsagePercent = DownloadedTracksLimitMb > 0 ? Math.Clamp((double)downloadSizeMb / DownloadedTracksLimitMb, 0, 1) : 0;
    }

    private async Task BrowseDownloadPathAsync()
    {
        var newPath = await DialogService.SelectFolderAsync(DownloadPath);
        if (string.IsNullOrEmpty(newPath)) return;
        DownloadPath = newPath;
        _library.DownloadPath = newPath;
    }

    private async Task ClearImageCacheAsync()
    {
        await _imageCache.ClearDiskCacheAsync();
        UpdateCacheStats();
    }

    private async Task ClearAudioCacheAsync()
    {
        var cache = AudioSourceFactory.GlobalCache;
        if (cache is not null)
            await cache.ClearAllAsync();
        else
            Log.Warn("[AudioCache] AudioCacheManager not initialized, cannot clear cache.");

        UpdateCacheStats();
    }

    private async Task ClearDownloadsAsync()
    {
        if (!await _dialog.ConfirmAsync(SL["Dialog_Confirm_Title"], SL["Settings_ClearDownloadsConfirm"])) return;

        await AudioCacheManager.ClearDownloadsAsync();

        foreach (var track in _registry.GetPinnedTracks().Where(t => t.IsDownloaded))
        {
            track.IsDownloaded = false;
            track.LocalPath = null;
        }

        UpdateCacheStats();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var cache = AudioSourceFactory.GlobalCache;
            if (cache != null)
            {
                cache.OnFormatCached -= OnAudioFormatCached;
                cache.OnCacheCleared -= OnAudioCacheCleared;
            }

            _storageDebounceTimer?.Stop();
            _downloadsLimitDebounceTimer?.Stop();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Пресеты количества bitmap-объектов в RAM-кэше изображений.</summary>
public enum ImageCachePreset { Custom, Low, Medium, High }