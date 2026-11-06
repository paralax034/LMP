using LMP.Core.Youtube.Videos.Streams;

namespace LMP.Core.Helpers.Extensions;

/// <summary>
/// Extension methods for <see cref="IStreamInfo"/> media stream metadata.
/// </summary>
public static class StreamInfoExtensions
{
    extension<T>(IEnumerable<T> streamInfos) where T : IStreamInfo
    {
        /// <summary>
        /// Returns the stream with the highest bitrate, or <see langword="null"/> if the collection is empty.
        /// </summary>
        /// <exception cref="ArgumentNullException">If <paramref name="streamInfos"/> is null.</exception>
        public T? TryGetWithHighestBitrate()
        {
            ArgumentNullException.ThrowIfNull(streamInfos);
            return streamInfos.MaxBy(static s => s.Bitrate);
        }

        /// <summary>
        /// Returns the stream with the highest bitrate.
        /// </summary>
        /// <exception cref="ArgumentNullException">If <paramref name="streamInfos"/> is null.</exception>
        /// <exception cref="InvalidOperationException">If the collection is empty.</exception>
        public T GetWithHighestBitrate()
        {
            ArgumentNullException.ThrowIfNull(streamInfos);
            return streamInfos.TryGetWithHighestBitrate()
                ?? throw new InvalidOperationException("Input stream collection is empty.");
        }
    }
}