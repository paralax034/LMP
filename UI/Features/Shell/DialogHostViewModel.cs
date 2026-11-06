using Avalonia.Threading;


namespace LMP.UI.Features.Shell;

/// <summary>
/// Container for displaying dialogs on top of content.
/// Supports a dialog stack: if a new dialog opens over an active one,
/// the active one is suspended and restored after the new one closes.
/// </summary>
public sealed partial class DialogHostViewModel : ViewModelBase
{
    /// <summary>The currently displayed dialog.</summary>
    [ObservableProperty] public partial object? CurrentDialog { get; private set; }

    /// <summary>Indicates whether a dialog is currently active.</summary>
    [ObservableProperty] public partial bool HasActiveDialog { get; private set; }

    //  Stack: (dialog content, its TCS) 
    private readonly Stack<(object Content, TaskCompletionSource<object?> Tcs)> _stack = new();
    private TaskCompletionSource<object?>? _dialogTcs;
    private readonly Lock _lock = new();

    /// <summary>
    /// Displays a dialog on top of the current one (if any) and asynchronously awaits the result.
    /// If an active dialog exists, it is pushed to the stack and restored after the new one closes.
    /// </summary>
    /// <remarks>
    /// A dialog is stacked only if its TCS is not yet completed. A completed TCS means
    /// that <see cref="CloseDialog"/> was already called, but asynchronous UI state cleanup
    /// has not executed yet. Restoring such a dialog would lead
    /// to displaying a disposed ViewModel that nothing is awaiting.
    /// </remarks>
    public async Task<T?> ShowAsync<T>(object dialogContent)
    {
        var tcs = new TaskCompletionSource<object?>();

        lock (_lock)
        {
            // Stack the current dialog only if its TCS is still active.
            // Completed TCS = CloseDialog was already called, the dialog is closed —
            // it must not be restored after the new dialog.
            if (HasActiveDialog && _dialogTcs is { Task.IsCompleted: false } && CurrentDialog != null)
            {
                Log.Debug($"[DialogHost] Stacking dialog. Suspending: {CurrentDialog.GetType().Name}, Showing: {dialogContent.GetType().Name}");
                _stack.Push((CurrentDialog, _dialogTcs));
            }

            _dialogTcs = tcs;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            CurrentDialog = dialogContent;
            HasActiveDialog = true;
        });

        Log.Debug($"[DialogHost] Showing: {dialogContent.GetType().Name}");

        var result = await tcs.Task;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            lock (_lock)
            {
                if (_dialogTcs != tcs)
                    return; // Another dialog has already taken control

                // Defensive cleanup: discard entries with a completed TCS
                // that might have entered the stack before the IsCompleted check was introduced
                // or due to other edge case scenarios (Dispose, repeated CloseDialog).
                while (_stack.Count > 0 && _stack.Peek().Tcs.Task.IsCompleted)
                {
                    var stale = _stack.Pop();
                    Log.Debug($"[DialogHost] Discarded stale stacked dialog: {stale.Content.GetType().Name}");
                }

                if (_stack.Count > 0)
                {
                    // Restore the previous dialog from the stack
                    var (prevContent, prevTcs) = _stack.Pop();
                    _dialogTcs = prevTcs;
                    CurrentDialog = prevContent;
                    HasActiveDialog = true;
                    Log.Debug($"[DialogHost] Restored from stack: {prevContent.GetType().Name}");
                }
                else
                {
                    HasActiveDialog = false;
                    CurrentDialog = null;
                    _dialogTcs = null;
                }
            }
        });

        Log.Debug($"[DialogHost] Closed: {dialogContent.GetType().Name}, result: {result}");
        return result is T typed ? typed : default;
    }

    /// <summary>Closes the current top dialog with the specified result.</summary>
    public void CloseDialog(object? result = null)
    {
        lock (_lock)
        {
            _dialogTcs?.TrySetResult(result);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_lock)
            {
                _dialogTcs?.TrySetCanceled();
                while (_stack.Count > 0)
                {
                    var (_, stackedTcs) = _stack.Pop();
                    stackedTcs.TrySetCanceled();
                }
                CurrentDialog = null;
                HasActiveDialog = false;
            }
        }
        base.Dispose(disposing);
    }
}
