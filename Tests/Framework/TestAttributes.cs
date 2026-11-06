using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace LMP.Tests.Framework;

/// <summary>
/// Marks a static method as a test or debug action for automatic discovery.
/// <para>
/// Supported signatures:
/// <list type="bullet">
///   <item><c>static Task MethodAsync()</c></item>
///   <item><c>static Task MethodAsync(CancellationToken ct)</c></item>
///   <item><c>static Task MethodAsync(IServiceProvider services)</c></item>
///   <item><c>static Task MethodAsync(IServiceProvider services, CancellationToken ct)</c></item>
///   <item><c>static Task MethodAsync(CancellationToken ct, IServiceProvider services)</c></item>
/// </list>
/// </para>
/// </summary>
/// <param name="category">Execution category (<see cref="TestCategory"/>).</param>
/// <param name="displayName">Display name for UI (if null, generated from method name).</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class TestMethodAttribute(
    TestCategory category,
    string? displayName = null) : Attribute
{
    /// <summary>Load category and isolation level of the test.</summary>
    public TestCategory Category { get; } = category;

    /// <summary>Display name of the test without duplicate group prefixes.</summary>
    public string? DisplayName { get; } = displayName;

    /// <summary>
    /// Thematic subsystem from <see cref="TestGroups"/>.
    /// If omitted, automatically derived from the enclosing class name.
    /// </summary>
    public string? Group { get; init; }

    /// <summary>Sort order within the group (lower values appear higher in the list).</summary>
    public int Order { get; init; } = 100;

    /// <summary>Flag requiring an active internet connection.</summary>
    public bool RequiresNetwork { get; init; }

    /// <summary>Execution timeout limit in seconds (0 = no limit).</summary>
    public int TimeoutSeconds { get; init; } = 30;
}

/// <summary>Execution categories for tests and debug tools.</summary>
public enum TestCategory : byte
{
    /// <summary>Isolated deterministic logic tests without network or external dependencies.</summary>
    Unit = 0,

    /// <summary>Integration tests for subsystems (network, deciphering, SQLite, components).</summary>
    Integration = 1,

    /// <summary>Performance, throughput, and memory allocation benchmarks.</summary>
    Benchmark = 2,

    /// <summary>End-to-end full playback cycle scenarios.</summary>
    E2E = 3,

    /// <summary>Diagnostic network and system probes (CDN ping, DNS, TSPU audit).</summary>
    Diagnostic = 4,

    /// <summary>Interactive and destructive debug actions (data generation, cache clearing).</summary>
    Action = 5
}

/// <summary>
/// Canonical thematic groups of tests and subsystems.
/// </summary>
public static class TestGroups
{
    /// <summary>Audio engine, decoders, DSP, equalizer, EBU R128 pre-scan, and zero-alloc buffers.</summary>
    public const string Audio = "Audio";

    /// <summary>YouTube deciphering: NToken, SigCipher, PoToken (BotGuard), AST Solver, and QuickJS.</summary>
    public const string Cipher = "Cipher";

    /// <summary>Networking layer: CDN, DNS/DoH, TSPU/DPI diagnostics, URL expiration.</summary>
    public const string Network = "Network";

    /// <summary>YouTube/YTM parsers (Search, PlayerResponse, Browse) and streaming pipeline.</summary>
    public const string Youtube = "Youtube";

    /// <summary>Local storage: SQLite, MemoryPack, AtomicFile, disk and RAM caches.</summary>
    public const string Storage = "Storage";

    /// <summary>Playlists: track ordering, synchronization, CRUD operations, and migrations.</summary>
    public const string Playlists = "Playlists";

    /// <summary>System services: auto-updater, notifications, UI monitoring.</summary>
    public const string System = "System";

    // Aliases for smooth backward compatibility
    public const string NToken = Cipher;
    public const string SigCipher = Cipher;
    public const string Solver = Cipher;
    public const string Pipeline = Youtube;
    public const string Cache = Storage;
    public const string Notifications = System;

    /// <summary>
    /// Normalizes an arbitrary group string to a canonical <see cref="TestGroups"/> constant.
    /// </summary>
    public static string Normalize(string? rawGroup) => rawGroup switch
    {
        null or "" => System,
        "NToken" or "SigCipher" or "Solver" or "PoToken" or Cipher => Cipher,
        "CdnDiagnostic" or "StreamFreshness" or Network => Network,
        "Pipeline" or "StreamPipeline" or "Search" or "Player" or Youtube => Youtube,
        "Cache" or "AtomicFile" or "MemoryPack" or Storage => Storage,
        "Audio" or "PreScan" or "HotPath" => Audio,
        "Playlist" or "PlaylistOrder" or Playlists => Playlists,
        "Update" or "Notifications" or "Notification" or System => System,
        _ => rawGroup
    };
}

/// <summary>
/// Exception thrown when a test cannot be executed due to unfulfilled preconditions.
/// Transitions the test to <see cref="TestRunState.Skipped"/> status instead of a false pass.
/// </summary>
public sealed class TestSkippedException(string reason) : Exception(reason)
{
    /// <summary>Reason for skipping the test to be displayed in the UI.</summary>
    public string Reason => Message;
}

/// <summary>
/// Centralized lightweight assertions API.
/// Captures evaluated expressions via <see cref="CallerArgumentExpressionAttribute"/>.
/// </summary>
public static class TestAssert
{
    /// <summary>Asserts that a condition is true.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void True(
        [DoesNotReturnIf(false)] bool condition,
        string? message = null,
        [CallerArgumentExpression(nameof(condition))] string? expr = null)
    {
        if (!condition)
            ThrowFailure(message ?? $"Expected condition to be true: {expr}");
    }

    /// <summary>Asserts that a condition is false.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void False(
        [DoesNotReturnIf(true)] bool condition,
        string? message = null,
        [CallerArgumentExpression(nameof(condition))] string? expr = null)
    {
        if (condition)
            ThrowFailure(message ?? $"Expected condition to be false: {expr}");
    }

    /// <summary>Asserts that two values are equal.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Equal<T>(
        T expected,
        T actual,
        string? message = null,
        [CallerArgumentExpression(nameof(actual))] string? expr = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            ThrowFailure(message ?? $"Equal assertion failed for '{expr}': expected '{expected}', got '{actual}'");
    }

    /// <summary>Asserts that a reference is null.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Null<T>(
        T? value,
        string? message = null,
        [CallerArgumentExpression(nameof(value))] string? expr = null) where T : class
    {
        if (value is not null)
            ThrowFailure(message ?? $"Expected '{expr}' to be null, but got '{value}'");
    }

    /// <summary>Asserts that a reference is not null.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T NotNull<T>(
        [NotNull] T? value,
        string? message = null,
        [CallerArgumentExpression(nameof(value))] string? expr = null) where T : class
    {
        if (value is null)
            ThrowFailure(message ?? $"Expected '{expr}' to be non-null");
        return value;
    }

    /// <summary>Asserts that a string is neither null nor empty.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string NotNullOrEmpty(
        [NotNull] string? value,
        string? message = null,
        [CallerArgumentExpression(nameof(value))] string? expr = null)
    {
        if (string.IsNullOrEmpty(value))
            ThrowFailure(message ?? $"Expected '{expr}' to be non-empty string");
        return value;
    }

    /// <summary>Aborts test execution and transitions it to the Skipped state.</summary>
    [DoesNotReturn]
    public static void Skip(string reason) =>
        throw new TestSkippedException(reason);

    /// <summary>Fails test execution with an error message.</summary>
    [DoesNotReturn]
    public static void Fail(string message) =>
        throw new InvalidOperationException($"[TestAssert.Fail] {message}");

    /// <summary>
    /// Verifies the absence of managed allocations in the hot loop.
    /// </summary>
    public static void ZeroAlloc(Action action, int warmup = 5, int iterations = 1000, string? context = null)
    {
        for (int i = 0; i < warmup; i++)
            action();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
            action();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        if (allocated != 0)
        {
            ThrowFailure(
                $"{context ?? "ZeroAlloc"}: expected 0 B allocated, got {allocated} B over {iterations} calls ({(double)allocated / iterations:F2} B/call)");
        }
    }

    [DoesNotReturn]
    private static void ThrowFailure(string message) =>
        throw new InvalidOperationException($"[Assert] {message}");
}