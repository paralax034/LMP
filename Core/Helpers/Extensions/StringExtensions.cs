using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace LMP.Core.Helpers.Extensions;

/// <summary>
/// Методы расширения для строк.
/// </summary>
internal static class StringExtensions
{
    private const int StackallocThreshold = 256;

    extension(string? s)
    {
        /// <summary>
        /// Усекает строку до длины <paramref name="len"/> с добавлением многоточия.
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
        /// Возвращает <see langword="null"/>, если строка пуста или состоит только из пробелов.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string? NullIfWhiteSpace() =>
            !string.IsNullOrWhiteSpace(str) ? str : null;

        /// <summary>
        /// Возвращает подстроку до первого вхождения <paramref name="sub"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string SubstringUntil(string sub, StringComparison comparison = StringComparison.Ordinal)
        {
            var index = str.IndexOf(sub, comparison);
            return index < 0 ? str : str[..index];
        }

        /// <summary>
        /// Возвращает подстроку после первого вхождения <paramref name="sub"/> или пустую строку.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string SubstringAfter(string sub, StringComparison comparison = StringComparison.Ordinal)
        {
            var index = str.IndexOf(sub, comparison);
            return index < 0
                ? string.Empty
                : str[(index + sub.Length)..];
        }

        /// <summary>
        /// Удаляет все нецифровые символы из строки.
        /// </summary>
        public string StripNonDigit()
        {
            var allDigits = true;
            foreach (var c in str)
            {
                if (!char.IsDigit(c))
                {
                    allDigits = false;
                    break;
                }
            }

            return allDigits ? str : str.StripNonDigitOptimized();
        }

        /// <summary>
        /// Удаляет нецифровые символы с выделением буфера под длину строки.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string StripNonDigitOptimized()
        {
            var builder = new StringBuilder(str.Length);
            foreach (var c in str)
            {
                if (char.IsDigit(c))
                    builder.Append(c);
            }
            return builder.ToString();
        }

        /// <summary>
        /// Разворачивает строку без аллокации массивов.
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
        /// Меняет местами символы по индексам <paramref name="firstCharIndex"/> и <paramref name="secondCharIndex"/>.
        /// </summary>
        public string SwapChars(int firstCharIndex, int secondCharIndex)
        {
            return string.Create(str.Length, (str, firstCharIndex, secondCharIndex), static (span, state) =>
            {
                state.str.AsSpan().CopyTo(span);
                (span[state.firstCharIndex], span[state.secondCharIndex]) =
                    (span[state.secondCharIndex], span[state.firstCharIndex]);
            });
        }

        /// <summary>
        /// Нормализует входную строку, заменяя любые управляющие символы и переводы строк на пробелы,
        /// схлопывая повторные пробелы и выполняя тримминг по краям.
        /// </summary>
        /// <returns>Нормализованная однострочная строка без мусорных управляющих символов.</returns>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public string SanitizeSingleLine()
        {
            if (string.IsNullOrEmpty(str))
                return string.Empty;

            ReadOnlySpan<char> span = str.AsSpan();
            int start = 0;
            while (start < span.Length && (span[start] is '\r' or '\n' or '\t' || char.IsWhiteSpace(span[start]) || char.IsControl(span[start])))
            {
                start++;
            }

            int end = span.Length - 1;
            while (end >= start && (span[end] is '\r' or '\n' or '\t' || char.IsWhiteSpace(span[end]) || char.IsControl(span[end])))
            {
                end--;
            }

            if (start > end)
                return string.Empty;

            ReadOnlySpan<char> trimmed = span.Slice(start, end - start + 1);

            bool hasControlChars = false;
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                if (c is '\r' or '\n' or '\t' || char.IsControl(c))
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
                    if (c is '\r' or '\n' or '\t' || char.IsControl(c) || char.IsWhiteSpace(c))
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