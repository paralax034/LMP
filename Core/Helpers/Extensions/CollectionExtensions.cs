namespace LMP.Core.Helpers.Extensions;

/// <summary>
/// Extension methods for collections and sequences.
/// </summary>
internal static class CollectionExtensions
{
    extension<T>(IEnumerable<T?> source) where T : class
    {
        /// <summary>
        /// Filters the sequence, removing <see langword="null"/> elements.
        /// </summary>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="source"/> is null.</exception>
        public IEnumerable<T> WhereNotNull()
        {
            ArgumentNullException.ThrowIfNull(source);

            foreach (var i in source)
            {
                if (i is not null)
                    yield return i;
            }
        }
    }

    extension<T>(IEnumerable<T?> source) where T : struct
    {
        /// <summary>
        /// Filters a sequence of <see cref="Nullable{T}"/>, returning unwrapped values.
        /// </summary>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="source"/> is null.</exception>
        public IEnumerable<T> WhereNotNull()
        {
            ArgumentNullException.ThrowIfNull(source);

            foreach (var i in source)
            {
                if (i is not null)
                    yield return i.Value;
            }
        }
    }

    extension<T>(IEnumerable<T> source) where T : struct
    {
        /// <summary>
        /// Returns the element at the specified <paramref name="index"/> or <see langword="null"/> if the index is out of bounds.
        /// </summary>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="source"/> is null.</exception>
        public T? ElementAtOrNull(int index)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (index < 0) return null;

            if (source is IReadOnlyList<T> readOnlyList)
                return index < readOnlyList.Count ? readOnlyList[index] : null;

            if (source is IList<T> list)
                return index < list.Count ? list[index] : null;

            if (source is ICollection<T> collection && index >= collection.Count)
                return null;

            if (source is IReadOnlyCollection<T> readOnlyCollection && index >= readOnlyCollection.Count)
                return null;

            using var enumerator = source.GetEnumerator();
            var currentIndex = 0;
            while (enumerator.MoveNext())
            {
                if (currentIndex == index)
                    return enumerator.Current;
                currentIndex++;
            }

            return null;
        }

        /// <summary>
        /// Returns the first element of the sequence or <see langword="null"/> if it is empty.
        /// </summary>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="source"/> is null.</exception>
        public T? FirstOrNull()
        {
            ArgumentNullException.ThrowIfNull(source);

            foreach (var i in source)
                return i;

            return null;
        }
    }
}