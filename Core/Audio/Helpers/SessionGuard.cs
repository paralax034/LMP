using System.Runtime.CompilerServices;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// Unified session validity check mechanism.
/// Eliminates pattern duplication of <c>Interlocked.CompareExchange(ref _session, 0, 0)</c>
/// and comparison operator discrepancies (<c>&lt;</c> vs <c>!=</c>) between AudioEngine and AudioPlayer.
/// </summary>
/// <remarks>
/// <para><b>Semantics:</b> A session is considered "stale" if its ID is strictly less than the current one.
/// The <c>&lt;</c> operator is chosen over <c>!=</c> to correctly handle wrap-around
/// during long runs (int.MaxValue → negative values).</para>
/// <para><b>Thread safety:</b> All operations use <see cref="Interlocked"/>
/// or <see cref="Volatile"/> — safe without additional locks.</para>
/// </remarks>
public struct SessionGuard
{
    private int _currentSession;

    /// <summary>
    /// Current session ID (volatile read).
    /// </summary>
    public readonly int Current => Volatile.Read(ref Unsafe.AsRef(in _currentSession));

    /// <summary>
    /// Starts a new session. Returns the new session ID.
    /// </summary>
    /// <returns>The new session ID (strictly greater than the previous one).</returns>
    public int BeginNew() => Interlocked.Increment(ref _currentSession);

    /// <summary>
    /// Checks whether the specified session is stale.
    /// </summary>
    /// <param name="sessionId">Session ID to check.</param>
    /// <returns>true if <paramref name="sessionId"/> is strictly less than the current one.</returns>
    public readonly bool IsStale(int sessionId) =>
        sessionId < Volatile.Read(ref Unsafe.AsRef(in _currentSession));

    /// <summary>
    /// Checks whether the session is stale or the token has been canceled.
    /// </summary>
    /// <param name="sessionId">Session ID to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>true if the session is stale or the token is canceled.</returns>
    public readonly bool IsStaleOrCancelled(int sessionId, CancellationToken ct) =>
        ct.IsCancellationRequested || IsStale(sessionId);
}