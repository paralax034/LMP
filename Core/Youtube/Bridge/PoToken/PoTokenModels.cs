namespace LMP.Core.Youtube.Bridge.PoToken;

/// <summary>
/// Parsed response from WAA/Create (after descrambling).
/// JSPB structure: [messageId, [[scriptUrl, scriptContent], interpreterHash], program, globalName, ...]
/// </summary>
public sealed record BotGuardChallenge
{
    /// <summary>URL of the bg.js script (e.g. //www.gstatic.com/bg/...).</summary>
    public required string ScriptUrl { get; init; }

    /// <summary>
    /// Script hash. Used as a cache key for bg.js —
    /// if the hash matches, the script is not redownloaded.
    /// </summary>
    public required string InterpreterHash { get; init; }

    /// <summary>Task bytecode. Changes on every request.</summary>
    public required string Program { get; init; }

    /// <summary>
    /// VM constructor name in globalThis.
    /// E.g.: "botguard", "bg", "_bg".
    /// </summary>
    public required string GlobalName { get; init; }

    /// <summary>
    /// Inline bg.js script (when YouTube serves the script directly in the JSPB response, without a separate URL).
    /// <c>null</c> if the script needs to be downloaded via <see cref="ScriptUrl"/>.
    /// </summary>
    public string? InlineScript { get; init; }

    /// <summary>Indicates whether a ready script is available (inline or via URL).</summary>
    public bool HasScript => !string.IsNullOrEmpty(InlineScript) || !string.IsNullOrEmpty(ScriptUrl);
}

/// <summary>
/// Response from WAA/GenerateIT.
/// JSPB format: [integrityToken, estimatedTtlSecs, mintRefreshThreshold, websafeFallbackToken?]
/// </summary>
public sealed record IntegrityTokenData
{
    public required string IntegrityToken { get; init; }

    /// <summary>TTL in seconds. Usually 43200 (12 hours).</summary>
    public int EstimatedTtlSecs { get; init; }

    public int MintRefreshThreshold { get; init; }

    /// <summary>
    /// Flag indicating the token was retrieved from websafeFallbackToken ([3]) rather than integrityToken ([0]).
    /// This is normal — YouTube sometimes returns only the fallback.
    /// </summary>
    public bool IsFallbackToken { get; init; }
}