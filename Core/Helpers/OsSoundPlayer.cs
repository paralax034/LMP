using System.Runtime.InteropServices;

namespace LMP.Core.Helpers;

/// <summary>
/// Plays system sounds.
/// </summary>
public static partial class OsSoundPlayer
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MessageBeep(uint type);

    // Windows MessageBeep types
    private const uint MB_ICONERROR = 0x00000010;
    private const uint MB_OK = 0x00000000;

    /// <summary>
    /// Plays the system error sound.
    /// </summary>
    public static void PlayError()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                MessageBeep(MB_ICONERROR);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // paplay — PulseAudio, works on most distributions
                _ = Task.Run(() =>
                {
                    try
                    {
                        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "paplay",
                            Arguments = "/usr/share/sounds/freedesktop/stereo/dialog-error.oga",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        });
                        process?.WaitForExit(2000);
                    }
                    catch { /* No sound system available */ }
                });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "afplay",
                            Arguments = "/System/Library/Sounds/Basso.aiff",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        });
                        process?.WaitForExit(2000);
                    }
                    catch { /* ignore */ }
                });
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[ErrorSound] Failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Plays the system success sound.
    /// </summary>
    public static void PlaySuccess()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                MessageBeep(MB_OK);
            }
            // Linux/macOS — same with other audio files
        }
        catch (Exception ex)
        {
            Log.Debug($"[SuccessSound] Failed: {ex.Message}");
        }
    }
}