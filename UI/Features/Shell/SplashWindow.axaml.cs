using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace LMP.UI.Features.Shell;

public partial class SplashWindow : Window
{
    private readonly double _progressBarMaxWidth;
    private readonly Stopwatch _showStopwatch;

    public SplashWindow()
    {
        InitializeComponent();
        
        _showStopwatch = Stopwatch.StartNew();
        _progressBarMaxWidth = Width - 96;
        
        // Version
        var info = G.Build.Info;
        VersionText.Text = info.DisplayVersion;
        GitHashText.Text = info.GitHash;
        
        // Localized commit string
        var L = LocalizationService.Instance;
        BuildInfoText.Text = $"{string.Format(L["Splash_Commits"], info.CommitCount)} • {info.BuildDate:yyyy-MM-dd}";
        
        // GitHub URL
        GitHubUrlText.Text = G.DisplayGithubUrl;
        
        GitHubButton.Click += OnGitHubButtonClick;
        
        Log.Info($"[Splash] Version: {info.FullVersionString}");
    }

    private void OnGitHubButtonClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = G.GitHubUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to open GitHub: {ex.Message}");
        }
    }

    public void UpdateStatus(string status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusText.Text = status;
        });
    }

    public void SetProgress(double percent)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var targetWidth = _progressBarMaxWidth * (percent / 100.0);
            ProgressFill.Width = Math.Max(0, targetWidth);
        });
    }

    public int GetRemainingMinShowTimeMs()
    {
        var elapsed = (int)_showStopwatch.ElapsedMilliseconds;
        var remaining = G.Build.MinSplashTimeMs - elapsed;
        return Math.Max(0, remaining);
    }
}