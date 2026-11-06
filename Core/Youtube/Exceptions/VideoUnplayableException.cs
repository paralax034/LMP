namespace LMP.Core.Youtube.Exceptions;

/// <summary>
/// Exception thrown when the requested video is unplayable through any client.
/// </summary>
public class VideoUnplayableException : YoutubeExplodeException
{
    /// <summary>Video identifier (raw, without <c>yt_</c> prefix).</summary>
    public string VideoId { get; }

    /// <param name="message">Error description.</param>
    /// <param name="videoId">Raw video ID.</param>
    public VideoUnplayableException(string message, string videoId)
        : base(message)
    {
        VideoId = videoId;
    }

    /// <summary>Backward-compatible constructor for legacy throw-sites.</summary>
    public VideoUnplayableException(string message)
        : base(message)
    {
        VideoId = "";
    }
}