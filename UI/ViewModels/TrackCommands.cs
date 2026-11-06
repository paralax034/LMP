using System.Windows.Input;
using Avalonia.Threading;

namespace LMP.UI.ViewModels;

/// <summary>
/// Ultra-lightweight async command for TrackItemViewModel supporting CanExecuteChanged notifications.
/// Does not allocate Subject, Observable, or Scheduler — uses only a single int execution flag.
/// </summary>
internal sealed class TrackAsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private int _isExecuting;

    public event EventHandler? CanExecuteChanged;

    public TrackAsyncCommand(Func<Task> execute) => _execute = execute;

    public bool CanExecute(object? parameter) =>
        Volatile.Read(ref _isExecuting) == 0;

    public async void Execute(object? parameter)
    {
        if (Interlocked.CompareExchange(ref _isExecuting, 1, 0) != 0) return;

        NotifyCanExecuteChanged();

        try
        {
            await _execute();
        }
        catch (Exception ex)
        {
            Log.Error($"[TrackAsyncCommand] {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _isExecuting, 0);
            NotifyCanExecuteChanged();
        }
    }

    private void NotifyCanExecuteChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Dispatcher.UIThread.Post(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
        }
    }
}

/// <summary>
/// Ultra-lightweight sync command for TrackItemViewModel.
/// CanExecute is always true — zero Observable overhead.
/// </summary>
internal sealed class TrackSyncCommand : ICommand
{
    private readonly Action _execute;

    public TrackSyncCommand(Action execute) => _execute = execute;

    /// <summary>
    /// CanExecute is always true — the event never fires.
    /// Empty accessors prevent CS0067 and eliminate backing field allocation.
    /// </summary>
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter)
    {
        try { _execute(); }
        catch (Exception ex) { Log.Error($"[TrackSyncCommand] {ex.Message}"); }
    }
}