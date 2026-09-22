using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;

namespace LMP.UI.Features.Settings;

/// <summary>
/// Представление страницы настроек.
/// Обеспечивает сброс позиции скролла при смене активного раздела настроек.
/// </summary>
public partial class SettingsView : UserControl
{
    private SettingsViewModel? _currentVm;

    /// <summary>
    /// Инициализирует новый экземпляр представления настроек.
    /// </summary>
    public SettingsView()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UnsubscribeFromVm();
        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc />
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

    /// <summary>
    /// Отписывается от отслеживания изменений свойств ViewModel.
    /// </summary>
    private void UnsubscribeFromVm()
    {
        _currentVm?.PropertyChanged -= OnViewModelPropertyChanged;
        _currentVm = null;
    }

    /// <summary>
    /// Обрабатывает изменение выбранного пункта меню и сбрасывает скролл страницы наверх.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.SelectedSidebarItem))
        {
            ContentScrollViewer.ScrollToHome();
        }
    }
}