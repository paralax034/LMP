using Avalonia.Threading;

namespace LMP.UI.Features.Settings.ViewModels;

public sealed partial class MemorySettingsViewModel : ViewModelBase
{
    private readonly LibraryService _library;
    private DispatcherTimer? _memoryIntervalDebounceTimer;
    private DispatcherTimer? _memoryPressureDebounceTimer;
    private bool _isLoading;

    public sealed record GpuCachePresetItem(long Mb, string Name)
    {
        public override string ToString() => Name;
    }

    [ObservableProperty] public partial IReadOnlyList<GpuCachePresetItem> GpuCachePresets { get; private set; } = [];
    [ObservableProperty] public partial GpuCachePresetItem? SelectedGpuCachePreset { get; set; }
    [ObservableProperty] public partial bool GpuCacheRestartRequired { get; private set; }

    [ObservableProperty] public partial bool AutoMemoryCleanupEnabled { get; set; }
    [ObservableProperty] public partial int MemoryCleanupIntervalMinutes { get; set; }
    [ObservableProperty] public partial int MemoryPressureThresholdMb { get; set; }

    public IRelayCommand CleanupMemoryNowCommand { get; }

    public MemorySettingsViewModel(LibraryService library)
    {
        _library = library;
        CleanupMemoryNowCommand = new RelayCommand(() => MemoryCleanupHelper.PerformCleanup(aggressive: true));

        InitGpuCachePresets();
        LoadSettings();
    }

    public void InitGpuCachePresets()
    {
        var currentMb = SelectedGpuCachePreset?.Mb ?? BootstrapSettings.Current.GpuTextureCacheMb;

        GpuCachePresets =
        [
            new GpuCachePresetItem(32, $"32 MB  ({SL["Cache_Low"]})"),
            new GpuCachePresetItem(64, $"64 MB  ({SL["Cache_Medium"]})"),
            new GpuCachePresetItem(128, $"128 MB ({SL["Cache_High"]})"),
            new GpuCachePresetItem(256, $"256 MB ({SL["Cache_Ultra"]})"),
        ];

        SelectedGpuCachePreset = GpuCachePresets.FirstOrDefault(x => x.Mb == currentMb)
                              ?? GpuCachePresets[1];
    }

    public void LoadSettings()
    {
        _isLoading = true;
        try
        {
            var mem = _library.Settings.Memory;
            AutoMemoryCleanupEnabled = mem.AutoCleanupEnabled;
            MemoryCleanupIntervalMinutes = mem.AutoCleanupIntervalMinutes > 0 ? mem.AutoCleanupIntervalMinutes : 30;
            MemoryPressureThresholdMb = mem.PressureThresholdMb > 0 ? mem.PressureThresholdMb : 400;
        }
        finally
        {
            _isLoading = false;
        }
    }

    partial void OnSelectedGpuCachePresetChanged(GpuCachePresetItem? value)
    {
        if (_isLoading || value is null) return;
        if (BootstrapSettings.Current.GpuTextureCacheMb == value.Mb) return;

        BootstrapSettings.Current.GpuTextureCacheMb = value.Mb;
        BootstrapSettings.Current.Save();
        GpuCacheRestartRequired = true;
        Log.Info($"[Settings] GPU cache → {value.Mb}MB (restart required)");
    }

    partial void OnAutoMemoryCleanupEnabledChanged(bool value)
    {
        if (_isLoading) return;
        _library.UpdateSettings(s => s.Memory.AutoCleanupEnabled = value);
        MemoryCleanupHelper.RestartAutoCleanup();
    }

    partial void OnMemoryCleanupIntervalMinutesChanged(int value)
    {
        if (_isLoading) return;
        _memoryIntervalDebounceTimer?.Stop();
        _memoryIntervalDebounceTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(500),
            DispatcherPriority.Normal,
            (_, _) =>
            {
                _memoryIntervalDebounceTimer?.Stop();
                _library.UpdateSettings(s => s.Memory.AutoCleanupIntervalMinutes = value);
                MemoryCleanupHelper.RestartAutoCleanup();
            });
        _memoryIntervalDebounceTimer.Start();
    }

    partial void OnMemoryPressureThresholdMbChanged(int value)
    {
        if (_isLoading) return;
        _memoryPressureDebounceTimer?.Stop();
        _memoryPressureDebounceTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(500),
            DispatcherPriority.Normal,
            (_, _) =>
            {
                _memoryPressureDebounceTimer?.Stop();
                _library.UpdateSettings(s => s.Memory.PressureThresholdMb = value);
            });
        _memoryPressureDebounceTimer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _memoryIntervalDebounceTimer?.Stop();
            _memoryPressureDebounceTimer?.Stop();
        }
        base.Dispose(disposing);
    }
}
