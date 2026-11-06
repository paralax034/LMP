namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Represents a declarative pattern for matching the structure of Esprima AST nodes.
/// </summary>
public sealed record MatchTemplate
{
    /// <summary>Expected AST node type.</summary>
    public Type? NodeType { get; init; }

    /// <summary>Expected expression operator (e.g., "=").</summary>
    public string? Operator { get; init; }

    /// <summary>Pattern for the left side of the expression.</summary>
    public MatchTemplate? Left { get; init; }

    /// <summary>Pattern for the right side of the expression.</summary>
    public MatchTemplate? Right { get; init; }

    /// <summary>Pattern for the object in a MemberExpression.</summary>
    public MatchTemplate? Object { get; init; }

    /// <summary>Pattern for the property in a MemberExpression.</summary>
    public MatchTemplate? Property { get; init; }

    /// <summary>Pattern for the expression in an ExpressionStatement.</summary>
    public MatchTemplate? Expression { get; init; }

    /// <summary>Pattern for the callee object in a CallExpression.</summary>
    public MatchTemplate? Callee { get; init; }

    /// <summary>Pattern for the identifier (Id) in a declaration.</summary>
    public MatchTemplate? Id { get; init; }

    /// <summary>Pattern for a variable initializer (Init).</summary>
    public MatchTemplate? Init { get; init; }

    /// <summary>A set of patterns, at least one of which must match (logical OR).</summary>
    public MatchTemplate[]? Or { get; init; }

    /// <summary>A set of patterns, each of which must match at least one element of the collection (anykey).</summary>
    public MatchTemplate[]? AnyKey { get; init; }

    /// <summary>Expected literal value.</summary>
    public object? Value { get; init; }

    /// <summary>Flag indicating if the function is async.</summary>
    public bool? Async { get; init; }

    /// <summary>Flag indicating if the property is computed in a MemberExpression.</summary>
    public bool? Computed { get; init; }

    /// <summary>Flag indicating if the call or property access is optional.</summary>
    public bool? Optional { get; init; }

    /// <summary>Expected identifier name.</summary>
    public string? Name { get; init; }

    /// <summary>Pattern for the declaration list (VariableDeclaration).</summary>
    public MatchTemplate? DeclarationsTemplate { get; init; }

    /// <summary>Patterns for the argument list in a CallExpression.</summary>
    public MatchTemplate[]? Arguments { get; init; }

    /// <summary>Expected argument count in a CallExpression.</summary>
    public int? ExpectedArgumentsCount { get; init; }
}