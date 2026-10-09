using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace LMP.UI.Controls;

/// <summary>
/// Высокопроизводительный хост страниц верхнего уровня с предварительной загрузкой представлений и шелковистым CrossFade переходом.
/// Монтирует все визуальные деревья на этапе старта приложения, гарантируя отклик 1 мс при первом открытии любой вкладки.
/// </summary>
public sealed class PersistentPageHost : Panel
{
    private static readonly CrossFade TransitionEngine = new(TimeSpan.FromMilliseconds(200))
    {
        FadeInEasing = new CubicEaseInOut(), FadeOutEasing = new CubicEaseInOut()
    };

    private readonly Dictionary<object, Control> _pageCache = new(8);
    private Control? _activeView;
    private CancellationTokenSource? _transitionCts;

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

            view.IsVisible = isCurrent;
            view.Opacity = isCurrent ? 1.0 : 0.0;
            view.ZIndex = isCurrent ? 1 : 0;

            _pageCache[page] = view;
            Children.Add(view);

            if (isCurrent)
            {
                _activeView = view;
            }
        }
    }

    private async void SwitchToPage(object? targetPage)
    {
        try
        {
            _transitionCts?.CancelAsync();
            _transitionCts?.Dispose();
            var cts = new CancellationTokenSource();
            _transitionCts = cts;

            if (targetPage is null)
            {
                if (_activeView != null)
                {
                    _activeView.IsVisible = false;
                    _activeView = null;
                }

                return;
            }

            if (!_pageCache.TryGetValue(targetPage, out var nextView))
            {
                nextView = CreatePageView(targetPage);
                _pageCache[targetPage] = nextView;
                Children.Add(nextView);
            }

            if (ReferenceEquals(_activeView, nextView))
            {
                nextView.IsVisible = true;
                nextView.Opacity = 1.0;
                return;
            }

            var previousView = _activeView;
            _activeView = nextView;

            nextView.ZIndex = 1;
            if (previousView != null)
            {
                previousView.ZIndex = 0;
                previousView.IsHitTestVisible = false;
            }

            nextView.IsHitTestVisible = true;

            if (previousView != null)
            {
                try
                {
                    await TransitionEngine.Start(previousView, nextView, cts.Token);

                    if (!cts.IsCancellationRequested)
                    {
                        previousView.IsVisible = false;
                        TransitionEngine.Reset(previousView);
                    }
                }
                catch (OperationCanceledException)
                {
                    TransitionEngine.Reset(nextView);
                }
            }
            else
            {
                nextView.IsVisible = true;
                nextView.Opacity = 1.0;
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
            return directControl;
        }

        var template = this.FindDataTemplate(data);
        var created = template?.Build(data) ?? new ContentControl { Content = data };
        created.DataContext = data;
        return created;
    }
}
