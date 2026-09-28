using System.Windows.Input;
using Avalonia.Threading;

namespace LMP.UI.ViewModels;

/// <summary>
/// Сверхлёгкая async команда для TrackItemViewModel с поддержкой нотификации CanExecuteChanged.
/// Не создаёт Subject, Observable, Scheduler — только один int для флага выполнения.
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
/// Сверхлёгкая sync команда для TrackItemViewModel.
/// CanExecute всегда true — никакого Observable overhead.
/// </summary>
internal sealed class TrackSyncCommand : ICommand
{
    private readonly Action _execute;

    public TrackSyncCommand(Action execute) => _execute = execute;

    /// <summary>
    /// CanExecute всегда true — событие никогда не стреляет.
    /// Пустые аксессоры предотвращают CS0067 и исключают аллокацию backing field.
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