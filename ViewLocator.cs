using Avalonia.Controls;
using Avalonia.Controls.Templates;
#if DEBUG
using LMP.UI.Features.Debug;
#endif
using LMP.UI.Features.Home;
using LMP.UI.Features.Library;
using LMP.UI.Features.Notifications;
using LMP.UI.Features.Player;
using LMP.UI.Features.Playlist;
using LMP.UI.Features.Queue;
using LMP.UI.Features.Search;
using LMP.UI.Features.Settings;
using LMP.UI.Features.Shell;

namespace LMP;

/// <summary>
/// Статический сопоставитель моделей представления (ViewModel) и представлений (View).
/// Работает по принципу чистого Pattern Matching без рефлексии и без избыточного глобального кэширования.
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? data)
    {
        if (data is null)
            return null;

        return data switch
        {
            HomeViewModel => new HomeView(),
            SearchViewModel => new SearchView(),
            LibraryViewModel => new LibraryView(),
            PlaylistViewModel => new PlaylistView(),
            QueueViewModel => new QueueView(),
            SettingsViewModel => new SettingsView(),

            PlayerBarViewModel => new PlayerBarView(),

            NotificationButtonViewModel => new NotificationButton(),
            NotificationPanelViewModel => new NotificationPanel(),
            ToastOverlayViewModel => new ToastOverlay(),

            MainWindowViewModel => new MainWindow(),

#if DEBUG
            DebugViewModel => new DebugWindow(),
#endif

            _ => new TextBlock { Text = $"View Not Registered for: {data.GetType().FullName}" }
        };
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}