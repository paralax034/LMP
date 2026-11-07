using System.Collections.ObjectModel;
using LMP.UI.Features.Settings.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.UI.Features.Settings;

/// <summary>
/// Settings page facade with fully lazy (on-demand) initialization of child sections.
/// Prevents synchronous UI thread blocking during the initial navigation to settings.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase, IUnsavedChangesGuard
{
    private readonly IServiceProvider _services;
    private readonly Dictionary<Type, ViewModelBase> _initializedSections = new(9);
    private bool _isDataLoaded;
    private bool _isDisposed;
    private bool _isRevertingSidebarSelection;

    [ObservableProperty] public partial bool IsContentReady { get; private set; }
    [ObservableProperty] public partial SettingsSidebarItemBase? SelectedSidebarItem { get; set; }
    [ObservableProperty] public partial bool IsSidebarExpanded { get; set; } = true;

    public ObservableCollection<SettingsSidebarItemBase> SidebarItems { get; }

    public SettingsViewModel(IServiceProvider services)
    {
        _services = services;

        SidebarItems =
        [
            new AccountLanguageSidebarItem("Settings_Account_Language", () => GetOrCreateSection<AccountLanguageSettingsViewModel>()),
            new NetworkSidebarItem("Settings_Network", () => GetOrCreateSection<NetworkSettingsViewModel>()),
            new StorageCacheSidebarItem("Settings_Storage_Data", () => GetOrCreateSection<StorageCacheSettingsViewModel>()),
            new MemorySidebarItem("Settings_Memory", () => GetOrCreateSection<MemorySettingsViewModel>()),
            new AppearanceSidebarItem("Settings_Appearance", () => GetOrCreateSection<AppearanceSettingsViewModel>()),
            new AudioSidebarItem("Settings_Audio", () => GetOrCreateSection<AudioSettingsViewModel>()),
            new PlaybackSidebarItem("Settings_Playback", () => GetOrCreateSection<PlaybackSettingsViewModel>()),
            new WindowBehaviorSidebarItem("Settings_WindowBehavior", () => GetOrCreateSection<WindowBehaviorSettingsViewModel>()),
            new GeneralSidebarItem("Settings_General", () =>
            {
                var vm = GetOrCreateSection<GeneralSettingsViewModel>();
                vm.OnLibraryReset = LoadAllSections;
                return vm;
            }),
        ];

        SelectedSidebarItem = SidebarItems[0];
        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>
    /// Resolves a section ViewModel strictly on demand upon initial tab navigation.
    /// </summary>
    public T GetOrCreateSection<T>() where T : ViewModelBase
    {
        if (_initializedSections.TryGetValue(typeof(T), out var existing))
            return (T)existing;

        var instance = _services.GetRequiredService<T>();
        _initializedSections[typeof(T)] = instance;
        return instance;
    }

    public bool HasUnsavedChanges => TryGetActiveUnsavedGuard(out _);

    public void ApplyChanges()
    {
        if (TryGetActiveUnsavedGuard(out var guard))
            guard.ApplyChanges();
    }

    public void DiscardChanges()
    {
        if (TryGetActiveUnsavedGuard(out var guard))
            guard.DiscardChanges();
    }

    private bool TryGetActiveUnsavedGuard(out IUnsavedChangesGuard guard)
    {
        foreach (var section in _initializedSections.Values)
        {
            if (section is IUnsavedChangesGuard candidate && candidate.HasUnsavedChanges)
            {
                guard = candidate;
                return true;
            }
        }

        guard = null!;
        return false;
    }

    partial void OnSelectedSidebarItemChanged(SettingsSidebarItemBase? oldValue, SettingsSidebarItemBase? newValue)
    {
        if (_isRevertingSidebarSelection || newValue == null) return;

        if (oldValue != null &&
            !ReferenceEquals(oldValue, newValue) &&
            TryGetActiveUnsavedGuard(out var guard))
        {
            _isRevertingSidebarSelection = true;
            SelectedSidebarItem = oldValue;
            _isRevertingSidebarSelection = false;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(SelectedSidebarItem, oldValue))
                    OnPropertyChanged(nameof(SelectedSidebarItem));
            }, Avalonia.Threading.DispatcherPriority.Input);

            _ = ConfirmAndSwitchSidebarItemAsync(guard, newValue);
            return;
        }

        ActivateSidebarSection(newValue);
    }

    private async Task ConfirmAndSwitchSidebarItemAsync(IUnsavedChangesGuard guard, SettingsSidebarItemBase targetItem)
    {
        var dialogService = _services.GetRequiredService<DialogService>();
        if (!await dialogService.ConfirmUnsavedChangesAsync(guard))
            return;

        _isRevertingSidebarSelection = true;
        SelectedSidebarItem = targetItem;
        _isRevertingSidebarSelection = false;

        ActivateSidebarSection(targetItem);
    }

    private static void ActivateSidebarSection(SettingsSidebarItemBase value)
    {
        var section = value.GetSection();

        if (section is NetworkSettingsViewModel netVm && netVm.NetworkStatus == NetworkSettingsViewModel.NetworkStatusKind.Unknown)
        {
            _ = netVm.TestNetworkAsync();
        }
        else if (section is StorageCacheSettingsViewModel storageVm)
        {
            storageVm.UpdateCacheStats();
        }
    }

    public override async Task OnNavigatedToAsync()
    {
        if (_isDisposed) return;

        if (!_isDataLoaded)
        {
            _isDataLoaded = true;
            var activeSection = SelectedSidebarItem?.GetSection();
            if (activeSection is AccountLanguageSettingsViewModel accVm)
            {
                accVm.LoadSettings();
            }
        }

        IsContentReady = true;
        await Task.CompletedTask;
    }

    private void LoadAllSections()
    {
        foreach (var section in _initializedSections.Values)
        {
            switch (section)
            {
                case AccountLanguageSettingsViewModel acc: acc.LoadSettings(); break;
                case NetworkSettingsViewModel net: net.LoadSettings(); break;
                case StorageCacheSettingsViewModel st: st.LoadSettings(); break;
                case MemorySettingsViewModel mem: mem.LoadSettings(); break;
                case AppearanceSettingsViewModel app: app.LoadSettings(); break;
                case AudioSettingsViewModel aud: aud.LoadSettings(); break;
                case PlaybackSettingsViewModel pb: pb.LoadSettings(); break;
                case WindowBehaviorSettingsViewModel win: win.LoadSettings(); break;
                case GeneralSettingsViewModel gen: gen.LoadSettings(); break;
            }
        }
    }

    private void OnLanguageChanged(object? sender, string e)
    {
        foreach (var section in _initializedSections.Values)
        {
            switch (section)
            {
                case NetworkSettingsViewModel net: net.RefreshLists(); break;
                case StorageCacheSettingsViewModel st: st.RefreshLists(); break;
                case MemorySettingsViewModel mem: mem.InitGpuCachePresets(); break;
                case AppearanceSettingsViewModel app: app.RefreshPresets(); break;
                case AudioSettingsViewModel aud: aud.RefreshLists(); break;
                case PlaybackSettingsViewModel pb: pb.RefreshLists(); break;
                case WindowBehaviorSettingsViewModel win: win.RefreshLists(); break;
                case GeneralSettingsViewModel gen: gen.RefreshLists(); break;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;
        if (disposing)
        {
            _isDisposed = true;
            LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;

            foreach (var section in _initializedSections.Values)
            {
                if (section is IDisposable disposable)
                    disposable.Dispose();
            }
            _initializedSections.Clear();
        }
        base.Dispose(disposing);
    }
}