namespace LMP.Core.ViewModels;

/// <summary>
/// Base class for all application view models (ViewModels).
/// Built on CommunityToolkit.Mvvm with full AOT support.
/// </summary>
public abstract partial class ViewModelBase : ObservableObject, IDisposable, ISuspendable, IAccountAware
{
    #region Properties

    /// <summary>
    /// Resource disposal container.
    /// </summary>
    protected List<IDisposable> Disposables { get; } = [];

    private readonly Lock _disposablesLock = new();

    /// <summary>
    /// Current global application suspension level.
    /// </summary>
    public static SuspendLevel CurrentSuspendLevel { get; private set; } = SuspendLevel.None;

    /// <summary>
    /// Event raised when the global application suspension level changes.
    /// </summary>
    public static event Action<SuspendLevel>? SuspendLevelChanged;

    /// <summary>
    /// Indicates whether the current component is suspended (in background mode).
    /// </summary>
    [ObservableProperty]
    public partial bool IsSuspended { get; private set; }

    /// <summary>
    /// Provides access to the localization service for single-level binding (IFilterable requirement).
    /// </summary>
#pragma warning disable CA1822
    public LocalizationService L => LocalizationService.Instance;
#pragma warning restore CA1822

    /// <summary>
    /// Provides static access to the localization service.
    /// </summary>
    public static LocalizationService SL => LocalizationService.Instance;

    /// <summary>
    /// Gets a value indicating whether the instance should receive broadcast notifications on account switch.
    /// Disabled by default to avoid registering unnecessary VMs.
    /// </summary>
    protected virtual bool HandlesAccountChanges => false;

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes the base class.
    /// When <see cref="HandlesAccountChanges"/> is <c>true</c>, registers
    /// in the lifecycle registry to receive account change notifications.
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
    /// Asynchronously invoked when navigating to this page.
    /// </summary>
    public virtual Task OnNavigatedToAsync() => Task.CompletedTask;

    /// <summary>
    /// Invoked when navigating away from this page to another screen.
    /// </summary>
    public virtual void OnNavigatedFrom()
    {
    }

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
    /// Invoked when the component enters background mode without parameters.
    /// </summary>
    protected virtual void OnSuspend()
    {
    }

    /// <summary>
    /// Invoked when the component returns to active mode without parameters.
    /// </summary>
    protected virtual void OnResume()
    {
    }

    /// <summary>
    /// Invoked when the component enters background mode with a specified suspension level.
    /// Forwards the call to the parameterless method for compatibility.
    /// </summary>
    protected virtual void OnSuspend(SuspendLevel level) => OnSuspend();

    /// <summary>
    /// Invoked when the component returns to active mode with a specified previous suspension level.
    /// Forwards the call to the parameterless method for compatibility.
    /// </summary>
    protected virtual void OnResume(SuspendLevel previousLevel) => OnResume();

    /// <summary>
    /// Invoked when the user profile changes.
    /// </summary>
    protected virtual void OnAccountChanged()
    {
    }

    #endregion

    #region Helpers for Subclasses

    /// <summary>
    /// Registers a command or resource in the resource disposal container.
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
    /// Propagates the suspension level across the system. Applies a cascading navigation model
    /// and targets notifications to background services.
    /// </summary>
    public static void BroadcastSuspendLevel(SuspendLevel level, bool forceOptimize = false)
    {
        var previousLevel = CurrentSuspendLevel;
        if (previousLevel == level && !forceOptimize) return;

        CurrentSuspendLevel = level;

        SuspendLevelChanged?.Invoke(level);

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
    /// Broadcasts the account change event via a targeted registry.
    /// </summary>
    public static void BroadcastAccountChanged()
    {
        LifecycleRegistry.Instance?.BroadcastAccountChanged();
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes component resources.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes managed and unmanaged resources.
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
