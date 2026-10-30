using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace LMP.UI.Controls;

/// <summary>
/// Высокопроизводительный хост страниц верхнего уровня с предварительной материализацией представлений.
/// Удерживает страницы постоянно видимыми для LayoutManager (IsVisible=true), изолируя неактивные
/// вкладки через нулевую прозрачность и отключение хит-тестов.
/// Плавное переключение управляется нативными DoubleTransition, исключая откат приоритетов и мерцание.
/// </summary>
public sealed class PersistentPageHost : Panel
{
    private static readonly Transitions PageTransitions =
    [
        new DoubleTransition
        {
            Property = OpacityProperty,
            Duration = TimeSpan.FromMilliseconds(160),
            Easing = new CubicEaseOut()
        }
    ];

    /// <summary>
    /// Возникает синхронно при смене активной страницы в хосте.
    /// </summary>
    public static event Action? ActivePageChanged;

    /// <summary>
    /// Единая точка проверки (DRY/SRP), находится ли визуальный элемент на активной видимой странице
    /// в не-свёрнутом окне без активного режима приостановки <see cref="SuspendLevel"/>.
    /// Не отсекает элементы по промежуточному <c>Opacity == 0</c> обычных контейнеров во время fade-in анимаций.
    /// </summary>
    public static bool CanAnimateVisual(Visual visual, TopLevel? cachedTopLevel = null)
    {
        if (!visual.IsEffectivelyVisible || ViewModelBase.CurrentSuspendLevel != SuspendLevel.None)
            return false;

        var topLevel = cachedTopLevel ?? TopLevel.GetTopLevel(visual);
        if (topLevel == null)
            return false;

        if (topLevel is Window w && (!w.IsVisible || w.WindowState == WindowState.Minimized))
            return false;

        Visual? current = visual;
        while (current != null)
        {
            Visual? parent = current.GetVisualParent();
            if (parent is PersistentPageHost && current is InputElement page && !page.IsHitTestVisible)
                return false;

            current = parent;
        }

        return true;
    }

    private readonly Dictionary<object, Control> _pageCache = new(8);
    private Control? _activeView;

    public static readonly StyledProperty<object?> CurrentPageProperty =
        AvaloniaProperty.Register<PersistentPageHost, object?>(nameof(CurrentPage));

    public object? CurrentPage
    {
        get => GetValue(CurrentPageProperty);
        set => SetValue(CurrentPageProperty, value);
    }

    public static readonly StyledProperty<IEnumerable<object?>?> PrewarmPagesProperty =
        AvaloniaProperty.Register<PersistentPageHost, IEnumerable<object?>?>(nameof(PrewarmPages));

    public IEnumerable<object?>? PrewarmPages
    {
        get => GetValue(PrewarmPagesProperty);
        set => SetValue(PrewarmPagesProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == CurrentPageProperty)
        {
            SwitchToPage(change.NewValue);
        }
        else if (change.Property == PrewarmPagesProperty)
        {
            if (change.NewValue is IEnumerable<object?> pages)
            {
                Prewarm(pages);
            }
        }
    }

    private void Prewarm(IEnumerable<object?> pages)
    {
        foreach (var page in pages)
        {
            if (page is null || _pageCache.ContainsKey(page))
                continue;

            var view = CreatePageView(page);
            bool isCurrent = ReferenceEquals(page, CurrentPage);

            view.IsVisible = true;
            view.Opacity = isCurrent ? 1.0 : 0.0;
            view.IsHitTestVisible = isCurrent;
            view.ZIndex = isCurrent ? 1 : 0;

            _pageCache[page] = view;
            Children.Add(view);

            if (isCurrent)
            {
                _activeView = view;
            }
        }
    }

    private void SwitchToPage(object? targetPage)
    {
        try
        {
            if (targetPage is null)
            {
                if (_activeView != null)
                {
                    _activeView.IsHitTestVisible = false;
                    _activeView.Opacity = 0.0;
                    _activeView = null;
                }

                return;
            }

            if (!_pageCache.TryGetValue(targetPage, out var nextView))
            {
                nextView = CreatePageView(targetPage);
                nextView.IsVisible = true;
                nextView.Opacity = 0.0;
                nextView.IsHitTestVisible = false;

                _pageCache[targetPage] = nextView;
                Children.Add(nextView);
            }

            if (ReferenceEquals(_activeView, nextView))
            {
                nextView.Opacity = 1.0;
                nextView.IsHitTestVisible = true;
                nextView.ZIndex = 1;
                ActivePageChanged?.Invoke();
                return;
            }

            var previousView = _activeView;
            _activeView = nextView;

            nextView.ZIndex = 1;
            nextView.IsHitTestVisible = true;
            nextView.Opacity = 1.0;

            if (previousView != null)
            {
                previousView.ZIndex = 0;
                previousView.IsHitTestVisible = false;
                previousView.Opacity = 0.0;
            }

            ActivePageChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error($"[PersistentPageHost] Navigation transition failed: {ex.Message}");
        }
    }

    private Control CreatePageView(object data)
    {
        if (data is Control directControl)
        {
            directControl.Transitions = PageTransitions;
            return directControl;
        }

        var template = this.FindDataTemplate(data);
        var created = template?.Build(data) ?? new ContentControl { Content = data };
        created.DataContext = data;
        created.Transitions = PageTransitions;
        return created;
    }
}