using LMP.UI.Features.Settings.ViewModels;

namespace LMP.UI.Features.Settings;

/// <summary>
/// Ленивый маркер элемента sidebar с фабричным разрешением секции и независимым заголовком.
/// </summary>
public abstract class SettingsSidebarItemBase
{
    private readonly Func<ViewModelBase> _sectionFactory;
    private ViewModelBase? _resolvedSection;

    protected SettingsSidebarItemBase(string titleKey, Func<ViewModelBase> sectionFactory)
    {
        TitleKey = titleKey;
        _sectionFactory = sectionFactory;
    }

    public string TitleKey { get; }

    public string Title => LocalizationService.Instance[TitleKey];

    public ViewModelBase Section => GetSection();

    public ViewModelBase GetSection() => _resolvedSection ??= _sectionFactory();
}

public sealed class AccountLanguageSidebarItem(string titleKey, Func<AccountLanguageSettingsViewModel> factory) : SettingsSidebarItemBase(titleKey, factory);
public sealed class NetworkSidebarItem(string titleKey, Func<NetworkSettingsViewModel> factory) : SettingsSidebarItemBase(titleKey, factory);
public sealed class StorageCacheSidebarItem(string titleKey, Func<StorageCacheSettingsViewModel> factory) : SettingsSidebarItemBase(titleKey, factory);
public sealed class MemorySidebarItem(string titleKey, Func<MemorySettingsViewModel> factory) : SettingsSidebarItemBase(titleKey, factory);
public sealed class AppearanceSidebarItem(string titleKey, Func<AppearanceSettingsViewModel> factory) : SettingsSidebarItemBase(titleKey, factory);
public sealed class AudioSidebarItem(string titleKey, Func<AudioSettingsViewModel> factory) : SettingsSidebarItemBase(titleKey, factory);
public sealed class PlaybackSidebarItem(string titleKey, Func<PlaybackSettingsViewModel> factory) : SettingsSidebarItemBase(titleKey, factory);
public sealed class WindowBehaviorSidebarItem(string titleKey, Func<WindowBehaviorSettingsViewModel> factory) : SettingsSidebarItemBase(titleKey, factory);
public sealed class GeneralSidebarItem(string titleKey, Func<GeneralSettingsViewModel> factory) : SettingsSidebarItemBase(titleKey, factory);