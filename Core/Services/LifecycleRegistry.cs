namespace LMP.Core.Services;

/// <summary>
/// Application lifecycle registry.
/// Manages registration and safe notification of components about account changes and activity states.
/// </summary>
/// <remarks>
/// <para>Uses weak references (<see cref="WeakReference{T}"/>) to prevent memory leaks
/// and replaces the legacy flat scanning of all live heap objects.</para>
/// </remarks>
public sealed class LifecycleRegistry
{
    private readonly List<WeakReference<IAccountAware>> _accountAwareItems = [];
    private readonly List<WeakReference<ISuspendable>> _backgroundSuspendableItems = [];
    private readonly Lock _lock = new();

    /// <summary>
    /// Global access to the registry singleton to prevent circular dependencies with the UI project.
    /// </summary>
    public static LifecycleRegistry? Instance { get; internal set; }

    /// <summary>
    /// Delegate for retrieving the active UI page without tightly coupling the Core project to the UI project (Dependency Inversion).
    /// </summary>
    public static Func<ISuspendable?>? ActiveUiPageResolver { get; set; }

    /// <summary>
    /// Registers a component to track account change events.
    /// </summary>
    /// <param name="item">Component implementing <see cref="IAccountAware"/>.</param>
    public void RegisterAccountAware(IAccountAware item)
    {
        lock (_lock)
        {
            _accountAwareItems.Add(new WeakReference<IAccountAware>(item));
        }
    }

    /// <summary>
    /// Registers a background service to track suspension events.
    /// </summary>
    /// <param name="item">Service implementing <see cref="ISuspendable"/>.</param>
    public void RegisterBackgroundSuspendable(ISuspendable item)
    {
        lock (_lock)
        {
            _backgroundSuspendableItems.Add(new WeakReference<ISuspendable>(item));
        }
    }

    /// <summary>
    /// Unregisters a background service from suspension notifications.
    /// </summary>
    /// <param name="item">Service implementing <see cref="ISuspendable"/>.</param>
    public void UnregisterBackgroundSuspendable(ISuspendable item)
    {
        lock (_lock)
        {
            for (int i = _backgroundSuspendableItems.Count - 1; i >= 0; i--)
            {
                if (!_backgroundSuspendableItems[i].TryGetTarget(out var target) || ReferenceEquals(target, item))
                    _backgroundSuspendableItems.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Broadcasts an account change notification to all registered components.
    /// </summary>
    public void BroadcastAccountChanged()
    {
        List<IAccountAware>? targets = null;
        lock (_lock)
        {
            for (int i = _accountAwareItems.Count - 1; i >= 0; i--)
            {
                if (_accountAwareItems[i].TryGetTarget(out var item))
                {
                    targets ??= new List<IAccountAware>(_accountAwareItems.Count);
                    targets.Add(item);
                }
                else
                {
                    _accountAwareItems.RemoveAt(i);
                }
            }
        }

        if (targets == null) return;
        for (int i = 0; i < targets.Count; i++)
        {
            try
            {
                targets[i].OnAccountChanged();
            }
            catch (Exception ex)
            {
                Log.Error($"[Lifecycle] Error broadcasting AccountChanged: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Transitions registered background services into suspended mode.
    /// </summary>
    /// <param name="level">Suspension level.</param>
    public void BroadcastBackgroundSuspend(SuspendLevel level)
    {
        List<ISuspendable>? targets = null;
        lock (_lock)
        {
            for (int i = _backgroundSuspendableItems.Count - 1; i >= 0; i--)
            {
                if (_backgroundSuspendableItems[i].TryGetTarget(out var item))
                {
                    targets ??= new List<ISuspendable>(_backgroundSuspendableItems.Count);
                    targets.Add(item);
                }
                else
                {
                    _backgroundSuspendableItems.RemoveAt(i);
                }
            }
        }

        if (targets == null) return;
        for (int i = 0; i < targets.Count; i++)
        {
            try
            {
                targets[i].OnSuspend(level);
            }
            catch (Exception ex)
            {
                Log.Error($"[Lifecycle] Error broadcasting Suspend to background service: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Resumes registered background services back to active mode.
    /// </summary>
    /// <param name="previousLevel">Previous suspension level.</param>
    public void BroadcastBackgroundResume(SuspendLevel previousLevel)
    {
        List<ISuspendable>? targets = null;
        lock (_lock)
        {
            for (int i = _backgroundSuspendableItems.Count - 1; i >= 0; i--)
            {
                if (_backgroundSuspendableItems[i].TryGetTarget(out var item))
                {
                    targets ??= new List<ISuspendable>(_backgroundSuspendableItems.Count);
                    targets.Add(item);
                }
                else
                {
                    _backgroundSuspendableItems.RemoveAt(i);
                }
            }
        }

        if (targets == null) return;
        for (int i = 0; i < targets.Count; i++)
        {
            try
            {
                targets[i].OnResume(previousLevel);
            }
            catch (Exception ex)
            {
                Log.Error($"[Lifecycle] Error broadcasting Resume to background service: {ex.Message}");
            }
        }
    }
}