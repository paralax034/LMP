using Avalonia;
using Avalonia.Controls;

namespace LMP.UI.Features.Debug;

/// <summary>
/// Debug-окно с вкладками: Tests, YouTube, Memory, Audio.
/// DataContext инициализируется до вызова InitializeComponent для исключения предупреждений привязки.
/// </summary>
public partial class DebugWindow : Window
{
    public DebugWindow() : this([])
    {
    }

    public DebugWindow(string[] cliArgs)
    {
        var vm = new DebugViewModel();
        DataContext = vm;

        InitializeComponent();

        vm.TestRunner.ScrollToIndexRequested += OnScrollToTestIndex;

        if (cliArgs.Length > 0)
        {
            vm.TestRunner.ApplyCliArguments(cliArgs);
        }
    }

    private void OnScrollToTestIndex(int index)
    {
        var sv = this.FindControl<ScrollViewer>("TestListScrollViewer");
        if (sv == null || index < 0) return;

        double targetY = Math.Max(0, (index * 33.0) - 60.0);
        sv.Offset = new Vector(sv.Offset.X, targetY);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is DebugViewModel vm)
        {
            vm.TestRunner.ScrollToIndexRequested -= OnScrollToTestIndex;
            vm.Dispose();
        }

        base.OnClosed(e);
    }
}