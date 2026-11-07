using Avalonia.Threading;
using LMP.Core.Audio.Cache;

namespace LMP.UI.Features.Settings.ViewModels;

public sealed partial class StorageCacheSettingsViewModel : ViewModelBase
{
    [Flags]
    private enum PendingStorageChanges : uint
    {
        None = 0,
        StorageQuotas = 1 << 0,
        DownloadsLimit = 1 << 1,
        BitmapCacheItems = 1 << 2
    }

    private readonly LibraryService _library;
    private readonly ImageCacheService _imageCache;
    private readonly DialogService _dialog;

    private DispatcherTimer? _debounceTimer;
    private PendingStorageChanges _pendingChanges;
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
         ImageCacheService imageCache,
         DialogService dialog)
    {
        _library = library;
        _imageCache = imageCache;
        _dialog = dialog;

        BrowseDownloadPathCommand = new AsyncRelayCommand(BrowseDownloadPathAsync);
        ClearDownloadsCommand = new AsyncRelayCommand(ClearDownloadsAsync);
        ClearImageCacheCommand = new AsyncRelayCommand(ClearImageCacheAsync);
        ClearAudioCacheCommand = new AsyncRelayCommand(ClearAudioCacheAsync);

        AudioSourceFactory.GlobalCache.OnFormatCached += OnAudioFormatCached;
        AudioSourceFactory.GlobalCache.OnCacheCleared += OnAudioCacheCleared;
        AudioSourceFactory.GlobalCache.OnEntryEvicted += OnAudioEntryEvicted;

        RefreshLists();
        LoadSettings();
    }

    private void OnAudioFormatCached(string trackId, AudioFormat format, int bitrate, bool isDownloaded) =>
        UpdateCacheStats();

    private void OnAudioCacheCleared() =>
        UpdateCacheStats();

    private void OnAudioEntryEvicted(string trackId) =>
        UpdateCacheStats();

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

            MaxBitmapCacheItems = s.Storage.MaxBitmapCacheItems > 0 ? s.Storage.MaxBitmapCacheItems : 25;
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

    private void ScheduleDebounce(PendingStorageChanges flag)
    {
        _pendingChanges |= flag;
        _debounceTimer?.Stop();
        _debounceTimer ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(450),
            DispatcherPriority.Normal,
            (_, _) => FlushPendingChanges());
        _debounceTimer.Start();
    }

    private void FlushPendingChanges()
    {
        _debounceTimer?.Stop();
        var pending = _pendingChanges;
        if (pending == PendingStorageChanges.None) return;
        _pendingChanges = PendingStorageChanges.None;

        if ((pending & PendingStorageChanges.StorageQuotas) != 0)
        {
            AudioSourceFactory.GlobalCache?.UpdateMaxCacheSizeMb(AudioCacheLimitMb);
            Log.Info($"[Settings] Storage quotas updated -> AudioCache={AudioCacheLimitMb}MB, ImageCache={ImageCacheLimitMb}MB");
        }

        if ((pending & (PendingStorageChanges.StorageQuotas | PendingStorageChanges.BitmapCacheItems)) != 0)
            _imageCache.EnforceLimits();

        if ((pending & PendingStorageChanges.BitmapCacheItems) != 0)
            Log.Info($"[Settings] MaxBitmapCacheItems -> {MaxBitmapCacheItems}");

        if ((pending & PendingStorageChanges.DownloadsLimit) != 0)
            Log.Info($"[Settings] DownloadedTracksLimitMb -> {DownloadedTracksLimitMb}MB");

        UpdateCacheStats();
    }

    partial void OnAutoSaveToDownloadsChanged(bool value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.Storage.AutoSaveToDownloads = value);
        Log.Info($"[Settings] AutoSaveToDownloads -> {value}");
    }

    partial void OnDownloadedTracksLimitMbChanged(int value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.Storage.DownloadedTracksLimitMb = value);
        ScheduleDebounce(PendingStorageChanges.DownloadsLimit);
    }

    partial void OnImageCacheLimitMbChanged(int value) => ScheduleStorageQuotasUpdate();
    partial void OnAudioCacheLimitMbChanged(int value) => ScheduleStorageQuotasUpdate();

    private void ScheduleStorageQuotasUpdate()
    {
        if (_isLoading) return;
        _library.UpdateSettings(s =>
        {
            s.Storage.ImageCacheLimitMb = ImageCacheLimitMb;
            s.Storage.AudioCacheLimitMb = AudioCacheLimitMb;
        });
        ScheduleDebounce(PendingStorageChanges.StorageQuotas);
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
        ScheduleDebounce(PendingStorageChanges.BitmapCacheItems);

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
        if (Dispatcher.UIThread.CheckAccess())
        {
            _ = Task.Run(ComputeAndPublishCacheStats);
        }
        else
        {
            ComputeAndPublishCacheStats();
        }
    }

    private void ComputeAndPublishCacheStats()
    {
        var (memItems, _, imgCount, imgSizeMb) = _imageCache.GetStats();
        var (audioFileCount, audioSizeMb) = AudioSourceFactory.GlobalCache.GetStatsCompact();
        var (downloadFileCount, downloadSizeMb) = AudioCacheManager.GetDownloadsStats(_library.DownloadPath);

        Dispatcher.UIThread.Post(() =>
        {
            int imgLimit = ImageCacheLimitMb;
            int audLimit = AudioCacheLimitMb;
            int dlLimit = DownloadedTracksLimitMb;

            ImageCacheStats = $"{imgSizeMb} MB / {imgLimit} MB ({imgCount} {SL["Common_Files"]}, RAM: {memItems})";
            AudioCacheStats = $"{audioSizeMb} MB / {audLimit} MB ({audioFileCount} {SL["Common_Files"]})";
            DownloadsStats = $"{downloadSizeMb} MB / {dlLimit} MB ({downloadFileCount} {SL["Common_Files"]})";

            ImageCacheUsagePercent = imgLimit > 0 ? Math.Clamp((double)imgSizeMb / imgLimit, 0, 1) : 0;
            AudioCacheUsagePercent = audLimit > 0 ? Math.Clamp((double)audioSizeMb / audLimit, 0, 1) : 0;
            DownloadsUsagePercent = dlLimit > 0 ? Math.Clamp((double)downloadSizeMb / dlLimit, 0, 1) : 0;
        });
    }

    private async Task BrowseDownloadPathAsync()
    {
        var newPath = await DialogService.SelectFolderAsync(DownloadPath);
        if (string.IsNullOrEmpty(newPath)) return;
        DownloadPath = newPath;
        _library.DownloadPath = newPath;
        Log.Info($"[Settings] DownloadPath -> '{newPath}'");
        UpdateCacheStats();
    }

    private async Task ClearImageCacheAsync()
    {
        await _imageCache.ClearDiskCacheAsync();
        Log.Info("[Settings] Image disk cache cleared by user");
        UpdateCacheStats();
    }

    private async Task ClearAudioCacheAsync()
    {
        var cache = AudioSourceFactory.GlobalCache;
        if (cache is not null)
        {
            await cache.ClearAllAsync();
            Log.Info("[Settings] Audio cache cleared by user");
        }
        else
        {
            Log.Warn("[AudioCache] AudioCacheManager not initialized, cannot clear cache.");
        }

        UpdateCacheStats();
    }

    private async Task ClearDownloadsAsync()
    {
        if (!await _dialog.ConfirmAsync(SL["Dialog_Confirm_Title"], SL["Settings_ClearDownloadsConfirm"])) return;

        await AudioCacheManager.ClearDownloadsAsync(_library.DownloadPath);
        await _library.ClearDownloadsMetadataAsync();
        Log.Info("[Settings] Downloaded tracks cleared by user");

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
                cache.OnEntryEvicted -= OnAudioEntryEvicted;
            }

            FlushPendingChanges();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Presets for the number of bitmap objects in the image RAM cache.</summary>
public enum ImageCachePreset { Custom, Low, Medium, High }