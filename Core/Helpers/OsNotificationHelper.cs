using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LMP.Core.Helpers;

/// <summary>
/// Кроссплатформенные уведомления и системные диалоговые окна ОС.
/// Минимальная реализация без внешних зависимостей.
/// </summary>
public static partial class OsNotificationHelper
{
    #region Win32 Native Interop

    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONERROR = 0x00000010;
    private const uint MB_TOPMOST = 0x00040000;
    private const uint MB_SETFOREGROUND = 0x00010000;

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(IntPtr hWndNewOwner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalLock(IntPtr hMem);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(IntPtr hMem);

    #endregion

    /// <summary>
    /// Отображает модальное окно фатальной ошибки и автоматически копирует полный отчет в буфер обмена.
    /// Работает даже если графическая подсистема Avalonia или среда .NET еще не успели инициализироваться.
    /// </summary>
    /// <param name="title">Заголовок окна ошибки.</param>
    /// <param name="message">Краткое описание ошибки.</param>
    /// <param name="details">Технические подробности (StackTrace, InnerException).</param>
    public static void ShowFatalError(string title, string message, string? details = null)
    {
        var sb = new StringBuilder(1024);
        sb.AppendLine($"=== {title} ===");
        sb.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"Framework: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine();
        sb.AppendLine("Message:");
        sb.AppendLine(message);

        if (!string.IsNullOrWhiteSpace(details))
        {
            sb.AppendLine();
            sb.AppendLine("Details:");
            sb.AppendLine(details);
        }

        string fullReport = sb.ToString();

        // 1. Автоматически копируем полный стек-трейс в буфер обмена
        TryCopyTextToClipboard(fullReport);

        // 2. Отображаем модальное окно пользователю
        string displayMessage = OperatingSystem.IsWindows()
            ? $"[Полный текст ошибки скопирован в буфер обмена]\n\n{message}\n\n{(details != null && details.Length > 300 ? details[..300] + "..." : details)}"
            : $"{message}\n\n{details}";

        if (OperatingSystem.IsWindows())
        {
            MessageBox(
                IntPtr.Zero,
                displayMessage,
                title,
                MB_OK | MB_ICONERROR | MB_TOPMOST | MB_SETFOREGROUND);
        }
        else if (OperatingSystem.IsMacOS())
        {
            try
            {
                var script = $"display alert \"{EscapeAppleScript(title)}\" message \"{EscapeAppleScript(displayMessage)}\" as critical buttons {{\"OK\"}} default button \"OK\"";
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "osascript",
                    Arguments = $"-e '{script}'",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                });
                proc?.WaitForExit(10000);
            }
            catch
            {
                Console.Error.WriteLine($"[{title}] {fullReport}");
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "zenity",
                    Arguments = $"--error --title=\"{EscapeShell(title)}\" --text=\"{EscapeShell(displayMessage)}\" --width=500",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                proc?.WaitForExit(10000);
            }
            catch
            {
                Console.Error.WriteLine($"[{title}] {fullReport}");
            }
        }
        else
        {
            Console.Error.WriteLine($"[{title}] {fullReport}");
        }
    }

    /// <summary>
    /// Копирует текст в системный буфер обмена без сторонних зависимостей.
    /// </summary>
    public static bool TryCopyTextToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!OpenClipboard(IntPtr.Zero)) return false;
                try
                {
                    EmptyClipboard();

                    int bytesCount = (text.Length + 1) * sizeof(char);
                    IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytesCount);
                    if (hGlobal == IntPtr.Zero) return false;

                    IntPtr target = GlobalLock(hGlobal);
                    if (target == IntPtr.Zero) return false;

                    try
                    {
                        Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                        Marshal.WriteInt16(target + (text.Length * sizeof(char)), 0);
                    }
                    finally
                    {
                        GlobalUnlock(hGlobal);
                    }

                    return SetClipboardData(CF_UNICODETEXT, hGlobal) != IntPtr.Zero;
                }
                finally
                {
                    CloseClipboard();
                }
            }

            if (OperatingSystem.IsMacOS())
            {
                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "pbcopy",
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                proc.StandardInput.Write(text);
                proc.StandardInput.Close();
                proc.WaitForExit(2000);
                return proc.ExitCode == 0;
            }

            if (OperatingSystem.IsLinux())
            {
                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "xclip",
                        Arguments = "-selection clipboard",
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                proc.StandardInput.Write(text);
                proc.StandardInput.Close();
                proc.WaitForExit(2000);
                return proc.ExitCode == 0;
            }
        }
        catch
        {
            // Безопасный fallback при блокировке буфера обмена
        }

        return false;
    }

    /// <summary>
    /// Показывает стандартное всплывающее уведомление ОС.
    /// </summary>
    public static async Task ShowAsync(string title, string message, NotificationSeverity severity)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                await ShowWindowsToastAsync(title, message);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                await ShowLinuxNotificationAsync(title, message, severity);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                await ShowMacNotificationAsync(title, message);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[OsNotification] Failed: {ex.Message}");
        }
    }

    private static async Task ShowWindowsToastAsync(string title, string message)
    {
        _ = title.Replace("'", "''").Replace("`", "``");
        _ = message.Replace("'", "''").Replace("`", "``");

        var script = $"""
            [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
            [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null
            $xml = @"
            <toast>
                <visual>
                    <binding template='ToastGeneric'>
                        <text>{EscapeXml(title)}</text>
                        <text>{EscapeXml(message)}</text>
                    </binding>
                </visual>
                <audio silent='true'/>
            </toast>
            "@
            $XmlDocument = [Windows.Data.Xml.Dom.XmlDocument]::new()
            $XmlDocument.LoadXml($xml)
            $AppId = 'LiteMusicPlayer'
            [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($AppId).Show([Windows.UI.Notifications.ToastNotification]::new($XmlDocument))
            """;

        await RunProcessAsync("powershell", $"-NoProfile -NonInteractive -Command \"{script}\"", 5000);
    }

    private static async Task ShowLinuxNotificationAsync(string title, string message, NotificationSeverity severity)
    {
        var urgency = severity switch
        {
            NotificationSeverity.Error => "critical",
            NotificationSeverity.Warning => "normal",
            _ => "low"
        };

        await RunProcessAsync("notify-send",
            $"--urgency={urgency} --app-name=\"Lite Music Player\" \"{EscapeShell(title)}\" \"{EscapeShell(message)}\"",
            3000);
    }

    private static async Task ShowMacNotificationAsync(string title, string message)
    {
        var script = $"display notification \"{EscapeAppleScript(message)}\" with title \"{EscapeAppleScript(title)}\"";
        await RunProcessAsync("osascript", $"-e '{script}'", 3000);
    }

    private static async Task RunProcessAsync(string fileName, string arguments, int timeoutMs)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        process.Start();

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch { }
        }
    }

    private static string EscapeXml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string EscapeShell(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`");

    private static string EscapeAppleScript(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}