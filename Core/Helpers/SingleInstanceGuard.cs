using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace LMP.Core.Helpers;

/// <summary>
/// Обеспечивает запуск строго одного экземпляра приложения в рамках пользовательской сессии.
/// Инкапсулирует системный мьютекс, кроссплатформенный IPC-канал передачи фокуса,
/// автоматически устраняет зависшие зомби-процессы и восстанавливает главное окно.
/// </summary>
public sealed partial class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\LMP_SingleInstance_Mutex_paralax034";
    private const string PipeName = "LMP_SingleInstance_IPC_paralax034";
    private const byte CommandWakeUp = 0x01;
    private const int IpcConnectTimeoutMs = 200;
    private const int IpcConnectDebounceMs = 300;

    private const int SwRestore = 9;
    private const int SwShow = 5;

    private const uint MB_YESNO = 0x00000004;
    private const uint MB_ICONQUESTION = 0x00000020;
    private const uint MB_TOPMOST = 0x00040000;
    private const uint MB_SETFOREGROUND = 0x00010000;
    private const int IDYES = 6;

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _serverCts;
    private static Action? _activationCallback;
    private bool _disposed;

    private SingleInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
        _serverCts = new CancellationTokenSource();
        StartIpcServerLoop(_serverCts.Token);
    }

    /// <summary>
    /// Регистрирует делегат активации главного окна при получении межпроцессного сигнала от второго экземпляра.
    /// </summary>
    public static void RegisterActivationCallback(Action? callback)
    {
        _activationCallback = callback;
    }

    /// <summary>
    /// Пытается захватить владение системным мьютексом единственного экземпляра.
    /// </summary>
    /// <returns>
    /// Экземпляр <see cref="SingleInstanceGuard"/> при успешном запуске (освобождает мьютекс при Dispose),
    /// либо <c>null</c>, если процесс уже запущен и пользователь отказался от его перезапуска.
    /// </returns>
    public static SingleInstanceGuard? TryAcquire()
    {
        Mutex? mutex;
        bool isOnlyInstance;

        try
        {
            mutex = new Mutex(true, MutexName, out isOnlyInstance);
            if (!isOnlyInstance)
            {
                try
                {
                    if (mutex.WaitOne(0, false))
                        isOnlyInstance = true;
                }
                catch (AbandonedMutexException)
                {
                    isOnlyInstance = true;
                }
            }
        }
        catch (AbandonedMutexException)
        {
            isOnlyInstance = true;
            mutex = new Mutex(true, MutexName, out _);
        }

        if (isOnlyInstance)
            return new SingleInstanceGuard(mutex);

        mutex?.Dispose();
        mutex = null;

        // Второй процесс: пытаемся разбудить первый через быстрый кроссплатформенный IPC
        if (TryNotifyActiveInstance())
        {
            return null;
        }

        // Проверяем существующие процессы: устранение зависаний либо подтверждение перезапуска пользователем
        if (HandleExistingProcesses())
        {
            try
            {
                // Даём ОС паузу для освобождения системных дескрипторов после завершения старого процесса
                Thread.Sleep(150);
                mutex = new Mutex(true, MutexName, out isOnlyInstance);
                if (!isOnlyInstance)
                {
                    try
                    {
                        if (mutex.WaitOne(0, false))
                            isOnlyInstance = true;
                    }
                    catch (AbandonedMutexException)
                    {
                        isOnlyInstance = true;
                    }
                }

                if (isOnlyInstance)
                    return new SingleInstanceGuard(mutex);
            }
            catch (AbandonedMutexException)
            {
                mutex = new Mutex(true, MutexName, out _);
                return new SingleInstanceGuard(new Mutex(true, MutexName, out _));
            }
            finally
            {
                if (!isOnlyInstance)
                    mutex?.Dispose();
            }
        }

        return null;
    }

    private static bool TryNotifyActiveInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(IpcConnectTimeoutMs);
            client.WriteByte(CommandWakeUp);
            client.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void StartIpcServerLoop(CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                    int cmd = server.ReadByte();
                    if (cmd == CommandWakeUp)
                    {
                        _activationCallback?.Invoke();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    if (!await ct.DelayNoThrowAsync(IpcConnectDebounceMs, continueOnCapturedContext: false))
                        break;
                }
            }
        }, ct);
    }

    /// <summary>
    /// Инспектирует запущенные процессы с аналогичным именем: принудительно завершает зависшие процессы,
    /// запрашивает перезапуск у пользователя для живого процесса либо выводит его окно на передний план.
    /// </summary>
    /// <returns><c>true</c>, если предыдущий процесс был завершён и можно продолжить запуск текущего экземпляра.</returns>
    private static bool HandleExistingProcesses()
    {
        try
        {
            using var currentProcess = Process.GetCurrentProcess();
            var processes = Process.GetProcessesByName(currentProcess.ProcessName);

            Process? activeProcess = null;

            foreach (var proc in processes)
            {
                if (proc.Id == currentProcess.Id)
                {
                    proc.Dispose();
                    continue;
                }

                // 1. Принудительное уничтожение зависшего зомби-процесса без лишних вопросов
                if (!proc.Responding)
                {
                    try
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(2000);
                        proc.Dispose();
                        return true;
                    }
                    catch
                    {
                        proc.Dispose();
                    }

                    continue;
                }

                activeProcess = proc;
                break;
            }

            if (activeProcess == null)
                return true;

            using (activeProcess)
            {
                // 2. Интерактивный диалог: завершить старый процесс или оставить его
                if (PromptTerminateExistingProcess())
                {
                    try
                    {
                        activeProcess.Kill(entireProcessTree: true);
                        activeProcess.WaitForExit(3000);
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                }

                // 3. Пользователь отказался от перезапуска — активируем окно существующего процесса
                ActivateProcessWindow(activeProcess);
            }
        }
        catch
        {
            // Ошибки доступа к чужим процессам не должны вызывать аварийный сбой запуска
        }

        return false;
    }

    /// <summary>
    /// Отображает модальный системный диалог с вопросом о завершении предыдущего процесса.
    /// </summary>
    /// <returns><c>true</c>, если пользователь подтвердил закрытие предыдущего процесса.</returns>
    private static bool PromptTerminateExistingProcess()
    {
        string title = "Lite Music Player";
        string question =
            "Lite Music Player is already running.\n\nWould you like to terminate the existing instance and start a new one?";

        try
        {
            BootstrapSettings.Initialize();
            var lang = BootstrapSettings.Current.LanguageCode;
            LocalizationService.Instance.Initialize(lang);

            var locTitle = LocalizationService.Instance["Notification_AlreadyRunning_Title"];
            var locQuestion = LocalizationService.Instance["Notification_AlreadyRunning_Question"];

            // Проверяем, что локализация вернула реальные значения, а не сырые ключи вида [Key]
            title = locTitle;
            question = locQuestion;
        }
        catch
        {
            // Безопасный fallback при непредвиденных ошибках чтения настроек
        }

        if (OperatingSystem.IsWindows())
        {
            int result = MessageBox(IntPtr.Zero, question, title,
                MB_YESNO | MB_ICONQUESTION | MB_TOPMOST | MB_SETFOREGROUND);
            return result == IDYES;
        }

        if (OperatingSystem.IsMacOS())
        {
            try
            {
                var script =
                    $"display dialog \"{EscapeAppleScript(question)}\" with title \"{EscapeAppleScript(title)}\" buttons {{\"No\", \"Yes\"}} default button \"Yes\" with icon caution";
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "osascript",
                    Arguments = $"-e '{script}'",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                });

                if (proc != null)
                {
                    var output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(10000);
                    return output.Contains("button returned:Yes", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "zenity",
                    Arguments =
                        $"--question --title=\"{EscapeShell(title)}\" --text=\"{EscapeShell(question)}\" --ok-label=\"Yes\" --cancel-label=\"No\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (proc != null)
                {
                    proc.WaitForExit(10000);
                    return proc.ExitCode == 0;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    /// <summary>
    /// Выводит на передний план окно указанного процесса.
    /// Корректно находит хэндл окна через перечисление дескрипторов, даже если окно было скрыто в трей.
    /// </summary>
    /// <param name="proc">Целевой процесс существующего экземпляра.</param>
    private static void ActivateProcessWindow(Process proc)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var hWnd = proc.MainWindowHandle;

        if (hWnd == IntPtr.Zero)
        {
            hWnd = FindProcessWindowHandle(proc.Id);
        }

        if (hWnd != IntPtr.Zero)
        {
            ShowWindow(hWnd, SwShow);
            ShowWindow(hWnd, SwRestore);
            SetForegroundWindow(hWnd);
        }
    }

    /// <summary>
    /// Находит дескриптор окна процесса через системное перечисление Win32 окон.
    /// </summary>
    /// <param name="processId">Идентификатор целевого процесса.</param>
    /// <returns>Дескриптор окна либо <see cref="IntPtr.Zero"/>, если окно не найдено.</returns>
    private static IntPtr FindProcessWindowHandle(int processId)
    {
        IntPtr result = IntPtr.Zero;

        EnumWindows((hWnd, lParam) =>
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == (uint)processId)
            {
                result = hWnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return result;
    }

    private static string EscapeShell(string s) =>
        s.Replace("\\", @"\\\\").Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`");

    private static string EscapeAppleScript(string s) =>
        s.Replace("\\", @"\\\\").Replace("\"", "\\\"");

    #region Win32 P/Invoke

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    #endregion

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _serverCts.Cancel();
            _serverCts.Dispose();
        }
        catch
        {
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch
        {
        }

        _mutex.Dispose();
    }
}
