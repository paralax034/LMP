using Avalonia.Threading;


namespace LMP.UI.Features.Shell;

/// <summary>
/// Контейнер для отображения диалогов поверх контента.
/// Поддерживает стек диалогов: если новый диалог открывается поверх активного,
/// активный приостанавливается и восстанавливается после закрытия нового.
/// </summary>
public sealed partial class DialogHostViewModel : ViewModelBase
{
    /// <summary>Текущий отображаемый диалог.</summary>
    [ObservableProperty] public partial object? CurrentDialog { get; private set; }

    /// <summary>Есть ли активный диалог.</summary>
    [ObservableProperty] public partial bool HasActiveDialog { get; private set; }

    //  Стек: (контент диалога, его TCS) 
    private readonly Stack<(object Content, TaskCompletionSource<object?> Tcs)> _stack = new();
    private TaskCompletionSource<object?>? _dialogTcs;
    private readonly Lock _lock = new();

    /// <summary>
    /// Показывает диалог поверх текущего (если есть) и асинхронно ожидает результата.
    /// При наличии активного диалога он уходит в стек и восстанавливается после закрытия нового.
    /// </summary>
    /// <remarks>
    /// Диалог стекируется только если его TCS ещё не завершён. Завершённый TCS означает,
    /// что <see cref="CloseDialog"/> уже был вызван, а асинхронная очистка UI-состояния
    /// ещё не успела выполниться. Восстановление такого диалога приводило бы
    /// к показу освобождённого ViewModel, который никто не ожидает.
    /// </remarks>
    public async Task<T?> ShowAsync<T>(object dialogContent)
    {
        var tcs = new TaskCompletionSource<object?>();

        lock (_lock)
        {
            // Стекируем текущий диалог только если его TCS ещё активен.
            // Завершённый TCS = CloseDialog уже вызван, диалог закрыт —
            // восстанавливать его после нового диалога нельзя.
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
                    return; // Другой диалог уже взял управление

                // Защитная очистка: отбрасываем записи с завершённым TCS,
                // которые могли попасть в стек до введения проверки IsCompleted
                // или из-за иных граничных сценариев (Dispose, повторный CloseDialog).
                while (_stack.Count > 0 && _stack.Peek().Tcs.Task.IsCompleted)
                {
                    var stale = _stack.Pop();
                    Log.Debug($"[DialogHost] Discarded stale stacked dialog: {stale.Content.GetType().Name}");
                }

                if (_stack.Count > 0)
                {
                    // Восстанавливаем предыдущий диалог из стека
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

    /// <summary>Закрывает текущий верхний диалог с указанным результатом.</summary>
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
