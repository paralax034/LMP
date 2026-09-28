using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LMP.UI.Features.Shell;

public partial class MainWindow
{
    private static int CopyHintDurationMs => CopyHintService.Instance.DisplayDurationMs;
    private const int CopyHintFadeDurationMs = 150;

    private Canvas? _copyHintCanvas;
    private Border? _copyHintOverlay;
    private TextBlock? _copyHintText;
    private PathIcon? _copyHintIcon;
    private CancellationTokenSource? _copyHintCts;

    private void InitializeCopyHint()
    {
        _copyHintCanvas = this.FindControl<Canvas>("CopyHintCanvas");
        _copyHintOverlay = this.FindControl<Border>("CopyHintOverlay");
        _copyHintText = this.FindControl<TextBlock>("CopyHintText");
        _copyHintIcon = this.FindControl<PathIcon>("CopyHintIcon");

        CopyHintService.Instance.HintRequested += OnCopyHintRequested;
    }

    private void CleanupCopyHint()
    {
        CopyHintService.Instance.HintRequested -= OnCopyHintRequested;
        _copyHintCts?.Cancel();
        _copyHintCts?.Dispose();

        if (_copyHintOverlay != null)
        {
            _copyHintOverlay.IsVisible = false;
            _copyHintOverlay.Opacity = 0;
        }
    }

    private void OnCopyHintRequested(string text, CopyHintKind kind, Point? cursorPosition)
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => OnCopyHintRequested(text, kind, cursorPosition));
            return;
        }

        ShowCopyHint(text, kind, cursorPosition);
    }

    private async void ShowCopyHint(string text, CopyHintKind kind, Point? cursorPosition)
    {
        if (_copyHintOverlay is null || _copyHintText is null || _copyHintCanvas is null)
            return;

        _copyHintCts?.Cancel();
        _copyHintCts?.Dispose();
        var cts = new CancellationTokenSource();
        _copyHintCts = cts;

        try
        {
            ApplyCopyHintStyle(kind);
            _copyHintText.Text = text;

            _copyHintOverlay.IsVisible = true;
            _copyHintOverlay.Opacity = 0;

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                () => { }, Avalonia.Threading.DispatcherPriority.Render);

            PositionCopyHint(cursorPosition);
            _copyHintOverlay.Opacity = 1;

            await Task.Delay(CopyHintDurationMs, cts.Token);

            _copyHintOverlay.Opacity = 0;
            await Task.Delay(CopyHintFadeDurationMs, cts.Token);

            _copyHintOverlay.IsVisible = false;
        }
        catch (OperationCanceledException) { }
    }

    private void PositionCopyHint(Point? cursorPosition)
    {
        if (_copyHintCanvas is null || _copyHintOverlay is null) return;

        double hintW = _copyHintOverlay.DesiredSize.Width;
        double hintH = _copyHintOverlay.DesiredSize.Height;
        double canvasW = _copyHintCanvas.Bounds.Width;
        double canvasH = _copyHintCanvas.Bounds.Height;

        const double margin = 8.0;

        if (cursorPosition is not { } cursor)
        {
            Canvas.SetLeft(_copyHintOverlay, Math.Max(margin, (canvasW - hintW) * 0.5));
            Canvas.SetTop(_copyHintOverlay, Math.Max(margin, canvasH - hintH - 40));
            return;
        }

        double x = cursor.X - hintW / 2.0;
        double y = cursor.Y - hintH - 12;

        if (y < margin)
            y = cursor.Y + 24;

        x = Math.Clamp(x, margin, canvasW - hintW - margin);
        y = Math.Clamp(y, margin, canvasH - hintH - margin);

        Canvas.SetLeft(_copyHintOverlay, x);
        Canvas.SetTop(_copyHintOverlay, y);
    }

    private void ApplyCopyHintStyle(CopyHintKind kind)
    {
        if (_copyHintIcon is null || _copyHintOverlay is null) return;

        var (iconKey, brushKey) = kind switch
        {
            CopyHintKind.Warning => ("Icon.InformationOutline", "SystemWarnOrangeBrush"),
            CopyHintKind.Error => ("Icon.Close", "SystemErrorBrush"),
            _ => ("Icon.CheckCircle", "AccentBrush")
        };

        var theme = Application.Current?.ActualThemeVariant;

        if (Application.Current?.Resources.TryGetResource(iconKey, theme, out var geo) == true
            && geo is StreamGeometry geometry)
            _copyHintIcon.Data = geometry;

        if (Application.Current?.Resources.TryGetResource(brushKey, theme, out var res) == true
            && res is IBrush brush)
        {
            _copyHintIcon.Foreground = brush;
            _copyHintOverlay.BorderBrush = brush;
        }
    }
}