namespace LMP.Core.ViewModels;

/// <summary>
/// UI update suspension level.
/// </summary>
public enum SuspendLevel
{
    /// <summary>
    /// Window is active — full operation of all subscriptions and animations.
    /// </summary>
    None = 0,

    /// <summary>
    /// Focus lost or minimized (not to tray).
    /// Behavior depends on OptimizeWhenInactive setting:
    /// - true: similar to Hard (dispose heavy subs)
    /// - false: operates normally (for second monitor)
    /// </summary>
    Soft = 1,

    /// <summary>
    /// Minimized to tray — hard stop.
    /// Always disposes heavy subscriptions regardless of settings.
    /// </summary>
    Hard = 2
}

/// <summary>
/// Defines the contract for components supporting suspension and resumption of operation.
/// Used for optimizing CPU and GPU resource usage during background mode.
/// </summary>
public interface ISuspendable
{
    /// <summary>
    /// Called when the application or a specific screen enters background mode.
    /// </summary>
    /// <param name="level">Suspension level defining the depth of resource optimization.</param>
    void OnSuspend(SuspendLevel level);

    /// <summary>
    /// Called when the application or screen returns to active mode.
    /// </summary>
    /// <param name="previousLevel">Previous suspension level from which resumption occurs.</param>
    void OnResume(SuspendLevel previousLevel);
}

/// <summary>
/// Defines the contract for components reacting to active user profile changes.
/// Requires full reinitialization or clearing of local data caches.
/// </summary>
public interface IAccountAware
{
    /// <summary>
    /// Called upon successful switching of the user account in the system.
    /// </summary>
    void OnAccountChanged();
}