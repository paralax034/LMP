namespace LMP.Core.ViewModels;

/// <summary>
/// Базовый класс для всех моделей представления (ViewModels) приложения.
/// Построен на CommunityToolkit.Mvvm с полной поддержкой AOT.
/// </summary>
public abstract partial class ViewModelBase : ObservableObject, IDisposable, ISuspendable, IAccountAware
{
    #region Properties

    /// <summary>
    /// Контейнер для автоматической утилизации ресурсов.
    /// </summary>
    protected List<IDisposable> Disposables { get; } = [];

    private readonly Lock _disposablesLock = new();

    /// <summary>
    /// Текущий глобальный уровень приостановки активности приложения.
    /// </summary>
    public static SuspendLevel CurrentSuspendLevel { get; private set; } = SuspendLevel.None;

    /// <summary>
    /// Указывает, находится ли текущий компонент в состоянии приостановки (фоновом режиме).
    /// </summary>
    [ObservableProperty] public partial bool IsSuspended { get; private set; }

    /// <summary>
    /// Предоставляет доступ к службе локализации для одноуровневого биндинга (требование IFilterable).
    /// </summary>
#pragma warning disable CA1822
    public LocalizationService L => LocalizationService.Instance;
#pragma warning restore CA1822

    /// <summary>
    /// Предоставляет статический доступ к службе локализации.
    /// </summary>
    public static LocalizationService SL => LocalizationService.Instance;

    /// <summary>
    /// Определяет, должен ли экземпляр получать широковещательные уведомления о смене аккаунта.
    /// По умолчанию выключено, чтобы не регистрировать все VM подряд без необходимости.
    /// </summary>
    protected virtual bool HandlesAccountChanges => false;

    #endregion

    #region Constructor

    /// <summary>
    /// Инициализирует базовый класс.
    /// При <see cref="HandlesAccountChanges"/> = <c>true</c> выполняет регистрацию
    /// в реестре жизненного цикла для получения уведомлений о смене аккаунта.
    /// </summary>
    protected ViewModelBase()
    {
        if (HandlesAccountChanges)
        {
            LifecycleRegistry.Instance?.RegisterAccountAware(this);
        }
    }

    #endregion

    #region Navigation Lifecycle

    /// <summary>
    /// Асинхронно вызывается при переходе на данную страницу навигации.
    /// </summary>
    public virtual Task OnNavigatedToAsync() => Task.CompletedTask;

    #endregion

    #region ISuspendable Explicit Implementation

    /// <inheritdoc />
    void ISuspendable.OnSuspend(SuspendLevel level)
    {
        IsSuspended = true;
        OnSuspend(level);
    }

    /// <inheritdoc />
    void ISuspendable.OnResume(SuspendLevel previousLevel)
    {
        IsSuspended = false;
        OnResume(previousLevel);
    }

    #endregion

    #region IAccountAware Explicit Implementation

    /// <inheritdoc />
    void IAccountAware.OnAccountChanged()
    {
        OnAccountChanged();
    }

    #endregion

    #region Protected Virtual Lifecycle

    /// <summary>
    /// Вызывается при переходе компонента в фоновый режим без параметров.
    /// </summary>
    protected virtual void OnSuspend() { }

    /// <summary>
    /// Вызывается при возвращении компонента в активный режим без параметров.
    /// </summary>
    protected virtual void OnResume() { }

    /// <summary>
    /// Вызывается при переходе компонента в фоновый режим с указанием уровня приостановки.
    /// Перенаправляет вызов в беспараметрический метод для совместимости.
    /// </summary>
    protected virtual void OnSuspend(SuspendLevel level) => OnSuspend();

    /// <summary>
    /// Вызывается при возвращении компонента в активный режим с указанием предыдущего уровня приостановки.
    /// Перенаправляет вызов в беспараметрический метод для совместимости.
    /// </summary>
    protected virtual void OnResume(SuspendLevel previousLevel) => OnResume();

    /// <summary>
    /// Вызывается при изменении профиля пользователя.
    /// </summary>
    protected virtual void OnAccountChanged() { }

    #endregion

    #region Helpers for Subclasses

    /// <summary>
    /// Регистрирует команду или ресурс в контейнере утилизации ресурсов.
    /// </summary>
    protected TCommand CreateCommand<TCommand>(TCommand command) where TCommand : IDisposable
    {
        lock (_disposablesLock)
        {
            Disposables.Add(command);
        }
        return command;
    }

    #endregion

    #region Static Lifecycle Broadcasts

    /// <summary>
    /// Распространяет уровень приостановки по системе. Применяет каскадную модель навигации 
    /// и точечно оповещает фоновые службы .
    /// </summary>
    public static void BroadcastSuspendLevel(SuspendLevel level, bool forceOptimize = false)
    {
        var previousLevel = CurrentSuspendLevel;
        if (previousLevel == level && !forceOptimize) return;

        CurrentSuspendLevel = level;

        if (LifecycleRegistry.Instance != null)
        {
            if (level != SuspendLevel.None)
                LifecycleRegistry.Instance.BroadcastBackgroundSuspend(level);
            else
                LifecycleRegistry.Instance.BroadcastBackgroundResume(previousLevel);
        }

        var activePage = LifecycleRegistry.ActiveUiPageResolver?.Invoke();
        if (activePage != null)
        {
            if (level != SuspendLevel.None)
                activePage.OnSuspend(level);
            else
                activePage.OnResume(previousLevel);
        }
    }

    /// <summary>
    /// Рассылает событие изменения аккаунта через точечный реестр.
    /// </summary>
    public static void BroadcastAccountChanged()
    {
        LifecycleRegistry.Instance?.BroadcastAccountChanged();
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Выполняет утилизацию ресурсов компонента.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Выполняет утилизацию управляемых и неуправляемых ресурсов.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_disposablesLock)
            {
                for (int i = 0; i < Disposables.Count; i++)
                {
                    Disposables[i].Dispose();
                }
                Disposables.Clear();
            }
        }
    }

    #endregion
}