using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

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