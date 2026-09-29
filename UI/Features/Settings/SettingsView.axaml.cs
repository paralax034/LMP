using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using LMP.UI.Features.Settings.Pages;

namespace LMP.UI.Features.Settings;

/// <summary>
/// Представление страницы настроек с кэшированием секций для устранения рекурсивных проходов компоновки.
/// </summary>
public partial class SettingsView : UserControl
{
    private SettingsViewModel? _currentVm;
    private readonly Dictionary<Type, Control> _pageCache = new(capacity: 9);
    private Control? _activePage;

    public SettingsView()
    {
        InitializeComponent();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UnsubscribeFromVm();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        UnsubscribeFromVm();

        if (DataContext is SettingsViewModel vm)
        {
            _currentVm = vm;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            UpdateActiveSection(vm.SelectedSidebarItem);
        }
    }

    private void UnsubscribeFromVm()
    {
        _currentVm?.PropertyChanged -= OnViewModelPropertyChanged;
        _currentVm = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.SelectedSidebarItem))
        {
            ContentScrollViewer.ScrollToHome();
            UpdateActiveSection(_currentVm?.SelectedSidebarItem);
        }
    }

    /// <summary>
    /// Переключает секции настроек с нулевыми аллокациями и гарантированной изоляцией DataContext.
    /// </summary>
    private void UpdateActiveSection(SettingsSidebarItemBase? selectedItem)
    {
        if (selectedItem == null) return;

        var targetType = selectedItem.GetType();

        if (!_pageCache.TryGetValue(targetType, out var page))
        {
            page = CreatePageForSection(selectedItem);

            // КРИТИЧНО: Присваиваем DataContext ДО вставки в дерево, чтобы не допустить наследования родителя
            page.DataContext = selectedItem.GetSection();
            _pageCache[targetType] = page;
            PageHostContainer.Children.Add(page);
        }
        else
        {
            page.DataContext = selectedItem.GetSection();
        }

        if (_activePage != page)
        {
            _activePage?.IsVisible = false;

            page.IsVisible = true;
            _activePage = page;
        }
    }

    private static Control CreatePageForSection(SettingsSidebarItemBase item)
    {
        return item switch
        {
            AccountLanguageSidebarItem => new AccountLanguagePage(),
            NetworkSidebarItem => new NetworkPage(),
            StorageCacheSidebarItem => new StorageCachePage(),
            MemorySidebarItem => new MemoryPage(),
            AppearanceSidebarItem => new AppearancePage(),
            AudioSidebarItem => new AudioPage(),
            PlaybackSidebarItem => new PlaybackPage(),
            WindowBehaviorSidebarItem => new WindowBehaviorPage(),
            GeneralSidebarItem => new GeneralPage(),
            _ => throw new ArgumentOutOfRangeException(nameof(item), item, null)
        };
    }
}