using System.Runtime.CompilerServices;

namespace LMP.Core.Helpers.Extensions;

/// <summary>
/// Extension methods for <see cref="IAsyncEnumerable{T}"/> async streams.
/// </summary>
internal static class AsyncCollectionExtensions
{
    extension<T>(IAsyncEnumerable<T> source)
    {
        /// <summary>
        /// Returns no more than <paramref name="count"/> elements from the start of the stream.
        /// </summary>
        /// <exception cref="ArgumentNullException">If <paramref name="source"/> is null.</exception>
        public async IAsyncEnumerable<T> TakeAsync(
            int count,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);
            var currentCount = 0;

            await foreach (var i in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (currentCount >= count)
                    yield break;

                yield return i;
                currentCount++;
            }
        }

        /// <summary>
        /// Projects stream elements into collections and flattens them into a single sequence.
        /// </summary>
        /// <exception cref="ArgumentNullException">If <paramref name="source"/> or <paramref name="transform"/> is null.</exception>
        public async IAsyncEnumerable<T1> SelectManyAsync<T1>(
            Func<T, IEnumerable<T1>> transform,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(transform);

            await foreach (var i in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                foreach (var j in transform(i))
                    yield return j;
            }
        }
    }

    extension(IAsyncEnumerable<object> source)
    {
        /// <summary>
        /// Filters stream elements based on a specified type <typeparamref name="T"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException">If <paramref name="source"/> is null.</exception>
        public async IAsyncEnumerable<T> OfTypeAsync<T>(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);

            await foreach (var i in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (i is T match)
                    yield return match;
            }
        }
    }
}