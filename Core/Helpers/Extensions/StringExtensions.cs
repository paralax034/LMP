using System.Buffers;
using System.Runtime.CompilerServices;

namespace LMP.Core.Helpers.Extensions;

/// <summary>
/// Extension methods for strings.
/// </summary>
internal static class StringExtensions
{
    private const int StackallocThreshold = 256;

    extension(string? s)
    {
        /// <summary>
        /// Truncates the string to length <paramref name="len"/>, appending an ellipsis.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string Truncate(int len = 20)
        {
            if (s is null) return "null";
            return s.Length <= len ? s : string.Concat(s.AsSpan(0, len), "...");
        }
    }

    extension(string str)
    {
        /// <summary>
        /// Returns <see langword="null"/> if the string is empty or consists only of whitespace.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string? NullIfWhiteSpace() =>
            !string.IsNullOrWhiteSpace(str) ? str : null;

        /// <summary>
        /// Reverses the string without array allocations.
        /// </summary>
        public string Reverse()
        {
            return string.Create(str.Length, str, static (span, state) =>
            {
                var stateSpan = state.AsSpan();
                for (var i = 0; i < stateSpan.Length; i++)
                {
                    span[i] = stateSpan[stateSpan.Length - 1 - i];
                }
            });
        }

        /// <summary>
        /// Normalizes the input string by replacing any control characters and newlines with spaces,
        /// collapsing duplicate spaces, and trimming leading and trailing whitespace.
        /// </summary>
        /// <returns>A normalized single-line string without junk control characters.</returns>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public string SanitizeSingleLine()
        {
            if (string.IsNullOrEmpty(str))
                return string.Empty;

            ReadOnlySpan<char> span = str.AsSpan();
            int start = 0;
            while (start < span.Length && (char.IsWhiteSpace(span[start]) || char.IsControl(span[start])))
            {
                start++;
            }

            int end = span.Length - 1;
            while (end >= start && (char.IsWhiteSpace(span[end]) || char.IsControl(span[end])))
            {
                end--;
            }

            if (start > end)
                return string.Empty;

            ReadOnlySpan<char> trimmed = span.Slice(start, end - start + 1);

            bool hasControlChars = false;
            for (int i = 0; i < trimmed.Length; i++)
            {
                if (char.IsControl(trimmed[i]))
                {
                    hasControlChars = true;
                    break;
                }
            }

            if (!hasControlChars)
                return trimmed.Length == str.Length ? str : trimmed.ToString();

            char[]? rentedArray = null;
            Span<char> buffer = trimmed.Length <= StackallocThreshold
                ? stackalloc char[trimmed.Length]
                : (rentedArray = ArrayPool<char>.Shared.Rent(trimmed.Length));

            try
            {
                int writeIndex = 0;
                bool lastWasWhitespace = false;

                for (int i = 0; i < trimmed.Length; i++)
                {
                    char c = trimmed[i];
                    if (char.IsWhiteSpace(c) || char.IsControl(c))
                    {
                        if (!lastWasWhitespace && writeIndex > 0)
                        {
                            buffer[writeIndex++] = ' ';
                            lastWasWhitespace = true;
                        }
                    }
                    else
                    {
                        buffer[writeIndex++] = c;
                        lastWasWhitespace = false;
                    }
                }

                ReadOnlySpan<char> resultSpan = buffer[..writeIndex].TrimEnd();
                return resultSpan.IsEmpty ? string.Empty : new string(resultSpan);
            }
            finally
            {
                if (rentedArray != null)
                    ArrayPool<char>.Shared.Return(rentedArray);
            }
        }
    }
}