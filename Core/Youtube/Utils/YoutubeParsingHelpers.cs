namespace LMP.Core.Youtube.Utils;

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LMP.Core.Helpers.Extensions;

/// <summary>
/// Обеспечивает централизованный высокопроизводительный парсинг данных YouTube.
/// </summary>
internal static class YoutubeParsingHelpers
{
    public static readonly string[] DateFormats =
    [
        "MMM d, yyyy",
        "MMM dd, yyyy",
        "MMMM d, yyyy",
        "MMMM dd, yyyy",
        "d MMM yyyy",
        "dd MMM yyyy",
        "d MMMM yyyy",
        "dd MMMM yyyy",
        "M/d/yyyy",
        "yyyy-MM-dd"
    ];

    public static readonly CultureInfo EnCulture = CultureInfo.GetCultureInfo("en-US");

    /// <summary>
    /// Склеивает текст из массива runs без LINQ и без промежуточных коллекций в куче.
    /// Использует string.Create для прямой записи в память строки и пул массивов для ссылок.
    /// </summary>
    /// <param name="runsElement">JSON-элемент массива runs или null.</param>
    /// <returns>Склеенный текст или null.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string? ConcatTextRuns(JsonElement? runsElement)
    {
        if (runsElement is null || runsElement.Value.ValueKind != JsonValueKind.Array)
            return null;

        var array = runsElement.Value;
        int len = array.GetArrayLength();
        if (len == 0) return null;
        if (len == 1) return array[0].GetPropertyOrNull(InnerTubeTokens.Text)?.GetStringOrNull();

        var parts = System.Buffers.ArrayPool<string?>.Shared.Rent(len);
        try
        {
            int totalLen = 0;

            for (int i = 0; i < len; i++)
            {
                var t = array[i].GetPropertyOrNull(InnerTubeTokens.Text)?.GetStringOrNull();
                parts[i] = t;
                if (t is not null) totalLen += t.Length;
            }

            if (totalLen == 0) return null;

            return string.Create(totalLen, (parts, len), static (span, state) =>
            {
                var (p, count) = state;
                int pos = 0;

                for (int i = 0; i < count; i++)
                {
                    if (p[i] is { } s)
                    {
                        s.AsSpan().CopyTo(span[pos..]);
                        pos += s.Length;
                    }
                }
            });
        }
        finally
        {
            System.Buffers.ArrayPool<string?>.Shared.Return(parts, clearArray: true);
        }
    }

    /// <summary>
    /// Проверяет, представляет ли переданная строка относительную дату английского API InnerTube.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsRelativeDate(ReadOnlySpan<char> span)
    {
        var trimmed = span.Trim();
        if (trimmed.IsEmpty) return false;

        return trimmed.Equals("today", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("just now", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("yesterday", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith("ago", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Проверяет, представляет ли переданная строка относительную дату английского API InnerTube.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsRelativeDate(string? text) =>
        !string.IsNullOrEmpty(text) && IsRelativeDate(text.AsSpan());

    /// <summary>
    /// Выполняет детерминированный разбор английской относительной даты InnerTube без выделения памяти в куче.
    /// </summary>
    public static DateOnly? ParseRelativeDate(ReadOnlySpan<char> span, DateTime nowUtc)
    {
        var trimmed = span.Trim();
        if (trimmed.IsEmpty) return null;

        if (trimmed.Equals("today", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("just now", StringComparison.OrdinalIgnoreCase))
            return DateOnly.FromDateTime(nowUtc);

        if (trimmed.Equals("yesterday", StringComparison.OrdinalIgnoreCase))
            return DateOnly.FromDateTime(nowUtc.AddDays(-1));

        if (trimmed.EndsWith("ago", StringComparison.OrdinalIgnoreCase))
        {
            var val = ParseLongFromText(trimmed);
            if (!val.HasValue) return null;

            int delta = (int)Math.Min(val.Value, int.MaxValue);

            if (trimmed.Contains("week", StringComparison.OrdinalIgnoreCase))
                return DateOnly.FromDateTime(nowUtc.AddDays(-delta * 7));

            if (trimmed.Contains("month", StringComparison.OrdinalIgnoreCase))
                return DateOnly.FromDateTime(nowUtc.AddMonths(-delta));

            if (trimmed.Contains("year", StringComparison.OrdinalIgnoreCase))
                return DateOnly.FromDateTime(nowUtc.AddYears(-delta));

            if (trimmed.Contains("day", StringComparison.OrdinalIgnoreCase))
                return DateOnly.FromDateTime(nowUtc.AddDays(-delta));

            if (trimmed.Contains("hour", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("minute", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("second", StringComparison.OrdinalIgnoreCase))
            {
                return DateOnly.FromDateTime(nowUtc);
            }
        }

        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int? ParseYearFromText(ReadOnlySpan<char> text)
    {
        if (text.Length < 4) return null;

        for (int i = 0; i <= text.Length - 4; i++)
        {
            if (char.IsAsciiDigit(text[i])
                && char.IsAsciiDigit(text[i + 1])
                && char.IsAsciiDigit(text[i + 2])
                && char.IsAsciiDigit(text[i + 3]))
            {
                bool leftOk = i == 0 || !char.IsAsciiDigit(text[i - 1]);
                bool rightOk = i + 4 == text.Length || !char.IsAsciiDigit(text[i + 4]);

                if (leftOk && rightOk)
                {
                    int year = (text[i] - '0') * 1000
                             + (text[i + 1] - '0') * 100
                             + (text[i + 2] - '0') * 10
                             + (text[i + 3] - '0');
                    if (year >= 1900 && year <= 2100) return year;
                }
            }
        }
        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int? ParseYearFromText(string? text) =>
        string.IsNullOrEmpty(text) ? null : ParseYearFromText(text.AsSpan());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long? ParseLongFromText(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return null;

        long allDigits = 0;
        long integerPart = 0;
        long fractionPart = 0;
        int fractionDigits = 0;
        int separatorCount = 0;
        bool found = false;
        int i = 0;

        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsAsciiDigit(c))
            {
                int digit = c - '0';
                allDigits = allDigits * 10 + digit;
                if (separatorCount == 0)
                {
                    integerPart = integerPart * 10 + digit;
                }
                else
                {
                    fractionPart = fractionPart * 10 + digit;
                    fractionDigits++;
                }
                found = true;
            }
            else if (found && (c == '.' || c == ','))
            {
                separatorCount++;
                fractionPart = 0;
                fractionDigits = 0;
            }
            else if (found && (c == ' ' || c == '\u00A0'))
            {
                if (i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]))
                    continue;
                break;
            }
            else if (found)
            {
                break;
            }
        }

        if (!found) return null;

        long multiplier = 1;
        if (i < text.Length)
        {
            char suffix = text[i];
            bool nextIsBoundary = i + 1 >= text.Length || !char.IsAsciiLetter(text[i + 1]);
            if (nextIsBoundary)
            {
                multiplier = suffix switch
                {
                    'K' or 'k' => 1_000L,
                    'M' or 'm' => 1_000_000L,
                    'B' or 'b' => 1_000_000_000L,
                    _ => 1L
                };
            }
        }

        if (multiplier > 1)
        {
            if (separatorCount == 1 && fractionDigits > 0)
            {
                long divisor = 1;
                for (int d = 0; d < fractionDigits; d++)
                    divisor *= 10;

                return integerPart * multiplier + (fractionPart * multiplier) / divisor;
            }

            return allDigits * multiplier;
        }

        return allDigits;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long? ParseLongFromText(string? text) =>
        string.IsNullOrEmpty(text) ? null : ParseLongFromText(text.AsSpan());
}