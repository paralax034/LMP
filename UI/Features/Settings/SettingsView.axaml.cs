using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;

namespace LMP.UI.Features.Settings;

/// <summary>
/// Представление страницы настроек.
/// Управляет адаптивной шириной sidebar и сбросом скролла при смене вкладки.
/// </summary>
public partial class SettingsView : UserControl
{
    /// <summary>
    /// Ширина ниже которой текст скрывается — остаются только иконки.
    /// 46px = иконка 18px + отступы 14px*2.
    /// </summary>
    private const double CollapsedThreshold = 120.0;

    /// <summary>Начальная ширина sidebar.</summary>
    private const double DefaultWidth = 240.0;

    /// <summary>
    /// Минимум = только иконки. Не даём утащить левее.
    /// </summary>
    private const double MinWidthSidebar = 46.0;

    /// <summary>Максимум — не даём растянуть больше половины типичного окна.</summary>
    private const double MaxWidthSidebar = 320.0;

    private SettingsViewModel? _currentVm;

    public SettingsView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        var col = LayoutGrid.ColumnDefinitions[0];
        col.Width = new GridLength(DefaultWidth, GridUnitType.Pixel);
        col.MinWidth = MinWidthSidebar;
        col.MaxWidth = MaxWidthSidebar;

        LayoutGrid.LayoutUpdated += OnLayoutUpdated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LayoutGrid.LayoutUpdated -= OnLayoutUpdated;
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
        }
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        var col = LayoutGrid.ColumnDefinitions[0];
        var actualWidth = col.ActualWidth;

        if (actualWidth <= 0) return;

        // Жёсткий зажим — на случай если GridSplitter всё же вышел за границы
        if (actualWidth < MinWidthSidebar)
            col.Width = new GridLength(MinWidthSidebar, GridUnitType.Pixel);
        else if (actualWidth > MaxWidthSidebar)
            col.Width = new GridLength(MaxWidthSidebar, GridUnitType.Pixel);

        // Автоматическое переключение текст ↔ только иконки
        if (_currentVm is { } vm)
        {
            var shouldExpand = actualWidth >= CollapsedThreshold;
            if (vm.IsSidebarExpanded != shouldExpand)
                vm.IsSidebarExpanded = shouldExpand;
        }
    }
}