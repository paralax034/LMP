using System.Runtime.CompilerServices;
using LMP.Core.Audio.Sources;

namespace LMP.Tests.Unit;

/// <summary>
/// Strongly typed test access to private members of <see cref="CachingStreamSource"/> without reflection (AOT-compatible).
/// Signatures are bound on first call: renaming or changing types in the source
/// throws <see cref="MissingMethodException"/> or <see cref="MissingFieldException"/> instead of silently skipping the check.
/// </summary>
internal static class CachingStreamSourceAccessors
{
    /// <summary>Coordinated stream URL refresh: single-flight and shared HTTP 403 budget.</summary>
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CoordinatedRefreshAsync")]
    internal static extern Task<CachingStreamSource.RefreshOutcome> CoordinatedRefreshAsync(
        CachingStreamSource source, CancellationToken ct);

    /// <summary>Awaits a refresh that was already initiated by another thread.</summary>
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "WaitForConcurrentRefreshAsync")]
    internal static extern Task<CachingStreamSource.RefreshOutcome> WaitForConcurrentRefreshAsync(
        CachingStreamSource source, CancellationToken ct);

    /// <summary>Single-flight URL retrieval for a sourceless target.</summary>
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "EnsureUrlAvailableAsync")]
    internal static extern Task<bool> EnsureUrlAvailableAsync(CachingStreamSource source, CancellationToken ct);

    /// <summary>HTTP 403 response diagnostics with immediate SessionCacheStore invalidation.</summary>
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "LogAndDiagnose403Async")]
    internal static extern Task LogAndDiagnose403Async(
        CachingStreamSource source, int logicalIndex, HttpRequestMessage request, HttpResponseMessage response);

    /// <summary>Refresh decision table; the first parameter specifies the target static method's declaring type and is always <c>null</c>.</summary>
    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "DecideRefresh")]
    internal static extern CachingStreamSource.RefreshDecision DecideRefresh(
        CachingStreamSource? owner, CachingStreamSource.RefreshOutcome outcome, bool forbiddenDriven);

    /// <summary>Consecutive HTTP 403 counter used for budget deduction.</summary>
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_consecutive403Count")]
    internal static extern ref int Consecutive403Count(CachingStreamSource source);
}