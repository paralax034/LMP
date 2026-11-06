namespace LMP.Core.Exceptions;

/// <summary>
/// Thrown when the CDN host is unreachable for media traffic
/// (TSPU silent drop on /videoplayback).
/// </summary>
public sealed class CdnUnavailableException(string host, string url)
    : Exception($"CDN host '{host}' is unavailable for media path (TSPU block?)")
{
    /// <summary>CDN hostname.</summary>
    public string Host { get; } = host;

    /// <summary>Original media URL.</summary>
    public string MediaUrl { get; } = url;
}