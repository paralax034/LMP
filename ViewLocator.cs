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
/// </summary>
/// <remarks>
/// <para>
/// Стандартная реализация Avalonia <c>ViewLocator</c> использует динамический поиск типов через
/// <see cref="Type.GetType(string)"/> и создание экземпляров через <see cref="Activator.CreateInstance(Type)"/>.
/// При публикации в режиме <b>Native AOT</b> (<c>PublishAot=true</c>) и полном тримминге (<c>TrimMode=full</c>)
/// метаданные и неиспользуемые напрямую типы вырезаются компилятором ILC, из-за чего рефлексивный поиск
/// всегда завершается неудачей (<c>null</c>) и интерфейс ломается.
/// </para>
/// <para>
/// Данная реализация построена на базе строго типизированного сопоставления с образцом (Pattern Matching),
/// гарантируя:
/// <list type="bullet">
///   <item><description><b>Zero-Reflection:</b> полное отсутствие обращений к метаданным в рантайме.</description></item>
///   <item><description><b>Full Native AOT &amp; Trimming Readiness:</b> компилятор видит прямые ссылки на конструкторы View.</description></item>
///   <item><description><b>Zero-Alloc:</b> диспетчеризация происходит через опкод компилятора без аллокаций строк.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class ViewLocator : IDataTemplate
{
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ViewModelBase, Control> _viewCache = new();

    /// <summary>
    /// Создаёт или возвращает закэшированный визуальный элемент, соответствующий переданной модели представления.
    /// </summary>
    public Control? Build(object? data)
    {
        if (data is null)
            return null;

        if (data is ViewModelBase vm && _viewCache.TryGetValue(vm, out var cachedView))
        {
            return cachedView;
        }

        Control created = data switch
        {
            // Главные экраны приложения (кэшируются вместе с ViewModel)
            HomeViewModel => new HomeView(),
            SearchViewModel => new SearchView(),
            LibraryViewModel => new LibraryView(),
            PlaylistViewModel => new PlaylistView(),
            QueueViewModel => new QueueView(),
            SettingsViewModel => new SettingsView(),

            // Компоненты плеера и панели управления
            PlayerBarViewModel => new PlayerBarView(),

            // Всплывающие панели и уведомления
            NotificationButtonViewModel => new NotificationButton(),
            NotificationPanelViewModel => new NotificationPanel(),
            ToastOverlayViewModel => new ToastOverlay(),

            // Главная оболочка
            MainWindowViewModel => new MainWindow(),

#if DEBUG
            DebugViewModel => new DebugWindow(),
#endif

            _ => new TextBlock { Text = $"View Not Registered for: {data.GetType().FullName}" }
        };

        if (data is ViewModelBase viewModel)
        {
            _viewCache.AddOrUpdate(viewModel, created);
        }

        return created;
    }

    /// <summary>
    /// Проверяет, применим ли данный шаблон данных к переданному объекту.
    /// </summary>
    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}
