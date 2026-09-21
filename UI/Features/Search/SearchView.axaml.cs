using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using LMP.Core.Helpers.Extensions;

namespace LMP.UI.Features.Search;

/// <summary>
/// Представление экрана поиска треков.
/// Координирует адаптивную маску краев ленты, туннелирование клавиш автодополнения и управление скроллом.
/// </summary>
public partial class SearchView : UserControl
{
    private static readonly IBrush RightFadeMask = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        [
            new GradientStop(Color.FromRgb(255, 255, 255), 0.0),
            new GradientStop(Color.FromRgb(255, 255, 255), 0.92),
            new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0)
        ]
    };

    private static readonly IBrush LeftFadeMask = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        [
            new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.0),
            new GradientStop(Color.FromRgb(255, 255, 255), 0.08),
            new GradientStop(Color.FromRgb(255, 255, 255), 1.0)
        ]
    };

    private static readonly IBrush BothFadeMask = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        [
            new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.0),
            new GradientStop(Color.FromRgb(255, 255, 255), 0.06),
            new GradientStop(Color.FromRgb(255, 255, 255), 0.94),
            new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0)
        ]
    };

    private SearchViewModel? _currentVm;
    private ScrollViewer? _ribbonSv;

    public SearchView()
    {
        InitializeComponent();

        var searchBox = this.FindControl<TextBox>("SearchBox");
        if (searchBox != null)
        {
            searchBox.GotFocus += (sender, e) =>
            {
                if (DataContext is SearchViewModel vm && string.IsNullOrWhiteSpace(vm.SearchQuery))
                {
                    vm.OpenHistoryIfAvailable();
                }
            };

            searchBox.AddHandler(InputElement.KeyDownEvent, OnSearchBoxKeyDown, RoutingStrategies.Tunnel);
            searchBox.AddHandler(TextBox.PastingFromClipboardEvent, OnSearchBoxPastingFromClipboard, RoutingStrategies.Tunnel);
        }
    }

    /// <summary>
    /// Перехватывает нативное событие вставки в TextBox из любого источника (клавиатура или контекстное меню)
    /// для гарантированной санитизации строк с начальными переносами каретки.
    /// </summary>
    private async void OnSearchBoxPastingFromClipboard(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox textBox) return;

        e.Handled = true;
        await PasteCleanTextAsync(textBox);
    }

    /// <summary>
    /// Извлекает текст из буфера обмена ОС, нормализует его в однострочный вид и вставляет в позицию выделения поля ввода.
    /// </summary>
    /// <param name="textBox">Целевой экземпляр поля ввода.</param>
    /// <returns>Задача асинхронного выполнения операции вставки.</returns>
    private static async Task PasteCleanTextAsync(TextBox textBox)
    {
        string? raw;
        try
        {
            raw = await Clipboard.GetTextAsync();
        }
        catch
        {
            return;
        }

        if (string.IsNullOrEmpty(raw)) return;

        var clean = raw.SanitizeSingleLine();
        var current = textBox.Text ?? string.Empty;

        int start = Math.Clamp(Math.Min(textBox.SelectionStart, textBox.SelectionEnd), 0, current.Length);
        int end = Math.Clamp(Math.Max(textBox.SelectionStart, textBox.SelectionEnd), 0, current.Length);

        var newText = string.Concat(current.AsSpan(0, start), clean, current.AsSpan(end));
        textBox.Text = newText;
        textBox.CaretIndex = start + clean.Length;
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox || DataContext is not SearchViewModel vm)
            return;

        if ((e.Key == Key.V && (e.KeyModifiers & KeyModifiers.Control) != 0) ||
            (e.Key == Key.Insert && (e.KeyModifiers & KeyModifiers.Shift) != 0))
        {
            e.Handled = true;
            _ = PasteCleanTextAsync(textBox);
            return;
        }

        if (e.Key == Key.Tab && vm.HasGhostText)
        {
            e.Handled = true;
            vm.CompleteGhostText();
            textBox.CaretIndex = textBox.Text?.Length ?? 0;
            return;
        }

        if (e.Key == Key.Right && vm.HasGhostText && textBox.CaretIndex == (textBox.Text?.Length ?? 0))
        {
            e.Handled = true;
            vm.CompleteGhostText();
            textBox.CaretIndex = textBox.Text?.Length ?? 0;
            return;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _ribbonSv = this.FindControl<ScrollViewer>("RibbonScrollViewer");
        _ribbonSv?.PropertyChanged += OnRibbonScrollViewerPropertyChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _ribbonSv?.PropertyChanged -= OnRibbonScrollViewerPropertyChanged;
        _ribbonSv = null;

        UnsubscribeVm();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        UnsubscribeVm();

        if (DataContext is SearchViewModel vm)
        {
            _currentVm = vm;
            vm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void UnsubscribeVm()
    {
        _currentVm?.PropertyChanged -= OnViewModelPropertyChanged;
        _currentVm = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchViewModel.IsLoading) && sender is SearchViewModel { IsLoading: true })
        {
            var sv = this.FindControl<ScrollViewer>("ResultsScrollViewer");
            sv?.Offset = new Vector(0, 0);
        }
    }

    private void OnRibbonScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is ScrollViewer sv &&
            (e.Property == ScrollViewer.OffsetProperty ||
             e.Property == ScrollViewer.ExtentProperty ||
             e.Property == ScrollViewer.ViewportProperty))
        {
            UpdateRibbonMask(sv);
        }
    }

    /// <summary>
    /// Динамически вычисляет маску затухания краев ленты:
    /// маски появляются строго при наличии доступной области прокрутки.
    /// </summary>
    private static void UpdateRibbonMask(ScrollViewer sv)
    {
        double extent = sv.Extent.Width;
        double viewport = sv.Viewport.Width;
        double offset = sv.Offset.X;

        if (extent <= viewport || viewport <= 0)
        {
            sv.OpacityMask = null;
            return;
        }

        bool hasLeft = offset > 4.0;
        bool hasRight = (offset + viewport) < (extent - 4.0);

        if (hasLeft && hasRight)
            sv.OpacityMask = BothFadeMask;
        else if (hasLeft)
            sv.OpacityMask = LeftFadeMask;
        else if (hasRight)
            sv.OpacityMask = RightFadeMask;
        else
            sv.OpacityMask = null;
    }

    /// <summary>
    /// Обрабатывает клики по чипам: ЛКМ — моментальный поиск, ПКМ — удаление элемента.
    /// </summary>
    private void OnSuggestionPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: SearchSuggestionItem item })
            return;

        var point = e.GetCurrentPoint(this);

        if (point.Properties.IsLeftButtonPressed)
        {
            e.Handled = true;
            ((ICommand)item.Owner.SuggestionClickCommand).Execute(item.Text);
        }
        else if (point.Properties.IsRightButtonPressed)
        {
            e.Handled = true;
            ((ICommand)item.Owner.RemoveSuggestionCommand).Execute(item.Text);
        }
    }

    /// <summary>
    /// Гарантированный перехват контекстного меню чипа для удаления запроса из истории.
    /// </summary>
    private void OnSuggestionContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control { DataContext: SearchSuggestionItem item })
        {
            e.Handled = true;
            ((ICommand)item.Owner.RemoveSuggestionCommand).Execute(item.Text);
        }
    }
}