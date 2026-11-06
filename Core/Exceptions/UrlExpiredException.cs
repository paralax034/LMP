namespace LMP.Core.Exceptions;

/// <summary>
/// The URL has expired and needs to be refreshed.
/// </summary>
public class UrlExpiredException(string expiredUrl, string? trackId = null)
    : AudioSourceException($"URL expired: {expiredUrl[..Math.Min(50, expiredUrl.Length)]}...")
{
    public string? TrackId { get; } = trackId;
    public string ExpiredUrl { get; } = expiredUrl;
}