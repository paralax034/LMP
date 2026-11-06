namespace LMP.Tests.Framework;

/// <summary>
/// Test method parameter signature for efficient invocation without reflection lookup.
/// </summary>
public enum TestParameterSignature : byte
{
    None = 0,
    ServicesOnly = 1,
    TokenOnly = 2,
    ServicesAndToken = 3,
    TokenAndServices = 4
}

/// <summary>
/// Delegate for invoking a test method with unified arguments; built by the registry generator,
/// which packs services and the token in the order of the original method parameters.
/// </summary>
/// <param name="services">Application service provider.</param>
/// <param name="ct">Cancellation token respecting the test timeout.</param>
public delegate Task TestInvoker(IServiceProvider services, CancellationToken ct);

/// <summary>
/// Test registration created by LMP.Tests.Generator at build time (reflection-free).
/// </summary>
/// <param name="ClassName">Containing class name.</param>
/// <param name="MethodName">Test method name.</param>
/// <param name="Attribute">Attribute instance with arguments from the source code.</param>
/// <param name="Signature">Method parameter signature.</param>
/// <param name="Invoke">Static delegate to invoke the method.</param>
public readonly record struct TestRegistration(
    string ClassName,
    string MethodName,
    TestMethodAttribute Attribute,
    TestParameterSignature Signature,
    TestInvoker Invoke);

/// <summary>
/// Descriptor of a registered test or debug action.
/// </summary>
public sealed record TestDescriptor
{
    /// <summary>Unique identifier: "ClassName.MethodName".</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable name for UI.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Test execution category.</summary>
    public required TestCategory Category { get; init; }

    /// <summary>Thematic subsystem from <see cref="TestGroups"/>.</summary>
    public required string Group { get; init; }

    /// <summary>Containing class name.</summary>
    public required string ClassName { get; init; }

    /// <summary>Sort order.</summary>
    public required int Order { get; init; }

    /// <summary>Whether the test requires an active network connection.</summary>
    public required bool RequiresNetwork { get; init; }

    /// <summary>Timeout in seconds (0 = no limit).</summary>
    public required int TimeoutSeconds { get; init; }

    /// <summary>Whether the item is an interactive debug action.</summary>
    public bool IsAction => Category == TestCategory.Action;

    /// <summary>Whether the item is a diagnostic probe.</summary>
    public bool IsDiagnostic => Category == TestCategory.Diagnostic;

    /// <summary>Method parameter signature.</summary>
    public required TestParameterSignature Signature { get; init; }

    /// <summary>Test invocation delegate created by the registry generator.</summary>
    public required TestInvoker Invoke { get; init; }
}

/// <summary>Test execution state.</summary>
public enum TestRunState : byte
{
    NotRun = 0,
    Running = 1,
    Passed = 2,
    Failed = 3,
    Skipped = 4
}

/// <summary>
/// Result of a test or action execution.
/// </summary>
public sealed record TestResult
{
    /// <summary>Test identifier.</summary>
    public required string TestId { get; init; }

    /// <summary>Final execution status.</summary>
    public required TestRunState State { get; init; }

    /// <summary>Elapsed time.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Error message or skip reason (null on success).</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Formatted duration for UI display.</summary>
    public string DurationFormatted => Duration.TotalMilliseconds < 1000
        ? $"{Duration.TotalMilliseconds:F0}ms"
        : $"{Duration.TotalSeconds:F1}s";
}