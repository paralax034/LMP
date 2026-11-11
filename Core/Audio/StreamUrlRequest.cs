namespace LMP.Core.Audio;

/// <summary>
/// Purpose of the stream URL request from source.
/// </summary>
internal enum StreamUrlRequestKind
{
    /// <summary>Initial retrieval of continuation URL from a partial-cache source without a live URL.</summary>
    Acquire,

    /// <summary>Refresh of a URL rejected by CDN (HTTP 403) or expired.</summary>
    Refresh
}

/// <summary>
/// Immutable explicit stream URL request: which track, which stream variant, and why it is needed.
/// </summary>
/// <remarks>
/// The request is created by the source itself at the moment of invocation, so the provider does not need to guess the initiator
/// based on the active pipeline or ambient context. The request does not hold a reference to the source:
/// background provider tasks do not extend the lifetime of RAM buffers or disk cache.
/// </remarks>
/// <param name="Kind">Purpose of the request.</param>
/// <param name="TrackId">Source track identifier.</param>
/// <param name="Format">Container whose bytes are already stored in RAM and disk cache.</param>
/// <param name="BitrateKbps">Variant bitrate (kbps).</param>
/// <param name="ContentLength">Total size of the variant in bytes (<c>clen</c>); <c>0</c> if unknown.</param>
/// <param name="CurrentUrl">Source URL at the time of request; empty string until continuation URL is acquired.</param>
/// <param name="IsForbiddenDriven">Request was triggered by an HTTP 403 from CDN on <paramref name="CurrentUrl"/>.</param>
internal sealed record StreamUrlRequest(
    StreamUrlRequestKind Kind,
    string TrackId,
    AudioFormat Format,
    int BitrateKbps,
    long ContentLength,
    string CurrentUrl,
    bool IsForbiddenDriven)
{
    /// <summary>
    /// Returns a stream selection hint pointing to the exact source variant.
    /// </summary>
    public StreamSelectionHint ToSelectionHint() => new(Format, BitrateKbps);

    /// <summary>
    /// Checks whether source bytes can be continued with the candidate variant (same container and total size).
    /// </summary>
    /// <param name="format">Candidate container.</param>
    /// <param name="contentLength">Total candidate size in bytes; <c>0</c> if unknown.</param>
    /// <returns><c>true</c> if the candidate is compatible or if the size on either side is unknown.</returns>
    public bool IsCompatible(AudioFormat format, long contentLength) =>
        format == Format
        && (ContentLength <= 0 || contentLength <= 0 || contentLength == ContentLength);
}

/// <summary>
/// Stream URL provider for source: single point of entry for <see cref="StreamUrlRequestKind.Acquire"/>
/// and <see cref="StreamUrlRequestKind.Refresh"/>.
/// </summary>
/// <param name="request">Explicit source request.</param>
/// <param name="ct">Cancellation token.</param>
/// <returns>Live URL of a compatible variant, or <c>null</c> if the URL could not be retrieved.</returns>
internal delegate ValueTask<string?> StreamUrlProvider(StreamUrlRequest request, CancellationToken ct);