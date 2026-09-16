using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;

namespace LMP.UI.Behaviors;

/// <summary>
/// Поведение автоматической постраничной подгрузки списка при приближении к границе скролла.
/// Исключает race conditions и паразитные циклы за счёт синхронизации с состоянием команд и фазами отрисовки.
/// </summary>
public sealed class InfiniteScrollBehavior : Behavior<Control>
{
    private ScrollViewer? _scrollViewer;
    private ICommand? _observedCommand;

    private bool _isExecuting;

    #region Styled Properties

    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<InfiniteScrollBehavior, ICommand?>(nameof(Command));

    public static readonly StyledProperty<double> ThresholdProperty =
        AvaloniaProperty.Register<InfiniteScrollBehavior, double>(nameof(Threshold), 250.0);

    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public double Threshold
    {
        get => GetValue(ThresholdProperty);
        set => SetValue(ThresholdProperty, value);
    }

    #endregion

    protected override void OnAttached()
    {
        base.OnAttached();

        if (AssociatedObject is ScrollViewer sv)
        {
            AttachToScrollViewer(sv);
        }
        else
        {
            AssociatedObject?.Loaded += OnAssociatedObjectLoaded;
        }

        HookCommandCanExecute(Command);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == CommandProperty)
        {
            HookCommandCanExecute(change.GetNewValue<ICommand?>());
        }
    }

    private void OnAssociatedObjectLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (AssociatedObject == null) return;
        AssociatedObject.Loaded -= OnAssociatedObjectLoaded;

        var scroll = AssociatedObject.FindDescendantOfType<ScrollViewer>()
                  ?? AssociatedObject.FindAncestorOfType<ScrollViewer>();

        if (scroll != null)
            AttachToScrollViewer(scroll);
    }

    private void AttachToScrollViewer(ScrollViewer sv)
    {
        _scrollViewer = sv;
        _scrollViewer.ScrollChanged += OnScrollViewerScrollChanged;
        _scrollViewer.PropertyChanged += OnScrollViewerPropertyChanged;
    }

    private void OnScrollViewerScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        CheckAndTrigger();
    }

    private void OnScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ScrollViewer.OffsetProperty || e.Property == ScrollViewer.ExtentProperty)
        {
            CheckAndTrigger();
        }
    }

    protected override void OnDetaching()
    {
        UnhookCommandCanExecute();

        if (_scrollViewer != null)
        {
            _scrollViewer.ScrollChanged -= OnScrollViewerScrollChanged;
            _scrollViewer.PropertyChanged -= OnScrollViewerPropertyChanged;
            _scrollViewer = null;
        }

        base.OnDetaching();
    }

    private void HookCommandCanExecute(ICommand? newCommand)
    {
        UnhookCommandCanExecute();
        _observedCommand = newCommand;

        _observedCommand?.CanExecuteChanged += OnCommandCanExecuteChanged;
    }

    private void UnhookCommandCanExecute()
    {
        _observedCommand?.CanExecuteChanged -= OnCommandCanExecuteChanged;
        _observedCommand = null;
    }

    private void OnCommandCanExecuteChanged(object? sender, EventArgs e)
    {
        // Проверяем скролл только после того, как Avalonia завершит фазу Layout для новых элементов
        Dispatcher.UIThread.Post(CheckAndTrigger, DispatcherPriority.Loaded);
    }

    private void CheckAndTrigger()
    {
        if (!IsEnabled || _scrollViewer == null || Command == null || _isExecuting)
            return;

        var sv = _scrollViewer;

        if (sv.Viewport.Height <= 0 || sv.Extent.Height <= 0)
            return;

        double scrollableHeight = sv.Extent.Height - sv.Viewport.Height;

        // Если элементы физически не заполнили даже один экран — скролла нет
        if (scrollableHeight <= 0)
            return;

        double distanceToEnd = scrollableHeight - sv.Offset.Y;

        if (distanceToEnd <= Threshold)
        {
            if (Command.CanExecute(null))
            {
                ExecuteCommand();
            }
        }
    }

    private void ExecuteCommand()
    {
        _isExecuting = true;

        try
        {
            Command?.Execute(null);
        }
        finally
        {
            // Сбрасываем флаг только на следующем тике, давая RelayCommand обновить IsExecuting
            Dispatcher.UIThread.Post(() => _isExecuting = false, DispatcherPriority.Normal);
        }
    }
}