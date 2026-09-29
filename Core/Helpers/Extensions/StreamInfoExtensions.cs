using LMP.Core.Youtube.Videos.Streams;

namespace LMP.Core.Helpers.Extensions;

/// <summary>
/// Методы расширения для метаданных медиапотоков <see cref="IStreamInfo"/>.
/// </summary>
public static class StreamInfoExtensions
{
    extension<T>(IEnumerable<T> streamInfos) where T : IStreamInfo
    {
        /// <summary>
        /// Возвращает поток с максимальным битрейтом или <see langword="null"/>, если коллекция пуста.
        /// </summary>
        /// <exception cref="ArgumentNullException">Если <paramref name="streamInfos"/> равен null.</exception>
        public T? TryGetWithHighestBitrate()
        {
            ArgumentNullException.ThrowIfNull(streamInfos);
            return streamInfos.MaxBy(static s => s.Bitrate);
        }

        /// <summary>
        /// Возвращает поток с максимальным битрейтом.
        /// </summary>
        /// <exception cref="ArgumentNullException">Если <paramref name="streamInfos"/> равен null.</exception>
        /// <exception cref="InvalidOperationException">Если коллекция пуста.</exception>
        public T GetWithHighestBitrate()
        {
            ArgumentNullException.ThrowIfNull(streamInfos);
            return streamInfos.TryGetWithHighestBitrate()
                ?? throw new InvalidOperationException("Input stream collection is empty.");
        }
    }
}