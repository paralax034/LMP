namespace LMP.Core.Exceptions;

/// <summary>
/// Thrown when the cache file is deleted, truncated, or contains corrupted chunks.
/// </summary>
/// <remarks>
/// <para><b>Recoverable vs Fatal:</b></para>
/// <para>If <see cref="IsRecoverable"/> = true, <c>AudioEngine</c> performs an
/// automatic retry (up to <c>MaxAutoRetries</c>) while preserving the position.
/// If <c>IsRecoverable</c> = false or retries are exhausted, the error is bubbled up to the UI.</para>
/// </remarks>
public sealed class CacheInvalidatedException : Exception
{
    /// <summary>ID of the track for which the error occurred.</summary>
    public string? TrackId { get; init; }

    /// <summary>
    /// Index of the corrupted chunk. Null if the error affects the entire file
    /// (e.g. <see cref="CacheInvalidationKind.FileDeleted"/>).
    /// </summary>
    public int? ChunkIndex { get; init; }

    /// <summary>Indicates whether recovery is possible without user intervention.</summary>
    public bool IsRecoverable { get; init; }

    /// <summary>Classification of the invalidation cause.</summary>
    public CacheInvalidationKind Kind { get; init; }

    /// <summary>
    /// Number of auto-retry attempts already performed for this track.
    /// Incremented in <c>AudioEngine</c> before each re-run.
    /// </summary>
    public int RetryCount { get; init; }

    public CacheInvalidatedException(string message) : base(message) { }

    public CacheInvalidatedException(string message, Exception? inner) : base(message, inner) { }

    public CacheInvalidatedException(
        string message,
        CacheInvalidationKind kind,
        bool isRecoverable,
        string? trackId = null,
        int? chunkIndex = null,
        int retryCount = 0,
        Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        IsRecoverable = isRecoverable;
        TrackId = trackId;
        ChunkIndex = chunkIndex;
        RetryCount = retryCount;
    }
}

/// <summary>Classification of the cache invalidation cause.</summary>
public enum CacheInvalidationKind
{
    /// <summary>File was deleted from disk during playback.</summary>
    FileDeleted,

    /// <summary>File is truncated: declared length exceeds actual size.</summary>
    Truncated,

    /// <summary>Short read of a single chunk: data on disk is incomplete.</summary>
    ShortRead,

    /// <summary>Container parser performed a resync and reached EOF prematurely.</summary>
    ParserResync,

    /// <summary>Unknown cause.</summary>
    Unknown
}