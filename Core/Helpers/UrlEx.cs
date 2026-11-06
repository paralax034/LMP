using System.Globalization;
using System.Runtime.CompilerServices;

namespace LMP.Core.Helpers;

/// <summary>
/// Utilities for working with URLs. All operations preserve the original parameter order
/// and do NOT re-encode values that were not modified.
/// </summary>
internal static class UrlEx
{
    //  SetQueryParameter — IN-PLACE replacement preserving order

    /// <summary>
    /// Sets the value of a query parameter. If the parameter already exists,
    /// replaces its value IN-PLACE while preserving the order of all other parameters.
    /// If the parameter does not exist, appends it to the end.
    /// The value is encoded via <see cref="Uri.EscapeDataString"/>,
    /// but all other parameters remain untouched (zero re-encoding).
    /// </summary>
    public static string SetQueryParameter(string url, string key, string value)
    {
        var urlSpan = url.AsSpan();

        // Find the position of the existing parameter
        var (keyStart, valueStart, valueEnd) = FindParameterBounds(urlSpan, key);

        // Encode ONLY the new value
        var encodedValue = Uri.EscapeDataString(value);

        if (keyStart >= 0)
        {
            // IN-PLACE REPLACEMENT
            // url = [prefix][old_value][suffix]
            //        ^0..valueStart    ^valueEnd..
            // Replace old_value with encodedValue; bitwise copy everything else.

            int newLength = valueStart + encodedValue.Length + (url.Length - valueEnd);

            return string.Create(newLength, (url, valueStart, valueEnd, encodedValue),
                static (span, state) =>
            {
                var (originalUrl, valStart, valEnd, newVal) = state;
                var src = originalUrl.AsSpan();

                // Prefix: everything before the old value
                src[..valStart].CopyTo(span);
                int pos = valStart;

                // New value
                newVal.AsSpan().CopyTo(span[pos..]);
                pos += newVal.Length;

                // Suffix: everything after the old value
                src[valEnd..].CopyTo(span[pos..]);
            });
        }

        // APPEND — parameter does not exist, append to the end
        var separator = urlSpan.Contains('?') ? '&' : '?';
        var encodedKey = Uri.EscapeDataString(key);

        int appendLength = url.Length + 1 + encodedKey.Length + 1 + encodedValue.Length;

        return string.Create(appendLength, (url, separator, encodedKey, encodedValue),
            static (span, state) =>
        {
            var (originalUrl, sep, eKey, eVal) = state;
            int pos = 0;

            originalUrl.AsSpan().CopyTo(span);
            pos += originalUrl.Length;

            span[pos++] = sep;

            eKey.AsSpan().CopyTo(span[pos..]);
            pos += eKey.Length;

            span[pos++] = '=';

            eVal.AsSpan().CopyTo(span[pos..]);
        });
    }

    /// <summary>
    /// Removes a query parameter from the URL. Does not touch or re-encode other parameters.
    /// </summary>
    public static string RemoveQueryParameter(string url, string key)
    {
        var urlSpan = url.AsSpan();
        var (keyStart, _, valueEnd) = FindParameterBounds(urlSpan, key);

        if (keyStart < 0)
            return url; // Parameter does not exist — return as is

        // Determine the full removal range (including the '&' or '?' delimiter)
        int removeStart;
        int removeEnd = valueEnd;

        if (keyStart > 0 && urlSpan[keyStart - 1] == '&')
        {
            // Parameter is NOT first — remove together with the preceding '&'
            removeStart = keyStart - 1;
        }
        else if (keyStart > 0 && urlSpan[keyStart - 1] == '?')
        {
            // Parameter is first after '?'
            if (removeEnd < urlSpan.Length && urlSpan[removeEnd] == '&')
            {
                // More parameters remain — remove the following '&'
                removeStart = keyStart;
                removeEnd++;
            }
            else
            {
                // Sole parameter — remove together with '?'
                removeStart = keyStart - 1;
            }
        }
        else
        {
            // Parameter at the start of the string (signatureCipher without '?')
            removeStart = keyStart;
            if (removeEnd < urlSpan.Length && urlSpan[removeEnd] == '&')
                removeEnd++;
        }

        int newLength = removeStart + (url.Length - removeEnd);
        if (newLength <= 0)
            return url[..Math.Max(0, removeStart)];

        return string.Create(newLength, (url, removeStart, removeEnd),
            static (span, state) =>
        {
            var (originalUrl, rStart, rEnd) = state;
            var src = originalUrl.AsSpan();

            src[..rStart].CopyTo(span);
            src[rEnd..].CopyTo(span[rStart..]);
        });
    }

    //  Query parameter reading — zero-alloc fast path

    /// <summary>
    /// Returns the raw (undecoded) parameter value, or null if not found.
    /// Decoding is unnecessary for most cases.
    /// </summary>
    public static string? TryGetQueryParameterValue(string url, string key)
    {
        var urlSpan = url.AsSpan();
        var (keyStart, valueStart, valueEnd) = FindParameterBounds(urlSpan, key);

        if (keyStart < 0)
            return null;

        var rawValue = urlSpan[valueStart..valueEnd];

        // If the value contains %XX, decode it; otherwise return as is
        if (rawValue.Contains('%'))
            return Uri.UnescapeDataString(rawValue.ToString());

        return rawValue.ToString();
    }

    /// <summary>
    /// Extracts URL expiration time from the &amp;expire= parameter (UTC) without string allocations.
    /// </summary>
    /// <param name="url">URL to check.</param>
    /// <param name="expireUtc">Parsed time in UTC.</param>
    /// <returns><c>true</c> if the parameter was found and successfully parsed; otherwise <c>false</c>.</returns>
    public static bool TryGetExpireUtc(string url, out DateTime expireUtc)
    {
        var urlSpan = url.AsSpan();
        var (keyStart, valueStart, valueEnd) = FindParameterBounds(urlSpan, "expire");

        if (keyStart >= 0)
        {
            var rawValue = urlSpan[valueStart..valueEnd];
            if (long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
            {
                expireUtc = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
                return true;
            }
        }

        expireUtc = DateTime.MaxValue;
        return false;
    }

    /// <summary>
    /// Checks whether the URL has expired or will expire shortly.
    /// </summary>
    /// <param name="url">URL to check.</param>
    /// <param name="safetyMargin">Time margin before actual expiration (defaults to 5 minutes).</param>
    /// <returns><c>true</c> if the URL is expired or about to expire; <c>false</c> if the link is still valid or expire parameter is missing.</returns>
    public static bool IsUrlExpiredOrExpiringSoon(string url, TimeSpan? safetyMargin = null)
    {
        if (string.IsNullOrWhiteSpace(url))
            return true;

        if (!TryGetExpireUtc(url, out var expireUtc))
            return false;

        var margin = safetyMargin ?? TimeSpan.FromMinutes(5);
        return DateTime.UtcNow.Add(margin) >= expireUtc;
    }

    public static IReadOnlyDictionary<string, string> GetQueryParameters(string url)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        var urlSpan = url.AsSpan();

        int queryStart = urlSpan.IndexOf('?');
        int pos = queryStart >= 0 ? queryStart + 1 : 0;

        while (pos < urlSpan.Length)
        {
            // Find the end of the current parameter
            int ampPos = urlSpan[pos..].IndexOf('&');
            int segEnd = ampPos >= 0 ? pos + ampPos : urlSpan.Length;

            if (segEnd > pos)
            {
                var segment = urlSpan[pos..segEnd];
                int eqPos = segment.IndexOf('=');

                string paramKey;
                string paramValue;

                if (eqPos >= 0)
                {
                    var rawKey = segment[..eqPos];
                    var rawVal = segment[(eqPos + 1)..];

                    paramKey = rawKey.Contains('%')
                        ? Uri.UnescapeDataString(rawKey.ToString())
                        : rawKey.ToString();
                    paramValue = rawVal.Contains('%')
                        ? Uri.UnescapeDataString(rawVal.ToString())
                        : rawVal.ToString();
                }
                else
                {
                    paramKey = segment.Contains('%')
                        ? Uri.UnescapeDataString(segment.ToString())
                        : segment.ToString();
                    paramValue = "";
                }

                if (paramKey.Length > 0)
                    dict[paramKey] = paramValue;
            }

            pos = segEnd + 1;
        }

        return dict;
    }

    //  CORE: FindParameterBounds — span-based, zero-allocation

    /// <summary>
    /// Finds the exact parameter bounds in the URL.
    /// Returns (keyStart, valueStart, valueEnd):
    ///   keyStart   — index of the first character of the key
    ///   valueStart — index of the first character of the value (after '=')
    ///   valueEnd   — index of the character AFTER the last character of the value ('&' or end of string)
    /// If the parameter is not found, keyStart = -1.
    /// 
    /// Searches using the RAW key (without decoding), as YouTube keys are always plain ASCII.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int keyStart, int valueStart, int valueEnd) FindParameterBounds(
        ReadOnlySpan<char> url, string key)
    {
        var keySpan = key.AsSpan();
        int queryStart = url.IndexOf('?');
        int searchPos = queryStart >= 0 ? queryStart + 1 : 0;

        while (searchPos < url.Length)
        {
            // Find the end of the current segment
            int ampPos = url[searchPos..].IndexOf('&');
            int segmentEnd = ampPos >= 0 ? searchPos + ampPos : url.Length;

            // Find '=' in the current segment
            var segment = url[searchPos..segmentEnd];
            int eqPos = segment.IndexOf('=');

            if (eqPos >= 0)
            {
                var paramKey = segment[..eqPos];

                // Exact key comparison (case-sensitive, as required by YouTube)
                if (paramKey.SequenceEqual(keySpan))
                {
                    int keyStart = searchPos;
                    int valueStart = searchPos + eqPos + 1;
                    int valueEnd = segmentEnd;
                    return (keyStart, valueStart, valueEnd);
                }
            }
            else
            {
                // Parameter without a value (key without =)
                if (segment.SequenceEqual(keySpan))
                {
                    return (searchPos, segmentEnd, segmentEnd);
                }
            }

            searchPos = segmentEnd + 1;
        }

        return (-1, -1, -1);
    }
}