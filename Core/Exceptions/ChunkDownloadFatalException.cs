namespace LMP.Core.Exceptions;

/// <summary>
/// Fatal chunk loading error — stream is permanently unavailable.
/// </summary>
public class ChunkDownloadFatalException : AudioSourceException
{
    /// <summary>
    /// Chunk index where the error occurred.
    /// </summary>
    public int ChunkIndex { get; }

    /// <summary>
    /// Number of consecutive failed attempts.
    /// </summary>
    public int ConsecutiveFailures { get; }

    /// <summary>
    /// Cause of the error.
    /// </summary>
    public ChunkDownloadFailureReason Reason { get; }

    /// <summary>
    /// Track ID (for diagnostics).
    /// </summary>
    public string? TrackId { get; }

    /// <summary>
    /// HTTP status code of the last failed request (if applicable).
    /// </summary>
    public int? HttpStatusCode { get; }

    public ChunkDownloadFatalException(
        string message,
        int chunkIndex,
        int consecutiveFailures,
        ChunkDownloadFailureReason reason,
        string? trackId = null,
        int? httpStatusCode = null)
        : base(message)
    {
        ChunkIndex = chunkIndex;
        ConsecutiveFailures = consecutiveFailures;
        Reason = reason;
        TrackId = trackId;
        HttpStatusCode = httpStatusCode;
    }

    public ChunkDownloadFatalException(
        string message,
        int chunkIndex,
        int consecutiveFailures,
        ChunkDownloadFailureReason reason,
        string? trackId,
        int? httpStatusCode,
        Exception innerException)
        : base(message, innerException)
    {
        ChunkIndex = chunkIndex;
        ConsecutiveFailures = consecutiveFailures;
        Reason = reason;
        TrackId = trackId;
        HttpStatusCode = httpStatusCode;
    }

    /// <summary>
    /// Returns the localization key for the user-facing message.
    /// </summary>
    public string GetLocalizationKey() => Reason switch
    {
        ChunkDownloadFailureReason.Forbidden403 => "Error_Stream_Forbidden",
        ChunkDownloadFailureReason.UmpFormat => "Error_Stream_UmpFormat",
        ChunkDownloadFailureReason.MaxRetriesExceeded => "Error_Stream_MaxRetries",
        ChunkDownloadFailureReason.NetworkError => "Error_Stream_Network",
        _ => "Error_Stream_Unknown"
    };
}

/// <summary>
/// Cause of the fatal chunk loading error.
/// </summary>
public enum ChunkDownloadFailureReason
{
    /// <summary>HTTP 403 Forbidden response limit exceeded.</summary>
    Forbidden403,

    /// <summary>YouTube returned UMP (encrypted) format instead of raw audio.</summary>
    UmpFormat,

    /// <summary>Retry attempt limit exceeded.</summary>
    MaxRetriesExceeded,

    /// <summary>Network error (timeout, connection reset).</summary>
    NetworkError,

    /// <summary>Unknown error.</summary>
    Unknown
}